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
// n11 approves only Created lines and splits only Picking packages; the n11
// answer carries the reason when a line is refused.
public sealed partial class N11HttpClient
{
    internal const string OrderUpdate = "rest/order/v1/update";
    internal const string SplitPackage = "rest/delivery/v1/splitCombinePackage";

    public async Task<AdapterResult<PackageActionResult>> ExecutePackageActionAsync(AdapterContext context, PackageActionCommand command, CancellationToken cancellationToken)
    {
        var action = command.Action.Trim().ToUpperInvariant();
        string? body;
        try { body = N11PackageActionMapper.Body(action, command.PayloadJson); }
        catch (JsonException) { return Failure<PackageActionResult>(AdapterErrorClass.Validation, "N11_PACKAGE_ACTION_PAYLOAD_INVALID", "N11 paket işlemi için sipariş satırları okunamadı.", HttpStatusCode.UnprocessableEntity); }
        if (body is null)
            return NotSupported<PackageActionResult>("N11_PACKAGE_ACTION_UNSUPPORTED", "N11 API'si yalnız siparişi işleme almayı ve paket bölmeyi destekliyor; bu işlemi N11 satıcı panelinden yapın.");

        var account = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken);
        if (account is null) return CredentialMissing<PackageActionResult>();
        if (!ConnectionWritesEnabled(account.Connection.SettingsJson))
            return Failure<PackageActionResult>(AdapterErrorClass.Validation, "N11_EXTERNAL_WRITES_DISABLED", "N11 bağlantısında dış yazma kapalı; paket işlemi gönderilmedi.", HttpStatusCode.Forbidden);
        var result = await SendAsync(account, action == "PICKING" ? HttpMethod.Put : HttpMethod.Post, action == "PICKING" ? OrderUpdate : SplitPackage, cancellationToken,
            new StringContent(body, Encoding.UTF8, "application/json"));
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
            default:
                return null;
        }
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
