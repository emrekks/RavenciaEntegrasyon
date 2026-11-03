using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Api.Realtime;

/// <summary>
/// Keeps published realtime invalidation events for 3 days. Unpublished and
/// failed events are retained until they are successfully delivered. Nothing
/// reads an old published event: the dispatcher reads only unpublished ones,
/// panels get no replay on reconnect (they re-read their data), and the
/// dashboard snapshot refresher looks back only to its snapshot, which is at
/// most DashboardSnapshotRefreshPolicy.Reconciliation (5 minutes) old when it
/// reads the outbox. The rows are deleted BatchSize at a time, one batch per
/// cleanup pass of OperationsRealtimeBroadcaster, so a backlog drains in small
/// transactions instead of one long delete.
/// </summary>
public static class OperationsRealtimeOutboxRetention
{
    public static readonly TimeSpan RetentionPeriod = TimeSpan.FromDays(3);
    public const int BatchSize = 1_000;

    public static Task<int> DeleteExpiredPublishedBatchAsync(
        AppDbContext db,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var cutoff = now - RetentionPeriod;
        var expiredIds = db.IntegrationOutboxEvents.AsNoTracking()
            .Where(row => row.PublishedAt != null && row.PublishedAt < cutoff)
            .OrderBy(row => row.PublishedAt)
            .ThenBy(row => row.Id)
            .Select(row => row.Id)
            .Take(BatchSize);

        return db.IntegrationOutboxEvents
            .Where(row => expiredIds.Contains(row.Id))
            .ExecuteDeleteAsync(cancellationToken);
    }
}
