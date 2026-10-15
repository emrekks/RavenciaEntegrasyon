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

    // ---- decisions ----

    private const string RefundId = "ef2affaf-fdfc-4128-b5bc-3a4c1129e662";

    private static ReturnActionCommand Command(string action, string? reason = null, string? explanation = null, params ReturnEvidenceFile[] files) =>
        new(RefundId, [RefundId], action, reason, explanation, files);

    private static ReturnEvidenceFile Photo(int bytes = 100, string mime = "image/png") => new("kanit.png", mime, new byte[bytes]);

    [Fact]
    public void Approval_sends_status_two_for_the_refund()
    {
        var request = PazaramaReturnMapper.ActionBody(Command("APPROVE"));

        Assert.Null(request.Error);
        Assert.Equal($$"""{"refundId":"{{RefundId}}","status":2}""", JsonSerializer.Serialize(request.Body));
    }

    [Fact]
    public void Rejection_carries_reason_description_and_photos_as_base64()
    {
        var request = PazaramaReturnMapper.ActionBody(Command("REJECT", "2", " Urun defolu geldi ", new ReturnEvidenceFile("a.png", "image/png", [1, 2, 3])));

        Assert.Null(request.Error);
        var body = JsonSerializer.Serialize(request.Body);
        Assert.Contains("\"status\":3", body);
        Assert.Contains("\"refundRejectType\":2", body);
        Assert.Contains("\"description\":\"Urun defolu geldi\"", body);
        Assert.Contains("\"documentObjects\":[{\"name\":\"a.png\",\"bytes\":\"AQID\"}]", body);
    }

    [Theory]
    [InlineData("10")]
    [InlineData("11")]
    public void Empty_package_and_lost_package_reasons_need_no_photo(string reason)
    {
        var request = PazaramaReturnMapper.ActionBody(Command("REJECT", reason, "Paket boş geldi"));

        Assert.Null(request.Error);
        Assert.Contains("\"documentObjects\":[]", JsonSerializer.Serialize(request.Body));
    }

    [Theory]
    [InlineData("1")]
    [InlineData("7")]
    [InlineData("12")]
    public void Other_reasons_need_at_least_one_photo(string reason) =>
        Assert.NotNull(PazaramaReturnMapper.ActionBody(Command("REJECT", reason, "Açıklama")).Error);

    [Theory]
    [InlineData(null, "Açıklama")]
    [InlineData("", "Açıklama")]
    [InlineData("0", "Açıklama")]
    [InlineData("13", "Açıklama")]
    [InlineData("abc", "Açıklama")]
    [InlineData("10", null)]
    [InlineData("10", "   ")]
    public void A_rejection_needs_a_known_reason_and_an_explanation(string? reason, string? explanation) =>
        Assert.NotNull(PazaramaReturnMapper.ActionBody(Command("REJECT", reason, explanation, Photo())).Error);

    [Fact]
    public void Photos_are_limited_to_three_files_of_five_megabytes_each()
    {
        Assert.Null(PazaramaReturnMapper.ActionBody(Command("REJECT", "1", "x", Photo(), Photo(), Photo())).Error);
        Assert.NotNull(PazaramaReturnMapper.ActionBody(Command("REJECT", "1", "x", Photo(), Photo(), Photo(), Photo())).Error);
        Assert.Null(PazaramaReturnMapper.ActionBody(Command("REJECT", "1", "x", Photo(PazaramaReturnMapper.MaxEvidenceBytes))).Error);
        Assert.NotNull(PazaramaReturnMapper.ActionBody(Command("REJECT", "1", "x", Photo(PazaramaReturnMapper.MaxEvidenceBytes + 1))).Error);
        Assert.NotNull(PazaramaReturnMapper.ActionBody(Command("REJECT", "1", "x", Photo(0))).Error);
    }

    [Fact]
    public void Only_images_are_accepted_as_evidence() =>
        Assert.NotNull(PazaramaReturnMapper.ActionBody(Command("REJECT", "1", "x", Photo(10, "application/pdf"))).Error);

    [Theory]
    [InlineData("")]
    [InlineData("not-a-guid")]
    public void The_refund_id_must_be_a_guid(string id) =>
        Assert.NotNull(PazaramaReturnMapper.ActionBody(new ReturnActionCommand(id, [], "APPROVE", null, null, [])).Error);

    [Fact]
    public void Reasons_report_which_ones_need_photos()
    {
        var needPhotos = PazaramaReturnMapper.RejectReasons.Where(x => x.EvidenceRequired).Select(x => x.Id).ToList();

        Assert.Equal(10, needPhotos.Count);
        Assert.DoesNotContain("10", needPhotos);
        Assert.DoesNotContain("11", needPhotos);
    }

    [Fact]
    public void The_update_service_is_the_documented_one() => Assert.Equal("order/updateRefund", PazaramaHttpClient.UpdateRefund);

    [Fact]
    public void The_documented_reject_reasons_are_offered() =>
        Assert.Equal(Enumerable.Range(1, 12).Select(x => x.ToString()), PazaramaReturnMapper.RejectReasons.Select(x => x.Id));
}
