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

    }
}
