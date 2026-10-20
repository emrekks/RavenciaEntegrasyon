using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using MarketplaceHub.Application;

namespace MarketplaceHub.Infrastructure.Adapters.Pazarama;

// Order actions (phase 5). The panel sends one JSON body per action; they are turned into Pazarama's
// requests here:
//   PICKING (3 -> 12)    PUT  order/updateOrderStatus, one request per order item, status 12
//   NOT_SUPPLIED (13)    PUT  order/updateOrderStatus, one request per order item, status 13
//   SPLIT                POST order/api/packages-split         (packages of order items, optional address/carrier)
//   UPDATE_PACKAGE       PUT  order/update-packages            (change a package's address or carrier)
//   LABOR_COST           PUT  order/api/{orderId}/items/labor-costs
//   CANCEL_APPROVE/REJECT PUT order/api/cancel                 (waiting cancel requests of the order)
// With Pazarama contracted cargo the cargo process runs by itself after status 12; shipped (5) is never sent.
// An order moved to 12 and not handed to the carrier is refunded automatically and hurts the seller score,
// so every one of these is a deliberate user action behind the write gate.
public sealed partial class PazaramaHttpClient
{
    internal const string UpdateOrderStatus = "order/updateOrderStatus";
    internal const string PackagesSplit = "order/api/packages-split";
    internal const string UpdatePackages = "order/update-packages";
    internal const string CancelRequests = "order/api/cancel/items";
    internal const string CancelDecision = "order/api/cancel";

    internal static string LaborCosts(string orderId) => $"order/api/{Uri.EscapeDataString(orderId)}/items/labor-costs";

    public async Task<AdapterResult<PackageActionResult>> ExecutePackageActionAsync(AdapterContext context, PackageActionCommand command, CancellationToken cancellationToken)
    {
        var action = command.Action.Trim().ToUpperInvariant();
        if (!PazaramaPackageActionMapper.Actions.Contains(action, StringComparer.Ordinal))
            return NotSupported<PackageActionResult>("PAZARAMA_PACKAGE_ACTION_UNSUPPORTED", "Pazarama için yalnız siparişi işleme alma, tedarik edilemedi, paket bölme/güncelleme, işçilik bedeli ve iptal talebi kararı desteklenir; diğer işlemleri Pazarama satıcı panelinden yapın.");
        if (!PazaramaPackageActionMapper.TryOrderNumber(command.ExternalPackageId, out var orderNumber))
            return Failure<PackageActionResult>(AdapterErrorClass.Validation, "PAZARAMA_PACKAGE_ID_INVALID", "Pazarama paket numarası order:{sipariş numarası} biçiminde olmalıdır.", HttpStatusCode.UnprocessableEntity);
        if (PazaramaPackageActionMapper.Validate(action, command.PayloadJson) is { } invalid)
            return Failure<PackageActionResult>(AdapterErrorClass.Validation, "PAZARAMA_PACKAGE_ACTION_PAYLOAD_INVALID", invalid, HttpStatusCode.UnprocessableEntity);

        var account = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken);
        if (account is null) return CredentialMissing<PackageActionResult>();
        if (WriteBlocked<PackageActionResult>(account, context, "paket işlemi") is { } blocked) return blocked;

        if (action is "PICKING" or "NOT_SUPPLIED")
        {
            var status = action == "PICKING" ? PazaramaPackageActionMapper.PreparingStatus : PazaramaPackageActionMapper.NotSuppliedStatus;
            RateLimitMetadata? last = null;
            var done = 0;
            // A retry after a run that stopped part way finds the first items already moved; Pazarama refuses
            // to set a status an item already has. On a refusal the order is read once, and an item already in
            // the target status counts as done.
            IReadOnlyDictionary<string, string>? current = null;
            var targetStatus = PazaramaOrderMapper.ItemStatus(status);
            foreach (var (itemId, statusBody) in PazaramaPackageActionMapper.StatusUpdateTargets(orderNumber, command.PayloadJson, status))
            {
                var result = await SendAsync(account, HttpMethod.Put, UpdateOrderStatus, cancellationToken, Json(statusBody));
                last = result.RateLimit ?? last;
                if (!result.IsSuccess && result.Error!.Class == AdapterErrorClass.Validation)
                {
                    if (current is null)
                    {
                        var read = await GetAsync(context, orderNumber.ToString(CultureInfo.InvariantCulture), cancellationToken);
                        current = read.IsSuccess
                            ? read.Value!.Lines.GroupBy(line => line.ExternalLineId, StringComparer.OrdinalIgnoreCase).ToDictionary(group => group.Key, group => group.First().RawStatus ?? "", StringComparer.OrdinalIgnoreCase)
                            : new Dictionary<string, string>();
                    }
                    if (current.TryGetValue(itemId, out var itemStatus) && string.Equals(itemStatus, targetStatus, StringComparison.OrdinalIgnoreCase))
                    {
                        done++;
                        continue;
                    }
                }
                if (!result.IsSuccess)
                    return AdapterResult<PackageActionResult>.Failure(done == 0 ? result.Error! : result.Error! with { SafeMessage = $"{done} kalem işlendi, sonrası reddedildi: {result.Error!.SafeMessage}" }, result.RateLimit);
                result.Value!.Dispose();
                done++;
            }
            return AdapterResult<PackageActionResult>.Success(new(command.ExternalPackageId, "ACCEPTED", null), last);
        }

