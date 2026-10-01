using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class HepsiburadaInvoiceReconciliationBatchPolicyTests
{
    [Fact]
    public void SelectContinuesAfterCursorAndWrapsOnlyToFillTheBatch()
    {
        var updatedAt = DateTimeOffset.Parse("2026-09-30T10:00:00Z");
        var orderedAt = DateTimeOffset.Parse("2026-09-30T09:00:00Z");
        var first = new HepsiburadaInvoiceOrderCandidate(Guid.Parse("00000000-0000-0000-0000-000000000001"), "HB-1", updatedAt, orderedAt);
        var second = new HepsiburadaInvoiceOrderCandidate(Guid.Parse("00000000-0000-0000-0000-000000000002"), "HB-2", updatedAt, orderedAt);
        var third = new HepsiburadaInvoiceOrderCandidate(Guid.Parse("00000000-0000-0000-0000-000000000003"), "HB-3", updatedAt, orderedAt);
        var fourth = new HepsiburadaInvoiceOrderCandidate(Guid.Parse("00000000-0000-0000-0000-000000000004"), "HB-4", updatedAt, orderedAt);

        var selected = HepsiburadaInvoiceReconciliationBatchPolicy.Select([third, fourth], [first, second], 3);

        Assert.Equal([third, fourth, first], selected);
    }

    [Fact]
    public void SelectDoesNotRepeatTheWrappedWindowWhenTheForwardWindowIsFull()
    {
        var updatedAt = DateTimeOffset.Parse("2026-09-30T10:00:00Z");
        var orderedAt = DateTimeOffset.Parse("2026-09-30T09:00:00Z");
        var first = new HepsiburadaInvoiceOrderCandidate(Guid.Parse("00000000-0000-0000-0000-000000000001"), "HB-1", updatedAt, orderedAt);
        var second = new HepsiburadaInvoiceOrderCandidate(Guid.Parse("00000000-0000-0000-0000-000000000002"), "HB-2", updatedAt, orderedAt);
        var third = new HepsiburadaInvoiceOrderCandidate(Guid.Parse("00000000-0000-0000-0000-000000000003"), "HB-3", updatedAt, orderedAt);

        var selected = HepsiburadaInvoiceReconciliationBatchPolicy.Select([second, third], [first], 2);

        Assert.Equal([second, third], selected);
    }

    [Fact]
    public void ReadCursorReturnsNullForLegacyOrInvalidCursorValues()
    {
        Assert.Null(HepsiburadaInvoiceReconciliationBatchPolicy.ReadCursor(null));
        Assert.Null(HepsiburadaInvoiceReconciliationBatchPolicy.ReadCursor("not-json"));
    }
}
