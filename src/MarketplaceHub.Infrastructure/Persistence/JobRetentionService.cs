using MarketplaceHub.Application;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

// Removes old finished jobs (their attempts cascade), old webhook inbox rows, retired quota counters and
// old settled marketplace-action records.
// - "scheduled:" jobs and the short-lived reads ("webhook:", "effect-reconcile:", "manual:") go after the
//   succeeded retention (14 days) when they succeeded or were cancelled, after the failed retention (30 days)
//   otherwise. Their dedup keys carry a time bucket or an event id, and their work is idempotent.
// - Every other finished job goes after the failed retention (30 days), unless its job type guards a
//   marketplace or invoice effect (product create, price/stock writes, shipment and return actions, invoice
//   submit/delivery/cancellation ...): its dedup key is what stops the effect from running twice, so it is
//   kept. Product/catalog syncs and imports are kept because import sessions refer to them, and activation
//   bootstrap jobs because a connection's first import is known by them.
// - A guard is not needed forever when its key can never come back: stock dispatches and price/stock writes
//   carry the projection and price versions in their keys, so they go after the guard retention (90 days).
//   A guard job that never sent anything (cancelled before its first run, or blocked before the send because
//   the connection was off or writes were disabled) guards nothing and goes after the failed retention.
public sealed class JobRetentionService(AppDbContext db, TimeProvider timeProvider)
{
    public const int BatchSize = 2_000;
    public static readonly TimeSpan DefaultGuardRetention = TimeSpan.FromDays(90);
    public static readonly TimeSpan DefaultInboxRetention = TimeSpan.FromDays(90);
    public static readonly TimeSpan RateLimitUsageRetention = TimeSpan.FromDays(2);
    public static readonly TimeSpan DefaultEffectRecordRetention = TimeSpan.FromDays(90);

    private static readonly string[] KeptJobTypes =
    [
        MarketplaceJobTypes.ProductSync, MarketplaceJobTypes.ShopifyProductSync,
        MarketplaceJobTypes.HepsiburadaProductSync, MarketplaceJobTypes.CatalogStructureImport
    ];

    internal static readonly string[] EffectGuardJobTypes =
    [
        MarketplaceJobTypes.ProductCreate, MarketplaceJobTypes.ProductApprovalReconcile, MarketplaceJobTypes.ProductUpdate, MarketplaceJobTypes.ProductArchive,
        MarketplaceJobTypes.PriceInventorySync, MarketplaceJobTypes.StockProjectionDispatch,
        MarketplaceJobTypes.ShipmentAction, MarketplaceJobTypes.ReturnAction, MarketplaceJobTypes.CommonLabel,
        MarketplaceJobTypes.CapabilityProbe, MarketplaceJobTypes.StageTestOrder,
        InvoicingJobTypes.InvoiceSubmit, InvoicingJobTypes.MarketplaceDelivery, InvoicingJobTypes.ManualOrderInvoiceDelivery,
        InvoicingJobTypes.InvoiceCancellation, InvoicingJobTypes.StageCapabilityProbe,
        "IMPORT_PREVIEW", "IMPORT_APPLY"
    ];

    // Guard job types whose dedup key carries a version that only moves forward, so the key never recurs.
    internal static readonly string[] VersionedGuardJobTypes = [MarketplaceJobTypes.StockProjectionDispatch, MarketplaceJobTypes.PriceInventorySync];

    // Blocks that happen before anything is sent to the marketplace.
    internal static readonly string[] PreSendBlockCodes =
    [
        "CONNECTION_REQUIRED", "CONNECTION_OUT_OF_SCOPE", "CONNECTION_INACTIVE", "CONNECTION_NOT_FOUND",
        "EXTERNAL_WRITES_DISABLED", "N11_EXTERNAL_WRITES_DISABLED", "PAZARAMA_EXTERNAL_WRITES_DISABLED",
        "STOCK_PROJECTION_PAYLOAD_INVALID", "INVALID_IMPORT_PAYLOAD"
    ];

    public Task<int> PurgeAsync(TimeSpan succeededRetention, TimeSpan failedRetention, int maximumBatches, CancellationToken cancellationToken) =>
        PurgeAsync(succeededRetention, failedRetention, DefaultGuardRetention, maximumBatches, cancellationToken);

