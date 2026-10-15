using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using MarketplaceHub.Application;

namespace MarketplaceHub.Infrastructure.Adapters.Pazarama;

// Price and stock writes (phase 4). Pazarama takes price and stock as two separate requests, each
// keyed by barcode, at least 10 s apart per seller and at most 3000 products per request. Each
// request answers with a data id (a GUID) and the per-barcode result is read later from
// listing-state/batch-id/{dataId}/lake-projections, where it can take hours to appear.
// The shared port carries one operation id, so the two data ids travel together as
// "p:{priceDataId}:{count}|s:{stockDataId}:{count}".
public sealed partial class PazaramaHttpClient : IInventoryPricePort
{
    internal const string PriceUpdate = "product/updatePrice-v2";
    internal const string StockUpdate = "product/updateStock-v2";
    // The second request waits for the shared 10 s price/stock slot inside the HTTP pipeline, so it
    // gets more time than an ordinary call before it counts as timed out.
    private static readonly TimeSpan SpacedRequestTimeout = TimeSpan.FromSeconds(90);

    internal static string BatchResults(string dataId, int page, int size) =>
        $"listing-state/batch-id/{Uri.EscapeDataString(dataId)}/lake-projections?page={page.ToString(CultureInfo.InvariantCulture)}&pageSize={size.ToString(CultureInfo.InvariantCulture)}";

    public async Task<AdapterResult<RemoteOperationRef>> PushPriceAndInventoryAsync(AdapterContext context, string payloadJson, CancellationToken cancellationToken)
    {
        var bodies = PazaramaPriceInventoryRules.Split(payloadJson);
        if (bodies is null)
            return Failure<RemoteOperationRef>(AdapterErrorClass.Validation, "PAZARAMA_PRICE_STOCK_PAYLOAD_INVALID", $"Pazarama fiyat-stok içeriği beklenen yapıda değil (en fazla {PazaramaPriceInventoryRules.MaxItemsPerRequest} ürün, barkod ve geçerli değerler).", HttpStatusCode.UnprocessableEntity);
        var account = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken);
        if (account is null) return CredentialMissing<RemoteOperationRef>();
        if (WriteBlocked<RemoteOperationRef>(account, "fiyat-stok") is { } blocked) return blocked;

        var parts = new List<string>(2);
        RateLimitMetadata? rate = null;
        foreach (var (kind, path, body, count) in new[] { ("p", PriceUpdate, bodies.Price, bodies.PriceCount), ("s", StockUpdate, bodies.Stock, bodies.StockCount) })
        {
            if (count == 0) continue;
            var result = await SendAsync(account, HttpMethod.Post, path, cancellationToken, new StringContent(body, System.Text.Encoding.UTF8, "application/json"), SpacedRequestTimeout);
            rate = result.RateLimit ?? rate;
            if (!result.IsSuccess) return AdapterResult<RemoteOperationRef>.Failure(result.Error!, result.RateLimit);
            using var document = result.Value!;
            var dataId = Data(document.RootElement, out var data) && data.ValueKind == JsonValueKind.String ? data.GetString()?.Trim() : null;
            if (string.IsNullOrWhiteSpace(dataId))
                return Failure<RemoteOperationRef>(AdapterErrorClass.ContractViolation, "PAZARAMA_BATCH_ID_MISSING", "Pazarama fiyat-stok yanıtında işlem numarası (dataId) yok.", HttpStatusCode.BadGateway);
            parts.Add($"{kind}:{dataId}:{count.ToString(CultureInfo.InvariantCulture)}");
        }
        return AdapterResult<RemoteOperationRef>.Success(new(string.Join('|', parts), "PRICE_STOCK_UPDATE", timeProvider.GetUtcNow()), rate);
    }

    public async Task<AdapterResult<RemoteOperationStatus>> GetOperationAsync(AdapterContext context, string externalOperationId, CancellationToken cancellationToken)
    {
        if (externalOperationId.StartsWith("b:", StringComparison.Ordinal) || externalOperationId.StartsWith("i:", StringComparison.Ordinal))
            return await GetProductOperationAsync(context, externalOperationId, cancellationToken);
        var parts = PazaramaBatchMapper.ParseOperation(externalOperationId);
        if (parts.Count == 0)
            return Failure<RemoteOperationStatus>(AdapterErrorClass.Validation, "PAZARAMA_OPERATION_ID_INVALID", "Pazarama işlem numarası geçersiz.", HttpStatusCode.UnprocessableEntity);
        var account = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken);
        if (account is null) return CredentialMissing<RemoteOperationStatus>();

        var results = new List<(PazaramaBatchMapper.BatchPart Part, IReadOnlyList<PazaramaBatchMapper.BatchItem> Items)>();
        RateLimitMetadata? rate = null;
        foreach (var part in parts)
        {
            var items = new List<PazaramaBatchMapper.BatchItem>();
            for (var page = 1; page <= PazaramaBatchMapper.MaxPages; page++)
            {
                var result = await SendAsync(account, HttpMethod.Get, BatchResults(part.DataId, page, PazaramaBatchMapper.PageSize), cancellationToken);
                rate = result.RateLimit ?? rate;
                if (!result.IsSuccess) return AdapterResult<RemoteOperationStatus>.Failure(result.Error!, result.RateLimit);
                using var document = result.Value!;
                try
                {
                    var read = PazaramaBatchMapper.Page(document.RootElement);
                    var before = items.Count;
                    items.AddRange(read.Items.Where(item => items.All(known => !string.Equals(known.Code, item.Code, StringComparison.OrdinalIgnoreCase))));
                    // Stop when everything announced arrived, or when a page brought nothing new.
                    if (items.Count >= (read.TotalCount ?? 0) || items.Count == before) break;
                }
                catch (JsonException)
                {
                    return Failure<RemoteOperationStatus>(AdapterErrorClass.ContractViolation, "PAZARAMA_BATCH_CONTRACT_INVALID", "Pazarama fiyat-stok sonuç yanıtı beklenen yapıda değil.", HttpStatusCode.BadGateway);
                }
            }
            results.Add((part, items));
        }
        return AdapterResult<RemoteOperationStatus>.Success(PazaramaBatchMapper.Merge(externalOperationId, results), rate);
    }
}

