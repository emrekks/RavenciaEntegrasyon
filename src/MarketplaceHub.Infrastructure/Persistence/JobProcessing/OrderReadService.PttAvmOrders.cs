using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

internal sealed partial class OrderReadService
{
    // PTT AVM lists orders by date only, one request per window of at most 40 days with no paging. A full scan
    // reads the last three months; an incremental run re-reads the last days, because the date filter cannot
    // name only the new orders. Status changes of older orders come from the open-order scan.
    private async Task<bool> SyncPttAvmOrders(Guid tenantId, Guid connectionId, string payloadJson, string correlationId, string cursorResourceType, bool allowBaseline, CancellationToken cancellationToken)
    {
        var request = JobPayloadReader.OrderRead(payloadJson) ?? throw OrderSyncPayloadInvalid();
        var full = request.Full;
        if (request.IsSingleOrder)
        {
            await UpsertOrders(tenantId, connectionId, [await ReadSingleOrderAsync(tenantId, connectionId, correlationId, "pttavm-order-get", request.ExternalOrderId!, cancellationToken)], cancellationToken);
            return true;
        }

        var cursor = await Cursor(tenantId, connectionId, cursorResourceType, cancellationToken);
        var now = timeProvider.GetUtcNow();
        var hasSnapshots = await db.Orders.AsNoTracking().AnyAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId, cancellationToken);
        var baseline = allowBaseline && (full || !hasSnapshots);
        var startDay = PttAvmOrderHistoryPolicy.StartDay(baseline, cursor.LastModifiedWatermark ?? (allowBaseline ? null : now.AddDays(-1)), now);
        await ReadPttAvmOrderWindows(tenantId, connectionId, correlationId, "pttavm-order-sync", startDay, now, cancellationToken);
        // The watermark is the moment of this finished run; the next run starts a few days before it.
        cursor.LastModifiedWatermark = now;
        cursor.Version++;
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    // Open PTT AVM orders are read again through the date windows that hold them: a single request per window
    // brings every order of it with its current line statuses. Open orders older than three months are read one by
    // one from the day their number names. Returns false when no order is open.
    private async Task<bool> SyncPttAvmOpenOrdersByWindow(Guid tenantId, Guid connectionId, string correlationId, int batchSize, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var open = await (from package in db.ShipmentPackages.AsNoTracking()
                          join order in db.Orders.AsNoTracking()
                              on new { package.TenantId, package.OrderId } equals new { order.TenantId, OrderId = order.Id }
                          where package.TenantId == tenantId && package.ConnectionId == connectionId && package.Status != ShipmentPackageStatus.Delivered && package.Status != ShipmentPackageStatus.Cancelled && package.Status != ShipmentPackageStatus.Returned
                          // Least lately read first: an unchanged read moves only the invoice "last checked" time (SyncNoOpWrites).
                          select new { order.ExternalOrderId, order.OrderedAt, UpdatedAt = package.MarketplaceInvoiceObservedAt > package.UpdatedAt ? package.MarketplaceInvoiceObservedAt!.Value : package.UpdatedAt })
            .ToListAsync(cancellationToken);
        if (open.Count == 0) return false;

        // The windows throw on a failure, so windows that were read are an answer for OpenOrderReadTally.
        var tally = new OpenOrderReadTally();
        if (PttAvmOrderHistoryPolicy.OpenOrderStartDay(open.Select(x => x.OrderedAt).ToArray(), now) is { } startDay)
        {
            await ReadPttAvmOrderWindows(tenantId, connectionId, correlationId, "pttavm-open-orders", startDay, now, cancellationToken);
            tally.Answered();
        }

        var floor = now.AddMonths(-PttAvmOrderHistoryPolicy.InitialLookbackMonths);
        var older = open.Where(x => x.OrderedAt < floor)
            .GroupBy(x => x.ExternalOrderId)
            .OrderBy(x => x.Min(row => row.UpdatedAt))
            .Select(x => x.Key)
            .Take(batchSize)
            .ToList();
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
        await FailWhenNothingRead(tally, null, null, cancellationToken);
        if (received.Count > 0) await UpsertOrders(tenantId, connectionId, received, cancellationToken);
        return true;
    }

    private async Task ReadPttAvmOrderWindows(Guid tenantId, Guid connectionId, string correlationId, string operation, DateTime startDay, DateTimeOffset now, CancellationToken cancellationToken)
    {
        foreach (var (windowStart, windowEnd) in PttAvmOrderHistoryPolicy.Windows(startDay, now))
        {
            if (cancellationToken.IsCancellationRequested) return;
            TrackRequest();
            var result = await orders.PollAsync(
                Context(tenantId, connectionId, correlationId, $"{operation}:{windowStart:yyyyMMdd}"),
                new OrderPollWindow(new DateTimeOffset(windowStart, PttAvmOrderHistoryPolicy.TurkeyOffset), new DateTimeOffset(windowEnd, PttAvmOrderHistoryPolicy.TurkeyOffset)),
                new(null, 0),
                cancellationToken);
            if (!result.IsSuccess) { TrackResultFailure(result.Error); throw JobProcessingException.FromAdapter(result.Error!); }
            foreach (var _ in result.Value!.Items) TrackReceived();
            foreach (var issue in result.Value.Issues ?? [])
                await RecordIssue(tenantId, $"pttavm-order-contract:{connectionId}:{issue.Identity}:{issue.Code}", issue.Code, issue.Message, cancellationToken);
            await UpsertOrders(tenantId, connectionId, result.Value.Items, cancellationToken);
        }
    }
}
