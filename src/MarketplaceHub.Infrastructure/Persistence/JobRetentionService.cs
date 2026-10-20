using MarketplaceHub.Application;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

// Removes old finished jobs (their attempts cascade) and processed webhook inbox rows.
// - "scheduled:" jobs and the short-lived reads ("webhook:", "effect-reconcile:", "manual:") go after the
//   succeeded retention (14 days) when they succeeded or were cancelled, after the failed retention (30 days)
//   otherwise. Their dedup keys carry a time bucket or an event id, and their work is idempotent.
// - Every other finished job goes after the failed retention (30 days), unless its job type guards a
//   marketplace or invoice effect (product create, price/stock writes, shipment and return actions, invoice
//   submit/delivery/cancellation ...): its dedup key is what stops the effect from running twice, so it is
//   kept forever. Product/catalog syncs and imports are kept because import sessions refer to them, and
//   activation bootstrap jobs because a connection's first import is known by them.
public sealed class JobRetentionService(AppDbContext db, TimeProvider timeProvider)
{
    public const int BatchSize = 2_000;
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
    public async Task<int> PurgeProcessedInboxAsync(TimeSpan retention, int maximumBatches, CancellationToken cancellationToken)
    {
        var cutoff = timeProvider.GetUtcNow() - retention;
        var deleted = 0;
        for (var batch = 0; batch < maximumBatches; batch++)
        {
            var count = await db.Database.ExecuteSqlInterpolatedAsync($"""
                WITH doomed AS (
                    SELECT "Id" FROM integration.inbox_messages
                    WHERE "ProcessedAt" < {cutoff}
                    LIMIT {BatchSize})
                DELETE FROM integration.inbox_messages inbox USING doomed WHERE inbox."Id" = doomed."Id"
                """, cancellationToken);
            deleted += count;
            if (count < BatchSize) break;
        }
        return deleted;
    }
}
