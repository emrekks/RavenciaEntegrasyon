using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Adapters;
using MarketplaceHub.Infrastructure.Adapters.Hepsiburada;
using MarketplaceHub.Infrastructure.Adapters.N11;
using MarketplaceHub.Infrastructure.Adapters.Pazarama;
using MarketplaceHub.Infrastructure.Adapters.PttAvm;
using MarketplaceHub.Infrastructure.Adapters.Trendyol;
using MarketplaceHub.Infrastructure.Adapters.Trendyol.Mapping;
using MarketplaceHub.Infrastructure.Imports;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace MarketplaceHub.Infrastructure.Persistence;

public sealed partial class MarketplaceJobProcessor
{
    private sealed record OrderReconcileState(DateTimeOffset AnchorEnd, int WindowIndex, int StoreFrontIndex, string? NextCursor, string[] StoreFronts);
    private static readonly TimeSpan SliceBudget = TimeSpan.FromSeconds(30);

    // Trendyol reconciliation re-reads the order stream over the last 1, 3 or 90 days (14-day
    // windows, active storefronts) instead of asking for orders one by one. It works in slices of
    // about 30 seconds and saves its place, so the shared orders lock is never held long and a
    // new-order read waits at most one slice.
    private async Task<JobExecutionResult> ReconcileTrendyolOrderWindows(Guid tenantId, Guid connectionId, string payloadJson, string correlationId, CancellationToken cancellationToken)
    {
        var lookback = TimeSpan.FromDays(ReadBoundedInt(payloadJson, "lookbackDays", 1, 1, 90));
        var startedAt = timeProvider.GetUtcNow();
        var cursor = await Cursor(tenantId, connectionId, $"ORDER_RECONCILE_{(int)lookback.TotalDays}D", cancellationToken);
        OrderReconcileState? state = null;
        if (!string.IsNullOrWhiteSpace(cursor.OpaqueCursor))
        {
            try { state = JsonSerializer.Deserialize<OrderReconcileState>(cursor.OpaqueCursor); }
            catch (JsonException) { }
        }
        // A pass left unfinished for a day starts over rather than finishing stale windows.
        if (state is null || state.StoreFronts is not { Length: > 0 } || state.StoreFrontIndex >= state.StoreFronts.Length || startedAt - state.AnchorEnd > TimeSpan.FromDays(1))
        {
            var activity = ReadStorefrontActivity(await Cursor(tenantId, connectionId, TrendyolStorefrontPolicy.CursorResource, cancellationToken));
            state = new(startedAt, 0, 0, null, [.. TrendyolStorefrontPolicy.Active(TrendyolReadStorefronts.Codes, activity, startedAt)]);
        }

        var oldest = state.AnchorEnd - lookback;
        while (!cancellationToken.IsCancellationRequested)
        {
            var end = state.AnchorEnd - TimeSpan.FromTicks(state.WindowIndex * (OrderStreamWindowSpan.Ticks + TimeSpan.TicksPerMillisecond));
            var start = end - OrderStreamWindowSpan < oldest ? oldest : end - OrderStreamWindowSpan;
            var storefront = state.StoreFronts[state.StoreFrontIndex];
            TrackRequest();
            var page = await orders.PollAsync(Context(tenantId, connectionId, correlationId, $"order-reconcile:{storefront}:{state.WindowIndex}:{state.NextCursor ?? "0"}"), new(start, end, storefront), new(state.NextCursor, 200), cancellationToken);
            if (!page.IsSuccess)
            {
                if (state.NextCursor is not null && page.Error is { Class: AdapterErrorClass.Validation, HttpStatus: 400 })
                    state = state with { NextCursor = null };
                else if (state.StoreFrontIndex > 0 && state.NextCursor is null && page.Error?.HttpStatus is 400 or 404)
                    state = state with { StoreFrontIndex = state.StoreFrontIndex + 1 };
                else
                {
                    TrackResultFailure(page.Error);
                    cursor.OpaqueCursor = JsonSerializer.Serialize(state);
                    cursor.Version++;
                    await db.SaveChangesAsync(cancellationToken);
                    return JobExecutionResult.FromAdapterError(page.Error!);
                }
            }
            else
            {
                await IngestOrderPageAsync(tenantId, connectionId, page.Value!, "order-contract", cancellationToken);
                state = page.Value!.HasMore && !string.IsNullOrWhiteSpace(page.Value.NextCursor)
                    ? state with { NextCursor = page.Value.NextCursor }
                    : state with { StoreFrontIndex = state.StoreFrontIndex + 1, NextCursor = null };
            }

            if (state.StoreFrontIndex >= state.StoreFronts.Length)
            {
                if (start <= oldest)
                {
                    cursor.OpaqueCursor = null;
                    cursor.Version++;
                    await db.SaveChangesAsync(cancellationToken);
                    return JobExecutionResult.Success();
                }
                state = state with { WindowIndex = state.WindowIndex + 1, StoreFrontIndex = 0, NextCursor = null };
            }
            cursor.OpaqueCursor = JsonSerializer.Serialize(state);
            cursor.Version++;
            await db.SaveChangesAsync(cancellationToken);
            if (timeProvider.GetUtcNow() - startedAt >= SliceBudget)
                return JobExecutionResult.Continue("Sipariş uzlaştırması kaldığı pencereden birkaç saniye içinde devam edecek.");
        }
        return JobExecutionResult.Continue("Sipariş uzlaştırması kaldığı pencereden devam edecek.");
    }

