using MarketplaceHub.Domain;

namespace MarketplaceHub.Application;

public static class InvoiceDeliveryEnvironmentPolicy
{
    public const string MismatchErrorCode = "INVOICE_DELIVERY_ENVIRONMENT_MISMATCH";

    public static bool HasProviderCredentialForMarketplace(
        string? marketplaceEnvironment,
        IReadOnlyList<string>? providerEnvironmentsWithCredential,
        bool legacyProviderHasCredential = false)
    {
        var marketplace = Normalize(marketplaceEnvironment);
        if (providerEnvironmentsWithCredential is null)
            return marketplace is not null && legacyProviderHasCredential;

        return marketplace is not null && providerEnvironmentsWithCredential
            .Select(Normalize)
            .Any(provider => string.Equals(provider, marketplace, StringComparison.Ordinal));
    }

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

public static class InvoiceDeliveryOperationPolicy
{
    public static AdapterContext ForAutomaticMarketplaceDelivery(AdapterContext context) =>
        context with { Operation = IntegrationOperation.Automatic };
}
