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



    }
}
