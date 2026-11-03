using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

// A scheduled read that ended blocked or dead (P9 of the 1 Nov 2026 review: 107 Pazarama order reads of 19 Oct still
// counted as errors for 30 days) is closed once a later run of the same read on the same connection succeeded: the
// later run read the same data again, so the old failure no longer stands for anything. It becomes CANCELLED with the
// code SUPERSEDED_BY_LATER_RUN. Only untargeted scheduled reads qualify (the scheduler's "scheduled:" key, a read job
// type from the list below, the same key series without its time bucket); a write, a user request, or a targeted read
// (one order, one package) is never touched.
public static class ScheduledReadSupersession
{
    public const string Code = "SUPERSEDED_BY_LATER_RUN";
    public const string ScheduledPrefix = "scheduled:";

    internal static readonly IReadOnlySet<string> ReadJobTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        MarketplaceJobTypes.OrderSync, MarketplaceJobTypes.ShopifyOrderSync, MarketplaceJobTypes.HepsiburadaOrderSync, MarketplaceJobTypes.N11OrderSync, MarketplaceJobTypes.PazaramaOrderSync, MarketplaceJobTypes.PttAvmOrderSync,
        MarketplaceJobTypes.OrderRecoverySync, MarketplaceJobTypes.ShopifyOrderRecoverySync, MarketplaceJobTypes.HepsiburadaOrderRecoverySync, MarketplaceJobTypes.N11OrderRecoverySync, MarketplaceJobTypes.PazaramaOrderRecoverySync, MarketplaceJobTypes.PttAvmOrderRecoverySync,
        MarketplaceJobTypes.OrderStatusSync, MarketplaceJobTypes.ShopifyOrderStatusSync, MarketplaceJobTypes.HepsiburadaOrderStatusSync, MarketplaceJobTypes.PazaramaOrderStatusSync, MarketplaceJobTypes.PttAvmOrderStatusSync,
        MarketplaceJobTypes.TrendyolOrderCargoInfoReconciliation, MarketplaceJobTypes.OrderReconciliation, MarketplaceJobTypes.ShopifyOrderReconciliation,
        MarketplaceJobTypes.OrderInvoiceReconciliation, MarketplaceJobTypes.ShopifyOrderInvoiceReconciliation, MarketplaceJobTypes.HepsiburadaOrderInvoiceReconciliation,
        MarketplaceJobTypes.ReturnSync, MarketplaceJobTypes.HepsiburadaReturnSync, MarketplaceJobTypes.ReturnStatusSync, MarketplaceJobTypes.ReturnReconciliation,
        MarketplaceJobTypes.ReferenceSync, MarketplaceJobTypes.HepsiburadaReferenceSync, MarketplaceJobTypes.N11ReferenceSync, MarketplaceJobTypes.PazaramaReferenceSync, MarketplaceJobTypes.PttAvmReferenceSync,
        MarketplaceJobTypes.QuestionSync, MarketplaceJobTypes.TrendyolFinanceSync, MarketplaceJobTypes.PazaramaFinanceSync, MarketplaceJobTypes.N11SettlementSync, MarketplaceJobTypes.HepsiburadaFinanceSync
    };

    public static bool IsScheduledRead(string jobType, string dedupKey) =>
        dedupKey.StartsWith(ScheduledPrefix, StringComparison.Ordinal) && ReadJobTypes.Contains(jobType) && dedupKey.LastIndexOf(':') > ScheduledPrefix.Length;

    // The key without its time bucket: "scheduled:orders:{connection}:{bucket}" → "scheduled:orders:{connection}".
    public static string Series(string dedupKey) => dedupKey[..dedupKey.LastIndexOf(':')];

    public static bool Supersedes(
        (Guid ConnectionId, string JobType, string DedupKey, JobStatus Status, DateTimeOffset CreatedAt) failed,
        (Guid ConnectionId, string JobType, string DedupKey, JobStatus Status, DateTimeOffset CreatedAt) later) =>
        failed.Status is JobStatus.Blocked or JobStatus.Dead
        && later.Status == JobStatus.Succeeded
        && failed.ConnectionId == later.ConnectionId
        && string.Equals(failed.JobType, later.JobType, StringComparison.Ordinal)
        && IsScheduledRead(failed.JobType, failed.DedupKey) && IsScheduledRead(later.JobType, later.DedupKey)
        && string.Equals(Series(failed.DedupKey), Series(later.DedupKey), StringComparison.Ordinal)
        && later.CreatedAt > failed.CreatedAt;

    internal static string Summary(string? previousCode) =>
        $"Aynı bağlantıda aynı okumanın sonraki zamanlanmış çalışması başarılı oldu; bu eski hata kaydı kapatıldı (önceki hata: {previousCode ?? "—"}).";

    // Closes the superseded failures; returns how many. Each pass looks at a bounded number of failures.
    internal static async Task<int> CloseAsync(AppDbContext db, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var types = ReadJobTypes.ToArray();
        var failed = (await db.IntegrationJobs.AsNoTracking()
                .Where(x => (x.Status == JobStatus.Blocked || x.Status == JobStatus.Dead) && x.ConnectionId != null
                    && x.JobDedupKey.StartsWith(ScheduledPrefix) && types.Contains(x.JobType))
                .OrderBy(x => x.CreatedAt)
                .Select(x => new { x.Id, x.TenantId, ConnectionId = x.ConnectionId!.Value, x.JobType, x.JobDedupKey, x.Status, x.CreatedAt, x.LastErrorCode })
                .Take(2000)
                .ToListAsync(cancellationToken))
            .Where(x => IsScheduledRead(x.JobType, x.JobDedupKey))
            .ToList();
        var closed = 0;
        foreach (var series in failed.GroupBy(x => (x.TenantId, x.ConnectionId, x.JobType, Series: Series(x.JobDedupKey))))
        {
            var prefix = series.Key.Series + ":";
            var successes = await db.IntegrationJobs.AsNoTracking()
                .Where(x => x.TenantId == series.Key.TenantId && x.ConnectionId == series.Key.ConnectionId && x.JobType == series.Key.JobType
                    && x.Status == JobStatus.Succeeded && x.JobDedupKey.StartsWith(prefix))
                .OrderByDescending(x => x.CreatedAt)
                .Select(x => new { x.JobDedupKey, x.CreatedAt })
                .Take(20)
                .ToListAsync(cancellationToken);
            var latest = successes.FirstOrDefault(x => IsScheduledRead(series.Key.JobType, x.JobDedupKey) && Series(x.JobDedupKey) == series.Key.Series);
            if (latest is null) continue;
            foreach (var job in series.Where(x => Supersedes(
                         (x.ConnectionId, x.JobType, x.JobDedupKey, x.Status, x.CreatedAt),
                         (series.Key.ConnectionId, series.Key.JobType, latest.JobDedupKey, JobStatus.Succeeded, latest.CreatedAt))))
            {
                var summary = Summary(job.LastErrorCode);
                closed += await db.IntegrationJobs
                    .Where(x => x.Id == job.Id && x.TenantId == job.TenantId && (x.Status == JobStatus.Blocked || x.Status == JobStatus.Dead))
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(x => x.Status, JobStatus.Cancelled)
                        .SetProperty(x => x.LastErrorCode, Code)
                        .SetProperty(x => x.LastErrorSummary, summary)
                        .SetProperty(x => x.CompletedAt, x => x.CompletedAt ?? now)
                        .SetProperty(x => x.Version, x => x.Version + 1), cancellationToken);
            }
        }
        return closed;
    }
}
