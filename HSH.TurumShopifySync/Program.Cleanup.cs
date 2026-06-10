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
        // CLEANUP: Archive Shopify products missing in Turum feed

        // ==========================

        private static async Task ArchiveAndCleanupMissingTurumProductsAsync(HttpClient shopify, IList<TurumProduct> turumProducts, Dictionary<string, long> activeSkuIndex, CancellationToken ct)

        {

            // 1) Build set of current Turum SKUs

            var turumSkus = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var p in turumProducts)

            {

                var sku = ((string)p.sku ?? "").Trim();

                if (sku.Length > 0) turumSkus.Add(sku);

            }



            Console.WriteLine("[INFO] Cleanup: Turum SKUs=" + turumSkus.Count + " Shopify SKUs indexed=" + activeSkuIndex.Count);



            int checkedCount = 0;

            int archivedCount = 0;

            int keptCount = 0;

            int deletedVariantsTotal = 0;



            foreach (var kvp in activeSkuIndex)

            {

                ct.ThrowIfCancellationRequested();



                var sku = (kvp.Key ?? "").Trim();

                var productId = kvp.Value;



                if (sku.Length == 0 || productId <= 0)

                    continue;



                // If SKU exists in current Turum feed -> do nothing

                if (turumSkus.Contains(sku))

                    continue;



                checkedCount++;



                // Fetch product (only for missing SKUs)

                // Need: tags + variants(id,barcode)

                dynamic productDoc = await ShopifyGetAsync<dynamic>(

                    shopify,

                    "products/" + productId + ".json?fields=id,title,vendor,tags,variants",

                    ct);



                // SAFETY GUARD — Only TURUM-PRODUCT

                if (!HasTag(productDoc, "TURUM"))

                {

                    Console.WriteLine(

                        "SKIP archive (not Turum product) productId " + productId);



                    continue;

                }



                // Safety: if product has tag "PO" -> do not archive automatically

                if (HasTag(productDoc, "PO"))

                {

                    Console.WriteLine("[WARN] SKIP archive (tag PO) missing SKU " + sku + " productId " + productId);

                    keptCount++;

                    continue;

                }



                // If any variant has barcode HSH -> keep product, delete non-HSH variants

                if (HasVariantBarcode(productDoc, "HSH"))

                {

                    Console.WriteLine("[WARN] Missing in Turum but has HSH variant -> keep product, delete other variants. SKU " + sku);



                    int deleted = await DeleteNonBarcodeVariantsAsync(shopify, productDoc, "HSH", ct);

                    deletedVariantsTotal += deleted;



                    Console.WriteLine("[INFO] Deleted " + deleted + " non-HSH variants. SKU " + sku);

                    keptCount++;



                    // Remove tag TURUM (if present) from product tags. It is no longer TURUM product

                    try

                    {

                        var existingTagsCsv = (string)(productDoc.product.tags ?? "");

                        var tagsList = existingTagsCsv

                            .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)

                            .Select(t => t.Trim())

                            .Where(t => !string.Equals(t, "TURUM", StringComparison.OrdinalIgnoreCase) && t.Length > 0)

                            .ToList();



                        var newTagsCsv = string.Join(", ", tagsList);



                        // Only update if tags changed (i.e., TURUM removed)

                        if (!string.Equals(NormalizeTags(existingTagsCsv), NormalizeTags(newTagsCsv), StringComparison.OrdinalIgnoreCase))

                        {

                            var updatePayload = new

                            {

                                product = new

                                {

                                    id = productId,

                                    tags = newTagsCsv

                                }

                            };



                            await ShopifyPutAsync<dynamic>(shopify, "products/" + productId + ".json", updatePayload, d => d, ct);

                            Console.WriteLine("[INFO] Removed TURUM tag from productId " + productId + " SKU " + sku + " NewTags: " + newTagsCsv);

                        }

                    }

                    catch (Exception ex)

                    {

                        Console.WriteLine("[WARN] Failed to remove TURUM tag for productId " + productId + ": " + ex.Message);

                        // Non-fatal: continue sync

                    }



                    // Set vendor to Highstreet Heaven, now that it's no longer Turum

                    //await ShopifyPutAsync<dynamic>(shopify, "products/" + productId + ".json",

                    //    new

                    //    {

                    //        product = new

                    //        {

                    //            id = productId,

                    //            vendor = "Highstreet Heaven" // we use vendor for the brand of the product

                    //        }

                    //    },

                    //    d => d,

                    //    ct);



                    // Remove tag TURUM







                    continue;

                }



                // Otherwise archive the product

                Console.WriteLine("[WARN] ARCHIVE missing in Turum (no HSH variants). SKU " + sku + " productId " + productId);



                var archivePayload = new

                {

                    product = new

                    {

                        id = productId,

                        status = "archived"

                    }

                };



                await ShopifyPutAsync<dynamic>(shopify, "products/" + productId + ".json", archivePayload, d => d, ct);

                archivedCount++;

            }



            Console.WriteLine("[INFO] Cleanup done. MissingChecked=" + checkedCount +

                              " Archived=" + archivedCount +

                              " Kept(HSH/PO)=" + keptCount +

                              " DeletedVariants=" + deletedVariantsTotal);

        }



        // Check if product has specific variant with the barcode

        private static bool HasVariantBarcode(dynamic productDoc, string barcode)

        {

            if (productDoc == null || productDoc.product == null || productDoc.product.variants == null)

                return false;



            var target = (barcode ?? "").Trim();

            if (target.Length == 0) return false;



            foreach (var v in productDoc.product.variants)

            {

                var bc = ((string)v.barcode ?? "").Trim();

                if (string.Equals(bc, target, StringComparison.OrdinalIgnoreCase))

                    return true;

            }



            return false;

        }



        // Delete all variants that do not match the keepBarcode

        private static async Task<int> DeleteNonBarcodeVariantsAsync(HttpClient shopify, dynamic productDoc, string keepBarcode, CancellationToken ct)

        {

            if (productDoc == null || productDoc.product == null || productDoc.product.variants == null)

                return 0;



            var keep = (keepBarcode ?? "").Trim();

            int deleted = 0;



            foreach (var v in productDoc.product.variants)

            {

                var bc = ((string)v.barcode ?? "").Trim();



                // Keep those that match keepBarcode (HSH)

                if (string.Equals(bc, keep, StringComparison.OrdinalIgnoreCase))

                    continue;



                long variantId = ToLong(v.id);



                // DELETE /variants/{id}.json

                await ShopifyDeleteAsync(shopify, "variants/" + variantId + ".json", ct);

                deleted++;

            }



            return deleted;

        }



        private static long ToLong(dynamic value)

        {

            if (value == null) return 0;



            var token = value as Newtonsoft.Json.Linq.JToken;

            if (token != null)

                return token.Value<long>();



            if (value is long) return (long)value;

            if (value is int) return (int)value;



            long l;

            if (long.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out l))

                return l;



            throw new Exception("Cannot convert to long: " + value);

        }



        //

        // Skip product update if no changes

        //

        private static string NormalizeTags(string tagsCsv)

        {

            if (string.IsNullOrWhiteSpace(tagsCsv)) return "";

            var set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);



            foreach (var t in tagsCsv.Split(','))

            {

                var tag = t.Trim();

                if (tag.Length > 0) set.Add(tag);

            }



            return string.Join(",", set); // canonical

        }



        private static bool ProductUpdateNeeded(dynamic existingProductDoc, string newTitle, string newVendor, string newProductType, string newTagsCsv)

        {

            var ep = existingProductDoc.product;



            if (!EqualsIgnoreCase((string)ep.title, newTitle)) return true;

            if (!EqualsIgnoreCase((string)ep.vendor, newVendor)) return true;

            if (!EqualsIgnoreCase((string)ep.product_type, newProductType)) return true;



            var oldTags = NormalizeTags((string)ep.tags);

            var newTags = NormalizeTags(newTagsCsv);

            if (!string.Equals(oldTags, newTags, StringComparison.OrdinalIgnoreCase)) return true;



            return false;

        }



        // ==========================

        // TURUM

        // ==========================

    }
}
