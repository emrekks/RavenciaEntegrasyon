using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Adapters.N11;
using MarketplaceHub.Infrastructure.Adapters.Trendyol.Mapping;
using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// The Returns page's Reddedilen card (rejected, package back to the customer not with the cargo yet) and its
// Bugün Onaylanan / Reddedilen card (decisions the marketplace dates today, Istanbul).
public sealed class ReturnMetricCardTests
{
    private static string TrendyolClaim(string status, string? rejectedPackage) =>
        """{"page":0,"totalPages":1,"content":[{"id":"c1","claimId":"c1","orderNumber":"o1","lastModifiedDate":1760000000000,"cargoTrackingLink":"https://tracking.trendyol.com/?id=inbound","""
        + (rejectedPackage is null ? "" : "\"rejectedPackageInfo\":" + rejectedPackage + ",")
        + "\"items\":[{\"orderLine\":{\"id\":101},\"claimItems\":[{\"id\":\"i1\",\"claimItemStatus\":{\"name\":\"" + status + "\"}}]}]}]}";

    [Theory]
    [InlineData("Rejected", null, ReturnRejectedPackageStates.None)]
    [InlineData("Rejected", """{"packageId":92248304,"cargoTrackingNumber":2207284754}""", ReturnRejectedPackageStates.AwaitingCargo)]
    [InlineData("Rejected", """{"packageId":92248304,"cargoTrackingLink":""}""", ReturnRejectedPackageStates.AwaitingCargo)]
    [InlineData("Rejected", """{"packageId":92248304,"cargoTrackingLink":"https://tracking.trendyol.com/?trackingId=x"}""", ReturnRejectedPackageStates.Shipped)]
    [InlineData("Rejected", """{"packageId":92248304,"dontShipBack":true}""", ReturnRejectedPackageStates.DontShipBack)]
    [InlineData("Rejected", "\"odd\"", null)]
    [InlineData("Accepted", """{"packageId":92248304}""", null)]
    public void TrendyolsRejectedPackageSaysWhetherTheReturnWentBackToTheCargo(string status, string? rejectedPackage, string? expected)
    {
        var claim = Assert.Single(TrendyolJsonMapper.Returns(TrendyolClaim(status, rejectedPackage)).Items);
        Assert.Equal(expected, claim.RejectedPackageState);
        Assert.Null(claim.DecidedAt);
    }

    [Fact]
    public void ANewOutcomeIsDatedAgainAndALeftPackageDoesNotGoBackToWaiting()
    {
        var earlier = DateTimeOffset.Parse("2026-11-01T08:00:00Z");
        var today = DateTimeOffset.Parse("2026-11-02T10:00:00+03:00");
        // Same outcome: the stored date stays; a date the read brings replaces it (in UTC).
        Assert.Equal((null, earlier), MarketplaceJobProcessor.NextReturnOutcomeFacts(null, earlier, ReturnClaimStatus.Approved, ReturnClaimStatus.Approved, null, null));
        var (_, dated) = MarketplaceJobProcessor.NextReturnOutcomeFacts(null, earlier, ReturnClaimStatus.Approved, ReturnClaimStatus.Approved, null, today);
        Assert.Equal(TimeSpan.Zero, dated!.Value.Offset);
        Assert.Equal(today, dated);
        // A new outcome drops the old date; a claim that is neither approved nor rejected has none.
        Assert.Equal((ReturnRejectedPackageStates.AwaitingCargo, (DateTimeOffset?)null), MarketplaceJobProcessor.NextReturnOutcomeFacts(null, earlier, ReturnClaimStatus.Approved, ReturnClaimStatus.Rejected, ReturnRejectedPackageStates.AwaitingCargo, null));
        Assert.Equal((null, (DateTimeOffset?)null), MarketplaceJobProcessor.NextReturnOutcomeFacts(null, earlier, ReturnClaimStatus.Approved, ReturnClaimStatus.Completed, null, today));
        // A shipped package stays shipped on an older read; a read without the state keeps what is known.
        Assert.Equal(ReturnRejectedPackageStates.Shipped, MarketplaceJobProcessor.NextReturnOutcomeFacts(ReturnRejectedPackageStates.Shipped, null, ReturnClaimStatus.Rejected, ReturnClaimStatus.Rejected, ReturnRejectedPackageStates.AwaitingCargo, null).RejectedPackageState);
        Assert.Equal(ReturnRejectedPackageStates.AwaitingCargo, MarketplaceJobProcessor.NextReturnOutcomeFacts(ReturnRejectedPackageStates.AwaitingCargo, null, ReturnClaimStatus.Rejected, ReturnClaimStatus.Rejected, null, null).RejectedPackageState);
        Assert.Equal(ReturnRejectedPackageStates.Shipped, MarketplaceJobProcessor.NextReturnOutcomeFacts(ReturnRejectedPackageStates.AwaitingCargo, null, ReturnClaimStatus.Rejected, ReturnClaimStatus.Rejected, ReturnRejectedPackageStates.Shipped, null).RejectedPackageState);
    }

