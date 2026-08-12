namespace MarketplaceHub.Infrastructure.Adapters.TrendyolEFaturam;

public sealed class TrendyolEFaturamOptions
{
    public const string SectionName = "TrendyolEFaturam";
    public Uri StageBaseAddress { get; init; } = new("https://stage-apigateway.trendyolefaturam.com/");
    public Uri ProductionBaseAddress { get; init; } = new("https://apigateway.trendyolecozum.com/");
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);
    public string[] DocumentAllowedHosts { get; init; } = [];
    // Exact outgoing E-Invoice status/search path is provider endpoint configuration. It
    // intentionally defaults to null so no environment guesses an undocumented endpoint.
    public string? OutgoingInvoiceStatusPath { get; init; }
}

internal static class TrendyolEFaturamEndpoints
{
    public const string SignIn = "api/auth/signin";
    public const string CreateOutgoingInvoice = "api/invoice/documents/outgoing-einvoice";
    public const string CreateEArchive = "api/invoice/documents/earchive";
    public const string PermanentDocumentUrl = "api/invoice/documents/download/permanent-url";
    public const string CancelEArchive = "api/invoice/documents/earchive/cancel";
    public static string EArchiveStatus(string uuid) => $"api/invoice/documents/earchive/status/{Uri.EscapeDataString(uuid)}";
}
