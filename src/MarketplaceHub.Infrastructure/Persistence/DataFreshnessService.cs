using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

// Read-only answers to "how fresh is what I see": when orders, returns and questions were last read
// from the marketplaces, whether order notifications arrive, and how far a first import has come.
public sealed class DataFreshnessService(AppDbContext db, TimeProvider timeProvider) : IDataFreshnessService
{
    private static readonly string[] MarketplacePlatforms = ["TRENDYOL", "HEPSIBURADA", "N11", "PAZARAMA", "SHOPIFY"];
    private static readonly JobStatus[] OpenStatuses = [JobStatus.Pending, JobStatus.Leased, JobStatus.RetryScheduled];

    public async Task<DataFreshnessView> FreshnessAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var connections = await db.PlatformConnections.AsNoTracking()
            .Where(x => x.TenantId == tenantId && MarketplacePlatforms.Contains(x.PlatformCode) && (x.Status == "ACTIVE" || x.Status == "VERIFIED"))
            .Select(x => new { x.Id, x.PlatformCode })
            .ToListAsync(cancellationToken);
        var ids = connections.Select(x => x.Id).ToArray();
        var cursors = await db.SyncCursors.AsNoTracking()
            .Where(x => x.TenantId == tenantId && ids.Contains(x.ConnectionId) && (x.ResourceType == "ORDERS_HOT" || x.ResourceType == "RETURNS"))
            .Select(x => new { x.ConnectionId, x.ResourceType, x.LastSuccessAt })
            .ToListAsync(cancellationToken);
        var questions = await db.MarketplaceQuestionSyncStates.AsNoTracking()
            .Where(x => x.TenantId == tenantId && ids.Contains(x.ConnectionId))
            .Select(x => new { x.ConnectionId, x.LastSuccessAt })
            .ToListAsync(cancellationToken);
        var instant = await InstantConnectionsAsync(tenantId, ids, cancellationToken);

        // The oldest store decides: "updated 40 s ago" must hold for every store read so far.
        static DateTimeOffset? Oldest(IEnumerable<Guid> scope, Func<Guid, DateTimeOffset?> read) => scope.Select(read).Where(x => x is not null).Min();
        var questionScope = connections.Where(x => x.PlatformCode != "SHOPIFY").Select(x => x.Id).ToArray();
        return new DataFreshnessView(
        [
            new("ORDERS", Oldest(ids, id => cursors.FirstOrDefault(x => x.ConnectionId == id && x.ResourceType == "ORDERS_HOT")?.LastSuccessAt), ids.Length > 0 && ids.All(instant.Contains), ids.Length),
            new("RETURNS", Oldest(ids, id => cursors.FirstOrDefault(x => x.ConnectionId == id && x.ResourceType == "RETURNS")?.LastSuccessAt), false, ids.Length),
            new("QUESTIONS", Oldest(questionScope, id => questions.FirstOrDefault(x => x.ConnectionId == id)?.LastSuccessAt), false, questionScope.Length)
        ]);
    }

    public async Task<ServiceResult<InitialImportView>> InitialImportAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken)
    {
        var connection = await db.PlatformConnections.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.Id == connectionId)
            .Select(x => new { x.PlatformCode })
            .SingleOrDefaultAsync(cancellationToken);
        if (connection is null) return ServiceResult<InitialImportView>.Fail("RESOURCE_NOT_FOUND", "Bağlantı bulunamadı.", 404);

        // First-import jobs carry the initial-sync priority; slices of a long read keep it.
        var openImports = await db.IntegrationJobs.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.Priority == MarketplaceConnectionService.InitialDataSyncPriority && OpenStatuses.Contains(x.Status))
            .Select(x => new { x.ResourceType, x.Status })
            .ToListAsync(cancellationToken);
        var recovery = await db.SyncCursors.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ResourceType == "ORDERS_RECOVERY")
            .Select(x => x.OpaqueCursor)
            .FirstOrDefaultAsync(cancellationToken);
        var question = await db.MarketplaceQuestionSyncStates.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId)
            .Select(x => new { x.HistoryImported, x.ProgressStatus })
            .FirstOrDefaultAsync(cancellationToken);

        InitialImportPart Part(string kind, string resourceType, int? percent = null)
        {
            var open = openImports.Where(x => x.ResourceType == resourceType).ToArray();
            if (open.Length == 0) return new(kind, "DONE", null);
            return new(kind, open.Any(x => x.Status == JobStatus.Leased) || percent > 0 ? "RUNNING" : "QUEUED", percent);
        }
        var parts = new List<InitialImportPart> { Part("ORDERS", "orders", MarketplaceJobProcessor.OrderBaselinePercent(recovery)) };
        if (connection.PlatformCode != "SHOPIFY") parts.Add(Part("RETURNS", "returns"));
        if (connection.PlatformCode != "SHOPIFY" && question is not null)
            parts.Add(new("QUESTIONS", question.HistoryImported ? "DONE" : question.ProgressStatus == "RUNNING" ? "RUNNING" : "QUEUED", null));
        return ServiceResult<InitialImportView>.Ok(new(connectionId, parts.Any(x => x.State != "DONE"), parts));
    }

    private async Task<HashSet<Guid>> InstantConnectionsAsync(Guid tenantId, Guid[] ids, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var subscriptions = await db.WebhookSubscriptions.AsNoTracking()
            .Where(x => x.TenantId == tenantId && ids.Contains(x.ConnectionId) && x.Status == "ACTIVE" && x.ExternalSubscriptionId != null)
            .Select(x => new { x.ConnectionId, x.LastReceivedAt, x.VerifiedAt })
            .ToListAsync(cancellationToken);
        var healthy = new HashSet<Guid>();
        foreach (var subscription in subscriptions)
        {
            var health = await db.SyncCursors.AsNoTracking()
                .Where(x => x.TenantId == tenantId && x.ConnectionId == subscription.ConnectionId && x.ResourceType == WebhookPollingPolicy.HealthCursorResource)
                .Select(x => x.OpaqueCursor)
                .FirstOrDefaultAsync(cancellationToken);
            if (WebhookPollingPolicy.IsHealthy(subscription.LastReceivedAt, subscription.VerifiedAt, ScheduledJobProducer.WebhookGaps(health), now,
                    await ScheduledJobProducer.OrdersArrivedSinceAsync(db, tenantId, subscription.ConnectionId, subscription.LastReceivedAt ?? subscription.VerifiedAt, cancellationToken)))
                healthy.Add(subscription.ConnectionId);
        }
        return healthy;
    }
}
