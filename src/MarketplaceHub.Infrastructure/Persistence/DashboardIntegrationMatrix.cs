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

    private static readonly JobStatus[] Failed = [JobStatus.Dead, JobStatus.ManualReview, JobStatus.Blocked];
    private static readonly JobStatus[] Retrying = [JobStatus.RetryScheduled];

    public static async Task<IReadOnlyList<DashboardIntegrationRowView>> QueryAsync(AppDbContext db, Guid tenantId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var connections = await db.PlatformConnections.AsNoTracking()
            .Where(x => x.TenantId == tenantId && DashboardMetricPolicy.OperationalConnectionStatuses.Contains(x.Status))
            .OrderBy(x => x.DisplayName)
            .Select(x => new { x.Id, x.PlatformCode, x.DisplayName, x.ExternalStoreId, x.Status })
            .ToListAsync(cancellationToken);
        if (connections.Count == 0) return [];
        var since = now - Window;
        var jobTypes = Kinds.SelectMany(x => x.JobTypes).Distinct().ToArray();
        var connectionIds = connections.Select(x => (Guid?)x.Id).ToArray();
        var successes = await db.IntegrationJobs.AsNoTracking()
            .Where(x => x.TenantId == tenantId && connectionIds.Contains(x.ConnectionId) && x.CreatedAt >= since && jobTypes.Contains(x.JobType) && x.Status == JobStatus.Succeeded)
            .GroupBy(x => new { x.ConnectionId, x.JobType })
            .Select(g => new { g.Key.ConnectionId, g.Key.JobType, At = g.Max(x => x.CompletedAt ?? x.CreatedAt) })
            .ToListAsync(cancellationToken);
        var problems = await db.IntegrationJobs.AsNoTracking()
            .Where(x => x.TenantId == tenantId && connectionIds.Contains(x.ConnectionId) && x.CreatedAt >= since && jobTypes.Contains(x.JobType) && (Failed.Contains(x.Status) || Retrying.Contains(x.Status)))
            .OrderByDescending(x => x.CreatedAt)
            .Take(300)
            .Select(x => new { x.ConnectionId, x.JobType, x.Status, x.CreatedAt, x.LastErrorSummary })
            .ToListAsync(cancellationToken);
        return connections.Select(connection =>
        {
            string? issue = null;
            var cells = new Dictionary<string, DashboardIntegrationCellView>();
            foreach (var (key, types, staleAfter) in Kinds)
            {
                DateTimeOffset? lastSuccess = successes.Where(x => x.ConnectionId == connection.Id && types.Contains(x.JobType)).Select(x => (DateTimeOffset?)x.At).Max();
                var problem = problems.FirstOrDefault(x => x.ConnectionId == connection.Id && types.Contains(x.JobType) && (lastSuccess is null || x.CreatedAt > lastSuccess));
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
}
