using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

// What an order or return read pulled, record by record (the job detail's "Çekilen Kayıtlar"): each order or claim the
// run upserted, with its number, status and what the read did with it. Compared before and after the upsert on the
// values the owner reads (the order's status, its packages' statuses and tracking, the claim's version); nothing about
// the customer is kept. Recorded only while a read job that records its progress runs.
public sealed partial class MarketplaceJobProcessor
{

    // The reads that keep their records, by the kind of record they pull; null for a job that keeps none.
    internal static string? ReadRecordKind(string jobType) => jobType switch
    {
        MarketplaceJobTypes.ReturnSync or MarketplaceJobTypes.HepsiburadaReturnSync or MarketplaceJobTypes.ReturnStatusSync or MarketplaceJobTypes.ReturnReconciliation => "RETURN",
        MarketplaceJobTypes.OrderInvoiceReconciliation or MarketplaceJobTypes.ShopifyOrderInvoiceReconciliation or MarketplaceJobTypes.HepsiburadaOrderInvoiceReconciliation => "INVOICE",
        MarketplaceJobTypes.ProductSync or MarketplaceJobTypes.ShopifyProductSync or MarketplaceJobTypes.HepsiburadaProductSync => "PRODUCT",
        MarketplaceJobTypes.QuestionSync => "QUESTION",
        MarketplaceJobTypes.TrendyolFinanceSync or MarketplaceJobTypes.PazaramaFinanceSync or MarketplaceJobTypes.N11SettlementSync or MarketplaceJobTypes.HepsiburadaFinanceSync => "FINANCE",
        MarketplaceJobTypes.OrderReconciliation or MarketplaceJobTypes.ShopifyOrderReconciliation or MarketplaceJobTypes.TrendyolOrderCargoInfoReconciliation => "ORDER",
        _ => RecordsReadProgress(jobType) ? "ORDER" : null
    };

    // A package the invoice check read on its own (Trendyol): its number, the marketplace invoice number and state.
    private void RecordInvoiceRead(ShipmentPackage package, MarketplaceInvoiceStatus before, string? numberBefore)
    {
        if (!run.ReadRecordingEnabled || run.ReadRecordingKind != "INVOICE") return;
        var changed = before != package.MarketplaceInvoiceStatus || !string.Equals(numberBefore, package.MarketplaceInvoiceNumber, StringComparison.Ordinal);
        run.ReadRecorder.Add("INVOICE", new JobReadRecord(package.ExternalPackageId, package.MarketplaceInvoiceNumber, package.MarketplaceInvoiceStatus.ToString(), changed ? JobReadOutcomes.Updated : JobReadOutcomes.Unchanged));
    }

    // A product the catalog read pulled: its model code (or the marketplace's product id), its first barcode and how many
    // variants came with it. A failed batch marks each of its products with the error.
    private void RecordProductRead(RemoteCatalogProduct snapshot, string outcome, string? reason)
    {
        if (!run.ReadRecordingEnabled || run.ReadRecordingKind != "PRODUCT") return;
        var reference = string.IsNullOrWhiteSpace(snapshot.ProductMainId) ? snapshot.ExternalProductId : snapshot.ProductMainId!;
        var barcode = snapshot.Variants.Select(x => x.Barcode).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
        var secondary = barcode is null ? null : snapshot.Variants.Count > 1 ? $"{barcode} +{snapshot.Variants.Count - 1}" : barcode;
        run.ReadRecorder.Add("PRODUCT", new JobReadRecord(reference, secondary, $"{snapshot.Variants.Count} varyant", outcome, reason));
    }


    // Stores what this run pulled on the job's summary row, joined to earlier slices of the same job, and returns the job's
    // counts so far. Display only: a
    // failed write never fails the read it describes.
    private async Task<JobReadCounts?> FlushReadRecordsAsync(Guid tenantId, Guid? jobId, CancellationToken cancellationToken, bool final = false)
    {
        if (jobId is not { } id || !run.ReadRecordingEnabled) return null;
        try
        {
            var row = await db.JobReadSummaries.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.JobId == id, cancellationToken);
            // A finished read that pulled nothing still says so ("0 kayıt"), instead of looking like an old job.
            if (run.ReadRecorder.IsEmpty && (row is not null || !final)) return row is null ? null : new JobReadCounts(row.Received, row.Inserted, row.Updated, row.Unchanged, row.Skipped, row.Failed);
            var previous = row is null ? JobReadCounts.Zero : new JobReadCounts(row.Received, row.Inserted, row.Updated, row.Unchanged, row.Skipped, row.Failed);
            var (counts, kept, omitted) = run.ReadRecorder.MergeWith(previous, row is null ? [] : JobReadRecorder.Deserialize(row.RecordsJson), row?.Omitted ?? 0);
            var kind = row?.Kind ?? run.ReadRecorder.Kind ?? run.ReadRecordingKind;
            var json = JobReadRecorder.Serialize(kept);
            var now = timeProvider.GetUtcNow().ToUniversalTime();
            // Written past the change tracker: after a failed run it may still hold that run's half-applied entities,
            // which must not be saved along with the summary.
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO integration.job_read_summaries ("Id", "TenantId", "JobId", "Kind", "Received", "Inserted", "Updated", "Unchanged", "Skipped", "Failed", "Omitted", "RecordsJson", "UpdatedAt")
                VALUES ({Guid.CreateVersion7()}, {tenantId}, {id}, {kind}, {counts.Received}, {counts.Inserted}, {counts.Updated}, {counts.Unchanged}, {counts.Skipped}, {counts.Failed}, {omitted}, {json}, {now})
                ON CONFLICT ("JobId") DO UPDATE SET "Received" = EXCLUDED."Received", "Inserted" = EXCLUDED."Inserted", "Updated" = EXCLUDED."Updated",
                    "Unchanged" = EXCLUDED."Unchanged", "Skipped" = EXCLUDED."Skipped", "Failed" = EXCLUDED."Failed", "Omitted" = EXCLUDED."Omitted",
                    "RecordsJson" = EXCLUDED."RecordsJson", "UpdatedAt" = EXCLUDED."UpdatedAt"
                """, cancellationToken);
            run.ReadRecorder.Reset();
            return counts;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            run.ReadRecorder.Reset();
            return null;
        }
    }
}