public static class PazaramaPriceInventoryRules
{
    public const int MaxItemsPerRequest = 3000;
    public const int MaxCodeLength = 100;

    public sealed record SplitBodies(string Price, int PriceCount, string Stock, int StockCount);

    public static decimal Price(decimal value) => decimal.Round(value, 2, MidpointRounding.AwayFromZero);

    public static int Quantity(decimal available) => (int)Math.Clamp(decimal.Floor(Math.Max(0, available)), 0, int.MaxValue);

    // The composer writes {"price":[{code,listPrice,salePrice}],"stock":[{code,stockCount}]}; each list
    // becomes the body of its own request. Null when the shape, the counts or a value is not acceptable.
    // Pazarama refuses a request whose price (or stock) equals what it already holds, so a barcode goes into the
    // price list only when its price changed and into the stock list only when its stock changed.
    public static string Payload(IReadOnlyList<(PriceInventoryPushLine Line, bool Price, bool Stock)> lines)
    {
        var price = new JsonArray();
        var stock = new JsonArray();
        foreach (var (line, priceChanged, stockChanged) in lines)
        {
            var sale = Price(line.SalePrice);
            if (priceChanged) price.Add(new JsonObject { ["code"] = line.Barcode, ["listPrice"] = Price(Math.Max(line.ListPrice, sale)), ["salePrice"] = sale });
            if (stockChanged) stock.Add(new JsonObject { ["code"] = line.Barcode, ["stockCount"] = Quantity(line.Quantity) });
        }
        return new JsonObject { ["price"] = price, ["stock"] = stock }.ToJsonString();
    }

    // The hash that marks a price as sent; the composer and the catalog mapping must agree on it.
    public static string PriceHash(decimal listPrice, decimal salePrice, long priceVersion) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"{listPrice:0.00}|{salePrice:0.00}|TRY|{priceVersion}")));

    public static SplitBodies? Split(string payloadJson)
    {
        try
        {
            if (JsonNode.Parse(payloadJson) is not JsonObject root) return null;
            var price = root["price"] as JsonArray ?? [];
            var stock = root["stock"] as JsonArray ?? [];
            if (price.Count == 0 && stock.Count == 0 || price.Count > MaxItemsPerRequest || stock.Count > MaxItemsPerRequest) return null;
            if (!price.All(PriceItemValid) || !stock.All(StockItemValid)) return null;
            return new(new JsonObject { ["items"] = price.DeepClone() }.ToJsonString(), price.Count, new JsonObject { ["items"] = stock.DeepClone() }.ToJsonString(), stock.Count);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException) { return null; }
    }

    private static bool CodeValid(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var code) && !string.IsNullOrWhiteSpace(code) && code.Trim().Length <= MaxCodeLength;

    private static bool PriceItemValid(JsonNode? node) =>
        node is JsonObject item && CodeValid(item["code"])
        && item["salePrice"] is JsonValue sale && sale.TryGetValue<decimal>(out var salePrice) && salePrice > 0
        && item["listPrice"] is JsonValue list && list.TryGetValue<decimal>(out var listPrice) && listPrice >= salePrice;

    private static bool StockItemValid(JsonNode? node) =>
        node is JsonObject item && CodeValid(item["code"]) && item["stockCount"] is JsonValue count && count.TryGetValue<int>(out var quantity) && quantity >= 0;
}

