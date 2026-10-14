using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Adapters.Trendyol.Mapping;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class ReturnLineStatusTests
{
    [Fact]
    public void A_claim_with_one_accepted_and_one_waiting_line_stays_actionable()
    {
        var status = ReturnLineStatusPolicy.Resolve(ReturnClaimStatus.Approved, ["Accepted", "WaitingInAction"]);
        Assert.Equal(ReturnClaimStatus.ActionRequired, status);
    }

    [Fact]
    public void The_claim_level_status_stands_when_no_line_waits_or_a_line_status_is_unknown()
    {
        Assert.Equal(ReturnClaimStatus.Approved, ReturnLineStatusPolicy.Resolve(ReturnClaimStatus.Approved, ["Accepted", "Accepted"]));
        Assert.Equal(ReturnClaimStatus.Approved, ReturnLineStatusPolicy.Resolve(ReturnClaimStatus.Approved, ["Accepted", null]));
        Assert.Equal(ReturnClaimStatus.Requested, ReturnLineStatusPolicy.Resolve(ReturnClaimStatus.Requested, []));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("WaitingInAction", true)]
    [InlineData("Accepted", false)]
    [InlineData("Rejected", false)]
    public void Only_lines_waiting_for_the_seller_are_actionable(string? raw, bool expected) =>
        Assert.Equal(expected, ReturnLineStatusPolicy.IsActionable(raw));

    [Fact]
    public void Trendyol_claim_lines_carry_their_own_item_status()
    {
        const string json = """
        {"page":0,"totalPages":1,"content":[{"id":"c1","claimId":"c1","orderNumber":"o1","lastModifiedDate":1760000000000,
          "items":[{"orderLine":{"id":101},"claimItems":[{"id":"i1","claimItemStatus":{"name":"Accepted"}}]},
                   {"orderLine":{"id":102},"claimItems":[{"id":"i2","claimItemStatus":{"name":"WaitingInAction"}}]}]}]}
        """;
        var claim = Assert.Single(TrendyolJsonMapper.Returns(json).Items);
        Assert.Equal("Accepted,WaitingInAction", string.Join(",", claim.Lines.OrderBy(x => x.ExternalLineId).Select(x => x.RawStatus)));
    }
}
