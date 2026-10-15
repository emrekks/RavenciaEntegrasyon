using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MarketplaceHub.Application;

namespace MarketplaceHub.Infrastructure.Adapters.N11;

// Package actions (phase 5). The panel sends Trendyol-shaped bodies; they are
// turned into n11's requests here:
//   PICKING     -> PUT rest/order/v1/update {lines:[{lineId}], status:"Picking"}
//   SPLIT       -> POST rest/delivery/v1/splitCombinePackage, one group
//   MULTI_SPLIT -> the same service with several groups
//   SPLIT_BY_QUANTITY  -> POST rest/delivery/v1/splitPackageByQuantity (quantity splits, optional cancelledItems)
//   LABOR_COST         -> PUT  rest/order/v1/labor-costs
//   COLLECTION_REQUEST -> PUT  rest/delivery/v1/collectionRequest (logistics carriers HLZ/CEVA/BL)
// n11 approves only Created lines and splits only Picking packages; the n11
// answer carries the reason when a line is refused.
public sealed partial class N11HttpClient
{
    internal const string OrderUpdate = "rest/order/v1/update";
    internal const string SplitPackage = "rest/delivery/v1/splitCombinePackage";
    internal const string SplitPackageByQuantity = "rest/delivery/v1/splitPackageByQuantity";
    internal const string LaborCosts = "rest/order/v1/labor-costs";
    internal const string CollectionRequest = "rest/delivery/v1/collectionRequest";

    public async Task<AdapterResult<PackageActionResult>> ExecutePackageActionAsync(AdapterContext context, PackageActionCommand command, CancellationToken cancellationToken)
    {
        var action = command.Action.Trim().ToUpperInvariant();
        string? body;
        try { body = N11PackageActionMapper.Body(action, command.PayloadJson); }
        catch (JsonException) { return Failure<PackageActionResult>(AdapterErrorClass.Validation, "N11_PACKAGE_ACTION_PAYLOAD_INVALID", "N11 paket işlemi için sipariş satırları okunamadı.", HttpStatusCode.UnprocessableEntity); }
        if (body is null)
            return NotSupported<PackageActionResult>("N11_PACKAGE_ACTION_UNSUPPORTED", "N11 API'si yalnız siparişi işleme alma, paket bölme, işçilik bedeli ve lojistik toplama talebini destekliyor; bu işlemi N11 satıcı panelinden yapın.");

        var account = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken);
        if (account is null) return CredentialMissing<PackageActionResult>();
        if (!ConnectionWritesEnabled(account.Connection.SettingsJson))
            return Failure<PackageActionResult>(AdapterErrorClass.Validation, "N11_EXTERNAL_WRITES_DISABLED", "N11 bağlantısında dış yazma kapalı; paket işlemi gönderilmedi.", HttpStatusCode.Forbidden);
        var (method, path) = N11PackageActionMapper.Endpoint(action);
        var result = await SendAsync(account, method, path, cancellationToken, new StringContent(body, Encoding.UTF8, "application/json"));
        if (!result.IsSuccess) return AdapterResult<PackageActionResult>.Failure(result.Error!, result.RateLimit);
        using var document = result.Value!;
        var refused = N11PackageActionMapper.RefusedLines(document.RootElement);
        return refused is null
            ? AdapterResult<PackageActionResult>.Success(new(command.ExternalPackageId, "ACCEPTED", null), result.RateLimit)
            : Failure<PackageActionResult>(AdapterErrorClass.Validation, "N11_PACKAGE_ACTION_REJECTED", $"N11 işlemi reddetti: {refused}", HttpStatusCode.UnprocessableEntity);
    }
}

public static class N11PackageActionMapper
{
    // n11 cancel reasons of the quantity split: 61 out of stock, 62 defective, 63 wrong price,
    // 64 force majeure, 65 other.
    internal static readonly IReadOnlySet<int> CancelReasons = new HashSet<int> { 61, 62, 63, 64, 65 };
    internal static readonly IReadOnlySet<int> LaborVatRates = new HashSet<int> { 0, 1, 10, 20 };
    internal static readonly IReadOnlySet<string> CollectionCarriers = new HashSet<string>(StringComparer.Ordinal) { "HLZ", "CEVA", "BL" };

