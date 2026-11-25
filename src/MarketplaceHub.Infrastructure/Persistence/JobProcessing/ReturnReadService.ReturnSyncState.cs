using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Adapters.Trendyol;

namespace MarketplaceHub.Infrastructure.Persistence;

// The Trendyol return read's resumable state (ISLEYICI-9 stage 3c: it stays with the read; the claim ingestion moved to
// ReturnIngestionService).
internal sealed partial class ReturnReadService
{
    internal static ReturnSyncState ReadReturnSyncState(SyncCursor cursor, DateTimeOffset now, TimeSpan overlap, bool forceFull = false)
    {
        // B-8: "İadeleri Çek" (forceFull) goes on in slices with the same payload, so each slice asked for the baseline
        // again and restarted it from page 0. A baseline saved by the slice before (three months back from its end, which
        // is less than a day old) is resumed; anything else starts a new one (as ReadHepsiburadaOrderSyncState does).
        if (forceFull)
            return SavedReturnSyncState(cursor) is { StartAt: { } start, EndAt: { } end } saved && start <= end.AddMonths(-3) && now - end < TimeSpan.FromDays(1)
                ? saved
                : InitialReturnWindow(now);
        if (!string.IsNullOrWhiteSpace(cursor.OpaqueCursor))
        {
            // The oldest cursors were a bare page number (a number is not a state object, so it is read first).
            if (int.TryParse(cursor.OpaqueCursor, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var oldPage))
                return InitialReturnWindow(now) with { Page = oldPage };
            if (SavedReturnSyncState(cursor) is { } state) return state;
        }
        var watermark = cursor.LastModifiedWatermark ?? cursor.LastSuccessAt;
        return watermark is null
            ? InitialReturnWindow(now)
            : new(ReturnSyncStateVersion, 0, 0, false, watermark.Value.Subtract(overlap), now);
    }

    private static ReturnSyncState? SavedReturnSyncState(SyncCursor cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor.OpaqueCursor)) return null;
        if (ResumableCursor.Read<ReturnSyncState>(cursor.OpaqueCursor, state => state is { Version: ReturnSyncStateVersion, StoreFrontIndex: >= 0, Page: >= 0 } && state.StoreFrontIndex < TrendyolReadStorefronts.ReturnCodes.Length) is not { } state) return null;
        return state.PageCursor is { } pageCursor && (string.IsNullOrWhiteSpace(pageCursor) || pageCursor.Length > MaximumReturnPageCursorLength)
            ? state with { PageCursor = null }
            : state;
    }

    internal static string SerializeReturnSyncState(ReturnSyncState state) => ResumableCursor.Serialize(state);
    private static ReturnSyncState InitialReturnWindow(DateTimeOffset now) => new(ReturnSyncStateVersion, 0, 0, false, now.AddMonths(-3), now);
}
