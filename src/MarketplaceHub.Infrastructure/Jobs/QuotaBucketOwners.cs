using System.Collections.Concurrent;
using MarketplaceHub.Infrastructure.Adapters;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Jobs;

// Queue phase 0 (27 Nov 2026): which connection takes tokens from which quota bucket, for the quota card's tenant scope.
// integration.rate_limit_usage has no tenant column and most bucket keys name an account by a hash of its credentials, so the
// card could only read Trendyol's order buckets (matched by seller id). The worker writes a (bucket, connection) pair when a
// job of the connection used the bucket; each pair is written at most once per RefreshAfter per process, so a job costs no
// extra round trip except the first use of a bucket in that time. The keys are stored as the limiter stores them.
public static class QuotaBucketOwners
{
    public static readonly TimeSpan RefreshAfter = TimeSpan.FromHours(6);
    private const int MaximumRemembered = 20_000;
    private static readonly ConcurrentDictionary<(string Key, Guid ConnectionId), DateTimeOffset> written = new();

    // The pairs of this run not written in the last RefreshAfter (empty: nothing to write).
    public static IReadOnlyList<string> Due(Guid connectionId, IReadOnlyCollection<string> keys, DateTimeOffset now)
    {
        if (keys.Count == 0) return [];
        return [.. keys.Select(PostgresSharedRateLimiter.StorageKey).Distinct(StringComparer.Ordinal)
            .Where(key => !written.TryGetValue((key, connectionId), out var at) || now - at >= RefreshAfter)];
    }

    // Writes the pairs (insert, or a newer LastSeenAt) and remembers them.
    public static async Task RecordAsync(AppDbContext db, Guid connectionId, IReadOnlyList<string> storageKeys, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (storageKeys.Count == 0) return;
        var seenAt = now.ToUniversalTime();
        var keys = storageKeys.ToArray();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO integration.rate_limit_bucket_connections ("Key", "ConnectionId", "LastSeenAt")
            SELECT bucket.key, {connectionId}, {seenAt} FROM unnest({keys}::text[]) AS bucket(key)
            ON CONFLICT ("Key", "ConnectionId") DO UPDATE SET "LastSeenAt" = GREATEST(integration.rate_limit_bucket_connections."LastSeenAt", EXCLUDED."LastSeenAt")
            """, cancellationToken);
        if (written.Count > MaximumRemembered) written.Clear();
        foreach (var key in keys) written[(key, connectionId)] = now;
    }

    // Tests: forget what was written.
    internal static void Reset() => written.Clear();
}
