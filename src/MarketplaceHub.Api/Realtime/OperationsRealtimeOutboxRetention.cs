using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Api.Realtime;

/// <summary>
/// Keeps published realtime invalidation events for 30 days. Unpublished and
/// failed events are retained until they are successfully delivered.
/// </summary>
public static class OperationsRealtimeOutboxRetention
{
    public static readonly TimeSpan RetentionPeriod = TimeSpan.FromDays(30);
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
