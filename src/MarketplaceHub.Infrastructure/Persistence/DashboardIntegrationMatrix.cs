using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

// The dashboard's integration matrix: one row per marketplace connection, one cell per kind of data, each saying
// when that data last synced for that connection and whether the latest attempts failed. Read-only.
public static class DashboardIntegrationMatrix
{
    // As far back as succeeded jobs are kept (Worker:JobRetention:SucceededDays), so a flow silent for a week still has
    // the last success it is measured from instead of turning grey.
    internal static readonly TimeSpan Window = TimeSpan.FromDays(14);
    // Problems older than a week are not shown: a failure the week has not repeated is no current sign.
    internal static readonly TimeSpan ProblemWindow = TimeSpan.FromDays(7);

    // Kinds of data and the jobs that carry them. Stock and price share the combined price/stock push. StaleAfter is
    // the least period a kind read on a schedule is measured against (a flow is late past its period, bad past twice
    // it; a flow whose sync policy runs it less often is measured against that interval); products, stock and price run
    // only when there is something to do (an import, a changed price or stock), so a quiet week is not "Gecikiyor"
    // (null: no age rule).
    public static readonly (string Key, string[] JobTypes, TimeSpan? StaleAfter)[] Kinds =
    [
        ("orders", [MarketplaceJobTypes.OrderSync, MarketplaceJobTypes.ShopifyOrderSync, MarketplaceJobTypes.HepsiburadaOrderSync, MarketplaceJobTypes.N11OrderSync, MarketplaceJobTypes.PazaramaOrderSync, MarketplaceJobTypes.PttAvmOrderSync, MarketplaceJobTypes.OrderRecoverySync, MarketplaceJobTypes.ShopifyOrderRecoverySync, MarketplaceJobTypes.HepsiburadaOrderRecoverySync, MarketplaceJobTypes.N11OrderRecoverySync, MarketplaceJobTypes.PazaramaOrderRecoverySync, MarketplaceJobTypes.PttAvmOrderRecoverySync, MarketplaceJobTypes.OrderStatusSync, MarketplaceJobTypes.ShopifyOrderStatusSync, MarketplaceJobTypes.HepsiburadaOrderStatusSync, MarketplaceJobTypes.PazaramaOrderStatusSync, MarketplaceJobTypes.PttAvmOrderStatusSync, MarketplaceJobTypes.OrderReconciliation, MarketplaceJobTypes.ShopifyOrderReconciliation, MarketplaceJobTypes.TrendyolOrderCargoInfoReconciliation, MarketplaceJobTypes.WebhookIngest, MarketplaceJobTypes.ShopifyWebhookIngest, MarketplaceJobTypes.HepsiburadaWebhookIngest], TimeSpan.FromHours(2)),
        ("products", [MarketplaceJobTypes.ProductSync, MarketplaceJobTypes.ShopifyProductSync, MarketplaceJobTypes.HepsiburadaProductSync, MarketplaceJobTypes.ProductCreate, MarketplaceJobTypes.ProductUpdate, MarketplaceJobTypes.ProductArchive, MarketplaceJobTypes.ProductApprovalReconcile], null),
        ("stock", [MarketplaceJobTypes.StockProjectionDispatch, MarketplaceJobTypes.StockReconciliation, MarketplaceJobTypes.PriceInventorySync], null),
        ("price", [MarketplaceJobTypes.PriceInventorySync], null),
        ("returns", [MarketplaceJobTypes.ReturnSync, MarketplaceJobTypes.HepsiburadaReturnSync, MarketplaceJobTypes.ReturnStatusSync, MarketplaceJobTypes.ReturnReconciliation, MarketplaceJobTypes.ReturnAction], TimeSpan.FromHours(12)),
        ("invoices", [InvoicingJobTypes.MarketplaceDelivery, InvoicingJobTypes.ManualOrderInvoiceDelivery, MarketplaceJobTypes.OrderInvoiceReconciliation, MarketplaceJobTypes.ShopifyOrderInvoiceReconciliation, MarketplaceJobTypes.HepsiburadaOrderInvoiceReconciliation], TimeSpan.FromDays(2)),
    ];

