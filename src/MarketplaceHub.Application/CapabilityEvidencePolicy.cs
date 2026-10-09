using MarketplaceHub.Domain;

namespace MarketplaceHub.Application;

public static class CapabilityEvidencePolicy
{
    public static string OfficialDocumentationHost(string platformCode) => platformCode.Trim().ToUpperInvariant() switch
    {
        "TRENDYOL" => "developers.trendyol.com",
        "TRENDYOL_EFATURAM" => "developers.trendyolefaturam.com",
        "HEPSIBURADA" => "developers.hepsiburada.com",
        "SHOPIFY" => "shopify.dev",
        _ => throw new ArgumentOutOfRangeException(nameof(platformCode), "Unsupported platform capability evidence scope.")
    };

    public static bool RequiresStageFixtureChecksum(string capabilityCode) => capabilityCode.Trim().ToUpperInvariant() is
        MarketplaceCapabilities.ProductWrite or MarketplaceCapabilities.InventoryWrite or MarketplaceCapabilities.PriceWrite
        or MarketplaceCapabilities.ShipmentWrite or MarketplaceCapabilities.LabelWrite or MarketplaceCapabilities.ReturnWrite
        or InvoicingCapabilities.InvoiceSubmit or InvoicingCapabilities.InvoiceCancel or InvoicingCapabilities.InvoiceDeliver;

    public static bool IsVerifiedWriteCapability(PlatformCapability? capability, PlatformConnection connection, string capabilityCode)
    {
        if (capability is null
            || capability.TenantId != connection.TenantId
            || capability.ConnectionId != connection.Id
            || !string.Equals(capability.Code, capabilityCode, StringComparison.OrdinalIgnoreCase)
            || capability.SupportLevel != CapabilitySupportLevel.Supported
            || !string.Equals(capability.ApiVersion, connection.ApiVersion, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(capability.Environment, connection.Environment, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(capability.StoreScope, connection.ExternalStoreId, StringComparison.Ordinal)
            || capability.VerifiedAt is null
            || !Uri.TryCreate(capability.SourceUrl, UriKind.Absolute, out var source)
            || source.Scheme != Uri.UriSchemeHttps
            || !source.Host.Equals(OfficialDocumentationHost(connection.PlatformCode), StringComparison.OrdinalIgnoreCase)) return false;

        if (string.Equals(connection.PlatformCode, "SHOPIFY", StringComparison.OrdinalIgnoreCase)
            || !RequiresStageFixtureChecksum(capabilityCode)
            || string.Equals(connection.PlatformCode, "HEPSIBURADA", StringComparison.OrdinalIgnoreCase)
                && capabilityCode.Trim().ToUpperInvariant() is MarketplaceCapabilities.PriceWrite or MarketplaceCapabilities.InventoryWrite or MarketplaceCapabilities.ShipmentWrite or MarketplaceCapabilities.ReturnWrite) return true;
        var checksum = capability.FixtureChecksum;
        return checksum is { Length: 64 } && checksum.All(Uri.IsHexDigit);
    }
}