    public async Task<int> PurgeAsync(TimeSpan succeededRetention, TimeSpan failedRetention, TimeSpan guardRetention, int maximumBatches, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var succeededCutoff = now - succeededRetention;
        var failedCutoff = now - failedRetention;
        var guardCutoff = now - (guardRetention < failedRetention ? failedRetention : guardRetention);
        var deleted = 0;
        for (var batch = 0; batch < maximumBatches; batch++)
        {
            var count = await db.Database.ExecuteSqlInterpolatedAsync($"""
                WITH doomed AS (
                    SELECT "Id" FROM integration.jobs
                    WHERE "Status" IN ('SUCCEEDED', 'CANCELLED', 'DEAD', 'MANUAL_REVIEW', 'BLOCKED')
                      AND NOT ("JobType" = ANY({KeptJobTypes}::text[]))
                      AND (
                        (("JobDedupKey" LIKE 'scheduled:%'
                            OR (NOT ("JobType" = ANY({EffectGuardJobTypes}::text[]))
                                AND ("JobDedupKey" LIKE 'webhook:%' OR "JobDedupKey" LIKE 'effect-reconcile:%' OR "JobDedupKey" LIKE 'manual:%')))
                          AND (("Status" IN ('SUCCEEDED', 'CANCELLED') AND coalesce("CompletedAt", "CreatedAt") < {succeededCutoff})
                            OR ("Status" IN ('DEAD', 'MANUAL_REVIEW', 'BLOCKED') AND coalesce("CompletedAt", "CreatedAt") < {failedCutoff})))
                        OR (NOT ("JobType" = ANY({EffectGuardJobTypes}::text[]))
                          AND "JobDedupKey" NOT LIKE 'activation-bootstrap:%'
                          AND coalesce("CompletedAt", "CreatedAt") < {failedCutoff})
                        OR ("JobType" = ANY({VersionedGuardJobTypes}::text[])
                          AND ("JobDedupKey" LIKE 'stock-projection:%:v%' OR "JobDedupKey" LIKE 'price-inventory:%:revision:%')
                          AND coalesce("CompletedAt", "CreatedAt") < {guardCutoff})
                        OR ("JobType" = ANY({EffectGuardJobTypes}::text[])
                          AND (("Status" = 'CANCELLED' AND "AttemptCount" = 0)
                            OR ("Status" = 'BLOCKED' AND "LastErrorCode" = ANY({PreSendBlockCodes}::text[])))
                          AND coalesce("CompletedAt", "CreatedAt") < {failedCutoff}))
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

    // Every webhook delivery leaves an inbox row; once processed and older than the retention it only takes room.
    public async Task<int> PurgeProcessedInboxAsync(TimeSpan retention, int maximumBatches, CancellationToken cancellationToken) =>
        await PurgeInboxAsync(retention, DefaultInboxRetention, maximumBatches, cancellationToken);

    // Processed rows go after the processed retention. A row whose ingest job ended blocked or dead is never
    // marked processed; every row goes once it was received longer ago than the receipt retention (90 days).
    public async Task<int> PurgeInboxAsync(TimeSpan processedRetention, TimeSpan receivedRetention, int maximumBatches, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var processedCutoff = now - processedRetention;
        var receivedCutoff = now - (receivedRetention < processedRetention ? processedRetention : receivedRetention);
        var deleted = 0;
        for (var batch = 0; batch < maximumBatches; batch++)
        {
            var count = await db.Database.ExecuteSqlInterpolatedAsync($"""
                WITH doomed AS (
                    SELECT "Id" FROM integration.inbox_messages
                    WHERE "ProcessedAt" < {processedCutoff} OR "ReceivedAt" < {receivedCutoff}
                    LIMIT {BatchSize})
                DELETE FROM integration.inbox_messages inbox USING doomed WHERE inbox."Id" = doomed."Id"
                """, cancellationToken);
            deleted += count;
            if (count < BatchSize) break;
        }
        return deleted;
    }

    // The quota counters clean up only their own key when a request is counted, so a key no longer used (a
    // changed host or seller id, a removed connection) kept its rows forever. The panel reads the last hour.
    public async Task<int> PurgeRateLimitUsageAsync(int maximumBatches, CancellationToken cancellationToken)
    {
        var cutoff = timeProvider.GetUtcNow() - RateLimitUsageRetention;
        var deleted = 0;
        for (var batch = 0; batch < maximumBatches; batch++)
        {
            var count = await db.Database.ExecuteSqlInterpolatedAsync($"""
                WITH doomed AS (
                    SELECT "Key", "Minute" FROM integration.rate_limit_usage
                    WHERE "Minute" < {cutoff}
                    LIMIT {BatchSize})
                DELETE FROM integration.rate_limit_usage usage USING doomed
                WHERE usage."Key" = doomed."Key" AND usage."Minute" = doomed."Minute"
                """, cancellationToken);
            deleted += count;
            if (count < BatchSize) break;
        }
        return deleted;
    }

    // A settled marketplace-action record (applied or closed unresolved) is history once its retention passed.
    // Open records are never removed: they still block a second send of the same action.
    public async Task<int> PurgeCompletedEffectRecordsAsync(TimeSpan retention, int maximumBatches, CancellationToken cancellationToken)
    {
        var cutoff = timeProvider.GetUtcNow() - retention;
        var deleted = 0;
        for (var batch = 0; batch < maximumBatches; batch++)
        {
            var count = await db.Database.ExecuteSqlInterpolatedAsync($"""
                WITH doomed AS (
                    SELECT "Id" FROM integration.external_effect_records
                    WHERE "CompletedAt" IS NOT NULL AND "CompletedAt" < {cutoff}
                    LIMIT {BatchSize})
                DELETE FROM integration.external_effect_records effect USING doomed WHERE effect."Id" = doomed."Id"
                """, cancellationToken);
            deleted += count;
            if (count < BatchSize) break;
        }
        return deleted;
    }
}
