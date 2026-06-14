using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace HSH.TurumShopifySync
{
    internal static partial class ProductSyncService
    {
        private static readonly Dictionary<string, long> _collectionIdCache = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        private static bool _collectionIdCacheLoaded;

        private static string ProductGid(long id) => "gid://shopify/Product/" + id;
        private static string VariantGid(long id) => "gid://shopify/ProductVariant/" + id;
        private static string CollectionGid(long id) => "gid://shopify/Collection/" + id;
        private static string InventoryItemGid(long id) => "gid://shopify/InventoryItem/" + id;
        private static string LocationGid(long id) => "gid://shopify/Location/" + id;

        private static async Task<dynamic> ShopifyGraphQlDocumentAsync(HttpClient shopify, string query, object variables, CancellationToken ct)
        {
            dynamic doc = await ShopifyGraphQlAsync<dynamic>(shopify, query, variables, ct);

            if (doc == null)
                throw new Exception("Shopify GraphQL returned an empty response.");

            if (doc.errors != null && doc.errors.Count > 0)
                throw new Exception("Shopify GraphQL failed: " + JsonConvert.SerializeObject(doc.errors));

            return doc;
        }

        private static void ThrowIfUserErrors(dynamic userErrors, string operation)
        {
            try
            {
                if (userErrors == null || userErrors.Count == 0)
                    return;

                throw new Exception(operation + " failed: " + JsonConvert.SerializeObject(userErrors));
            }
            catch (Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
            {
                return;
            }
        }

        private static List<string> SplitTags(string tagsCsv)
        {
            if (string.IsNullOrWhiteSpace(tagsCsv))
                return new List<string>();

            return tagsCsv
                .Split(',')
                .Select(t => t.Trim())
                .Where(t => t.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static async Task<long> GetFirstShopifyLocationIdAsync(HttpClient shopify, CancellationToken ct)
        {
            const string query = @"
                query {
                  locations(first: 1) {
                    nodes { id }
                  }
                }";

            dynamic doc = await ShopifyGraphQlDocumentAsync(shopify, query, new { }, ct);
            if (doc.data.locations.nodes == null || doc.data.locations.nodes.Count == 0)
                throw new Exception("No Shopify locations found.");

            return ExtractLegacyIdFromGid((string)doc.data.locations.nodes[0].id);
        }

        private static async Task<ShopifySkuIndexes> BuildShopifySkuIndexesAsync(HttpClient shopify, CancellationToken ct)
        {
            var res = new ShopifySkuIndexes();
            await FillIndexByStatusGraphQlAsync(shopify, "ACTIVE", res.Active, res.ProductsById, ct);
            await FillIndexByStatusGraphQlAsync(shopify, "ARCHIVED", res.Archived, res.ProductsById, ct);
            return res;
        }

        private static async Task FillIndexByStatusGraphQlAsync(
            HttpClient shopify,
            string status,
            Dictionary<string, long> index,
            Dictionary<long, dynamic> productsById,
            CancellationToken ct)
        {
            const string query = @"
                query($cursor: String, $search: String!) {
                  products(first: 25, after: $cursor, query: $search) {
                    pageInfo { hasNextPage endCursor }
                    nodes {
                      id
                      title
                      vendor
                      productType
                      tags
                      status
                      media(first: 1) {
                        nodes {
                          ... on MediaImage { image { url } }
                        }
                      }
                      options { name values }
                      variants(first: 25) {
                        pageInfo { hasNextPage }
                        edges {
                          node {
                            id
                            title
                            sku
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
                  }
                }";

            string cursor = null;
            var search = "status:" + status.ToLowerInvariant();

            while (true)
            {
                dynamic doc = await ShopifyGraphQlDocumentAsync(shopify, query, new { cursor = cursor, search = search }, ct);

                foreach (var p in doc.data.products.nodes)
                {
                    long productId = ExtractLegacyIdFromGid((string)p.id);
                    var variantsTruncated = false;
                    try { variantsTruncated = (bool)p.variants.pageInfo.hasNextPage; } catch { }

                    if (productId > 0 && !variantsTruncated && !productsById.ContainsKey(productId))
                        productsById[productId] = NormalizeShopifyProductGraphQlNode(p);

                    if (p.variants == null || p.variants.edges == null)
                        continue;

                    foreach (var edge in p.variants.edges)
                    {
                        var sku = ((string)edge.node.sku ?? "").Trim();
                        if (sku.Length == 0)
                            continue;

                        if (!index.ContainsKey(sku))
                            index[sku] = productId;
                    }
                }

                if (!(bool)doc.data.products.pageInfo.hasNextPage)
                    break;

                cursor = (string)doc.data.products.pageInfo.endCursor;
            }
        }

        private static async Task<long> CreateShopifyProductGraphQlAsync(HttpClient shopify, TurumProduct p, bool includeImage, string category, CancellationToken ct)
        {
            var productType = category == "Sneakers" ? "Sneakers" : category;
            var tags = SplitTags(TagsMergeAndCleanUp(null, p, category));
            var optionValues = p.variants
                .Select(v => v.eu_size ?? v.size)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => new { name = s.Trim() })
                .GroupBy(v => v.name, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();

            const string query = @"
                mutation($product: ProductCreateInput!, $media: [CreateMediaInput!]) {
                  productCreate(product: $product, media: $media) {
                    product { id }
                    userErrors { field message }
                  }
                }";

            var variables = new
            {
                product = new
                {
                    title = p.name,
                    vendor = p.brand,
                    productType = productType,
                    status = "ACTIVE",
                    tags = tags,
                    productOptions = new[]
                    {
                        new { name = "Vælg størrelse", values = optionValues }
                    }
                },
                media = includeImage
                    ? new[] { new { originalSource = p.image, alt = p.name, mediaContentType = "IMAGE" } }
                    : new object[0]
            };

            dynamic doc = await ShopifyGraphQlDocumentAsync(shopify, query, variables, ct);
            ThrowIfUserErrors(doc.data.productCreate.userErrors, "productCreate");
            return ExtractLegacyIdFromGid((string)doc.data.productCreate.product.id);
        }

        private static async Task UpdateShopifyProductGraphQlAsync(HttpClient shopify, long productId, TurumProduct p, string mergedTags, string category, CancellationToken ct)
        {
            await UpdateShopifyProductGraphQlAsync(
                shopify,
                productId,
                new
                {
                    id = ProductGid(productId),
                    title = p.name,
                    vendor = p.brand,
                    productType = category,
                    tags = SplitTags(mergedTags)
                },
                ct);
        }

        private static async Task SetShopifyProductStatusGraphQlAsync(HttpClient shopify, long productId, string status, CancellationToken ct)
        {
            await UpdateShopifyProductGraphQlAsync(
                shopify,
                productId,
                new
                {
                    id = ProductGid(productId),
                    status = status.ToUpperInvariant()
                },
                ct);
        }

        private static async Task UpdateShopifyProductTagsGraphQlAsync(HttpClient shopify, long productId, string tagsCsv, CancellationToken ct)
        {
            await UpdateShopifyProductGraphQlAsync(
                shopify,
                productId,
                new
                {
                    id = ProductGid(productId),
                    tags = SplitTags(tagsCsv)
                },
                ct);
        }

        private static async Task UpdateShopifyProductGraphQlAsync(HttpClient shopify, long productId, object product, CancellationToken ct)
        {
            const string query = @"
                mutation($product: ProductUpdateInput!) {
                  productUpdate(product: $product) {
                    product { id }
                    userErrors { field message }
                  }
                }";

            dynamic doc = await ShopifyGraphQlDocumentAsync(shopify, query, new { product = product }, ct);
            ThrowIfUserErrors(doc.data.productUpdate.userErrors, "productUpdate " + productId);
        }

        private static async Task SetShopifyCategorySneakersAsync(HttpClient shopify, long productId, CancellationToken ct)
        {
            await UpdateShopifyProductGraphQlAsync(
                shopify,
                productId,
                new
                {
                    id = ProductGid(productId),
                    category = Settings.ShopifySneakersCategoryId
                },
                ct);
        }

        private static async Task UpdateVariantGraphQlAsync(HttpClient shopify, long productId, long variantId, decimal price, string sku, CancellationToken ct)
        {
            await UpdateVariantsGraphQlAsync(
                shopify,
                productId,
                new[] { new VariantUpdateInput { VariantId = variantId, Price = price, Sku = sku } },
                ct);
        }

        private static async Task UpdateVariantsGraphQlAsync(HttpClient shopify, long productId, IEnumerable<VariantUpdateInput> updates, CancellationToken ct)
        {
            const string query = @"
                mutation($productId: ID!, $variants: [ProductVariantsBulkInput!]!) {
                  productVariantsBulkUpdate(productId: $productId, variants: $variants, allowPartialUpdates: true) {
                    productVariants { id }
                    userErrors { field message }
                  }
                }";

            var variables = new
            {
                productId = ProductGid(productId),
                variants = updates.Select(update => new
                {
                    id = VariantGid(update.VariantId),
                    price = update.Price.ToString("0", CultureInfo.InvariantCulture),
                    inventoryItem = new { sku = update.Sku, tracked = true }
                }).ToList()
            };

            dynamic doc = await ShopifyGraphQlDocumentAsync(shopify, query, variables, ct);
            ThrowIfUserErrors(doc.data.productVariantsBulkUpdate.userErrors, "productVariantsBulkUpdate " + productId);
        }

        private sealed class VariantUpdateInput
        {
            public long VariantId { get; set; }
            public decimal Price { get; set; }
            public string Sku { get; set; }
        }

        private static async Task<long> CreateVariantGraphQlAsync(HttpClient shopify, long productId, string optionName, string size, decimal price, string sku, string barcode, CancellationToken ct)
        {
            const string query = @"
                mutation($productId: ID!, $variants: [ProductVariantsBulkInput!]!) {
                  productVariantsBulkCreate(productId: $productId, variants: $variants, strategy: REMOVE_STANDALONE_VARIANT) {
                    productVariants { id }
                    userErrors { field message }
                  }
                }";

            var variables = new
            {
                productId = ProductGid(productId),
                variants = new[]
                {
                    new
                    {
                        price = price.ToString("0", CultureInfo.InvariantCulture),
                        barcode = barcode,
                        taxable = false,
                        inventoryPolicy = "DENY",
                        inventoryItem = new { sku = sku, tracked = true, requiresShipping = true },
                        optionValues = new[]
                        {
                            new { optionName = optionName, name = size }
                        }
                    }
                }
            };

            dynamic doc = await ShopifyGraphQlDocumentAsync(shopify, query, variables, ct);
            ThrowIfUserErrors(doc.data.productVariantsBulkCreate.userErrors, "productVariantsBulkCreate " + productId + " size " + size);

            if (doc.data.productVariantsBulkCreate.productVariants == null || doc.data.productVariantsBulkCreate.productVariants.Count == 0)
                throw new Exception("Variant create returned no variants for product " + productId + " size " + size);

            return ExtractLegacyIdFromGid((string)doc.data.productVariantsBulkCreate.productVariants[0].id);
        }

        private static async Task DeleteVariantGraphQlAsync(HttpClient shopify, long productId, long variantId, CancellationToken ct)
        {
            const string query = @"
                mutation($productId: ID!, $variantsIds: [ID!]!) {
                  productVariantsBulkDelete(productId: $productId, variantsIds: $variantsIds) {
                    product { id }
                    userErrors { field message }
                  }
                }";

            dynamic doc = await ShopifyGraphQlDocumentAsync(
                shopify,
                query,
                new { productId = ProductGid(productId), variantsIds = new[] { VariantGid(variantId) } },
                ct);

            ThrowIfUserErrors(doc.data.productVariantsBulkDelete.userErrors, "productVariantsBulkDelete " + variantId);
        }

        private static async Task ReorderVariantsGraphQlAsync(HttpClient shopify, long productId, IEnumerable<(long id, int position)> positions, CancellationToken ct)
        {
            const string query = @"
                mutation($productId: ID!, $positions: [ProductVariantPositionInput!]!) {
                  productVariantsBulkReorder(productId: $productId, positions: $positions) {
                    product { id }
                    userErrors { field message }
                  }
                }";

            var variables = new
            {
                productId = ProductGid(productId),
                positions = positions.Select(p => new { id = VariantGid(p.id), position = p.position }).ToList()
            };

            dynamic doc = await ShopifyGraphQlDocumentAsync(shopify, query, variables, ct);
            ThrowIfUserErrors(doc.data.productVariantsBulkReorder.userErrors, "productVariantsBulkReorder " + productId);
        }

        private static async Task SetInventoryQuantityGraphQlAsync(HttpClient shopify, long locationId, long inventoryItemId, int quantity, CancellationToken ct)
        {
            await SetInventoryQuantitiesGraphQlAsync(
                shopify,
                locationId,
                new[] { new InventoryQuantityInput { InventoryItemId = inventoryItemId, Quantity = quantity } },
                ct);
        }

        private static async Task SetInventoryQuantitiesGraphQlAsync(HttpClient shopify, long locationId, IEnumerable<InventoryQuantityInput> quantities, CancellationToken ct)
        {
            const string query = @"
                mutation InventorySet($input: InventorySetQuantitiesInput!) {
                  inventorySetQuantities(input: $input) {
                    inventoryAdjustmentGroup { createdAt }
                    userErrors { field message }
                  }
                }";

            var variables = new
            {
                input = new
                {
                    name = "available",
                    reason = "correction",
                    ignoreCompareQuantity = true,
                    referenceDocumentUri = "turum-sync://inventory/" + DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture),
                    quantities = quantities.Select(q => new
                    {
                        inventoryItemId = InventoryItemGid(q.InventoryItemId),
                        locationId = LocationGid(locationId),
                        quantity = q.Quantity
                    }).ToList()
                }
            };

            dynamic doc = await ShopifyGraphQlDocumentAsync(shopify, query, variables, ct);
            ThrowIfUserErrors(doc.data.inventorySetQuantities.userErrors, "inventorySetQuantities");
        }

        private sealed class InventoryQuantityInput
        {
            public long InventoryItemId { get; set; }
            public int Quantity { get; set; }
        }

        private static async Task<long> GetOrCreateCustomCollectionIdAsync(HttpClient shopify, string title, CancellationToken ct)
        {
            title = (title ?? "").Trim();
            if (title.Length == 0)
                throw new Exception("Cannot create or find Shopify collection without a title.");

            await EnsureCollectionIdCacheLoadedAsync(shopify, ct);

            long cachedId;
            if (_collectionIdCache.TryGetValue(title, out cachedId))
                return cachedId;

            const string query = @"
                mutation($input: CollectionInput!) {
                  collectionCreate(input: $input) {
                    collection { id }
                    userErrors { field message }
                  }
                }";

            dynamic doc = await ShopifyGraphQlDocumentAsync(shopify, query, new { input = new { title = title } }, ct);
            ThrowIfUserErrors(doc.data.collectionCreate.userErrors, "collectionCreate " + title);
            var createdId = ExtractLegacyIdFromGid((string)doc.data.collectionCreate.collection.id);
            _collectionIdCache[title] = createdId;
            return createdId;
        }

        private static async Task EnsureCollectionIdCacheLoadedAsync(HttpClient shopify, CancellationToken ct)
        {
            if (_collectionIdCacheLoaded)
                return;

            await LoadCollectionIdCacheAsync(shopify, ct);
            _collectionIdCacheLoaded = true;
        }

        private static async Task LoadCollectionIdCacheAsync(HttpClient shopify, CancellationToken ct)
        {
            const string query = @"
                query($cursor: String) {
                  collections(first: 250, after: $cursor) {
                    pageInfo { hasNextPage endCursor }
                    nodes { id title }
                  }
                }";

            string cursor = null;
            while (true)
            {
                dynamic doc = await ShopifyGraphQlDocumentAsync(shopify, query, new { cursor = cursor }, ct);
                foreach (var c in doc.data.collections.nodes)
                {
                    var title = ((string)c.title ?? "").Trim();
                    if (title.Length == 0)
                        continue;

                    if (!_collectionIdCache.ContainsKey(title))
                        _collectionIdCache[title] = ExtractLegacyIdFromGid((string)c.id);
                }

                if (!(bool)doc.data.collections.pageInfo.hasNextPage)
                    return;

                cursor = (string)doc.data.collections.pageInfo.endCursor;
            }
        }

        private static async Task EnsureProductInCollectionAsync(HttpClient shopify, long productId, long collectionId, CancellationToken ct)
        {
            const string query = @"
                mutation($id: ID!, $productIds: [ID!]!) {
                  collectionAddProducts(id: $id, productIds: $productIds) {
                    collection { id }
                    userErrors { field message }
                  }
                }";

            dynamic doc = await ShopifyGraphQlDocumentAsync(
                shopify,
                query,
                new { id = CollectionGid(collectionId), productIds = new[] { ProductGid(productId) } },
                ct);

            try
            {
                if (doc.data.collectionAddProducts.userErrors != null && doc.data.collectionAddProducts.userErrors.Count > 0)
                {
                    var message = (string)doc.data.collectionAddProducts.userErrors[0].message;
                    if (!string.IsNullOrWhiteSpace(message) && message.IndexOf("already", StringComparison.OrdinalIgnoreCase) >= 0)
                        return;
                }
            }
            catch { }

            ThrowIfUserErrors(doc.data.collectionAddProducts.userErrors, "collectionAddProducts " + productId + " -> " + collectionId);
        }

        private static async Task ReplaceProductImagesGraphQlAsync(HttpClient shopify, long productId, string imageUrl, string alt, CancellationToken ct)
        {
            var productGid = ProductGid(productId);
            var current = await GetProductMediaGraphQlAsync(shopify, productGid, ct);

            if (current.Count > 0 && string.Equals(current[0].src, imageUrl, StringComparison.OrdinalIgnoreCase))
                return;

            if (current.Count > 0)
            {
                foreach (var media in current)
                    await DeleteProductMediaGraphQlAsync(shopify, productGid, new List<string> { media.id }, ct);
            }

            await CreateProductImageGraphQlAsync(shopify, productGid, imageUrl, alt, ct);
        }

        private static async Task<List<(string id, string src)>> GetProductMediaGraphQlAsync(HttpClient shopify, string productGid, CancellationToken ct)
        {
            const string query = @"
                query($id: ID!) {
                  product(id: $id) {
                    media(first: 20) {
                      nodes {
                        id
                        ... on MediaImage { image { url } }
                      }
                    }
                  }
                }";

            dynamic doc = await ShopifyGraphQlDocumentAsync(shopify, query, new { id = productGid }, ct);
            var list = new List<(string id, string src)>();
            if (doc.data.product == null || doc.data.product.media == null || doc.data.product.media.nodes == null)
                return list;

            foreach (var m in doc.data.product.media.nodes)
                list.Add(((string)m.id, (string)(m.image?.url ?? "")));

            return list;
        }

        private static async Task DeleteProductMediaGraphQlAsync(HttpClient shopify, string productGid, List<string> mediaIds, CancellationToken ct)
        {
            const string query = @"
                mutation($productId: ID!, $mediaIds: [ID!]!) {
                  productDeleteMedia(productId: $productId, mediaIds: $mediaIds) {
                    deletedMediaIds
                    userErrors { field message }
                  }
                }";

            dynamic doc = await ShopifyGraphQlDocumentAsync(shopify, query, new { productId = productGid, mediaIds = mediaIds }, ct);
            if (ProductDeleteMediaErrorsAreAlreadyDeleted(doc.data.productDeleteMedia.userErrors))
            {
                Console.WriteLine("[WARN] Product media was already gone for " + productGid + ". Continuing image replacement.");
                return;
            }

            ThrowIfUserErrors(doc.data.productDeleteMedia.userErrors, "productDeleteMedia " + productGid);
        }

        private static bool ProductDeleteMediaErrorsAreAlreadyDeleted(dynamic userErrors)
        {
            try
            {
                if (userErrors == null || userErrors.Count == 0)
                    return false;

                foreach (var error in userErrors)
                {
                    var message = (string)error.message;
                    if (string.IsNullOrWhiteSpace(message) ||
                        message.IndexOf("does not exist", StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        return false;
                    }
                }

                return true;
            }
            catch (Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
            {
                return false;
            }
        }

        private static async Task CreateProductImageGraphQlAsync(HttpClient shopify, string productGid, string imageUrl, string alt, CancellationToken ct)
        {
            const string query = @"
                mutation($productId: ID!, $media: [CreateMediaInput!]!) {
                  productCreateMedia(productId: $productId, media: $media) {
                    media { id }
                    mediaUserErrors { field message }
                  }
                }";

            dynamic doc = await ShopifyGraphQlDocumentAsync(
                shopify,
                query,
                new
                {
                    productId = productGid,
                    media = new[] { new { originalSource = imageUrl, alt = alt, mediaContentType = "IMAGE" } }
                },
                ct);

            ThrowIfUserErrors(doc.data.productCreateMedia.mediaUserErrors, "productCreateMedia " + productGid);
        }
    }
}
