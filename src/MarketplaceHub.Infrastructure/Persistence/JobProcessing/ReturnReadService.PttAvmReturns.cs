using MarketplaceHub.Application;

namespace MarketplaceHub.Infrastructure.Persistence;

// ISLEYICI-9 stage 3e (16 Nov 2026): PTT AVM's return read, moved as it was from the processor's PTT AVM order part.
internal sealed partial class ReturnReadService
{
    // PTT AVM returns are order lines in "iade" status (no return service), recorded as completed returns. A run reads
    // the order windows of the last two days; the first run, and one every six hours, reads the whole three months
    // (PttAvmReturnReadPolicy). It used to read the three months on every run, every three minutes. Nothing is sent to PTT AVM.
    private async Task<bool> SyncPttAvmReturns(Guid tenantId, Guid connectionId, string payloadJson, string correlationId, CancellationToken cancellationToken)
    {
        var cursor = await Cursor(tenantId, connectionId, "RETURNS", cancellationToken);
        var wideCursor = await Cursor(tenantId, connectionId, ReturnRereadPolicy.WideCursorResource, cancellationToken);
        var now = timeProvider.GetUtcNow();
        var full = ReadBoolean(payloadJson, "forceFull") || cursor.LastSuccessAt is null;
        var wide = PttAvmReturnReadPolicy.WideReadDue(full, wideCursor.LastSuccessAt, now);
        var startDay = PttAvmOrderHistoryPolicy.StartDay(wide, cursor.LastModifiedWatermark, now);
        foreach (var (windowStart, windowEnd) in PttAvmOrderHistoryPolicy.Windows(startDay, now))
        {
            // KUYRUK-9 (16 Nov 2026): a run stopped between windows says so instead of the generic F3_JOB_REJECTED; it ends as
            // it did (blocked, the watermark not moved).
            if (cancellationToken.IsCancellationRequested)
                throw new JobProcessingException(JobExecutionResult.Blocked("PTTAVM_RETURN_READ_CANCELLED", "PttAVM iade okuması tamamlanmadan durduruldu; okunmayan günler okunmuş sayılmadı ve sonraki okumada yeniden okunacak."));
            TrackRequest();
            var result = await returns.PollAsync(
                Context(tenantId, connectionId, correlationId, $"pttavm-return-sync:{windowStart:yyyyMMdd}"),
                new ReturnPollWindow(new DateTimeOffset(windowStart, PttAvmOrderHistoryPolicy.TurkeyOffset), new DateTimeOffset(windowEnd, PttAvmOrderHistoryPolicy.TurkeyOffset)),
                new(null, 0),
                cancellationToken);
            if (!result.IsSuccess) { TrackResultFailure(result.Error); throw JobProcessingException.FromAdapter(result.Error!); }
            foreach (var claim in result.Value!.Items)
            {
                TrackReceived();
                await UpsertReturn(tenantId, connectionId, claim, cancellationToken);
            }
        }
        if (wide)
        {
            wideCursor.LastSuccessAt = now;
            wideCursor.Version++;
        }
        cursor.OpaqueCursor = null;
        cursor.LastModifiedWatermark = now;
        cursor.LastSuccessAt = timeProvider.GetUtcNow();
        cursor.Version++;
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }
}
