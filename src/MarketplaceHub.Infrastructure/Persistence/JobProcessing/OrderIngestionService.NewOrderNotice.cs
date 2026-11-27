using MarketplaceHub.Application;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

internal sealed partial class OrderIngestionService
{
    // An order this read made actionable (NewOrderNoticePolicy.BecameActionable) joins the run's "Yeni Sipariş" notice,
    // unless its store has not caught up: its order read has not completed once since its cursor was (re)created (a new
    // store, the sliced re-import after Tam Sıfırlama or a deleted connection). Read from the stored cursor once per store
    // and run, before the run's own completion is recorded. A failed read only leaves the order unannounced.
    private async Task NoteNewlyActionableOrderAsync(Guid tenantId, Guid connectionId, Guid orderId, CancellationToken cancellationToken)
    {
        if (!run.NewOrderNoticeCaughtUp.TryGetValue(connectionId, out var caughtUp))
        {
            try
            {
                caughtUp = await db.SyncCursors.AsNoTracking().AnyAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId
                    && x.ResourceType == NewOrderNoticePolicy.CaughtUpCursor && x.LastSuccessAt != null, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                caughtUp = false;
            }
            run.NewOrderNoticeCaughtUp[connectionId] = caughtUp;
        }
        if (caughtUp) run.NoteNewOrder(connectionId, orderId);
    }
}
