namespace MarketplaceHub.Domain;

// Trendyol's "Ürün Reklamları Raporum" export as the seller uploaded it: one snapshot per upload, its campaigns kept as
// JSON. Its figures are each campaign's totals since it started, as of the export (ExportedAt, from the file's name, else
// the upload time): there is no per-day breakdown, so they are never matched to a report period. Nothing is sent to the
// marketplace.
public sealed class AdReportFile
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid ConnectionId { get; set; }
    public required string PlatformCode { get; set; }
    public required string FileName { get; set; }
    public required string FileHash { get; set; }
    public DateTimeOffset ExportedAt { get; set; }
    // Whether ExportedAt was read from the file's name (true) or is the upload time (false).
    public bool ExportedAtFromFileName { get; set; }
    public DateTimeOffset UploadedAt { get; set; }
    public Guid? UploadedBy { get; set; }
    public int CampaignCount { get; set; }
    public required string CampaignsJson { get; set; }
}

// One campaign row of the export, every figure as the file writes it (null where it writes "-" or nothing). Start and End
// are the file's Istanbul local times as text ("2026-11-02 17:20"); CpcBid is the bid as text (it can be a list of ranges).
public sealed record AdCampaignRow(string Name, string? Status, string? Start, string? End, int? ProductCount, decimal? TotalBudget, decimal? DailyBudget,
    decimal? RemainingBudget, decimal? Spent, string? CpcBid, decimal? ActualCpc, long? Clicks, long? Impressions, decimal? DirectSales, decimal? IndirectSales,
    decimal? TotalSales, decimal? DirectRevenue, decimal? IndirectRevenue, decimal? TotalRevenue, decimal? Roas);
