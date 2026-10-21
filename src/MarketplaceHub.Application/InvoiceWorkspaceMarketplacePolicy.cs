namespace MarketplaceHub.Application;

public static class InvoiceWorkspaceMarketplacePolicy
{
    public static readonly string[] PlatformCodes = ["TRENDYOL", "SHOPIFY", "HEPSIBURADA", "N11", "PAZARAMA", "PTTAVM"];

    public static bool Supports(string? platformCode) =>
        platformCode is not null && PlatformCodes.Contains(platformCode, StringComparer.Ordinal);
}
