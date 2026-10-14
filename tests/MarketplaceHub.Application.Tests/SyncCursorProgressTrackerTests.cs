using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class SyncCursorProgressTrackerTests
{
    [Fact]
    public void SuccessfulRunDoesNotBecomeStagnantWhenItsCursorAdvancedBeforeCompletionSave()
    {
        var start = new DateTimeOffset(2026, 10, 7, 19, 0, 0, TimeSpan.Zero);
        var cursor = new SyncCursor
        {
            ResourceType = "ORDERS_HOT",
            OpaqueCursor = "page-1",
            LastModifiedWatermark = start,
            LastCursorAdvancedAt = start,
            LastSuccessAt = start,
            LastAttemptAt = start.AddMinutes(1),
            LastReceivedCount = 25
        };

        // The order sync persists its advanced watermark before telemetry completion.
        var previousWatermark = cursor.LastModifiedWatermark;
        var previousSuccess = cursor.LastSuccessAt;
        cursor.LastModifiedWatermark = start.AddMinutes(2);
        SyncCursorProgressTracker.TrackModification(cursor, cursor.OpaqueCursor, previousWatermark, previousSuccess, start.AddMinutes(2));
        Assert.Null(cursor.CursorStagnantSince);
        Assert.Equal(start.AddMinutes(2), cursor.LastCursorAdvancedAt);

        // RecordSyncCompletion saves LastSuccessAt separately from the cursor movement.
        previousWatermark = cursor.LastModifiedWatermark;
        previousSuccess = cursor.LastSuccessAt;
        cursor.LastSuccessAt = start.AddMinutes(3);
        SyncCursorProgressTracker.TrackModification(cursor, cursor.OpaqueCursor, previousWatermark, previousSuccess, start.AddMinutes(3));
        Assert.Null(cursor.CursorStagnantSince);

        // A later successful run with received records and no cursor movement is still reported.
        cursor.LastAttemptAt = start.AddMinutes(4);
        previousWatermark = cursor.LastModifiedWatermark;
        previousSuccess = cursor.LastSuccessAt;
        cursor.LastSuccessAt = start.AddMinutes(5);
        SyncCursorProgressTracker.TrackModification(cursor, cursor.OpaqueCursor, previousWatermark, previousSuccess, start.AddMinutes(5));
        Assert.Equal(start.AddMinutes(5), cursor.CursorStagnantSince);
    }
}
