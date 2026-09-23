namespace MarketplaceHub.Application;

public static class ShopifyManualInvoicePolicy
{
    public const string TrackingSequencePurpose = "SHOPIFY_MANUAL";

    public static bool IsManualOnly(string? platformCode, string? sequencePurpose) =>
        string.Equals(platformCode, "SHOPIFY", StringComparison.OrdinalIgnoreCase)
        || string.Equals(sequencePurpose, TrackingSequencePurpose, StringComparison.OrdinalIgnoreCase);

    public static decimal TrackingAmount(decimal orderNetAmount, decimal? packageNetAmount) =>
        decimal.Round(packageNetAmount is > 0 ? packageNetAmount.Value : orderNetAmount, 2, MidpointRounding.AwayFromZero);

    public static bool CanSetUploaded(bool hasUploadedDocument) => hasUploadedDocument;
}
