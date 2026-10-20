using MarketplaceHub.Domain;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class StockPricePublishingPolicyTests
{
    private static readonly Guid OrderConnection = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid OtherConnection = Guid.Parse("10000000-0000-0000-0000-000000000002");
    private static readonly Guid UnrelatedConnection = Guid.Parse("10000000-0000-0000-0000-000000000003");
    private static readonly Guid Variant = Guid.Parse("20000000-0000-0000-0000-000000000001");
    private static readonly Guid OtherVariant = Guid.Parse("20000000-0000-0000-0000-000000000002");

    // K1: a stock change reaches every connection selling the variant, at the latest projection version.
    [Fact]
    public void StockChange_FansOutToEveryConnectionWithAnActiveOffer()
    {
        var targets = StockProjectionOutboxPolicy.FanOut(
            [new StockProjectionChange(Variant, 4, "order:a"), new StockProjectionChange(Variant, 5, "order:b")],
            [(OrderConnection, Variant), (OtherConnection, Variant), (UnrelatedConnection, OtherVariant), (OtherConnection, Variant)]);

        Assert.Equal(2, targets.Count);
        Assert.Equal([OrderConnection, OtherConnection], targets.Select(target => target.ConnectionId));
        Assert.All(targets, target => Assert.Equal("order:b", target.SourceEventId));
        Assert.Equal(StockProjectionOutboxPolicy.DedupKey(OtherConnection, Variant, 5), targets[1].DedupKey);
    }

    [Fact]
    public void StockChange_WithoutActiveOffers_QueuesNothing()
    {
        Assert.Empty(StockProjectionOutboxPolicy.FanOut([new StockProjectionChange(Variant, 2, "manual:1")], [(OtherConnection, OtherVariant)]));
    }

    // K2: these outcomes finish the dispatch instead of blocking it (and stopping the reconcile scan).
    [Theory]
    [InlineData("NO_EXTERNAL_CHANGES", true)]
    [InlineData("LIVE_OFFER_REQUIRED", true)]
    [InlineData("CHANNEL_OFFER_INVALID", true)]
    [InlineData("HEPSIBURADA_LISTING_LINK_REQUIRED", false)]
    [InlineData("INVENTORY_REQUIRED", false)]
    public void StockDispatch_SettledBuildFailures(string code, bool settled)
    {
        Assert.Equal(settled, StockProjectionOutboxPolicy.IsSettledBuildFailure(code));
    }

    // K3: Trendyol's price request carries stock too, so an automatic send needs both policies.
    [Fact]
    public void AutomaticPriceInventorySend_RequiresPriceAndStockPolicies()
    {
        Assert.Equal([MarketplaceExternalWritePolicies.Price, MarketplaceExternalWritePolicies.Stock], PriceInventoryOutboxPolicy.RequiredWritePolicies(manual: false));
        Assert.Empty(PriceInventoryOutboxPolicy.RequiredWritePolicies(manual: true));
    }

    // K4: with the price policy off, a stock dispatch is held and reported instead of failing later.
    [Fact]
    public void StockDispatch_WithPricePolicyOff_IsHeldNotSent()
    {
        Assert.Equal(StockProjectionDispatchDecision.Skip, StockProjectionOutboxPolicy.Decide(stockWriteEnabled: false, priceWriteEnabled: true));
        Assert.Equal(StockProjectionDispatchDecision.HoldForPricePolicy, StockProjectionOutboxPolicy.Decide(stockWriteEnabled: true, priceWriteEnabled: false));
        Assert.Equal(StockProjectionDispatchDecision.Dispatch, StockProjectionOutboxPolicy.Decide(stockWriteEnabled: true, priceWriteEnabled: true));
    }

    // K6: the order shortfall is reserved only where the connection policy reserves the status.
    [Fact]
    public void OrderShortfall_IsReservedOnlyWhenThePolicyReservesTheStatus()
    {
        var policy = new ConnectionInventoryPolicy
        {
            AuthorityMode = InventoryAuthorityPolicy.Central,
            ReservationMode = "ORDER_LINE",
            ReserveOnStatuses = "Created",
            ReleaseOnStatuses = "Cancelled"
        };

        Assert.Equal(2m, OrderInventoryReservationPolicy.ReservedQuantity(2m, policy, "Created"));
        Assert.Equal(0m, OrderInventoryReservationPolicy.ReservedQuantity(2m, policy, "Cancelled"));
        Assert.Equal(0m, OrderInventoryReservationPolicy.ReservedQuantity(2m, null, "Created"));
        Assert.Equal(0m, OrderInventoryReservationPolicy.ReservedQuantity(0m, policy, "Created"));
        var at = DateTimeOffset.Parse("2026-10-20T10:00:00Z");
        Assert.Equal(at.AddDays(14), OrderInventoryReservationPolicy.ExpiresAt(at));
    }
}
