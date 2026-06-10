using System;
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

                foreach (var p in turumProducts.AsEnumerable())
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

                        // Upsert variants (by size)
                        var variantsChanged = await UpsertVariantsBySizeAsync(shopifyHttp, productId, shopifyProduct, p, ct);

                        // Refresh if variants were created or deleted.
                        if (variantsChanged)
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

    }
}
