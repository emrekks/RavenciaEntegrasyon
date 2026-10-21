using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

public sealed partial class MarketplaceJobProcessor
{
    // Hepsiburada's cancelled-order list: the last day every 15 minutes, the whole month once a day.
    // A listed order that the panel does not show as cancelled is read again by the open-order scan, unless every
    // line the list names is already cancelled in the panel: a partly cancelled order stays open for good, and
    // reading it again each run filled the per-run reads so that real full cancellations were never reached.
    private async Task<IReadOnlyList<string>> HepsiburadaCancelledOrdersToRead(Guid tenantId, Guid connectionId, string correlationId, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var cursor = await Cursor(tenantId, connectionId, HepsiburadaCancellationPolicy.CursorResource, cancellationToken);
        var wideCursor = await Cursor(tenantId, connectionId, HepsiburadaCancellationPolicy.WideCursorResource, cancellationToken);
        if (HepsiburadaCancellationPolicy.Window(cursor.LastSuccessAt, wideCursor.LastSuccessAt, now) is not { } window) return [];

        var cancelledLines = new List<RemoteCancelledOrderLine>();
        string? page = null;
        for (var pageIndex = 0; pageIndex < HepsiburadaCancellationPolicy.MaxPages; pageIndex++)
        {
            TrackRequest();
            var result = await hepsiburadaInvoiceStatus.ListCancelledOrderLinesAsync(
                Context(tenantId, connectionId, correlationId, $"hepsiburada-cancelled:{page ?? "0"}"), window.Since, now, new AdapterPageRequest(page, 50), cancellationToken);
            if (!result.IsSuccess)
            {
                // The open-order scan still finds cancellations order by order.
                TrackResultFailure(result.Error);
                await RecordIssue(tenantId, $"hepsiburada-cancelled:{connectionId}", result.Error!.Code, $"Hepsiburada iptal edilen sipariş listesi okunamadı; açık siparişler tek tek taranmaya devam ediyor. {result.Error.SafeMessage}", cancellationToken);
                return [];
            }
            TrackReceived();
            cancelledLines.AddRange(result.Value!.Items);
            if (!result.Value.HasMore || string.IsNullOrWhiteSpace(result.Value.NextCursor) || result.Value.NextCursor == page) break;
            page = result.Value.NextCursor;
        }
        await ResolveIssue(tenantId, $"hepsiburada-cancelled:{connectionId}", cancellationToken);
        cursor.LastSuccessAt = now;
        cursor.Version++;
        if (window.Wide)
        {
            wideCursor.LastSuccessAt = now;
            wideCursor.Version++;
        }
        if (cancelledLines.Count == 0) return [];

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
}
