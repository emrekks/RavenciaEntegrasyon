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

    // 26 Oct 2026: how Trendyol settled a disputed line is read; a field Trendyol leaves out stays unknown.
    [Fact]
    public void Trendyol_claim_lines_carry_how_the_line_was_settled()
    {
        const string json = """
        {"page":0,"totalPages":1,"content":[{"id":"c1","claimId":"c1","orderNumber":"o1","lastModifiedDate":1760000000000,
          "items":[{"orderLine":{"id":101},"claimItems":[{"id":"i1","claimItemStatus":{"name":"Accepted"},"acceptDetail":"DISPUTE","note":" Müşteri haklı bulundu ","resolved":true,"acceptedBySeller":false}]},
                   {"orderLine":{"id":102},"claimItems":[{"id":"i2","claimItemStatus":{"name":"Unresolved"},"note":null}]}]}]}
        """;
        var lines = Assert.Single(TrendyolJsonMapper.Returns(json).Items).Lines.OrderBy(x => x.ExternalLineId).ToList();
        Assert.Equal(("DISPUTE", "Müşteri haklı bulundu", true, false), (lines[0].AcceptDetail, lines[0].Note, lines[0].Resolved, lines[0].AcceptedBySeller));
        Assert.Equal((null, null, null, null), (lines[1].AcceptDetail, lines[1].Note, lines[1].Resolved, lines[1].AcceptedBySeller));
    }

    [Fact]
    public void Trendyol_claim_item_audits_read_oldest_first_and_skip_entries_without_a_status_or_date()
    {
        const string json = """
        [{"claimId":"c1","claimItemId":"i1","previousStatus":"WaitingInAction","newStatus":"Unresolved","userInfoDocument":{"executorId":"9","executorApp":"SellerCenter","executorUser":"123"},"date":1761500000000},
         {"claimId":"c1","claimItemId":"i1","previousStatus":null,"newStatus":"Created","userInfoDocument":{"executorApp":"Customer"},"date":1761400000000},
         {"claimId":"c1","claimItemId":"i1","newStatus":"","date":1761600000000},
         {"claimId":"c1","claimItemId":"i1","newStatus":"Accepted"}]
        """;
        var audits = TrendyolJsonMapper.ReturnLineAudits(json);
        Assert.Equal(new[] { "Created", "Unresolved" }, audits.Select(x => x.NewStatus));
        Assert.Equal((null, "Customer", null), (audits[0].PreviousStatus, audits[0].ExecutorApp, audits[0].ExecutorUser));
        Assert.Equal(("WaitingInAction", "SellerCenter", "123"), (audits[1].PreviousStatus, audits[1].ExecutorApp, audits[1].ExecutorUser));
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1761500000000), audits[1].At);
    }
}
