using System.Text.Json;
using MarketplaceHub.Application;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

public sealed partial class MarketplaceJobProcessor
{
    private async Task<bool> SyncShopifyOrders(Guid tenantId, Guid connectionId, string payloadJson, string correlationId, string cursorResourceType, bool allowBaseline, CancellationToken cancellationToken)
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
            var single = await orders.GetAsync(Context(tenantId, connectionId, correlationId, $"shopify-order-get:{externalOrderId}"), externalOrderId.Trim(), cancellationToken);
            if (!single.IsSuccess) { TrackResultFailure(single.Error); throw JobProcessingException.FromAdapter(single.Error!); }
            TrackReceived();
            await UpsertOrder(tenantId, connectionId, single.Value!, cancellationToken, persistFinancialObservations: true);
            return true;
        }

        var cursor = await Cursor(tenantId, connectionId, cursorResourceType, cancellationToken);
        var now = timeProvider.GetUtcNow();
        var baselineCursor = allowBaseline ? await Cursor(tenantId, connectionId, $"{cursorResourceType}_BASELINE", cancellationToken) : null;
        var modifiedAfter = ShopifyOrderHistoryPolicy.ModifiedAfter(
            full,
            allowBaseline,
            cursor.LastModifiedWatermark,
            cursor.LastSuccessAt,
            now,
            baselineCursor?.LastSuccessAt);
        var readsBaseline = allowBaseline && modifiedAfter == ShopifyOrderHistoryPolicy.InitialWindowStart(now);
        // A read stopped between pages resumes in the window its page cursor belongs to.
        var resumed = ShopifyOrderHistoryPolicy.Decode(cursor.OpaqueCursor);
        var window = resumed ?? new ShopifyOrderHistoryPolicy.ResumableWindow(modifiedAfter, now, 0, null);
        if (resumed is not null) readsBaseline = false;
        var latestSeen = window.From;
        do
        {
            TrackRequest();
            var result = await orders.PollAsync(
                Context(tenantId, connectionId, correlationId, $"shopify-order-sync:{window.Cursor ?? "0"}"),
                new OrderPollWindow(window.From, window.Until, null),
                new(window.Cursor, 100),
                cancellationToken);
            if (!result.IsSuccess) { TrackResultFailure(result.Error); throw JobProcessingException.FromAdapter(result.Error!); }
            await IngestOrderPageAsync(tenantId, connectionId, result.Value!, "shopify-order-contract", cancellationToken);
            foreach (var item in result.Value!.Items)
                if (latestSeen is null || item.LastModifiedAt > latestSeen) latestSeen = item.LastModifiedAt;
            if (result.Value.HasMore)
            {
                if (string.IsNullOrWhiteSpace(result.Value.NextCursor)) throw new InvalidOperationException("Shopify sipariş sayfası hasMore=true ancak nextCursor boş döndü.");
                window = window.Pages + 1 >= ShopifyOrderHistoryPolicy.PagesBeforeRestart && latestSeen is { } restartFrom
                    ? window with { From = restartFrom, Pages = 0, Cursor = null }
                    : window with { Pages = window.Pages + 1, Cursor = result.Value.NextCursor };
                cursor.OpaqueCursor = ShopifyOrderHistoryPolicy.Encode(window);
                cursor.Version++;
                await db.SaveChangesAsync(cancellationToken);
                continue;
            }

            cursor.OpaqueCursor = null;
            cursor.LastModifiedWatermark = window.Until.AddSeconds(-60);
            cursor.Version++;
            if (readsBaseline && baselineCursor is not null)
            {
                baselineCursor.LastSuccessAt = now;
                baselineCursor.Version++;
            }
            await db.SaveChangesAsync(cancellationToken);
            break;
        } while (!cancellationToken.IsCancellationRequested);
        return true;
    }

    // n11 lists shipment packages by last modification, at most 15 days per
    // request, so a run walks consecutive windows from the watermark (or three
    // months back for a full scan) up to now. Status changes of older orders
    // come back through the same modification filter.
    private async Task<bool> SyncN11Orders(Guid tenantId, Guid connectionId, string payloadJson, string correlationId, string cursorResourceType, bool allowBaseline, CancellationToken cancellationToken)
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
            var single = await orders.GetAsync(Context(tenantId, connectionId, correlationId, $"n11-order-get:{externalOrderId}"), externalOrderId.Trim(), cancellationToken);
            if (!single.IsSuccess) { TrackResultFailure(single.Error); throw JobProcessingException.FromAdapter(single.Error!); }
            TrackReceived();
            await UpsertOrders(tenantId, connectionId, [single.Value!], cancellationToken);
            return true;
        }

        var cursor = await Cursor(tenantId, connectionId, cursorResourceType, cancellationToken);
        var now = timeProvider.GetUtcNow();
        var hasSnapshots = await db.Orders.AsNoTracking().AnyAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId, cancellationToken);
        var baseline = allowBaseline && (full || !hasSnapshots);
        // The connection's overlap, at least ten minutes: n11 may index a change a little after its time.
        var overlapSeconds = await db.ConnectionSyncPolicies.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ResourceType == "ORDERS")
            .Select(x => (int?)x.OverlapSeconds)
            .SingleOrDefaultAsync(cancellationToken) ?? 0;
        var overlap = TimeSpan.FromSeconds(Math.Clamp(Math.Max(overlapSeconds, N11OrderHistoryPolicy.MinimumOverlapSeconds), 0, 86_399));
        foreach (var (windowStart, windowEnd) in N11OrderHistoryPolicy.Windows(N11OrderHistoryPolicy.Start(baseline, cursor.LastModifiedWatermark ?? (allowBaseline ? null : now.AddDays(-1)), overlap, now), now))
        {
            // Pages are numbered and sorted by last change: a package changed while the window is walked moves to
            // its end and shifts the rest up a place. When the window's count changes during the walk, it is
            // walked once more so the shifted package is not skipped.
            string? page = null;
            int? firstTotal = null, lastTotal = null;
            var rewalked = false;
            do
            {
                TrackRequest();
                var result = await orders.PollAsync(
                    Context(tenantId, connectionId, correlationId, $"n11-order-sync:{windowStart.ToUnixTimeMilliseconds()}:{page ?? "0"}"),
                    new OrderPollWindow(windowStart, windowEnd),
                    new(page, 100),
                    cancellationToken);
                if (!result.IsSuccess) { TrackResultFailure(result.Error); throw JobProcessingException.FromAdapter(result.Error!); }
                await IngestOrderPageAsync(tenantId, connectionId, result.Value!, "n11-order-contract", cancellationToken);
                if (page is null) firstTotal = result.Value!.TotalCount;
                lastTotal = result.Value!.TotalCount;
                page = result.Value.HasMore ? result.Value.NextCursor : null;
                if (page is null && !rewalked && firstTotal is not null && lastTotal is not null && firstTotal != lastTotal)
                {
                    rewalked = true;
                    page = "0";
                    firstTotal = null;
                }
            } while (page is not null && !cancellationToken.IsCancellationRequested);

            // Each finished window moves the watermark, so an interrupted run
            // resumes from the last complete window.
            if (cursor.LastModifiedWatermark is null || windowEnd > cursor.LastModifiedWatermark)
            {
                cursor.LastModifiedWatermark = windowEnd;
                cursor.Version++;
                await db.SaveChangesAsync(cancellationToken);
            }
        }
        return true;
    }

    // Pazarama lists orders by order date only, at most a month per request. A full scan reads the
    // last three months; an incremental run re-reads the last days, because the day filter cannot
    // name only the new orders. Status changes of older orders come from the open-order scan.
    private async Task<bool> SyncPazaramaOrders(Guid tenantId, Guid connectionId, string payloadJson, string correlationId, string cursorResourceType, bool allowBaseline, CancellationToken cancellationToken)
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
            var single = await orders.GetAsync(Context(tenantId, connectionId, correlationId, $"pazarama-order-get:{externalOrderId}"), externalOrderId.Trim(), cancellationToken);
            if (!single.IsSuccess) { TrackResultFailure(single.Error); throw JobProcessingException.FromAdapter(single.Error!); }
            TrackReceived();
            await UpsertOrders(tenantId, connectionId, [single.Value!], cancellationToken);
            return true;
        }

        var cursor = await Cursor(tenantId, connectionId, cursorResourceType, cancellationToken);
        var now = timeProvider.GetUtcNow();
        var hasSnapshots = await db.Orders.AsNoTracking().AnyAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId, cancellationToken);
        var baseline = allowBaseline && (full || !hasSnapshots);
        var startDay = PazaramaOrderHistoryPolicy.StartDay(baseline, cursor.LastModifiedWatermark ?? (allowBaseline ? null : now.AddDays(-1)), now,
            PazaramaOrderHistoryPolicy.LookbackDays(cursorResourceType));
        foreach (var (windowStart, windowEnd) in PazaramaOrderHistoryPolicy.Windows(startDay, now))
        {
            var walk = await PagedReadLoop.RunAsync(null, PazaramaOrderPageGuard, async (page, pageIndex) =>
            {
                TrackRequest();
                var result = await orders.PollAsync(
                    Context(tenantId, connectionId, correlationId, $"pazarama-order-sync:{windowStart:yyyyMMdd}:{page ?? "1"}"),
                    new OrderPollWindow(new DateTimeOffset(windowStart, PazaramaOrderHistoryPolicy.TurkeyOffset), new DateTimeOffset(windowEnd, PazaramaOrderHistoryPolicy.TurkeyOffset)),
                    new(page, PazaramaOrderHistoryPolicy.PageSize),
                    cancellationToken);
                if (!result.IsSuccess) { TrackResultFailure(result.Error); throw JobProcessingException.FromAdapter(result.Error!); }
                await IngestOrderPageAsync(tenantId, connectionId, result.Value!, "pazarama-order-contract", cancellationToken);
                return new PageStep(result.Value!.HasMore ? result.Value.NextCursor : null, PazaramaOrderPageKey(result.Value.Items));
            }, cancellationToken);
            // A list that never ends (a page number it ignores) is not read for ever: the window is closed and named.
            if (walk.Stop == PagedReadStop.PageCap)
                await RecordIssue(tenantId, $"pazarama-order-page-cap:{connectionId}:{windowStart:yyyyMMdd}", "PAZARAMA_ORDER_PAGE_CAP",
                    $"Pazarama sipariş listesi {windowStart:dd.MM.yyyy} başlangıçlı pencerede {PazaramaOrderPageCap} sayfayı aştı; okuma bu pencerede durduruldu.", cancellationToken);
            // The same orders again on the next page: the list ignored the page number. The window is closed.
            else if (walk.Stop == PagedReadStop.RepeatedPage)
                await RecordIssue(tenantId, $"pazarama-order-page-repeat:{connectionId}", "PAZARAMA_ORDER_PAGE_REPEATED",
                    "Pazarama sipariş listesi sonraki sayfada aynı siparişleri döndürdü (sayfa numarası yok sayıldı); bu pencerede okuma durduruldu.", cancellationToken);
        }
        // The watermark is the moment of this finished run; the next run starts a few days before it.
        cursor.LastModifiedWatermark = now;
        cursor.Version++;
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }
}
