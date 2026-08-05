using MarketplaceHub.Application;

namespace MarketplaceHub.Infrastructure.Adapters.Trendyol.Ports;

public static class TrendyolCapabilityCatalog
{
    public static IReadOnlyDictionary<string, string> OfficialSources { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [F3Capabilities.ConnectionTest] = "https://developers.trendyol.com/v2.0/docs/authorization",
        [F3Capabilities.ReferenceRead] = "https://developers.trendyol.com/v2.0/docs/trendyol-category-list-getcategorytree",
        [F3Capabilities.ProductRead] = "https://developers.trendyol.com/v2.0/docs/product-filtering-approved-products-v2",
        [F3Capabilities.ProductWrite] = "https://developers.trendyol.com/v2.0/docs/product-create-v2",
        [F3Capabilities.InventoryWrite] = "https://developers.trendyol.com/v2.0/docs/stock-and-price-update-updatepriceandinventory-1",
        [F3Capabilities.PriceWrite] = "https://developers.trendyol.com/v2.0/docs/stock-and-price-update-updatepriceandinventory-1",
        [F3Capabilities.OrderRead] = "https://developers.trendyol.com/v2.0/docs/getshipmentpackagesstream",
        [F3Capabilities.OrderWebhook] = "https://developers.trendyol.com/v2.0/docs/webhook-model",
        [F3Capabilities.ShipmentWrite] = "https://developers.trendyol.com/v3.0/docs/order-services-best-practices",
        [F3Capabilities.LabelRead] = "https://developers.trendyol.com/v2.0/docs/common-label-barcode-get-integration",
        [F3Capabilities.LabelWrite] = "https://developers.trendyol.com/v2.0/docs/common-label-barcode-request-createcommonlabel",
        [F3Capabilities.ReturnRead] = "https://developers.trendyol.com/v2.0/docs/getting-returned-orders-getclaims",
        [F3Capabilities.ReturnWrite] = "https://developers.trendyol.com/v2.0/reference/approveclaimlineitems"
    };
}