        if (action is "CANCEL_APPROVE" or "CANCEL_REJECT")
            return await DecideCancelRequestsAsync(account, command, orderNumber, action == "CANCEL_APPROVE" ? 2 : 3, cancellationToken);

        // The remaining actions are addressed to the order by its id (a GUID), which only the order read carries.
        var order = await GetAsync(context, orderNumber.ToString(CultureInfo.InvariantCulture), cancellationToken);
        if (!order.IsSuccess) return AdapterResult<PackageActionResult>.Failure(order.Error!, order.RateLimit);
        var orderId = PazaramaPackageActionMapper.OrderId(order.Value!.RawJson);
        if (orderId is null)
            return Failure<PackageActionResult>(AdapterErrorClass.ContractViolation, "PAZARAMA_ORDER_ID_MISSING", "Pazarama sipariş yanıtında sipariş kimliği (orderId) yok.", HttpStatusCode.BadGateway);

        (HttpMethod method, string path, string body) = action switch
        {
            "LABOR_COST" => (HttpMethod.Put, LaborCosts(orderId), PazaramaPackageActionMapper.LaborCostBody(command.PayloadJson)),
            "UPDATE_PACKAGE" => (HttpMethod.Put, UpdatePackages, PazaramaPackageActionMapper.PackagesBody(orderId, command.PayloadJson)),
            _ => (HttpMethod.Post, PackagesSplit, PazaramaPackageActionMapper.PackagesBody(orderId, command.PayloadJson))
        };
        var sent = await SendAsync(account, method, path, cancellationToken, new StringContent(body, System.Text.Encoding.UTF8, "application/json"));
        if (!sent.IsSuccess) return AdapterResult<PackageActionResult>.Failure(sent.Error!, sent.RateLimit);
        sent.Value!.Dispose();
        return AdapterResult<PackageActionResult>.Success(new(command.ExternalPackageId, "ACCEPTED", null), sent.RateLimit);
    }

    // Cancel requests carry refund ids; the waiting ones of the order are listed and decided one by one.
    private async Task<AdapterResult<PackageActionResult>> DecideCancelRequestsAsync(PazaramaRequestContext account, PackageActionCommand command, long orderNumber, int decision, CancellationToken cancellationToken)
    {
        var today = timeProvider.GetUtcNow().ToOffset(TurkeyOffset).Date;
        var listed = await SendAsync(account, HttpMethod.Post, CancelRequests, cancellationToken, Json(new
        {
            pageSize = 100,
            pageNumber = 1,
            refundStatus = (int?)null,
            requestStartDate = Day(today.AddMonths(-6)),
            requestEndDate = Day(today.AddDays(1)),
            orderNumber
        }));
        if (!listed.IsSuccess) return AdapterResult<PackageActionResult>.Failure(listed.Error!, listed.RateLimit);
        IReadOnlyList<string> refundIds;
        using (var document = listed.Value!)
        {
            try { refundIds = PazaramaPackageActionMapper.WaitingCancelRequests(document.RootElement, orderNumber); }
            catch (JsonException)
            {
                return Failure<PackageActionResult>(AdapterErrorClass.ContractViolation, "PAZARAMA_CANCEL_CONTRACT_INVALID", "Pazarama iptal talebi yanıtı beklenen yapıda değil.", HttpStatusCode.BadGateway);
            }
        }
        if (refundIds.Count == 0)
            return Failure<PackageActionResult>(AdapterErrorClass.Validation, "PAZARAMA_NO_WAITING_CANCEL_REQUEST", "Bu sipariş için karar bekleyen bir iptal talebi bulunamadı.", HttpStatusCode.UnprocessableEntity);

        RateLimitMetadata? last = listed.RateLimit;
        var done = 0;
        foreach (var refundId in refundIds)
        {
            var result = await SendAsync(account, HttpMethod.Put, CancelDecision, cancellationToken, Json(new { refundId, status = decision }));
            last = result.RateLimit ?? last;
            if (!result.IsSuccess)
                return AdapterResult<PackageActionResult>.Failure(done == 0 ? result.Error! : result.Error! with { SafeMessage = $"{done} iptal talebi işlendi, sonrası reddedildi: {result.Error!.SafeMessage}" }, result.RateLimit);
            result.Value!.Dispose();
            done++;
        }
        return AdapterResult<PackageActionResult>.Success(new(command.ExternalPackageId, "ACCEPTED", null), last);
    }
}

