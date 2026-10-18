using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

public sealed partial class MarketplaceJobProcessor
{
    // When more Pazarama orders are open than one run reads by order number, the order-date
    // windows that hold them are read whole instead: a few list requests bring every open order
    // with its current item statuses. Returns false when the per-order read is enough.
    private async Task<bool> SyncPazaramaOpenOrdersByWindow(Guid tenantId, Guid connectionId, string correlationId, int batchSize, CancellationToken cancellationToken)
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

        foreach (var (windowStart, windowEnd) in PazaramaOrderHistoryPolicy.Windows(startDay, now))
        {
            string? page = null;
            var pages = 0;
            do
            {
                TrackRequest();
                var result = await orders.PollAsync(
                    Context(tenantId, connectionId, correlationId, $"pazarama-open-orders:{windowStart:yyyyMMdd}:{page ?? "1"}"),
                    new OrderPollWindow(new DateTimeOffset(windowStart, PazaramaOrderHistoryPolicy.TurkeyOffset), new DateTimeOffset(windowEnd, PazaramaOrderHistoryPolicy.TurkeyOffset)),
                    new(page, PazaramaOrderHistoryPolicy.PageSize),
                    cancellationToken);
                if (!result.IsSuccess) { TrackResultFailure(result.Error); throw JobProcessingException.FromAdapter(result.Error!); }
                foreach (var _ in result.Value!.Items) TrackReceived();
                await UpsertOrders(tenantId, connectionId, result.Value.Items, cancellationToken);
                page = result.Value.HasMore ? result.Value.NextCursor : null;
            } while (page is not null && ++pages < PazaramaOpenOrderPolicy.MaxPagesPerWindow && !cancellationToken.IsCancellationRequested);
        }

        // Open orders older than the windows are still read one by one.
        var floor = now.AddMonths(-PazaramaOrderHistoryPolicy.InitialLookbackMonths);
        var older = await (from package in db.ShipmentPackages.AsNoTracking()
                           join order in db.Orders.AsNoTracking()
                               on new { package.TenantId, package.OrderId } equals new { order.TenantId, OrderId = order.Id }
                           where package.TenantId == tenantId && package.ConnectionId == connectionId && order.OrderedAt < floor && package.Status != ShipmentPackageStatus.Delivered && package.Status != ShipmentPackageStatus.Cancelled && package.Status != ShipmentPackageStatus.Returned
                           group package by order.ExternalOrderId into openOrder
                           orderby openOrder.Min(x => x.UpdatedAt)
                           select openOrder.Key)
            .Take(batchSize)
            .ToListAsync(cancellationToken);
        var received = new List<RemoteOrder>();
        foreach (var externalOrderId in older)
        {
            TrackRequest();
            var result = await orders.GetAsync(Context(tenantId, connectionId, correlationId, $"order-lifecycle:{externalOrderId}"), externalOrderId, cancellationToken);
            if (!result.IsSuccess) { if (result.Error?.Class != AdapterErrorClass.NotFound) TrackResultFailure(result.Error); continue; }
            TrackReceived();
            received.Add(result.Value!);
        }
        if (received.Count > 0) await UpsertOrders(tenantId, connectionId, received, cancellationToken);
        return true;
    }
}
