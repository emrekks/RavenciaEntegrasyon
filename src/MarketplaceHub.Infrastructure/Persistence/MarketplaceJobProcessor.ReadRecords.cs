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
    private readonly JobReadRecorder readRecorder = new();
    private bool readRecordingEnabled;
    private string readRecordingKind = "ORDER";

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
    private string? readReturnSkipReason;
    private string? readReturnFailReason;

    private const string StaleReason = "Panelde daha yeni bir bilgi vardı; eski kayıt uygulanmadı.";
    private const string RejectedOrderReason = "Pazaryeri kaydı eksik veya tutarsızdı; uygulanmadı (Operasyon Sorunları).";

    private async Task<bool> UpsertOrder(Guid tenantId, Guid connectionId, RemoteOrder remote, CancellationToken cancellationToken, OrderIngestionBatch? batch = null, bool saveChanges = true, bool persistFinancialObservations = false)
    {
        if (!readRecordingEnabled) return await UpsertOrderCore(tenantId, connectionId, remote, cancellationToken, batch, saveChanges, persistFinancialObservations);
        var before = await OrderStateAsync(tenantId, connectionId, remote.ExternalOrderId, fromDatabase: true, cancellationToken);
        var skippedBefore = telemetrySkippedCount;
        var accepted = await UpsertOrderCore(tenantId, connectionId, remote, cancellationToken, batch, saveChanges, persistFinancialObservations);
        var after = await OrderStateAsync(tenantId, connectionId, remote.ExternalOrderId, fromDatabase: false, cancellationToken);
        var outcome = !accepted ? JobReadOutcomes.Failed
            : before is null ? (after is null ? JobReadOutcomes.Skipped : JobReadOutcomes.New)
            // An invoice check reads the invoice state, which it compares whatever the order's age: an older order snapshot
            // that left the invoice as it was is unchanged there, not skipped.
            : readRecordingKind != "INVOICE" && telemetrySkippedCount > skippedBefore && after?.Compare == before.Compare ? JobReadOutcomes.Skipped
            : after is not null && after.Compare != before.Compare ? JobReadOutcomes.Updated
            : JobReadOutcomes.Unchanged;
        var reason = outcome == JobReadOutcomes.Failed ? RejectedOrderReason : outcome == JobReadOutcomes.Skipped ? StaleReason : null;
        var state = after ?? before;
        // An invoice check names the packages' invoice state; any other read the order's status.
        var invoiceCheck = readRecordingKind == "INVOICE";
        readRecorder.Add(invoiceCheck ? "INVOICE" : "ORDER", new JobReadRecord(remote.OrderNumber, state?.PackagesText, invoiceCheck ? state?.InvoiceText : state?.Status, outcome, reason));
        return accepted;
    }

    private async Task UpsertReturn(Guid tenantId, Guid connectionId, RemoteReturnClaim remote, CancellationToken cancellationToken)
    {
        if (!readRecordingEnabled) { await UpsertReturnCore(tenantId, connectionId, remote, cancellationToken); return; }
        readReturnSkipReason = null;
        readReturnFailReason = null;
        var before = await ReturnStateAsync(tenantId, connectionId, remote.ExternalClaimId, fromDatabase: true, cancellationToken);
        await UpsertReturnCore(tenantId, connectionId, remote, cancellationToken);
        var after = await ReturnStateAsync(tenantId, connectionId, remote.ExternalClaimId, fromDatabase: false, cancellationToken);
        var stale = before is not null && IsStaleReturnObservation(before.LastRemoteModifiedAt, remote.LastModifiedAt);
        var (outcome, reason) = readReturnFailReason is { } fail ? (JobReadOutcomes.Failed, fail)
            : readReturnSkipReason is { } skip ? (JobReadOutcomes.Skipped, skip)
            : before is null ? (after is null ? (JobReadOutcomes.Skipped, (string?)null) : (JobReadOutcomes.New, null))
            : after is not null && after.Version != before.Version ? (JobReadOutcomes.Updated, null)
            : stale ? (JobReadOutcomes.Skipped, StaleReason)
            : (JobReadOutcomes.Unchanged, null);
        var orderNumber = after?.OrderNumber ?? before?.OrderNumber ?? remote.ExternalOrderId;
        readRecorder.Add("RETURN", new JobReadRecord(remote.ExternalClaimId, orderNumber, (after ?? before)?.Status ?? remote.RawStatus, outcome, reason));
    }

    private sealed record OrderReadState(string Compare, string Status, string? PackagesText, string? InvoiceText);
    // A package the invoice check read on its own (Trendyol): its number, the marketplace invoice number and state.
    private void RecordInvoiceRead(ShipmentPackage package, MarketplaceInvoiceStatus before, string? numberBefore)
    {
        if (!readRecordingEnabled || readRecordingKind != "INVOICE") return;
        var changed = before != package.MarketplaceInvoiceStatus || !string.Equals(numberBefore, package.MarketplaceInvoiceNumber, StringComparison.Ordinal);
        readRecorder.Add("INVOICE", new JobReadRecord(package.ExternalPackageId, package.MarketplaceInvoiceNumber, package.MarketplaceInvoiceStatus.ToString(), changed ? JobReadOutcomes.Updated : JobReadOutcomes.Unchanged));
    }

    // A product the catalog read pulled: its model code (or the marketplace's product id), its first barcode and how many
    // variants came with it. A failed batch marks each of its products with the error.
    private void RecordProductRead(RemoteCatalogProduct snapshot, string outcome, string? reason)
    {
        if (!readRecordingEnabled || readRecordingKind != "PRODUCT") return;
        var reference = string.IsNullOrWhiteSpace(snapshot.ProductMainId) ? snapshot.ExternalProductId : snapshot.ProductMainId!;
        var barcode = snapshot.Variants.Select(x => x.Barcode).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
        var secondary = barcode is null ? null : snapshot.Variants.Count > 1 ? $"{barcode} +{snapshot.Variants.Count - 1}" : barcode;
        readRecorder.Add("PRODUCT", new JobReadRecord(reference, secondary, $"{snapshot.Variants.Count} varyant", outcome, reason));
    }

    private sealed record ReturnReadState(string Status, long Version, DateTimeOffset? LastRemoteModifiedAt, string? OrderNumber);

    // The order as the read leaves it: the tracked entities (a batch may not have saved yet), or before the upsert the
    // stored row overlaid with what this run already changed in memory.
    private async Task<OrderReadState?> OrderStateAsync(Guid tenantId, Guid connectionId, string externalOrderId, bool fromDatabase, CancellationToken cancellationToken)
    {
        var tracked = db.Orders.Local.FirstOrDefault(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ExternalOrderId == externalOrderId);
        Guid orderId;
        string status;
        if (tracked is not null) { orderId = tracked.Id; status = tracked.DerivedStatus; }
        else if (fromDatabase)
        {
            var stored = await db.Orders.AsNoTracking().Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ExternalOrderId == externalOrderId)
                .Select(x => new { x.Id, x.DerivedStatus }).FirstOrDefaultAsync(cancellationToken);
            if (stored is null) return null;
            orderId = stored.Id;
            status = stored.DerivedStatus;
        }
        else return null;
        var packages = new Dictionary<Guid, (string Number, string Key, string Invoice)>();
        if (fromDatabase)
            foreach (var row in await db.ShipmentPackages.AsNoTracking().Where(x => x.TenantId == tenantId && x.OrderId == orderId)
                         .Select(x => new { x.Id, x.ExternalPackageId, x.Status, x.RawStatus, x.CargoTrackingNumber, x.MarketplaceInvoiceStatus, x.MarketplaceInvoiceNumber }).ToListAsync(cancellationToken))
                packages[row.Id] = (row.ExternalPackageId, $"{row.ExternalPackageId}|{row.Status}|{row.RawStatus}|{row.CargoTrackingNumber}|{row.MarketplaceInvoiceStatus}|{row.MarketplaceInvoiceNumber}", row.MarketplaceInvoiceStatus.ToString());
        foreach (var package in db.ShipmentPackages.Local.Where(x => x.TenantId == tenantId && x.OrderId == orderId))
            packages[package.Id] = (package.ExternalPackageId, $"{package.ExternalPackageId}|{package.Status}|{package.RawStatus}|{package.CargoTrackingNumber}|{package.MarketplaceInvoiceStatus}|{package.MarketplaceInvoiceNumber}", package.MarketplaceInvoiceStatus.ToString());
        // The comparison holds the tracking number; the stored text shows only package numbers, at most three.
        var key = string.Join(";", packages.Values.Select(x => x.Key).Order(StringComparer.Ordinal));
        var numbers = packages.Values.Select(x => x.Number).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var text = numbers.Count == 0 ? null : string.Join(", ", numbers.Take(3)) + (numbers.Count > 3 ? $" +{numbers.Count - 3}" : "");
        var invoices = packages.Values.Select(x => x.Invoice).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        return new OrderReadState($"{status}#{key}", status, text, invoices.Count == 0 ? null : string.Join(", ", invoices));
    }

    private async Task<ReturnReadState?> ReturnStateAsync(Guid tenantId, Guid connectionId, string externalClaimId, bool fromDatabase, CancellationToken cancellationToken)
    {
        var tracked = db.ReturnClaims.Local.FirstOrDefault(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ExternalClaimId == externalClaimId);
        if (tracked is not null)
        {
            var orderNumber = db.Orders.Local.FirstOrDefault(x => x.Id == tracked.OrderId)?.OrderNumber
                ?? await db.Orders.AsNoTracking().Where(x => x.Id == tracked.OrderId).Select(x => x.OrderNumber).FirstOrDefaultAsync(cancellationToken);
            return new(tracked.RawStatus, tracked.Version, tracked.LastRemoteModifiedAt, orderNumber);
        }
        if (!fromDatabase) return null;
        return await (from claim in db.ReturnClaims.AsNoTracking()
                      join order in db.Orders.AsNoTracking() on claim.OrderId equals order.Id
                      where claim.TenantId == tenantId && claim.ConnectionId == connectionId && claim.ExternalClaimId == externalClaimId
                      select new ReturnReadState(claim.RawStatus, claim.Version, claim.LastRemoteModifiedAt, order.OrderNumber))
            .FirstOrDefaultAsync(cancellationToken);
    }

    // Stores what this run pulled on the job's summary row, joined to earlier slices of the same job, and returns the job's
    // counts so far. Display only: a
    // failed write never fails the read it describes.
    private async Task<JobReadCounts?> FlushReadRecordsAsync(Guid tenantId, Guid? jobId, CancellationToken cancellationToken, bool final = false)
    {
        if (jobId is not { } id || !readRecordingEnabled) return null;
        try
        {
            var row = await db.JobReadSummaries.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.JobId == id, cancellationToken);
            // A finished read that pulled nothing still says so ("0 kayıt"), instead of looking like an old job.
            if (readRecorder.IsEmpty && (row is not null || !final)) return row is null ? null : new JobReadCounts(row.Received, row.Inserted, row.Updated, row.Unchanged, row.Skipped, row.Failed);
            var previous = row is null ? JobReadCounts.Zero : new JobReadCounts(row.Received, row.Inserted, row.Updated, row.Unchanged, row.Skipped, row.Failed);
            var (counts, kept, omitted) = readRecorder.MergeWith(previous, row is null ? [] : JobReadRecorder.Deserialize(row.RecordsJson), row?.Omitted ?? 0);
            var kind = row?.Kind ?? readRecorder.Kind ?? readRecordingKind;
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
            readRecorder.Reset();
            return counts;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            readRecorder.Reset();
            return null;
        }
    }
}

