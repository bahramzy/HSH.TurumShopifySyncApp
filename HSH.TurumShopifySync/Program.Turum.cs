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

    }
}
