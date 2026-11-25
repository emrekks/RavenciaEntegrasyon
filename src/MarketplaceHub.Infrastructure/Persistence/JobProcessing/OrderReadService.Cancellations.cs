using MarketplaceHub.Application;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

internal sealed partial class OrderReadService
{
    // At most MaxPages pages of 50 lines; a cursor that does not move ends the read. No stop on cancellation between pages:
    // the read goes on to the next request (which fails on the cancelled token) instead of taking the stop for its end.
    private static readonly PageGuard HepsiburadaCancellationPageGuard = new(MaxPages: HepsiburadaCancellationPolicy.MaxPages, StopOnRepeatedCursor: true, StopOnCancellation: false);

    // Hepsiburada's cancelled-order list: the last day every 15 minutes, the whole month once a day.
    // A listed order that the panel does not show as cancelled is read again by the open-order scan, unless every
    // line the list names is already cancelled in the panel: a partly cancelled order stays open for good, and
    // reading it again each run filled the per-run reads so that real full cancellations were never reached.
    // The listed lines (with their cancelDate) are added to listedLines, so the orders read again carry the time.
    private async Task<IReadOnlyList<string>> HepsiburadaCancelledOrdersToRead(Guid tenantId, Guid connectionId, string correlationId, List<RemoteCancelledOrderLine> listedLines, CancellationToken cancellationToken, OpenOrderReadTally? tally = null)
    {
        var now = timeProvider.GetUtcNow();
        var cursor = await Cursor(tenantId, connectionId, HepsiburadaCancellationPolicy.CursorResource, cancellationToken);
        var wideCursor = await Cursor(tenantId, connectionId, HepsiburadaCancellationPolicy.WideCursorResource, cancellationToken);
        var wideStopped = wideCursor.OpaqueCursor == HepsiburadaCancellationPolicy.WideStoppedMark;
        if (HepsiburadaCancellationPolicy.Window(cursor.LastSuccessAt, wideCursor.LastSuccessAt, now, wideStopped) is not { } window) return [];
        // The last-day read that follows a stopped month clears the mark, so the next run tries the month again.
        if (wideStopped && !window.Wide) SetWideMark(wideCursor, null);

        var cancelledLines = new List<RemoteCancelledOrderLine>();
        var failed = false;
        // J3 (15 Nov 2026): at the page cap or on a repeated cursor the lines read so far are still used, but the list is not
        // taken as read: its cursors stay where they were (the next run reads the window again) and an issue names the stop.
        // (It used to stop as if the list had ended and move the cursor past the unread rest.)
        var walk = await PagedReadLoop.RunAsync(null, HepsiburadaCancellationPageGuard, async (page, pageIndex) =>
        {
            TrackRequest();
            var result = await hepsiburadaInvoiceStatus.ListCancelledOrderLinesAsync(
                Context(tenantId, connectionId, correlationId, $"hepsiburada-cancelled:{page ?? "0"}"), window.Since, now, new AdapterPageRequest(page, 50), cancellationToken);
            if (!result.IsSuccess)
            {
                // The open-order scan still finds cancellations order by order.
                TrackResultFailure(result.Error);
                await RecordIssue(tenantId, $"hepsiburada-cancelled:{connectionId}", result.Error!.Code, $"Hepsiburada iptal edilen sipariş listesi okunamadı; açık siparişler tek tek taranmaya devam ediyor. {result.Error.SafeMessage}", cancellationToken);
                failed = true;
                return null;
            }
            TrackReceived();
            // A list Hepsiburada answered is an answer for the open-order scan (OpenOrderReadTally).
            tally?.Answered();
            cancelledLines.AddRange(result.Value!.Items);
            return new PageStep(!result.Value.HasMore || string.IsNullOrWhiteSpace(result.Value.NextCursor) ? null : result.Value.NextCursor);
        }, cancellationToken);
        if (failed) return [];
        var incompleteKey = $"hepsiburada-cancelled-incomplete:{connectionId}";
        if (walk.Stop is PagedReadStop.PageCap or PagedReadStop.RepeatedCursor)
        {
            await RecordIssue(tenantId, incompleteKey, "HEPSIBURADA_CANCELLED_LIST_INCOMPLETE",
                walk.Stop == PagedReadStop.PageCap
                    ? $"Hepsiburada iptal edilen sipariş listesi {HepsiburadaCancellationPolicy.MaxPages} sayfayı aştı; okunan kısım işlendi, liste okunmuş sayılmadı ve sonraki taramada yeniden okunacak."
                    : "Hepsiburada iptal edilen sipariş listesi aynı sayfayı yeniden sonraki sayfa olarak verdi; okunan kısım işlendi, liste okunmuş sayılmadı ve sonraki taramada yeniden okunacak.",
                cancellationToken);
            if (window.Wide) SetWideMark(wideCursor, HepsiburadaCancellationPolicy.WideStoppedMark);
        }
        else
        {
            await ResolveIssue(tenantId, $"hepsiburada-cancelled:{connectionId}", cancellationToken);
            await ResolveIssue(tenantId, incompleteKey, cancellationToken);
            cursor.LastSuccessAt = now;
            cursor.Version++;
            if (window.Wide)
            {
                wideCursor.LastSuccessAt = now;
                wideCursor.Version++;
                SetWideMark(wideCursor, null);
            }
        }
        if (cancelledLines.Count == 0) return [];
        listedLines.AddRange(cancelledLines);
        await FillHepsiburadaCancelTimes(tenantId, connectionId, cancelledLines, cancellationToken);

        var listed = cancelledLines.Select(line => line.OrderNumber).Distinct(StringComparer.Ordinal).ToArray();
        var open = await db.Orders.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && listed.Contains(x.ExternalOrderId) && x.DerivedStatus != "CANCELLED")
            .Select(x => new
            {
                x.ExternalOrderId,
                CancelledLines = db.OrderLines.Where(line => line.TenantId == tenantId && line.OrderId == x.Id && line.CancelledQuantity > 0).Select(line => line.ExternalLineId).ToList()
            })
            .ToListAsync(cancellationToken);
        return HepsiburadaCancellationsToRead(
            cancelledLines,
            open.ToDictionary(x => x.ExternalOrderId, x => (IReadOnlyCollection<string>)x.CancelledLines, StringComparer.Ordinal),
            HepsiburadaCancellationPolicy.MaxReadsPerRun);
    }

    private static void SetWideMark(MarketplaceHub.Domain.SyncCursor wideCursor, string? mark)
    {
        if (wideCursor.OpaqueCursor == mark) return;
        wideCursor.OpaqueCursor = mark;
        wideCursor.Version++;
    }

    // The listed orders still to read: open in the panel (a key of locallyCancelledLines, with the ids of its lines
    // already cancelled) and named with a line the panel does not show as cancelled, or with no line at all. A line
    // id the panel does not know keeps the order on the list, so a mismatch only costs a read as before. The latest
    // cancellations come first, so an order that keeps coming back cannot hold newer ones off the per-run reads.
    internal static IReadOnlyList<string> HepsiburadaCancellationsToRead(IEnumerable<RemoteCancelledOrderLine> listed, IReadOnlyDictionary<string, IReadOnlyCollection<string>> locallyCancelledLines, int maximum) =>
        listed
            .Where(line => locallyCancelledLines.TryGetValue(line.OrderNumber, out var cancelled)
                && (string.IsNullOrWhiteSpace(line.LineItemId) || !cancelled.Contains(line.LineItemId.Trim(), StringComparer.Ordinal)))
            .GroupBy(line => line.OrderNumber, StringComparer.Ordinal)
            .OrderByDescending(group => group.Max(line => line.CancelledAt))
            .ThenBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => group.Key)
            .Take(maximum)
            .ToArray();

    // A line the panel already shows cancelled but without a time takes the cancelDate the list gives it; a time the
    // line already has is never replaced (as in OrderLineCancelTime.Apply). Saved with the open-order scan.
    private async Task FillHepsiburadaCancelTimes(Guid tenantId, Guid connectionId, IReadOnlyList<RemoteCancelledOrderLine> listed, CancellationToken cancellationToken)
    {
        var times = HepsiburadaListedCancelTimes(listed);
        if (times.Count == 0) return;
        var orderNumbers = times.Keys.Select(key => key.OrderNumber).Distinct(StringComparer.Ordinal).ToArray();
        var lineIds = times.Keys.Select(key => key.LineItemId).Distinct(StringComparer.Ordinal).ToArray();
        var lines = await (from line in db.OrderLines
                           join order in db.Orders on new { line.TenantId, line.OrderId } equals new { order.TenantId, OrderId = order.Id }
                           where order.TenantId == tenantId && order.ConnectionId == connectionId && orderNumbers.Contains(order.ExternalOrderId)
                               && lineIds.Contains(line.ExternalLineId) && line.CancelledQuantity > 0 && line.CancelledAt == null
                           select new { Line = line, order.ExternalOrderId })
            .ToListAsync(cancellationToken);
        foreach (var found in lines)
        {
            if (!times.TryGetValue((found.ExternalOrderId, found.Line.ExternalLineId), out var cancelledAt)) continue;
            found.Line.CancelledAt = cancelledAt;
            found.Line.Version++;
        }
    }

    // The latest cancelDate the list gives each (order, line), in UTC; a line without an id or a time is left out.
    internal static IReadOnlyDictionary<(string OrderNumber, string LineItemId), DateTimeOffset> HepsiburadaListedCancelTimes(IEnumerable<RemoteCancelledOrderLine> listed) =>
        listed
            .Where(line => !string.IsNullOrWhiteSpace(line.LineItemId) && line.CancelledAt is not null)
            .GroupBy(line => (line.OrderNumber, LineItemId: line.LineItemId!.Trim()))
            .ToDictionary(group => group.Key, group => group.Max(line => line.CancelledAt!.Value).ToUniversalTime());

    // The order read again, each line the list names carrying the list's cancelDate as its cancellation time. A line
    // the list does not name, or names without a time, keeps what the read says: null, as Hepsiburada's order detail
    // gives no cancellation time.
    internal static RemoteOrder WithHepsiburadaListedCancelTimes(RemoteOrder remote, IEnumerable<RemoteCancelledOrderLine> listed)
    {
        var times = HepsiburadaListedCancelTimes(listed.Where(line => line.OrderNumber == remote.ExternalOrderId || line.OrderNumber == remote.OrderNumber));
        if (times.Count == 0) return remote;
        var byLine = times.ToDictionary(pair => pair.Key.LineItemId, pair => pair.Value, StringComparer.Ordinal);
        return remote with
        {
            Lines = [.. remote.Lines.Select(line => byLine.TryGetValue(line.ExternalLineId.Trim(), out var cancelledAt) ? line with { CancelledAt = cancelledAt } : line)]
        };
    }
}
