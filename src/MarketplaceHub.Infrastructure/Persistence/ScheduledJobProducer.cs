using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace MarketplaceHub.Infrastructure.Persistence;

public sealed class ScheduledJobProducer(AppDbContext db, TimeProvider timeProvider, IConfiguration configuration) : IScheduledJobProducer
{
    public async Task<int> EnqueueDueAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var added = 0;
        await DisableProductAutomationAsync(cancellationToken);
        await EnsureDefaultPoliciesAsync(cancellationToken);
        await DisableExternalWriteAutomationAsync(cancellationToken);
        var policies = (await (from policy in db.ConnectionSyncPolicies.AsNoTracking()
                               join connection in db.PlatformConnections.AsNoTracking()
                                   on new { policy.TenantId, Id = policy.ConnectionId } equals new { connection.TenantId, connection.Id }
                               where policy.Enabled && (connection.Status == "ACTIVE" || connection.Status == "VERIFIED")
                                   && (connection.PlatformCode == "TRENDYOL" || connection.PlatformCode == "SHOPIFY")
                               select new { Policy = policy, Connection = connection }).ToListAsync(cancellationToken))
            .Where(row => row.Connection.PlatformCode == "TRENDYOL" || IsShopifyReadPolicy(row.Policy.ResourceType))
            // Keep the hot order stream ahead of lifecycle and reconciliation
            // scans. The query has no guaranteed row order, so an incidental
            // database order could otherwise enqueue a long lifecycle scan
            // first and block fresh orders behind the shared orders lane.
            .OrderBy(row => ScheduledPolicyPriority(row.Policy.ResourceType))
            .ThenBy(row => row.Connection.Id)
            .ToList();
        var bootstrapConnections = (await db.IntegrationJobs.AsNoTracking()
            .Where(x => x.ConnectionId != null
                && x.JobDedupKey.StartsWith(MarketplaceJobTypes.ActivationBootstrapPrefix)
                && (x.Status == JobStatus.Pending || x.Status == JobStatus.Leased || x.Status == JobStatus.RetryScheduled))
            .Select(x => x.ConnectionId!.Value)
            .Distinct()
            .ToListAsync(cancellationToken)).ToHashSet();
        var reservedExecutionGroups = new HashSet<string>(StringComparer.Ordinal);
        var backgroundOrderReservations = new HashSet<Guid>();

