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
//   the connection was off or writes were disabled) guards nothing and goes after the failed retention, and so
//   does a failed price/stock write that gave up its key ("...:retired:{id}") when the same values were queued again.
public sealed class JobRetentionService(AppDbContext db, TimeProvider timeProvider)
{
    public const int BatchSize = 2_000;
    public static readonly TimeSpan DefaultGuardRetention = TimeSpan.FromDays(90);
    public static readonly TimeSpan DefaultInboxRetention = TimeSpan.FromDays(90);
    public static readonly TimeSpan RateLimitUsageRetention = TimeSpan.FromDays(2);

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
        "EXTERNAL_WRITES_DISABLED", "N11_EXTERNAL_WRITES_DISABLED", "PAZARAMA_EXTERNAL_WRITES_DISABLED", "PTTAVM_EXTERNAL_WRITES_DISABLED",
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
                        OR ("JobType" = ANY({VersionedGuardJobTypes}::text[])
                          AND "JobDedupKey" LIKE 'price-inventory:%:retired:%'
                          AND coalesce("CompletedAt", "CreatedAt") < {failedCutoff})
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
    // A marketplace list (brands, categories, attributes) is kept as a snapshot; a refresh with new content makes a new
    // current one and leaves the old one behind. Old Trendyol brand lists (~800,000 rows each) piled up to 12 million
    // rows (26 Oct 2026), and a connection reset that deletes brands then ran for minutes. A snapshot that is not current,
    // was fetched more than a day ago (a refresh builds its staging snapshot as not current) and no mapping points at
    // goes, its items in batches first.
    public static readonly TimeSpan StaleReferenceSnapshotAge = TimeSpan.FromDays(1);
    public const int ReferenceItemBatchSize = 20_000;

    public async Task<int> PurgeStaleReferenceSnapshotsAsync(int maximumBatches, CancellationToken cancellationToken)
    {
        var cutoff = timeProvider.GetUtcNow() - StaleReferenceSnapshotAge;
        var deleted = 0;
        // One stale snapshot at a time, its items read through the (TenantId, SnapshotId, ...) index: a join over all
        // stale snapshots scans the whole 13-million-row table on every batch.
        for (var batch = 0; batch < maximumBatches; batch++)
        {
            var count = await db.Database.ExecuteSqlInterpolatedAsync($"""
                WITH target AS (
                    SELECT s."TenantId", s."Id" FROM integration.reference_snapshots s
                    WHERE NOT s."IsCurrent" AND s."FetchedAt" < {cutoff}
                      AND EXISTS (SELECT 1 FROM integration.reference_items i WHERE i."TenantId" = s."TenantId" AND i."SnapshotId" = s."Id")
                      AND NOT EXISTS (SELECT 1 FROM catalog.category_mappings m WHERE m."TenantId" = s."TenantId" AND m."SnapshotId" = s."Id")
                      AND NOT EXISTS (SELECT 1 FROM catalog.brand_mappings m WHERE m."TenantId" = s."TenantId" AND m."SnapshotId" = s."Id")
                      AND NOT EXISTS (SELECT 1 FROM catalog.attribute_mappings m WHERE m."TenantId" = s."TenantId" AND m."SnapshotId" = s."Id")
                      AND NOT EXISTS (SELECT 1 FROM catalog.attribute_value_mappings m WHERE m."TenantId" = s."TenantId" AND m."SnapshotId" = s."Id")
                    ORDER BY s."FetchedAt"
                    LIMIT 1),
                doomed AS (
                    SELECT i."Id" FROM integration.reference_items i
                    WHERE i."TenantId" = (SELECT "TenantId" FROM target) AND i."SnapshotId" = (SELECT "Id" FROM target)
                    LIMIT {ReferenceItemBatchSize})
                DELETE FROM integration.reference_items item USING doomed WHERE item."Id" = doomed."Id"
                """, cancellationToken);
            deleted += count;
            if (count == 0) break;
        }
        // Snapshots left without items go too; one still holding items waits for the next pass.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            DELETE FROM integration.reference_snapshots s
            WHERE NOT s."IsCurrent" AND s."FetchedAt" < {cutoff}
              AND NOT EXISTS (SELECT 1 FROM integration.reference_items i WHERE i."TenantId" = s."TenantId" AND i."SnapshotId" = s."Id")
              AND NOT EXISTS (SELECT 1 FROM catalog.category_mappings m WHERE m."TenantId" = s."TenantId" AND m."SnapshotId" = s."Id")
              AND NOT EXISTS (SELECT 1 FROM catalog.brand_mappings m WHERE m."TenantId" = s."TenantId" AND m."SnapshotId" = s."Id")
              AND NOT EXISTS (SELECT 1 FROM catalog.attribute_mappings m WHERE m."TenantId" = s."TenantId" AND m."SnapshotId" = s."Id")
              AND NOT EXISTS (SELECT 1 FROM catalog.attribute_value_mappings m WHERE m."TenantId" = s."TenantId" AND m."SnapshotId" = s."Id")
            """, cancellationToken);
        return deleted;
    }

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

    // The price/stock send log keeps 90 days. Each tenant's oldest rows are read through its (TenantId, CreatedAt) index,
    // so a batch never scans the table.
    public async Task<int> PurgePriceStockSendLogAsync(TimeSpan retention, int maximumBatches, CancellationToken cancellationToken)
    {
        var cutoff = timeProvider.GetUtcNow() - retention;
        var deleted = 0;
        for (var batch = 0; batch < maximumBatches; batch++)
        {
            var count = await db.Database.ExecuteSqlInterpolatedAsync($"""
                WITH doomed AS (
                    SELECT old."Id" FROM iam.tenants tenant
                    CROSS JOIN LATERAL (
                        SELECT log."Id" FROM integration.price_stock_send_log log
                        WHERE log."TenantId" = tenant."Id" AND log."CreatedAt" < {cutoff}
                        ORDER BY log."CreatedAt", log."Id"
                        LIMIT {BatchSize}) old
                    LIMIT {BatchSize})
                DELETE FROM integration.price_stock_send_log log USING doomed WHERE log."Id" = doomed."Id"
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
