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
        private static async Task ReplaceProductImagesAsync(HttpClient shopify, long productId, string imageUrl, string alt, dynamic existingProductDoc, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(imageUrl))
            {
                Console.WriteLine("SKIP image update (no image url). Archiving product " + productId);
                try
                {
                    await SetShopifyProductStatusGraphQlAsync(shopify, productId, "ARCHIVED", ct);
                    Console.WriteLine("[INFO] Archived product " + productId + " due to missing image.");
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[WARN] Failed to archive product " + productId + ": " + ex.Message);
                }

                return;
            }

            imageUrl = imageUrl.Trim();

            var currentImageUrl = GetCurrentProductImageUrl(existingProductDoc);
            if (ImageUrlsLikelySame(currentImageUrl, imageUrl))
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(currentImageUrl) && !IsKnownMissingImageUrl(imageUrl))
            {
                return;
            }

            var ok = await IsValidImageUrlAsync(imageUrl, ct);
            if (!ok)
            {
                Console.WriteLine("SKIP image update (invalid image url). Archiving product " + productId + " url " + imageUrl);
                try
                {
                    await SetShopifyProductStatusGraphQlAsync(shopify, productId, "ARCHIVED", ct);
                    Console.WriteLine("[INFO] Archived product " + productId + " due to invalid image URL.");
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[WARN] Failed to archive product " + productId + ": " + ex.Message);
                }

                return;
            }

            try
            {
                await ReplaceProductImagesGraphQlAsync(shopify, productId, imageUrl, alt, ct);
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

        private static string GetCurrentProductImageUrl(dynamic productDoc)
        {
            try
            {
                return (string)(productDoc?.product?.image_src ?? "");
            }
            catch
            {
                return "";
            }
        }

        private static bool ImageUrlsLikelySame(string currentImageUrl, string sourceImageUrl)
        {
            if (string.IsNullOrWhiteSpace(currentImageUrl) || string.IsNullOrWhiteSpace(sourceImageUrl))
                return false;

            if (string.Equals(currentImageUrl.Trim(), sourceImageUrl.Trim(), StringComparison.OrdinalIgnoreCase))
                return true;

            var currentFileName = NormalizeImageFileName(currentImageUrl);
            var sourceFileName = NormalizeImageFileName(sourceImageUrl);

            return currentFileName.Length > 0 &&
                   sourceFileName.Length > 0 &&
                   string.Equals(currentFileName, sourceFileName, StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeImageFileName(string imageUrl)
        {
            if (string.IsNullOrWhiteSpace(imageUrl))
                return "";

            Uri uri;
            if (!Uri.TryCreate(imageUrl.Trim(), UriKind.Absolute, out uri))
                return "";

            var fileName = Path.GetFileName(uri.LocalPath);
            if (string.IsNullOrWhiteSpace(fileName))
                return "";

            fileName = WebUtility.UrlDecode(fileName).Trim();
            fileName = Regex.Replace(fileName, @"\s+", " ");

            return fileName;
        }

        private static bool IsKnownMissingImageUrl(string imageUrl)
        {
            return !string.IsNullOrWhiteSpace(imageUrl) &&
                   imageUrl.IndexOf("not_found", StringComparison.OrdinalIgnoreCase) >= 0;
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
