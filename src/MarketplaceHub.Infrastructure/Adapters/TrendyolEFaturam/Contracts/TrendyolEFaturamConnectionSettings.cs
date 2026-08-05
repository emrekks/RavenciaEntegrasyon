namespace MarketplaceHub.Infrastructure.Adapters.TrendyolEFaturam.Contracts;

public sealed record TrendyolEFaturamCredentialPayload(string? Email, string? Password);

public sealed record TrendyolEFaturamConnectionSettings(bool ExternalWritesEnabled);
