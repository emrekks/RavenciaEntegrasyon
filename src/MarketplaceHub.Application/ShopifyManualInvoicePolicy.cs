namespace MarketplaceHub.Application;

public static class ShopifyManualInvoicePolicy
{
    public const string TrackingSequencePurpose = "SHOPIFY_MANUAL";

    public static bool IsManualOnly(string? platformCode, string? sequencePurpose) =>
        string.Equals(platformCode, "SHOPIFY", StringComparison.OrdinalIgnoreCase)
        || string.Equals(sequencePurpose, TrackingSequencePurpose, StringComparison.OrdinalIgnoreCase);
}