    // Job types that run when something happens (a notification, a user's action, an invoice to send, an open return
    // claim to follow: ScheduledJobProducer queues the open-return scan only then), not on a fixed schedule: silence
    // says nothing about them, only their failures count.
    internal static readonly string[] EventDriven =
    [
        MarketplaceJobTypes.WebhookIngest, MarketplaceJobTypes.ShopifyWebhookIngest, MarketplaceJobTypes.HepsiburadaWebhookIngest,
        MarketplaceJobTypes.ReturnAction, MarketplaceJobTypes.ReturnStatusSync, InvoicingJobTypes.MarketplaceDelivery, InvoicingJobTypes.ManualOrderInvoiceDelivery
    ];

    // Blocked jobs are failures too, except the ones a policy stopped on purpose (external writes switched off, a
    // superseded price push, a passive connection): those say nothing about the integration's health.
    private static readonly JobStatus[] Failed = [JobStatus.Dead, JobStatus.ManualReview, JobStatus.Blocked];
    private static readonly JobStatus[] Retrying = [JobStatus.RetryScheduled];
    public static readonly string[] PolicyBlockCodes = ["EXTERNAL_WRITES_DISABLED", "EXTERNAL_WRITE_POLICY_DISABLED", "HEPSIBURADA_WRITE_CAPABILITY_EVIDENCE_REQUIRED", "PRICE_INVENTORY_SUPERSEDED", "CONNECTION_INACTIVE"];

    public static async Task<IReadOnlyList<DashboardIntegrationRowView>> QueryAsync(AppDbContext db, Guid tenantId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var connections = await db.PlatformConnections.AsNoTracking()
            .Where(x => x.TenantId == tenantId && DashboardMetricPolicy.OperationalConnectionStatuses.Contains(x.Status))
            .OrderBy(x => x.DisplayName)
            .Select(x => new { x.Id, x.PlatformCode, x.DisplayName, x.ExternalStoreId, x.Status })
            .ToListAsync(cancellationToken);
        if (connections.Count == 0) return [];
        var since = now - Window;
        var connectionIds = connections.Select(x => (Guid?)x.Id).ToArray();
        var plainIds = connections.Select(x => x.Id).ToArray();
        var successes = await SuccessQuery(db, tenantId, connectionIds, since).ToListAsync(cancellationToken);
        // The newest problem of every store and job type, however many other stores fail more often.
        var problems = await ProblemQuery(db, tenantId, connectionIds, now - ProblemWindow).ToListAsync(cancellationToken);
        var policies = await db.ConnectionSyncPolicies.AsNoTracking()
            .Where(x => x.TenantId == tenantId && plainIds.Contains(x.ConnectionId))
            .Select(x => new PolicyRow(x.ConnectionId, x.ResourceType, x.IntervalSeconds, x.Enabled, x.IdleBackoffEnabled, x.IdleIntervalSeconds))
            .ToListAsync(cancellationToken);
        return connections.Select(connection =>
        {
            string? issue = null;
            var silent = false;
            var cells = new Dictionary<string, DashboardIntegrationCellView>();
            var periods = Periods(connection.PlatformCode, policies.Where(x => x.ConnectionId == connection.Id));
            foreach (var (key, types, staleAfter) in Kinds)
            {
                var state = "off";
                DateTimeOffset? cellSuccess = null, cellProblem = null;
                foreach (var type in types.Distinct())
                {
                    var success = successes.FirstOrDefault(x => x.ConnectionId == connection.Id && x.JobType == type)?.At;
                    var problem = problems.FirstOrDefault(x => x.ConnectionId == connection.Id && x.JobType == type);
                    if (problem is not null && success is not null && problem.CreatedAt <= success) problem = null;
                    var period = Period(type, staleAfter, periods);
                    var typeState = TypeState(success, problem?.Status, period, now);
                    if (success is { } at && (cellSuccess is null || at > cellSuccess)) cellSuccess = at;
                    if (problem is not null && (cellProblem is null || problem.CreatedAt > cellProblem)) cellProblem = problem.CreatedAt;
                    if (problem is not null && Failed.Contains(problem.Status)) issue ??= problem.LastErrorSummary;
                    if (typeState == "bad" && (problem is null || !Failed.Contains(problem.Status))) silent = true;
                    state = Worst(state, typeState);
                }
                cells[key] = new DashboardIntegrationCellView(state, cellSuccess, cellProblem);
            }
            if (issue is null && cells.Values.Any(x => x.State == "bad"))
                issue = silent ? "Bazı akışlar beklenen sürenin iki katından uzun süredir çalışmadı." : "Bazı işlemler tamamlanamadı.";
            return new DashboardIntegrationRowView(connection.Id, connection.PlatformCode, connection.DisplayName, connection.ExternalStoreId, connection.Status, cells, issue);
        }).ToList();
    }

