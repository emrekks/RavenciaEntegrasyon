namespace MarketplaceHub.Application;

public static class InvoiceWorkspaceMarketplacePolicy
{
    public static readonly string[] PlatformCodes = ["TRENDYOL", "SHOPIFY", "HEPSIBURADA", "N11", "PAZARAMA"];

    public static bool Supports(string? platformCode) =>
        platformCode is not null && PlatformCodes.Contains(platformCode, StringComparer.Ordinal);
}
