using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Adapters.Trendyol;

namespace MarketplaceHub.Infrastructure.Persistence;

// The Trendyol return read's resumable state (ISLEYICI-9 stage 3c: it stays with the read; the claim ingestion moved to
// ReturnIngestionService).
internal sealed partial class ReturnReadService
{
    internal static ReturnSyncState ReadReturnSyncState(SyncCursor cursor, DateTimeOffset now, TimeSpan overlap, bool forceFull = false)
    {
        if (forceFull) return InitialReturnWindow(now);
        if (!string.IsNullOrWhiteSpace(cursor.OpaqueCursor))
        {
            // The oldest cursors were a bare page number (a number is not a state object, so it is read first).
            if (int.TryParse(cursor.OpaqueCursor, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var oldPage))
                return InitialReturnWindow(now) with { Page = oldPage };
            if (ResumableCursor.Read<ReturnSyncState>(cursor.OpaqueCursor, state => state is { Version: ReturnSyncStateVersion, StoreFrontIndex: >= 0, Page: >= 0 } && state.StoreFrontIndex < TrendyolReadStorefronts.ReturnCodes.Length) is { } state)
                return state.PageCursor is { } pageCursor && (string.IsNullOrWhiteSpace(pageCursor) || pageCursor.Length > MaximumReturnPageCursorLength)
                    ? state with { PageCursor = null }
                    : state;
        }
        var watermark = cursor.LastModifiedWatermark ?? cursor.LastSuccessAt;
        return watermark is null
            ? InitialReturnWindow(now)
            : new(ReturnSyncStateVersion, 0, 0, false, watermark.Value.Subtract(overlap), now);
    }

    internal static string SerializeReturnSyncState(ReturnSyncState state) => ResumableCursor.Serialize(state);
    private static ReturnSyncState InitialReturnWindow(DateTimeOffset now) => new(ReturnSyncStateVersion, 0, 0, false, now.AddMonths(-3), now);
}
