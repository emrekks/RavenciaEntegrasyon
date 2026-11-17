using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

// ISLEYICI-9 stage 3c: the claim upsert with its read record (Çekilen Kayıtlar), moved from the processor's read records.
internal sealed partial class ReturnIngestionService
{
    internal async Task UpsertReturn(Guid tenantId, Guid connectionId, RemoteReturnClaim remote, CancellationToken cancellationToken)
    {
        if (!run.ReadRecordingEnabled) { await UpsertReturnCore(tenantId, connectionId, remote, cancellationToken); return; }
        run.ReadReturnSkipReason = null;
        run.ReadReturnFailReason = null;
        var before = await ReturnStateAsync(tenantId, connectionId, remote.ExternalClaimId, fromDatabase: true, cancellationToken);
        await UpsertReturnCore(tenantId, connectionId, remote, cancellationToken);
        var after = await ReturnStateAsync(tenantId, connectionId, remote.ExternalClaimId, fromDatabase: false, cancellationToken);
        var stale = before is not null && IsStaleReturnObservation(before.LastRemoteModifiedAt, remote.LastModifiedAt);
        var (outcome, reason) = run.ReadReturnFailReason is { } fail ? (JobReadOutcomes.Failed, fail)
            : run.ReadReturnSkipReason is { } skip ? (JobReadOutcomes.Skipped, skip)
            : before is null ? (after is null ? (JobReadOutcomes.Skipped, (string?)null) : (JobReadOutcomes.New, null))
            : after is not null && after.Version != before.Version ? (JobReadOutcomes.Updated, null)
            : stale ? (JobReadOutcomes.Skipped, OrderIngestionService.StaleReason)
            : (JobReadOutcomes.Unchanged, null);
        var orderNumber = after?.OrderNumber ?? before?.OrderNumber ?? remote.ExternalOrderId;
        run.ReadRecorder.Add("RETURN", new JobReadRecord(remote.ExternalClaimId, orderNumber, (after ?? before)?.Status ?? remote.RawStatus, outcome, reason));
    }

    private sealed record ReturnReadState(string Status, long Version, DateTimeOffset? LastRemoteModifiedAt, string? OrderNumber);

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
}
