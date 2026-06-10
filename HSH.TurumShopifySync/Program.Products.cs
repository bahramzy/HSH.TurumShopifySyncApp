using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace HSH.TurumShopifySync
{
    internal static partial class ProductSyncService
    {
        private static object BuildShopifyCreateProductPayload(TurumProduct p, bool includeImage, string category)
        {
            var optionValues = p.variants
                .Select(v => (v.eu_size ?? v.size))
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var variants = p.variants
                .Where(v => !string.IsNullOrWhiteSpace(v.eu_size ?? v.size))
                .Select(v =>
                {
                    var size = (v.eu_size ?? v.size).Trim();

                    var raw = ConvertToDkk(v.price) * Settings.MomsRate + Settings.Profit;
                    var dkk = RoundRetailPrice(raw);

                    return new
                    {
                        option1 = size,
                        price = dkk.ToString("0", CultureInfo.InvariantCulture),
                        sku = p.sku, // SAME SKU for all variants
                        inventory_management = "shopify",
                        inventory_policy = "deny",
                        taxable = true,
                        requires_shipping = true,
                        barcode = v.ean
                    };
                })
                .ToList();

            if (!includeImage)
                Console.WriteLine("SKIP invalid image url for SKU " + p.sku + ": " + p.image);

            // Tags and category
            var productType = category == "Sneakers" ? "Sneakers" : category;

            // Use the helper to build tags
            string tags = TagsMergeAndCleanUp(null, p, category);

            // Shopify product payload
            return new
            {
                product = new
                {
                    title = p.name,
                    vendor = p.brand,
                    product_type = productType,
                    status = "active",
                    tags = string.Join(", ", tags),
                    options = new[]
                    {
                        new { name = "Vælg størrelse", position = 1, values = optionValues }
                    },
                    variants = variants,
                    images = includeImage
                        ? new[] { new { src = p.image, alt = p.name } }
                        : new object[0],
                }
            };
        }

        private static object BuildShopifyUpdateProductPayload(long productId, TurumProduct p, string mergedTags, string category)
        {
            return new
            {
                product = new
                {
                    id = productId,
                    title = p.name,
                    vendor = p.brand,
                    product_type = category,
                    tags = mergedTags
                }
            };
        }

        //private static string ToTitleCase(string value)
        //{
        //    if (string.IsNullOrWhiteSpace(value))
        //        return value;

        //    var textInfo = CultureInfo.CurrentCulture.TextInfo;

        //    // lower first → then TitleCase
        //    return textInfo.ToTitleCase(value.ToLower());
        //}

        private static string ToTitleCase(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return value;

            return string.Join(" ",
                value.Split(' ')
                     .Where(w => w.Length > 0)
                     .Select(w => char.ToUpper(w[0]) + w.Substring(1).ToLower()));
        }

        private static string MergeTags(string existingTagsCsv, IEnumerable<string> newTags)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // existing
            if (!string.IsNullOrWhiteSpace(existingTagsCsv))
            {
                foreach (var t in existingTagsCsv.Split(','))
                {
                    var tag = t.Trim();
                    if (tag.Length > 0)
                        set.Add(tag);
                }
            }

            // new
            foreach (var t in newTags)
            {
                var tag = (t ?? "").Trim();
                if (tag.Length > 0)
                    set.Add(tag);
            }

            return string.Join(", ", set.OrderBy(x => x));
        }

        private static decimal ConvertToDkk(decimal eur)
        {
            //return Math.Round(eur * Settings.EurToDkkRate, 2, MidpointRounding.AwayFromZero);
            return eur * Settings.EurToDkkRate; // NO rounding here
        }

        //private static decimal RoundRetailPrice(decimal price)
        //{
        //    // Danish sneaker style: 749,95
        //    var whole = Math.Floor(price);
        //    return whole + 0.95m;
        //}

        private static decimal RoundRetailPrice(decimal price)
        {
            // Always round UP
            var ceil = Math.Ceiling(price);

            // Which hundred are we in
            var baseHundred = Math.Floor(ceil / 100m) * 100m;

            // remainder inside the hundred
            var remainder = ceil - baseHundred;

            decimal[] steps = { 25m, 45m, 75m, 95m };

            foreach (var step in steps)
            {
                if (remainder <= step)
                    return baseHundred + step;
            }

            // If above 95 → jump to next hundred + 25
            return baseHundred + 100m + 25m;
        }

        // ==============================

        private static bool HasTag(dynamic shopifyProductDoc, string tag)
        {
            if (shopifyProductDoc == null || shopifyProductDoc.product == null)
                return false;

            // Shopify REST returns tags as comma-separated string
            string tags = (string)(shopifyProductDoc.product.tags ?? "");
            if (string.IsNullOrWhiteSpace(tags)) return false;

            var parts = tags.Split(',')
                .Select(t => t.Trim())
                .Where(t => t.Length > 0);

            return parts.Any(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase));
        }

        // =========================
        // SHOPIFY: update the image URL from Turum
        // ========================

        private static readonly HashSet<string> ApparelSizes =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "XS", "S", "M", "L", "XL", "XXL", "XXXL"
            };

        private static string DetectTurumCategory(dynamic p)
        {
            var name = ((string)p.name ?? string.Empty).ToLowerInvariant();

            // 1) Accessories / Apparel (high precision first)

            // Bags
            if (Regex.IsMatch(name, @"\b(shoulder bag|waist bag|crossbody|tote|duffel|backpack|bag)\b"))
                return "Bag";

            // Coats (before jackets)
            if (Regex.IsMatch(name, @"\b(wool coat|pea coat|overcoat|trench( coat)?|parka|coat)\b"))
                return "Coat";

            // Jackets
            if (Regex.IsMatch(name, @"\b(track jacket|denim jacket|windbreaker|bomber|coach|shell jacket|jacket)\b"))
                return "Jacket";

            // Socks
            if (Regex.IsMatch(name, @"\b(crew socks|ankle socks|tube socks|socks?|sock)\b"))
                return "Socks";

            // Pants
            if (Regex.IsMatch(name, @"\b(track pants|sweatpants|trousers|pants|jeans|chino(s)?|jogger(s)?)\b"))
                return "Pants";

            // Hoodie
            if (Regex.IsMatch(name, @"\b(zip hoodie|pullover hoodie|hooded|hoodie)\b"))
                return "Hoodie";

            // Sweatshirt
            if (Regex.IsMatch(name, @"\b(crewneck|sweatshirt)\b"))
                return "Sweatshirt";

            // T-shirt
            if (Regex.IsMatch(name, @"\b(t-?shirt|tshirt|graphic tee|short sleeve tee|long sleeve( tee)?|tee)\b"))
                return "T-shirt";

            // 2) Positive shoe detection (after apparel/accessories)

            if (Regex.IsMatch(
                    name,
                    @"\b(" +
                    @"sneaker(s)?|shoe(s)?|trainer(s)?|footwear|" +
                    @"running|runner(s)?|basketball|" +
                    @"boot(s)?|chelsea|chukka|workboot|hiking|trail|" +
                    @"sandal(s)?|slide(s)?|clog(s)?|" +
                    @"gazelle|campus|samba|superstar|stan smith|" +
                    @"air max|air force|air jordan|jordan|dunk|blazer|" +
                    @"new balance|nb\b|asics|gel-?\w+|salomon|xt-?\w+|" +
                    @"converse|chuck|vans|old skool|sk8-?hi" +
                    @")\b"))
            {
                return "Sneakers";
            }

            // 3) Size-based fallback (last resort)
            if (p.variants != null)
            {
                foreach (var v in p.variants)
                {
                    var size = ((string)v.size ?? string.Empty).Trim().ToUpperInvariant();

                    if (ApparelSizes.Contains(size))
                        return "Apparel";

                    // Waist / inseam sizes: 32/32, 30-32, W32
                    if (Regex.IsMatch(size, @"^(W)?\d{2}([/-]\d{2})?$"))
                        return "Apparel";
                }
            }

            // 4) Default
            // Change to "Unknown" if you want zero false Sneaker defaults.
            return "Sneakers";
        }

        // ==========================
        // HTTP HELPERS (retry 429 / 5xx)
        // ==========================

        private static string TagsMergeAndCleanUp(dynamic shopifyProduct, TurumProduct turumProduct, string category)
        {
            // Merge tags
            var existingTagsCsv = (string)(shopifyProduct?.product.tags ?? "");
            var mergedTags = MergeTags(existingTagsCsv, new[]
            {
                    turumProduct.brand,
                    turumProduct.brand.ToLower(),
                    ToTitleCase(turumProduct.brand),
                    category,
                    "TURUM"
            });

            var hasTShirt = existingTagsCsv
                .Split(',')
                .Select(t => t.Trim())
                .Any(t => t.Equals("T-shirt", StringComparison.OrdinalIgnoreCase));

            // CLEANUP RULE - remove T-shirt tag if it is sneaker
            // if category is Sneakers and existingTagsCsv contain 'T-shirt', remove T-shirt tag if present (cleanup old tagging mistakes)
            if (string.Equals(category, "Sneakers", StringComparison.OrdinalIgnoreCase) && hasTShirt)
            {
                var list = mergedTags
                    .Split(',')
                    .Select(t => t.Trim())
                    .Where(t => !string.Equals(t, "T-shirt", StringComparison.OrdinalIgnoreCase))
                    .ToList();

                mergedTags = string.Join(", ", list);

                Console.WriteLine("Removed T-shirt tag (is sneaker) SKU " + turumProduct.sku);
            }

            var hasSneakers = existingTagsCsv
                .Split(',')
                .Select(t => t.Trim())
                .Any(t => t.Equals("Sneakers", StringComparison.OrdinalIgnoreCase));

            //
            // CLEANUP RULE - remove Sneakers tag if not sneaker
            if (!string.Equals(category, "Sneakers", StringComparison.OrdinalIgnoreCase) && hasSneakers)
            {
                var list = mergedTags
                    .Split(',')
                    .Select(t => t.Trim())
                    .Where(t => !string.Equals(t, "Sneakers", StringComparison.OrdinalIgnoreCase))
                    .ToList();

                mergedTags = string.Join(", ", list);

                Console.WriteLine("Removed Sneakers tag (not sneaker) SKU " + turumProduct.sku);
            }

            // CLEANUP - Remove Apparel tag if it Sneakers
            var hasApparel = existingTagsCsv
                .Split(',')
                .Select(t => t.Trim())
                .Any(t => t.Equals("Apparel", StringComparison.OrdinalIgnoreCase));
            if (string.Equals(category, "Sneakers", StringComparison.OrdinalIgnoreCase) && hasApparel)
            {
                var list = mergedTags
                    .Split(',')
                    .Select(t => t.Trim())
                    .Where(t => !string.Equals(t, "Apparel", StringComparison.OrdinalIgnoreCase))
                    .ToList();

                mergedTags = string.Join(", ", list);
                Console.WriteLine("Removed Apparel tag (not apparel) SKU " + turumProduct.sku);
            }

            // CLEANUP - Remove Footwear tag if it exists (we don't use this tag any more)
            var hasFootwear = existingTagsCsv
                .Split(',')
                .Select(t => t.Trim())
                .Any(t => t.Equals("Footwear", StringComparison.OrdinalIgnoreCase));
            if (hasFootwear)
            {
                var list = mergedTags
                    .Split(',')
                    .Select(t => t.Trim())
                    .Where(t => !string.Equals(t, "Footwear", StringComparison.OrdinalIgnoreCase))
                    .ToList();

                mergedTags = string.Join(", ", list);
                Console.WriteLine("Removed Footwear tag (not sneakers) SKU " + turumProduct.sku);
            }

            // Add popular models tags for sneakers that have these keywords in the name (Air Max 95, Air Max Plus, Air Max 1, Dn8, Tasman, and 2002R)
            if (string.Equals(category, "Sneakers", StringComparison.OrdinalIgnoreCase))
            {
                var name = turumProduct.name ?? "";
                if (name.IndexOf("Air Max 95", StringComparison.OrdinalIgnoreCase) >= 0)
                    mergedTags = MergeTags(mergedTags, new[] { "Air Max 95" });
                if (name.IndexOf("Air Max Plus", StringComparison.OrdinalIgnoreCase) >= 0)
                    mergedTags = MergeTags(mergedTags, new[] { "Air Max Plus" });
                if (name.IndexOf("Air Max 1", StringComparison.OrdinalIgnoreCase) >= 0)
                    mergedTags = MergeTags(mergedTags, new[] { "Air Max 1" });
                if (name.IndexOf("Dn8", StringComparison.OrdinalIgnoreCase) >= 0)
                    mergedTags = MergeTags(mergedTags, new[] { "Dn8" });
                if (name.IndexOf("Tasman", StringComparison.OrdinalIgnoreCase) >= 0)
                    mergedTags = MergeTags(mergedTags, new[] { "Tasman" });
                if (name.IndexOf("2002R", StringComparison.OrdinalIgnoreCase) >= 0)
                    mergedTags = MergeTags(mergedTags, new[] { "2002R" });
            }

            return mergedTags;

        }



        //
        // GrapghQL API
        //


    }
}
