using System.Security.Cryptography;
using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Imports;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

// The Trendyol product ads reports the seller uploads: every upload is kept as a snapshot; the newest of each store is
// shown. Its figures are campaign totals as of the export, never matched to a report period (AdReportContracts).
public sealed class AdReportService(AppDbContext db, TimeProvider timeProvider) : IAdReportService
{
    public const long MaximumFileBytes = 10 * 1024 * 1024;
    private const long MaximumExpandedBytes = 64 * 1024 * 1024;
    private const string Trendyol = "TRENDYOL";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<ServiceResult<AdReportsView>> LatestAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var stores = await StoresAsync(tenantId, cancellationToken);
        var ids = stores.Select(store => store.ConnectionId).ToArray();
        // The newest snapshot of each store: picked from the narrow rows, then read whole.
        var heads = await db.AdReportFiles.AsNoTracking().Where(x => x.TenantId == tenantId && ids.Contains(x.ConnectionId))
            .Select(x => new { x.Id, x.ConnectionId, x.ExportedAt, x.UploadedAt }).ToListAsync(cancellationToken);
        var newest = heads.GroupBy(x => x.ConnectionId).Select(group => group.OrderByDescending(x => x.ExportedAt).ThenByDescending(x => x.UploadedAt).First().Id).ToArray();
        var files = await db.AdReportFiles.AsNoTracking().Where(x => x.TenantId == tenantId && newest.Contains(x.Id)).ToListAsync(cancellationToken);
        var names = stores.ToDictionary(store => store.ConnectionId, store => store.StoreName);
        return ServiceResult<AdReportsView>.Ok(new(files.OrderByDescending(x => x.ExportedAt).Select(file => View(file, names.GetValueOrDefault(file.ConnectionId) ?? Trendyol)).ToList(), stores));
    }

    public async Task<ServiceResult<AdReportUploadResult>> UploadAsync(Guid tenantId, Guid? userId, Guid connectionId, string fileName, Stream content, string correlationId, CancellationToken cancellationToken)
    {
        var connection = await db.PlatformConnections.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == connectionId && x.Status != "DELETED", cancellationToken);
        if (connection is null) return ServiceResult<AdReportUploadResult>.Fail("RESOURCE_NOT_FOUND", "Bağlantı bulunamadı.", 404);
        if (!string.Equals(connection.PlatformCode, Trendyol, StringComparison.OrdinalIgnoreCase))
            return ServiceResult<AdReportUploadResult>.Fail("AD_REPORT_PLATFORM_UNSUPPORTED", "Reklam raporu yalnız Trendyol mağazası için yüklenebilir.", 422);
        await using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken);
        if (buffer.Length is 0 or > MaximumFileBytes)
            return ServiceResult<AdReportUploadResult>.Fail("AD_REPORT_FILE_SIZE", "Dosya boş olamaz ve en fazla 10 MiB olabilir.", 413);
        ParsedAdReport parsed;
        try
        {
            buffer.Position = 0;
            parsed = TrendyolAdReportParser.Parse(ImportFileReader.ReadXlsxTable(buffer, MaximumExpandedBytes));
        }
        catch (Exception exception) when (exception is InvalidDataException or System.Xml.XmlException)
        {
            return ServiceResult<AdReportUploadResult>.Fail("AD_REPORT_FILE_INVALID", exception is InvalidDataException ? exception.Message : "Dosya okunamadı; Trendyol'dan indirilen .xlsx dosyasını yükleyin.", 422);
        }

        var now = timeProvider.GetUtcNow();
        var safeName = Path.GetFileName(fileName);
        if (safeName.Length > 260) safeName = safeName[..260];
        // The export's time from its name; a file renamed (or an export of the future) takes the upload time.
        var named = TrendyolAdReportParser.ExportedAt(safeName);
        var fromName = named is { } at && at <= now.AddMinutes(5);
        var file = new AdReportFile
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            ConnectionId = connectionId,
            PlatformCode = Trendyol,
            FileName = safeName,
            FileHash = Convert.ToHexString(SHA256.HashData(buffer.ToArray())),
            ExportedAt = fromName ? named!.Value : now,
            ExportedAtFromFileName = fromName,
            UploadedAt = now,
            UploadedBy = userId,
            CampaignCount = parsed.Campaigns.Count,
            CampaignsJson = JsonSerializer.Serialize(parsed.Campaigns, Json)
        };
        db.AdReportFiles.Add(file);
        db.AuditLogs.Add(new AuditLog { TenantId = tenantId, ActorUserId = userId, Action = "AD_REPORT_UPLOADED", TargetType = "PlatformConnection", TargetId = connectionId.ToString("D"), Reason = $"{safeName}: {parsed.Campaigns.Count} kampanya", CorrelationId = correlationId, CreatedAt = now });
        await db.SaveChangesAsync(cancellationToken);
        var warnings = fromName ? parsed.Warnings : [.. parsed.Warnings, "Dosya adında rapor zamanı yok; yükleme zamanı rapor zamanı sayıldı."];
        return ServiceResult<AdReportUploadResult>.Ok(new(View(file, connection.DisplayName), warnings));
    }

    internal static AdReportView View(AdReportFile file, string storeName)
    {
        var campaigns = JsonSerializer.Deserialize<List<AdCampaignRow>>(file.CampaignsJson, Json) ?? [];
        return new(file.Id, file.ConnectionId, storeName, file.FileName, file.ExportedAt, file.ExportedAtFromFileName, file.UploadedAt, Totals(campaigns), campaigns);
    }

    internal static AdReportTotals Totals(IReadOnlyList<AdCampaignRow> campaigns)
    {
        var spent = campaigns.Sum(x => x.Spent ?? 0m);
        var revenue = campaigns.Sum(x => x.TotalRevenue ?? 0m);
        var both = campaigns.Where(x => x.Spent is not null && x.TotalRevenue is not null).ToList();
        var bothSpent = both.Sum(x => x.Spent!.Value);
        var clicks = campaigns.Sum(x => x.Clicks ?? 0L);
        var impressions = campaigns.Sum(x => x.Impressions ?? 0L);
        var clickSpent = campaigns.Where(x => x.Spent is not null && x.Clicks is not null).ToList();
        var paidClicks = clickSpent.Sum(x => x.Clicks!.Value);
        var seen = campaigns.Where(x => x.Clicks is not null && x.Impressions is not null).ToList();
        var seenImpressions = seen.Sum(x => x.Impressions!.Value);
        return new(Math.Round(spent, 2), Math.Round(revenue, 2), Math.Round(campaigns.Sum(x => x.DirectRevenue ?? 0m), 2), Math.Round(campaigns.Sum(x => x.IndirectRevenue ?? 0m), 2),
            bothSpent > 0 ? Math.Round(both.Sum(x => x.TotalRevenue!.Value) / bothSpent, 2) : null, clicks, impressions,
            paidClicks > 0 ? Math.Round(clickSpent.Sum(x => x.Spent!.Value) / paidClicks, 2) : null,
            seenImpressions > 0 ? Math.Round((decimal)seen.Sum(x => x.Clicks!.Value) / seenImpressions, 4) : null,
            campaigns.Sum(x => x.TotalSales ?? 0m), campaigns.Sum(x => x.DirectSales ?? 0m), campaigns.Sum(x => x.IndirectSales ?? 0m));
    }

    // The tenant's Trendyol stores that are not test environments.
    private async Task<List<AdReportStoreView>> StoresAsync(Guid tenantId, CancellationToken cancellationToken) =>
        (await db.PlatformConnections.AsNoTracking().Where(x => x.TenantId == tenantId && x.Status != "DELETED" && x.PlatformCode.ToUpper() == Trendyol)
            .Select(x => new { x.Id, x.DisplayName, x.Environment }).ToListAsync(cancellationToken))
        .Where(x => !ReportBuckets.IsTestEnvironment(x.Environment)).OrderBy(x => x.DisplayName, StringComparer.Ordinal).Select(x => new AdReportStoreView(x.Id, x.DisplayName)).ToList();
}