public static class PazaramaPackageActionMapper
{
    public const int PreparingStatus = 12;
    public const int NotSuppliedStatus = 13;

    public static readonly IReadOnlyList<string> Actions = ["PICKING", "NOT_SUPPLIED", "SPLIT", "UPDATE_PACKAGE", "LABOR_COST", "CANCEL_APPROVE", "CANCEL_REJECT"];

    // Packages of a Pazarama order are named "order:{orderNumber}".
    public static bool TryOrderNumber(string? packageId, out long orderNumber)
    {
        orderNumber = 0;
        var text = packageId?.Trim() ?? "";
        return text.StartsWith("order:", StringComparison.OrdinalIgnoreCase) && long.TryParse(text[6..], NumberStyles.None, CultureInfo.InvariantCulture, out orderNumber) && orderNumber > 0;
    }

    // Null when the body is acceptable for the action, otherwise the reason in the user's language.
    public static string? Validate(string action, string payloadJson)
    {
        try
        {
            if (JsonNode.Parse(string.IsNullOrWhiteSpace(payloadJson) ? "{}" : payloadJson) is not JsonObject root) return "Paket aksiyonu içeriği bir JSON nesnesi olmalıdır.";
            switch (action)
            {
                case "PICKING":
                case "NOT_SUPPLIED":
                    return LineIds(root).Count == 0 ? "İşlem için en az bir sipariş kalemi (lines[].lineId) gerekir." : null;
                case "SPLIT":
                case "UPDATE_PACKAGE":
                    return Packages(root) is { Count: > 0 } ? null : "En az bir paket ve her pakette en az bir sipariş kalemi (packages[].orderLineIds) gerekir.";
                case "LABOR_COST":
                    return LaborItems(root).Count == 0 ? "İşçilik bedeli için en az bir kalem (items[].orderLineId, laborCost, processCount) gerekir." : null;
                default:
                    return null;
            }
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException) { return "Paket aksiyonu geçerli JSON olmalıdır."; }
    }

