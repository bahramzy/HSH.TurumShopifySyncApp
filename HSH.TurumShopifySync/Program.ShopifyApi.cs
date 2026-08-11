using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace HSH.TurumShopifySync
{
    internal static partial class ProductSyncService
    {
        private static HttpClient CreateShopifyClient(string storeDomain, string adminToken, string apiVersion)
        {
            ConfigureShopifyNetworkDefaults();

            var client = new HttpClient
            {
                BaseAddress = new Uri("https://" + storeDomain + "/admin/api/" + apiVersion + "/"),
                Timeout = TimeSpan.FromMinutes(5)
            };

            client.DefaultRequestHeaders.Add("X-Shopify-Access-Token", adminToken);
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            return client;
        }

        private static HttpClient CreateShopifyClient(string storeDomain, ShopifyTokenProvider tokenProvider, string apiVersion)
        {
            ConfigureShopifyNetworkDefaults();

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

        private static void ConfigureShopifyNetworkDefaults()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            ServicePointManager.DefaultConnectionLimit = 50;
            ServicePointManager.Expect100Continue = false;
        }

        private static async Task<HttpResponseMessage> ShopifySendWithRetryAsync(HttpClient shopify, HttpRequestMessage req, CancellationToken ct)
        {
            for (int attempt = 1; attempt <= 8; attempt++)
            {
                HttpResponseMessage resp = null;

                try
                {
                    resp = await shopify.SendAsync(CloneRequest(req), ct);

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

            return await shopify.SendAsync(CloneRequest(req), ct);
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

            const int maxAttempts = 12;

            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                using (var req = new HttpRequestMessage(HttpMethod.Post, "graphql.json"))
                {
                    req.Content = new StringContent(json, Encoding.UTF8, "application/json");

                    using (var resp = await ShopifySendWithRetryAsync(shopify, req, ct))
                    {
                        var respJson = await resp.Content.ReadAsStringAsync();
                        if (!resp.IsSuccessStatusCode)
                            throw new Exception("Shopify GraphQL HTTP failed: " + (int)resp.StatusCode + " " + resp.ReasonPhrase + "\n" + respJson);

                        var doc = JsonConvert.DeserializeObject<JObject>(respJson);
                        await DelayForGraphQlThrottleAsync(doc, ct);

                        if (GraphQlResponseIsThrottled(doc))
                        {
                            if (attempt < maxAttempts)
                            {
                                await Task.Delay(GetGraphQlThrottleRetryDelay(doc, attempt), ct);
                                continue;
                            }

                            throw new Exception("Shopify GraphQL throttled after " + maxAttempts.ToString(CultureInfo.InvariantCulture) + " attempts: " + respJson);
                        }

                        return doc.ToObject<T>();
                    }
                }
            }

            throw new Exception("Shopify GraphQL failed after throttle retries.");
        }

        private static bool GraphQlResponseIsThrottled(JObject doc)
        {
            var errors = doc?["errors"] as JArray;
            if (errors == null)
                return false;

            foreach (var error in errors)
            {
                var code = (string)error["extensions"]?["code"];
                var message = (string)error["message"];

                if (string.Equals(code, "THROTTLED", StringComparison.OrdinalIgnoreCase) ||
                    (!string.IsNullOrWhiteSpace(message) && message.IndexOf("throttled", StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    return true;
                }
            }

            return false;
        }

        private static TimeSpan GetGraphQlThrottleRetryDelay(JObject doc, int attempt)
        {
            var restoreRate = ReadGraphQlCostDecimal(doc, "restoreRate");
            if (restoreRate > 0)
            {
                var requested = ReadGraphQlCostDecimal(doc, "requestedQueryCost");
                var available = ReadGraphQlCostDecimal(doc, "currentlyAvailable");
                var missing = requested > 0
                    ? Math.Max(10m, requested - available)
                    : 50m;
                var seconds = Math.Min(60m, missing / restoreRate);
                return TimeSpan.FromMilliseconds((double)(seconds * 1000m) + 500);
            }

            return TimeSpan.FromSeconds(Math.Min(10, Math.Pow(2, attempt - 1)));
        }

        private static async Task DelayForGraphQlThrottleAsync(JObject doc, CancellationToken ct)
        {
            var restoreRate = ReadGraphQlCostDecimal(doc, "restoreRate");
            if (restoreRate <= 0)
                return;

            var available = ReadGraphQlCostDecimal(doc, "currentlyAvailable");
            var requested = ReadGraphQlCostDecimal(doc, "requestedQueryCost");
            if (requested <= 0)
                requested = ReadGraphQlCostDecimal(doc, "actualQueryCost");

            var maximumAvailable = ReadGraphQlCostDecimal(doc, "maximumAvailable");
            var threshold = Math.Max(50m, requested + 50m);
            if (maximumAvailable > 0)
                threshold = Math.Min(threshold, Math.Max(50m, maximumAvailable - 50m));

            if (available >= threshold)
                return;

            var seconds = Math.Min(60m, (threshold - available) / restoreRate);
            if (seconds <= 0)
                return;

            await Task.Delay(TimeSpan.FromMilliseconds((double)(seconds * 1000m) + 250), ct);
        }

        private static decimal ReadGraphQlCostDecimal(JObject doc, string name)
        {
            var cost = doc?["extensions"]?["cost"];
            if (cost == null)
                return 0m;

            JToken token;
            if (name == "currentlyAvailable" || name == "restoreRate" || name == "maximumAvailable")
                token = cost["throttleStatus"]?[name];
            else
                token = cost[name];

            if (token == null)
                return 0m;

            decimal value;
            return decimal.TryParse(token.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out value)
                ? value
                : 0m;
        }
        private static async Task<dynamic> GetShopifyProductGraphQlAsync(HttpClient shopify, long productId, CancellationToken ct)
        {
            var gid = "gid://shopify/Product/" + productId;

            const string query = @"
                query ($id: ID!) {
                  product(id: $id) {
                    id
                    title
                    vendor
                    productType
                    tags
                    status
                    category { id }
                    collections(first: 100) { nodes { id title } }
                    media(first: 1) {
                      nodes {
                        ... on MediaImage { image { url } }
                      }
                    }
                    options { name values }
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

            dynamic gql = await ShopifyGraphQlDocumentAsync(shopify, query, new { id = gid }, ct);
            var p = gql?.data?.product;
            if (p == null)
                return new { product = (object)null };

            return NormalizeShopifyProductGraphQlNode(p);
        }

        private static dynamic NormalizeShopifyProductGraphQlNode(dynamic p)
        {
            string tagsCsv = "";
            try
            {
                var tagsList = new List<string>();
                foreach (var t in p.tags)
                    tagsList.Add((string)t);

                tagsCsv = string.Join(", ", tagsList);
            }
            catch { tagsCsv = ""; }

            var flatVariants = new List<object>();
            var flatCollections = new List<object>();
            string firstImageSrc = "";
            string categoryId = "";

            try { categoryId = (string)(p.category?.id ?? ""); } catch { }
            try
            {
                foreach (var collection in p.collections.nodes)
                {
                    flatCollections.Add(new
                    {
                        id = ExtractLegacyIdFromGid((string)collection.id),
                        title = (string)collection.title
                    });
                }
            }
            catch { }

            try
            {
                if (p.media != null && p.media.nodes != null && p.media.nodes.Count > 0)
                    firstImageSrc = (string)(p.media.nodes[0].image?.url ?? "");
            }
            catch { firstImageSrc = ""; }

            try
            {
                foreach (var edge in p.variants.edges)
                {
                    var n = edge.node;
                    string option1 = null;

                    try
                    {
                        foreach (var opt in n.selectedOptions)
                        {
                            var optName = (string)opt.name;
                            if (optName.Equals("Size", StringComparison.OrdinalIgnoreCase) ||
                                optName.Equals("Vælg størrelse", StringComparison.OrdinalIgnoreCase))
                            {
                                option1 = (string)opt.value;
                                break;
                            }
                        }

                        if (string.IsNullOrWhiteSpace(option1) && n.selectedOptions != null)
                            option1 = (string)n.selectedOptions[0].value;
                    }
                    catch { }

                    if (string.IsNullOrWhiteSpace(option1))
                        option1 = (string)(n.title ?? "");

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
                    catch { }

                    flatVariants.Add(new
                    {
                        id = ExtractLegacyIdFromGid((string)n.id),
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
            catch { }

            return new
            {
                product = new
                {
                    id = ExtractLegacyIdFromGid((string)p.id),
                    title = (string)p.title,
                    vendor = (string)p.vendor,
                    product_type = (string)p.productType,
                    tags = tagsCsv,
                    category_id = categoryId,
                    collections = flatCollections,
                    image_src = firstImageSrc,
                    options = NormalizeOptions(p.options),
                    variants = flatVariants
                }
            };
        }

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
