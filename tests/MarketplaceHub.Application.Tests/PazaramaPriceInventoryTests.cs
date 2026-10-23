using System.Text.Json;
using System.Text.Json.Nodes;
using MarketplaceHub.Application;
using MarketplaceHub.Infrastructure.Adapters.Pazarama;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class PazaramaPriceInventoryTests
{
    private static PriceInventoryPushLine Line(string barcode, decimal quantity = 5, decimal list = 120m, decimal sale = 99.994m) =>
        new(Guid.NewGuid(), Guid.NewGuid(), barcode, quantity, list, sale, "TRY", 1, 1, "hash");

    private static string Both(IReadOnlyList<PriceInventoryPushLine> lines) =>
        PazaramaPriceInventoryRules.Payload(lines.Select(line => (line, true, true)).ToList());

    [Fact]
    public void A_barcode_goes_only_into_the_lists_whose_value_changed()
    {
        var payload = JsonNode.Parse(PazaramaPriceInventoryRules.Payload([(Line("PRICE"), true, false), (Line("STOCK"), false, true), (Line("BOTH"), true, true)]))!.AsObject();

        Assert.Equal(["PRICE", "BOTH"], payload["price"]!.AsArray().Select(x => (string?)x!["code"]));
        Assert.Equal(["STOCK", "BOTH"], payload["stock"]!.AsArray().Select(x => (string?)x!["code"]));
    }

    [Fact]
    public void A_price_only_payload_makes_only_a_price_request()
    {
        var bodies = PazaramaPriceInventoryRules.Split(PazaramaPriceInventoryRules.Payload([(Line("B1"), true, false)]));

        Assert.NotNull(bodies);
        Assert.Equal(1, bodies.PriceCount);
        Assert.Equal(0, bodies.StockCount);
    }

    [Fact]
    public void The_price_hash_is_stable_and_depends_on_every_part()
    {
        var hash = PazaramaPriceInventoryRules.PriceHash(120m, 99.99m, 1);

        Assert.Equal(hash, PazaramaPriceInventoryRules.PriceHash(120m, 99.99m, 1));
        Assert.NotEqual(hash, PazaramaPriceInventoryRules.PriceHash(121m, 99.99m, 1));
        Assert.NotEqual(hash, PazaramaPriceInventoryRules.PriceHash(120m, 99.98m, 1));
        Assert.NotEqual(hash, PazaramaPriceInventoryRules.PriceHash(120m, 99.99m, 2));
    }

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    // ---- payload ----

    [Fact]
    public void Payload_carries_price_and_stock_lists_keyed_by_barcode()
    {
        var payload = JsonNode.Parse(Both([Line("B1", 7.9m), Line("B2", -3m, 50m, 80m)]))!.AsObject();

        var price = payload["price"]!.AsArray();
        var stock = payload["stock"]!.AsArray();
        Assert.Equal(2, price.Count);
        Assert.Equal("B1", (string?)price[0]!["code"]);
        Assert.Equal(99.99m, (decimal?)price[0]!["salePrice"]);
        Assert.Equal(120m, (decimal?)price[0]!["listPrice"]);
        // listPrice never drops below salePrice.
        Assert.Equal(80m, (decimal?)price[1]!["listPrice"]);
        Assert.Equal(7, (int?)stock[0]!["stockCount"]);
        Assert.Equal(0, (int?)stock[1]!["stockCount"]);
    }

    [Fact]
    public void Split_makes_one_request_body_per_list()
    {
        var bodies = PazaramaPriceInventoryRules.Split(Both([Line("B1"), Line("B2")]));

        Assert.NotNull(bodies);
        Assert.Equal(2, bodies.PriceCount);
        Assert.Equal(2, bodies.StockCount);
        var price = JsonNode.Parse(bodies.Price)!["items"]!.AsArray();
        var stock = JsonNode.Parse(bodies.Stock)!["items"]!.AsArray();
        Assert.Equal(["B1", "B2"], price.Select(x => (string?)x!["code"]));
        Assert.Equal(["B1", "B2"], stock.Select(x => (string?)x!["code"]));
        Assert.Null(price[0]!["stockCount"]);
        Assert.Null(stock[0]!["salePrice"]);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("""{"price":[],"stock":[]}""")]
    [InlineData("""{"price":[{"code":"","listPrice":10,"salePrice":10}],"stock":[]}""")]
    [InlineData("""{"price":[{"code":"B1","listPrice":5,"salePrice":10}],"stock":[]}""")]
    [InlineData("""{"price":[{"code":"B1","listPrice":10,"salePrice":0}],"stock":[]}""")]
    [InlineData("""{"price":[],"stock":[{"code":"B1","stockCount":-1}]}""")]
    [InlineData("""{"price":[],"stock":[{"code":"B1","stockCount":1.5}]}""")]
    [InlineData("""{"price":[],"stock":[{"stockCount":1}]}""")]
    public void Split_rejects_payloads_the_service_would_refuse(string payload) =>
        Assert.Null(PazaramaPriceInventoryRules.Split(payload));

    [Fact]
    public void Split_rejects_more_than_the_request_limit()
    {
        var lines = Enumerable.Range(0, PazaramaPriceInventoryRules.MaxItemsPerRequest + 1).Select(i => Line($"B{i}")).ToList();

        Assert.Null(PazaramaPriceInventoryRules.Split(Both(lines)));
        Assert.NotNull(PazaramaPriceInventoryRules.Split(Both(lines.Take(PazaramaPriceInventoryRules.MaxItemsPerRequest).ToList())));
    }

    [Fact]
    public void Split_rejects_a_barcode_longer_than_the_documented_length() =>
        Assert.Null(PazaramaPriceInventoryRules.Split(Both([Line(new string('9', PazaramaPriceInventoryRules.MaxCodeLength + 1))])));

    [Theory]
    [InlineData(5.9, 5)]
    [InlineData(-2, 0)]
    [InlineData(0, 0)]
    [InlineData(1e12, int.MaxValue)]
    public void Quantity_is_a_non_negative_whole_number(double available, int expected) =>
        Assert.Equal(expected, PazaramaPriceInventoryRules.Quantity((decimal)available));

    // ---- operation id ----

    [Fact]
    public void Operation_id_round_trips_both_batches()
    {
        var parts = PazaramaBatchMapper.ParseOperation("p:aaa:12|s:bbb:12");

        Assert.Equal([("p", "aaa", 12), ("s", "bbb", 12)], parts.Select(x => (x.Kind, x.DataId, x.ExpectedCount)));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("aaa")]
    [InlineData("x:aaa:1")]
    [InlineData("p:aaa:0")]
    [InlineData("p:aaa:1|garbage")]
    [InlineData("p::1")]
    public void Foreign_operation_ids_are_refused(string? id) => Assert.Empty(PazaramaBatchMapper.ParseOperation(id));

    // ---- results ----

    private static string Row(string code, string price = "null", string stock = "null", string text = "Başarılı") =>
        $$"""{"id":"i","batchId":"b","code":"{{code}}","price":{{price}},"stock":{{stock}},"status":null,"operationStatusText":"{{text}}"}""";

    private static string Status(int status, string detail = "x") => $$"""{"status":{{status}},"operationDetail":"{{detail}}"}""";

    private static string Result(string rows, int? total = null, string pageInfo = "") =>
        $$"""{"data":{"data":[{{rows}}],"totalCount":{{total ?? rows.Split("\"code\"").Length - 1}},"pageIndex":1,"pageSize":100{{pageInfo}}},"success":true}""";

    [Fact]
    public void Result_page_lists_barcodes_with_their_state_and_total()
    {
        var page = PazaramaBatchMapper.Page(Parse(Result($"{Row("B1", Status(0, "Başarılı"))},{Row("B2", Status(2, "Geçersiz fiyat"), text: "Hata oluştu")}")));

        Assert.Equal(2, page.TotalCount);
        Assert.Equal(PazaramaBatchMapper.BatchState.Succeeded, page.Items[0].State);
        Assert.Equal(PazaramaBatchMapper.BatchState.Failed, page.Items[1].State);
        Assert.Equal("Geçersiz fiyat", page.Items[1].Detail);
    }

    [Theory]
    [InlineData(0, PazaramaBatchMapper.BatchState.Succeeded)]
    [InlineData(5, PazaramaBatchMapper.BatchState.PendingApproval)]
    [InlineData(3, PazaramaBatchMapper.BatchState.Processing)]
    [InlineData(1, PazaramaBatchMapper.BatchState.Failed)]
    [InlineData(2, PazaramaBatchMapper.BatchState.Failed)]
    public void Part_status_codes_map_to_states(int status, PazaramaBatchMapper.BatchState expected)
    {
        var page = PazaramaBatchMapper.Page(Parse(Result(Row("B1", Status(status)))));

        Assert.Equal(expected, page.Items.Single().State);
    }

    [Theory]
    [InlineData("Başarılı", PazaramaBatchMapper.BatchState.Succeeded)]
    [InlineData("Onaya gönderildi", PazaramaBatchMapper.BatchState.PendingApproval)]
    [InlineData("İşleniyor", PazaramaBatchMapper.BatchState.Processing)]
    [InlineData("Hata oluştu", PazaramaBatchMapper.BatchState.Failed)]
    [InlineData("Tamamlanamadı", PazaramaBatchMapper.BatchState.Failed)]
    public void Without_part_status_the_overall_text_decides(string text, PazaramaBatchMapper.BatchState expected)
    {
        var page = PazaramaBatchMapper.Page(Parse(Result(Row("B1", text: text))));

        Assert.Equal(expected, page.Items.Single().State);
    }

    [Theory]
    [InlineData("""{"success":true}""")]
    [InlineData("""{"data":[]}""")]
    [InlineData("""{"data":{"totalCount":0}}""")]
    public void Result_without_a_list_is_a_contract_error(string json) =>
        Assert.Throws<JsonException>(() => PazaramaBatchMapper.Page(Parse(json)));

    private static (PazaramaBatchMapper.BatchPart, IReadOnlyList<PazaramaBatchMapper.BatchItem>) Part(string kind, int expected, params (string Code, PazaramaBatchMapper.BatchState State)[] items) =>
        (new(kind, kind + "id", expected), items.Select(x => new PazaramaBatchMapper.BatchItem(x.Code, x.State, "detay")).ToList());

    [Fact]
    public void Operation_is_in_progress_until_every_announced_result_arrived()
    {
        var status = PazaramaBatchMapper.Merge("op", [
            Part("p", 2, ("B1", PazaramaBatchMapper.BatchState.Succeeded)),
            Part("s", 2, ("B1", PazaramaBatchMapper.BatchState.Succeeded), ("B2", PazaramaBatchMapper.BatchState.Succeeded))]);

        Assert.Equal("IN_PROGRESS", status.Status);
        Assert.Empty(status.Lines);
    }

    [Fact]
    public void Operation_is_in_progress_while_any_item_is_processing()
    {
        var status = PazaramaBatchMapper.Merge("op", [
            Part("p", 1, ("B1", PazaramaBatchMapper.BatchState.Succeeded)),
            Part("s", 1, ("B1", PazaramaBatchMapper.BatchState.Processing))]);

        Assert.Equal("IN_PROGRESS", status.Status);
    }

    // C39: status 5 "Onaya gönderildi" is accepted but reported as waiting for Pazarama's approval.
    [Fact]
    public void A_price_sent_to_approval_is_accepted_and_reported_as_pending()
    {
        var status = PazaramaBatchMapper.Merge("op", [
            Part("p", 2, ("B1", PazaramaBatchMapper.BatchState.PendingApproval), ("B2", PazaramaBatchMapper.BatchState.Succeeded)),
            Part("s", 2, ("B1", PazaramaBatchMapper.BatchState.Succeeded), ("B2", PazaramaBatchMapper.BatchState.Succeeded))]);

        Assert.Equal("COMPLETED", status.Status);
        var b1 = status.Lines.Single(x => x.ExternalKey == "B1");
        Assert.True(b1.Succeeded);
        Assert.Null(b1.ErrorCode);
        Assert.Equal(PazaramaBatchMapper.PendingApprovalStatus, b1.Status);
        Assert.NotEqual(PazaramaBatchMapper.PendingApprovalStatus, status.Lines.Single(x => x.ExternalKey == "B2").Status);
    }

    [Fact]
    public void A_barcode_succeeds_only_when_price_and_stock_both_did()
    {
        var status = PazaramaBatchMapper.Merge("op", [
            Part("p", 2, ("B1", PazaramaBatchMapper.BatchState.Succeeded), ("B2", PazaramaBatchMapper.BatchState.Failed)),
            Part("s", 2, ("B1", PazaramaBatchMapper.BatchState.Failed), ("B2", PazaramaBatchMapper.BatchState.Succeeded))]);

        Assert.Equal("COMPLETED", status.Status);
        var b1 = status.Lines.Single(x => x.ExternalKey == "B1");
        var b2 = status.Lines.Single(x => x.ExternalKey == "B2");
        Assert.False(b1.Succeeded);
        Assert.Equal("PAZARAMA_STOCK_REJECTED", b1.ErrorCode);
        Assert.False(b2.Succeeded);
        Assert.Equal("PAZARAMA_PRICE_REJECTED", b2.ErrorCode);
    }

    [Fact]
    public void A_part_that_was_not_accepted_does_not_hold_the_other_back()
    {
        var parts = PazaramaBatchMapper.ParseOperation("s:bbb:2|p!:PAZARAMA_VALIDATION_FAILED:2");
        Assert.Equal([("s", true), ("p", false)], parts.Select(x => (x.Family, x.Sent)));

        var status = PazaramaBatchMapper.Merge("op", [
            (parts[0], [new PazaramaBatchMapper.BatchItem("B1", PazaramaBatchMapper.BatchState.Succeeded, null), new PazaramaBatchMapper.BatchItem("B2", PazaramaBatchMapper.BatchState.Failed, "Stok geçersiz")]),
            (parts[1], [])]);

        Assert.Equal("COMPLETED", status.Status);
        Assert.True(status.Lines.Single(x => x.ExternalKey == "B1").Succeeded);
        Assert.Equal(PazaramaBatchMapper.StockRejected, status.Lines.Single(x => x.ExternalKey == "B2").ErrorCode);
    }

    [Fact]
    public void A_value_Pazarama_already_holds_is_up_to_date_not_failed()
    {
        Assert.True(PazaramaPriceInventoryRules.AlreadyHeld("Pazarama isteği reddetti: İndirimli fiyat, liste fiyatı ve stok sayısı önceki değerleriyle aynı olamaz — Pazarama bu fiyat veya stok değerini zaten tutuyor."));
        Assert.False(PazaramaPriceInventoryRules.AlreadyHeld("Pazarama isteği reddetti: Ürün bulunamadı"));
        Assert.False(PazaramaPriceInventoryRules.AlreadyHeld(null));

        var parts = PazaramaBatchMapper.ParseOperation($"s:bbb:1|p=:{PazaramaBatchMapper.AlreadyHeldMarker}:1");
        Assert.Equal([("s", true, false), ("p", false, true)], parts.Select(x => (x.Family, x.Sent, x.Held)));
        // A held part reads nothing back and does not hold the operation open.
        var status = PazaramaBatchMapper.Merge("op", [(parts[0], [new PazaramaBatchMapper.BatchItem("B1", PazaramaBatchMapper.BatchState.Succeeded, null)]), (parts[1], [])]);
        Assert.Equal("COMPLETED", status.Status);
        Assert.True(status.Lines.Single().Succeeded);
    }

    // ---- a request refused over one value Pazarama already holds (audit C2) ----

    private const string HeldMessage = "Pazarama isteği reddetti: İndirimli fiyat, liste fiyatı ve stok sayısı önceki değerleriyle aynı olamaz";

    private static List<string> Codes(string body) => JsonNode.Parse(body)!["items"]!.AsArray().Select(x => (string)x!["code"]!).ToList();

    // Pazarama refuses any request that carries one of the held barcodes and accepts the rest.
    private static Func<string, string, CancellationToken, Task<AdapterResult<JsonDocument>>> Pazarama(List<(string Path, List<string> Codes)> calls, params string[] held) =>
        (path, body, _) =>
        {
            var codes = Codes(body);
            calls.Add((path, codes));
            return Task.FromResult(codes.Any(held.Contains)
                ? AdapterResult<JsonDocument>.Failure(new(AdapterErrorClass.Validation, "PAZARAMA_REQUEST_REJECTED", HeldMessage, null, null, null))
                : AdapterResult<JsonDocument>.Success(JsonDocument.Parse($$"""{"data":"id-{{string.Join('-', codes)}}","success":true}""")));
        };

    private static async Task<string> Push(string payload, List<(string Path, List<string> Codes)> calls, params string[] held)
    {
        var result = await PazaramaHttpClient.PushAsync(PazaramaPriceInventoryRules.Split(payload)!, Pazarama(calls, held), () => DateTimeOffset.UnixEpoch, CancellationToken.None);
        Assert.True(result.IsSuccess);
        return result.Value!.ExternalOperationId;
    }

    // Pazarama's later results: every barcode of every sent part succeeded.
    private static Func<string, RemoteOperationLine?, PazaramaBatchMapper.LineOutcome> Outcomes(string operationId, string payload)
    {
        var parts = PazaramaBatchMapper.ParseOperation(operationId);
        var (price, stock) = PazaramaBatchMapper.PayloadCodeLists(payload);
        var status = PazaramaBatchMapper.Merge(operationId, parts.Select(part => (part, (IReadOnlyList<PazaramaBatchMapper.BatchItem>)(part.Sent
            ? (part.Family == "p" ? price : stock).Skip(part.Offset ?? 0).Take(part.ExpectedCount).Select(code => new PazaramaBatchMapper.BatchItem(code, PazaramaBatchMapper.BatchState.Succeeded, null)).ToList()
            : []))).ToList());
        Assert.Equal("COMPLETED", status.Status);
        var outcome = PazaramaBatchMapper.LineOutcomes(operationId, payload);
        return (code, _) => outcome(code, status.Lines.SingleOrDefault(line => line.ExternalKey == code));
    }

    [Fact]
    public async Task A_two_barcode_stock_request_refused_as_held_ends_with_the_unchanged_one_held_and_the_other_sent()
    {
        var payload = PazaramaPriceInventoryRules.Payload([(Line("HELD"), false, true), (Line("NEW"), false, true)]);
        var calls = new List<(string Path, List<string> Codes)>();

        var operationId = await Push(payload, calls, "HELD");

        // The whole list, then each barcode alone, all through the same paced stock request.
        Assert.Equal(["HELD,NEW", "HELD", "NEW"], calls.Select(call => string.Join(',', call.Codes)));
        Assert.All(calls, call => Assert.Equal(PazaramaHttpClient.StockUpdate, call.Path));
        Assert.Equal($"s=:{PazaramaBatchMapper.AlreadyHeldMarker}:1:0|s:id-NEW:1:1", operationId);
        var outcome = Outcomes(operationId, payload);
        Assert.Equal(new PazaramaBatchMapper.LineOutcome(true, true, null, false), outcome("HELD", null));
        Assert.Equal(new PazaramaBatchMapper.LineOutcome(true, true, null, false), outcome("NEW", null));
    }

    [Fact]
    public async Task A_split_finds_the_held_barcode_by_halves_and_a_line_refused_later_stays_unsent()
    {
        var lines = Enumerable.Range(0, 8).Select(i => (Line($"B{i}"), true, true)).ToList();
        var payload = PazaramaPriceInventoryRules.Payload(lines);
        var calls = new List<(string Path, List<string> Codes)>();

        var operationId = await Push(payload, calls, "B5");

        // Stock: 8 refused, 0-3 sent, 4-7 refused, 4-5 refused, B4 sent, B5 held, 6-7 sent; then the price list the same way.
        Assert.Equal(14, calls.Count);
        var parts = PazaramaBatchMapper.ParseOperation(operationId);
        Assert.Equal([("s", 4, 0), ("s", 1, 4), ("s=", 1, 5), ("s", 2, 6)], parts.Where(part => part.Family == "s").Select(part => (part.Kind, part.ExpectedCount, part.Offset ?? -1)));
        var outcome = Outcomes(operationId, payload);
        Assert.All(lines, line => Assert.Equal(new PazaramaBatchMapper.LineOutcome(true, true, null, false), outcome(line.Item1.Barcode, null)));

        // A barcode Pazarama later reports as refused is not marked sent; a sent barcode without a result is a contract error.
        var judge = PazaramaBatchMapper.LineOutcomes(operationId, payload);
        Assert.False(judge("B6", new RemoteOperationLine("B6", false, null, PazaramaBatchMapper.StockRejected, false, "FAIL")).StockOk);
        Assert.True(judge("B6", null).MissingResult);
        Assert.False(judge("B5", null).MissingResult);
    }

    [Fact]
    public async Task When_the_split_budget_is_spent_the_rest_stays_not_sent_by_position()
    {
        var lines = Enumerable.Range(0, 64).Select(i => (Line($"B{i}"), false, true)).ToList();
        var payload = PazaramaPriceInventoryRules.Payload(lines);
        var calls = new List<(string Path, List<string> Codes)>();
        // Every barcode is held: each request is refused.
        var operationId = await Push(payload, calls, lines.Select(line => line.Item1.Barcode).ToArray());

        Assert.Equal(1 + PazaramaHttpClient.MaxSplitRequests, calls.Count);
        var parts = PazaramaBatchMapper.ParseOperation(operationId);
        // Every barcode is covered exactly once, by a held part or a not-sent one.
        Assert.Equal(64, parts.Sum(part => part.ExpectedCount));
        Assert.All(parts, part => Assert.NotNull(part.Offset));
        var judge = PazaramaBatchMapper.LineOutcomes(operationId, payload);
        var outcomes = lines.Select(line => judge(line.Item1.Barcode, null)).ToList();
        Assert.Contains(outcomes, outcome => outcome.StockOk);
        Assert.Contains(outcomes, outcome => !outcome.StockOk && outcome.NotSentCode == "PAZARAMA_REQUEST_REJECTED");
        Assert.Equal(parts.Where(part => part.Held).Sum(part => part.ExpectedCount), outcomes.Count(outcome => outcome.StockOk));
    }

    [Fact]
    public void An_operation_id_without_positions_still_covers_each_whole_list()
    {
        var payload = Both([Line("B1"), Line("B2")]);
        var judge = PazaramaBatchMapper.LineOutcomes($"s:bbb:2|p!:PAZARAMA_VALIDATION_FAILED:2", payload);

        var outcome = judge("B2", new RemoteOperationLine("B2", true, null, null, false, null));
        Assert.True(outcome.StockOk);
        Assert.False(outcome.PriceOk);
        Assert.Equal("PAZARAMA_VALIDATION_FAILED", outcome.NotSentCode);
        Assert.True(judge("B1", null).MissingResult);
        Assert.Equal([("s", 2, (int?)7)], PazaramaBatchMapper.ParseOperation("s:bbb:2:7").Select(part => (part.Kind, part.ExpectedCount, part.Offset)));
        Assert.Empty(PazaramaBatchMapper.ParseOperation("s:bbb:2:x"));
    }

    [Fact]
    public void A_barcode_refused_for_both_says_so()
    {
        var status = PazaramaBatchMapper.Merge("op", [
            Part("p", 1, ("B1", PazaramaBatchMapper.BatchState.Failed)),
            Part("s", 1, ("B1", PazaramaBatchMapper.BatchState.Failed))]);

        Assert.Equal(PazaramaBatchMapper.PriceAndStockRejected, Assert.Single(status.Lines).ErrorCode);
    }

    [Fact]
    public void Payload_codes_list_which_barcodes_carry_a_price_and_which_a_stock()
    {
        var (price, stock) = PazaramaBatchMapper.PayloadCodes("""{"price":[{"code":"B1","listPrice":10,"salePrice":9}],"stock":[{"code":"B1","stockCount":3},{"code":"B2","stockCount":0}]}""");

        Assert.Equal(["B1"], price);
        Assert.Equal(["B1", "B2"], stock.Order());
        Assert.Equal("PAZARAMAX1", PazaramaBatchMapper.MarkerCode("PAZARAMA:X|1"));
        Assert.Equal("PAZARAMA_REQUEST_REJECTED", PazaramaBatchMapper.MarkerCode(":|"));
    }

    [Fact]
    public void Fully_successful_operation_reports_every_barcode_as_done()
    {
        var status = PazaramaBatchMapper.Merge("op", [
            Part("p", 2, ("B1", PazaramaBatchMapper.BatchState.Succeeded), ("B2", PazaramaBatchMapper.BatchState.Succeeded)),
            Part("s", 2, ("b1", PazaramaBatchMapper.BatchState.Succeeded), ("B2", PazaramaBatchMapper.BatchState.Succeeded))]);

        Assert.Equal("COMPLETED", status.Status);
        // Barcodes are matched without regard to case.
        Assert.Equal(2, status.Lines.Count);
        Assert.All(status.Lines, line => Assert.True(line.Succeeded));
    }

    [Fact]
    public void Request_paths_follow_the_documented_services()
    {
        Assert.Equal("product/updatePrice-v2", PazaramaHttpClient.PriceUpdate);
        Assert.Equal("product/updateStock-v2", PazaramaHttpClient.StockUpdate);
        Assert.Equal("listing-state/batch-id/abc/lake-projections?page=2&pageSize=1000", PazaramaHttpClient.BatchResults("abc", 2, 1000));
    }

    [Theory]
    [InlineData("product/updatePrice-v2", "price-stock")]
    [InlineData("product/updateStock-v2", "price-stock")]
    [InlineData("listing-state/batch-id/abc/lake-projections", "reads")]
    public void Price_and_stock_writes_share_the_ten_second_bucket_and_results_are_ordinary_reads(string path, string bucket) =>
        Assert.Equal(bucket, PazaramaRateLimitHandler.BucketFor(new Uri("https://x/" + path)));
}