        foreach (var row in policies)
        {
            if (bootstrapConnections.Contains(row.Connection.Id)) continue;
            var definition = Definition(row.Policy.ResourceType, row.Connection.Id, row.Connection.PlatformCode);
            if (definition is null) continue;

            var interval = Math.Clamp(row.Policy.IntervalSeconds, 30, 86_400);
            // Different scheduled job types can share one provider execution
            // lane (for example order sync and order reconciliation). Checking
            // the execution group keeps the queue coalesced before it reaches
            // the worker's advisory lock.
            var activeJobTypes = await db.IntegrationJobs.AsNoTracking()
                .Where(x => x.TenantId == row.Policy.TenantId && x.ConnectionId == row.Connection.Id
                    && (x.Status == JobStatus.Pending || x.Status == JobStatus.Leased || x.Status == JobStatus.RetryScheduled))
                .Select(x => x.JobType)
                .Distinct()
                .ToListAsync(cancellationToken);
            var executionGroup = MarketplaceSyncExecutionLock.GroupFor(definition.Value.JobType);
            var active = activeJobTypes.Any(jobType => MarketplaceSyncExecutionLock.GroupFor(jobType) == executionGroup);
            var isHotOrder = definition.Value.JobType is MarketplaceJobTypes.OrderSync or MarketplaceJobTypes.ShopifyOrderSync;
            var hotOrderAlreadyQueued = activeJobTypes.Contains(MarketplaceJobTypes.OrderSync, StringComparer.Ordinal)
                || activeJobTypes.Contains(MarketplaceJobTypes.ShopifyOrderSync, StringComparer.Ordinal);
            var isOrderLifecycle = definition.Value.JobType is MarketplaceJobTypes.OrderStatusSync or MarketplaceJobTypes.ShopifyOrderStatusSync;
            var lifecycleAlreadyQueued = activeJobTypes.Contains(MarketplaceJobTypes.OrderStatusSync, StringComparer.Ordinal)
                || activeJobTypes.Contains(MarketplaceJobTypes.ShopifyOrderStatusSync, StringComparer.Ordinal);
            var activeOrderLane = activeJobTypes.Any(jobType => MarketplaceSyncExecutionLock.GroupFor(jobType) == "orders");
            var canQueueHotOrderBehindOrderLane = isHotOrder && activeOrderLane && !hotOrderAlreadyQueued;
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
            var reservationKey = $"{row.Policy.TenantId:N}:{row.Connection.Id:N}:{executionGroup}";
            if ((active && !canQueueHotOrderBehindOrderLane && !canQueueLifecycleBehindOrderLane && !canQueueOrderBackgroundBehindOrderLane)
                || (reservedExecutionGroups.Contains(reservationKey) && !canQueueHotOrderBehindOrderLane && !canQueueLifecycleBehindOrderLane && !canQueueOrderBackgroundBehindOrderLane)) continue;
            var latest = await db.IntegrationJobs.AsNoTracking()
                .Where(x => x.TenantId == row.Policy.TenantId && x.ConnectionId == row.Connection.Id && x.JobType == definition.Value.JobType && x.JobDedupKey.StartsWith(definition.Value.DedupPrefix))
                .OrderByDescending(x => x.CreatedAt).Select(x => (DateTimeOffset?)x.CreatedAt).FirstOrDefaultAsync(cancellationToken);
            var hasOrderSnapshots = definition.Value.JobType is not (MarketplaceJobTypes.OrderSync or MarketplaceJobTypes.ShopifyOrderSync)
                || await db.Orders.AsNoTracking().AnyAsync(x => x.TenantId == row.Policy.TenantId && x.ConnectionId == row.Connection.Id, cancellationToken);
            var jitterSeconds = StableJitterSeconds(row.Connection.Id, row.Policy.ResourceType, row.Policy.JitterSeconds);
            if (hasOrderSnapshots && latest is not null && latest.Value.AddSeconds(interval + jitterSeconds) > now) continue;

            var bucket = now.ToUnixTimeSeconds() / interval;
            var dedup = $"{definition.Value.DedupPrefix}:{bucket}";
            if (await db.IntegrationJobs.AsNoTracking().AnyAsync(x => x.TenantId == row.Policy.TenantId && x.JobType == definition.Value.JobType && x.JobDedupKey == dedup, cancellationToken)) continue;
            reservedExecutionGroups.Add(reservationKey);
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
        catch (DbUpdateException exception) when (IsExpectedDedupRace(exception))
        {
            // Another worker may have won the unique dedup race. Clear pending tracked rows;
            // the next scheduler pass will observe the committed jobs.
            foreach (var entry in db.ChangeTracker.Entries<IntegrationJob>().Where(x => x.State == EntityState.Added)) entry.State = EntityState.Detached;
            return 0;
        }
    }

