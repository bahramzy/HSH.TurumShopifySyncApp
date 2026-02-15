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
        private static readonly string TurumToken = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiJoaWdoc3RyZWV0aGVhdmVuMjRAZ21haWwuY29tIiwicm9sZSI6InR1cnVtX2N1c3RvbWVyIiwiZXhwIjoxNzcxMTUyOTc1fQ.12WsPrft2n7IenuAK8kV7y-YoTK553bBTuIKcxR39XI";//Environment.GetEnvironmentVariable("TURUM_TOKEN");

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

            // Start high-resolution timer
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            try
            {

                // Run main flow (synchronously waiting)
                RunAsync().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
                Console.WriteLine(ex.Message);
                Environment.ExitCode = 1;
            }
            finally
            {
                // Always stop and print elapsed time, even after exceptions
                stopwatch.Stop();

                // Always stop and print elapsed time, even after exceptions
                stopwatch.Stop();

                Console.WriteLine();
                Console.WriteLine("=================================================");
                Console.WriteLine("Turum Shopify Sync finished: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                Console.WriteLine("Elapsed time: " + stopwatch.Elapsed.ToString(@"hh\:mm\:ss\.fff"));
                Console.WriteLine("Elapsed milliseconds: " + stopwatch.ElapsedMilliseconds + " ms");
                Console.WriteLine("=================================================");
            }
        }

        private static async Task RunAsync()
        {
            var ct = CancellationToken.None;

            HttpClient shopifyHttp = null;
            HttpClient turumHttp = null;

            // Prefer client id / secret from env variables (auto-refresh)
            /*
             * Set these evinvironment variables in your system or development environment to enable auto-refreshing tokens:
             * Open an elevated or normal PowerShell (for current user), run the commands and restart VS or whatever you use to run the code:
             * setx SHOPIFY_CLIENT_ID "your-client-id"
             * setx SHOPIFY_CLIENT_SECRET "your-client-secret"
             */
            var clientId = Environment.GetEnvironmentVariable("SHOPIFY_CLIENT_ID");
            var clientSecret = Environment.GetEnvironmentVariable("SHOPIFY_CLIENT_SECRET");
            if (!string.IsNullOrWhiteSpace(clientId) && !string.IsNullOrWhiteSpace(clientSecret))
            {
                var tokenProvider = new ShopifyTokenProvider(ShopifyStoreDomain, clientId, clientSecret);
                shopifyHttp = CreateShopifyClient(ShopifyStoreDomain, tokenProvider, ShopifyApiVersion);
                Console.WriteLine("Shopify client: using token provider (auto-refresh).");
            }
            else
            {
                // Fallback to static admin token (unchanged behavior)

                if (string.IsNullOrWhiteSpace(ShopifyAdminToken))
                    throw new Exception("Missing env var SHOPIFY_ADMIN_TOKEN");
                if (string.IsNullOrWhiteSpace(TurumToken))
                    throw new Exception("Missing env var TURUM_TOKEN");

                shopifyHttp = CreateShopifyClient(ShopifyStoreDomain, ShopifyAdminToken, ShopifyApiVersion);
                Console.WriteLine("Shopify client: using static admin token (no auto-refresh).");
            }

            // Turum token auto-refresh: prefer TURUM_USERNAME & TURUM_PASSWORD env vars
            var turumUser = Environment.GetEnvironmentVariable("TURUM_USERNAME");
            var turumPass = Environment.GetEnvironmentVariable("TURUM_PASSWORD");
            if (!string.IsNullOrWhiteSpace(turumUser) && !string.IsNullOrWhiteSpace(turumPass))
            {
                var turumProvider = new TurumTokenProvider(turumUser, turumPass);
                turumHttp = CreateTurumClient(turumProvider);
                Console.WriteLine("Turum client: using token provider (auto-refresh).");
            }
            else
            {
                // fallback to static token
                if (string.IsNullOrWhiteSpace(TurumToken))
                    throw new Exception("Missing env var TURUM_TOKEN or TURUM_USERNAME/TURUM_PASSWORD");
                turumHttp = CreateTurumClient(TurumToken);
                Console.WriteLine("Turum client: using static token (no auto-refresh).");
            }

            using (turumHttp)
            using (shopifyHttp) // created above with possible token provider
            {
                var locationId = await GetFirstShopifyLocationIdAsync(shopifyHttp, ct);
                Console.WriteLine("Using Shopify location ID: " + locationId);

                // Build SKU -> productId map (paged)
                var skuIndexes = await BuildShopifySkuIndexesAsync(shopifyHttp, ct);
                var activeSkuIndex = skuIndexes.Active;
                var archivedSkuIndex = skuIndexes.Archived;
                Console.WriteLine("Indexed SKUs from Shopify: Active=" + activeSkuIndex.Count + " Archived=" + archivedSkuIndex.Count);

                // Fetch Turum products
                var turumProducts = await FetchTurumProductsAsync(turumHttp, ct);
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
                        if (activeSkuIndex.TryGetValue(p.sku, out productId))
                        {
                            //existingProductDoc = await GetShopifyProductAsync(shopifyHttp, productId, ct);
                            existingProductDoc = await GetShopifyProductGraphQlAsync(shopifyHttp, productId, ct);

                            // NEW RULE: if tagged "PO" → force new product
                            if (HasTag(existingProductDoc, "PO"))
                                mustCreateNewBecausePo = true;
                        }
                        else if (archivedSkuIndex.TryGetValue(p.sku, out productId))
                        {
                            // Unarchive only if the image is valid. Skip otherwise.
                            if (!includeImage)
                            {
                                Console.WriteLine("[WARN] SKU " + p.sku + " exists but archived, and image URL is invalid. Keep it archived and skip. SKU " + p.sku + " url " + p.image);
                                continue;
                            }

                            Console.WriteLine("[INFO] SKU " + p.sku + " exists but archived. Unarchive and treat as update.");

                            // Unarchive the product first
                            var unarchivePayload = new
                            {
                                product = new
                                {
                                    id = productId,
                                    status = "active"
                                }
                            };

                            await ShopifyPutAsync<dynamic>(shopifyHttp, "products/" + productId + ".json", unarchivePayload, d => d, ct);

                            // Move it from archived index to active index
                            archivedSkuIndex.Remove(p.sku);
                            activeSkuIndex[p.sku] = productId;

                            // Continue with update flow (fetch product from shopify)
                            //existingProductDoc = await GetShopifyProductAsync(shopifyHttp, productId, ct);
                            existingProductDoc = await GetShopifyProductGraphQlAsync(shopifyHttp, productId, ct);
                        }

                        if (!activeSkuIndex.ContainsKey(p.sku) || mustCreateNewBecausePo)
                        {
                            // ======================
                            // CREATE PRODUCT
                            // ======================

                            // Skip if the image is invalid
                            if (!includeImage)
                            {
                                Console.WriteLine("[WARN] Invalid image URL, Skip product creation. SKU " + p.sku + " url " + p.image);
                                continue;
                            }

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

                            activeSkuIndex[p.sku] = productId;

                            created++;
                            Console.WriteLine("CREATED SKU " + p.sku + " -> product " + productId);
                        }
                        else
                        {
                            // ======================
                            // UPDATE PRODUCT
                            // ======================

                            // Update image
                            await ReplaceProductImagesAsync(shopifyHttp, productId, p.image, p.name, ct);

                            // CLEANUP and merge tags
                            string mergedTags = TagsMergeAndCleanUp(existingProductDoc, p, category);

                            var needsProductUpdate = ProductUpdateNeeded(existingProductDoc, p.name, p.brand, category, mergedTags);
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

                            // Ensure category is Sneakers. (uncomment if necessary)
                            //await SetShopifyCategorySneakersAsync(shopifyHttp, productId, ct);
                        }
                        
                        // Always fetch product to get variants + inventory_item_id
                        //var shopifyProduct = existingProductDoc ?? await GetShopifyProductAsync(shopifyHttp, productId, ct);
                        var shopifyProduct = existingProductDoc ?? await GetShopifyProductGraphQlAsync(shopifyHttp, productId, ct);

                        // Upsert variants (by size) + inventory set
                        var variantsCreated = await UpsertVariantsBySizeAsync(shopifyHttp, productId, shopifyProduct, p, ct);

                        // Refresh (if new variants were created)
                        if (variantsCreated)
                        {
                            //shopifyProduct = await GetShopifyProductAsync(shopifyHttp, productId, ct);
                            shopifyProduct = await GetShopifyProductGraphQlAsync(shopifyHttp, productId, ct);
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
                            //Console.WriteLine("[INFO] SKIP reorder (already ordered). SKU " + p.sku);
                        }

                        // Inventory
                        await SetInventoryFromTurumAsync(shopifyHttp, locationId, shopifyProduct, p, ct);
                    }
                    finally
                    {
                        processed++;
                        var pct = (processed * 100) / total;

                        // Print only every 10th time or on the final item
                        if (processed % 10 == 0 || processed == total)
                        {
                            Console.WriteLine($"Processed {processed}/{total} ({pct}%)");
                        }
                    }
                }

                Console.WriteLine("Done. Created: " + created + ", Updated: " + updated);

                // Remove products from Shopify that are no longer in Turum
                // After main sync loop:
                Console.WriteLine();
                Console.WriteLine("[INFO] Starting cleanup of missing Turum products...");
                await ArchiveAndCleanupMissingTurumProductsAsync(shopifyHttp, turumProducts, activeSkuIndex, ct);

            }
        }

        // ==========================
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
        private static async Task<List<TurumProduct>> FetchTurumProductsAsync(HttpClient http, CancellationToken ct)
        {
            var req = new HttpRequestMessage(HttpMethod.Get, "https://api.b2b.turum.pl/v1/products_full_list_new");
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

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

        private static HttpClient CreateTurumClient(TurumTokenProvider provider)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            ServicePointManager.DefaultConnectionLimit = 50;
            ServicePointManager.Expect100Continue = false;

            var handler = new TurumTokenRefreshHandler(provider)
            {
                InnerHandler = new HttpClientHandler
                {
                    AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
                }
            };

            var client = new HttpClient(handler)
            {
                BaseAddress = new Uri("https://api.b2b.turum.pl/v1/"),
                Timeout = TimeSpan.FromMinutes(5)
            };

            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            return client;
        }

        private static HttpClient CreateTurumClient(string bearerToken)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            ServicePointManager.DefaultConnectionLimit = 50;
            ServicePointManager.Expect100Continue = false;

            var client = new HttpClient
            {
                BaseAddress = new Uri("https://api.b2b.turum.pl/v1/"),
                Timeout = TimeSpan.FromMinutes(5)
            };

            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            if (!string.IsNullOrWhiteSpace(bearerToken))
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);

            return client;
        }

        // Get a single TURUM product by SKU (not used in main flow, but can be useful for testing)
        // {{baseUrl}}/v1/product/:sku
        private static async Task<TurumProduct> FetchTurumProductBySkuAsync(HttpClient http, string sku, CancellationToken ct)
        {
            var req = new HttpRequestMessage(HttpMethod.Get, "https://api.b2b.turum.pl/v1/product/" + WebUtility.UrlEncode(sku));
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            
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

        // --- Add overload for CreateShopifyClient that accepts the token provider ---
        private static HttpClient CreateShopifyClient(string storeDomain, ShopifyTokenProvider tokenProvider, string apiVersion)
        {
            // .NET Framework network tuning (do once)
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            ServicePointManager.DefaultConnectionLimit = 50;
            ServicePointManager.Expect100Continue = false;

            var handler = new TokenRefreshHandler(tokenProvider)
            {
                InnerHandler = new HttpClientHandler
                {
                    AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
                }
            };

            var client = new HttpClient(handler)
            {
                BaseAddress = new Uri("https://" + storeDomain + "/admin/api/" + apiVersion + "/"),
                Timeout = TimeSpan.FromMinutes(5)
            };

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

        private static async Task<ShopifySkuIndexes> BuildShopifySkuIndexesAsync(HttpClient shopify, CancellationToken ct)
        {
            var res = new ShopifySkuIndexes();

            // Pull ALL products, but only fields we need. We need status and variants.sku and id.
            // Note: products.json supports status=active|archived|draft|any
            // We'll fetch active+draft first, then archived.
            await FillIndexByStatusAsync(shopify, "active", res.Active, ct);
            //await FillIndexByStatusAsync(shopify, "draft", res.Active, ct);
            await FillIndexByStatusAsync(shopify, "archived", res.Archived, ct);

            return res;
        }

        private static async Task FillIndexByStatusAsync(
            HttpClient shopify,
            string status,
            Dictionary<string, long> index,
            CancellationToken ct)
        {
            string relativeUrl =
                "products.json?limit=250"
                + "&status=" + status
                + "&fields=id,variants";

            while (!string.IsNullOrEmpty(relativeUrl))
            {
                var req = new HttpRequestMessage(HttpMethod.Get, relativeUrl);

                using (var resp = await ShopifySendWithRetryAsync(shopify, req, ct))
                {
                    resp.EnsureSuccessStatusCode();

                    var json = await resp.Content.ReadAsStringAsync();
                    dynamic doc = JsonConvert.DeserializeObject(json);

                    foreach (var p in doc.products)
                    {
                        long productId = ToLong(p.id);

                        if (p.variants == null)
                            continue;

                        foreach (var v in p.variants)
                        {
                            var sku = ((string)v.sku ?? "").Trim();
                            if (sku.Length == 0)
                                continue;

                            if (!index.ContainsKey(sku))
                                index[sku] = productId;
                        }
                    }

                    relativeUrl = TryGetNextPageRelativeUrl(resp);
                }
            }
        }

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
            var existingSizes = new List<string>();
            bool blockNewVariantCreation = false;

            var seenSizes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Collect existing variants and sizes
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

                var raw = ConvertToDkk(tv.price) * MomsRate + Profit;
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
                        try
                        {
                            Console.WriteLine("[DEBUG] Variant create response: " + JsonConvert.SerializeObject(resp));
                        }
                        catch { /* ignore logging errors */ }

                        long createdVariantId = 0;
                        try { createdVariantId = ToLong(resp.variant.id); } catch { createdVariantId = 0; }

                        createdAny = true;

                        if (createdVariantId > 0)
                        {
                            createdVariants.Add((size, createdVariantId));
                            Console.WriteLine("CREATED variant SKU " + turum.sku + " size " + size + " productId " + productId + " variantId " + createdVariantId);

                            // Verify variant exists server-side (same store/token)
                            try
                            {
                                dynamic vdoc = await ShopifyGetAsync<dynamic>(shopify, "variants/" + createdVariantId + ".json", ct);
                                Console.WriteLine("[DEBUG] Fetched created variant " + createdVariantId + ": " + JsonConvert.SerializeObject(vdoc));
                            }
                            catch (Exception ex)
                            {
                                // 404 or other errors — surface for debugging
                                Console.WriteLine("[WARN] Fetching created variant " + createdVariantId + " failed: " + ex.Message);
                                Console.WriteLine("[WARN] You should inspect the POST response above and ensure Postman uses the same store/token and API version.");
                            }

                            // Fetch product to inspect options & variant list
                            try
                            {
                                dynamic pdoc = await ShopifyGetAsync<dynamic>(shopify, "products/" + productId + ".json?fields=variants,options", ct);
                                Console.WriteLine("[DEBUG] Product variants after create: " + JsonConvert.SerializeObject(pdoc.product.variants));
                                Console.WriteLine("[DEBUG] Product options after create: " + JsonConvert.SerializeObject(pdoc.product.options));
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine("[WARN] Failed to fetch product after variant create: " + ex.Message);
                            }
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
            /*
             PSEUDOCODE / PLAN (detailed):
             1. If imageUrl is null/empty/whitespace:
                - Log that there is no image URL.
                - Attempt to archive the Shopify product by sending a PUT to "products/{id}.json" with status = "archived".
                - If archiving fails, log a warning but do not throw (non-fatal).
                - Return.
             2. Trim the imageUrl.
             3. Validate the image URL via IsValidImageUrlAsync.
                - If validation fails:
                  - Log that the image is invalid.
                  - Attempt to archive the Shopify product as in step 1.
                  - Return.
             4. If validation succeeds:
                - Fetch current product images (id, src).
                - If there are no images:
                  - Try to create the image via POST to "products/{id}/images.json".
                  - Handle Shopify rejecting the image by checking exception message for "Image URL is invalid" and log+return.
                - If there is at least one image:
                  - Compare first image src to new imageUrl; if equal, return (no-op).
                  - Otherwise update the first image via PUT to "products/{id}/images/{imageId}.json".
                  - On Shopify image rejection, log and return.
             Notes:
             - Archiving should not throw the whole sync; failures are logged and treated as non-fatal.
             - Use existing helper ShopifyPutAsync and ShopifyPostAsync/ShopifyGetAsync.
            */

            if (string.IsNullOrWhiteSpace(imageUrl))
            {
                Console.WriteLine("SKIP image update (no image url). Archiving product " + productId);
                try
                {
                    var archivePayload = new
                    {
                        product = new
                        {
                            id = productId,
                            status = "archived"
                        }
                    };

                    await ShopifyPutAsync<dynamic>(shopify, "products/" + productId + ".json", archivePayload, d => d, ct);
                    Console.WriteLine("[INFO] Archived product " + productId + " due to missing image.");
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[WARN] Failed to archive product " + productId + ": " + ex.Message);
                }

                return;
            }

            imageUrl = imageUrl.Trim();

            var ok = await IsValidImageUrlAsync(imageUrl, ct);
            if (!ok)
            {
                Console.WriteLine("SKIP image update (invalid image url). Archiving product " + productId + " url " + imageUrl);
                try
                {
                    var archivePayload = new
                    {
                        product = new
                        {
                            id = productId,
                            status = "archived"
                        }
                    };

                    await ShopifyPutAsync<dynamic>(shopify, "products/" + productId + ".json", archivePayload, d => d, ct);
                    Console.WriteLine("[INFO] Archived product " + productId + " due to invalid image URL.");
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[WARN] Failed to archive product " + productId + ": " + ex.Message);
                }

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



        //
        // GrapghQL API 
        //

        private static async Task<T> ShopifyGraphQlAsync<T>(HttpClient shopify, string query, object variables, CancellationToken ct)
        {
            var payload = new
            {
                query,
                variables
            };

            var json = JsonConvert.SerializeObject(payload);

            using (var req = new HttpRequestMessage(HttpMethod.Post, "graphql.json"))
            {
                req.Content = new StringContent(json, Encoding.UTF8, "application/json");

                using (var resp = await ShopifySendWithRetryAsync(shopify, req, ct))
                {
                    resp.EnsureSuccessStatusCode();
                    var respJson = await resp.Content.ReadAsStringAsync();
                    return JsonConvert.DeserializeObject<T>(respJson);
                }
            }
        }

        private static async Task<dynamic> GetShopifyProductGraphQlAsync(
            HttpClient shopify,
            long productId,
            CancellationToken ct)
        {
            var gid = $"gid://shopify/Product/{productId}";

            const string query = @"
                    query ($id: ID!) {
                      product(id: $id) {
                        id
                        title
                        vendor
                        productType
                        tags
                        options {
                          name
                          values
                        }
                        variants(first: 250) {
                          edges {
                            node {
                              id
                              title
                              price
                              position
                              barcode
                              selectedOptions { name value }
                              inventoryQuantity
                              inventoryItem { legacyResourceId }
                              metafield(namespace: ""custom"", key: ""hsh_antal"") { value }
                            }
                          }
                        }
                      }
                    }";

            dynamic gql = await ShopifyGraphQlAsync<dynamic>(shopify, query, new { id = gid }, ct);
            var p = gql?.data?.product;
            if (p == null)
                return new { product = (object)null };

            // Convert tags list -> CSV string (REST-style)
            string tagsCsv = "";
            try
            {
                var tagsList = new List<string>();
                foreach (var t in p.tags)
                    tagsList.Add((string)t);

                tagsCsv = string.Join(", ", tagsList); // REST usually comes back comma+space
            }
            catch { tagsCsv = ""; }

            // Flatten variants to REST-style array
            var flatVariants = new List<object>();

            try
            {
                foreach (var edge in p.variants.edges)
                {
                    var n = edge.node;

                    // option1 (Size) from selectedOptions; fallback to title
                    string option1 = null;
                    try
                    {
                        foreach (var opt in n.selectedOptions)
                        {
                            if (((string)opt.name).Equals("Size", StringComparison.OrdinalIgnoreCase))
                            {
                                option1 = (string)opt.value;
                                break;
                            }
                        }

                        if (string.IsNullOrWhiteSpace(option1) && n.selectedOptions != null)
                            option1 = (string)n.selectedOptions[0].value;
                    }
                    catch { /* ignore */ }

                    if (string.IsNullOrWhiteSpace(option1))
                        option1 = (string)(n.title ?? "");

                    // Build REST-like variant object
                    var metafields = new List<object>();
                    try
                    {
                        var mfVal = (string)n.metafield?.value;
                        if (!string.IsNullOrWhiteSpace(mfVal))
                        {
                            metafields.Add(new
                            {
                                @namespace = "custom",
                                key = "hsh_antal",
                                value = mfVal
                            });
                        }
                    }
                    catch { /* ignore */ }

                    flatVariants.Add(new
                    {
                        id = ExtractLegacyIdFromGid((string)n.id), // numeric variant id (optional, but nice)
                        title = (string)n.title,
                        price = n.price,
                        position = n.position,
                        barcode = (string)n.barcode,
                        option1 = option1,
                        inventory_quantity = (int)(n.inventoryQuantity ?? 0),
                        inventory_item_id = (long)n.inventoryItem.legacyResourceId,
                        metafields = metafields
                    });
                }
            }
            catch
            {
                // if variants missing, keep empty list
            }

            // Return object shaped like REST: { product: { ... } }
            return new
            {
                product = new
                {
                    id = ExtractLegacyIdFromGid((string)p.id),
                    title = (string)p.title,
                    vendor = (string)p.vendor,
                    product_type = (string)p.productType,
                    tags = tagsCsv, // CSV string
                    options = NormalizeOptions(p.options),
                    variants = flatVariants
                }
            };
        }

        // Shopify GraphQL IDs are like: gid://shopify/ProductVariant/1234567890
        private static long ExtractLegacyIdFromGid(string gid)
        {
            if (string.IsNullOrWhiteSpace(gid)) return 0;
            var idx = gid.LastIndexOf('/');
            if (idx < 0 || idx == gid.Length - 1) return 0;
            var tail = gid.Substring(idx + 1);
            return long.TryParse(tail, out var id) ? id : 0;
        }

        private static List<object> NormalizeOptions(dynamic options)
        {
            var list = new List<object>();
            try
            {
                foreach (var o in options)
                {
                    var values = new List<string>();
                    foreach (var v in o.values)
                        values.Add((string)v);

                    list.Add(new
                    {
                        name = (string)o.name,
                        values = values
                    });
                }
            }
            catch { }

            return list;
        }


    }

    public class ShopifySkuIndexes
    {
        public Dictionary<string, long> Active = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, long> Archived = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
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

    // ==========================
    // Shopify Token Refresh logic
    // ==========================

    public sealed class TokenResponse
    {
        // Matches the JSON you receive from your token endpoint.
        public string access_token { get; set; }
        public int expires_in { get; set; } // seconds (if present)
    }

    public sealed class ShopifyTokenProvider
    {
        private readonly string _storeDomain;
        private readonly string _clientId;
        private readonly string _clientSecret;
        private readonly HttpClient _http = new HttpClient();
        private readonly SemaphoreSlim _mutex = new SemaphoreSlim(1, 1);

        private string _accessToken;
        private DateTime _expiresAtUtc = DateTime.MinValue;

        public ShopifyTokenProvider(string storeDomain, string clientId, string clientSecret)
        {
            _storeDomain = storeDomain ?? throw new ArgumentNullException(nameof(storeDomain));
            _clientId = clientId ?? throw new ArgumentNullException(nameof(clientId));
            _clientSecret = clientSecret ?? throw new ArgumentNullException(nameof(clientSecret));
        }

        public async Task<string> GetAccessTokenAsync(CancellationToken ct = default)
        {
            // Prevent concurrent refreshes
            await _mutex.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                // If token exists and is not close to expiry, return it.
                if (!string.IsNullOrWhiteSpace(_accessToken) &&
                    DateTime.UtcNow < _expiresAtUtc - TimeSpan.FromMinutes(5))
                {
                    return _accessToken;
                }

                // Request new token
                var url = $"https://{_storeDomain}/admin/oauth/access_token";
                using (var req = new HttpRequestMessage(HttpMethod.Post, url))
                {
                    var form = new[]
                    {
                    new KeyValuePair<string, string>("grant_type", "client_credentials"),
                    new KeyValuePair<string, string>("client_id", _clientId),
                    new KeyValuePair<string, string>("client_secret", _clientSecret)
                };

                    req.Content = new FormUrlEncodedContent(form);

                    using (var resp = await _http.SendAsync(req, ct).ConfigureAwait(false))
                    {
                        resp.EnsureSuccessStatusCode();
                        var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                        TokenResponse dto = null;
                        try { dto = JsonConvert.DeserializeObject<TokenResponse>(json); } catch { /* ignore */ }

                        if (dto == null || string.IsNullOrWhiteSpace(dto.access_token))
                            throw new Exception("Failed to obtain Shopify access token.");

                        _accessToken = dto.access_token;

                        if (dto.expires_in > 0)
                            _expiresAtUtc = DateTime.UtcNow.AddSeconds(dto.expires_in);
                        else
                            // If server doesn't return expires_in, pick sensible default (23h).
                            _expiresAtUtc = DateTime.UtcNow.AddHours(23);

                        return _accessToken;
                    }
                }
            }
            finally
            {
                _mutex.Release();
            }
        }
    }

    public sealed class TokenRefreshHandler : DelegatingHandler
    {
        private readonly ShopifyTokenProvider _provider;

        public TokenRefreshHandler(ShopifyTokenProvider provider)
        {
            _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // Ensure we have a fresh token for each outgoing request.
            var token = await _provider.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);

            // Replace any existing X-Shopify-Access-Token header (safe).
            if (request.Headers.Contains("X-Shopify-Access-Token"))
                request.Headers.Remove("X-Shopify-Access-Token");

            request.Headers.Add("X-Shopify-Access-Token", token);

            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
    }



    // ==========================
    // Turum Token Refresh logic
    // ==========================

    public sealed class TurumTokenProvider
    {
        private readonly string _username;
        private readonly string _password;
        private readonly HttpClient _http = new HttpClient();
        private readonly SemaphoreSlim _mutex = new SemaphoreSlim(1, 1);

        private string _accessToken;
        private DateTime _expiresAtUtc = DateTime.MinValue;

        public TurumTokenProvider(string username, string password)
        {
            _username = username ?? throw new ArgumentNullException(nameof(username));
            _password = password ?? throw new ArgumentNullException(nameof(password));
        }

        public async Task<string> GetAccessTokenAsync(CancellationToken ct = default)
        {
            await _mutex.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (!string.IsNullOrWhiteSpace(_accessToken) &&
                    DateTime.UtcNow < _expiresAtUtc - TimeSpan.FromMinutes(5))
                {
                    return _accessToken;
                }

                var url = "https://api.b2b.turum.pl/v1/account/login";
                using (var req = new HttpRequestMessage(HttpMethod.Post, url))
                {
                    var body = new
                    {
                        username = _username,
                        password = _password
                    };

                    req.Content = new StringContent(JsonConvert.SerializeObject(body), Encoding.UTF8, "application/json");
                    req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

                    using (var resp = await _http.SendAsync(req, ct).ConfigureAwait(false))
                    {
                        resp.EnsureSuccessStatusCode();
                        var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

                        string token = null;
                        int expiresIn = 0;

                        try
                        {
                            var obj = JsonConvert.DeserializeObject<JObject>(json);
                            if (obj != null)
                            {
                                token = obj.Value<string>("token")
                                        ?? obj.Value<string>("access_token")
                                        ?? obj.Value<string>("jwt")
                                        ?? obj.Value<string>("accessToken");

                                if (string.IsNullOrWhiteSpace(token) && obj["data"] != null)
                                {
                                    var data = obj["data"] as JObject;
                                    if (data != null)
                                    {
                                        token = data.Value<string>("token")
                                            ?? data.Value<string>("access_token")
                                            ?? data.Value<string>("jwt")
                                            ?? data.Value<string>("accessToken");
                                    }
                                }

                                if (obj["expires_in"] != null)
                                    int.TryParse(obj["expires_in"].ToString(), out expiresIn);
                            }
                        }
                        catch
                        {
                            // ignore parse errors below - try raw token fallback
                        }

                        if (string.IsNullOrWhiteSpace(token))
                            token = json?.Trim().Trim('"');

                        if (string.IsNullOrWhiteSpace(token))
                            throw new Exception("Failed to obtain Turum token. Response: " + json);

                        _accessToken = token;
                        if (expiresIn > 0)
                            _expiresAtUtc = DateTime.UtcNow.AddSeconds(expiresIn);
                        else
                            _expiresAtUtc = DateTime.UtcNow.AddHours(23);

                        return _accessToken;
                    }
                }
            }
            finally
            {
                _mutex.Release();
            }
        }
    }

    public sealed class TurumTokenRefreshHandler : DelegatingHandler
    {
        private readonly TurumTokenProvider _provider;

        public TurumTokenRefreshHandler(TurumTokenProvider provider)
        {
            _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var token = await _provider.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);

            if (request.Headers.Authorization != null)
                request.Headers.Authorization = null;

            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
    }


}