public static class PazaramaBatchMapper
{
    private static readonly CultureInfo Turkish = new("tr-TR");
    public const int PageSize = 1000;
    public const int MaxPages = 10;

    public sealed record BatchPart(string Kind, string DataId, int ExpectedCount);

    public sealed record BatchItem(string Code, BatchState State, string? Detail);

    public sealed record BatchPage(IReadOnlyList<BatchItem> Items, int? TotalCount);

    public enum BatchState { Processing, Succeeded, Failed }

    // "p:{id}:{count}|s:{id}:{count}"; anything else is not an operation this adapter created.
    public static IReadOnlyList<BatchPart> ParseOperation(string? operationId)
    {
        var parts = new List<BatchPart>();
        foreach (var raw in (operationId ?? "").Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var fields = raw.Split(':');
            if (fields.Length != 3 || fields[0] is not ("p" or "s") || fields[1].Length == 0 || !int.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out var count) || count <= 0) return [];
            parts.Add(new(fields[0], fields[1], count));
        }
        return parts;
    }

    // data.data is the list, data.totalCount the number of results Pazarama has for the batch so far.
    public static BatchPage Page(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            throw new JsonException("Pazarama batch result has no data object.");
        if (!data.TryGetProperty("data", out var rows) || rows.ValueKind != JsonValueKind.Array)
            throw new JsonException("Pazarama batch result has no item list.");
        var items = new List<BatchItem>();
        foreach (var row in rows.EnumerateArray())
        {
            var code = Text(row, "code");
            if (code is null) continue;
            items.Add(new(code, State(row), Detail(row)));
        }
        return new(items, data.TryGetProperty("totalCount", out var total) && total.ValueKind == JsonValueKind.Number && total.TryGetInt32(out var count) ? count : null);
    }

    // One barcode's outcome. Price and stock carry their own status (0 done, 1 not completed, 2 error,
    // 3 processing, 5 sent to approval - a price change past the discount limit waits for Pazarama
    // staff, the value itself was accepted). Without those, the overall status text decides.
    internal static BatchState State(JsonElement row)
    {
        var statuses = new List<int>();
        foreach (var section in new[] { "price", "stock" })
            if (row.TryGetProperty(section, out var part) && part.ValueKind == JsonValueKind.Object && part.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.Number && status.TryGetInt32(out var value))
                statuses.Add(value);
        if (statuses.Count > 0)
            return statuses.Any(x => x == 3) ? BatchState.Processing : statuses.All(x => x is 0 or 5) ? BatchState.Succeeded : BatchState.Failed;
        return (Text(row, "operationStatusText") ?? "").ToLower(Turkish) switch
        {
            "başarılı" or "onaya gönderildi" => BatchState.Succeeded,
            "işleniyor" => BatchState.Processing,
            _ => BatchState.Failed
        };
    }

    private static string? Detail(JsonElement row)
    {
        foreach (var section in new[] { "price", "stock" })
            if (row.TryGetProperty(section, out var part) && part.ValueKind == JsonValueKind.Object && Text(part, "operationDetail") is { } detail) return detail.Length <= 300 ? detail : detail[..300];
        return Text(row, "operationStatusText");
    }

    // Everything announced must have arrived and nothing may still be processing; only then is the
    // operation complete. A barcode succeeds when its price and its stock result both did.
    public static RemoteOperationStatus Merge(string operationId, IReadOnlyList<(BatchPart Part, IReadOnlyList<BatchItem> Items)> results)
    {
        foreach (var (part, items) in results)
            if (items.Count < part.ExpectedCount || items.Any(item => item.State == BatchState.Processing))
                return new(operationId, "IN_PROGRESS", []);

        var lines = new Dictionary<string, RemoteOperationLine>(StringComparer.OrdinalIgnoreCase);
        foreach (var (part, items) in results)
            foreach (var item in items)
            {
                var failed = item.State == BatchState.Failed;
                var code = part.Kind == "p" ? "PAZARAMA_PRICE_REJECTED" : "PAZARAMA_STOCK_REJECTED";
                if (!lines.TryGetValue(item.Code, out var existing))
                    lines[item.Code] = new(item.Code, !failed, null, failed ? code : null, false, item.Detail);
                else if (failed && existing.Succeeded)
                    lines[item.Code] = existing with { Succeeded = false, ErrorCode = code, Status = item.Detail };
            }
        return new(operationId, "COMPLETED", lines.Values.ToList());
    }

    private static string? Text(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var value)) return null;
        var text = value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null
        };
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }
}
