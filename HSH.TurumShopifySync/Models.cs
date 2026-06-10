using System;
using System.Collections.Generic;

namespace HSH.TurumShopifySync
{
    public class ShopifySkuIndexes
    {
        public Dictionary<string, long> Active = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, long> Archived = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
    }

    // ==========================
    // TURUM DTOs
    // ==========================
    public sealed class TurumResponse
    {
        public List<TurumProduct> data { get; set; }
    }

    public sealed class TurumProduct
    {
        public string sku { get; set; }
        public string image { get; set; }
        public string name { get; set; }
        public decimal price { get; set; }
        public string brand { get; set; }
        public List<TurumVariant> variants { get; set; }
    }

    public sealed class TurumVariant
    {
        public string variant_id { get; set; }
        public string size { get; set; }
        public int stock { get; set; }
        public bool has_more { get; set; }
        public decimal price { get; set; }
        public string eu_size { get; set; }
        public string ean { get; set; }
    }

    // ==========================
    // Shopify Token Refresh logic
    // ==========================
}
