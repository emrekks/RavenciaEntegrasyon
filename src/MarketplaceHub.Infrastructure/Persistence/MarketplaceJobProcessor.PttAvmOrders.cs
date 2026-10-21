using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

public sealed partial class MarketplaceJobProcessor
{
    // PTT AVM lists orders by date only, one request per window of at most 40 days with no paging. A full scan
    // reads the last three months; an incremental run re-reads the last days, because the date filter cannot
    // name only the new orders. Status changes of older orders come from the open-order scan.
    private async Task<bool> SyncPttAvmOrders(Guid tenantId, Guid connectionId, string payloadJson, string correlationId, string cursorResourceType, bool allowBaseline, CancellationToken cancellationToken)
    {
        string? externalOrderId = null;
        var full = false;
        try
        {
            using var payload = JsonDocument.Parse(payloadJson);
            if (payload.RootElement.TryGetProperty("externalOrderId", out var value) && value.ValueKind == JsonValueKind.String) externalOrderId = value.GetString();
            if (payload.RootElement.TryGetProperty("full", out var fullValue) && fullValue.ValueKind is JsonValueKind.True or JsonValueKind.False) full = fullValue.GetBoolean();
        }
        catch (JsonException) { return false; }

        if (!string.IsNullOrWhiteSpace(externalOrderId))
        {
            TrackRequest();
            var single = await orders.GetAsync(Context(tenantId, connectionId, correlationId, $"pttavm-order-get:{externalOrderId}"), externalOrderId.Trim(), cancellationToken);
            if (!single.IsSuccess) { TrackResultFailure(single.Error); throw JobProcessingException.FromAdapter(single.Error!); }
            TrackReceived();
            await UpsertOrders(tenantId, connectionId, [single.Value!], cancellationToken);
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
                          select new { order.ExternalOrderId, order.OrderedAt, package.UpdatedAt })
            .ToListAsync(cancellationToken);
        if (open.Count == 0) return false;

        if (PttAvmOrderHistoryPolicy.OpenOrderStartDay(open.Select(x => x.OrderedAt).ToArray(), now) is { } startDay)
            await ReadPttAvmOrderWindows(tenantId, connectionId, correlationId, "pttavm-open-orders", startDay, now, cancellationToken);

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
            if (!result.IsSuccess) { if (result.Error?.Class != AdapterErrorClass.NotFound) TrackResultFailure(result.Error); continue; }
            TrackReceived();
            received.Add(result.Value!);
        }
        if (received.Count > 0) await UpsertOrders(tenantId, connectionId, received, cancellationToken);
        return true;
    }

    // PTT AVM returns are order lines in "iade" status (no return service): each run reads the order windows of the
    // last three months and records the returned lines as completed returns. Nothing is sent to PTT AVM.
    private async Task<bool> SyncPttAvmReturns(Guid tenantId, Guid connectionId, string correlationId, CancellationToken cancellationToken)
    {
        var cursor = await Cursor(tenantId, connectionId, "RETURNS", cancellationToken);
        var now = timeProvider.GetUtcNow();
        var productSnapshots = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var (windowStart, windowEnd) in PttAvmOrderHistoryPolicy.Windows(PttAvmOrderHistoryPolicy.StartDay(true, null, now), now))
        {
            if (cancellationToken.IsCancellationRequested) return false;
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
                await UpsertReturn(tenantId, connectionId, correlationId, claim, productSnapshots, cancellationToken);
            }
        }
        cursor.OpaqueCursor = null;
        cursor.LastModifiedWatermark = now;
        cursor.LastSuccessAt = timeProvider.GetUtcNow();
        cursor.Version++;
        await db.SaveChangesAsync(cancellationToken);
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