    private async Task<bool> ReconcileOrders(Guid tenantId, Guid connectionId, string payloadJson, string correlationId, CancellationToken cancellationToken)
    {
        var platformCode = await db.PlatformConnections.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.Id == connectionId)
            .Select(x => x.PlatformCode)
            .SingleOrDefaultAsync(cancellationToken);
        var isShopify = platformCode == "SHOPIFY";
        var lookbackDays = ReadBoundedInt(payloadJson, "lookbackDays", 1, 1, 90);
        var batchSize = ReadBoundedInt(payloadJson, "batchSize", 25, 1, 100);
        var end = timeProvider.GetUtcNow();
        var start = end.AddDays(-lookbackDays);
        // Reconciliation is deliberately driven by local order numbers and the
        // documented orderNumber read. The stream is ideal for discovery, but a
        // per-order read is the reliable repair path for already imported orders
        // when a stream cursor or stream request is temporarily unavailable.
        // The window is walked in order (OrderedAt, Id) from where the last run of the same window stopped, wrapping round.
        // It used to take the orders with the oldest UpdatedAt, which only rotated because every read rewrote UpdatedAt;
        // an unchanged order now keeps it (SyncNoOpWrites), so the oldest would have been read again on every run.
        var cursor = await Cursor(tenantId, connectionId, $"ORDER_RECONCILE_{lookbackDays}D", cancellationToken);
        var window = db.Orders.AsNoTracking()
            .Where(x => x.TenantId == tenantId
                && x.ConnectionId == connectionId
                && x.OrderedAt >= start
                && x.OrderedAt <= end);
        var page = window;
        if (OrderReconcilePosition(cursor.OpaqueCursor) is { } after)
            page = page.Where(x => x.OrderedAt > after.OrderedAt || x.OrderedAt == after.OrderedAt && x.Id.CompareTo(after.Id) > 0);
        var picked = await page.OrderBy(x => x.OrderedAt).ThenBy(x => x.Id)
            .Select(x => new { x.ExternalOrderId, x.OrderedAt, x.Id })
            .Take(batchSize)
            .ToListAsync(cancellationToken);
        if (picked.Count < batchSize && page != window)
        {
            var pickedIds = picked.Select(x => x.Id).ToHashSet();
            picked.AddRange((await window.OrderBy(x => x.OrderedAt).ThenBy(x => x.Id)
                    .Select(x => new { x.ExternalOrderId, x.OrderedAt, x.Id })
                    .Take(batchSize - picked.Count)
                    .ToListAsync(cancellationToken))
                .Where(x => !pickedIds.Contains(x.Id)));
        }
        var externalOrderIds = picked.Select(x => x.ExternalOrderId).ToList();
        var nextPosition = picked.Count == 0 ? null : OrderReconcilePosition(picked[^1].OrderedAt, picked[^1].Id);
        if (cursor.OpaqueCursor != nextPosition)
        {
            cursor.OpaqueCursor = nextPosition;
            cursor.Version++;
            await db.SaveChangesAsync(cancellationToken);
        }

        foreach (var externalOrderId in externalOrderIds)
        {
            TrackRequest();
            var result = await orders.GetAsync(Context(tenantId, connectionId, correlationId, $"order-reconcile:{externalOrderId}"), externalOrderId, await OrderHint(tenantId, connectionId, externalOrderId, cancellationToken), cancellationToken);
            if (!result.IsSuccess)
            {
                if (result.Error?.Class == AdapterErrorClass.NotFound) continue;
                TrackResultFailure(result.Error);
                await RecordIssue(tenantId, $"order-reconcile:{connectionId}:{externalOrderId}", result.Error!.Code,
                    $"Sipariş uzlaştırılamadı; sonraki otomatik taramada tekrar denenecek. {result.Error.SafeMessage}", cancellationToken);
                continue;
            }

            TrackReceived();
            await UpsertOrder(tenantId, connectionId, result.Value!, cancellationToken, persistFinancialObservations: isShopify);
            await ResolveIssue(tenantId, $"order-reconcile:{connectionId}:{externalOrderId}", cancellationToken);
        }

        return true;
    }

    // The last order a reconcile run of a window read: "<OrderedAt UTC ticks>|<order id>" (ticks keep PostgreSQL's microseconds).
    internal static string OrderReconcilePosition(DateTimeOffset orderedAt, Guid id) =>
        $"{orderedAt.UtcTicks.ToString(CultureInfo.InvariantCulture)}|{id:D}";

    internal static (DateTimeOffset OrderedAt, Guid Id)? OrderReconcilePosition(string? saved)
    {
        var parts = saved?.Split('|');
        return parts is { Length: 2 } && long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
            && ticks >= DateTimeOffset.MinValue.UtcTicks && ticks <= DateTimeOffset.MaxValue.UtcTicks && Guid.TryParse(parts[1], out var id)
            ? (new DateTimeOffset(ticks, TimeSpan.Zero), id)
            : null;
    }
}
