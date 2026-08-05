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
        F3Capabilities.ProductWrite or F3Capabilities.InventoryWrite or F3Capabilities.PriceWrite
        or F3Capabilities.ShipmentWrite or F3Capabilities.LabelWrite or F3Capabilities.ReturnWrite
        or F4Capabilities.InvoiceSubmit or F4Capabilities.InvoiceCancel or F4Capabilities.InvoiceDeliver;
}
