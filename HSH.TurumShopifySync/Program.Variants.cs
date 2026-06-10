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



            // existingBySize: option1 -> variant object

            var existingBySize = new Dictionary<string, dynamic>(StringComparer.OrdinalIgnoreCase);

            var existingSizes = new List<string>();

            bool blockNewVariantCreation = false;



            var seenSizes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);



            // Collect existing variants and sizes

            // Shopify product variants - size -> variant object dictionary

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

                // If shopifyProductDoc shape is unexpected, fall back to empty existing set

            }



            // Collect Turum sizes (deduped and in source order)

            var turumSizes = new List<string>();

            foreach (var tv in turum.variants)

            {

                var size = (tv.eu_size ?? tv.size);

                if (string.IsNullOrWhiteSpace(size)) continue;

                size = size.Trim();

                if (!turumSizes.Contains(size, StringComparer.OrdinalIgnoreCase))

                    turumSizes.Add(size);

            }



            // Desired sizes = union(existingSizes, turumSizes) then sorted by CompareSizes

            var desiredSizes = new List<string>(existingSizes);

            foreach (var s in turumSizes)

            {

                if (!desiredSizes.Contains(s, StringComparer.OrdinalIgnoreCase))

                    desiredSizes.Add(s);

            }



            desiredSizes.Sort(Comparer<string>.Create((a, b) => CompareSizes(a, b)));



            // Helper: desired position (1-based) for a size

            Func<string, int> getDesiredPosition = size =>

            {

                for (int i = 0; i < desiredSizes.Count; i++)

                {

                    if (string.Equals(desiredSizes[i], size, StringComparison.OrdinalIgnoreCase))

                        return i + 1;

                }

                return desiredSizes.Count + 1;

            };



            // Keep created variants to position them after creation

            var createdVariants = new List<(string size, long variantId)>();



            // First pass: update existing variants' price/sku and create missing variants

            foreach (var tv in turum.variants)

            {

                var size = (tv.eu_size ?? tv.size);

                if (string.IsNullOrWhiteSpace(size)) continue;

                size = size.Trim();



                if (!seenSizes.Add(size)) continue; // skip duplicates from TURUM



                var raw = ConvertToDkk(tv.price) * Settings.MomsRate + Settings.Profit;

                var dkk = RoundRetailPrice(raw);



                dynamic existing;

                if (existingBySize.TryGetValue(size, out existing))

                {

                    var oldPrice = ParseShopifyMoney(existing.price);

                    var newPrice = dkk;



                    if (oldPrice == newPrice)

                        continue;



                    long variantId = ToLong(existing.id);



                    var payload = new

                    {

                        variant = new

                        {

                            id = variantId,

                            price = dkk.ToString("0", CultureInfo.InvariantCulture),

                            sku = turum.sku

                        }

                    };



                    await ShopifyPutAsync(shopify, "variants/" + variantId + ".json", payload, ct);

                }

                else

                {

                    if (blockNewVariantCreation) continue;



                    var payload = new

                    {

                        variant = new

                        {

                            option1 = size,

                            price = dkk.ToString("0", CultureInfo.InvariantCulture),

                            sku = turum.sku,

                            inventory_management = "shopify",

                            inventory_policy = "deny",

                            taxable = false,

                            requires_shipping = true,

                            barcode = tv.ean

                        }

                    };



                    try

                    {

                        // Create and capture response to obtain created variant id

                        var resp = await ShopifyPostAsync<dynamic>(shopify, "products/" + productId + "/variants.json", payload, doc => doc, ct);



                        // Log full create response for debugging

                        //try

                        //{

                        //    Console.WriteLine("[DEBUG] Variant create response: " + JsonConvert.SerializeObject(resp));

                        //}

                        //catch { /* ignore logging errors */ }



                        long createdVariantId = 0;

                        try { createdVariantId = ToLong(resp.variant.id); } catch { createdVariantId = 0; }



                        createdAny = true;



                        if (createdVariantId > 0)

                        {

                            createdVariants.Add((size, createdVariantId));

                            Console.WriteLine("CREATED variant SKU " + turum.sku + " size " + size + " productId " + productId + " variantId " + createdVariantId);



                            // Verify variant exists server-side (same store/token)

                            //try

                            //{

                            //    dynamic vdoc = await ShopifyGetAsync<dynamic>(shopify, "variants/" + createdVariantId + ".json", ct);

                            //    Console.WriteLine("[DEBUG] Fetched created variant " + createdVariantId + ": " + JsonConvert.SerializeObject(vdoc));

                            //}

                            //catch (Exception ex)

                            //{

                            //    // 404 or other errors — surface for debugging

                            //    Console.WriteLine("[WARN] Fetching created variant " + createdVariantId + " failed: " + ex.Message);

                            //    Console.WriteLine("[WARN] You should inspect the POST response above and ensure Postman uses the same store/token and API version.");

                            //}



                            // Fetch product to inspect options & variant list

                            //try

                            //{

                            //    dynamic pdoc = await ShopifyGetAsync<dynamic>(shopify, "products/" + productId + ".json?fields=variants,options", ct);

                            //    Console.WriteLine("[DEBUG] Product variants after create: " + JsonConvert.SerializeObject(pdoc.product.variants));

                            //    Console.WriteLine("[DEBUG] Product options after create: " + JsonConvert.SerializeObject(pdoc.product.options));

                            //}

                            //catch (Exception ex)

                            //{

                            //    Console.WriteLine("[WARN] Failed to fetch product after variant create: " + ex.Message);

                            //}

                        }

                        else

                        {

                            // No numeric id in response — log and throw to make failure visible

                            Console.WriteLine("[ERROR] Variant create response did not include numeric id: " + JsonConvert.SerializeObject(resp));

                            throw new Exception("Variant create returned no id for product " + productId + " size " + size);

                        }

                    }

                    catch (Exception ex)

                    {

                        if (ex.Message.IndexOf("option value linked to a metafield", StringComparison.OrdinalIgnoreCase) >= 0)

                        {

                            blockNewVariantCreation = true;

                            Console.WriteLine("SKIP creating new variant (connected options): SKU " + turum.sku + " size " + size + " productId " + productId);

                            continue;

                        }



                        throw;

                    }

                }

            }



            // Second pass: set positions for created variants (do this in desired order)

            if (createdVariants.Count > 0)

            {

                Console.WriteLine("[INFO] Created " + createdVariants.Count + " variants. Starting position updates. SKU " + turum.sku + " productId " + productId);



                // Map created size -> id for quick lookup

                var createdMap = createdVariants.ToDictionary(x => x.size, x => x.variantId, StringComparer.OrdinalIgnoreCase);



                int desiredPos = 1;

                foreach (var s in desiredSizes)

                {

                    if (createdMap.TryGetValue(s, out var vid))

                    {

                        Console.WriteLine("Setting position for created variant SKU " + turum.sku + " size " + s + " to " + desiredPos);

                        try

                        {

                            var posPayload = new

                            {

                                variant = new

                                {

                                    id = vid,

                                    position = desiredPos

                                }

                            };



                            await ShopifyPutAsync<dynamic>(shopify, "variants/" + vid + ".json", posPayload, d => d, ct);

                        }

                        catch (Exception ex)

                        {

                            // Non-fatal: log and continue. Caller still has a fallback reorder step.

                            Console.WriteLine("[WARN] Failed to set variant position for created variant " + vid + " product " + productId + " size " + s + " : " + ex.Message);

                        }

                    }



                    desiredPos++;

                }

            }



            // Remove Shopify variants that are not present in Turum anymore.

            // Keep variants that have special barcodes (e.g. "HSH") as a safety.

            try

            {

                var turumSet = new HashSet<string>(turumSizes, StringComparer.OrdinalIgnoreCase);



                foreach (var kv in existingBySize)

                {

                    var size = kv.Key;

                    // If Turum still has this size, keep it

                    if (turumSet.Contains(size))

                        continue;



                    dynamic sv = kv.Value;

                    var barcode = ((string)sv.barcode ?? "").Trim();



                    // Preserve HSH-marked variants (or any other special barcode)

                    if (string.Equals(barcode, "HSH", StringComparison.OrdinalIgnoreCase))

                        continue;



                    long variantId = ToLong(sv.id);

                    if (variantId <= 0) continue;



                    try

                    {

                        await ShopifyDeleteAsync(shopify, "variants/" + variantId + ".json", ct);

                        deletedAny = true;

                        Console.WriteLine("[INFO] Deleted Shopify-only variant size " + size + " variantId " + variantId + " for product " + productId);

                    }

                    catch (Exception ex)

                    {

                        Console.WriteLine("[WARN] Failed to delete variant " + variantId + ": " + ex.Message);

                        // non-fatal — continue

                    }

                }

            }

            catch (Exception ex)

            {

                Console.WriteLine("[WARN] Cleanup extra variants failed: " + ex.Message);

            }



            return createdAny || deletedAny;

        }



        private static async Task EnsureVariantPositionsBySizeAsync(HttpClient shopify, long productId, dynamic shopifyProductDoc, CancellationToken ct)

        {

            if (shopifyProductDoc == null || shopifyProductDoc.product == null || shopifyProductDoc.product.variants == null)

                return;



            // Build list: (id, size, currentPosition)

            var list = new List<(long id, string size, int pos)>();



            foreach (var v in shopifyProductDoc.product.variants)

            {

                var id = ToLong(v.id);



                // Size is usually option1 (your "Vælg størrelse")

                var size = (string)(v.option1 ?? v.title ?? "");

                size = (size ?? "").Trim();

                if (size.Length == 0) continue;



                int pos = 0;

                try { pos = (int)v.position; } catch { /* ignore */ }



                list.Add((id, size, pos));

            }



            // Sort by size

            list.Sort((a, b) => CompareSizes(a.size, b.size));



            // Update only if position differs

            int desired = 1;

            foreach (var item in list)

            {

                if (item.pos == desired)

                {

                    desired++;

                    continue;

                }



                Console.WriteLine("Reorder variant position SKU? productId " + productId +

                                  " size " + item.size + " " + item.pos + " -> " + desired);



                var payload = new

                {

                    variant = new

                    {

                        id = item.id,

                        position = desired

                    }

                };



                // Use your generic PUT helper if you have it:

                await ShopifyPutAsync<dynamic>(

                    shopify,

                    "variants/" + item.id + ".json",

                    payload,

                    d => d,

                    ct);



                desired++;

            }

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

        // SHOPIFY: INVENTORY (set to Turum stock; missing sizes -> 0)

        // ==========================



        //private static async Task SetInventoryFromTurumAsync(HttpClient shopify, long locationId, dynamic shopifyProductDoc, TurumProduct turum, CancellationToken ct)

        //{

        //    var stockBySize = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);



        //    foreach (var v in turum.variants)

        //    {

        //        var size = (v.eu_size ?? v.size);

        //        if (string.IsNullOrWhiteSpace(size))

        //            continue;



        //        stockBySize[size.Trim()] = v.stock;

        //    }



        //    foreach (var sv in shopifyProductDoc.product.variants)

        //    {

        //        string size = (string)(sv.option1 ?? sv.title ?? "");

        //        if (string.IsNullOrWhiteSpace(size))

        //            continue;



        //        size = size.Trim();



        //        int available;

        //        if (!stockBySize.TryGetValue(size, out available))

        //        {

        //            //available = 0;

        //            // If size not in TURUM, do NOT touch Shopify inventory

        //            continue;

        //        }



        //        long inventoryItemId = (long)sv.inventory_item_id;



        //        int currentQty = 0;

        //        try

        //        {

        //            // Shopify returns inventory_quantity on variant in many product responses

        //            currentQty = (int)sv.inventory_quantity;

        //        }

        //        catch

        //        {

        //            // if missing, keep 0 and proceed with update

        //        }



        //        //Console.WriteLine($"[INV-CHECK] SKU {turum.sku} size {size} | Shopify={currentQty} Turum={available}");





        //        if (currentQty == available)

        //        {

        //            //Console.WriteLine($"[INV-SKIP] SKU {turum.sku} size {size} (no change)");



        //            // skip inventory update

        //            //Console.WriteLine("SKIP inventory update...");

        //            continue;

        //        }



        //        var payload = new

        //        {

        //            location_id = locationId,

        //            inventory_item_id = inventoryItemId,

        //            available = available

        //        };



        //        await ShopifyPostAsync<dynamic>(shopify, "inventory_levels/set.json", payload, doc => doc, ct);

        //    }

        //}



        private static async Task SetInventoryFromTurumAsync(

            HttpClient shopify,

            long locationId,

            dynamic shopifyProductDoc,   // normalized: { product = { variants = [...] } }

            TurumProduct turum,

            CancellationToken ct)

        {

            var stockBySize = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);



            foreach (var v in turum.variants)

            {

                var size = (v.eu_size ?? v.size);

                if (string.IsNullOrWhiteSpace(size))

                    continue;



                stockBySize[size.Trim()] = v.stock;

            }



            foreach (var sv in shopifyProductDoc.product.variants)

            {

                // REST-style: option1 is typically the Size

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

                    // normalized uses REST-style name

                    currentQty = (int)sv.inventory_quantity;

                }

                catch

                {

                    // if missing, keep 0 and proceed with update

                }



                if (currentQty == available)

                    continue;



                var payload = new

                {

                    location_id = locationId,

                    inventory_item_id = inventoryItemId,

                    available = available

                };



                await ShopifyPostAsync<dynamic>(shopify, "inventory_levels/set.json", payload, doc => doc, ct);

            }

        }



        // ==========================

        // SHOPIFY: CREATE / UPDATE PAYLOADS

        // ==========================

    }
}
