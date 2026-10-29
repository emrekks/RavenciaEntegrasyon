using System.Text.Json;
using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// Audit 29 Oct 2026, S-5: Hepsiburada writes the unpackedDate of an unpack event in Istanbul time without an offset; it was
// read as UTC (three hours late), and a value with an offset kept it, which a timestamptz write refuses.
public sealed class HepsiburadaUnpackEventTimeTests
{
    [Theory]
    [InlineData("""{"packageNumber":"P1","unpackedDate":"2026-10-29T14:00:00"}""", "2026-10-29T11:00:00Z")]
    [InlineData("""{"packageNumber":"P1","unpackedDate":"2026-10-29T14:00:00+03:00"}""", "2026-10-29T11:00:00Z")]
    [InlineData("""{"packageNumber":"P1","unpackedDate":"2026-10-29T11:00:00Z"}""", "2026-10-29T11:00:00Z")]
    public void The_unpack_time_is_Istanbul_time_stored_in_UTC(string body, string expected)
    {
        using var document = JsonDocument.Parse(body);

        var unpackedAt = MarketplaceJobProcessor.HepsiburadaUnpackedAt(document.RootElement);

        Assert.Equal(DateTimeOffset.Parse(expected), unpackedAt);
        Assert.Equal(TimeSpan.Zero, unpackedAt!.Value.Offset);
    }

    [Fact]
    public void An_unpack_event_without_a_readable_time_has_none()
    {
        using var document = JsonDocument.Parse("""{"packageNumber":"P1","unpackedDate":"yarın"}""");

        Assert.Null(MarketplaceJobProcessor.HepsiburadaUnpackedAt(document.RootElement));
    }
}
