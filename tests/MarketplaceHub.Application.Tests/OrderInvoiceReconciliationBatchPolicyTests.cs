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
    public void SelectReturnClaimHydrationPrioritizesOldPartialOrdersAndSkipsNotFoundOrders()
    {
        var partial = Candidate(1);
        var regular = Candidate(2);
        var unreachable = Candidate(3);
        var candidates = new (OrderInvoiceReconciliationCandidate Candidate, string? CustomerSnapshotJson)[]
        {
            (regular, """{"customerFirstName":"Ada"}"""),
            (unreachable, """{"claimId":"claim-3","claimDate":"2026-07-03T09:12:00Z","orderShipmentPackageId":"pkg-3"}"""),
            (partial, """{"claimId":"claim-1","claimDate":"2026-07-03T09:12:00Z","orderOutboundPackageId":"pkg-1"}""")
        };

        var selected = OrderInvoiceReconciliationBatchPolicy.SelectReturnClaimHydration(
            candidates,
            new HashSet<Guid> { unreachable.OrderId },
            10);

        Assert.Equal([partial], selected);
    }

    private static OrderInvoiceReconciliationCandidate Candidate(int id) =>
        new(Guid.Parse($"0199a5a1-1c00-7000-8000-{id:D12}"), $"ORDER-{id}");
}
