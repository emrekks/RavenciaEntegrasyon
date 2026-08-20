namespace MarketplaceHub.Application;

public static class CapabilityEvidencePolicy
{
    public static string OfficialDocumentationHost(string platformCode) => platformCode.Trim().ToUpperInvariant() switch
    {
        "TRENDYOL" => "developers.trendyol.com",
        "TRENDYOL_EFATURAM" => "developers.trendyolefaturam.com",
        _ => throw new ArgumentOutOfRangeException(nameof(platformCode), "Unsupported platform capability evidence scope.")
    };

    public static bool RequiresStageFixtureChecksum(string capabilityCode) => capabilityCode.Trim().ToUpperInvariant() is
        MarketplaceCapabilities.ProductWrite or MarketplaceCapabilities.InventoryWrite or MarketplaceCapabilities.PriceWrite
        or MarketplaceCapabilities.ShipmentWrite or MarketplaceCapabilities.LabelWrite or MarketplaceCapabilities.ReturnWrite
        or InvoicingCapabilities.InvoiceSubmit or InvoicingCapabilities.InvoiceCancel or InvoicingCapabilities.InvoiceDeliver;
}
