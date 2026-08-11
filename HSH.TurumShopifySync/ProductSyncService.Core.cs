using System;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace HSH.TurumShopifySync
{
    internal static partial class ProductSyncService
    {
        // Hvis et produkt findes og har tagget PO på Shopify, så skal det ikke opdateres, men oprettes som et nyt produkt.
        // Alle produkter skal have tilføjet kollekktionen "Sneakers" på Shopify. Derudover skal de have kollektionen, der svarer til brandet fra Turum. Og hvis kollektionen ikke findes, skal den oprettes først.
        // Alle produkter fra Turum skal have kategorien "Sneakers" på Shopify.

        private static SyncSettings _settings;

        private static SyncSettings Settings
        {
            get
            {
                if (_settings == null)
                    throw new InvalidOperationException("ProductSyncService has not been initialized with settings.");

                return _settings;
            }
        }

        private static readonly HttpClient _imageCheckClient = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate })
        {
            Timeout = TimeSpan.FromSeconds(10)
        };

        private static bool EqualsIgnoreCase(string a, string b) => string.Equals((a ?? "").Trim(), (b ?? "").Trim(), StringComparison.OrdinalIgnoreCase);
        public static async Task RunAsync(SyncSettings settings)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
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
            var clientId = settings.ShopifyClientId;
            var clientSecret = settings.ShopifyClientSecret;
            if (!string.IsNullOrWhiteSpace(clientId) && !string.IsNullOrWhiteSpace(clientSecret))
            {
                var tokenProvider = new ShopifyTokenProvider(settings.ShopifyStoreDomain, clientId, clientSecret);
                shopifyHttp = CreateShopifyClient(settings.ShopifyStoreDomain, tokenProvider, settings.ShopifyApiVersion);
                Console.WriteLine("Shopify client: using token provider (auto-refresh).");
            }
            else
            {
                // Fallback to static admin token (unchanged behavior)

                if (string.IsNullOrWhiteSpace(settings.ShopifyAdminToken))
                    throw new Exception("Missing env var SHOPIFY_ADMIN_TOKEN");
                if (string.IsNullOrWhiteSpace(settings.TurumToken))
                    throw new Exception("Missing env var TURUM_TOKEN");

                shopifyHttp = CreateShopifyClient(settings.ShopifyStoreDomain, settings.ShopifyAdminToken, settings.ShopifyApiVersion);
                Console.WriteLine("Shopify client: using static admin token (no auto-refresh).");
            }

            // Turum token auto-refresh: prefer TURUM_USERNAME & TURUM_PASSWORD env vars
            var turumUser = settings.TurumUsername;
            var turumPass = settings.TurumPassword;
            if (!string.IsNullOrWhiteSpace(turumUser) && !string.IsNullOrWhiteSpace(turumPass))
            {
                var turumProvider = new TurumTokenProvider(turumUser, turumPass);
                turumHttp = CreateTurumClient(turumProvider);
                Console.WriteLine("Turum client: using token provider (auto-refresh).");
            }
            else
            {
                // fallback to static token
                if (string.IsNullOrWhiteSpace(settings.TurumToken))
                    throw new Exception("Missing env var TURUM_TOKEN or TURUM_USERNAME/TURUM_PASSWORD");
                turumHttp = CreateTurumClient(settings.TurumToken);
                Console.WriteLine("Turum client: using static token (no auto-refresh).");
            }

            using (turumHttp)
            using (shopifyHttp) // created above with possible token provider
            {
                var locationId = await WithBusyIndicatorAsync(
                    "Getting Shopify location",
                    () => GetFirstShopifyLocationIdAsync(shopifyHttp, ct),
                    ct);
                Console.WriteLine("Using Shopify location ID: " + locationId);

                // Build SKU -> productId map (paged)
                var shopifyIndexTimer = Stopwatch.StartNew();
                var skuIndexes = await WithBusyIndicatorAsync(
                    "Indexing Shopify SKUs",
                    () => BuildShopifySkuIndexesAsync(shopifyHttp, ct),
                    ct);
                shopifyIndexTimer.Stop();
                var activeSkuIndex = skuIndexes.Active;
                var archivedSkuIndex = skuIndexes.Archived;
                var shopifyProductsById = skuIndexes.ProductsById;
                Console.WriteLine("Indexed SKUs from Shopify: Active=" + activeSkuIndex.Count + " Archived=" + archivedSkuIndex.Count + " Products=" + shopifyProductsById.Count);
                Console.WriteLine("Shopify SKU index elapsed: " + shopifyIndexTimer.Elapsed.ToString(@"hh\:mm\:ss\.fff"));

                // Fetch Turum products
                var turumProducts = await WithBusyIndicatorAsync(
                    "Fetching Turum products",
                    () => FetchTurumProductsAsync(turumHttp, ct),
                    ct);
                Console.WriteLine("Fetched TURUM products: " + turumProducts.Count);

                int created = 0, updated = 0;

                int total = turumProducts.Count;
                int processed = 0;
                var mainLoopTimer = Stopwatch.StartNew();
                var productFetchTimer = TimeSpan.Zero;
                var imageTimer = TimeSpan.Zero;
                var productUpdateTimer = TimeSpan.Zero;
                var variantTimer = TimeSpan.Zero;
                var variantRefreshTimer = TimeSpan.Zero;
                var reorderTimer = TimeSpan.Zero;
                var inventoryTimer = TimeSpan.Zero;
                int liveProductFetches = 0;
                int variantRefreshes = 0;
                int reorderCount = 0;

                foreach (var p in turumProducts.AsEnumerable())
                {
                    try
                    {
                        if (p == null || string.IsNullOrWhiteSpace(p.sku))
                            continue;

                        var sourceBrand = p.brand;
                        p.brand = NormalizeTurumBrand(p.brand);
                        if (p.brand.Length == 0 && !string.IsNullOrWhiteSpace(sourceBrand))
                            Console.WriteLine("[WARN] Ignoring unknown TURUM brand '" + sourceBrand.Trim() + "' for SKU " + p.sku);

                        long productId;
                        bool mustCreateNewBecausePo = false;

                        // DECLARE OUTSIDE so we can reuse it later
                        dynamic existingProductDoc = null;

                        // Category detection (for tags + product_type)
                        var category = DetectTurumCategory(p);

                        bool? includeImage = null;

                        // Check if SKU already exists
                        if (activeSkuIndex.TryGetValue(p.sku, out productId))
                        {
                            if (!shopifyProductsById.TryGetValue(productId, out existingProductDoc))
                            {
                                var opTimer = Stopwatch.StartNew();
                                existingProductDoc = await GetShopifyProductGraphQlAsync(shopifyHttp, productId, ct);
                                opTimer.Stop();
                                productFetchTimer += opTimer.Elapsed;
                                liveProductFetches++;
                            }

                            // NEW RULE: if tagged "PO" → force new product
                            if (HasTag(existingProductDoc, "PO"))
                                mustCreateNewBecausePo = true;
                        }
                        else if (archivedSkuIndex.TryGetValue(p.sku, out productId))
                        {
                            includeImage = await IsValidImageUrlAsync(p.image, ct);

                            // Unarchive only if the image is valid. Skip otherwise.
                            if (includeImage != true)
                            {
                                Console.WriteLine("[WARN] SKU " + p.sku + " exists but archived, and image URL is invalid. Keep it archived and skip. SKU " + p.sku + " url " + p.image);
                                continue;
                            }

                            Console.WriteLine("[INFO] SKU " + p.sku + " exists but archived. Unarchive and treat as update.");

                            // Unarchive the product first
                            await SetShopifyProductStatusGraphQlAsync(shopifyHttp, productId, "ACTIVE", ct);

                            // Move it from archived index to active index
                            archivedSkuIndex.Remove(p.sku);
                            activeSkuIndex[p.sku] = productId;

                            // Continue with update flow (fetch product from shopify)
                            if (!shopifyProductsById.TryGetValue(productId, out existingProductDoc))
                            {
                                var opTimer = Stopwatch.StartNew();
                                existingProductDoc = await GetShopifyProductGraphQlAsync(shopifyHttp, productId, ct);
                                opTimer.Stop();
                                productFetchTimer += opTimer.Elapsed;
                                liveProductFetches++;
                            }
                        }

                        if (!activeSkuIndex.ContainsKey(p.sku) || mustCreateNewBecausePo)
                        {
                            // ======================
                            // CREATE PRODUCT
                            // ======================

                            if (!includeImage.HasValue)
                                includeImage = await IsValidImageUrlAsync(p.image, ct);

                            // Skip if the image is invalid
                            if (includeImage != true)
                            {
                                Console.WriteLine("[WARN] Invalid image URL, Skip product creation. SKU " + p.sku + " url " + p.image);
                                continue;
                            }

                            try
                            {
                                productId = await CreateShopifyProductGraphQlAsync(shopifyHttp, p, includeImage.Value, category, ct);
                            }
                            catch (Exception ex)
                            {
                                // Shopify returns 422 with "Image URL is invalid"
                                if (ex.Message.IndexOf("Image URL is invalid", StringComparison.OrdinalIgnoreCase) >= 0)
                                {
                                    Console.WriteLine("[WARN] CREATE without image (invalid url). SKU " + p.sku + " url " + p.image);

                                    // retry without images
                                    productId = await CreateShopifyProductGraphQlAsync(shopifyHttp, p, false, category, ct);
                                }
                                else
                                {
                                    throw;
                                }
                            }

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
                            var opTimer = Stopwatch.StartNew();
                            await ReplaceProductImagesAsync(shopifyHttp, productId, p.image, p.name, existingProductDoc, ct);
                            opTimer.Stop();
                            imageTimer += opTimer.Elapsed;

                            // CLEANUP and merge tags
                            string mergedTags = TagsMergeAndCleanUp(existingProductDoc, p, category);

                            var needsProductUpdate = ProductUpdateNeeded(existingProductDoc, p.name, p.brand, category, mergedTags);
                            if (needsProductUpdate)
                            {
                                opTimer = Stopwatch.StartNew();
                                await UpdateShopifyProductGraphQlAsync(shopifyHttp, productId, p, mergedTags, category, ct);
                                opTimer.Stop();
                                productUpdateTimer += opTimer.Elapsed;

                                updated++;
                                Console.WriteLine("UPDATED SKU " + p.sku + " -> product " + productId);
                            }
                            else
                            {
                                //Console.WriteLine("SKIP product update (unchanged) SKU " + p.sku);
                            }

                        }

                        // Reconcile category and manual collections on every run. This also
                        // removes stale Sneakers/brand assignments left by older classifications.
                        await ReconcileProductOrganizationAsync(shopifyHttp, productId, existingProductDoc, p.brand, category, ct);

                        // Always fetch product to get variants + inventory_item_id
                        dynamic shopifyProduct = existingProductDoc;
                        if (shopifyProduct == null)
                        {
                            var opTimer = Stopwatch.StartNew();
                            shopifyProduct = await GetShopifyProductGraphQlAsync(shopifyHttp, productId, ct);
                            opTimer.Stop();
                            productFetchTimer += opTimer.Elapsed;
                            liveProductFetches++;
                        }

                        // Upsert variants (by size)
                        {
                            var opTimer = Stopwatch.StartNew();
                            var variantsChanged = await UpsertVariantsBySizeAsync(shopifyHttp, productId, shopifyProduct, p, ct);
                            opTimer.Stop();
                            variantTimer += opTimer.Elapsed;

                            // Refresh if variants were created or deleted.
                            if (variantsChanged)
                            {
                                opTimer = Stopwatch.StartNew();
                                shopifyProduct = await GetShopifyProductGraphQlAsync(shopifyHttp, productId, ct);
                                opTimer.Stop();
                                variantRefreshTimer += opTimer.Elapsed;
                                variantRefreshes++;
                            }
                        }

                        // Reorder variants if we need to
                        if (VariantReorderNeeded(shopifyProduct))
                        {
                            Console.WriteLine("[INFO] Reordering variants needed. SKU " + p.sku);
                            var opTimer = Stopwatch.StartNew();
                            await EnsureVariantPositionsBySizeAsync(shopifyHttp, productId, shopifyProduct, ct);
                            opTimer.Stop();
                            reorderTimer += opTimer.Elapsed;
                            reorderCount++;

                            // Optional: refresh if you rely on correct positions later (usually not needed)
                        }
                        else
                        {
                            //Console.WriteLine("[INFO] SKIP reorder (already ordered). SKU " + p.sku);
                        }

                        // Inventory
                        {
                            var opTimer = Stopwatch.StartNew();
                            await SetInventoryFromTurumAsync(shopifyHttp, locationId, shopifyProduct, p, ct);
                            opTimer.Stop();
                            inventoryTimer += opTimer.Elapsed;
                        }
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

                mainLoopTimer.Stop();
                Console.WriteLine("Main product loop elapsed: " + mainLoopTimer.Elapsed.ToString(@"hh\:mm\:ss\.fff"));
                Console.WriteLine("Performance profile: ProductFetch=" + productFetchTimer.ToString(@"hh\:mm\:ss\.fff") +
                                  " (" + liveProductFetches + " calls), Image=" + imageTimer.ToString(@"hh\:mm\:ss\.fff") +
                                  ", ProductUpdate=" + productUpdateTimer.ToString(@"hh\:mm\:ss\.fff") +
                                  ", Variants=" + variantTimer.ToString(@"hh\:mm\:ss\.fff") +
                                  ", VariantRefresh=" + variantRefreshTimer.ToString(@"hh\:mm\:ss\.fff") +
                                  " (" + variantRefreshes + " calls), Reorder=" + reorderTimer.ToString(@"hh\:mm\:ss\.fff") +
                                  " (" + reorderCount + " calls), Inventory=" + inventoryTimer.ToString(@"hh\:mm\:ss\.fff"));
                Console.WriteLine("Done. Created: " + created + ", Updated: " + updated);

                // Remove products from Shopify that are no longer in Turum
                // After main sync loop:
                Console.WriteLine();
                Console.WriteLine("[INFO] Starting cleanup of missing Turum products...");
                await WithBusyIndicatorAsync(
                    "Cleaning up missing Turum products",
                    () => ArchiveAndCleanupMissingTurumProductsAsync(shopifyHttp, turumProducts, activeSkuIndex, ct),
                    ct);

            }
        }

    }
}
