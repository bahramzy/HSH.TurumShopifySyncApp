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
    internal class Program
    {
        // Hvis et produkt findes og har tagget PO på Shopify, så skal det ikke opdateres, men oprettes som et nyt produkt.
        // Alle produkter skal have tilføjet kollekktionen "Sneakers" på Shopify. Derudover skal de have kollektionen, der svarer til brandet fra Turum. Og hvis kollektionen ikke findes, skal den oprettes først.
        // Alle produkter fra Turum skal have kategorien "Sneakers" på Shopify.


        // === CONFIG ===
        private const string ShopifyStoreDomain = "highstreet-heaven-2.myshopify.com";
        private const string ShopifyApiVersion = "2026-01"; // "latest" in REST docs at the moment
        private const decimal EurToDkkRate = 7.47m;         // TODO: replace with real FX later
        private const decimal MomsRate = 1.25m;
        private const decimal Profit = 275m;                // DKK profit per item
        private const string ShopifySneakersCategoryId = "gid://shopify/TaxonomyCategory/aa-8-8"; // TODO set actual. A constant for the Sneakers taxonomy category ID

        // Image-checking HttpClient
        private static readonly HttpClient _imageCheckClient = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate })
        {
            Timeout = TimeSpan.FromSeconds(10)
        };

        private static bool EqualsIgnoreCase(string a, string b) => string.Equals((a ?? "").Trim(), (b ?? "").Trim(), StringComparison.OrdinalIgnoreCase);

        private static readonly string ShopifyAdminToken = "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"; //Environment.GetEnvironmentVariable("SHOPIFY_ADMIN_TOKEN");
        private static readonly string TurumToken = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiJoaWdoc3RyZWV0aGVhdmVuMjRAZ21haWwuY29tIiwicm9sZSI6InR1cnVtX2N1c3RvbWVyIiwiZXhwIjoxNzcwODA5MzEzfQ.4h_vH4aDYQqGvRzqyhib8V1LKqS3wCec4-0aqjyl1zw";//Environment.GetEnvironmentVariable("TURUM_TOKEN");

        private static void Main(string[] args)
        {
            #region Setup daily file logging

            // ===== Setup daily file logging =====
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;   // bin/Debug eller bin/Release
            var logDir = Path.Combine(baseDir, "logs");

            Directory.CreateDirectory(logDir);

            // Daily file (append whole day)
            var logPath = Path.Combine(
                logDir,
                "TurumSync_" + DateTime.Now.ToString("yyyy-MM-dd") + ".log");

            var fileWriter = new StreamWriter(logPath, true) { AutoFlush = true };

            Console.SetOut(new TeeTextWriter(Console.Out, fileWriter));
            Console.SetError(new TeeTextWriter(Console.Error, fileWriter));

            Console.WriteLine("=================================================");
            Console.WriteLine("Turum Shopify Sync started: " + DateTime.Now);
            Console.WriteLine("Log file: " + logPath);
            Console.WriteLine("=================================================");

            #endregion

            // .NET Framework: async Main not available (unless newer compiler tricks)
            try
            {
                RunAsync().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
                Console.WriteLine(ex.Message);
                Environment.ExitCode = 1;
            }
        }

        private static async Task RunAsync()
        {
            if (string.IsNullOrWhiteSpace(ShopifyAdminToken))
                throw new Exception("Missing env var SHOPIFY_ADMIN_TOKEN");
            if (string.IsNullOrWhiteSpace(TurumToken))
                throw new Exception("Missing env var TURUM_TOKEN");

            var ct = CancellationToken.None;

            using (var turumHttp = new HttpClient())
            using (var shopifyHttp = CreateShopifyClient(ShopifyStoreDomain, ShopifyAdminToken, ShopifyApiVersion))
            {
                var locationId = await GetFirstShopifyLocationIdAsync(shopifyHttp, ct);
                Console.WriteLine("Using Shopify location ID: " + locationId);

                // Build SKU -> productId map (paged)
                var skuIndex = await BuildShopifySkuIndexAsync(shopifyHttp, ct);
                Console.WriteLine("Indexed SKUs from Shopify: " + skuIndex.Count);

                // Fetch Turum products
                var turumProducts = await FetchTurumProductsAsync(turumHttp, TurumToken, ct);
                Console.WriteLine("Fetched TURUM products: " + turumProducts.Count);

                int created = 0, updated = 0;

                int total = turumProducts.Count;
                int processed = 0;

                foreach (var p in turumProducts.AsEnumerable().Reverse())
                {
                    try
                    {
                        if (p == null || string.IsNullOrWhiteSpace(p.sku))
                            continue;

                        long productId;
                        bool mustCreateNewBecausePo = false;

                        // DECLARE OUTSIDE so we can reuse it later
                        dynamic existingProductDoc = null;

                        // Category detection (for tags + product_type)
                        var category = DetectTurumCategory(p);

                        // Check image URL
                        var includeImage = await IsValidImageUrlAsync(p.image, ct);

                        // Check if SKU already exists
                        if (skuIndex.TryGetValue(p.sku, out productId))
                        {
                            existingProductDoc = await GetShopifyProductAsync(shopifyHttp, productId, ct);

                            // NEW RULE: if tagged "PO" → force new product
                            if (HasTag(existingProductDoc, "PO"))
                                mustCreateNewBecausePo = true;
                        }

                        if (!skuIndex.ContainsKey(p.sku) || mustCreateNewBecausePo)
                        {
                            // ======================
                            // CREATE PRODUCT
                            // ======================
                            var createPayload = BuildShopifyCreateProductPayload(p, includeImage, category);

                            try
                            {
                                productId = await ShopifyPostAsync<long>(shopifyHttp, "products.json", createPayload, doc => (long)doc.product.id, ct);
                            }
                            catch (Exception ex)
                            {
                                // Shopify returns 422 with "Image URL is invalid"
                                if (ex.Message.IndexOf("Image URL is invalid", StringComparison.OrdinalIgnoreCase) >= 0)
                                {
                                    Console.WriteLine("[WARN] CREATE without image (invalid url). SKU " + p.sku + " url " + p.image);

                                    // retry without images
                                    var payloadNoImage = BuildShopifyCreateProductPayload(p, false, category);
                                    productId = await ShopifyPostAsync<long>(shopifyHttp, "products.json", payloadNoImage, d => (long)d.product.id, ct);
                                }
                                else
                                {
                                    throw;
                                }
                            }

                            // If category is Sneakers, and add to Sneakers collection
                            if (category == "Sneakers")
                            {
                                // Set category to Sneakers (via GraphSQL API)
                                await SetShopifyCategorySneakersAsync(shopifyHttp, productId, ct);

                                // Add to Sneakers collection
                                var sneakersCollectionId = await GetOrCreateCustomCollectionIdAsync(shopifyHttp, "Sneakers", ct);
                                await EnsureProductInCollectionAsync(shopifyHttp, productId, sneakersCollectionId, ct);
                            }

                            // Add to brand collection
                            var brandCollectionId = await GetOrCreateCustomCollectionIdAsync(shopifyHttp, p.brand, ct);
                            await EnsureProductInCollectionAsync(shopifyHttp, productId, brandCollectionId, ct);

                            skuIndex[p.sku] = productId;

                            created++;
                            Console.WriteLine("CREATED SKU " + p.sku + " -> product " + productId);
                        }
                        else
                        {
                            // ======================
                            // UPDATE PRODUCT
                            // ======================

                            // CLEANUP and merge tags
                            string mergedTags = TagsMergeAndCleanUp(existingProductDoc, p, category);

                            var needsProductUpdate = ProductUpdateNeeded(existingProductDoc, p.name, "TURUM B2B", category, mergedTags);
                            if (needsProductUpdate)
                            {
                                var updatePayload = BuildShopifyUpdateProductPayload(productId, p, mergedTags, category);

                                await ShopifyPutAsync<dynamic>(shopifyHttp, "products/" + productId + ".json", updatePayload, d => d, ct);

                                updated++;
                                Console.WriteLine("UPDATED SKU " + p.sku + " -> product " + productId);
                            }
                            else
                            {
                                //Console.WriteLine("SKIP product update (unchanged) SKU " + p.sku);
                            }

                            // NEW: update image
                            await ReplaceProductImagesAsync(shopifyHttp, productId, p.image, p.name, ct);

                            // Ensure category is Sneakers. (uncomment if necessary)
                            //await SetShopifyCategorySneakersAsync(shopifyHttp, productId, ct);
                        }
                        
                        // Always fetch product to get variants + inventory_item_id
                        var shopifyProduct = existingProductDoc ?? await GetShopifyProductAsync(shopifyHttp, productId, ct);

                        // Upsert variants (by size) + inventory set
                        var variantsCreated = await UpsertVariantsBySizeAsync(shopifyHttp, productId, shopifyProduct, p, ct);

                        // Refresh (if new variants were created)
                        if (variantsCreated)
                        {
                            shopifyProduct = await GetShopifyProductAsync(shopifyHttp, productId, ct);
                        }

                        // Reorder variants if we need to
                        if (VariantReorderNeeded(shopifyProduct))
                        {
                            Console.WriteLine("[INFO] Reordering variants needed. SKU " + p.sku);
                            await EnsureVariantPositionsBySizeAsync(shopifyHttp, productId, shopifyProduct, ct);

                            // Optional: refresh if you rely on correct positions later (usually not needed)
                            // shopifyProduct = await GetShopifyProductAsync(shopifyHttp, productId, ct);
                        }
                        else
                        {
                            Console.WriteLine("[INFO] SKIP reorder (already ordered). SKU " + p.sku);
                        }

                        // Inventory
                        await SetInventoryFromTurumAsync(shopifyHttp, locationId, shopifyProduct, p, ct);
                    }
                    finally
                    {
                        processed++;
                        var pct = (processed * 100) / total;

                        Console.WriteLine($"Processed {processed}/{total} ({pct}%)");
                    }
                }

                Console.WriteLine("Done. Created: " + created + ", Updated: " + updated);

                // Remove products from Shopify that are no longer in Turum
                // After main sync loop:
                Console.WriteLine();
                Console.WriteLine("[INFO] Starting cleanup of missing Turum products...");
                await ArchiveAndCleanupMissingTurumProductsAsync(shopifyHttp, turumProducts, skuIndex, ct);

            }
        }

        // ==========================
        // CLEANUP: Archive Shopify products missing in Turum feed
        // ==========================
        private static async Task ArchiveAndCleanupMissingTurumProductsAsync(HttpClient shopify, IList<TurumProduct> turumProducts, Dictionary<string, long> skuIndex, CancellationToken ct)
        {
            // 1) Build set of current Turum SKUs
            var turumSkus = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in turumProducts)
            {
                var sku = ((string)p.sku ?? "").Trim();
                if (sku.Length > 0) turumSkus.Add(sku);
            }

            Console.WriteLine("[INFO] Cleanup: Turum SKUs=" + turumSkus.Count + " Shopify SKUs indexed=" + skuIndex.Count);

            int checkedCount = 0;
            int archivedCount = 0;
            int keptCount = 0;
            int deletedVariantsTotal = 0;

            foreach (var kvp in skuIndex)
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
                if (!EqualsIgnoreCase((string)productDoc.product.vendor, "TURUM B2B"))
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

                    // Set vendor to Highstreet Heaven, now that it's no longer Turum
                    await ShopifyPutAsync<dynamic>(shopify, "products/" + productId + ".json",
                        new
                        {
                            product = new
                            {
                                id = productId,
                                vendor = "Highstreet Heaven"
                            }
                        },
                        d => d,
                        ct);

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
        private static async Task<List<TurumProduct>> FetchTurumProductsAsync(HttpClient http, string bearerToken, CancellationToken ct)
        {
            var req = new HttpRequestMessage(HttpMethod.Get, "https://api.b2b.turum.pl/v1/products_full_list_new");
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);

            using (var resp = await http.SendAsync(req, ct))
            {
                resp.EnsureSuccessStatusCode();
                var json = await resp.Content.ReadAsStringAsync();
                var dto = JsonConvert.DeserializeObject<TurumResponse>(json);

                if (dto == null || dto.data == null)
                    return new List<TurumProduct>();

                return dto.data;
            }
        }

        // Get a single TURUM product by SKU (not used in main flow, but can be useful for testing)
        // {{baseUrl}}/v1/product/:sku
        private static async Task<TurumProduct> FetchTurumProductBySkuAsync(HttpClient http, string bearerToken, string sku, CancellationToken ct)
        {
            var req = new HttpRequestMessage(HttpMethod.Get, "https://api.b2b.turum.pl/v1/product/" + WebUtility.UrlEncode(sku));
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
            using (var resp = await http.SendAsync(req, ct))
            {
                resp.EnsureSuccessStatusCode();
                var json = await resp.Content.ReadAsStringAsync();
                var dto = JsonConvert.DeserializeObject<TurumProduct>(json);
                return dto;
            }
        }

        // ==========================
        // SHOPIFY CLIENT
        // ==========================
        private static HttpClient CreateShopifyClient(string storeDomain, string adminToken, string apiVersion)
        {
            // .NET Framework network tuning (do once)
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            ServicePointManager.DefaultConnectionLimit = 50;
            ServicePointManager.Expect100Continue = false;

            var client = new HttpClient
            {
                BaseAddress = new Uri("https://" + storeDomain + "/admin/api/" + apiVersion + "/"),
                Timeout = TimeSpan.FromMinutes(5)
            };

            client.DefaultRequestHeaders.Add("X-Shopify-Access-Token", adminToken);
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            return client;
        }

        // ==========================
        // SHOPIFY: LOCATION
        // ==========================
        private static async Task<long> GetFirstShopifyLocationIdAsync(HttpClient shopify, CancellationToken ct)
        {
            dynamic doc = await ShopifyGetAsync<dynamic>(shopify, "locations.json", ct);
            if (doc.locations == null || doc.locations.Count == 0)
                throw new Exception("No Shopify locations found.");
            return (long)doc.locations[0].id;
        }

        // ==========================
        // SHOPIFY: BUILD SKU INDEX (sku -> productId) by paging all products
        // ==========================
        private static async Task<Dictionary<string, long>> BuildShopifySkuIndexAsync(HttpClient shopify, CancellationToken ct)
        {
            var map = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

            string nextUrl = "products.json?limit=250&fields=id,variants";
            while (!string.IsNullOrWhiteSpace(nextUrl))
            {
                using (var resp = await ShopifySendWithRetryAsync(shopify, new HttpRequestMessage(HttpMethod.Get, nextUrl), ct))
                {
                    resp.EnsureSuccessStatusCode();

                    var json = await resp.Content.ReadAsStringAsync();
                    dynamic doc = JsonConvert.DeserializeObject(json);

                    foreach (var p in doc.products)
                    {
                        long productId = (long)p.id;
                        foreach (var v in p.variants)
                        {
                            string sku = (string)(v.sku ?? "");
                            if (string.IsNullOrWhiteSpace(sku))
                                continue;

                            // Many variants share same SKU; they should point to same product id.
                            if (!map.ContainsKey(sku))
                                map.Add(sku, productId);
                        }
                    }

                    nextUrl = TryGetNextPageRelativeUrl(resp);
                }
            }

            return map;
        }

        private static string TryGetNextPageRelativeUrl(HttpResponseMessage resp)
        {
            IEnumerable<string> values;
            if (!resp.Headers.TryGetValues("Link", out values))
                return null;

            var link = values.FirstOrDefault();
            if (string.IsNullOrWhiteSpace(link))
                return null;

            foreach (var part in link.Split(','))
            {
                if (part.IndexOf("rel=\"next\"", StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                int start = part.IndexOf('<');
                int end = part.IndexOf('>');
                if (start < 0 || end <= start) continue;

                var url = part.Substring(start + 1, end - start - 1);

                // Convert absolute URL to relative to BaseAddress: /admin/api/{version}/...
                var marker = "/admin/api/";
                var idx = url.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                if (idx < 0) return url;

                var after = url.Substring(idx + marker.Length); // "{version}/products.json?...”
                var slash = after.IndexOf('/');
                if (slash < 0) return url;

                return after.Substring(slash + 1); // "products.json?...”
            }

            return null;
        }

        // ==========================
        // SHOPIFY: GET PRODUCT (need variants + inventory_item_id)
        // ==========================
        private static async Task<dynamic> GetShopifyProductAsync(HttpClient shopify, long productId, CancellationToken ct)
        {
            var url =
                "products/" + productId +
                ".json?fields=id,title,vendor,product_type,tags,variants,options";

            return await ShopifyGetAsync<dynamic>(shopify, url, ct);
        }

        // ==========================
        // SHOPIFY: UPSERT VARIANTS by size (option1)
        // ==========================
        private static async Task<bool> UpsertVariantsBySizeAsync(HttpClient shopify, long productId, dynamic shopifyProductDoc, TurumProduct turum, CancellationToken ct)
        {
            bool createdAny = false;

            // existingBySize: option1 -> variant object
            var existingBySize = new Dictionary<string, dynamic>(StringComparer.OrdinalIgnoreCase);
            bool blockNewVariantCreation = false;

            var seenSizes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var sv in shopifyProductDoc.product.variants)
            {
                string size = (string)(sv.option1 ?? sv.title ?? "");
                if (!string.IsNullOrWhiteSpace(size))
                    existingBySize[size.Trim()] = sv;
            }

            foreach (var tv in turum.variants)
            {
                var size = (tv.eu_size ?? tv.size);
                if (string.IsNullOrWhiteSpace(size))
                    continue;

                size = size.Trim();

                if (!seenSizes.Add(size))
                    continue; // skip duplicate size from TURUM

                var raw = ConvertToDkk(tv.price) * MomsRate + Profit;
                var dkk = RoundRetailPrice(raw);

                dynamic existing;
                if (existingBySize.TryGetValue(size, out existing))
                {
                    var oldPrice = ParseShopifyMoney(existing.price); // fx 845.00m
                    var newPrice = dkk;                               // decimal fra RoundRetailPrice

                    if (oldPrice == newPrice)
                    {
                        // No need to update this variant price
                        //Console.WriteLine(
                        //            "SKIP variant update (unchanged price) SKU " + turum.sku +
                        //            " size " + size +
                        //            " price " + newPrice.ToString("0", CultureInfo.InvariantCulture));
                        continue;
                    }

                    // Update variant: price, sku (same), barcode
                    long variantId = (long)existing.id;

                    var payload = new
                    {
                        variant = new
                        {
                            id = variantId,
                            price = dkk.ToString("0", CultureInfo.InvariantCulture),
                            sku = turum.sku,
                            //barcode = tv.ean // ean is always null in Turum response
                        }
                    };

                    await ShopifyPutAsync(shopify, "variants/" + variantId + ".json", payload, ct);
                }
                else
                {
                    if (blockNewVariantCreation)
                        continue;

                    // Create missing variant
                    var payload = new
                    {
                        variant = new
                        {
                            option1 = size,
                            price = dkk.ToString("0", CultureInfo.InvariantCulture),
                            sku = turum.sku, // SAME SKU on all variants (your requirement)
                            inventory_management = "shopify",
                            inventory_policy = "deny",
                            taxable = true,
                            requires_shipping = true,
                            barcode = tv.ean
                        }
                    };

                    try
                    {
                        await ShopifyPostAsync<dynamic>(shopify, "products/" + productId + "/variants.json", payload, doc => doc, ct);

                        createdAny = true;
                    }
                    catch (Exception ex)
                    {
                        // If product uses connected options (option values linked to metafields), Shopify blocks creating new option values
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

            // Optional: for Shopify variants not present in Turum -> inventory set to 0 will happen in SetInventoryFromTurumAsync

            return createdAny;
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
        private static async Task SetInventoryFromTurumAsync(HttpClient shopify, long locationId, dynamic shopifyProductDoc, TurumProduct turum, CancellationToken ct)
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
                string size = (string)(sv.option1 ?? sv.title ?? "");
                if (string.IsNullOrWhiteSpace(size))
                    continue;

                size = size.Trim();

                int available;
                if (!stockBySize.TryGetValue(size, out available))
                {
                    //available = 0;
                    // If size not in TURUM, do NOT touch Shopify inventory
                    continue;
                }

                long inventoryItemId = (long)sv.inventory_item_id;

                int currentQty = 0;
                try
                {
                    // Shopify returns inventory_quantity on variant in many product responses
                    currentQty = (int)sv.inventory_quantity;
                }
                catch
                {
                    // if missing, keep 0 and proceed with update
                }

                if (currentQty == available)
                {
                    // skip inventory update
                    //Console.WriteLine("SKIP inventory update...");
                    continue;
                }

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

                    var raw = ConvertToDkk(v.price) * MomsRate + Profit;
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
                    vendor = "TURUM B2B", // set the vendor to TURUM
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
                    vendor = "TURUM B2B",
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
            //return Math.Round(eur * EurToDkkRate, 2, MidpointRounding.AwayFromZero);
            return eur * EurToDkkRate; // NO rounding here
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
        // Shopify collections + collects
        // ==============================
        private static async Task<long> GetOrCreateCustomCollectionIdAsync(HttpClient shopify, string title, CancellationToken ct)
        {
            // Try find by title (paginate defensively)
            string nextUrl = "custom_collections.json?limit=250&fields=id,title";
            while (!string.IsNullOrWhiteSpace(nextUrl))
            {
                using (var resp = await ShopifySendWithRetryAsync(shopify, new HttpRequestMessage(HttpMethod.Get, nextUrl), ct))
                {
                    resp.EnsureSuccessStatusCode();
                    var json = await resp.Content.ReadAsStringAsync();
                    dynamic doc = JsonConvert.DeserializeObject(json);

                    foreach (var c in doc.custom_collections)
                    {
                        string t = (string)c.title;
                        if (string.Equals(t, title, StringComparison.OrdinalIgnoreCase))
                            return (long)c.id;
                    }

                    nextUrl = TryGetNextPageRelativeUrl(resp);
                }
            }

            // Not found -> create
            var payload = new
            {
                custom_collection = new
                {
                    title = title,
                    published = true
                }
            };

            return await ShopifyPostAsync<long>(
                shopify,
                "custom_collections.json",
                payload,
                doc => (long)doc.custom_collection.id,
                ct);
        }

        //private static async Task EnsureProductInCollectionAsync(HttpClient shopify, long productId, long collectionId, CancellationToken ct)
        //{
        //    // Create collect (idempotency not guaranteed; if duplicates happen, Shopify may return 422)
        //    var payload = new
        //    {
        //        collect = new
        //        {
        //            product_id = productId,
        //            collection_id = collectionId
        //        }
        //    };

        //    try
        //    {
        //        await ShopifyPostAsync<dynamic>(shopify, "collects.json", payload, doc => doc, ct);
        //    }
        //    catch (HttpRequestException)
        //    {
        //        // If you want, you can inspect response body for 422 and ignore.
        //        // Keeping it minimal: ignore failures to prevent stopping the sync.
        //    }
        //}

        private static async Task EnsureProductInCollectionAsync(HttpClient shopify, long productId, long collectionId, CancellationToken ct)
        {
            var payload = new
            {
                collect = new
                {
                    product_id = productId,
                    collection_id = collectionId
                }
            };

            var req = new HttpRequestMessage(HttpMethod.Post, "collects.json");
            req.Content = new StringContent(JsonConvert.SerializeObject(payload), Encoding.UTF8, "application/json");

            using (var resp = await ShopifySendWithRetryAsync(shopify, req, ct))
            {
                if (resp.IsSuccessStatusCode) return;

                var body = await resp.Content.ReadAsStringAsync();

                // Shopify returns 422 if the collect already exists – ignore that
                if ((int)resp.StatusCode == 422 &&
                    body.IndexOf("already", StringComparison.OrdinalIgnoreCase) >= 0)
                    return;

                throw new Exception("Collect create failed: " + (int)resp.StatusCode + " " + resp.ReasonPhrase + "\n" + body);
            }
        }

        // =========================
        // Shopify: If product exists AND has tag "PO" don’t update; create new instead
        // ========================
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
        //private static async Task ReplaceProductImagesAsync(HttpClient shopify, long productId, string imageUrl, string alt, CancellationToken ct)
        //{
        //    // Get existing images
        //    dynamic imagesDoc = await ShopifyGetAsync<dynamic>(shopify, "products/" + productId + "/images.json?fields=id", ct);

        //    if (imagesDoc.images != null)
        //    {
        //        foreach (var img in imagesDoc.images)
        //        {
        //            long imageId = (long)img.id;
        //            await ShopifyDeleteAsync(shopify, "products/" + productId + "/images/" + imageId + ".json", ct);
        //        }
        //    }

        //    // Add new image
        //    var payload = new
        //    {
        //        image = new
        //        {
        //            src = imageUrl,
        //            alt = alt
        //        }
        //    };

        //    await ShopifyPostAsync<dynamic>(shopify, "products/" + productId + "/images.json", payload, doc => doc, ct);
        //}

        private static async Task ReplaceProductImagesAsync(HttpClient shopify, long productId, string imageUrl, string alt, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(imageUrl))
                return;

            imageUrl = imageUrl.Trim();

            var ok = await IsValidImageUrlAsync(imageUrl, ct);
            if (!ok)
            {
                Console.WriteLine("SKIP image update: " + imageUrl);
                return;
            }

            dynamic imagesDoc = await ShopifyGetAsync<dynamic>(shopify, "products/" + productId + "/images.json?fields=id,src", ct);

            // no images - just create
            if (imagesDoc.images == null || imagesDoc.images.Count == 0)
            {
                try
                {
                    await ShopifyPostAsync<dynamic>(shopify, "products/" + productId + "/images.json", new { image = new { src = imageUrl, alt = alt } }, d => d, ct);
                }
                catch (Exception ex)
                {
                    if (ex.Message.IndexOf("Image URL is invalid", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        Console.WriteLine("[WARN] SKIP image update (Shopify rejects url) product " + productId + " url " + imageUrl);
                        return;
                    }
                    throw;
                }

                return;
            }

            long firstId = (long)imagesDoc.images[0].id;
            string currentSrc = (string)imagesDoc.images[0].src;

            // already correct - skip
            if (string.Equals(currentSrc, imageUrl, StringComparison.OrdinalIgnoreCase))
                return;

            // ONLY UPDATE FIRST IMAGE (1 request)
            try
            {
                await ShopifyPutAsync<dynamic>(shopify, "products/" + productId + "/images/" + firstId + ".json", new { image = new { id = firstId, src = imageUrl, alt = alt } }, d => d, ct);
            }
            catch (Exception ex)
            {
                if (ex.Message.IndexOf("Image URL is invalid", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    Console.WriteLine("[WARN] SKIP image update (Shopify rejects url) product " + productId + " url " + imageUrl);
                    return;
                }
                throw;
            }
        }

        private static async Task<bool> IsValidImageUrlAsync(string url, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(url))
                return false;

            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
                return false;

            if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
                return false;

            // Try HEAD first (cheap)
            try
            {
                using (var head = new HttpRequestMessage(HttpMethod.Head, uri))
                using (var resp = await _imageCheckClient.SendAsync(head, HttpCompletionOption.ResponseHeadersRead, ct))
                {
                    if (resp.IsSuccessStatusCode)
                    {
                        var ctHeader = resp.Content.Headers.ContentType?.MediaType;
                        if (!string.IsNullOrWhiteSpace(ctHeader) && ctHeader.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                            return true;
                    }
                }
            }
            catch
            {
                // ignore and try GET below
            }

            // Fallback: GET headers only
            try
            {
                using (var get = new HttpRequestMessage(HttpMethod.Get, uri))
                using (var resp = await _imageCheckClient.SendAsync(get, HttpCompletionOption.ResponseHeadersRead, ct))
                {
                    if (!resp.IsSuccessStatusCode)
                        return false;

                    var ctHeader = resp.Content.Headers.ContentType?.MediaType;
                    return !string.IsNullOrWhiteSpace(ctHeader) &&
                           ctHeader.StartsWith("image/", StringComparison.OrdinalIgnoreCase);
                }
            }
            catch
            {
                return false;
            }
        }

        private static async Task ShopifyDeleteAsync(HttpClient shopify, string relativeUrl, CancellationToken ct)
        {
            var req = new HttpRequestMessage(HttpMethod.Delete, relativeUrl);

            using (var resp = await ShopifySendWithRetryAsync(shopify, req, ct))
            {
                resp.EnsureSuccessStatusCode();
            }
        }

        // =========================
        // Shopify: Set product category to Sneakers, using GraphQL API (becouse it is not possible vi REST API)
        // =======================
        private static async Task SetShopifyCategorySneakersAsync(HttpClient shopifyHttp, long productId, CancellationToken ct)
        {
            // Convert REST numeric ID -> GraphQL GID
            var productGid = "gid://shopify/Product/" + productId;

            var query = @"
                        mutation($input: ProductInput!) {
                          productUpdate(input: $input) {
                            product { id }
                            userErrors { field message }
                          }
                        }";

            var payload = new
            {
                query = query,
                variables = new
                {
                    input = new
                    {
                        id = productGid,
                        category = ShopifySneakersCategoryId
                    }
                }
            };

            var req = new HttpRequestMessage(HttpMethod.Post, "graphql.json");
            req.Content = new StringContent(JsonConvert.SerializeObject(payload), Encoding.UTF8, "application/json");

            using (var resp = await ShopifySendWithRetryAsync(shopifyHttp, req, ct))
            {
                resp.EnsureSuccessStatusCode();

                var json = await resp.Content.ReadAsStringAsync();
                dynamic doc = JsonConvert.DeserializeObject(json);

                // Optional: if you want to fail fast on category errors
                if (doc.data.productUpdate.userErrors != null && doc.data.productUpdate.userErrors.Count > 0)
                {
                    var msg = (string)doc.data.productUpdate.userErrors[0].message;
                    throw new Exception("Category update failed: " + msg);
                }
            }
        }

        //private static string DetectTurumCategory(dynamic p)
        //{
        //    var name = ((string)p.name ?? "").ToLowerInvariant();

        //    // apparel keywords (expand over time)

        //    // Bags
        //    if (Regex.IsMatch(name, @"\b(bag|shoulder bag|crossbody|tote|duffel|backpack)\b", RegexOptions.IgnoreCase)) return "Bag";

        //    // Coat before Jacket (more specific & heavier)
        //    if (Regex.IsMatch(name, @"\b(coat|trench|overcoat|wool coat|pea coat|parka)\b", RegexOptions.IgnoreCase)) return "Coat";

        //    // Jackets (outerwear) — BEFORE hoodie & sweatshirt
        //    if (Regex.IsMatch(name, @"\b(jacket|bomber|coach|windbreaker|track jacket|varsity|denim jacket)\b", RegexOptions.IgnoreCase)) return "Jacket";

        //    // Socks FIRST (so "pants + socks pack" doesn’t become Pants)
        //    if (Regex.IsMatch(name, @"\b(sock|socks)\b", RegexOptions.IgnoreCase)) return "Socks";

        //    // Pants (incl. jeans/joggers etc.)
        //    if (Regex.IsMatch(name, @"\b(pants|trousers|jeans|chino(s)?|cargo|jogger(s)?|sweatpants|track pants)\b", RegexOptions.IgnoreCase)) return "Pants";

        //    // Hoodie (more specific Sweatshirt)
        //    if (Regex.IsMatch(name, @"\b(hoodie|hooded|zip hoodie|pullover hoodie)\b", RegexOptions.IgnoreCase)) return "Hoodie";

        //    // Sweatshirt (no hood)
        //    if (Regex.IsMatch(name, @"\b(crewneck|sweatshirt)\b", RegexOptions.IgnoreCase)) return "Sweatshirt";

        //    // T-shirt
        //    if (Regex.IsMatch(name, @"\b(t-?shirt|tshirt|tee|graphic tee|short sleeve tee|long sleeve tee)\b", RegexOptions.IgnoreCase)) return "T-shirt";


        //    // sizes often indicate apparel (S/M/L/XL etc.)
        //    if (p.variants != null)
        //    {
        //        foreach (var v in p.variants)
        //        {
        //            var size = ((string)v.size ?? "").Trim().ToUpperInvariant();
        //            if (size == "XS" || size == "S" || size == "M" || size == "L" || size == "XL" || size == "XXL")
        //                return "Apparel";
        //        }
        //    }

        //    // default
        //    return "Sneakers";
        //}

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
        private static async Task<T> ShopifyGetAsync<T>(HttpClient shopify, string relativeUrl, CancellationToken ct)
        {
            using (var resp = await ShopifySendWithRetryAsync(shopify, new HttpRequestMessage(HttpMethod.Get, relativeUrl), ct))
            {
                resp.EnsureSuccessStatusCode();
                var json = await resp.Content.ReadAsStringAsync();
                return JsonConvert.DeserializeObject<T>(json);
            }
        }

        private static async Task ShopifyPutAsync(HttpClient shopify, string relativeUrl, object payload, CancellationToken ct)
        {
            var req = new HttpRequestMessage(HttpMethod.Put, relativeUrl);
            req.Content = new StringContent(JsonConvert.SerializeObject(payload), Encoding.UTF8, "application/json");

            using (var resp = await ShopifySendWithRetryAsync(shopify, req, ct))
            {
                if (!resp.IsSuccessStatusCode)
                {
                    var body = await resp.Content.ReadAsStringAsync();
                    throw new Exception("Shopify PUT failed: " + (int)resp.StatusCode + " " + resp.ReasonPhrase + "\n" + body);
                }
            }
        }

        private static async Task<T> ShopifyPutAsync<T>(HttpClient shopify, string relativeUrl, object payload, Func<dynamic, T> map, CancellationToken ct)
        {
            var req = new HttpRequestMessage(HttpMethod.Put, relativeUrl);
            req.Content = new StringContent(JsonConvert.SerializeObject(payload), Encoding.UTF8, "application/json");

            using (var resp = await ShopifySendWithRetryAsync(shopify, req, ct))
            {
                if (!resp.IsSuccessStatusCode)
                {
                    var bodyErr = await resp.Content.ReadAsStringAsync();
                    throw new Exception("Shopify PUT failed: " + (int)resp.StatusCode + " " + resp.ReasonPhrase + "\n" + bodyErr);
                }

                var body = await resp.Content.ReadAsStringAsync();
                if (string.IsNullOrWhiteSpace(body))
                    return default(T);

                dynamic doc = JsonConvert.DeserializeObject(body);
                return map(doc);
            }
        }

        private static async Task<TOut> ShopifyPostAsync<TOut>(
            HttpClient shopify,
            string relativeUrl,
            object payload,
            Func<dynamic, TOut> selector,
            CancellationToken ct)
        {
            var req = new HttpRequestMessage(HttpMethod.Post, relativeUrl);
            req.Content = new StringContent(JsonConvert.SerializeObject(payload), Encoding.UTF8, "application/json");

            using (var resp = await ShopifySendWithRetryAsync(shopify, req, ct))
            {
                if (!resp.IsSuccessStatusCode)
                {
                    var body = await resp.Content.ReadAsStringAsync();
                    throw new Exception("Shopify POST failed: " + (int)resp.StatusCode + " " + resp.ReasonPhrase + "\n" + body);
                }

                resp.EnsureSuccessStatusCode();
                var json = await resp.Content.ReadAsStringAsync();
                dynamic doc = JsonConvert.DeserializeObject(json);
                return selector(doc);
            }
        }

        //private static async Task<HttpResponseMessage> ShopifySendWithRetryAsync(HttpClient shopify, HttpRequestMessage req, CancellationToken ct)
        //{
        //    // Important: HttpRequestMessage can’t be resent. We clone it each attempt.
        //    for (int attempt = 1; attempt <= 6; attempt++)
        //    {
        //        var cloned = CloneRequest(req);

        //        var resp = await shopify.SendAsync(cloned, ct);

        //        if (resp.StatusCode == (HttpStatusCode)429 || (int)resp.StatusCode >= 500)
        //        {
        //            var delay = GetRetryDelay(resp, attempt);
        //            resp.Dispose();
        //            await Task.Delay(delay, ct);
        //            continue;
        //        }

        //        return resp;
        //    }

        //    // Last attempt (no swallowing)
        //    return await shopify.SendAsync(CloneRequest(req), ct);
        //}

        private static async Task<HttpResponseMessage> ShopifySendWithRetryAsync(HttpClient shopify, HttpRequestMessage req, CancellationToken ct)
        {
            for (int attempt = 1; attempt <= 8; attempt++)
            {
                HttpResponseMessage resp = null;

                try
                {
                    resp = await shopify.SendAsync(CloneRequest(req), ct);

                    // Throttle on Shopify REST call limit header
                    var limit = TryGetCallLimit(resp);
                    if (limit != null)
                    {
                        var used = limit[0];
                        var max = limit[1];

                        // If we’re close to the ceiling, wait a bit to avoid hard throttling / connection drops
                        if (max > 0 && used >= (int)(max * 0.85))
                            await Task.Delay(1200, ct);
                    }

                    // Retry on 429 or 5xx
                    if (resp.StatusCode == (HttpStatusCode)429 || (int)resp.StatusCode >= 500)
                    {
                        var delay = GetRetryDelay(resp, attempt);
                        resp.Dispose();
                        await Task.Delay(delay, ct);
                        continue;
                    }

                    return resp;
                }
                catch (HttpRequestException)
                {
                    // Covers "underlying connection was closed" and similar transient network errors
                    var delay = TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, attempt - 1)));
                    if (resp != null) resp.Dispose();
                    await Task.Delay(delay, ct);
                    continue;
                }
                catch (WebException)
                {
                    var delay = TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, attempt - 1)));
                    if (resp != null) resp.Dispose();
                    await Task.Delay(delay, ct);
                    continue;
                }
            }

            // final attempt, bubble errors
            return await shopify.SendAsync(CloneRequest(req), ct);
        }

        private static int[] TryGetCallLimit(HttpResponseMessage resp)
        {
            IEnumerable<string> values;
            if (!resp.Headers.TryGetValues("X-Shopify-Shop-Api-Call-Limit", out values))
                return null;

            var v = values.FirstOrDefault(); // e.g. "32/40"
            if (string.IsNullOrWhiteSpace(v)) return null;

            var parts = v.Split('/');
            int used, max;
            if (parts.Length == 2 && int.TryParse(parts[0], out used) && int.TryParse(parts[1], out max))
                return new[] { used, max };

            return null;
        }

        private static TimeSpan GetRetryDelay(HttpResponseMessage resp, int attempt)
        {
            IEnumerable<string> values;
            if (resp.Headers.TryGetValues("Retry-After", out values))
            {
                var v = values.FirstOrDefault();
                int seconds;
                if (int.TryParse(v, out seconds))
                    return TimeSpan.FromSeconds(seconds);
            }

            // exponential backoff 1s,2s,4s,8s...
            var s = Math.Min(32, Math.Pow(2, attempt - 1));
            return TimeSpan.FromSeconds(s);
        }

        private static HttpRequestMessage CloneRequest(HttpRequestMessage req)
        {
            var clone = new HttpRequestMessage(req.Method, req.RequestUri);

            foreach (var h in req.Headers)
                clone.Headers.TryAddWithoutValidation(h.Key, h.Value);

            if (req.Content != null)
            {
                var content = req.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                var mediaType = req.Content.Headers.ContentType != null
                    ? req.Content.Headers.ContentType.MediaType
                    : "application/json";
                clone.Content = new StringContent(content, Encoding.UTF8, mediaType);
            }

            return clone;
        }

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
    }

    // ==========================
    // TURUM DTOs
    // ==========================
    public sealed class TurumResponse
    {
        public List<TurumProduct> data { get; set; }
    }

    public sealed class TurumProduct
    {
        public string sku { get; set; }
        public string image { get; set; }
        public string name { get; set; }
        public decimal price { get; set; }
        public string brand { get; set; }
        public List<TurumVariant> variants { get; set; }
    }

    public sealed class TurumVariant
    {
        public string variant_id { get; set; }
        public string size { get; set; }
        public int stock { get; set; }
        public bool has_more { get; set; }
        public decimal price { get; set; }
        public string eu_size { get; set; }
        public string ean { get; set; }
    }
}