namespace MarketplaceHub.Infrastructure.Adapters.TrendyolEFaturam.Contracts;

public sealed record TrendyolEFaturamCredentialPayload(
    string? Email,
    string? Password,
    string? CustomerEmail,
    string? CustomerPassword,
    string? CustomerTaxId);

public sealed record TrendyolEFaturamConnectionSettings(bool ExternalWritesEnabled);
