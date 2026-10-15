using System.Text.Json;
using System.Text.Json.Nodes;
using MarketplaceHub.Infrastructure.Adapters.Pazarama;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class PazaramaPackageActionTests
{
    private const string Item1 = "d6da388c-f465-4f9c-8800-afc9271b801b";
    private const string Item2 = "3fa85f64-5717-4562-b3fc-2c963f66afa6";
    private const string Address = "ccc02724-9f12-4789-e3a2-08dd4a725426";
    private const string Carrier = "8eb9aeb7-fd11-425b-9930-08d8e48cc18f";

    private static string Lines(params string[] ids) =>
        JsonSerializer.Serialize(new { lines = ids.Select(id => new { lineId = id, quantity = 1 }), status = "Picking" });

    // ---- package id ----

    [Theory]
    [InlineData("order:735071747", 735071747L)]
    [InlineData(" ORDER:1 ", 1L)]
    public void Package_ids_carry_the_order_number(string id, long expected)
    {
        Assert.True(PazaramaPackageActionMapper.TryOrderNumber(id, out var number));
        Assert.Equal(expected, number);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("735071747")]
    [InlineData("order:")]
    [InlineData("order:abc")]
    [InlineData("order:0")]
    [InlineData("order:-5")]
    public void Anything_else_is_not_a_pazarama_package(string? id) => Assert.False(PazaramaPackageActionMapper.TryOrderNumber(id, out _));

    // ---- status updates ----

    [Fact]
    public void Picking_sends_one_status_request_per_order_item()
    {
        var bodies = PazaramaPackageActionMapper.StatusUpdates(735071747, Lines(Item1, Item2, Item1), PazaramaPackageActionMapper.PreparingStatus)
            .Select(body => JsonSerializer.Serialize(body)).ToList();

        // A repeated item is sent once.
        Assert.Equal(2, bodies.Count);
        Assert.Equal($$$"""{"orderNumber":735071747,"item":{"orderItemId":"{{{Item1}}}","status":12}}""", bodies[0]);
        Assert.Contains("\"status\":12", bodies[1]);
    }

    [Fact]
    public void Not_supplied_uses_status_thirteen()
    {
        var body = JsonSerializer.Serialize(PazaramaPackageActionMapper.StatusUpdates(1, Lines(Item1), PazaramaPackageActionMapper.NotSuppliedStatus).Single());

        Assert.Contains("\"status\":13", body);
    }

    [Theory]
    [InlineData("PICKING")]
    [InlineData("NOT_SUPPLIED")]
    public void Status_actions_need_order_item_guids(string action)
    {
        Assert.Null(PazaramaPackageActionMapper.Validate(action, Lines(Item1)));
        Assert.NotNull(PazaramaPackageActionMapper.Validate(action, "{}"));
        Assert.NotNull(PazaramaPackageActionMapper.Validate(action, """{"lines":[{"lineId":"12345"}]}"""));
        Assert.NotNull(PazaramaPackageActionMapper.Validate(action, """{"lines":[]}"""));
        Assert.NotNull(PazaramaPackageActionMapper.Validate(action, "not json"));
        Assert.NotNull(PazaramaPackageActionMapper.Validate(action, "[]"));
    }

    [Fact]
    public void Line_ids_may_be_plain_strings_or_objects()
    {
        var root = (JsonNode.Parse($$"""{"lines":["{{Item1}}",{"lineId":"{{Item2}}"}]}""") as JsonObject)!;

        Assert.Equal([Item1, Item2], PazaramaPackageActionMapper.LineIds(root));
    }

    // ---- packages ----

    [Fact]
    public void Split_body_names_the_order_and_leaves_defaults_empty()
    {
        var payload = $$"""{"packages":[{"orderLineIds":["{{Item1}}","{{Item2}}"],"sellerAddressId":null,"cargoCompanyId":"{{Carrier}}"},{"orderLineIds":["{{Item2}}"],"sellerAddressId":"{{Address}}"}]}""";

        var body = JsonNode.Parse(PazaramaPackageActionMapper.PackagesBody("order-guid", payload))!.AsObject();

        Assert.Equal("order-guid", (string?)body["orderId"]);
        var packages = body["apiOrderItemPackages"]!.AsArray();
        Assert.Equal(2, packages.Count);
        Assert.Equal([Item1, Item2], packages[0]!["orderItemIds"]!.AsArray().Select(x => (string?)x));
        Assert.Null(packages[0]!["sellerAddressId"]);
        Assert.Equal(Carrier, (string?)packages[0]!["cargoCompanyId"]);
        Assert.Equal(Address, (string?)packages[1]!["sellerAddressId"]);
        Assert.Null(packages[1]!["cargoCompanyId"]);
    }

    [Theory]
    [InlineData("SPLIT")]
    [InlineData("UPDATE_PACKAGE")]
    public void Package_actions_need_at_least_one_package_with_items(string action)
    {
        Assert.Null(PazaramaPackageActionMapper.Validate(action, $$"""{"packages":[{"orderLineIds":["{{Item1}}"]}]}"""));
        Assert.Null(PazaramaPackageActionMapper.Validate(action, $$"""{"apiOrderItemPackages":[{"orderItemIds":["{{Item1}}"]}]}"""));
        Assert.NotNull(PazaramaPackageActionMapper.Validate(action, "{}"));
        Assert.NotNull(PazaramaPackageActionMapper.Validate(action, """{"packages":[{"orderLineIds":[]}]}"""));
        Assert.NotNull(PazaramaPackageActionMapper.Validate(action, """{"packages":[{"orderLineIds":["x"]}]}"""));
        Assert.NotNull(PazaramaPackageActionMapper.Validate(action, """{"packages":["x"]}"""));
    }

    // ---- labor cost ----

    [Fact]
    public void Labor_cost_body_carries_item_cost_and_count()
    {
        var body = JsonNode.Parse(PazaramaPackageActionMapper.LaborCostBody($$"""{"items":[{"orderLineId":"{{Item1}}","laborCost":10,"processCount":2},{"orderLineId":"{{Item2}}","laborCost":0}]}"""))!.AsObject();

        var items = body["items"]!.AsArray();
        Assert.Equal(Item1, (string?)items[0]!["orderItemId"]);
        Assert.Equal(10, (int?)items[0]!["laborCost"]);
        Assert.Equal(2, (int?)items[0]!["processCount"]);
        Assert.Equal(0, (int?)items[1]!["processCount"]);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"items":[]}""")]
    [InlineData("""{"items":[{"orderLineId":"x","laborCost":1}]}""")]
    [InlineData("""{"items":[{"orderLineId":"d6da388c-f465-4f9c-8800-afc9271b801b"}]}""")]
    [InlineData("""{"items":[{"orderLineId":"d6da388c-f465-4f9c-8800-afc9271b801b","laborCost":-1}]}""")]
    public void Labor_cost_rejects_incomplete_items(string payload) => Assert.NotNull(PazaramaPackageActionMapper.Validate("LABOR_COST", payload));

    [Fact]
    public void Labor_cost_path_names_the_order() =>
        Assert.Equal("order/api/36ccecf2-786f-4b6b-82dc-87874df2e2dc/items/labor-costs", PazaramaHttpClient.LaborCosts("36ccecf2-786f-4b6b-82dc-87874df2e2dc"));

    // ---- cancel requests ----

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void Only_waiting_cancel_requests_of_this_order_are_decided()
    {
        var root = Parse("""
            {"data":{"refundList":[
              {"refundId":"r1","orderNumber":696730236,"refundStatus":1},
              {"refundId":"r2","orderNumber":696730236,"refundStatus":2},
              {"refundId":"r3","orderNumber":111,"refundStatus":1},
              {"refundId":"r1","orderNumber":696730236,"refundStatus":1},
              {"refundId":"r4","orderNumber":696730236,"refundStatus":1}]},"success":true}
            """);

        Assert.Equal(["r1", "r4"], PazaramaPackageActionMapper.WaitingCancelRequests(root, 696730236));
    }

    [Theory]
    [InlineData("""{"data":{}}""")]
    [InlineData("""{"data":{"refundList":[]}}""")]
    public void No_list_or_an_empty_list_means_nothing_to_decide(string json) =>
        Assert.Empty(PazaramaPackageActionMapper.WaitingCancelRequests(Parse(json), 1));

    [Theory]
    [InlineData("""{"success":true}""")]
    [InlineData("""{"data":[]}""")]
    public void A_different_shape_is_a_contract_error(string json) =>
        Assert.Throws<JsonException>(() => PazaramaPackageActionMapper.WaitingCancelRequests(Parse(json), 1));

    [Theory]
    [InlineData("CANCEL_APPROVE")]
    [InlineData("CANCEL_REJECT")]
    public void Cancel_decisions_take_an_empty_body(string action) => Assert.Null(PazaramaPackageActionMapper.Validate(action, "{}"));

    // ---- order id ----

    [Fact]
    public void Order_id_is_read_from_the_order_and_must_be_a_guid()
    {
        Assert.Equal("36ccecf2-786f-4b6b-82dc-87874df2e2dc", PazaramaPackageActionMapper.OrderId("""{"orderId":"36CCECF2-786F-4B6B-82DC-87874DF2E2DC","orderNumber":1}"""));
        Assert.Null(PazaramaPackageActionMapper.OrderId("""{"orderId":"nope"}"""));
        Assert.Null(PazaramaPackageActionMapper.OrderId("""{"orderNumber":1}"""));
        Assert.Null(PazaramaPackageActionMapper.OrderId("not json"));
    }

    [Fact]
    public void The_offered_actions_are_the_documented_ones() =>
        Assert.Equal(["PICKING", "NOT_SUPPLIED", "SPLIT", "UPDATE_PACKAGE", "LABOR_COST", "CANCEL_APPROVE", "CANCEL_REJECT"], PazaramaPackageActionMapper.Actions);

    [Fact]
    public void Request_paths_follow_the_documented_services()
    {
        Assert.Equal("order/updateOrderStatus", PazaramaHttpClient.UpdateOrderStatus);
        Assert.Equal("order/api/packages-split", PazaramaHttpClient.PackagesSplit);
        Assert.Equal("order/update-packages", PazaramaHttpClient.UpdatePackages);
        Assert.Equal("order/api/cancel/items", PazaramaHttpClient.CancelRequests);
        Assert.Equal("order/api/cancel", PazaramaHttpClient.CancelDecision);
    }
}
