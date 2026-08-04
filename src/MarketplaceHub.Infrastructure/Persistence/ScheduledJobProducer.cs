using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

public sealed class ScheduledJobProducer(AppDbContext db, TimeProvider timeProvider) : IScheduledJobProducer
{
    public async Task<int> EnqueueDueAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var added = 0;
        var policies = await (from policy in db.ConnectionSyncPolicies.AsNoTracking()
                              join connection in db.PlatformConnections.AsNoTracking()
                                  on new { policy.TenantId, Id = policy.ConnectionId } equals new { connection.TenantId, connection.Id }
                              where policy.Enabled && connection.Status == "ACTIVE" && connection.PlatformCode == "TRENDYOL"
                              select new { Policy = policy, Connection = connection }).ToListAsync(cancellationToken);

        foreach (var row in policies)
        {
            var definition = Definition(row.Policy.ResourceType, row.Connection.Id);
            if (definition is null) continue;
            var capabilitySupported = await db.PlatformCapabilities.AsNoTracking().AnyAsync(x =>
                x.TenantId == row.Policy.TenantId && x.ConnectionId == row.Connection.Id && x.Code == definition.Value.Capability &&
                x.SupportLevel == CapabilitySupportLevel.Supported, cancellationToken);
            if (!capabilitySupported) continue;

            var interval = Math.Clamp(row.Policy.IntervalSeconds, 60, 86_400);
            var latest = await db.IntegrationJobs.AsNoTracking()
                .Where(x => x.TenantId == row.Policy.TenantId && x.ConnectionId == row.Connection.Id && x.JobType == definition.Value.JobType && x.JobDedupKey.StartsWith(definition.Value.DedupPrefix))
                .OrderByDescending(x => x.CreatedAt).Select(x => (DateTimeOffset?)x.CreatedAt).FirstOrDefaultAsync(cancellationToken);
            if (latest is not null && latest > now.AddSeconds(-interval)) continue;

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
            if (await db.IntegrationJobs.AsNoTracking().AnyAsync(x => x.TenantId == tenantId && x.JobType == F4JobTypes.InvoiceDueScan && x.JobDedupKey == dedup, cancellationToken)) continue;
            db.IntegrationJobs.Add(NewJob(tenantId, null, F4JobTypes.InvoiceDueScan, dedup, "{}", now, $"scheduler-{Guid.NewGuid():N}"));
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

    private static (string JobType, string Capability, string DedupPrefix, string PayloadJson)? Definition(string resourceType, Guid connectionId) => resourceType switch
    {
        "ORDERS" => (F3JobTypes.OrderSync, F3Capabilities.OrderRead, $"scheduled:orders:{connectionId:N}", JsonSerializer.Serialize(new { connectionId, externalOrderId = (string?)null })),
        "RETURNS" => (F3JobTypes.ReturnSync, F3Capabilities.ReturnRead, $"scheduled:returns:{connectionId:N}", JsonSerializer.Serialize(new { connectionId })),
        "REFERENCE_DATA" => (F3JobTypes.ReferenceSync, F3Capabilities.ReferenceRead, $"scheduled:reference:{connectionId:N}", JsonSerializer.Serialize(new { connectionId, resourceType = "CATEGORIES", parentExternalId = (string?)null })),
        _ => null
    };

    private static IntegrationJob NewJob(Guid tenantId, Guid? connectionId, string type, string dedup, string payload, DateTimeOffset availableAt, string correlationId) => new()
    {
        Id = Guid.CreateVersion7(), TenantId = tenantId, ConnectionId = connectionId, JobType = type, PayloadJson = payload, PayloadVersion = 1,
        PayloadHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))), JobDedupKey = dedup, EffectIdempotencyKey = dedup,
        AvailableAt = availableAt, CorrelationId = correlationId, Version = 1
    };

    private static int StableJitter(Guid policyId, long bucket, int maxSeconds)
    {
        Span<byte> input = stackalloc byte[24];
        policyId.TryWriteBytes(input[..16]);
        BitConverter.TryWriteBytes(input[16..], bucket);
        var hash = SHA256.HashData(input);
        return (int)(BitConverter.ToUInt32(hash, 0) % (uint)(maxSeconds + 1));
    }
}
