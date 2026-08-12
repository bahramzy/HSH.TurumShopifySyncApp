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

            // Normalized product documents keep tags as a comma-separated string
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
                "XXS", "XS", "S", "M", "L", "XL", "XXL", "XXXL",
                "2XL", "3XL", "4XL"
            };

        private static readonly HashSet<string> ManagedCategoryTags =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "Apparel", "Bag", "Bodysuit", "Coat", "Dress", "Headwear",
                "Hoodie", "Jacket", "Knitwear", "Other", "Pants", "Shirt",
                "Shorts", "Skirt", "Sneakers", "Socks", "Sweatshirt",
                "T-shirt", "Underwear", "Vest"
            };

        private static string NormalizeTurumBrand(string brand)
        {
            var normalized = (brand ?? string.Empty).Trim();
            if (normalized.Length == 0)
                return string.Empty;

            if (Regex.IsMatch(normalized, @"^(unknown|unkown|n/?a|none|-)$", RegexOptions.IgnoreCase))
                return string.Empty;

            return normalized;
        }

        private static string ResolveTurumBrand(string sourceBrand, string productName)
        {
            var normalizedSourceBrand = NormalizeTurumBrand(sourceBrand);
            if (normalizedSourceBrand.Length > 0)
                return normalizedSourceBrand;

            var name = (productName ?? string.Empty).Trim();
            var knownPrefixes = new[]
            {
                new { Pattern = @"^new balance\b", Brand = "NEW BALANCE" },
                new { Pattern = @"^(?:air )?jordan\b", Brand = "AIR JORDAN" },
                new { Pattern = @"^adidas\b", Brand = "ADIDAS" },
                new { Pattern = @"^nike\b", Brand = "NIKE" },
                new { Pattern = @"^asics\b", Brand = "ASICS" },
                new { Pattern = @"^converse\b", Brand = "CONVERSE" },
                new { Pattern = @"^crocs\b", Brand = "CROCS" },
                new { Pattern = @"^salomon\b", Brand = "SALOMON" },
                new { Pattern = @"^puma\b", Brand = "PUMA" },
                new { Pattern = @"^reebok\b", Brand = "REEBOK" },
                new { Pattern = @"^vans\b", Brand = "VANS" },
                new { Pattern = @"^ugg\b", Brand = "UGG" },
                new { Pattern = @"^hoka\b", Brand = "HOKA" },
                new { Pattern = @"^birkenstock\b", Brand = "BIRKENSTOCK" },
                new { Pattern = @"^supreme\b", Brand = "SUPREME" },
                new { Pattern = @"^stussy\b", Brand = "STUSSY" },
                new { Pattern = @"^ami paris\b", Brand = "AMI PARIS" }
            };

            foreach (var candidate in knownPrefixes)
            {
                if (Regex.IsMatch(name, candidate.Pattern, RegexOptions.IgnoreCase))
                    return candidate.Brand;
            }

            // Shopify displays the shop name when vendor is empty. Use an explicit
            // fallback so the shop's default vendor never leaks into the Brand filter.
            return "OTHER";
        }

        private static bool IsUnknownBrandName(string value)
        {
            return !string.IsNullOrWhiteSpace(value) && NormalizeTurumBrand(value).Length == 0;
        }

        private static string DetectTurumCategory(dynamic p)
        {
            var name = ((string)p.name ?? string.Empty).ToLowerInvariant();

            // Explicit sneaker model names must win over colorway names that contain
            // apparel words, e.g. "Jordan 3 Retro Lucky Shorts".
            if (Regex.IsMatch(name, @"\b(?:air\s+)?jordan\s*(?:1|2|3|4|5|6|7|8|9|10|11|12|13|14)\s+(?:retro|mid|low|high)\b"))
                return "Sneakers";

            // 1) Accessories / Apparel (high precision first)

            // Headwear
            if (Regex.IsMatch(name, @"\b(beanie|balaclava|bucket hat|cap|hat|headwear|new era|[56]-panel)\b"))
                return "Headwear";

            // Non-wearable accessories occasionally arrive from Turum as "shoes".
            if (Regex.IsMatch(name, @"\b(packing tape|adhesive tape|tape roll)\b"))
                return "Other";

            // Baby clothing
            if (Regex.IsMatch(name, @"\b(body[ -]?suit|baby grow|onesie|romper)\b"))
                return "Bodysuit";

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

            // Other apparel
            if (Regex.IsMatch(name, @"\b(shorts?|swim shorts?|trunks)\b"))
                return "Shorts";
            if (Regex.IsMatch(name, @"\b(button[- ]?down|shirt|jersey|polo)\b"))
                return "Shirt";
            if (Regex.IsMatch(name, @"\b(sweater|jumper|cardigan|knit|knitted|knitwear)\b"))
                return "Knitwear";
            if (Regex.IsMatch(name, @"\b(vest|gilet|waistcoat)\b"))
                return "Vest";
            if (Regex.IsMatch(name, @"\b(dress)\b"))
                return "Dress";
            if (Regex.IsMatch(name, @"\b(skirt)\b"))
                return "Skirt";
            if (Regex.IsMatch(name, @"\b(boxers?|briefs?|underwear)\b"))
                return "Underwear";

            // 2) Positive shoe detection (after apparel/accessories)

            if (Regex.IsMatch(
                    name,
                    @"\b(" +
                    @"sneaker(s)?|shoe(s)?|trainer(s)?|footwear|" +
                    @"running|runner(s)?|basketball|" +
                    @"boot(s)?|chelsea|chukka|workboot|hiking|trail|" +
                    @"sandal(s)?|slide(s)?|clog(s)?|" +
                    @"gazelle|campus|samba|superstar|stan smith|" +
                    @"air max|air force|air jordan|jordan\s*(?:1|2|3|4|5|6|7|8|9|10|11|12|13|14)|dunk|blazer|" +
                    @"new balance|nb\b|asics|gel-?\w+|salomon|xt-?\w+|" +
                    @"converse|chuck|vans|old skool|sk8-?hi" +
                    @")\b"))
            {
                return "Sneakers";
            }

            // 3) Turum's structured category is a stronger fallback than model-name
            // guessing. Explicit apparel/accessory rules above still protect the
            // known cases where Turum incorrectly labels those products as shoes.
            var turumCategory = ((string)p.category ?? string.Empty).Trim();
            if (turumCategory.Equals("shoes", StringComparison.OrdinalIgnoreCase))
                return "Sneakers";
            if (turumCategory.Equals("apparel", StringComparison.OrdinalIgnoreCase))
                return "Apparel";

            // 4) Size-based fallback for feeds without a usable category
            if (p.variants != null)
            {
                foreach (var v in p.variants)
                {
                    var size = ((string)v.size ?? string.Empty).Trim().ToUpperInvariant();

                    if (ApparelSizes.Contains(size))
                        return "Apparel";

                    // Waist / inseam sizes: 32/32, 30-32, W32. A plain numeric
                    // size such as 43 is a common EU shoe size, not apparel.
                    if (Regex.IsMatch(size, @"^(W\d{2}|\d{2}[/-]\d{2})$"))
                        return "Apparel";
                }
            }

            // 5) Conservative default: an unrecognized product must never become
            // a sneaker merely because its name/size did not match a known rule.
            return "Other";
        }

        // ==========================
        // HTTP HELPERS (retry 429 / 5xx)
        // ==========================

        private static string TagsMergeAndCleanUp(dynamic shopifyProduct, TurumProduct turumProduct, string category)
        {
            var existingTagsCsv = (string)(shopifyProduct?.product.tags ?? "");
            var cleanedExistingTags = existingTagsCsv
                .Split(',')
                .Select(t => t.Trim())
                .Where(t => t.Length > 0)
                .Where(t => !ManagedCategoryTags.Contains(t))
                .Where(t => !t.Equals("Footwear", StringComparison.OrdinalIgnoreCase))
                .Where(t => !IsUnknownBrandName(t));

            var mergedTags = MergeTags(string.Join(", ", cleanedExistingTags), new[]
            {
                NormalizeTurumBrand(turumProduct.brand),
                category,
                "TURUM"
            });

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
