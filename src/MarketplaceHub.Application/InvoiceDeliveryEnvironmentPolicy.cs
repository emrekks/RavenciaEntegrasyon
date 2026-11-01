namespace MarketplaceHub.Application;

public static class InvoiceDeliveryEnvironmentPolicy
{
    public const string MismatchErrorCode = "INVOICE_DELIVERY_ENVIRONMENT_MISMATCH";

    public static bool IsCompatible(string? invoiceProviderEnvironment, string? marketplaceEnvironment)
    {
        var provider = Normalize(invoiceProviderEnvironment);
        var marketplace = Normalize(marketplaceEnvironment);
        return provider is not null && marketplace is not null
            && string.Equals(provider, marketplace, StringComparison.Ordinal);
    }

    public static string DescribeMismatch(string? invoiceProviderEnvironment, string? marketplaceEnvironment)
    {
        var provider = Normalize(invoiceProviderEnvironment) ?? "bilinmiyor";
        var marketplace = Normalize(marketplaceEnvironment) ?? "bilinmiyor";
        return $"Fatura {provider} e-Fatura ortamında oluşturulmuş, sipariş ise {marketplace} pazaryeri ortamında. Fatura yalnız aynı ortamdaki pazaryeri siparişine iletilebilir.";
    }

    private static string? Normalize(string? environment)
    {
        var normalized = environment?.Trim().ToUpperInvariant();
        return normalized is "STAGE" or "PRODUCTION" ? normalized : null;
    }
}
