using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class JobListCursorTests
{
    [Fact]
    public void Cursor_RoundTripsCreatedAtAndId()
    {
        var createdAt = new DateTimeOffset(2026, 10, 20, 9, 30, 15, TimeSpan.FromHours(3)).AddTicks(1230);
        var id = Guid.CreateVersion7();

        var cursor = JobListCursor.Encode(createdAt, id);

        Assert.DoesNotContain('=', cursor);
        Assert.DoesNotContain('+', cursor);
        Assert.DoesNotContain('/', cursor);
        Assert.True(JobListCursor.TryDecode(cursor, out var decodedAt, out var decodedId));
        Assert.Equal(createdAt.UtcTicks, decodedAt.UtcTicks);
        Assert.Equal(TimeSpan.Zero, decodedAt.Offset);
        Assert.Equal(id, decodedId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-cursor")]
    [InlineData("SjF8MTIzfG5vdC1hLWd1aWQ")] // J1|123|not-a-guid
    [InlineData("WDF8MTIzfDAxOTlmMDAwLTAwMDAtNzAwMC04MDAwLTAwMDAwMDAwMDAwMA")] // X1|... (unknown version)
    public void Cursor_RejectsAnythingItDidNotWrite(string? cursor)
    {
        Assert.False(JobListCursor.TryDecode(cursor, out _, out _));
    }

    [Fact]
    public void Cursor_RejectsAnOverlongValue()
    {
        Assert.False(JobListCursor.TryDecode(new string('A', 4_000), out _, out _));
    }
}