    internal static (HttpMethod Method, string Path) Endpoint(string action) => action switch
    {
        "PICKING" => (HttpMethod.Put, N11HttpClient.OrderUpdate),
        "LABOR_COST" => (HttpMethod.Put, N11HttpClient.LaborCosts),
        "COLLECTION_REQUEST" => (HttpMethod.Put, N11HttpClient.CollectionRequest),
        "SPLIT_BY_QUANTITY" => (HttpMethod.Post, N11HttpClient.SplitPackageByQuantity),
        _ => (HttpMethod.Post, N11HttpClient.SplitPackage)
    };

    // Returns the n11 request body, or null when n11 has no API for the action.
    public static string? Body(string action, string payloadJson)
    {
        using var document = JsonDocument.Parse(payloadJson);
        var root = document.RootElement;
        switch (action)
        {
            case "PICKING":
                var lineIds = LineIds(root, "lines", "lineId");
                if (lineIds.Count == 0) throw new JsonException("no order lines to approve");
                return new JsonObject
                {
                    ["lines"] = new JsonArray(lineIds.Select(id => (JsonNode)new JsonObject { ["lineId"] = id }).ToArray()),
                    ["status"] = "Picking"
                }.ToJsonString();
            case "SPLIT":
                var single = Numbers(root, "orderLineIds");
                if (single.Count == 0) throw new JsonException("no order lines to split");
                return SplitGroups([single]);
            case "MULTI_SPLIT":
                if (!root.TryGetProperty("splitGroups", out var groups) || groups.ValueKind != JsonValueKind.Array) throw new JsonException("no split groups");
                var parsed = groups.EnumerateArray().Select(group => Numbers(group, "orderLineIds")).Where(group => group.Count > 0).ToList();
                if (parsed.Count == 0) throw new JsonException("no split groups");
                return SplitGroups(parsed);
            case "SPLIT_BY_QUANTITY":
                return QuantitySplit(root);
            case "LABOR_COST":
                return LaborCost(root);
            case "COLLECTION_REQUEST":
                return Collection(root);
            default:
                return null;
        }
    }

    // {splitPackages:[{packageDetails:[{orderLineId,quantities}]}], cancelledItems?:[{cancelReasonId,orderLineId,quantity}]}.
    // n11 cancels items only together with a split, so a body without split packages is refused.
    private static string QuantitySplit(JsonElement root)
    {
        var packages = new JsonArray();
        if (root.TryGetProperty("splitPackages", out var splitPackages) && splitPackages.ValueKind == JsonValueKind.Array)
            foreach (var package in splitPackages.EnumerateArray())
            {
                var details = new JsonArray();
                if (package.ValueKind == JsonValueKind.Object && package.TryGetProperty("packageDetails", out var packageDetails) && packageDetails.ValueKind == JsonValueKind.Array)
                    foreach (var detail in packageDetails.EnumerateArray())
                    {
                        var lineId = Number(detail, "orderLineId");
                        var quantity = Number(detail, "quantities");
                        if (lineId is null || quantity is not > 0) throw new JsonException("invalid split package detail");
                        details.Add(new JsonObject { ["orderLineId"] = lineId, ["quantities"] = quantity });
                    }
                if (details.Count == 0) throw new JsonException("empty split package");
                packages.Add(new JsonObject { ["packageDetails"] = details });
            }
        if (packages.Count == 0) throw new JsonException("no split packages");
        var body = new JsonObject();
        if (root.TryGetProperty("cancelledItems", out var cancelled) && cancelled.ValueKind == JsonValueKind.Array && cancelled.GetArrayLength() > 0)
        {
            var items = new JsonArray();
            foreach (var item in cancelled.EnumerateArray())
            {
                var lineId = Number(item, "orderLineId");
                var quantity = Number(item, "quantity");
                var reason = Number(item, "cancelReasonId");
                if (lineId is null || quantity is not > 0 || reason is null || !CancelReasons.Contains((int)reason.Value)) throw new JsonException("invalid cancelled item");
                items.Add(new JsonObject { ["cancelReasonId"] = reason, ["orderLineId"] = lineId, ["quantity"] = quantity });
            }
            body["cancelledItems"] = items;
        }
        body["splitPackages"] = packages;
        return body.ToJsonString();
    }

