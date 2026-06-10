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
                        category = Settings.ShopifySneakersCategoryId
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
}