    private async Task DisableProductAutomationAsync(CancellationToken cancellationToken)
    {
        var productPolicies = await (from policy in db.ConnectionSyncPolicies
                                     join connection in db.PlatformConnections
                                         on new { policy.TenantId, Id = policy.ConnectionId } equals new { connection.TenantId, connection.Id }
                                     where policy.ResourceType == "PRODUCTS" && policy.Enabled && connection.PlatformCode == "TRENDYOL"
                                     select policy)
            .ToListAsync(cancellationToken);
        foreach (var policy in productPolicies)
        {
            policy.Enabled = false;
            policy.Version++;
        }

        var scheduledProductJobs = await db.IntegrationJobs
            .Where(x => x.JobType == MarketplaceJobTypes.ProductSync
                && x.JobDedupKey.StartsWith("scheduled:products:")
                && x.ConnectionId != null
                && db.PlatformConnections.Any(connection => connection.TenantId == x.TenantId && connection.Id == x.ConnectionId && connection.PlatformCode == "TRENDYOL")
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

    private async Task DisableExternalWriteAutomationAsync(CancellationToken cancellationToken)
    {
        var connections = await db.PlatformConnections
            .Where(x => x.PlatformCode == "TRENDYOL")
            .ToListAsync(cancellationToken);
        var blockedConnectionIds = connections
            .Where(x => !WritesEnabled(x.SettingsJson))
            .Select(x => new { x.TenantId, ConnectionId = x.Id })
            .ToList();
        if (blockedConnectionIds.Count == 0) return;

        var connectionSettingsChanged = false;
        foreach (var connection in connections.Where(x => !WritesEnabled(x.SettingsJson) && StoredWritesEnabled(x.SettingsJson)))
        {
            connection.SettingsJson = DisableStoredWrites(connection.SettingsJson);
            connection.Version++;
            connectionSettingsChanged = true;
        }

        var connectionIds = blockedConnectionIds.Select(x => x.ConnectionId).ToArray();
        var policies = await db.ConnectionSyncPolicies
            .Where(x => connectionIds.Contains(x.ConnectionId) && x.Enabled)
            .ToListAsync(cancellationToken);
        foreach (var policy in policies.Where(x => x.ResourceType is "STOCK_RECONCILE_SHORT" or "STOCK_RECONCILE_MEDIUM" or "STOCK_RECONCILE_DAILY"))
        {
            policy.Enabled = false;
            policy.Version++;
        }

        var jobs = await db.IntegrationJobs
            .Where(x => connectionIds.Contains(x.ConnectionId!.Value)
                && (x.JobType == MarketplaceJobTypes.StockReconciliation
                    || x.JobType == MarketplaceJobTypes.PriceInventorySync
                    || x.JobType == MarketplaceJobTypes.StockProjectionDispatch)
                && (x.Status == JobStatus.Pending || x.Status == JobStatus.RetryScheduled))
            .ToListAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        foreach (var job in jobs)
        {
            job.Status = JobStatus.Cancelled;
            job.CompletedAt = now;
            job.Version++;
        }

        if (connectionSettingsChanged || policies.Any(x => x.ResourceType is "STOCK_RECONCILE_SHORT" or "STOCK_RECONCILE_MEDIUM" or "STOCK_RECONCILE_DAILY") || jobs.Count > 0)
            await db.SaveChangesAsync(cancellationToken);
    }

    private async Task EnsureDefaultPoliciesAsync(CancellationToken cancellationToken)
    {
        var operationalConnections = await db.PlatformConnections.AsNoTracking()
            .Where(x => (x.Status == "ACTIVE" || x.Status == "VERIFIED") && (x.PlatformCode == "TRENDYOL" || x.PlatformCode == "SHOPIFY"))
            .Select(x => new { x.TenantId, ConnectionId = x.Id, x.PlatformCode })
            .ToListAsync(cancellationToken);
        if (operationalConnections.Count == 0) return;

        var connectionIds = operationalConnections.Select(x => x.ConnectionId).ToArray();
        var existing = await db.ConnectionSyncPolicies
            .Where(x => connectionIds.Contains(x.ConnectionId) && (x.ResourceType == "PRODUCTS" || x.ResourceType == "ORDERS" || x.ResourceType == "ORDER_RECOVERY" || x.ResourceType == "ORDER_LIFECYCLE" || x.ResourceType == "ORDER_RECONCILE_SHORT" || x.ResourceType == "ORDER_RECONCILE_MEDIUM" || x.ResourceType == "ORDER_RECONCILE_DAILY" || x.ResourceType == "ORDER_INVOICE_RECONCILIATION" || x.ResourceType == "RETURNS" || x.ResourceType == "RETURN_LIFECYCLE" || x.ResourceType == "RETURN_RECONCILE_SHORT" || x.ResourceType == "RETURN_RECONCILE_MEDIUM" || x.ResourceType == "RETURN_RECONCILE_DAILY" || x.ResourceType == "STOCK_RECONCILE_SHORT" || x.ResourceType == "STOCK_RECONCILE_MEDIUM" || x.ResourceType == "STOCK_RECONCILE_DAILY" || x.ResourceType == MarketplaceExternalWritePolicies.Price || x.ResourceType == MarketplaceExternalWritePolicies.Stock || x.ResourceType == MarketplaceExternalWritePolicies.Shipment || x.ResourceType == MarketplaceExternalWritePolicies.Return))
            .ToListAsync(cancellationToken);
        var obsoleteProductPolicies = await (from policy in db.ConnectionSyncPolicies
                                             join connection in db.PlatformConnections
                                                 on new { policy.TenantId, Id = policy.ConnectionId } equals new { connection.TenantId, connection.Id }
                                             where connectionIds.Contains(policy.ConnectionId) && connection.PlatformCode == "TRENDYOL" && policy.ResourceType == "PRODUCTS" && policy.Enabled
                                             select policy)
            .ToListAsync(cancellationToken);
        foreach (var policy in obsoleteProductPolicies)
        {
            policy.Enabled = false;
            policy.Version++;
        }
        foreach (var connection in operationalConnections)
        {
            var defaultsForConnection = DefaultPolicies()
                .Where(defaults => connection.PlatformCode == "SHOPIFY" ? IsShopifyReadPolicy(defaults.ResourceType) : defaults.ResourceType != "PRODUCTS")
                .Select(defaults => connection.PlatformCode == "SHOPIFY" && defaults.ResourceType == "ORDERS"
                    ? defaults with { IntervalSeconds = 900, JitterSeconds = 30 }
                    : defaults);
            foreach (var defaults in defaultsForConnection)
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
        new("ORDERS", configuration.GetValue("MarketplaceSync:Orders:IntervalSeconds", 60), configuration.GetValue("MarketplaceSync:Orders:SafetyWindowSeconds", 600), configuration.GetValue("MarketplaceSync:Orders:JitterSeconds", 5)),
        new("PRODUCTS", configuration.GetValue("MarketplaceSync:Products:IntervalSeconds", 86_400), configuration.GetValue("MarketplaceSync:Products:SafetyWindowSeconds", 600), configuration.GetValue("MarketplaceSync:Products:JitterSeconds", 900)),
        new("ORDER_RECOVERY", configuration.GetValue("MarketplaceSync:OrderRecovery:IntervalSeconds", 900), configuration.GetValue("MarketplaceSync:OrderRecovery:SafetyWindowSeconds", 600), configuration.GetValue("MarketplaceSync:OrderRecovery:JitterSeconds", 30)),
        new("ORDER_LIFECYCLE", configuration.GetValue("MarketplaceSync:OrderLifecycle:IntervalSeconds", 180), 0, configuration.GetValue("MarketplaceSync:OrderLifecycle:JitterSeconds", 10)),
        new("ORDER_RECONCILE_SHORT", configuration.GetValue("MarketplaceSync:OrderReconciliation:ShortIntervalSeconds", 900), 0, configuration.GetValue("MarketplaceSync:OrderReconciliation:ShortJitterSeconds", 30)),
        new("ORDER_RECONCILE_MEDIUM", configuration.GetValue("MarketplaceSync:OrderReconciliation:MediumIntervalSeconds", 3600), 0, configuration.GetValue("MarketplaceSync:OrderReconciliation:MediumJitterSeconds", 120)),
        new("ORDER_RECONCILE_DAILY", configuration.GetValue("MarketplaceSync:OrderReconciliation:DailyIntervalSeconds", 86_400), 0, configuration.GetValue("MarketplaceSync:OrderReconciliation:DailyJitterSeconds", 900)),
        new("ORDER_INVOICE_RECONCILIATION", configuration.GetValue("MarketplaceSync:OrderInvoiceReconciliation:IntervalSeconds", 900), 0, configuration.GetValue("MarketplaceSync:OrderInvoiceReconciliation:JitterSeconds", 30)),
        new("RETURNS", configuration.GetValue("MarketplaceSync:Returns:IntervalSeconds", 180), configuration.GetValue("MarketplaceSync:Returns:SafetyWindowSeconds", 900), configuration.GetValue("MarketplaceSync:Returns:JitterSeconds", 10)),
        new("RETURN_LIFECYCLE", configuration.GetValue("MarketplaceSync:ReturnLifecycle:IntervalSeconds", 180), 0, configuration.GetValue("MarketplaceSync:ReturnLifecycle:JitterSeconds", 10)),
        new("RETURN_RECONCILE_SHORT", configuration.GetValue("MarketplaceSync:ReturnReconciliation:ShortIntervalSeconds", 900), 0, configuration.GetValue("MarketplaceSync:ReturnReconciliation:ShortJitterSeconds", 30)),
        new("RETURN_RECONCILE_MEDIUM", configuration.GetValue("MarketplaceSync:ReturnReconciliation:MediumIntervalSeconds", 3600), 0, configuration.GetValue("MarketplaceSync:ReturnReconciliation:MediumJitterSeconds", 120)),
        new("RETURN_RECONCILE_DAILY", configuration.GetValue("MarketplaceSync:ReturnReconciliation:DailyIntervalSeconds", 86_400), 0, configuration.GetValue("MarketplaceSync:ReturnReconciliation:DailyJitterSeconds", 900)),
        new("STOCK_RECONCILE_SHORT", configuration.GetValue("MarketplaceSync:StockReconciliation:ShortIntervalSeconds", 900), 0, configuration.GetValue("MarketplaceSync:StockReconciliation:ShortJitterSeconds", 30)),
        new("STOCK_RECONCILE_MEDIUM", configuration.GetValue("MarketplaceSync:StockReconciliation:MediumIntervalSeconds", 3600), 0, configuration.GetValue("MarketplaceSync:StockReconciliation:MediumJitterSeconds", 120)),
        new("STOCK_RECONCILE_DAILY", configuration.GetValue("MarketplaceSync:StockReconciliation:DailyIntervalSeconds", 86_400), 0, configuration.GetValue("MarketplaceSync:StockReconciliation:DailyJitterSeconds", 900)),
        new(MarketplaceExternalWritePolicies.Price, 0, 0, 0),
        new(MarketplaceExternalWritePolicies.Stock, 0, 0, 0),
        new(MarketplaceExternalWritePolicies.Shipment, 0, 0, 0),
        new(MarketplaceExternalWritePolicies.Return, 0, 0, 0)
    ];

    private static bool IsKnownDefault(ConnectionSyncPolicy current)
    {
        if (current.IntervalSeconds == 300 && (current.OverlapSeconds == 60 || current.OverlapSeconds == 120) && current.JitterSeconds == 15) return true;
        return current.ResourceType switch
        {
            "ORDERS" => current.IntervalSeconds == 30 && current.OverlapSeconds == 600 && current.JitterSeconds == 2
                || current.IntervalSeconds == 60 && current.OverlapSeconds == 600 && (current.JitterSeconds == 0 || current.JitterSeconds == 5)
                || current.IntervalSeconds == 900 && current.OverlapSeconds == 600 && (current.JitterSeconds == 0 || current.JitterSeconds == 30),
            "PRODUCTS" => current.IntervalSeconds == 86_400 && current.OverlapSeconds == 600 && (current.JitterSeconds == 0 || current.JitterSeconds == 900),
            "ORDER_RECOVERY" => current.IntervalSeconds == 900 && current.OverlapSeconds == 600 && (current.JitterSeconds == 0 || current.JitterSeconds == 30),
            "RETURNS" => current.IntervalSeconds == 60 && current.OverlapSeconds == 900 && current.JitterSeconds == 5
                || current.IntervalSeconds == 180 && current.OverlapSeconds == 900 && (current.JitterSeconds == 0 || current.JitterSeconds == 10),
            "ORDER_LIFECYCLE" or "RETURN_LIFECYCLE" => current.IntervalSeconds == 180 && current.OverlapSeconds == 0 && (current.JitterSeconds == 0 || current.JitterSeconds == 10),
            "ORDER_RECONCILE_SHORT" or "RETURN_RECONCILE_SHORT" or "STOCK_RECONCILE_SHORT" => current.IntervalSeconds == 900 && current.OverlapSeconds == 0 && (current.JitterSeconds == 0 || current.JitterSeconds == 30),
            "ORDER_RECONCILE_MEDIUM" or "RETURN_RECONCILE_MEDIUM" or "STOCK_RECONCILE_MEDIUM" => current.IntervalSeconds == 3600 && current.OverlapSeconds == 0 && (current.JitterSeconds == 0 || current.JitterSeconds == 120),
            "ORDER_RECONCILE_DAILY" or "RETURN_RECONCILE_DAILY" or "STOCK_RECONCILE_DAILY" => current.IntervalSeconds == 86_400 && current.OverlapSeconds == 0 && (current.JitterSeconds == 0 || current.JitterSeconds == 900),
            "ORDER_INVOICE_RECONCILIATION" => current.IntervalSeconds == 900 && current.OverlapSeconds == 0 && (current.JitterSeconds == 0 || current.JitterSeconds == 30),
            _ => false
        };
    }

    private static int StableJitterSeconds(Guid connectionId, string resourceType, int maximum)
    {
        if (maximum <= 0) return 0;
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes($"{connectionId:N}:{resourceType}"));
        return (int)(BitConverter.ToUInt32(digest, 0) % (uint)(maximum + 1));
    }

    private static int ScheduledPolicyPriority(string resourceType) => resourceType switch
    {
        "ORDERS" => 0,
        "PRODUCTS" => 5,
        "ORDER_RECOVERY" => 10,
        "ORDER_LIFECYCLE" => 20,
        "ORDER_RECONCILE_SHORT" => 30,
        "ORDER_RECONCILE_MEDIUM" => 31,
        "ORDER_RECONCILE_DAILY" => 32,
        "ORDER_INVOICE_RECONCILIATION" => 33,
        "RETURNS" => 40,
        "RETURN_LIFECYCLE" => 50,
        "RETURN_RECONCILE_SHORT" => 60,
        "RETURN_RECONCILE_MEDIUM" => 61,
        "RETURN_RECONCILE_DAILY" => 62,
        "STOCK_RECONCILE_SHORT" => 70,
        "STOCK_RECONCILE_MEDIUM" => 71,
        "STOCK_RECONCILE_DAILY" => 72,
        _ => 100
    };

    private static bool IsOrderBackgroundJob(string jobType) =>
        jobType is MarketplaceJobTypes.OrderRecoverySync
            or MarketplaceJobTypes.ShopifyOrderRecoverySync
            or MarketplaceJobTypes.OrderReconciliation
            or MarketplaceJobTypes.ShopifyOrderReconciliation
            or MarketplaceJobTypes.OrderInvoiceReconciliation
            or MarketplaceJobTypes.ShopifyOrderInvoiceReconciliation;

    private static bool IsShopifyReadPolicy(string resourceType) => resourceType is
        "PRODUCTS"
        or "ORDERS"
        or "ORDER_RECOVERY"
        or "ORDER_LIFECYCLE"
        or "ORDER_RECONCILE_SHORT"
        or "ORDER_RECONCILE_MEDIUM"
        or "ORDER_RECONCILE_DAILY";

    private (string JobType, string DedupPrefix, string PayloadJson)? Definition(string resourceType, Guid connectionId, string platformCode) => resourceType switch
    {
        "ORDERS" => (MarketplaceJobTypes.ForPlatform(platformCode, MarketplaceJobTypes.OrderSync), $"scheduled:orders:{connectionId:N}", JsonSerializer.Serialize(new { connectionId, externalOrderId = (string?)null })),
        "PRODUCTS" => (MarketplaceJobTypes.ForPlatform(platformCode, MarketplaceJobTypes.ProductSync), $"scheduled:products:{connectionId:N}", JsonSerializer.Serialize(new { connectionId, full = false, includeArchived = true, includeDrafts = false })),
        "ORDER_RECOVERY" => (MarketplaceJobTypes.ForPlatform(platformCode, MarketplaceJobTypes.OrderRecoverySync), $"scheduled:order-recovery:{connectionId:N}", JsonSerializer.Serialize(new { connectionId, externalOrderId = (string?)null })),
        "ORDER_LIFECYCLE" => (MarketplaceJobTypes.ForPlatform(platformCode, MarketplaceJobTypes.OrderStatusSync), $"scheduled:order-lifecycle:{connectionId:N}", JsonSerializer.Serialize(new { connectionId })),
        "ORDER_RECONCILE_SHORT" => (MarketplaceJobTypes.ForPlatform(platformCode, MarketplaceJobTypes.OrderReconciliation), $"scheduled:order-reconcile-short:{connectionId:N}", JsonSerializer.Serialize(new { connectionId, lookbackDays = ConfigInt("MarketplaceSync:OrderReconciliation:ShortLookbackDays", 1, 1, 14), batchSize = ConfigInt("MarketplaceSync:OrderReconciliation:ShortBatchSize", 25, 1, 100) })),
        "ORDER_RECONCILE_MEDIUM" => (MarketplaceJobTypes.ForPlatform(platformCode, MarketplaceJobTypes.OrderReconciliation), $"scheduled:order-reconcile-medium:{connectionId:N}", JsonSerializer.Serialize(new { connectionId, lookbackDays = ConfigInt("MarketplaceSync:OrderReconciliation:MediumLookbackDays", 3, 1, 30), batchSize = ConfigInt("MarketplaceSync:OrderReconciliation:MediumBatchSize", 50, 1, 100) })),
        "ORDER_RECONCILE_DAILY" => (MarketplaceJobTypes.ForPlatform(platformCode, MarketplaceJobTypes.OrderReconciliation), $"scheduled:order-reconcile-daily:{connectionId:N}", JsonSerializer.Serialize(new { connectionId, lookbackDays = ConfigInt("MarketplaceSync:OrderReconciliation:DailyLookbackDays", 90, 1, 90), batchSize = ConfigInt("MarketplaceSync:OrderReconciliation:DailyBatchSize", 50, 1, 100) })),
        "ORDER_INVOICE_RECONCILIATION" => (MarketplaceJobTypes.ForPlatform(platformCode, MarketplaceJobTypes.OrderInvoiceReconciliation), $"scheduled:order-invoice-reconciliation:{connectionId:N}", JsonSerializer.Serialize(new { connectionId, batchSize = ConfigInt("MarketplaceSync:OrderInvoiceReconciliation:BatchSize", 20, 1, 250) })),
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

    private bool WritesEnabled(string settingsJson)
    {
        if (!configuration.GetValue<bool>("FeatureFlags:ExternalWrites")) return false;
        return StoredWritesEnabled(settingsJson);
    }

    private static bool StoredWritesEnabled(string settingsJson)
    {
        try
        {
            using var document = JsonDocument.Parse(settingsJson);
            return document.RootElement.TryGetProperty("ExternalWritesEnabled", out var enabled) && enabled.ValueKind == JsonValueKind.True;
        }
        catch (JsonException) { return false; }
    }

    private static string DisableStoredWrites(string settingsJson)
    {
        try
        {
            using var document = JsonDocument.Parse(settingsJson);
            var userAgent = document.RootElement.TryGetProperty("UserAgentIdentity", out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? ""
                : "";
            return JsonSerializer.Serialize(new { UserAgentIdentity = userAgent, ExternalWritesEnabled = false });
        }
        catch (JsonException) { return settingsJson; }
    }

    private static bool IsExpectedDedupRace(DbUpdateException exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "IX_jobs_TenantId_JobType_JobDedupKey" }) return true;
        }

        return false;
    }

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
        MarketplaceJobTypes.OrderSync or MarketplaceJobTypes.ShopifyOrderSync or MarketplaceJobTypes.OrderStatusSync or MarketplaceJobTypes.ShopifyOrderStatusSync or MarketplaceJobTypes.WebhookIngest or MarketplaceJobTypes.ShopifyWebhookIngest => 0,
        MarketplaceJobTypes.OrderRecoverySync or MarketplaceJobTypes.ShopifyOrderRecoverySync => 6,
        MarketplaceJobTypes.OrderReconciliation or MarketplaceJobTypes.ShopifyOrderReconciliation or MarketplaceJobTypes.ReturnReconciliation or MarketplaceJobTypes.StockReconciliation => 4,
        MarketplaceJobTypes.ReturnSync or MarketplaceJobTypes.ReturnStatusSync => 2,
        MarketplaceJobTypes.ProductSync or MarketplaceJobTypes.ShopifyProductSync or MarketplaceJobTypes.ReferenceSync => 5,
        _ => 3
    };

    private sealed record PolicyDefaults(string ResourceType, int IntervalSeconds, int OverlapSeconds, int JitterSeconds);

}
