using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using MarketplaceHub.Application;
using MarketplaceHub.Infrastructure.Adapters.Trendyol.ErrorMapping;

namespace MarketplaceHub.Infrastructure.Adapters.Trendyol;

// Read-only diagnostic of how getShipmentPackages answers for one order (resend packages, 11 Nov 2026). Only GET
// requests; each answer keeps its paging fields and the packages of the order (or of the named package ids), with the
// buyer's personal fields removed (TrendyolRawOrderRedactor).
public sealed partial class TrendyolHttpClient : IOrderRawReadPort
{
    internal const int MaxDiagnosticStreamPages = 10;

    public async Task<AdapterResult<IReadOnlyList<RawOrderRead>>> ReadOrderRawAsync(AdapterContext context, string orderNumber, IReadOnlyList<string> packageIds, DateTimeOffset orderedAt, DateTimeOffset streamStart, DateTimeOffset streamEnd, CancellationToken cancellationToken)
    {
        var authorized = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken);
        if (authorized is null) return AdapterResult<IReadOnlyList<RawOrderRead>>.Failure(TrendyolErrorMapper.Configuration());
        var sellerId = authorized.Connection.ExternalStoreId;
        var number = Uri.EscapeDataString(orderNumber.Trim());
        var now = timeProvider.GetUtcNow();
        // V2 takes at most two weeks per request.
        var start = orderedAt.AddDays(-1);
        var end = start.AddDays(14) < now ? start.AddDays(14) : now;
        string Ms(DateTimeOffset value) => value.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
        var queries = new List<(string Label, string Path)>
        {
            ("v2 orderNumber + dates", $"{TrendyolEndpoints.Orders(sellerId)}?startDate={Ms(start)}&endDate={Ms(end)}&orderNumber={number}&page=0&size=200"),
            ("v2 orderNumber", $"{TrendyolEndpoints.Orders(sellerId)}?orderNumber={number}&page=0&size=200"),
            ("v1 orders orderNumber", $"order/sellers/{Uri.EscapeDataString(sellerId)}/orders?orderNumber={number}&page=0&size=200"),
            ("claims orderNumber", $"order/sellers/{Uri.EscapeDataString(sellerId)}/claims?orderNumber={number}&page=0&size=50"),
        };
        if (packageIds.Count > 0)
            queries.Add(("v2 shipmentPackageIds + dates", $"{TrendyolEndpoints.Orders(sellerId)}?startDate={Ms(start)}&endDate={Ms(end)}&shipmentPackageIds={Uri.EscapeDataString(string.Join(',', packageIds))}&page=0&size=200"));
        var reads = new List<RawOrderRead>();
        foreach (var (label, path) in queries)
        {
            if (cancellationToken.IsCancellationRequested) break;
            var response = await SendAsync(authorized, HttpMethod.Get, path, null, cancellationToken);
            reads.Add(response.IsSuccess
                ? new(label, 200, null, TrendyolRawOrderRedactor.Keep(response.Value!, orderNumber, packageIds))
                : new(label, response.Error?.HttpStatus, response.Error?.Code, null));
        }

        // The stream around the moment the resend appeared, every page, keeping the order's packages only.
        string? cursor = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var page = 1; page <= MaxDiagnosticStreamPages && !cancellationToken.IsCancellationRequested; page++)
        {
            var path = $"{TrendyolEndpoints.OrderStream(sellerId)}?size=200&lastModifiedStartDate={Ms(streamStart)}&lastModifiedEndDate={Ms(streamEnd)}"
                + (cursor is null ? "" : "&nextCursor=" + Uri.EscapeDataString(cursor));
            var response = await SendAsync(authorized, HttpMethod.Get, path, null, cancellationToken);
            if (!response.IsSuccess)
            {
                reads.Add(new($"stream page {page}", response.Error?.HttpStatus, response.Error?.Code, null));
                break;
            }
            reads.Add(new($"stream page {page}", 200, null, TrendyolRawOrderRedactor.Keep(response.Value!, orderNumber, packageIds)));
            cursor = TrendyolRawOrderRedactor.NextCursor(response.Value!);
            if (cursor is null || !seen.Add(cursor)) break;
        }
        return AdapterResult<IReadOnlyList<RawOrderRead>>.Success(reads);
    }
}

internal static class TrendyolRawOrderRedactor
{
    // Buyer data never leaves the answer: addresses, names, e-mail, phone, identity and tax numbers.
    private static readonly string[] PersonalFragments = ["address", "customer", "email", "phone", "identity", "taxnumber", "taxoffice", "firstname", "lastname", "fullname", "company", "buyer"];

    internal static bool IsPersonal(string key)
    {
        var lower = key.ToLowerInvariant();
        return PersonalFragments.Any(fragment => lower.Contains(fragment, StringComparison.Ordinal));
    }

    // The answer's paging fields and the packages of the order or of one of the package ids, personal fields removed;
    // the number of packages the page held is kept as "packagesOnPage".
    public static string Keep(string json, string orderNumber, IReadOnlyList<string> packageIds)
    {
        JsonNode? root;
        try { root = JsonNode.Parse(json); }
        catch (JsonException) { return "{\"unparsable\":true}"; }
        if (root is not JsonObject rootObject) return "{\"notAnObject\":true}";
        var result = new JsonObject();
        foreach (var (key, value) in rootObject)
        {
            if (key == "content") continue;
            if (IsPersonal(key)) continue;
            result[key] = value?.DeepClone();
        }
        var kept = new JsonArray();
        var count = 0;
        if (rootObject["content"] is JsonArray content)
            foreach (var item in content)
            {
                count++;
                if (item is not JsonObject package) continue;
                var number = Scalar(package["orderNumber"]);
                var id = Scalar(package["shipmentPackageId"]) ?? Scalar(package["id"]);
                if (!string.Equals(number, orderNumber, StringComparison.Ordinal) && (id is null || !packageIds.Contains(id, StringComparer.Ordinal))) continue;
                kept.Add(Redact(package));
            }
        result["packagesOnPage"] = count;
        result["content"] = kept;
        return result.ToJsonString();
    }

    public static string? NextCursor(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (root.TryGetProperty("hasMore", out var more) && more.ValueKind == JsonValueKind.False) return null;
            return root.TryGetProperty("nextCursor", out var next) && next.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(next.GetString()) ? next.GetString() : null;
        }
        catch (JsonException) { return null; }
    }

    internal static JsonNode? Redact(JsonNode? node) => node switch
    {
        JsonObject obj => new JsonObject(obj.Where(pair => !IsPersonal(pair.Key)).Select(pair => KeyValuePair.Create(pair.Key, Redact(pair.Value)))),
        JsonArray array => new JsonArray(array.Select(Redact).ToArray()),
        _ => node?.DeepClone()
    };

    private static string? Scalar(JsonNode? node) => node is JsonValue value
        ? value.TryGetValue<string>(out var text) ? text : value.ToJsonString().Trim('"')
        : null;
}
