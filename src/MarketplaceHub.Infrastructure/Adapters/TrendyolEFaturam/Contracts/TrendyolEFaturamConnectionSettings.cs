namespace MarketplaceHub.Infrastructure.Adapters.TrendyolEFaturam.Contracts;

public sealed record TrendyolEFaturamCredentialPayload(
    string? Email,
    string? Password)
{
    // The generated ToString would print the sign-in e-mail and password (also through the request context).
    public override string ToString() => nameof(TrendyolEFaturamCredentialPayload);
}

public sealed record TrendyolEFaturamConnectionSettings(bool ExternalWritesEnabled);
