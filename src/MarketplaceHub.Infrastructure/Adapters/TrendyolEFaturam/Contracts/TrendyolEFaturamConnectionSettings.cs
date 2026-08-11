namespace MarketplaceHub.Infrastructure.Adapters.TrendyolEFaturam.Contracts;

public sealed record TrendyolEFaturamCredentialPayload(
    string? PartnerEmail,
    string? PartnerPassword,
    string? CustomerEmail,
    string? CustomerPassword,
    string? CustomerTaxId);

public sealed record TrendyolEFaturamConnectionSettings(bool ExternalWritesEnabled);
