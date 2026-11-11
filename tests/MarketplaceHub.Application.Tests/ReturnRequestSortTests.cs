using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Adapters.Hepsiburada;
using MarketplaceHub.Infrastructure.Adapters.Trendyol.Mapping;
using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// 11 Nov 2026: the Returns page sorts by the marketplace's return request date by default (it used to list claims
// in the order the panel first stored them); a claim the marketplace has not dated goes last in either direction.
public sealed class ReturnRequestSortTests
{
    private static readonly Guid First = Guid.Parse("01900000-0000-7000-8000-000000000001");
    private static readonly Guid Second = Guid.Parse("01900000-0000-7000-8000-000000000002");
    private static readonly Guid Third = Guid.Parse("01900000-0000-7000-8000-000000000003");

    [Theory]
    [InlineData(null, "REQUESTED_DESC")]
    [InlineData("", "REQUESTED_DESC")]
    [InlineData("whatever", "REQUESTED_DESC")]
    [InlineData("requested_asc", "REQUESTED_ASC")]
    [InlineData("REQUESTED_DESC", "REQUESTED_DESC")]
    [InlineData("ORDERED_DESC", "ORDERED_DESC")]
    [InlineData("APPROVED_ASC", "APPROVED_ASC")]
    [InlineData("ID", "ID")]
    public void The_default_sort_is_the_newest_return_request(string? sort, string expected) =>
        Assert.Equal(expected, MarketplaceSalesService.NormalizeReturnSort(sort));

    [Fact]
    public void Request_dates_sort_both_ways_with_undated_claims_last()
    {
        var oct14 = new DateTimeOffset(2026, 10, 14, 9, 0, 0, TimeSpan.Zero);
        var oct20 = oct14.AddDays(6);
        var oct24 = oct14.AddDays(10);
        // In claim id order the dates read 14.10, 24.10, 20.10 (the order the owner saw).
        var items = new (DateTimeOffset? Date, Guid ClaimId, int Kind)[] { (oct14, First, 0), (null, Second, 0), (oct24, Third, 0), (oct20, Second, 1) };

        List<(DateTimeOffset? Date, Guid ClaimId, int Kind)> Sorted(string sort) =>
            items.OrderBy(x => x, Comparer<(DateTimeOffset?, Guid, int)>.Create((left, right) => MarketplaceSalesService.CompareReturnCards(left, right, sort))).ToList();

        Assert.Equal([(oct24, Third, 0), (oct20, Second, 1), (oct14, First, 0), (null, Second, 0)], Sorted("REQUESTED_DESC"));
        Assert.Equal([(oct14, First, 0), (oct20, Second, 1), (oct24, Third, 0), (null, Second, 0)], Sorted("REQUESTED_ASC"));
    }

    [Fact]
    public void A_cursor_after_a_card_continues_with_the_next_one_in_request_order()
    {
        var day = new DateTimeOffset(2026, 10, 20, 0, 0, 0, TimeSpan.Zero);
        var ordered = new (DateTimeOffset? Date, Guid ClaimId, int Kind)[] { (day.AddDays(2), Third, 0), (day, First, 0), (day, Second, 0), (null, First, 0) };
        var cursor = (Date: (DateTimeOffset?)day, ClaimId: First, Kind: 0);

        var next = Array.FindIndex(ordered, item => MarketplaceSalesService.CompareReturnCards(item, cursor, "REQUESTED_DESC") > 0);

        Assert.Equal(2, next);
    }

    [Fact]
    public void A_trendyol_claim_is_dated_by_its_claim_date_only()
    {
        const string json = """
        {"page":0,"totalPages":1,"content":[
          {"id":"c1","claimId":"c1","orderNumber":"o1","claimDate":1760432400000,"lastModifiedDate":1760600000000,"items":[]},
          {"id":"c2","claimId":"c2","orderNumber":"o2","createdDate":1760432400000,"lastModifiedDate":1760600000000,"items":[]}]}
        """;
        var claims = TrendyolJsonMapper.Returns(json).Items;

        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1760432400000), claims[0].RequestedAt);
        Assert.Null(claims[1].RequestedAt);
    }

    [Fact]
    public void A_hepsiburada_claim_date_is_read_as_istanbul_time()
    {
        const string claim = """
        {
          "claimNumber": "356189794", "type": "Return", "quantity": 1, "status": "AwaitingAction",
          "orderNumber": "2152988946", "claimDate": "2026-11-04T15:48:55", "orderDate": "2026-11-01T10:00:00",
          "line": { "lineItemId": "61680dc6", "productName": "Gömlek", "merchantSku": "K20", "price": { "currency": "TRY", "amount": 56.0 } }
        }
        """;
        using var document = JsonDocument.Parse(claim);

        Assert.Equal(new DateTimeOffset(2026, 11, 4, 12, 48, 55, TimeSpan.Zero), HepsiburadaJsonMapper.ReturnClaim(document.RootElement).RequestedAt);
    }

    [Fact]
    public void A_new_claim_keeps_the_request_date_in_utc()
    {
        var requested = new DateTimeOffset(2026, 11, 4, 15, 48, 55, TimeSpan.FromHours(3));
        var remote = new RemoteReturnClaim("c1", "o1", "WaitingInAction", null, null, null, null, [], "{}", RequestedAt: requested);

        var claim = MarketplaceJobProcessor.NewReturnClaim(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), remote, ReturnClaimStatus.ActionRequired, DateTimeOffset.UnixEpoch);

        Assert.Equal(requested, claim.RequestedAt);
        Assert.Equal(TimeSpan.Zero, claim.RequestedAt!.Value.Offset);
    }
}