    // One job type's latest result: a failure after its last success is bad, a retry warn; a scheduled flow silent past
    // its period is late (warn), past twice its period bad; one that never ran in the window says nothing (off).
    internal static string TypeState(DateTimeOffset? lastSuccess, JobStatus? problem, TimeSpan? period, DateTimeOffset now)
    {
        var state = problem is { } status ? Failed.Contains(status) ? "bad" : "warn" : lastSuccess is null ? "off" : "ok";
        if (lastSuccess is { } success && period is { } limit)
        {
            var age = now - success;
            state = Worst(state, age > limit * 2 ? "bad" : age > limit ? "warn" : "ok");
        }
        return state;
    }

    private static readonly string[] Severity = ["off", "ok", "warn", "bad"];
    internal static string Worst(string left, string right) => Array.IndexOf(Severity, right) > Array.IndexOf(Severity, left) ? right : left;

    // The period a scheduled job type is measured against: its sync policy's longest interval (thinned out while idle),
    // never less than the kind's own; none for event-driven types, kinds without an age rule, or a flow the user
    // switched off (every policy that runs it disabled).
    internal static TimeSpan? Period(string jobType, TimeSpan? kindPeriod, IReadOnlyDictionary<string, TimeSpan?> periods)
    {
        if (kindPeriod is not { } floor || EventDriven.Contains(jobType)) return null;
        if (!periods.TryGetValue(jobType, out var policy)) return floor;
        return policy is { } interval ? interval > floor ? interval : floor : null;
    }

    // The scheduled interval of each job type of a connection, from its sync policies: the shortest enabled one when
    // several policies run the same job type (short, medium and daily reconciliation); null when all are disabled.
    internal static IReadOnlyDictionary<string, TimeSpan?> Periods(string platformCode, IEnumerable<PolicyRow> policies)
    {
        var result = new Dictionary<string, TimeSpan?>(StringComparer.Ordinal);
        foreach (var policy in policies)
        {
            if (ScheduledJobType(platformCode, policy.ResourceType) is not { } jobType) continue;
            TimeSpan? interval = policy.Enabled
                ? TimeSpan.FromSeconds(policy.IdleBackoffEnabled ? SyncCadencePolicy.IdleSeconds(platformCode, policy.ResourceType, policy.IntervalSeconds, policy.IdleIntervalSeconds) : policy.IntervalSeconds)
                : null;
            result[jobType] = result.TryGetValue(jobType, out var known) ? known is null ? interval : interval is null ? known : interval < known ? interval : known : interval;
        }
        return result;
    }