    // {"lines":[{"lineId":"guid"}]}; line ids are order item GUIDs.
    public static IReadOnlyList<string> LineIds(JsonObject root) =>
        (root["lines"] as JsonArray ?? [])
            .Select(line => line is JsonObject item ? Guid(item["lineId"]) : Guid(line))
            .Where(id => id is not null)
            .Select(id => id!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    public static IEnumerable<object> StatusUpdates(long orderNumber, string payloadJson, int status) =>
        StatusUpdateTargets(orderNumber, payloadJson, status).Select(target => target.Body);

    // Each item with the body that moves it to the status.
    public static IEnumerable<(string ItemId, object Body)> StatusUpdateTargets(long orderNumber, string payloadJson, int status) =>
        LineIds((JsonNode.Parse(payloadJson) as JsonObject)!).Select(id => (id, (object)new { orderNumber, item = new { orderItemId = id, status } }));

    public static string? OrderId(string orderRawJson)
    {
        try
        {
            using var document = JsonDocument.Parse(orderRawJson);
            return document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("orderId", out var value) && value.ValueKind == JsonValueKind.String && System.Guid.TryParse(value.GetString(), out var id) ? id.ToString() : null;
        }
        catch (JsonException) { return null; }
    }

    // {"packages":[{"orderLineIds":["guid"],"sellerAddressId":"guid?","cargoCompanyId":"guid?"}]}
    // (the Pazarama field name apiOrderItemPackages / orderItemIds is accepted as well).
    public static IReadOnlyList<(IReadOnlyList<string> Items, string? SellerAddressId, string? CargoCompanyId)> Packages(JsonObject root)
    {
        var packages = new List<(IReadOnlyList<string>, string?, string?)>();
        foreach (var node in (root["packages"] as JsonArray ?? root["apiOrderItemPackages"] as JsonArray ?? []))
        {
            if (node is not JsonObject package) return [];
            var ids = (package["orderLineIds"] as JsonArray ?? package["orderItemIds"] as JsonArray ?? []).Select(Guid).ToList();
            if (ids.Count == 0 || ids.Any(id => id is null)) return [];
            packages.Add((ids.Select(id => id!).Distinct(StringComparer.OrdinalIgnoreCase).ToList(), OptionalGuid(package["sellerAddressId"]), OptionalGuid(package["cargoCompanyId"])));
        }
        return packages;
    }

    public static string PackagesBody(string orderId, string payloadJson)
    {
        var root = (JsonNode.Parse(payloadJson) as JsonObject)!;
        return new JsonObject
        {
            ["orderId"] = orderId,
            ["apiOrderItemPackages"] = new JsonArray(Packages(root).Select(package => (JsonNode)new JsonObject
            {
                ["orderItemIds"] = new JsonArray(package.Items.Select(id => (JsonNode)JsonValue.Create(id)!).ToArray()),
                // An empty address or carrier id lets Pazarama use the seller's defaults.
                ["sellerAddressId"] = package.SellerAddressId,
                ["cargoCompanyId"] = package.CargoCompanyId
            }).ToArray())
        }.ToJsonString();
    }

    public static IReadOnlyList<(string OrderItemId, int LaborCost, int ProcessCount)> LaborItems(JsonObject root)
    {
        var items = new List<(string, int, int)>();
        foreach (var node in root["items"] as JsonArray ?? [])
        {
            if (node is not JsonObject item) return [];
            var id = Guid(item["orderLineId"] ?? item["orderItemId"]);
            if (id is null || item["laborCost"] is not JsonValue cost || !cost.TryGetValue<int>(out var laborCost) || laborCost < 0) return [];
            var processCount = item["processCount"] is JsonValue count && count.TryGetValue<int>(out var parsed) && parsed >= 0 ? parsed : 0;
            items.Add((id, laborCost, processCount));
        }
        return items;
    }

    public static string LaborCostBody(string payloadJson) => new JsonObject
    {
        ["items"] = new JsonArray(LaborItems((JsonNode.Parse(payloadJson) as JsonObject)!)
            .Select(item => (JsonNode)new JsonObject { ["orderItemId"] = item.OrderItemId, ["laborCost"] = item.LaborCost, ["processCount"] = item.ProcessCount })
            .ToArray())
    }.ToJsonString();

    // Refund ids of the requests of this order that still wait for a decision (refundStatus 1).
    public static IReadOnlyList<string> WaitingCancelRequests(JsonElement root, long orderNumber)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            throw new JsonException("Pazarama cancel response has no data object.");
        if (!data.TryGetProperty("refundList", out var list) || list.ValueKind != JsonValueKind.Array) return [];
        var ids = new List<string>();
        foreach (var row in list.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object) continue;
            var number = row.TryGetProperty("orderNumber", out var n) && n.ValueKind == JsonValueKind.Number && n.TryGetInt64(out var parsedNumber) ? parsedNumber : 0;
            var status = row.TryGetProperty("refundStatus", out var s) && s.ValueKind == JsonValueKind.Number && s.TryGetInt32(out var parsedStatus) ? parsedStatus : 0;
            var id = row.TryGetProperty("refundId", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString()?.Trim() : null;
            if (number == orderNumber && status == 1 && !string.IsNullOrWhiteSpace(id) && !ids.Contains(id, StringComparer.OrdinalIgnoreCase)) ids.Add(id);
        }
        return ids;
    }

    private static string? Guid(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) && System.Guid.TryParse(text, out var id) ? id.ToString() : null;

    private static string? OptionalGuid(JsonNode? node) => node is null || node is JsonValue value && value.TryGetValue<string>(out var text) && string.IsNullOrWhiteSpace(text) ? null : Guid(node);
}
