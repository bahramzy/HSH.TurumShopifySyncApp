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
        private static async Task<bool> UpsertVariantsBySizeAsync(HttpClient shopify, long productId, dynamic shopifyProductDoc, TurumProduct turum, CancellationToken ct)
        {
            bool createdAny = false;
            bool deletedAny = false;

            var existingBySize = new Dictionary<string, dynamic>(StringComparer.OrdinalIgnoreCase);
            var existingSizes = new List<string>();
            var seenSizes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool blockNewVariantCreation = false;
            var sizeOptionName = GetVariantSizeOptionName(shopifyProductDoc);

            try
            {
                foreach (var sv in shopifyProductDoc.product.variants)
                {
                    string size = (string)(sv.option1 ?? sv.title ?? "");
                    if (string.IsNullOrWhiteSpace(size)) continue;
                    size = size.Trim();

                    if (!existingBySize.ContainsKey(size))
                    {
                        existingBySize[size] = sv;
                        existingSizes.Add(size);
                    }
                }
            }
            catch
            {
                // If shopifyProductDoc shape is unexpected, fall back to empty existing set.
            }

            var turumSizes = new List<string>();
            foreach (var tv in turum.variants)
            {
                var size = tv.eu_size ?? tv.size;
                if (string.IsNullOrWhiteSpace(size)) continue;
                size = size.Trim();

                if (!turumSizes.Contains(size, StringComparer.OrdinalIgnoreCase))
                    turumSizes.Add(size);
            }

            var desiredSizes = new List<string>(existingSizes);
            foreach (var s in turumSizes)
            {
                if (!desiredSizes.Contains(s, StringComparer.OrdinalIgnoreCase))
                    desiredSizes.Add(s);
            }

            desiredSizes.Sort(Comparer<string>.Create((a, b) => CompareSizes(a, b)));

            var createdVariants = new List<(string size, long variantId)>();
            var variantUpdates = new List<VariantUpdateInput>();

            foreach (var tv in turum.variants)
            {
                var size = tv.eu_size ?? tv.size;
                if (string.IsNullOrWhiteSpace(size)) continue;
                size = size.Trim();

                if (!seenSizes.Add(size)) continue;

                var raw = ConvertToDkk(tv.price) * Settings.MomsRate + Settings.Profit;
                var dkk = RoundRetailPrice(raw);

                dynamic existing;
                if (existingBySize.TryGetValue(size, out existing))
                {
                    var oldPrice = ParseShopifyMoney(existing.price);
                    if (oldPrice == dkk)
                        continue;

                    long variantId = ToLong(existing.id);
                    variantUpdates.Add(new VariantUpdateInput { VariantId = variantId, Price = dkk, Sku = turum.sku });
                    continue;
                }

                if (blockNewVariantCreation)
                    continue;

                try
                {
                    var createdVariantId = await CreateVariantGraphQlAsync(shopify, productId, sizeOptionName, size, dkk, turum.sku, tv.ean, ct);
                    createdAny = true;
                    createdVariants.Add((size, createdVariantId));
                    Console.WriteLine("CREATED variant SKU " + turum.sku + " size " + size + " productId " + productId + " variantId " + createdVariantId);
                }
                catch (Exception ex)
                {
                    if (ex.Message.IndexOf("option value linked to a metafield", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        blockNewVariantCreation = true;
                        Console.WriteLine("SKIP creating new variant (connected options): SKU " + turum.sku + " size " + size + " productId " + productId);
                        continue;
                    }

                    if (ex.Message.IndexOf("Option does not exist", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        blockNewVariantCreation = true;
                        Console.WriteLine("SKIP creating new variant (missing Shopify option): SKU " + turum.sku + " size " + size + " option " + sizeOptionName + " productId " + productId);
                        continue;
                    }

                    throw;
                }
            }

            if (variantUpdates.Count > 0)
                await UpdateVariantsGraphQlAsync(shopify, productId, variantUpdates, ct);

            if (createdVariants.Count > 0)
            {
                Console.WriteLine("[INFO] Created " + createdVariants.Count + " variants. Starting position updates. SKU " + turum.sku + " productId " + productId);

                var createdMap = createdVariants.ToDictionary(x => x.size, x => x.variantId, StringComparer.OrdinalIgnoreCase);
                var positions = new List<(long id, int position)>();

                int desiredPos = 1;
                foreach (var s in desiredSizes)
                {
                    long vid;
                    if (createdMap.TryGetValue(s, out vid))
                    {
                        Console.WriteLine("Setting position for created variant SKU " + turum.sku + " size " + s + " to " + desiredPos);
                        positions.Add((vid, desiredPos));
                    }

                    desiredPos++;
                }

                if (positions.Count > 0)
                {
                    try
                    {
                        await ReorderVariantsGraphQlAsync(shopify, productId, positions, ct);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("[WARN] Failed to set created variant positions for product " + productId + ": " + ex.Message);
                    }
                }
            }

            try
            {
                var turumSet = new HashSet<string>(turumSizes, StringComparer.OrdinalIgnoreCase);

                foreach (var kv in existingBySize)
                {
                    var size = kv.Key;
                    if (turumSet.Contains(size))
                        continue;

                    dynamic sv = kv.Value;
                    var barcode = ((string)sv.barcode ?? "").Trim();

                    if (string.Equals(barcode, "HSH", StringComparison.OrdinalIgnoreCase))
                        continue;

                    long variantId = ToLong(sv.id);
                    if (variantId <= 0) continue;

                    try
                    {
                        await DeleteVariantGraphQlAsync(shopify, productId, variantId, ct);
                        deletedAny = true;
                        Console.WriteLine("[INFO] Deleted Shopify-only variant size " + size + " variantId " + variantId + " for product " + productId);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("[WARN] Failed to delete variant " + variantId + ": " + ex.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("[WARN] Cleanup extra variants failed: " + ex.Message);
            }

            return createdAny || deletedAny;
        }

        private static string GetVariantSizeOptionName(dynamic shopifyProductDoc)
        {
            const string defaultOptionName = "Vælg størrelse";

            try
            {
                foreach (var option in shopifyProductDoc.product.options)
                {
                    var name = ((string)option.name ?? "").Trim();
                    if (name.Length == 0)
                        continue;

                    if (name.Equals(defaultOptionName, StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("Size", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("Størrelse", StringComparison.OrdinalIgnoreCase))
                    {
                        return name;
                    }
                }
            }
            catch { }

            try
            {
                foreach (var option in shopifyProductDoc.product.options)
                {
                    var name = ((string)option.name ?? "").Trim();
                    if (name.Length > 0)
                        return name;
                }
            }
            catch { }

            return defaultOptionName;
        }

        private static async Task EnsureVariantPositionsBySizeAsync(HttpClient shopify, long productId, dynamic shopifyProductDoc, CancellationToken ct)
        {
            if (shopifyProductDoc == null || shopifyProductDoc.product == null || shopifyProductDoc.product.variants == null)
                return;

            var list = new List<(long id, string size, int pos)>();

            foreach (var v in shopifyProductDoc.product.variants)
            {
                var id = ToLong(v.id);
                var size = (string)(v.option1 ?? v.title ?? "");
                size = (size ?? "").Trim();
                if (size.Length == 0 || id <= 0) continue;

                int pos = 0;
                try { pos = (int)v.position; } catch { }

                list.Add((id, size, pos));
            }

            list.Sort((a, b) => CompareSizes(a.size, b.size));

            var positions = new List<(long id, int position)>();
            for (int i = 0; i < list.Count; i++)
            {
                var desired = i + 1;
                if (list[i].pos != desired)
                {
                    Console.WriteLine("Reorder variant position SKU? productId " + productId +
                                      " size " + list[i].size + " " + list[i].pos + " -> " + desired);
                }

                positions.Add((list[i].id, desired));
            }

            if (positions.Count > 0)
                await ReorderVariantsGraphQlAsync(shopify, productId, positions, ct);
        }
        private static int CompareSizes(string a, string b)

        {

            // 1) numeric sizes (e.g. 38, 44.5) -> numeric compare

            if (TryParseSizeNumber(a, out var na) && TryParseSizeNumber(b, out var nb))

                return na.CompareTo(nb);



            // 2) apparel sizes -> fixed order

            var ra = ApparelRank(a);

            var rb = ApparelRank(b);

            if (ra != 999 && rb != 999)

                return ra.CompareTo(rb);



            // 3) fallback string compare

            return string.Compare(a, b, StringComparison.OrdinalIgnoreCase);

        }



        private static bool TryParseSizeNumber(string s, out decimal d)

        {

            d = 0m;

            if (string.IsNullOrWhiteSpace(s)) return false;



            // allow "44,5" too

            s = s.Trim().Replace(',', '.');



            return decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out d);

        }



        private static int ApparelRank(string s)

        {

            s = (s ?? "").Trim().ToUpperInvariant();



            // common apparel sizes

            if (s == "XXS") return 0;

            if (s == "XS") return 1;

            if (s == "S") return 2;

            if (s == "M") return 3;

            if (s == "L") return 4;

            if (s == "XL") return 5;

            if (s == "XXL") return 6;



            return 999; // not apparel

        }



        private static bool VariantReorderNeeded(dynamic shopifyProductDoc)

        {

            if (shopifyProductDoc == null || shopifyProductDoc.product == null || shopifyProductDoc.product.variants == null)

                return false;



            var list = new List<(long id, string size, int pos)>();



            foreach (var v in shopifyProductDoc.product.variants)

            {

                var size = (string)(v.option1 ?? v.title ?? "");

                size = (size ?? "").Trim();

                if (size.Length == 0) continue;



                int pos = 0;

                try { pos = (int)v.position; } catch { }



                list.Add((ToLong(v.id), size, pos));

            }



            if (list.Count <= 1)

                return false;



            // Compute desired order by size

            var sorted = list.OrderBy(x => x.size, Comparer<string>.Create((a, b) => CompareSizes(a, b))).ToList();



            // If any position is not matching desired (1..N), reorder needed

            for (int i = 0; i < sorted.Count; i++)

            {

                int desiredPos = i + 1;

                if (sorted[i].pos != desiredPos)

                    return true;

            }



            return false;

        }



        private static decimal ParseShopifyMoney(dynamic value)

        {

            if (value == null) return 0m;



            // Shopify returns money as string e.g. "845.00"

            var s = Convert.ToString(value, CultureInfo.InvariantCulture);

            if (string.IsNullOrWhiteSpace(s)) return 0m;



            decimal d;

            if (!decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out d))

                return 0m;



            return d;

        }



        // ==========================
        // SHOPIFY: INVENTORY
        // ==========================
        private static async Task SetInventoryFromTurumAsync(

            HttpClient shopify,

            long locationId,

            dynamic shopifyProductDoc,   // normalized: { product = { variants = [...] } }

            TurumProduct turum,

            CancellationToken ct)

        {

            var stockBySize = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var inventoryUpdates = new List<InventoryQuantityInput>();



            foreach (var v in turum.variants)

            {

                var size = (v.eu_size ?? v.size);

                if (string.IsNullOrWhiteSpace(size))

                    continue;



                stockBySize[size.Trim()] = v.stock;

            }



            foreach (var sv in shopifyProductDoc.product.variants)

            {

                // Normalized shape: option1 is typically the Size

                string size = (string)(sv.option1 ?? sv.title ?? "");

                if (string.IsNullOrWhiteSpace(size))

                    continue;



                size = size.Trim();



                if (!stockBySize.TryGetValue(size, out var availableFromTurum))

                {

                    // If size not in TURUM, do NOT touch Shopify inventory

                    continue;

                }



                // Read custom.hsh_antal from injected metafields

                int extra = 0;

                try

                {

                    if (sv.metafields != null)

                    {

                        foreach (var mf in sv.metafields)

                        {

                            var ns = (string)mf.@namespace;

                            var key = (string)mf.key;



                            if (!"custom".Equals(ns, StringComparison.OrdinalIgnoreCase)) continue;

                            if (!"hsh_antal".Equals(key, StringComparison.OrdinalIgnoreCase)) continue;



                            var s = (string)mf.value;

                            if (!string.IsNullOrWhiteSpace(s) && int.TryParse(s, out var parsed))

                                extra = parsed;



                            break;

                        }

                    }

                }

                catch

                {

                    // ignore metafield issues, treat as 0

                }



                if (extra < 0) extra = 0;



                int available = availableFromTurum + extra;



                long inventoryItemId = (long)sv.inventory_item_id;



                int currentQty = 0;

                try

                {

                    // normalized product document uses this field name

                    currentQty = (int)sv.inventory_quantity;

                }

                catch

                {

                    // if missing, keep 0 and proceed with update

                }



                if (currentQty == available)

                    continue;



                inventoryUpdates.Add(new InventoryQuantityInput { InventoryItemId = inventoryItemId, Quantity = available });

            }

            if (inventoryUpdates.Count > 0)
                await SetInventoryQuantitiesGraphQlAsync(shopify, locationId, inventoryUpdates, ct);

        }



        // ==========================

        // SHOPIFY: CREATE / UPDATE PAYLOADS

        // ==========================

    }
}
