using MarketplaceHub.Domain;

namespace MarketplaceHub.Application;

// Trendyol's product ads report (an uploaded "Ürün Reklamları Raporum" export). Every figure is a campaign total since
// the campaign started, as of ExportedAt: never a report period's figure. Totals add the campaigns up: Spent and Revenue
// over the campaigns that write them, Roas = Revenue ÷ Spent over the campaigns that write both, Cpc = Spent ÷ Clicks,
// Ctr = Clicks ÷ Impressions (null when the divisor is 0 or unknown).
public sealed record AdReportTotals(decimal Spent, decimal Revenue, decimal DirectRevenue, decimal IndirectRevenue, decimal? Roas, long Clicks, long Impressions,
    decimal? Cpc, decimal? Ctr, decimal Sales, decimal DirectSales, decimal IndirectSales);

public sealed record AdReportView(Guid Id, Guid ConnectionId, string StoreName, string FileName, DateTimeOffset ExportedAt, bool ExportedAtFromFileName, DateTimeOffset UploadedAt,
    AdReportTotals Totals, IReadOnlyList<AdCampaignRow> Campaigns);

// A Trendyol store a report can be uploaded for.
public sealed record AdReportStoreView(Guid ConnectionId, string StoreName);

// The newest report of each Trendyol store, and the stores a report can be uploaded for.
public sealed record AdReportsView(IReadOnlyList<AdReportView> Reports, IReadOnlyList<AdReportStoreView> Stores);

public sealed record AdReportUploadResult(AdReportView Report, IReadOnlyList<string> Warnings);

public interface IAdReportService
{
    Task<ServiceResult<AdReportsView>> LatestAsync(Guid tenantId, CancellationToken cancellationToken);
    Task<ServiceResult<AdReportUploadResult>> UploadAsync(Guid tenantId, Guid? userId, Guid connectionId, string fileName, Stream content, string correlationId, CancellationToken cancellationToken);
}
