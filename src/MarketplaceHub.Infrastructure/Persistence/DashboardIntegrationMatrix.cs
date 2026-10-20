using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

// The dashboard's integration matrix: one row per marketplace connection, one cell per kind of data, each saying
// when that data last synced for that connection and whether the latest attempts failed. Read-only.
public static class DashboardIntegrationMatrix
{
    private static readonly TimeSpan Window = TimeSpan.FromDays(7);

    // Kinds of data and the jobs that carry them. Stock and price share the combined price/stock push.
    public static readonly (string Key, string[] JobTypes, TimeSpan StaleAfter)[] Kinds =
    [
        ("orders", [MarketplaceJobTypes.OrderSync, MarketplaceJobTypes.ShopifyOrderSync, MarketplaceJobTypes.HepsiburadaOrderSync, MarketplaceJobTypes.N11OrderSync, MarketplaceJobTypes.PazaramaOrderSync, MarketplaceJobTypes.OrderRecoverySync, MarketplaceJobTypes.ShopifyOrderRecoverySync, MarketplaceJobTypes.HepsiburadaOrderRecoverySync, MarketplaceJobTypes.N11OrderRecoverySync, MarketplaceJobTypes.PazaramaOrderRecoverySync, MarketplaceJobTypes.OrderStatusSync, MarketplaceJobTypes.ShopifyOrderStatusSync, MarketplaceJobTypes.HepsiburadaOrderStatusSync, MarketplaceJobTypes.PazaramaOrderStatusSync, MarketplaceJobTypes.WebhookIngest, MarketplaceJobTypes.ShopifyWebhookIngest], TimeSpan.FromHours(2)),
        ("products", [MarketplaceJobTypes.ProductSync, MarketplaceJobTypes.ShopifyProductSync, MarketplaceJobTypes.HepsiburadaProductSync, MarketplaceJobTypes.ProductCreate, MarketplaceJobTypes.ProductUpdate, MarketplaceJobTypes.ProductArchive, MarketplaceJobTypes.ProductApprovalReconcile], TimeSpan.FromDays(3)),
        ("stock", [MarketplaceJobTypes.StockProjectionDispatch, MarketplaceJobTypes.StockReconciliation, MarketplaceJobTypes.PriceInventorySync], TimeSpan.FromHours(6)),
        ("price", [MarketplaceJobTypes.PriceInventorySync], TimeSpan.FromDays(2)),
        ("returns", [MarketplaceJobTypes.ReturnSync, MarketplaceJobTypes.HepsiburadaReturnSync, MarketplaceJobTypes.ReturnStatusSync, MarketplaceJobTypes.ReturnReconciliation, MarketplaceJobTypes.ReturnAction], TimeSpan.FromHours(12)),
        ("invoices", [InvoicingJobTypes.MarketplaceDelivery, InvoicingJobTypes.ManualOrderInvoiceDelivery, MarketplaceJobTypes.OrderInvoiceReconciliation, MarketplaceJobTypes.ShopifyOrderInvoiceReconciliation, MarketplaceJobTypes.HepsiburadaOrderInvoiceReconciliation], TimeSpan.FromDays(2)),
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
        var successes = await SuccessQuery(db, tenantId, connectionIds, since).ToListAsync(cancellationToken);
        // The newest problem of every store and job type, however many other stores fail more often.
        var problems = await ProblemQuery(db, tenantId, connectionIds, since).ToListAsync(cancellationToken);
        return connections.Select(connection =>
        {
            string? issue = null;
            var cells = new Dictionary<string, DashboardIntegrationCellView>();
            foreach (var (key, types, staleAfter) in Kinds)
            {
                DateTimeOffset? lastSuccess = successes.Where(x => x.ConnectionId == connection.Id && types.Contains(x.JobType)).Select(x => (DateTimeOffset?)x.At).Max();
                var problem = problems.Where(x => x.ConnectionId == connection.Id && types.Contains(x.JobType) && (lastSuccess is null || x.CreatedAt > lastSuccess))
                    .OrderByDescending(x => x.CreatedAt).FirstOrDefault();
                var state = problem is not null && Failed.Contains(problem.Status) ? "bad"
                    : problem is not null ? "warn"
                    : lastSuccess is null ? "off"
                    : now - lastSuccess > staleAfter ? "warn"
                    : "ok";
                if (problem is not null && Failed.Contains(problem.Status)) issue ??= problem.LastErrorSummary;
                cells[key] = new DashboardIntegrationCellView(state, lastSuccess, problem?.CreatedAt);
            }
            if (issue is null && cells.Values.Any(x => x.State == "bad")) issue = "Bazı işlemler tamamlanamadı.";
            return new DashboardIntegrationRowView(connection.Id, connection.PlatformCode, connection.DisplayName, connection.ExternalStoreId, connection.Status, cells, issue);
        }).ToList();
    }

    internal sealed record SuccessRow(Guid? ConnectionId, string JobType, DateTimeOffset At);
    internal sealed record ProblemRow(Guid? ConnectionId, string JobType, JobStatus Status, DateTimeOffset CreatedAt, string? LastErrorSummary);

    internal static IQueryable<SuccessRow> SuccessQuery(AppDbContext db, Guid tenantId, Guid?[] connectionIds, DateTimeOffset since)
    {
        var jobTypes = Kinds.SelectMany(x => x.JobTypes).Distinct().ToArray();
        return db.IntegrationJobs.AsNoTracking()
            .Where(x => x.TenantId == tenantId && connectionIds.Contains(x.ConnectionId) && x.CreatedAt >= since && jobTypes.Contains(x.JobType) && x.Status == JobStatus.Succeeded)
            .GroupBy(x => new { x.ConnectionId, x.JobType })
            .Select(g => new SuccessRow(g.Key.ConnectionId, g.Key.JobType, g.Max(x => x.CompletedAt ?? x.CreatedAt)));
    }

    // DISTINCT ON (connection, job type): one row each, the newest.
    internal static IQueryable<ProblemRow> ProblemQuery(AppDbContext db, Guid tenantId, Guid?[] connectionIds, DateTimeOffset since)
    {
        var jobTypes = Kinds.SelectMany(x => x.JobTypes).Distinct().ToArray();
        var policyCodes = PolicyBlockCodes;
        return db.IntegrationJobs.AsNoTracking()
            .Where(x => x.TenantId == tenantId && connectionIds.Contains(x.ConnectionId) && x.CreatedAt >= since && jobTypes.Contains(x.JobType)
                && (Failed.Contains(x.Status) || Retrying.Contains(x.Status))
                && !(x.Status == JobStatus.Blocked && x.LastErrorCode != null && (policyCodes.Contains(x.LastErrorCode) || x.LastErrorCode.EndsWith("_DISABLED"))))
            .GroupBy(x => new { x.ConnectionId, x.JobType })
            .Select(g => g.OrderByDescending(x => x.CreatedAt).Select(x => new ProblemRow(x.ConnectionId, x.JobType, x.Status, x.CreatedAt, x.LastErrorSummary)).First());
    }
}
