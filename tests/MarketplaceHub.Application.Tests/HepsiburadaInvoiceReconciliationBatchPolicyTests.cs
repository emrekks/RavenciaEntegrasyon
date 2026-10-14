using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class HepsiburadaInvoiceReconciliationBatchPolicyTests
{
    [Fact]
    public void MissingInvoiceFeedOnlyInfersUploadedForRecentOrdersWhenTheWholeFeedWasRead()
    {
        var now = DateTimeOffset.Parse("2026-10-06T12:00:00Z");
        var missing = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            HepsiburadaMissingInvoiceStatusPolicy.Key("HB-1", "PKG-1")
        };

        Assert.Equal("NOT_INVOICED", HepsiburadaMissingInvoiceStatusPolicy.Resolve("HB-1", "PKG-1", now.AddDays(-100), now, missing, true));
        Assert.Equal("INVOICED", HepsiburadaMissingInvoiceStatusPolicy.Resolve("HB-2", "PKG-2", now.AddDays(-29), now, missing, true));
        Assert.Null(HepsiburadaMissingInvoiceStatusPolicy.Resolve("HB-3", "PKG-3", now.AddDays(-31), now, missing, true));
        Assert.Null(HepsiburadaMissingInvoiceStatusPolicy.Resolve("HB-2", "PKG-2", now.AddDays(-1), now, missing, false));
    }

    [Fact]
    public void SelectContinuesAfterCursorAndWrapsOnlyToFillTheBatch()
    {
        var first = new HepsiburadaInvoiceOrderCandidate(Guid.Parse("00000000-0000-0000-0000-000000000001"), "HB-1");
        var second = new HepsiburadaInvoiceOrderCandidate(Guid.Parse("00000000-0000-0000-0000-000000000002"), "HB-2");
        var third = new HepsiburadaInvoiceOrderCandidate(Guid.Parse("00000000-0000-0000-0000-000000000003"), "HB-3");
        var fourth = new HepsiburadaInvoiceOrderCandidate(Guid.Parse("00000000-0000-0000-0000-000000000004"), "HB-4");

        var selected = HepsiburadaInvoiceReconciliationBatchPolicy.Select([third, fourth], [first, second], 3);

        Assert.Equal([third, fourth, first], selected);
    }

    [Fact]
    public void SelectDoesNotRepeatTheWrappedWindowWhenTheForwardWindowIsFull()
    {
        var first = new HepsiburadaInvoiceOrderCandidate(Guid.Parse("00000000-0000-0000-0000-000000000001"), "HB-1");
        var second = new HepsiburadaInvoiceOrderCandidate(Guid.Parse("00000000-0000-0000-0000-000000000002"), "HB-2");
        var third = new HepsiburadaInvoiceOrderCandidate(Guid.Parse("00000000-0000-0000-0000-000000000003"), "HB-3");

        var selected = HepsiburadaInvoiceReconciliationBatchPolicy.Select([second, third], [first], 2);

        Assert.Equal([second, third], selected);
    }

    [Fact]
    public void ReadCursorReturnsNullForLegacyOrInvalidCursorValues()
    {
        Assert.Null(HepsiburadaInvoiceReconciliationBatchPolicy.ReadCursor(null));
        Assert.Null(HepsiburadaInvoiceReconciliationBatchPolicy.ReadCursor("not-json"));
    }

    [Fact]
    public void CursorRoundTripsUsingStableOrderIdentityAndReadsPreviousCursorShape()
    {
        var orderId = Guid.Parse("00000000-0000-0000-0000-000000000042");
        var candidate = new HepsiburadaInvoiceOrderCandidate(orderId, "HB-42");
        var previousCursor = "{\"OrderId\":\"00000000-0000-0000-0000-000000000042\",\"ExternalOrderId\":\"HB-42\",\"UpdatedAt\":\"2026-09-30T10:00:00Z\",\"OrderedAt\":\"2026-09-30T09:00:00Z\"}";

        Assert.Equal(candidate, HepsiburadaInvoiceReconciliationBatchPolicy.ReadCursor(HepsiburadaInvoiceReconciliationBatchPolicy.WriteCursor(candidate)));
        Assert.Equal(candidate, HepsiburadaInvoiceReconciliationBatchPolicy.ReadCursor(previousCursor));
    }
}
