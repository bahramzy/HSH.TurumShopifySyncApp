using System;
using System.Globalization;

namespace HSH.TurumShopifySync
{
    public sealed class SyncSettings
    {
        public string ShopifyStoreDomain { get; set; }
        public string ShopifyApiVersion { get; set; }
        public string ShopifySneakersCategoryId { get; set; }
        public decimal EurToDkkRate { get; set; }
        public decimal MomsRate { get; set; }
        public decimal Profit { get; set; }
        public string ShopifyClientId { get; set; }
        public string ShopifyClientSecret { get; set; }
        public string ShopifyAdminToken { get; set; }
        public string TurumUsername { get; set; }
        public string TurumPassword { get; set; }
        public string TurumToken { get; set; }

        public static SyncSettings LoadFromEnvironment()
        {
            return new SyncSettings
            {
                ShopifyStoreDomain = GetString("SHOPIFY_STORE_DOMAIN", "highstreet-heaven-2.myshopify.com"),
                ShopifyApiVersion = GetString("SHOPIFY_API_VERSION", "2026-01"),
                ShopifySneakersCategoryId = GetString("SHOPIFY_SNEAKERS_CATEGORY_ID", "gid://shopify/TaxonomyCategory/aa-8-8"),
                EurToDkkRate = GetDecimal("EUR_TO_DKK_RATE", 7.47m),
                MomsRate = GetDecimal("MOMS_RATE", 1.25m),
                Profit = GetDecimal("PROFIT_DKK", 375m),
                ShopifyClientId = Environment.GetEnvironmentVariable("SHOPIFY_CLIENT_ID"),
                ShopifyClientSecret = Environment.GetEnvironmentVariable("SHOPIFY_CLIENT_SECRET"),
                ShopifyAdminToken = Environment.GetEnvironmentVariable("SHOPIFY_ADMIN_TOKEN"),
                TurumUsername = Environment.GetEnvironmentVariable("TURUM_USERNAME"),
                TurumPassword = Environment.GetEnvironmentVariable("TURUM_PASSWORD"),
                TurumToken = Environment.GetEnvironmentVariable("TURUM_TOKEN")
            };
        }

        private static string GetString(string name, string defaultValue)
        {
            var value = Environment.GetEnvironmentVariable(name);
            return string.IsNullOrWhiteSpace(value) ? defaultValue : value.Trim();
        }

        private static decimal GetDecimal(string name, decimal defaultValue)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrWhiteSpace(value))
                return defaultValue;

            decimal parsed;
            return decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out parsed)
                ? parsed
                : defaultValue;
        }
    }
}
