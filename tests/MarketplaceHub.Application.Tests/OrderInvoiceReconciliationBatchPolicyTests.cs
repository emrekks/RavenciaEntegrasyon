using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class OrderInvoiceReconciliationBatchPolicyTests
{
    [Fact]
    public void ReadAndWriteCursorRoundTripOrderId()
    {
        var orderId = Guid.Parse("0199a5a1-1c00-7000-8000-000000000001");

        Assert.Equal(orderId, OrderInvoiceReconciliationBatchPolicy.ReadCursor(OrderInvoiceReconciliationBatchPolicy.WriteCursor(orderId)));
        Assert.Null(OrderInvoiceReconciliationBatchPolicy.ReadCursor("invalid"));
    }

    [Fact]
    public void SelectContinuesAfterCursorAndWrapsOnlyToFillTheBatch()
    {
        var first = Candidate(1);
        var second = Candidate(2);
        var third = Candidate(3);
        var fourth = Candidate(4);

        var selected = OrderInvoiceReconciliationBatchPolicy.Select([third, fourth], [first, second], 3);

        Assert.Equal([third, fourth, first], selected);
    }

    [Fact]
    public void SelectDoesNotRepeatWrappedCandidatesWhenForwardPageIsFull()
    {
        var first = Candidate(1);
        var second = Candidate(2);
        var third = Candidate(3);

        var selected = OrderInvoiceReconciliationBatchPolicy.Select([second, third], [first], 2);

        Assert.Equal([second, third], selected);
    }

    [Fact]
    public void SelectKeepsPartialAndRegularOrdersInCursorOrder()
    {
        var partial = Candidate(1);
        var regular = Candidate(2);
        var next = Candidate(3);

        var selected = OrderInvoiceReconciliationBatchPolicy.Select([partial, regular], [next], 3);

        Assert.Equal([partial, regular, next], selected);
    }

    [Fact]
    public void TrendyolSweepContinuesFromItsPositionAndOtherwiseRunsEverySixHours()
    {
        var now = new DateTimeOffset(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);

        Assert.True(TrendyolInvoiceReconciliationPolicy.SweepDue(null, null, now));
        Assert.True(TrendyolInvoiceReconciliationPolicy.SweepDue("\"0199a5a1-1c00-7000-8000-000000000001\"", now.AddMinutes(-5), now));
        Assert.False(TrendyolInvoiceReconciliationPolicy.SweepDue(null, now.AddHours(-5), now));
        Assert.True(TrendyolInvoiceReconciliationPolicy.SweepDue(null, now.AddHours(-6), now));
    }

    private static OrderInvoiceReconciliationCandidate Candidate(int id) =>
        new(Guid.Parse($"0199a5a1-1c00-7000-8000-{id:D12}"), $"ORDER-{id}");
}
