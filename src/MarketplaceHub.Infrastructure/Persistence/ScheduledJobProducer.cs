using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace MarketplaceHub.Infrastructure.Persistence;

public sealed class ScheduledJobProducer(AppDbContext db, TimeProvider timeProvider, IConfiguration configuration) : IScheduledJobProducer
{
    public async Task<int> EnqueueDueAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var added = 0;
        await DisableProductAutomationAsync(cancellationToken);
        await EnsureDefaultPoliciesAsync(cancellationToken);
        var policies = await (from policy in db.ConnectionSyncPolicies.AsNoTracking()
                              join connection in db.PlatformConnections.AsNoTracking()
                                  on new { policy.TenantId, Id = policy.ConnectionId } equals new { connection.TenantId, connection.Id }
                              where policy.Enabled && (connection.Status == "ACTIVE" || connection.Status == "VERIFIED") && connection.PlatformCode == "TRENDYOL"
                              select new { Policy = policy, Connection = connection }).ToListAsync(cancellationToken);
        var reservedExecutionGroups = new HashSet<string>(StringComparer.Ordinal);
        var backgroundOrderReservations = new HashSet<Guid>();

        foreach (var row in policies)
        {
            var definition = Definition(row.Policy.ResourceType, row.Connection.Id);
            if (definition is null) continue;

            var interval = Math.Clamp(row.Policy.IntervalSeconds, 30, 86_400);
            // Different scheduled job types can share one provider execution
            // lane (for example order sync and order reconciliation). Checking
            // only the exact job type allowed those jobs to pile up behind the
            // advisory lock and eventually exhaust retries with SYNC_LOCK_BUSY.
            var activeJobTypes = await db.IntegrationJobs.AsNoTracking()
                .Where(x => x.TenantId == row.Policy.TenantId && x.ConnectionId == row.Connection.Id
                    && (x.Status == JobStatus.Pending || x.Status == JobStatus.Leased || x.Status == JobStatus.RetryScheduled))
                .Select(x => x.JobType)
                .Distinct()
                .ToListAsync(cancellationToken);
            var executionGroup = MarketplaceSyncExecutionLock.GroupFor(definition.Value.JobType);
            var active = activeJobTypes.Any(jobType => MarketplaceSyncExecutionLock.GroupFor(jobType) == executionGroup);
            var isOrderLifecycle = definition.Value.JobType == MarketplaceJobTypes.OrderStatusSync;
            var lifecycleAlreadyQueued = activeJobTypes.Contains(MarketplaceJobTypes.OrderStatusSync, StringComparer.Ordinal);
            var activeOrderLane = activeJobTypes.Any(jobType => MarketplaceSyncExecutionLock.GroupFor(jobType) == "orders");
            var canQueueLifecycleBehindOrderLane = isOrderLifecycle && activeOrderLane && !lifecycleAlreadyQueued;
            var isOrderBackground = IsOrderBackgroundJob(definition.Value.JobType);
            var backgroundOrderAlreadyQueued = activeJobTypes.Any(IsOrderBackgroundJob);
            var canQueueOrderBackgroundBehindOrderLane = isOrderBackground
                && activeOrderLane
                && !backgroundOrderAlreadyQueued
                && !backgroundOrderReservations.Contains(row.Connection.Id);
            // The jobs added during this pass are not visible to the AsNoTracking
            // queries until SaveChangesAsync. Reserve the provider lane in memory
            // as well, otherwise multiple order policies can be queued together.
            // A lifecycle scan is the exception: one pending lifecycle job may
            // wait behind the hot order stream, otherwise a continuously busy
            // stream can starve status refreshes forever.
            if ((active && !canQueueLifecycleBehindOrderLane && !canQueueOrderBackgroundBehindOrderLane)
                || (reservedExecutionGroups.Contains(executionGroup) && !canQueueLifecycleBehindOrderLane && !canQueueOrderBackgroundBehindOrderLane)) continue;
            var latest = await db.IntegrationJobs.AsNoTracking()
                .Where(x => x.TenantId == row.Policy.TenantId && x.ConnectionId == row.Connection.Id && x.JobType == definition.Value.JobType && x.JobDedupKey.StartsWith(definition.Value.DedupPrefix))
                .OrderByDescending(x => x.CreatedAt).Select(x => (DateTimeOffset?)x.CreatedAt).FirstOrDefaultAsync(cancellationToken);
            var hasOrderSnapshots = definition.Value.JobType != MarketplaceJobTypes.OrderSync
                || await db.Orders.AsNoTracking().AnyAsync(x => x.TenantId == row.Policy.TenantId && x.ConnectionId == row.Connection.Id, cancellationToken);
            if (hasOrderSnapshots && latest is not null && latest.Value.AddSeconds(interval) > now) continue;

            var bucket = now.ToUnixTimeSeconds() / interval;
            var dedup = $"{definition.Value.DedupPrefix}:{bucket}";
            if (await db.IntegrationJobs.AsNoTracking().AnyAsync(x => x.TenantId == row.Policy.TenantId && x.JobType == definition.Value.JobType && x.JobDedupKey == dedup, cancellationToken)) continue;
            reservedExecutionGroups.Add(executionGroup);
            if (canQueueOrderBackgroundBehindOrderLane) backgroundOrderReservations.Add(row.Connection.Id);
            db.IntegrationJobs.Add(NewJob(row.Policy.TenantId, row.Connection.Id, definition.Value.JobType, dedup, definition.Value.PayloadJson, now, $"scheduler-{Guid.NewGuid():N}"));
            added++;
        }

