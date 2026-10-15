using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Adapters.Pazarama;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class PazaramaReturnTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private const string Refund = """
        {"id":1,"refundId":"ef2affaf-fdfc-4128-b5bc-3a4c1129e662","orderNumber":818589784,"orderDate":"6 Ekim 2021 14:53","refundNumber":564213,
         "refundType":"Ürünün parçası eksik","refundStatus":1,"refundStatusName":"İade Onayı Bekliyor","refundDate":"6 Ekim 2021 19:46",
         "totalAmount":{"value":23.5,"currency":"TL"},"refundAmount":{"value":1.75,"currency":"TL"},"customerName":"Dilara Demiral",
         "productName":"Böğürtlen Reçeli 30 g.","productCode":"201040500","productStockCode":null,"shipmentCompanyName":"MNG","shipmentCode":54895647251356,
         "description":"test","boDescription":null}
        """;

    private static string Page(string rows, int index = 1, int totalPages = 1) =>
        $$"""{"data":{"responsePage":{"pageSize":100,"pageIndex":{{index}},"totalCount":49,"totalPages":{{totalPages}}},"refundList":[{{rows}}]},"success":true}""";

    [Fact]
    public void Refund_becomes_a_claim_keyed_by_its_id_with_the_order_number()
    {
        var page = PazaramaReturnMapper.Page(Parse(Page(Refund)), 1, Now);
        var claim = Assert.Single(page.Items);

        Assert.Equal("ef2affaf-fdfc-4128-b5bc-3a4c1129e662", claim.ExternalClaimId);
        Assert.Equal("818589784", claim.ExternalOrderId);
        Assert.Equal("Ürünün parçası eksik", claim.ReasonCode);
        Assert.Equal("test", claim.ReasonText);
        Assert.Equal("MNG", claim.CargoProviderName);
        Assert.Equal("54895647251356", claim.CargoTrackingNumber);
        Assert.Contains("\"productCode\":\"201040500\"", claim.RawJson);
        Assert.Single(claim.Lines);
        Assert.Equal(49, page.TotalCount);
        Assert.False(page.HasMore);
    }

    [Fact]
    public void Turkish_dates_are_read_as_turkey_time_and_stored_as_utc()
    {
        var claim = PazaramaReturnMapper.Claim(Parse(Refund), Now)!;

        // 19:46 in Turkey (UTC+3) is 16:46 UTC; PostgreSQL only accepts zero offsets.
        Assert.Equal(new DateTimeOffset(2021, 10, 6, 16, 46, 0, TimeSpan.Zero), claim.LastModifiedAt);
        Assert.Equal(TimeSpan.Zero, claim.LastModifiedAt.Offset);
        Assert.Equal(new DateTimeOffset(2021, 10, 6, 11, 53, 0, TimeSpan.Zero), claim.OrderCreatedAt);
        Assert.Equal(TimeSpan.Zero, claim.OrderCreatedAt!.Value.Offset);
    }

    [Theory]
    [InlineData("2026-02-17 17:23", 14, 23)]
    [InlineData("2026-02-17T17:23:00", 14, 23)]
    [InlineData("2026-02-17T17:23:00+03:00", 14, 23)]
    [InlineData("2026-02-17T14:23:00Z", 14, 23)]
    [InlineData("17.02.2026 17:23", 14, 23)]
    public void Instants_in_the_other_known_shapes_are_read(string value, int hour, int minute)
    {
        var instant = PazaramaReturnMapper.Instant(value);

        Assert.Equal(new DateTimeOffset(2026, 2, 17, hour, minute, 0, TimeSpan.Zero), instant);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("yarın")]
    public void Unreadable_instants_are_missing(string? value) => Assert.Null(PazaramaReturnMapper.Instant(value));

    [Fact]
    public void A_refund_with_an_unreadable_date_falls_back_to_now()
    {
        var claim = PazaramaReturnMapper.Claim(Parse("""{"refundId":"r","orderNumber":1,"refundStatus":1,"refundDate":"belirsiz"}"""), Now)!;

        Assert.Equal(Now, claim.LastModifiedAt);
    }

    [Theory]
    [InlineData(1, "WaitingInAction", ReturnClaimStatus.ActionRequired)]
    [InlineData(2, "Accepted", ReturnClaimStatus.Approved)]
    [InlineData(3, "Rejected", ReturnClaimStatus.Rejected)]
    [InlineData(4, "Accepted", ReturnClaimStatus.Approved)]
    [InlineData(5, "Rejected", ReturnClaimStatus.Rejected)]
    [InlineData(6, "Accepted", ReturnClaimStatus.Approved)]
    [InlineData(7, "Cancelled", ReturnClaimStatus.Cancelled)]
    [InlineData(8, "Accepted", ReturnClaimStatus.Approved)]
    public void Refund_states_map_to_the_shared_return_states(int status, string raw, ReturnClaimStatus expected)
    {
        Assert.Equal(raw, PazaramaReturnMapper.CanonicalStatus(status));
        Assert.Equal(expected, MarketplaceReturnStatus.Canonicalize(PazaramaReturnMapper.CanonicalStatus(status)));
    }

    [Fact]
    public void An_unknown_state_waits_for_the_seller() =>
        Assert.Equal(ReturnClaimStatus.ActionRequired, MarketplaceReturnStatus.Canonicalize(PazaramaReturnMapper.CanonicalStatus(99)));

    [Fact]
    public void Refund_without_ids_is_reported_not_imported()
    {
        var page = PazaramaReturnMapper.Page(Parse(Page($$"""{{Refund}},{"refundNumber":5,"refundStatus":1}""")), 1, Now);

        Assert.Single(page.Items);
        var issue = Assert.Single(page.Issues!);
        Assert.Equal("RETURN_CLAIM_INVALID", issue.Code);
        Assert.Equal("5", issue.Identity);
    }

    [Fact]
    public void Paging_continues_until_the_last_page()
    {
        var more = PazaramaReturnMapper.Page(Parse(Page(Refund, index: 1, totalPages: 3)), 1, Now);
        var last = PazaramaReturnMapper.Page(Parse(Page(Refund, index: 3, totalPages: 3)), 3, Now);

        Assert.True(more.HasMore);
        Assert.Equal("2", more.NextCursor);
        Assert.False(last.HasMore);
        Assert.Null(last.NextCursor);
    }

    [Theory]
    [InlineData("""{"data":null,"success":true}""")]
    [InlineData("""{"data":{"refundList":[]}}""")]
    [InlineData("""{"data":{}}""")]
    public void An_empty_answer_has_no_claims(string json)
    {
        var page = PazaramaReturnMapper.Page(Parse(json), 1, Now);

        Assert.Empty(page.Items);
        Assert.False(page.HasMore);
    }

    [Theory]
    [InlineData("""{"success":true}""")]
    [InlineData("""{"data":[]}""")]
    [InlineData("""{"data":{"refundList":{}}}""")]
    public void A_different_shape_is_a_contract_error(string json) =>
        Assert.Throws<JsonException>(() => PazaramaReturnMapper.Page(Parse(json), 1, Now));

    [Fact]
    public void Request_body_uses_turkey_days_and_covers_the_last_day()
    {
        var body = JsonSerializer.Serialize(PazaramaHttpClient.RefundBody(new DateTimeOffset(2026, 9, 1, 21, 30, 0, TimeSpan.Zero), new DateTimeOffset(2026, 9, 28, 10, 0, 0, TimeSpan.Zero), 2, 100));

        // 21:30 UTC on 1 Sep is already 2 Sep in Turkey.
        Assert.Contains("\"requestStartDate\":\"2026-09-02\"", body);
        Assert.Contains("\"requestEndDate\":\"2026-09-29\"", body);
        Assert.Contains("\"pageNumber\":2", body);
        Assert.Contains("\"pageSize\":100", body);
    }

    [Fact]
    public void The_documented_reject_reasons_are_offered() =>
        Assert.Equal(Enumerable.Range(1, 12).Select(x => x.ToString()), PazaramaReturnMapper.RejectReasons.Select(x => x.Id));
}
