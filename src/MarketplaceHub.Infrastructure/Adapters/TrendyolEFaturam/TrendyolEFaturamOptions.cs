namespace MarketplaceHub.Infrastructure.Adapters.TrendyolEFaturam;

public sealed class TrendyolEFaturamOptions
{
    public const string SectionName = "TrendyolEFaturam";
    public Uri StageBaseAddress { get; init; } = new("https://stage-apigateway.trendyolefaturam.com/");
    public Uri ProductionBaseAddress { get; init; } = new("https://apigateway.trendyolecozum.com/");
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);
    public string[] DocumentAllowedHosts { get; init; } = [];
}

internal static class TrendyolEFaturamEndpoints
{
    public const string SignIn = "api/auth/signin";
    public const string CreateOutgoingInvoice = "api/invoice/documents/outgoing-einvoice";
    public const string PermanentDocumentUrl = "api/invoice/documents/download/permanent-url";
}
