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
        await EnsureDefaultOrderPoliciesAsync(cancellationToken);
        var policies = await (from policy in db.ConnectionSyncPolicies.AsNoTracking()
                              join connection in db.PlatformConnections.AsNoTracking()
                                  on new { policy.TenantId, Id = policy.ConnectionId } equals new { connection.TenantId, connection.Id }
                              where policy.Enabled && (connection.Status == "ACTIVE" || connection.Status == "VERIFIED") && connection.PlatformCode == "TRENDYOL"
                              select new { Policy = policy, Connection = connection }).ToListAsync(cancellationToken);

        foreach (var row in policies)
        {
            var definition = Definition(row.Policy.ResourceType, row.Connection.Id);
            if (definition is null) continue;

            var interval = Math.Clamp(row.Policy.IntervalSeconds, 30, 86_400);
            var active = await db.IntegrationJobs.AsNoTracking()
                .AnyAsync(x => x.TenantId == row.Policy.TenantId && x.ConnectionId == row.Connection.Id && x.JobType == definition.Value.JobType
                    && (x.Status == JobStatus.Pending || x.Status == JobStatus.Leased || x.Status == JobStatus.RetryScheduled), cancellationToken);
            if (active) continue;
            var latest = await db.IntegrationJobs.AsNoTracking()
                .Where(x => x.TenantId == row.Policy.TenantId && x.ConnectionId == row.Connection.Id && x.JobType == definition.Value.JobType && x.JobDedupKey.StartsWith(definition.Value.DedupPrefix))
                .OrderByDescending(x => x.CreatedAt).Select(x => (DateTimeOffset?)x.CreatedAt).FirstOrDefaultAsync(cancellationToken);
            var hasOrderSnapshots = definition.Value.JobType != MarketplaceJobTypes.OrderSync
                || await db.Orders.AsNoTracking().AnyAsync(x => x.TenantId == row.Policy.TenantId && x.ConnectionId == row.Connection.Id, cancellationToken);
            var hasCatalogSnapshots = definition.Value.JobType != MarketplaceJobTypes.ProductSync
                || await db.MarketplaceProductLinks.AsNoTracking().AnyAsync(x => x.TenantId == row.Policy.TenantId && x.ConnectionId == row.Connection.Id, cancellationToken);
            // A completed no-op/failure before the first snapshot must not suppress
            // the initial import for the whole interval.
            if (hasOrderSnapshots && hasCatalogSnapshots && latest is not null && latest > now.AddSeconds(-interval)) continue;

            var bucket = now.ToUnixTimeSeconds() / interval;
            var dedup = $"{definition.Value.DedupPrefix}:{bucket}";
            if (await db.IntegrationJobs.AsNoTracking().AnyAsync(x => x.TenantId == row.Policy.TenantId && x.JobType == definition.Value.JobType && x.JobDedupKey == dedup, cancellationToken)) continue;
            var jitter = row.Policy.JitterSeconds <= 0 ? 0 : StableJitter(row.Policy.Id, bucket, row.Policy.JitterSeconds);
            db.IntegrationJobs.Add(NewJob(row.Policy.TenantId, row.Connection.Id, definition.Value.JobType, dedup, definition.Value.PayloadJson, now.AddSeconds(jitter), $"scheduler-{Guid.NewGuid():N}"));
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

    private async Task EnsureDefaultOrderPoliciesAsync(CancellationToken cancellationToken)
    {
        var operationalConnections = await db.PlatformConnections.AsNoTracking()
            .Where(x => (x.Status == "ACTIVE" || x.Status == "VERIFIED") && x.PlatformCode == "TRENDYOL")
            .Select(x => new { x.TenantId, ConnectionId = x.Id })
            .ToListAsync(cancellationToken);
        if (operationalConnections.Count == 0) return;

        var connectionIds = operationalConnections.Select(x => x.ConnectionId).ToArray();
        var existing = await db.ConnectionSyncPolicies
            .Where(x => connectionIds.Contains(x.ConnectionId) && (x.ResourceType == "ORDERS" || x.ResourceType == "ORDER_RECOVERY" || x.ResourceType == "ORDER_LIFECYCLE" || x.ResourceType == "ORDER_RECONCILE_SHORT" || x.ResourceType == "ORDER_RECONCILE_MEDIUM" || x.ResourceType == "ORDER_RECONCILE_DAILY" || x.ResourceType == "RETURNS" || x.ResourceType == "RETURN_LIFECYCLE" || x.ResourceType == "RETURN_RECONCILE_DAILY" || x.ResourceType == "STOCK_RECONCILE_SHORT" || x.ResourceType == "STOCK_RECONCILE_MEDIUM" || x.ResourceType == "STOCK_RECONCILE_DAILY"))
            .ToListAsync(cancellationToken);
        foreach (var connection in operationalConnections)
        {
            foreach (var defaults in DefaultPolicies())
            {
                var current = existing.SingleOrDefault(x => x.TenantId == connection.TenantId && x.ConnectionId == connection.ConnectionId && x.ResourceType == defaults.ResourceType);
                if (current is not null)
                {
                    // Upgrade only the exact legacy defaults. Explicit user choices remain untouched.
                    if (current.IntervalSeconds == 300 && current.OverlapSeconds == 120 && current.JitterSeconds == 15)
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
        if (db.ChangeTracker.Entries<ConnectionSyncPolicy>().Any(x => x.State == EntityState.Added))
            await db.SaveChangesAsync(cancellationToken);
    }

    private IReadOnlyList<PolicyDefaults> DefaultPolicies() =>
    [
        new("ORDERS", configuration.GetValue("MarketplaceSync:Orders:IntervalSeconds", 30), configuration.GetValue("MarketplaceSync:Orders:SafetyWindowSeconds", 600), configuration.GetValue("MarketplaceSync:Orders:JitterSeconds", 2)),
        new("ORDER_RECOVERY", configuration.GetValue("MarketplaceSync:OrderRecovery:IntervalSeconds", 900), configuration.GetValue("MarketplaceSync:OrderRecovery:SafetyWindowSeconds", 600), configuration.GetValue("MarketplaceSync:OrderRecovery:JitterSeconds", 30)),
        new("ORDER_LIFECYCLE", configuration.GetValue("MarketplaceSync:OrderLifecycle:IntervalSeconds", 180), 0, configuration.GetValue("MarketplaceSync:OrderLifecycle:JitterSeconds", 10)),
        new("ORDER_RECONCILE_SHORT", 900, 0, 30),
        new("ORDER_RECONCILE_MEDIUM", 3600, 0, 120),
        new("ORDER_RECONCILE_DAILY", 86_400, 0, 900),
        new("RETURNS", configuration.GetValue("MarketplaceSync:Returns:IntervalSeconds", 60), configuration.GetValue("MarketplaceSync:Returns:SafetyWindowSeconds", 900), configuration.GetValue("MarketplaceSync:Returns:JitterSeconds", 5)),
        new("RETURN_LIFECYCLE", configuration.GetValue("MarketplaceSync:ReturnLifecycle:IntervalSeconds", 180), 0, configuration.GetValue("MarketplaceSync:ReturnLifecycle:JitterSeconds", 10)),
        new("RETURN_RECONCILE_DAILY", 86_400, 0, 900),
        new("STOCK_RECONCILE_SHORT", 900, 0, 30),
        new("STOCK_RECONCILE_MEDIUM", 3600, 0, 120),
        new("STOCK_RECONCILE_DAILY", 86_400, 0, 900)
    ];

    private static (string JobType, string DedupPrefix, string PayloadJson)? Definition(string resourceType, Guid connectionId) => resourceType switch
    {
        "ORDERS" => (MarketplaceJobTypes.OrderSync, $"scheduled:orders:{connectionId:N}", JsonSerializer.Serialize(new { connectionId, externalOrderId = (string?)null })),
        "ORDER_RECOVERY" => (MarketplaceJobTypes.OrderRecoverySync, $"scheduled:order-recovery:{connectionId:N}", JsonSerializer.Serialize(new { connectionId, externalOrderId = (string?)null })),
        "ORDER_LIFECYCLE" => (MarketplaceJobTypes.OrderStatusSync, $"scheduled:order-lifecycle:{connectionId:N}", JsonSerializer.Serialize(new { connectionId })),
        "ORDER_RECONCILE_SHORT" => (MarketplaceJobTypes.OrderReconciliation, $"scheduled:order-reconcile-short:{connectionId:N}", JsonSerializer.Serialize(new { connectionId, lookbackDays = 1 })),
        "ORDER_RECONCILE_MEDIUM" => (MarketplaceJobTypes.OrderReconciliation, $"scheduled:order-reconcile-medium:{connectionId:N}", JsonSerializer.Serialize(new { connectionId, lookbackDays = 3 })),
        "ORDER_RECONCILE_DAILY" => (MarketplaceJobTypes.OrderReconciliation, $"scheduled:order-reconcile-daily:{connectionId:N}", JsonSerializer.Serialize(new { connectionId, lookbackDays = 90 })),
        "RETURNS" => (MarketplaceJobTypes.ReturnSync, $"scheduled:returns:{connectionId:N}", JsonSerializer.Serialize(new { connectionId })),
        "RETURN_LIFECYCLE" => (MarketplaceJobTypes.ReturnStatusSync, $"scheduled:return-lifecycle:{connectionId:N}", JsonSerializer.Serialize(new { connectionId })),
        "RETURN_RECONCILE_DAILY" => (MarketplaceJobTypes.ReturnReconciliation, $"scheduled:return-reconcile-daily:{connectionId:N}", JsonSerializer.Serialize(new { connectionId, lookbackDays = 90 })),
        "STOCK_RECONCILE_SHORT" => (MarketplaceJobTypes.StockReconciliation, $"scheduled:stock-reconcile-short:{connectionId:N}", JsonSerializer.Serialize(new { connectionId, lookbackHours = 1 })),
        "STOCK_RECONCILE_MEDIUM" => (MarketplaceJobTypes.StockReconciliation, $"scheduled:stock-reconcile-medium:{connectionId:N}", JsonSerializer.Serialize(new { connectionId, lookbackHours = 6 })),
        "STOCK_RECONCILE_DAILY" => (MarketplaceJobTypes.StockReconciliation, $"scheduled:stock-reconcile-daily:{connectionId:N}", JsonSerializer.Serialize(new { connectionId, lookbackHours = 720 })),
        "REFERENCE_DATA" => (MarketplaceJobTypes.ReferenceSync, $"scheduled:reference:{connectionId:N}", JsonSerializer.Serialize(new { connectionId, resourceType = "CATEGORIES", parentExternalId = (string?)null })),
        _ => null
    };

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

    private static int StableJitter(Guid policyId, long bucket, int maxSeconds)
    {
        Span<byte> input = stackalloc byte[24];
        policyId.TryWriteBytes(input[..16]);
        BitConverter.TryWriteBytes(input[16..], bucket);
        var hash = SHA256.HashData(input);
        return (int)(BitConverter.ToUInt32(hash, 0) % (uint)(maxSeconds + 1));
    }
}
