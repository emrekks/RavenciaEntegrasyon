namespace MarketplaceHub.Infrastructure.Adapters.Trendyol;

public sealed class TrendyolOptions
{
    public const string SectionName = "Trendyol";
    public Uri ProductionBaseAddress { get; init; } = new("https://apigw.trendyol.com/integration/");
    public Uri StageBaseAddress { get; init; } = new("https://stageapigw.trendyol.com/integration/");
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);
}

internal static class TrendyolEndpoints
{
    public static string ProductCreate(string sellerId) => $"product/sellers/{Uri.EscapeDataString(sellerId)}/v2/products";
    public static string ApprovedProducts(string sellerId) => $"product/sellers/{Uri.EscapeDataString(sellerId)}/products/approved";
    public static string UnapprovedProducts(string sellerId) => $"product/sellers/{Uri.EscapeDataString(sellerId)}/products/unapproved";
    public static string BatchResult(string sellerId, string batchId) => $"product/sellers/{Uri.EscapeDataString(sellerId)}/products/batch-requests/{Uri.EscapeDataString(batchId)}";
    public static string PriceAndInventory(string sellerId) => $"inventory/sellers/{Uri.EscapeDataString(sellerId)}/products/price-and-inventory";
    public static string OrderStream(string sellerId) => $"order/sellers/{Uri.EscapeDataString(sellerId)}/orders/stream";
    public static string Claims(string sellerId) => $"order/sellers/{Uri.EscapeDataString(sellerId)}/claims";
    public static string InvoiceLinks(string sellerId) => $"sellers/{Uri.EscapeDataString(sellerId)}/seller-invoice-links";
    public const string Categories = "product/product-categories";
    public const string Brands = "product/brands";
    public static string CategoryAttributes(string categoryId) => $"product/categories/{Uri.EscapeDataString(categoryId)}/attributes";
    public static string AttributeValues(string categoryId, string attributeId) => $"product/categories/{Uri.EscapeDataString(categoryId)}/attributes/{Uri.EscapeDataString(attributeId)}/values";
}