        var dueTenants = await db.InvoicePolicies.AsNoTracking().Where(x => x.AutoSubmit).Select(x => x.TenantId).Distinct().ToListAsync(cancellationToken);
        const int invoiceScanInterval = 300;
        var invoiceBucket = now.ToUnixTimeSeconds() / invoiceScanInterval;
        foreach (var tenantId in dueTenants)
        {
            var dedup = $"scheduled:invoice-due:{tenantId:N}:{invoiceBucket}";
            if (await db.IntegrationJobs.AsNoTracking().AnyAsync(x => x.TenantId == tenantId && x.JobType == InvoicingJobTypes.InvoiceDueScan && x.JobDedupKey == dedup, cancellationToken)) continue;
            db.IntegrationJobs.Add(NewJob(tenantId, null, InvoicingJobTypes.InvoiceDueScan, dedup, "{}", now, $"scheduler-{Guid.NewGuid():N}"));
            added++;
        }

        if (added == 0) return 0;
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return added;
        }
        catch (DbUpdateException)
        {
            // Another worker may have won the unique dedup race. Clear pending tracked rows;
            // the next scheduler pass will observe the committed jobs.
            foreach (var entry in db.ChangeTracker.Entries<IntegrationJob>().Where(x => x.State == EntityState.Added)) entry.State = EntityState.Detached;
            return 0;
        }
    }

    private async Task DisableProductAutomationAsync(CancellationToken cancellationToken)
    {
        var productPolicies = await db.ConnectionSyncPolicies
            .Where(x => x.ResourceType == "PRODUCTS" && x.Enabled)
            .ToListAsync(cancellationToken);
        foreach (var policy in productPolicies)
        {
            policy.Enabled = false;
            policy.Version++;
        }

        var scheduledProductJobs = await db.IntegrationJobs
            .Where(x => x.JobType == MarketplaceJobTypes.ProductSync
                && x.JobDedupKey.StartsWith("scheduled:products:")
                && (x.Status == JobStatus.Pending || x.Status == JobStatus.RetryScheduled))
            .ToListAsync(cancellationToken);
        foreach (var job in scheduledProductJobs)
        {
            job.Status = JobStatus.Cancelled;
            job.CompletedAt = timeProvider.GetUtcNow();
            job.Version++;
        }

        if (productPolicies.Count > 0 || scheduledProductJobs.Count > 0)
            await db.SaveChangesAsync(cancellationToken);
    }

    private async Task EnsureDefaultPoliciesAsync(CancellationToken cancellationToken)
    {
        var operationalConnections = await db.PlatformConnections.AsNoTracking()
            .Where(x => (x.Status == "ACTIVE" || x.Status == "VERIFIED") && x.PlatformCode == "TRENDYOL")
            .Select(x => new { x.TenantId, ConnectionId = x.Id })
            .ToListAsync(cancellationToken);
        if (operationalConnections.Count == 0) return;

        var connectionIds = operationalConnections.Select(x => x.ConnectionId).ToArray();
        var existing = await db.ConnectionSyncPolicies
            .Where(x => connectionIds.Contains(x.ConnectionId) && (x.ResourceType == "ORDERS" || x.ResourceType == "ORDER_RECOVERY" || x.ResourceType == "ORDER_LIFECYCLE" || x.ResourceType == "ORDER_RECONCILE_SHORT" || x.ResourceType == "ORDER_RECONCILE_MEDIUM" || x.ResourceType == "ORDER_RECONCILE_DAILY" || x.ResourceType == "ORDER_INVOICE_RECONCILIATION" || x.ResourceType == "RETURNS" || x.ResourceType == "RETURN_LIFECYCLE" || x.ResourceType == "RETURN_RECONCILE_SHORT" || x.ResourceType == "RETURN_RECONCILE_MEDIUM" || x.ResourceType == "RETURN_RECONCILE_DAILY" || x.ResourceType == "STOCK_RECONCILE_SHORT" || x.ResourceType == "STOCK_RECONCILE_MEDIUM" || x.ResourceType == "STOCK_RECONCILE_DAILY"))
            .ToListAsync(cancellationToken);
        var obsoleteProductPolicies = await db.ConnectionSyncPolicies
            .Where(x => connectionIds.Contains(x.ConnectionId) && x.ResourceType == "PRODUCTS" && x.Enabled)
            .ToListAsync(cancellationToken);
        foreach (var policy in obsoleteProductPolicies)
        {
            policy.Enabled = false;
            policy.Version++;
        }
        foreach (var connection in operationalConnections)
        {
            foreach (var defaults in DefaultPolicies())
            {
                var current = existing.SingleOrDefault(x => x.TenantId == connection.TenantId && x.ConnectionId == connection.ConnectionId && x.ResourceType == defaults.ResourceType);
                if (current is not null)
                {
                    // Upgrade only exact application defaults. Explicit user choices remain untouched.
                    if (IsKnownDefault(current))
                    {
                        current.IntervalSeconds = defaults.IntervalSeconds;
                        current.OverlapSeconds = defaults.OverlapSeconds;
                        current.JitterSeconds = defaults.JitterSeconds;
                        current.Version++;
                    }
                    continue;
                }
                db.ConnectionSyncPolicies.Add(new ConnectionSyncPolicy
                {
                    Id = Guid.CreateVersion7(),
                    TenantId = connection.TenantId,
                    ConnectionId = connection.ConnectionId,
                    ResourceType = defaults.ResourceType,
                    IntervalSeconds = defaults.IntervalSeconds,
                    OverlapSeconds = defaults.OverlapSeconds,
                    JitterSeconds = defaults.JitterSeconds,
                    Enabled = true,
                    Version = 1
                });
            }
        }
        if (db.ChangeTracker.Entries<ConnectionSyncPolicy>().Any(x => x.State is EntityState.Added or EntityState.Modified))
            await db.SaveChangesAsync(cancellationToken);
    }

    private IReadOnlyList<PolicyDefaults> DefaultPolicies() =>
    [
        new("ORDERS", configuration.GetValue("MarketplaceSync:Orders:IntervalSeconds", 60), configuration.GetValue("MarketplaceSync:Orders:SafetyWindowSeconds", 600), 0),
        new("ORDER_RECOVERY", configuration.GetValue("MarketplaceSync:OrderRecovery:IntervalSeconds", 900), configuration.GetValue("MarketplaceSync:OrderRecovery:SafetyWindowSeconds", 600), 0),
        new("ORDER_LIFECYCLE", configuration.GetValue("MarketplaceSync:OrderLifecycle:IntervalSeconds", 180), 0, 0),
        new("ORDER_RECONCILE_SHORT", configuration.GetValue("MarketplaceSync:OrderReconciliation:ShortIntervalSeconds", 900), 0, 0),
        new("ORDER_RECONCILE_MEDIUM", configuration.GetValue("MarketplaceSync:OrderReconciliation:MediumIntervalSeconds", 3600), 0, 0),
        new("ORDER_RECONCILE_DAILY", configuration.GetValue("MarketplaceSync:OrderReconciliation:DailyIntervalSeconds", 86_400), 0, 0),
        new("ORDER_INVOICE_RECONCILIATION", configuration.GetValue("MarketplaceSync:OrderInvoiceReconciliation:IntervalSeconds", 900), 0, 0),
        new("RETURNS", configuration.GetValue("MarketplaceSync:Returns:IntervalSeconds", 180), configuration.GetValue("MarketplaceSync:Returns:SafetyWindowSeconds", 900), 0),
        new("RETURN_LIFECYCLE", configuration.GetValue("MarketplaceSync:ReturnLifecycle:IntervalSeconds", 180), 0, 0),
        new("RETURN_RECONCILE_SHORT", configuration.GetValue("MarketplaceSync:ReturnReconciliation:ShortIntervalSeconds", 900), 0, 0),
        new("RETURN_RECONCILE_MEDIUM", configuration.GetValue("MarketplaceSync:ReturnReconciliation:MediumIntervalSeconds", 3600), 0, 0),
        new("RETURN_RECONCILE_DAILY", configuration.GetValue("MarketplaceSync:ReturnReconciliation:DailyIntervalSeconds", 86_400), 0, 0),
        new("STOCK_RECONCILE_SHORT", configuration.GetValue("MarketplaceSync:StockReconciliation:ShortIntervalSeconds", 900), 0, 0),
        new("STOCK_RECONCILE_MEDIUM", configuration.GetValue("MarketplaceSync:StockReconciliation:MediumIntervalSeconds", 3600), 0, 0),
        new("STOCK_RECONCILE_DAILY", configuration.GetValue("MarketplaceSync:StockReconciliation:DailyIntervalSeconds", 86_400), 0, 0)
    ];

    private static bool IsKnownDefault(ConnectionSyncPolicy current) =>
        current.IntervalSeconds == 300 && current.OverlapSeconds is 60 or 120 && current.JitterSeconds == 15
        || current.ResourceType == "ORDERS" && current.IntervalSeconds == 30 && current.OverlapSeconds == 600 && current.JitterSeconds == 2
        || current.ResourceType == "RETURNS" && current.IntervalSeconds == 60 && current.OverlapSeconds == 900 && current.JitterSeconds == 5;

    private static bool IsOrderBackgroundJob(string jobType) =>
        jobType is MarketplaceJobTypes.OrderRecoverySync
            or MarketplaceJobTypes.OrderReconciliation
            or MarketplaceJobTypes.OrderInvoiceReconciliation;

    private (string JobType, string DedupPrefix, string PayloadJson)? Definition(string resourceType, Guid connectionId) => resourceType switch
    {
        "ORDERS" => (MarketplaceJobTypes.OrderSync, $"scheduled:orders:{connectionId:N}", JsonSerializer.Serialize(new { connectionId, externalOrderId = (string?)null })),
        "ORDER_RECOVERY" => (MarketplaceJobTypes.OrderRecoverySync, $"scheduled:order-recovery:{connectionId:N}", JsonSerializer.Serialize(new { connectionId, externalOrderId = (string?)null })),
        "ORDER_LIFECYCLE" => (MarketplaceJobTypes.OrderStatusSync, $"scheduled:order-lifecycle:{connectionId:N}", JsonSerializer.Serialize(new { connectionId })),
        "ORDER_RECONCILE_SHORT" => (MarketplaceJobTypes.OrderReconciliation, $"scheduled:order-reconcile-short:{connectionId:N}", JsonSerializer.Serialize(new { connectionId, lookbackDays = ConfigInt("MarketplaceSync:OrderReconciliation:ShortLookbackDays", 1, 1, 14), batchSize = ConfigInt("MarketplaceSync:OrderReconciliation:ShortBatchSize", 25, 1, 100) })),
        "ORDER_RECONCILE_MEDIUM" => (MarketplaceJobTypes.OrderReconciliation, $"scheduled:order-reconcile-medium:{connectionId:N}", JsonSerializer.Serialize(new { connectionId, lookbackDays = ConfigInt("MarketplaceSync:OrderReconciliation:MediumLookbackDays", 3, 1, 30), batchSize = ConfigInt("MarketplaceSync:OrderReconciliation:MediumBatchSize", 50, 1, 100) })),
        "ORDER_RECONCILE_DAILY" => (MarketplaceJobTypes.OrderReconciliation, $"scheduled:order-reconcile-daily:{connectionId:N}", JsonSerializer.Serialize(new { connectionId, lookbackDays = ConfigInt("MarketplaceSync:OrderReconciliation:DailyLookbackDays", 90, 1, 90), batchSize = ConfigInt("MarketplaceSync:OrderReconciliation:DailyBatchSize", 50, 1, 100) })),
        "ORDER_INVOICE_RECONCILIATION" => (MarketplaceJobTypes.OrderInvoiceReconciliation, $"scheduled:order-invoice-reconciliation:{connectionId:N}", JsonSerializer.Serialize(new { connectionId, batchSize = ConfigInt("MarketplaceSync:OrderInvoiceReconciliation:BatchSize", 20, 1, 250) })),
        "RETURNS" => (MarketplaceJobTypes.ReturnSync, $"scheduled:returns:{connectionId:N}", JsonSerializer.Serialize(new { connectionId })),
        "RETURN_LIFECYCLE" => (MarketplaceJobTypes.ReturnStatusSync, $"scheduled:return-lifecycle:{connectionId:N}", JsonSerializer.Serialize(new { connectionId })),
        "RETURN_RECONCILE_SHORT" => (MarketplaceJobTypes.ReturnReconciliation, $"scheduled:return-reconcile-short:{connectionId:N}", JsonSerializer.Serialize(new { connectionId, lookbackDays = ConfigInt("MarketplaceSync:ReturnReconciliation:ShortLookbackDays", 1, 1, 14) })),
        "RETURN_RECONCILE_MEDIUM" => (MarketplaceJobTypes.ReturnReconciliation, $"scheduled:return-reconcile-medium:{connectionId:N}", JsonSerializer.Serialize(new { connectionId, lookbackDays = ConfigInt("MarketplaceSync:ReturnReconciliation:MediumLookbackDays", 3, 1, 30) })),
        "RETURN_RECONCILE_DAILY" => (MarketplaceJobTypes.ReturnReconciliation, $"scheduled:return-reconcile-daily:{connectionId:N}", JsonSerializer.Serialize(new { connectionId, lookbackDays = ConfigInt("MarketplaceSync:ReturnReconciliation:DailyLookbackDays", 90, 1, 90) })),
        "STOCK_RECONCILE_SHORT" => (MarketplaceJobTypes.StockReconciliation, $"scheduled:stock-reconcile-short:{connectionId:N}", JsonSerializer.Serialize(new { connectionId, lookbackHours = ConfigInt("MarketplaceSync:StockReconciliation:ShortLookbackHours", 1, 1, 24 * 7) })),
        "STOCK_RECONCILE_MEDIUM" => (MarketplaceJobTypes.StockReconciliation, $"scheduled:stock-reconcile-medium:{connectionId:N}", JsonSerializer.Serialize(new { connectionId, lookbackHours = ConfigInt("MarketplaceSync:StockReconciliation:MediumLookbackHours", 6, 1, 24 * 30) })),
        "STOCK_RECONCILE_DAILY" => (MarketplaceJobTypes.StockReconciliation, $"scheduled:stock-reconcile-daily:{connectionId:N}", JsonSerializer.Serialize(new { connectionId, lookbackHours = ConfigInt("MarketplaceSync:StockReconciliation:DailyLookbackHours", 720, 1, 24 * 90) })),
        "REFERENCE_DATA" => (MarketplaceJobTypes.ReferenceSync, $"scheduled:reference:{connectionId:N}", JsonSerializer.Serialize(new { connectionId, resourceType = "CATEGORIES", parentExternalId = (string?)null })),
        _ => null
    };

    private int ConfigInt(string key, int fallback, int minimum, int maximum) => Math.Clamp(configuration.GetValue(key, fallback), minimum, maximum);

    private static IntegrationJob NewJob(Guid tenantId, Guid? connectionId, string type, string dedup, string payload, DateTimeOffset availableAt, string correlationId) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = tenantId,
        ConnectionId = connectionId,
        JobType = type,
        PayloadJson = payload,
        PayloadVersion = 1,
        PayloadHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))),
        JobDedupKey = dedup,
        EffectIdempotencyKey = dedup,
        Priority = Priority(type),
        AvailableAt = availableAt,
        CorrelationId = correlationId,
        Version = 1
    };

    private static int Priority(string type) => type switch
    {
        MarketplaceJobTypes.OrderSync or MarketplaceJobTypes.OrderStatusSync or MarketplaceJobTypes.WebhookIngest => 0,
        MarketplaceJobTypes.OrderRecoverySync => 6,
        MarketplaceJobTypes.OrderReconciliation or MarketplaceJobTypes.ReturnReconciliation or MarketplaceJobTypes.StockReconciliation => 4,
        MarketplaceJobTypes.ReturnSync or MarketplaceJobTypes.ReturnStatusSync => 2,
        MarketplaceJobTypes.ProductSync or MarketplaceJobTypes.ReferenceSync => 5,
        _ => 3
    };

    private sealed record PolicyDefaults(string ResourceType, int IntervalSeconds, int OverlapSeconds, int JitterSeconds);

}
