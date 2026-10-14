using MarketplaceHub.Application;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

// Removes old finished scheduled jobs (their attempts cascade). Only "scheduled:" jobs are
// purged: their dedup keys carry a time bucket, so deleting them cannot let an effect run twice.
// Jobs whose dedup key guards a marketplace or invoice effect (product create, invoice submit,
// ...) are kept forever. Product/catalog syncs are kept because import sessions refer to them.
public sealed class JobRetentionService(AppDbContext db, TimeProvider timeProvider)
{
    public const int BatchSize = 2_000;
    private static readonly string[] KeptJobTypes =
    [
        MarketplaceJobTypes.ProductSync, MarketplaceJobTypes.ShopifyProductSync,
        MarketplaceJobTypes.HepsiburadaProductSync, MarketplaceJobTypes.CatalogStructureImport
    ];

    public async Task<int> PurgeAsync(TimeSpan succeededRetention, TimeSpan failedRetention, int maximumBatches, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var succeededCutoff = now - succeededRetention;
        var failedCutoff = now - failedRetention;
        var deleted = 0;
        for (var batch = 0; batch < maximumBatches; batch++)
        {
            var count = await db.Database.ExecuteSqlInterpolatedAsync($"""
                WITH doomed AS (
                    SELECT "Id" FROM integration.jobs
                    WHERE "JobDedupKey" LIKE 'scheduled:%'
                      AND NOT ("JobType" = ANY({KeptJobTypes}::text[]))
                      AND (("Status" IN ('SUCCEEDED', 'CANCELLED') AND coalesce("CompletedAt", "CreatedAt") < {succeededCutoff})
                        OR ("Status" IN ('DEAD', 'MANUAL_REVIEW', 'BLOCKED') AND coalesce("CompletedAt", "CreatedAt") < {failedCutoff}))
                    ORDER BY "CreatedAt"
                    LIMIT {BatchSize})
                DELETE FROM integration.jobs job USING doomed WHERE job."Id" = doomed."Id"
                """, cancellationToken);
            deleted += count;
            if (count < BatchSize) break;
        }
        var expiredBackoffCutoff = now - TimeSpan.FromDays(1);
        await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM integration.connection_backoffs WHERE \"Until\" < {expiredBackoffCutoff}", cancellationToken);
        return deleted;
    }
}
