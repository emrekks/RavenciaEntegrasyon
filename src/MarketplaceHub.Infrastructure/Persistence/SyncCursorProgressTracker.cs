using MarketplaceHub.Domain;

namespace MarketplaceHub.Infrastructure.Persistence;

public static class SyncCursorProgressTracker
{
    public static void TrackModification(
        SyncCursor cursor,
        string? previousOpaqueCursor,
        DateTimeOffset? previousLastModifiedWatermark,
        DateTimeOffset? previousLastSuccessAt,
        DateTimeOffset now)
    {
        var cursorAdvanced = !string.IsNullOrWhiteSpace(cursor.OpaqueCursor)
            && !string.Equals(previousOpaqueCursor, cursor.OpaqueCursor, StringComparison.Ordinal);
        var watermarkAdvanced = cursor.LastModifiedWatermark is { } currentWatermark
            && (previousLastModifiedWatermark is null || currentWatermark > previousLastModifiedWatermark.Value);
        var successfulRunAdvanced = cursor.LastSuccessAt is { } currentSuccessAt
            && (previousLastSuccessAt is null || currentSuccessAt > previousLastSuccessAt.Value);

        if (cursorAdvanced || watermarkAdvanced)
        {
            cursor.LastCursorAdvancedAt = now;
            cursor.CursorStagnantSince = null;
            return;
        }

        if (!successfulRunAdvanced) return;

        // A sync can persist its cursor before RecordSyncCompletion persists
        // LastSuccessAt. Do not mark that same successful run stagnant on the
        // second save when it already advanced the cursor after its attempt began.
        var cursorAdvancedDuringRun = cursor.LastAttemptAt is { } attemptAt
            && cursor.LastCursorAdvancedAt is { } advancedAt
            && advancedAt >= attemptAt;
        if (cursorAdvancedDuringRun)
        {
            cursor.CursorStagnantSince = null;
            return;
        }

        if (string.IsNullOrWhiteSpace(cursor.OpaqueCursor) && cursor.LastModifiedWatermark is null) return;
        if (cursor.LastReceivedCount > 0)
            cursor.CursorStagnantSince ??= now;
        else
            cursor.CursorStagnantSince = null;
    }
}
