namespace MarketplaceHub.Infrastructure.Adapters.TrendyolEFaturam.Contracts;

public sealed record TrendyolEFaturamCredentialPayload(
    string? Email,
    string? Password,
    string? PartnerEmail = null,
    string? PartnerPassword = null,
    string? CustomerEmail = null,
    string? CustomerPassword = null,
    string? CustomerTaxId = null);

public sealed record EfaturamCarrierIdentity(string ProviderName, string TaxId, string LegalName);

public sealed record TrendyolEFaturamConnectionSettings(
    string IntegrationModel,
    bool ExternalWritesEnabled,
    long? CompanyId = null,
    long? UserId = null,
    string? Prefix = null,
    IReadOnlyList<EfaturamCarrierIdentity>? Carriers = null,
    string PurchaseUrl = "https://www.trendyol.com",
    string PaymentAgentName = "Trendyol",
    string PaymentType = "PAZARYERI",
    string PaymentMeans = "MEDIATOR",
    string EInvoiceType = "TEMELFATURA")
{
    public IReadOnlyList<EfaturamCarrierIdentity> ConfiguredCarriers => Carriers ?? [];
}
