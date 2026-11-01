namespace MarketplaceHub.Infrastructure.Adapters.TrendyolEFaturam;

public sealed class TrendyolEFaturamOptions
{
    public const string SectionName = "TrendyolEFaturam";
    // Direct API_USER accounts use the documented Stage gateway. The portal BFF is
    // a private browser surface and must not be used by server integrations.
    public Uri StageBaseAddress { get; init; } = new("https://stage-apigateway.trendyolefaturam.com/");
    public Uri ProductionBaseAddress { get; init; } = new("https://apigateway.trendyolecozum.com/");
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);
    public string[] DocumentAllowedHosts { get; init; } = [];
}

internal static class TrendyolEFaturamEndpoints
{
    public const string SignIn = "api/auth/signin";
    public const string CreateOutgoingInvoice = "api/invoice/documents/outgoing-einvoice";
    public const string CreateEArchive = "api/invoice/documents/earchive";
    public const string PermanentDocumentUrl = "api/invoice/documents/download/permanent-url";
    public const string CancelEArchive = "api/invoice/documents/earchive/cancel";
    public static string EArchiveStatus(string uuid) => $"api/invoice/documents/earchive/status/{Uri.EscapeDataString(uuid)}";
    public static string OutgoingInvoiceStatus(string uuid) => $"api/invoice/documents/outgoing-einvoice/status/{Uri.EscapeDataString(uuid)}";
    // "getTaxPayersByTaxId" (developers.trendyolefaturam.com/OpenApi/Mükellef Sorgulama): 200 lists the aliases, 404 tax-payer-not-found.
    public static string TaxPayers(string taxId) => $"api/invoice/taxpayers/{Uri.EscapeDataString(taxId)}?showDeleted=false";
    public static string Status(string? invoiceType, string uuid) => invoiceType == "EARSIVFATURA" ? EArchiveStatus(uuid) : OutgoingInvoiceStatus(uuid);
}
