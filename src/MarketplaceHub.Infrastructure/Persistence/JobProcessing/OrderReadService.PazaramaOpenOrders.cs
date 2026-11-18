using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

internal sealed partial class OrderReadService
{
    // At most MaxPagesPerWindow pages per window (5,000 orders), and a page that repeats the one before ends the window.
    private static readonly PageGuard PazaramaOpenOrderPageGuard = new(MaxPages: PazaramaOpenOrderPolicy.MaxPagesPerWindow, PageRepeated: PazaramaOrderPageRepeated);

    // When more Pazarama orders are open than one run reads by order number, the order-date
    // windows that hold them are read whole instead: a few list requests bring every open order
    // with its current item statuses. Returns false when the per-order read is enough.
    private async Task<bool> SyncPazaramaOpenOrdersByWindow(Guid tenantId, Guid connectionId, string correlationId, int batchSize, SyncCursor cursor, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var openOrderedAt = await (from package in db.ShipmentPackages.AsNoTracking()
                                   join order in db.Orders.AsNoTracking()
                                       on new { package.TenantId, package.OrderId } equals new { order.TenantId, OrderId = order.Id }
                                   where package.TenantId == tenantId && package.ConnectionId == connectionId && package.Status != ShipmentPackageStatus.Delivered && package.Status != ShipmentPackageStatus.Cancelled && package.Status != ShipmentPackageStatus.Returned
                                   select order.OrderedAt)
            .Distinct()
            .ToListAsync(cancellationToken);
        if (PazaramaOpenOrderPolicy.StartDay(openOrderedAt, batchSize, now) is not { } startDay) return false;

        // A window that still has pages after the page cap (or repeats a page) is not dropped silently: it is reported, and
        // J3 (15 Nov 2026) the scan stops there with that code instead of finishing and moving its watermark past open
        // orders it did not read; the next scan reads them again.
        var truncatedWindows = new List<DateTime>();
        var tally = new OpenOrderReadTally();
        var repeatedPage = false;
        foreach (var (windowStart, windowEnd) in PazaramaOrderHistoryPolicy.Windows(startDay, now))
        {
            var walk = await PagedReadLoop.RunAsync(null, PazaramaOpenOrderPageGuard, async (page, pageIndex) =>
            {
                TrackRequest();
                var result = await orders.PollAsync(
                    Context(tenantId, connectionId, correlationId, $"pazarama-open-orders:{windowStart:yyyyMMdd}:{page ?? "1"}"),
                    new OrderPollWindow(new DateTimeOffset(windowStart, PazaramaOrderHistoryPolicy.TurkeyOffset), new DateTimeOffset(windowEnd, PazaramaOrderHistoryPolicy.TurkeyOffset)),
                    new(page, PazaramaOrderHistoryPolicy.PageSize),
                    cancellationToken);
                if (!result.IsSuccess) { TrackResultFailure(result.Error); throw JobProcessingException.FromAdapter(result.Error!); }
                tally.Answered();
                foreach (var _ in result.Value!.Items) TrackReceived();
                await UpsertOrders(tenantId, connectionId, result.Value.Items, cancellationToken);
                return new PageStep(result.Value.HasMore ? result.Value.NextCursor : null, PazaramaOrderPageKey(result.Value.Items));
            }, cancellationToken);
            if (walk.Stop is PagedReadStop.PageCap or PagedReadStop.RepeatedPage) truncatedWindows.Add(windowStart);
            repeatedPage |= walk.Stop == PagedReadStop.RepeatedPage;
        }
        cancellationToken.ThrowIfCancellationRequested();
        var truncatedKey = $"pazarama-open-orders-truncated:{connectionId}";
        if (truncatedWindows.Count > 0)
        {
            var summary = $"Pazarama açık sipariş taramasında {string.Join(", ", truncatedWindows.Select(day => day.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)))} tarihli pencere {(repeatedPage ? "aynı sayfa yeniden geldiği için" : $"{PazaramaOpenOrderPolicy.MaxPagesPerWindow} sayfa sınırında")} kesildi; kalan siparişlerin durumu bu turda güncellenmedi ve tarama tamamlanmış sayılmadı.";
            await RecordIssue(tenantId, truncatedKey, "PAZARAMA_OPEN_ORDER_WINDOW_TRUNCATED", summary, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            throw PagedReadLoop.GuardFailure("PAZARAMA_OPEN_ORDER_WINDOW_TRUNCATED", summary);
        }
        await ResolveIssue(tenantId, truncatedKey, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);

        // Open orders older than the windows are still read one by one.
        var floor = now.AddMonths(-PazaramaOrderHistoryPolicy.InitialLookbackMonths);
        var older = await (from package in db.ShipmentPackages.AsNoTracking()
                           join order in db.Orders.AsNoTracking()
                               on new { package.TenantId, package.OrderId } equals new { order.TenantId, OrderId = order.Id }
                           where package.TenantId == tenantId && package.ConnectionId == connectionId && order.OrderedAt < floor && package.Status != ShipmentPackageStatus.Delivered && package.Status != ShipmentPackageStatus.Cancelled && package.Status != ShipmentPackageStatus.Returned
                           group package by order.ExternalOrderId into openOrder
                           orderby openOrder.Min(x => x.MarketplaceInvoiceObservedAt > x.UpdatedAt ? x.MarketplaceInvoiceObservedAt!.Value : x.UpdatedAt)
                           select openOrder.Key)
            .Take(batchSize)
            .ToListAsync(cancellationToken);
        var received = new List<RemoteOrder>();
        foreach (var externalOrderId in older)
        {
            TrackRequest();
            var result = await orders.GetAsync(Context(tenantId, connectionId, correlationId, $"order-lifecycle:{externalOrderId}"), externalOrderId, cancellationToken);
            if (!result.IsSuccess)
            {
                if (result.Error?.Class == AdapterErrorClass.NotFound) tally.Answered();
                else { TrackResultFailure(result.Error); tally.Failed(result.Error); }
                continue;
            }
            TrackReceived();
            tally.Answered();
            received.Add(result.Value!);
        }
        await FailWhenNothingRead(tally, cursor, cursor.OpaqueCursor, cancellationToken);
        if (received.Count > 0) await UpsertOrders(tenantId, connectionId, received, cancellationToken);
        return true;
    }
}