    [Fact]
    public void ATrendyolClaimIsDatedByTheAuditThatMovedItToItsOutcome()
    {
        var entries = new[]
        {
            new RemoteReturnLineAudit(null, "Created", null, null, DateTimeOffset.Parse("2026-10-30T08:00:00Z")),
            new RemoteReturnLineAudit("Created", "WaitingInAction", null, null, DateTimeOffset.Parse("2026-11-01T08:00:00Z")),
            new RemoteReturnLineAudit("WaitingInAction", "Accepted", "SellerCenter", null, DateTimeOffset.Parse("2026-11-02T09:15:00+03:00")),
            new RemoteReturnLineAudit("WaitingInAction", "Accepted", "System", null, DateTimeOffset.Parse("2026-11-02T07:00:00Z"))
        };
        Assert.Equal(DateTimeOffset.Parse("2026-11-02T07:00:00Z"), MarketplaceJobProcessor.TrendyolDecisionTime(entries, ReturnClaimStatus.Approved));
        Assert.Null(MarketplaceJobProcessor.TrendyolDecisionTime(entries, ReturnClaimStatus.Rejected));
    }

    [Fact]
    public void HepsiburadaAndN11DateTheDecisionThemselves()
    {
        var approved = N11ReturnMapper.Claim(System.Xml.Linq.XElement.Parse("<claim><claimReturnId>9</claimReturnId><orderNumber>205</orderNumber><status>APPROVED</status><requestDate>2026-10-30</requestDate><approvedDate>2026-11-02</approvedDate></claim>"))!;
        Assert.Equal(DateTimeOffset.Parse("2026-11-01T21:00:00Z"), approved.DecidedAt);
        var waiting = N11ReturnMapper.Claim(System.Xml.Linq.XElement.Parse("<claim><claimReturnId>9</claimReturnId><orderNumber>205</orderNumber><status>PENDING</status><requestDate>2026-10-30</requestDate><approvedDate>2026-11-02</approvedDate></claim>"))!;
        Assert.Null(waiting.DecidedAt);
    }

    [Fact]
    public void TodayStartsAtMidnightIstanbul()
    {
        Assert.Equal(DateTimeOffset.Parse("2026-11-01T21:00:00Z"), MarketplaceSalesService.IstanbulDayStart(DateTimeOffset.Parse("2026-11-02T08:00:00Z")));
        // 22:30 UTC is already the next day in Istanbul.
        Assert.Equal(DateTimeOffset.Parse("2026-11-02T21:00:00Z"), MarketplaceSalesService.IstanbulDayStart(DateTimeOffset.Parse("2026-11-02T22:30:00Z")));
    }

    [Theory]
    [InlineData(ReturnClaimStatus.Rejected, ReturnRejectedPackageStates.AwaitingCargo, true, false)]
    [InlineData(ReturnClaimStatus.Rejected, ReturnRejectedPackageStates.Shipped, false, false)]
    [InlineData(ReturnClaimStatus.Rejected, ReturnRejectedPackageStates.None, false, false)]
    [InlineData(ReturnClaimStatus.Rejected, null, false, true)]
    [InlineData(ReturnClaimStatus.Cancelled, null, false, false)]
    public void OnlyARejectedReturnWhosePackageHasNotLeftWaitsForCargo(ReturnClaimStatus status, string? packageState, bool awaiting, bool unknown) =>
        Assert.Equal((awaiting, unknown), MarketplaceSalesService.RejectedCargoState(status, packageState));
}