    // {laborCostDetails:[{orderLineId,totalLaborCostExcludingVAT,laborVatRate?}]}; the VAT rate
    // defaults to 20 on n11's side when left out.
    private static string LaborCost(JsonElement root)
    {
        var rows = new JsonArray();
        if (root.TryGetProperty("laborCostDetails", out var details) && details.ValueKind == JsonValueKind.Array)
            foreach (var detail in details.EnumerateArray())
            {
                var lineId = Number(detail, "orderLineId");
                if (lineId is null || detail.ValueKind != JsonValueKind.Object
                    || !detail.TryGetProperty("totalLaborCostExcludingVAT", out var amountValue) || amountValue.ValueKind != JsonValueKind.Number || !amountValue.TryGetDecimal(out var amount) || amount <= 0)
                    throw new JsonException("invalid labor cost line");
                var row = new JsonObject { ["orderLineId"] = lineId, ["totalLaborCostExcludingVAT"] = Math.Round(amount, 2, MidpointRounding.AwayFromZero) };
                if (detail.TryGetProperty("laborVatRate", out var vat) && vat.ValueKind != JsonValueKind.Null)
                {
                    if (vat.ValueKind != JsonValueKind.Number || !vat.TryGetInt32(out var rate) || !LaborVatRates.Contains(rate)) throw new JsonException("invalid labor VAT rate");
                    row["laborVatRate"] = rate;
                }
                rows.Add(row);
            }
        if (rows.Count == 0) throw new JsonException("no labor cost lines");
        return new JsonObject { ["laborCostDetails"] = rows }.ToJsonString();
    }

    // {collectionRequestDetails:[{id,orderLineId,boxQuantity,desi,shipmentCompany}]}: only logistics
    // carriers; n11 wants every line of the package in one request.
    private static string Collection(JsonElement root)
    {
        var rows = new JsonArray();
        if (root.TryGetProperty("collectionRequestDetails", out var details) && details.ValueKind == JsonValueKind.Array)
            foreach (var detail in details.EnumerateArray())
            {
                var lineId = Number(detail, "orderLineId");
                var boxes = Number(detail, "boxQuantity");
                var id = Text(detail, "id");
                var carrier = Text(detail, "shipmentCompany")?.ToUpperInvariant();
                if (lineId is null || boxes is not > 0 || id is null || carrier is null || !CollectionCarriers.Contains(carrier)
                    || !detail.TryGetProperty("desi", out var desiValue) || desiValue.ValueKind != JsonValueKind.Number || !desiValue.TryGetDecimal(out var desi) || desi <= 0)
                    throw new JsonException("invalid collection request line");
                rows.Add(new JsonObject { ["id"] = id, ["orderLineId"] = lineId, ["boxQuantity"] = boxes, ["desi"] = desi, ["shipmentCompany"] = carrier });
            }
        if (rows.Count == 0) throw new JsonException("no collection request lines");
        return new JsonObject { ["collectionRequestDetails"] = rows }.ToJsonString();
    }

    // n11 answers order updates per line ({content:[{lineId,status,reasons}]})
    // and splits with {code, message}. Returns the refused part, or null.
    public static string? RefusedLines(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        if (root.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
        {
            var refused = content.EnumerateArray()
                .Where(line => !string.Equals(Text(line, "status"), "SUCCESS", StringComparison.OrdinalIgnoreCase))
                .Select(line => $"{Text(line, "lineId") ?? "?"} satırı: {Text(line, "reasons") ?? Text(line, "status") ?? "neden belirtilmedi"}")
                .ToList();
            return refused.Count == 0 ? null : string.Join("; ", refused);
        }
        var code = Text(root, "code");
        if (code is not null && code != "200")
            return Text(root, "message") ?? $"kod {code}";
        return null;
    }

    private static string SplitGroups(IEnumerable<List<long>> groups) =>
        new JsonObject
        {
            ["splitGroups"] = new JsonArray(groups.Select(group => (JsonNode)new JsonObject
            {
                ["orderLineIds"] = new JsonArray(group.Select(id => (JsonNode)JsonValue.Create(id)).ToArray())
            }).ToArray())
        }.ToJsonString();

    private static List<long> LineIds(JsonElement root, string arrayName, string idName) =>
        root.TryGetProperty(arrayName, out var lines) && lines.ValueKind == JsonValueKind.Array
            ? lines.EnumerateArray().Select(line => Number(line, idName)).Where(id => id is not null).Select(id => id!.Value).Distinct().ToList()
            : [];

    private static List<long> Numbers(JsonElement root, string arrayName) =>
        root.TryGetProperty(arrayName, out var values) && values.ValueKind == JsonValueKind.Array
            ? values.EnumerateArray().Select(value => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), out var parsed) ? parsed : (long?)null)
                .Where(id => id is not null).Select(id => id!.Value).Distinct().ToList()
            : [];

    private static long? Number(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value)
            ? value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), out var parsed) ? parsed : null
            : null;

    private static string? Text(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var value)) return null;
        var text = value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.Array => string.Join(" ", value.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString())),
            _ => null
        };
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }
}
