using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

public sealed partial class MarketplaceJobProcessor
{
    // Hepsiburada's cancelled-order list: the last day every 15 minutes, the whole month once a day.
    // A listed order that the panel does not show as cancelled is read again by the open-order scan.
    private async Task<IReadOnlyList<string>> HepsiburadaCancelledOrdersToRead(Guid tenantId, Guid connectionId, string correlationId, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var cursor = await Cursor(tenantId, connectionId, HepsiburadaCancellationPolicy.CursorResource, cancellationToken);
        var wideCursor = await Cursor(tenantId, connectionId, HepsiburadaCancellationPolicy.WideCursorResource, cancellationToken);
        if (HepsiburadaCancellationPolicy.Window(cursor.LastSuccessAt, wideCursor.LastSuccessAt, now) is not { } window) return [];

        var orderNumbers = new HashSet<string>(StringComparer.Ordinal);
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
            foreach (var line in result.Value!.Items) orderNumbers.Add(line.OrderNumber);
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
        if (orderNumbers.Count == 0) return [];

        var listed = orderNumbers.ToArray();
        return await db.Orders.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && listed.Contains(x.ExternalOrderId) && x.DerivedStatus != "CANCELLED")
            .Select(x => x.ExternalOrderId)
            .Take(HepsiburadaCancellationPolicy.MaxReadsPerRun)
            .ToListAsync(cancellationToken);
    }
}