    // The job a sync policy queues (ScheduledJobProducer.Definition), for the policies whose job types the matrix shows.
    internal static string? ScheduledJobType(string platformCode, string resourceType) => resourceType switch
    {
        "ORDERS" => MarketplaceJobTypes.ForPlatform(platformCode, MarketplaceJobTypes.OrderSync),
        "ORDER_RECOVERY" => MarketplaceJobTypes.ForPlatform(platformCode, MarketplaceJobTypes.OrderRecoverySync),
        "ORDER_LIFECYCLE" => MarketplaceJobTypes.ForPlatform(platformCode, MarketplaceJobTypes.OrderStatusSync),
        "ORDER_CARGO_INFO" => MarketplaceJobTypes.TrendyolOrderCargoInfoReconciliation,
        "ORDER_RECONCILE_SHORT" or "ORDER_RECONCILE_MEDIUM" or "ORDER_RECONCILE_DAILY" => MarketplaceJobTypes.ForPlatform(platformCode, MarketplaceJobTypes.OrderReconciliation),
        "ORDER_INVOICE_RECONCILIATION" => MarketplaceJobTypes.ForPlatform(platformCode, MarketplaceJobTypes.OrderInvoiceReconciliation),
        "RETURNS" => MarketplaceJobTypes.ForPlatform(platformCode, MarketplaceJobTypes.ReturnSync),
        "RETURN_LIFECYCLE" => MarketplaceJobTypes.ReturnStatusSync,
        "RETURN_RECONCILE_SHORT" or "RETURN_RECONCILE_MEDIUM" or "RETURN_RECONCILE_DAILY" => MarketplaceJobTypes.ReturnReconciliation,
        _ => null
    };

    internal sealed record SuccessRow(Guid? ConnectionId, string JobType, DateTimeOffset At);
    internal sealed record ProblemRow(Guid? ConnectionId, string JobType, JobStatus Status, DateTimeOffset CreatedAt, string? LastErrorSummary);
    internal sealed record PolicyRow(Guid ConnectionId, string ResourceType, int IntervalSeconds, bool Enabled, bool IdleBackoffEnabled, int? IdleIntervalSeconds);

    // A scheduled read folded into the same read already running (MarketplaceSyncExecutionLock.CoalescedCode) did
    // nothing itself: it is no sign that the flow works.
    internal static IQueryable<SuccessRow> SuccessQuery(AppDbContext db, Guid tenantId, Guid?[] connectionIds, DateTimeOffset since)
    {
        var jobTypes = Kinds.SelectMany(x => x.JobTypes).Distinct().ToArray();
        return db.IntegrationJobs.AsNoTracking()
            .Where(x => x.TenantId == tenantId && connectionIds.Contains(x.ConnectionId) && x.CreatedAt >= since && jobTypes.Contains(x.JobType) && x.Status == JobStatus.Succeeded
                && (x.LastErrorCode == null || x.LastErrorCode != MarketplaceSyncExecutionLock.CoalescedCode))
            .GroupBy(x => new { x.ConnectionId, x.JobType })
            .Select(g => new SuccessRow(g.Key.ConnectionId, g.Key.JobType, g.Max(x => x.CompletedAt ?? x.CreatedAt)));
    }

    // DISTINCT ON (connection, job type): one row each, the newest.
    internal static IQueryable<ProblemRow> ProblemQuery(AppDbContext db, Guid tenantId, Guid?[] connectionIds, DateTimeOffset since)
    {
        var jobTypes = Kinds.SelectMany(x => x.JobTypes).Distinct().ToArray();
        var policyCodes = PolicyBlockCodes;
        // A retry that only waits for the marketplace to finish what it accepted (a batch, an approval) is no problem.
        var waitingCodes = JobWaitingPolicy.Codes.ToArray();
        return db.IntegrationJobs.AsNoTracking()
            .Where(x => x.TenantId == tenantId && connectionIds.Contains(x.ConnectionId) && x.CreatedAt >= since && jobTypes.Contains(x.JobType)
                && (Failed.Contains(x.Status) || Retrying.Contains(x.Status) && (x.LastErrorCode == null || !waitingCodes.Contains(x.LastErrorCode)))
                && !(x.Status == JobStatus.Blocked && x.LastErrorCode != null && (policyCodes.Contains(x.LastErrorCode) || x.LastErrorCode.EndsWith("_DISABLED"))))
            .GroupBy(x => new { x.ConnectionId, x.JobType })
            .Select(g => g.OrderByDescending(x => x.CreatedAt).Select(x => new ProblemRow(x.ConnectionId, x.JobType, x.Status, x.CreatedAt, x.LastErrorSummary)).First());
    }
}
