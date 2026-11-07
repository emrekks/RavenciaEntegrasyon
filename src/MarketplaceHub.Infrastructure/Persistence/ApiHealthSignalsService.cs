using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

// The six signals of "API Sağlığı" per marketplace connection, read-only, from what the hub already stores.
public sealed class ApiHealthSignalsService(AppDbContext db, TimeProvider timeProvider) : IApiHealthSignalsService
{
    private static readonly string[] MarketplacePlatforms = ["TRENDYOL", "HEPSIBURADA", "N11", "PAZARAMA", "PTTAVM", "SHOPIFY"];
    private static readonly string[] Streams = ["ORDERS", "RETURNS", "QUESTIONS"];
    private static readonly JobStatus[] DueStatuses = [JobStatus.Pending, JobStatus.RetryScheduled];

    private sealed record BackoffRow(Guid ConnectionId, DateTimeOffset Until, string? Reason);

    public async Task<ApiHealthSignalsView> SignalsAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var dayAgo = now.AddHours(-24).ToUniversalTime();
        var monthAgo = now.AddDays(-30).ToUniversalTime();
        var connections = await db.PlatformConnections.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.Status != "DELETED" && MarketplacePlatforms.Contains(x.PlatformCode))
            .OrderBy(x => x.PlatformCode).ThenBy(x => x.DisplayName)
            .Select(x => new { x.Id, x.PlatformCode, x.DisplayName, x.Environment })
            .ToListAsync(cancellationToken);
        var ids = connections.Select(x => x.Id).ToArray();

        // Finished attempts of the last 24 hours, and the newest refusal and success of the last 30 days, per connection.
        var attempts = await (from attempt in db.JobAttempts.AsNoTracking()
                              join job in db.IntegrationJobs.AsNoTracking() on attempt.JobId equals job.Id
                              where attempt.TenantId == tenantId && job.ConnectionId != null && ids.Contains(job.ConnectionId.Value) && attempt.CompletedAt != null && attempt.StartedAt >= dayAgo
                              select new { ConnectionId = job.ConnectionId!.Value, attempt.Succeeded, attempt.ErrorCode }).ToListAsync(cancellationToken);
        var refusals = await (from attempt in db.JobAttempts.AsNoTracking()
                              join job in db.IntegrationJobs.AsNoTracking() on attempt.JobId equals job.Id
                              where attempt.TenantId == tenantId && job.ConnectionId != null && ids.Contains(job.ConnectionId.Value) && attempt.StartedAt >= monthAgo && !attempt.Succeeded
                                  && attempt.ErrorCode != null && (attempt.ErrorCode.EndsWith("_CREDENTIALS_REJECTED") || attempt.ErrorCode.EndsWith("_ACCESS_DENIED") || attempt.ErrorCode == "CREDENTIALS_REJECTED" || attempt.ErrorCode == "ACCESS_DENIED")
                              select new { ConnectionId = job.ConnectionId!.Value, attempt.StartedAt, attempt.ErrorCode }).ToListAsync(cancellationToken);
        var successes = await (from attempt in db.JobAttempts.AsNoTracking()
                               join job in db.IntegrationJobs.AsNoTracking() on attempt.JobId equals job.Id
                               where attempt.TenantId == tenantId && job.ConnectionId != null && ids.Contains(job.ConnectionId.Value) && attempt.StartedAt >= monthAgo && attempt.Succeeded
                               group attempt by job.ConnectionId!.Value into g
                               select new { ConnectionId = g.Key, At = g.Max(x => x.StartedAt) }).ToDictionaryAsync(x => x.ConnectionId, x => (DateTimeOffset?)x.At, cancellationToken);
        var backoffs = await db.Database.SqlQuery<BackoffRow>($"""SELECT "ConnectionId", "Until", "Reason" FROM integration.connection_backoffs WHERE "Until" > {now.ToUniversalTime()}""").ToListAsync(cancellationToken);

        var policies = await db.ConnectionSyncPolicies.AsNoTracking()
            .Where(x => x.TenantId == tenantId && ids.Contains(x.ConnectionId) && x.Enabled && Streams.Contains(x.ResourceType))
            .Select(x => new { x.ConnectionId, x.ResourceType, x.IntervalSeconds, x.IdleBackoffEnabled, x.IdleIntervalSeconds })
            .ToListAsync(cancellationToken);
        var cursorTypes = Streams.Select(MarketplaceSyncPolicyRules.CursorResourceType).ToArray();
        var cursors = await db.SyncCursors.AsNoTracking()
            .Where(x => x.TenantId == tenantId && ids.Contains(x.ConnectionId) && cursorTypes.Contains(x.ResourceType))
            .Select(x => new { x.ConnectionId, x.ResourceType, x.LastSuccessAt, x.LastRateLimitCount })
            .ToListAsync(cancellationToken);

        var queued = await db.IntegrationJobs.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ConnectionId != null && ids.Contains(x.ConnectionId.Value) && DueStatuses.Contains(x.Status))
            .Select(x => new { ConnectionId = x.ConnectionId!.Value, x.JobType, x.Status, x.LastErrorCode, x.AvailableAt, x.CreatedAt })
            .ToListAsync(cancellationToken);

        var subscriptions = await db.WebhookSubscriptions.AsNoTracking()
            .Where(x => x.TenantId == tenantId && ids.Contains(x.ConnectionId) && x.Status == "ACTIVE" && x.ExternalSubscriptionId != null)
            .GroupBy(x => x.ConnectionId)
            .Select(group => new { ConnectionId = group.Key, LastReceivedAt = group.Max(x => x.LastReceivedAt) })
            .ToDictionaryAsync(x => x.ConnectionId, x => x.LastReceivedAt, cancellationToken);
        var webhookHealthy = new HashSet<Guid>();
        foreach (var connectionId in subscriptions.Keys)
            if (await ScheduledJobProducer.WebhookHealthyAsync(db, tenantId, connectionId, now, cancellationToken)) webhookHealthy.Add(connectionId);

        var rows = connections.Select(connection =>
        {
            var healthy = webhookHealthy.Contains(connection.Id);
            var mine = attempts.Where(x => x.ConnectionId == connection.Id).ToList();
            var errorRate = ApiHealthSignalPolicy.ErrorRate(mine.Select(x => (x.Succeeded, (string?)x.ErrorCode)));

            var refusal = refusals.Where(x => x.ConnectionId == connection.Id).OrderByDescending(x => x.StartedAt).FirstOrDefault();
            var paused = backoffs.Where(x => x.ConnectionId == connection.Id && ApiHealthSignalPolicy.IsAuthRefusal(x.Reason)).Select(x => (DateTimeOffset?)x.Until).Max();
            var auth = ApiHealthSignalPolicy.Auth(refusal?.StartedAt, refusal?.ErrorCode, successes.GetValueOrDefault(connection.Id), paused, now);

            var streams = Streams.Select(stream =>
            {
                var policy = policies.FirstOrDefault(x => x.ConnectionId == connection.Id && x.ResourceType == stream);
                if (policy is null) return (Stream: stream, LastSuccessAt: (DateTimeOffset?)null, ExpectedSeconds: 0);
                var cursor = cursors.FirstOrDefault(x => x.ConnectionId == connection.Id && x.ResourceType == MarketplaceSyncPolicyRules.CursorResourceType(stream));
                var expected = SyncCadencePolicy.EffectiveSeconds(connection.PlatformCode, stream, connection.Environment, Math.Clamp(policy.IntervalSeconds, 30, ScheduledOrderPollingCadencePolicy.MaximumIntervalSeconds), policy.IdleBackoffEnabled, policy.IdleIntervalSeconds, healthy, null, now);
                return (Stream: stream, LastSuccessAt: cursor?.LastSuccessAt, ExpectedSeconds: expected);
            }).ToList();
            var lag = ApiHealthSignalPolicy.WorstLag(streams, now);

            var mineQueue = queued.Where(x => x.ConnectionId == connection.Id).ToList();
            var due = mineQueue.Select(x => new { x.JobType, Since = x.AvailableAt > x.CreatedAt ? x.AvailableAt : x.CreatedAt, x.LastErrorCode, x.Status }).Where(x => x.Since <= now).OrderBy(x => x.Since).ToList();
            var queue = due.Count == 0 ? null : new ApiHealthQueueSignal((long)(now - due[0].Since).TotalSeconds, due[0].JobType, due.Count);

            var quota = backoffs.Where(x => x.ConnectionId == connection.Id && ApiHealthSignalPolicy.IsRateLimited(x.Reason)).Select(x => (DateTimeOffset?)x.Until).Max();
            var waitingOnQuota = mineQueue.Count(x => ApiHealthSignalPolicy.IsRateLimited(x.LastErrorCode));
            var lastRunRefusals = cursors.Where(x => x.ConnectionId == connection.Id).Sum(x => x.LastRateLimitCount);
            var rateLimit = new ApiHealthRateLimitSignal(errorRate.RateLimited, waitingOnQuota, quota, lastRunRefusals);

            var ordersCursor = cursors.FirstOrDefault(x => x.ConnectionId == connection.Id && x.ResourceType == MarketplaceSyncPolicyRules.CursorResourceType("ORDERS"));
            var ordersStream = streams.First(x => x.Stream == "ORDERS");
            var webhook = new ApiHealthWebhookSignal(subscriptions.ContainsKey(connection.Id), subscriptions.GetValueOrDefault(connection.Id), healthy, ordersCursor?.LastSuccessAt, ordersStream.ExpectedSeconds > 0 ? ordersStream.ExpectedSeconds : null);
            return new ApiHealthConnectionSignals(connection.Id, connection.PlatformCode, connection.DisplayName, auth, lag, errorRate, rateLimit, queue, webhook);
        }).ToList();
        return new ApiHealthSignalsView(now, rows);
    }
}
