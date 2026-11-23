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
// A read that went to manual review for a passing cause is closed the same way (audit 2026-11-06 O1: 7 Pazarama reads
// on a refused token of 4 Nov and 24 Trendyol return reads on an unreadable answer of 5 Nov stayed in review although
// the next runs read the same data): only the codes below qualify, never a review a person has to settle.
public static class ScheduledReadSupersession
{
    public const string Code = "SUPERSEDED_BY_LATER_RUN";
    public const string ScheduledPrefix = "scheduled:";

    // Manual-review codes of a read that the next successful run of the same read makes moot: a token the marketplace
    // refused for a while, an answer that could not be read once.
    internal static readonly IReadOnlySet<string> TransientReviewCodes = new HashSet<string>(StringComparer.Ordinal)
    {
        "PAZARAMA_TOKEN_INVALID", "REMOTE_CONTRACT_INVALID"
    };

    public static bool IsClosableFailure(JobStatus status, string? errorCode) =>
        status is JobStatus.Blocked or JobStatus.Dead
        || status == JobStatus.ManualReview && errorCode is not null && TransientReviewCodes.Contains(errorCode);

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
        (Guid ConnectionId, string JobType, string DedupKey, JobStatus Status, DateTimeOffset CreatedAt) later,
        string? failedErrorCode = null) =>
        IsClosableFailure(failed.Status, failedErrorCode)
        && later.Status == JobStatus.Succeeded
        && failed.ConnectionId == later.ConnectionId
        && string.Equals(failed.JobType, later.JobType, StringComparison.Ordinal)
        && IsScheduledRead(failed.JobType, failed.DedupKey) && IsScheduledRead(later.JobType, later.DedupKey)
        && string.Equals(Series(failed.DedupKey), Series(later.DedupKey), StringComparison.Ordinal)
        && later.CreatedAt > failed.CreatedAt;

    internal const string Summary =
        "Aynı mağazadaki aynı okumanın sonraki çalışması başarılı oldu; bu eski hata kaydı kapatıldı.";

    // Scan 23 Nov 2026: a read the user asked for (not a scheduled one) that stopped in manual review because the
    // marketplace refused the store's credentials waits there for ever once the credentials work again (3 Pazarama
    // question reads of 17 Nov, every later question read of the store succeeded). It is closed when a later run of the
    // same read type on the same connection succeeded: the refusal was the store's, and that run got through. Only the
    // credential codes below qualify; the read is closed, not run again (the scheduled reads cover its data).
    internal static readonly IReadOnlySet<string> CredentialReviewCodes = new HashSet<string>(StringComparer.Ordinal) { "PAZARAMA_TOKEN_INVALID" };

    internal const string CredentialsRecoveredSummary =
        "Mağazanın kimlik bilgisi sonradan çalıştı (aynı okumanın sonraki çalışması başarılı); bu okuma kapatıldı, yeniden çalıştırılmadı.";

    public static bool CredentialsRecovered(
        (Guid ConnectionId, string JobType, JobStatus Status, string? ErrorCode, DateTimeOffset CreatedAt) failed,
        (Guid ConnectionId, string JobType, JobStatus Status, DateTimeOffset CreatedAt) later) =>
        failed.Status == JobStatus.ManualReview && failed.ErrorCode is not null && CredentialReviewCodes.Contains(failed.ErrorCode)
        && ReadJobTypes.Contains(failed.JobType)
        && later.Status == JobStatus.Succeeded
        && failed.ConnectionId == later.ConnectionId
        && string.Equals(failed.JobType, later.JobType, StringComparison.Ordinal)
        && later.CreatedAt > failed.CreatedAt;

    // Closes the reads CredentialsRecovered names; returns how many.
    internal static async Task<int> CloseCredentialReviewsAsync(AppDbContext db, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var types = ReadJobTypes.ToArray();
        var codes = CredentialReviewCodes.ToArray();
        var waiting = await db.IntegrationJobs.AsNoTracking()
            .Where(x => x.Status == JobStatus.ManualReview && x.LastErrorCode != null && codes.Contains(x.LastErrorCode)
                && x.ConnectionId != null && types.Contains(x.JobType))
            .OrderBy(x => x.CreatedAt)
            .Select(x => new { x.Id, x.TenantId, ConnectionId = x.ConnectionId!.Value, x.JobType, x.Status, x.LastErrorCode, x.CreatedAt })
            .Take(500)
            .ToListAsync(cancellationToken);
        var closed = 0;
        var byTenant = new Dictionary<Guid, int>();
        foreach (var group in waiting.GroupBy(x => (x.TenantId, x.ConnectionId, x.JobType)))
        {
            var latest = await db.IntegrationJobs.AsNoTracking()
                .Where(x => x.TenantId == group.Key.TenantId && x.ConnectionId == group.Key.ConnectionId && x.JobType == group.Key.JobType && x.Status == JobStatus.Succeeded)
                .OrderByDescending(x => x.CreatedAt)
                .Select(x => (DateTimeOffset?)x.CreatedAt)
                .FirstOrDefaultAsync(cancellationToken);
            if (latest is null) continue;
            foreach (var job in group.Where(x => CredentialsRecovered(
                         (x.ConnectionId, x.JobType, x.Status, x.LastErrorCode, x.CreatedAt),
                         (group.Key.ConnectionId, group.Key.JobType, JobStatus.Succeeded, latest.Value))))
            {
                var one = await Jobs.JobStateMachine.CancelWhereAsync(
                    db.IntegrationJobs.Where(x => x.Id == job.Id && x.TenantId == job.TenantId && x.Status == job.Status && x.LastErrorCode == job.LastErrorCode),
                    now, Code, CredentialsRecoveredSummary, keepEarlierCompletedAt: true, cancellationToken);
                closed += one;
                byTenant[job.TenantId] = byTenant.GetValueOrDefault(job.TenantId) + one;
            }
        }
        await Jobs.JobStateMachine.PublishBulkAsync(db, byTenant, now, JobStatus.Cancelled, cancellationToken);
        return closed;
    }

    // Closes the superseded failures; returns how many. Each pass looks at a bounded number of failures.
    internal static async Task<int> CloseAsync(AppDbContext db, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var types = ReadJobTypes.ToArray();
        var reviewCodes = TransientReviewCodes.ToArray();
        var failed = (await db.IntegrationJobs.AsNoTracking()
                .Where(x => (x.Status == JobStatus.Blocked || x.Status == JobStatus.Dead
                        || x.Status == JobStatus.ManualReview && x.LastErrorCode != null && reviewCodes.Contains(x.LastErrorCode))
                    && x.ConnectionId != null
                    && x.JobDedupKey.StartsWith(ScheduledPrefix) && types.Contains(x.JobType))
                .OrderBy(x => x.CreatedAt)
                .Select(x => new { x.Id, x.TenantId, ConnectionId = x.ConnectionId!.Value, x.JobType, x.JobDedupKey, x.Status, x.CreatedAt, x.LastErrorCode })
                .Take(2000)
                .ToListAsync(cancellationToken))
            .Where(x => IsScheduledRead(x.JobType, x.JobDedupKey))
            .ToList();
        var closed = 0;
        var byTenant = new Dictionary<Guid, int>();
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
                         (series.Key.ConnectionId, series.Key.JobType, latest.JobDedupKey, JobStatus.Succeeded, latest.CreatedAt), x.LastErrorCode)))
            {
                var summary = Summary;
                var one = await Jobs.JobStateMachine.CancelWhereAsync(
                    db.IntegrationJobs.Where(x => x.Id == job.Id && x.TenantId == job.TenantId && x.Status == job.Status && x.LastErrorCode == job.LastErrorCode),
                    now, Code, summary, keepEarlierCompletedAt: true, cancellationToken);
                closed += one;
                byTenant[job.TenantId] = byTenant.GetValueOrDefault(job.TenantId) + one;
            }
        }
        // K2: one "jobs" event per tenant for the whole sweep, not one per closed job.
        await Jobs.JobStateMachine.PublishBulkAsync(db, byTenant, now, JobStatus.Cancelled, cancellationToken);
        return closed;
    }
}
