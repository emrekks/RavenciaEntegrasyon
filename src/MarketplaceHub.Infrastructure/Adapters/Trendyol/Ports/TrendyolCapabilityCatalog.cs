using MarketplaceHub.Application;

namespace MarketplaceHub.Infrastructure.Adapters.Trendyol.Ports;

public static class TrendyolCapabilityCatalog
{
    public static IReadOnlyDictionary<string, string> OfficialSources { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [MarketplaceCapabilities.ConnectionTest] = "https://developers.trendyol.com/v2.0/docs/authorization",
        [MarketplaceCapabilities.ReferenceRead] = "https://developers.trendyol.com/v2.0/docs/trendyol-category-list-getcategorytree",
        [MarketplaceCapabilities.ProductRead] = "https://developers.trendyol.com/v2.0/docs/product-filtering-approved-products-v2",
        [MarketplaceCapabilities.ProductWrite] = "https://developers.trendyol.com/v2.0/docs/product-create-v2",
        [MarketplaceCapabilities.InventoryWrite] = "https://developers.trendyol.com/v2.0/docs/stock-and-price-update-updatepriceandinventory-1",
        [MarketplaceCapabilities.PriceWrite] = "https://developers.trendyol.com/v2.0/docs/stock-and-price-update-updatepriceandinventory-1",
        [MarketplaceCapabilities.OrderRead] = "https://developers.trendyol.com/v2.0/docs/getshipmentpackagesstream",
        [MarketplaceCapabilities.OrderWebhook] = "https://developers.trendyol.com/v2.0/docs/webhook-model",
        [MarketplaceCapabilities.ShipmentWrite] = "https://developers.trendyol.com/v3.0/docs/order-services-best-practices",
        [MarketplaceCapabilities.LabelRead] = "https://developers.trendyol.com/v2.0/docs/common-label-barcode-get-integration",
        [MarketplaceCapabilities.LabelWrite] = "https://developers.trendyol.com/v2.0/docs/common-label-barcode-request-createcommonlabel",
        [MarketplaceCapabilities.ReturnRead] = "https://developers.trendyol.com/v2.0/docs/getting-returned-orders-getclaims",
        [MarketplaceCapabilities.ReturnWrite] = "https://developers.trendyol.com/v2.0/reference/approveclaimlineitems"
    };
}
