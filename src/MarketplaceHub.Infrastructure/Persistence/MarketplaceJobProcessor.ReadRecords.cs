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

    internal static string ReadRecordKind(string jobType) => jobType is MarketplaceJobTypes.ReturnSync or MarketplaceJobTypes.HepsiburadaReturnSync or MarketplaceJobTypes.ReturnStatusSync or MarketplaceJobTypes.ReturnReconciliation ? "RETURN" : "ORDER";
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
            : telemetrySkippedCount > skippedBefore && after?.Compare == before.Compare ? JobReadOutcomes.Skipped
            : after is not null && after.Compare != before.Compare ? JobReadOutcomes.Updated
            : JobReadOutcomes.Unchanged;
        var reason = outcome == JobReadOutcomes.Failed ? RejectedOrderReason : outcome == JobReadOutcomes.Skipped ? StaleReason : null;
        var state = after ?? before;
        readRecorder.Add("ORDER", new JobReadRecord(remote.OrderNumber, state?.PackagesText, state?.Status, outcome, reason));
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

    private sealed record OrderReadState(string Compare, string Status, string? PackagesText);
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
        var packages = new Dictionary<Guid, (string Number, string Key)>();
        if (fromDatabase)
            foreach (var row in await db.ShipmentPackages.AsNoTracking().Where(x => x.TenantId == tenantId && x.OrderId == orderId)
                         .Select(x => new { x.Id, x.ExternalPackageId, x.Status, x.RawStatus, x.CargoTrackingNumber }).ToListAsync(cancellationToken))
                packages[row.Id] = (row.ExternalPackageId, $"{row.ExternalPackageId}|{row.Status}|{row.RawStatus}|{row.CargoTrackingNumber}");
        foreach (var package in db.ShipmentPackages.Local.Where(x => x.TenantId == tenantId && x.OrderId == orderId))
            packages[package.Id] = (package.ExternalPackageId, $"{package.ExternalPackageId}|{package.Status}|{package.RawStatus}|{package.CargoTrackingNumber}");
        // The comparison holds the tracking number; the stored text shows only package numbers, at most three.
        var key = string.Join(";", packages.Values.Select(x => x.Key).Order(StringComparer.Ordinal));
        var numbers = packages.Values.Select(x => x.Number).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var text = numbers.Count == 0 ? null : string.Join(", ", numbers.Take(3)) + (numbers.Count > 3 ? $" +{numbers.Count - 3}" : "");
        return new OrderReadState($"{status}#{key}", status, text);
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
            var row = await db.JobReadSummaries.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.JobId == id, cancellationToken);
            // A finished read that pulled nothing still says so ("0 kayıt"), instead of looking like an old job.
            if (readRecorder.IsEmpty && (row is not null || !final)) return row is null ? null : new JobReadCounts(row.Received, row.Inserted, row.Updated, row.Unchanged, row.Skipped, row.Failed);
            var previous = row is null ? JobReadCounts.Zero : new JobReadCounts(row.Received, row.Inserted, row.Updated, row.Unchanged, row.Skipped, row.Failed);
            var (counts, kept, omitted) = readRecorder.MergeWith(previous, row is null ? [] : JobReadRecorder.Deserialize(row.RecordsJson), row?.Omitted ?? 0);
            if (row is null)
            {
                row = new JobReadSummary { Id = Guid.CreateVersion7(), TenantId = tenantId, JobId = id, Kind = readRecorder.Kind ?? readRecordingKind, RecordsJson = "[]" };
                db.JobReadSummaries.Add(row);
            }
            row.Received = counts.Received; row.Inserted = counts.Inserted; row.Updated = counts.Updated; row.Unchanged = counts.Unchanged;
            row.Skipped = counts.Skipped; row.Failed = counts.Failed; row.Omitted = omitted;
            row.RecordsJson = JobReadRecorder.Serialize(kept);
            row.UpdatedAt = timeProvider.GetUtcNow();
            await db.SaveChangesAsync(cancellationToken);
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

