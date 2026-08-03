namespace MarketplaceHub.Infrastructure.Adapters.TrendyolEFaturam.Contracts;

public sealed record EfaturamCarrierIdentity(string ProviderName, string TaxId, string LegalName);

public sealed record TrendyolEFaturamConnectionSettings(
    string IntegrationModel,
    bool ExternalWritesEnabled,
    long? CompanyId = null,
    long? UserId = null,
    string? Prefix = null,
    IReadOnlyList<EfaturamCarrierIdentity>? Carriers = null)
{
    public IReadOnlyList<EfaturamCarrierIdentity> ConfiguredCarriers => Carriers ?? [];
}
