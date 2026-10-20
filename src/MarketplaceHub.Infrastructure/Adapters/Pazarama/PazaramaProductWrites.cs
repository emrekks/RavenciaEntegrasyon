using System.Buffers.Text;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using MarketplaceHub.Application;

namespace MarketplaceHub.Infrastructure.Adapters.Pazarama;

// Product writes (phase 9): create (POST product/create, results by batch id), sale open/close
// (POST product/external-status) and the publication state lookup. Pazarama has no service to edit a
// listing's content, so the update and archive-as-delete operations stay unsupported.
//
// Operation ids carry what the poll needs, so any process (API or worker) can answer it:
// "b:{batchRequestId}:{count}:{barcodes}" for a create batch polled with getProductBatchResult (the barcodes
// let a finished batch be reported line by line when Pazarama lists only the failures), and
// "c:{lines}" for an answer Pazarama gave at once (a single product create answers without a batch id;
// sale open/close answers per barcode). {barcodes} and {lines} are base64url JSON. Ids from before this
// encoding ("b:{id}:{count}" and "i:{guid}:{count}") are still read; an "i:" result was only in memory.
public sealed partial class PazaramaHttpClient
{
    internal const string ProductCreate = "product/create";
    internal const string ProductBatchResult = "product/getProductBatchResult";
    internal const string SaleStatus = "product/external-status";

    public async Task<AdapterResult<RemoteOperationRef>> CreateAsync(AdapterContext context, ProductPublication publication, CancellationToken cancellationToken)
    {
        var codes = PazaramaProductPayloadRules.Validate(publication.PayloadJson, out var error);
        if (codes is null)
            return Failure<RemoteOperationRef>(AdapterErrorClass.Validation, "PAZARAMA_PRODUCT_PAYLOAD_INVALID", error ?? "Pazarama ürün açma içeriği beklenen yapıda değil.", HttpStatusCode.UnprocessableEntity);
        var account = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken);
        if (account is null) return CredentialMissing<RemoteOperationRef>();
        if (WriteBlocked<RemoteOperationRef>(account, context, "ürün açma isteği") is { } blocked) return blocked;

        var result = await SendAsync(account, HttpMethod.Post, ProductCreate, cancellationToken, new StringContent(publication.PayloadJson, System.Text.Encoding.UTF8, "application/json"));
        if (!result.IsSuccess) return AdapterResult<RemoteOperationRef>.Failure(result.Error!, result.RateLimit);
        using var document = result.Value!;
        var created = PazaramaProductWriteMapper.Created(document.RootElement);
        if (created.Error is not null)
            return Failure<RemoteOperationRef>(AdapterErrorClass.Validation, "PAZARAMA_PRODUCT_CREATE_REJECTED", $"Pazarama ürün açma isteğini reddetti: {created.Error}", HttpStatusCode.UnprocessableEntity);
        var now = timeProvider.GetUtcNow();
        if (created.BatchId is null)
        {
            // A single product is answered without a batch: the accepted answer is the result.
            var inline = PazaramaOperationId.Completed(codes.Select(code => new RemoteOperationLine(code, true, null, null, false, "Ürün ekleme isteği iletildi.")).ToList());
            return AdapterResult<RemoteOperationRef>.Success(new(inline, "PRODUCT_CREATE", now), result.RateLimit);
        }
        return AdapterResult<RemoteOperationRef>.Success(new(PazaramaOperationId.Batch(created.BatchId, codes), "PRODUCT_CREATE", now), result.RateLimit);
    }

    // The result of a create batch or of a sale status change.
    private async Task<AdapterResult<RemoteOperationStatus>> GetProductOperationAsync(AdapterContext context, string operationId, CancellationToken cancellationToken)
    {
        if (operationId.StartsWith("i:", StringComparison.Ordinal))
            return Failure<RemoteOperationStatus>(AdapterErrorClass.NotFound, "PAZARAMA_OPERATION_FORGOTTEN", "Pazarama işleminin sonucu bellekte kalmadı; ürünü Pazarama'dan yeniden okuyarak kontrol edin.", HttpStatusCode.NotFound);
        if (operationId.StartsWith("c:", StringComparison.Ordinal))
            return PazaramaOperationId.TryCompletedLines(operationId, out var lines)
                ? AdapterResult<RemoteOperationStatus>.Success(new(operationId, "COMPLETED", lines))
                : Failure<RemoteOperationStatus>(AdapterErrorClass.Validation, "PAZARAMA_OPERATION_ID_INVALID", "Pazarama işlem numarası geçersiz.", HttpStatusCode.UnprocessableEntity);

        if (!PazaramaOperationId.TryBatch(operationId, out var batchId, out var codes))
            return Failure<RemoteOperationStatus>(AdapterErrorClass.Validation, "PAZARAMA_OPERATION_ID_INVALID", "Pazarama işlem numarası geçersiz.", HttpStatusCode.UnprocessableEntity);
        var account = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken);
        if (account is null) return CredentialMissing<RemoteOperationStatus>();
        var result = await SendAsync(account, HttpMethod.Get, $"{ProductBatchResult}?BatchRequestId={batchId}", cancellationToken);
        if (!result.IsSuccess) return AdapterResult<RemoteOperationStatus>.Failure(result.Error!, result.RateLimit);
        using var document = result.Value!;
        try { return AdapterResult<RemoteOperationStatus>.Success(PazaramaProductWriteMapper.BatchStatus(document.RootElement, operationId, codes), result.RateLimit); }
        catch (JsonException)
        {
            return Failure<RemoteOperationStatus>(AdapterErrorClass.ContractViolation, "PAZARAMA_BATCH_CONTRACT_INVALID", "Pazarama ürün işlem sonucu beklenen yapıda değil.", HttpStatusCode.BadGateway);
        }
    }

    // Publication state by barcode: approved list first, then the unapproved one.
    public async Task<AdapterResult<RemotePublicationStatus>> GetPublicationStatusAsync(AdapterContext context, string barcode, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(barcode))
            return Failure<RemotePublicationStatus>(AdapterErrorClass.Validation, "PAZARAMA_BARCODE_REQUIRED", "Pazarama ürün durumu için barkod gerekir.", HttpStatusCode.UnprocessableEntity);
        var account = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken);
        if (account is null) return CredentialMissing<RemotePublicationStatus>();
        var code = barcode.Trim();
        var filter = new ProductReadFilter(null, Barcode: code);
        foreach (var (path, pending) in new[] { (ApprovedPage(null, 1, filter), false), (UnapprovedPage(1, null, 1, filter), true) })
        {
            var result = await SendAsync(account, HttpMethod.Get, path, cancellationToken);
            if (!result.IsSuccess) return AdapterResult<RemotePublicationStatus>.Failure(result.Error!, result.RateLimit);
            using var document = result.Value!;
            try
            {
                var page = PazaramaProductMapper.Page(document.RootElement, pending);
                var item = FindItem(document.RootElement, code);
                if (page.Items.Count > 0 && item is { } found)
                    return AdapterResult<RemotePublicationStatus>.Success(PazaramaProductWriteMapper.Publication(found, code, pending), result.RateLimit);
            }
            catch (JsonException)
            {
                return Failure<RemotePublicationStatus>(AdapterErrorClass.ContractViolation, "PAZARAMA_PRODUCT_CONTRACT_INVALID", "Pazarama ürün yanıtı beklenen yapıda değil.", HttpStatusCode.BadGateway);
            }
        }
        return AdapterResult<RemotePublicationStatus>.Success(new(code, "NOT_FOUND", null, null, null, "{}"));
    }

    private static JsonElement? FindItem(JsonElement root, string code)
    {
        if (!root.TryGetProperty("data", out var data)) return null;
        var rows = data.ValueKind == JsonValueKind.Array ? data
            : data.ValueKind == JsonValueKind.Object ? new[] { "sellerProducts", "items", "products", "data" }.Select(name => data.TryGetProperty(name, out var list) ? list : default).FirstOrDefault(list => list.ValueKind == JsonValueKind.Array)
            : default;
        if (rows.ValueKind != JsonValueKind.Array) return null;
        foreach (var row in rows.EnumerateArray())
            if (row.ValueKind == JsonValueKind.Object && row.TryGetProperty("code", out var value) && value.ValueKind == JsonValueKind.String && string.Equals(value.GetString()?.Trim(), code, StringComparison.OrdinalIgnoreCase))
                return row.Clone();
        return null;
    }

    // Sale open/close: {"items":[{"barcode":"...","archived":true}]}; archived closes the product for sale (10),
    // not archived opens it (1). Pazarama answers each barcode at once.
    public async Task<AdapterResult<RemoteOperationRef>> ArchiveAsync(AdapterContext context, string payloadJson, CancellationToken cancellationToken)
    {
        var changes = PazaramaProductWriteMapper.SaleChanges(payloadJson);
        if (changes is null)
            return Failure<RemoteOperationRef>(AdapterErrorClass.Validation, "PAZARAMA_SALE_STATUS_PAYLOAD_INVALID", "Satışa açma/kapatma içeriği beklenen yapıda değil (1-100 barkod).", HttpStatusCode.UnprocessableEntity);
        var account = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken);
        if (account is null) return CredentialMissing<RemoteOperationRef>();
        if (WriteBlocked<RemoteOperationRef>(account, context, "satışa açma/kapatma") is { } blocked) return blocked;

        var lines = new List<RemoteOperationLine>();
        RateLimitMetadata? rate = null;
        foreach (var (code, close) in changes)
        {
            var result = await SendAsync(account, HttpMethod.Post, SaleStatus, cancellationToken, Json(new { productItem = new { code, productStatus = close ? PazaramaProductWriteMapper.ClosedStatus : PazaramaProductWriteMapper.OpenStatus } }));
            rate = result.RateLimit ?? rate;
            if (!result.IsSuccess)
            {
                if (lines.Count == 0) return AdapterResult<RemoteOperationRef>.Failure(result.Error!, result.RateLimit);
                lines.Add(new(code, false, null, "PAZARAMA_SALE_STATUS_REJECTED", false, result.Error!.SafeMessage));
                continue;
            }
            using var document = result.Value!;
            lines.Add(PazaramaProductWriteMapper.SaleResult(document.RootElement, code));
        }
        return AdapterResult<RemoteOperationRef>.Success(new(PazaramaOperationId.Completed(lines), "SALE_STATUS", timeProvider.GetUtcNow()), rate);
    }

    public Task<AdapterResult<RemoteOperationRef>> UpdateUnapprovedAsync(AdapterContext context, ProductUpdatePublication publication, CancellationToken cancellationToken) => ProductWriteUnsupported();
    public Task<AdapterResult<RemoteOperationRef>> UpdateApprovedContentAsync(AdapterContext context, ProductUpdatePublication publication, CancellationToken cancellationToken) => ProductWriteUnsupported();
    public Task<AdapterResult<RemoteOperationRef>> UpdateApprovedVariantsAsync(AdapterContext context, ProductUpdatePublication publication, CancellationToken cancellationToken) => ProductWriteUnsupported();
    public Task<AdapterResult<RemoteOperationRef>> UpdateApprovedDeliveryAsync(AdapterContext context, ProductUpdatePublication publication, CancellationToken cancellationToken) => ProductWriteUnsupported();
}

// Product write operation ids that carry their own result or barcodes (see PazaramaHttpClient above).
public static class PazaramaOperationId
{
    public static string Completed(IReadOnlyList<RemoteOperationLine> lines) =>
        "c:" + Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(lines));

    public static string Batch(string batchId, IReadOnlyList<string> codes) =>
        $"b:{batchId}:{codes.Count.ToString(CultureInfo.InvariantCulture)}:{Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(codes))}";

    public static bool TryCompletedLines(string operationId, out IReadOnlyList<RemoteOperationLine> lines)
    {
        lines = [];
        if (!operationId.StartsWith("c:", StringComparison.Ordinal)) return false;
        try
        {
            if (JsonSerializer.Deserialize<List<RemoteOperationLine>>(Base64Url.DecodeFromChars(operationId.AsSpan(2))) is not { } decoded) return false;
            lines = decoded;
            return true;
        }
        catch (Exception exception) when (exception is FormatException or JsonException) { return false; }
    }

    // codes is null for an id from before the barcodes were encoded ("b:{id}:{count}").
    public static bool TryBatch(string operationId, out Guid batchId, out IReadOnlyList<string>? codes)
    {
        batchId = Guid.Empty;
        codes = null;
        var parts = operationId.Split(':');
        if (parts.Length is not (3 or 4) || parts[0] != "b" || !Guid.TryParse(parts[1], out batchId)) return false;
        if (parts.Length == 3) return true;
        try
        {
            codes = JsonSerializer.Deserialize<List<string>>(Base64Url.DecodeFromChars(parts[3]));
            return codes is not null;
        }
        catch (Exception exception) when (exception is FormatException or JsonException) { return false; }
    }
}

public static class PazaramaProductPayloadRules
{
    public const int MaxProductsPerRequest = 500;

    // The barcodes of a create body, or null (and the reason) when Pazarama would refuse it.
    public static IReadOnlyList<string>? Validate(string payloadJson, out string? error)
    {
        error = null;
        try
        {
            if (JsonNode.Parse(payloadJson) is not JsonObject root || root["products"] is not JsonArray products || products.Count is < 1 or > MaxProductsPerRequest)
            {
                error = $"Ürün açma isteği 1-{MaxProductsPerRequest} ürün içermelidir.";
                return null;
            }
            var codes = new List<string>(products.Count);
            foreach (var node in products)
            {
                if (node is not JsonObject product) { error = "Ürün kaydı bir JSON nesnesi olmalıdır."; return null; }
                var code = Text(product, "code");
                var problem = Problem(product, code);
                if (problem is not null) { error = $"{code ?? "?"}: {problem}"; return null; }
                codes.Add(code!);
            }
            if (codes.Distinct(StringComparer.OrdinalIgnoreCase).Count() != codes.Count) { error = "Her istekteki barkodlar özgün olmalıdır."; return null; }
            return codes;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException)
        {
            error = "Ürün açma içeriği geçerli JSON değil.";
            return null;
        }
    }

    private static string? Problem(JsonObject product, string? code)
    {
        if (code is null || code.Length > 500) return "barkod (code) zorunludur ve en fazla 500 karakter olabilir.";
        if (Text(product, "name") is not { Length: > 0 and <= 100 }) return "ürün adı (name) zorunludur ve en fazla 100 karakter olabilir.";
        if (Text(product, "displayName") is not { Length: > 0 and <= 250 }) return "görünen ad (displayName) zorunludur ve en fazla 250 karakter olabilir.";
        if (Text(product, "description") is null) return "açıklama (description) zorunludur.";
        if (!IsGuid(product, "brandId")) return "marka kimliği (brandId) bir GUID olmalıdır.";
        if (!IsGuid(product, "categoryId")) return "kategori kimliği (categoryId) bir GUID olmalıdır.";
        if (Text(product, "groupCode") is not { Length: > 0 }) return "grup kodu (groupCode) zorunludur.";
        if (Text(product, "stockCode") is not { Length: > 0 and <= 100 }) return "stok kodu (stockCode) zorunludur ve en fazla 100 karakter olabilir.";
        if (!Integer(product, "desi", out var desi) || desi < 1) return "desi (en az 1 tam sayı) zorunludur.";
        if (!Integer(product, "stockCount", out var stock) || stock < 0) return "stok adedi (stockCount) 0 veya daha büyük bir tam sayı olmalıdır.";
        if (!Integer(product, "vatRate", out var vat) || vat is < 0 or > 100) return "KDV oranı (vatRate) tam sayı olmalıdır.";
        if (!Number(product, "salePrice", out var sale) || sale <= 0 || !Number(product, "listPrice", out var list) || list < sale) return "liste fiyatı satış fiyatından küçük olamaz ve satış fiyatı sıfırdan büyük olmalıdır.";
        if (product["images"] is not JsonArray { Count: > 0 } images || images.Any(image => image is not JsonObject item || Text(item, "imageurl") is not { Length: > 0 and <= 500 })) return "en az bir görsel (images[].imageurl, en fazla 500 karakter) gerekir.";
        if (product["attributes"] is not JsonArray attributes || attributes.Any(attribute => attribute is not JsonObject item || !IsGuid(item, "attributeId") || !IsGuid(item, "attributeValueId"))) return "özellikler (attributes) attributeId ve attributeValueId GUID çiftleri olmalıdır.";
        return null;
    }

    private static string? Text(JsonObject node, string name)
    {
        foreach (var property in node)
            if (string.Equals(property.Key, name, StringComparison.OrdinalIgnoreCase) && property.Value is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text))
                return text.Trim();
        return null;
    }

    private static JsonNode? Get(JsonObject node, string name) => node.FirstOrDefault(property => string.Equals(property.Key, name, StringComparison.OrdinalIgnoreCase)).Value;

    private static bool IsGuid(JsonObject node, string name) => Text(node, name) is { } text && Guid.TryParse(text, out var id) && id != Guid.Empty;

    private static bool Integer(JsonObject node, string name, out int value)
    {
        value = 0;
        return Get(node, name) is JsonValue json && json.TryGetValue(out value);
    }

    private static bool Number(JsonObject node, string name, out decimal value)
    {
        value = 0;
        return Get(node, name) is JsonValue json && json.TryGetValue(out value);
    }
}

public static class PazaramaProductWriteMapper
{
    public const int OpenStatus = 1;
    public const int ClosedStatus = 10;

    public sealed record CreateAnswer(string? BatchId, string? Error);

    // data.batchRequestId (all zeros for a single product) and data.error.errors / message.
    public static CreateAnswer Created(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object) return new(null, null);
        string? error = null;
        if (data.TryGetProperty("error", out var failure) && failure.ValueKind == JsonValueKind.Object)
        {
            var messages = new List<string>();
            if (failure.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array)
                messages.AddRange(errors.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() : item.ToString()).Where(text => !string.IsNullOrWhiteSpace(text))!);
            if (failure.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(message.GetString())) messages.Add(message.GetString()!);
            if (messages.Count > 0) error = string.Join(" ", messages.Select(text => text.Trim())).Trim();
        }
        var batchId = data.TryGetProperty("batchRequestId", out var id) && id.ValueKind == JsonValueKind.String && Guid.TryParse(id.GetString(), out var parsed) && parsed != Guid.Empty ? parsed.ToString() : null;
        return new(batchId, error is null ? null : (error.Length <= 400 ? error : error[..400]));
    }

    // Status 1 in progress, 2 done, 3 error. Failed products come with their reason; the others succeeded.
    public static RemoteOperationStatus BatchStatus(JsonElement root, string operationId, IReadOnlyList<string>? codes)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            throw new JsonException("Pazarama batch result has no data object.");
        var status = data.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.Number && s.TryGetInt32(out var parsed) ? parsed : 0;
        if (status == 1) return new(operationId, "IN_PROGRESS", []);
        if (status is not (2 or 3)) throw new JsonException("Pazarama batch result has an unknown status.");

        var failed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (data.TryGetProperty("failedProducts", out var failures) && failures.ValueKind == JsonValueKind.Array)
            foreach (var item in failures.EnumerateArray())
            {
                var code = item.TryGetProperty("productCode", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString()?.Trim() : null;
                var reason = item.TryGetProperty("errorReason", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString()?.Trim() : null;
                if (string.IsNullOrWhiteSpace(code)) continue;
                failed[code] = failed.TryGetValue(code, out var earlier) ? $"{earlier} {reason}".Trim() : reason ?? "";
            }

        // Successful codes: the ones Pazarama lists, else every remembered barcode that did not fail.
        var succeeded = new List<string>();
        if (data.TryGetProperty("batchResult", out var results) && results.ValueKind == JsonValueKind.Array)
            foreach (var item in results.EnumerateArray())
                if (item.ValueKind == JsonValueKind.Object && (item.TryGetProperty("productCode", out var c) || item.TryGetProperty("code", out c)) && c.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(c.GetString()))
                    succeeded.Add(c.GetString()!.Trim());
        if (succeeded.Count == 0 && codes is not null) succeeded.AddRange(codes.Where(code => !failed.ContainsKey(code)));

        var lines = failed.Select(entry => new RemoteOperationLine(entry.Key, false, null, "PAZARAMA_PRODUCT_REJECTED", false, PazaramaErrorAdvice.WithAdvice(entry.Value.Length <= 300 ? entry.Value : entry.Value[..300])))
            .Concat(succeeded.Where(code => !failed.ContainsKey(code)).Distinct(StringComparer.OrdinalIgnoreCase).Select(code => new RemoteOperationLine(code, true, null, null, false, "Ürün onaya gönderildi.")))
            .ToList();
        // An error batch without any line applied nothing.
        return new(operationId, status == 3 && lines.Count == 0 ? "REJECTED" : "COMPLETED", lines);
    }

    // Approved products are live; unapproved ones wait for review or were turned down.
    public static RemotePublicationStatus Publication(JsonElement item, string barcode, bool unapproved)
    {
        var groupCode = Text(item, "groupCode");
        var externalProductId = groupCode is null ? $"code:{barcode}" : $"group:{groupCode}";
        if (!unapproved)
        {
            var closed = item.TryGetProperty("productStatus", out var ps) && ps.ValueKind == JsonValueKind.Number && ps.TryGetInt32(out var value) && value == ClosedStatus;
            return new(barcode, closed ? "ARCHIVED" : "APPROVED", externalProductId, barcode, null, item.GetRawText());
        }
        var text = ((Text(item, "status") ?? "") + " " + (Text(item, "waitingApproveExp") ?? "")).Trim().ToLower(new CultureInfo("tr-TR"));
        var rejected = text.Contains("red", StringComparison.Ordinal) && !text.Contains("bekl", StringComparison.Ordinal);
        return new(barcode, rejected ? "REJECTED" : "PENDING_APPROVAL", externalProductId, barcode, rejected ? "PAZARAMA_PRODUCT_REJECTED" : null, item.GetRawText());
    }

    // {"items":[{"barcode":"...","archived":true}]}; archived means closed for sale.
    public static IReadOnlyList<(string Code, bool Close)>? SaleChanges(string payloadJson)
    {
        try
        {
            if (JsonNode.Parse(payloadJson) is not JsonObject root || root["items"] is not JsonArray { Count: >= 1 and <= 100 } items) return null;
            var changes = new List<(string, bool)>();
            foreach (var node in items)
            {
                if (node is not JsonObject item || item["barcode"] is not JsonValue code || !code.TryGetValue<string>(out var text) || string.IsNullOrWhiteSpace(text) || item["archived"] is not JsonValue flag || !flag.TryGetValue<bool>(out var close)) return null;
                changes.Add((text.Trim(), close));
            }
            return changes.Select(x => x.Item1).Distinct(StringComparer.OrdinalIgnoreCase).Count() == changes.Count ? changes : null;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException) { return null; }
    }

    // data.result[]: {success, message, code, productStatus}.
    public static RemoteOperationLine SaleResult(JsonElement root, string code)
    {
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object && data.TryGetProperty("result", out var results) && results.ValueKind == JsonValueKind.Array)
            foreach (var item in results.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var itemCode = item.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString()?.Trim() : null;
                if (itemCode is not null && !string.Equals(itemCode, code, StringComparison.OrdinalIgnoreCase)) continue;
                var ok = item.TryGetProperty("success", out var s) && s.ValueKind == JsonValueKind.True;
                var message = item.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString()?.Trim() : null;
                return new(code, ok, null, ok ? null : "PAZARAMA_SALE_STATUS_REJECTED", false, message);
            }
        return new(code, false, null, "PAZARAMA_SALE_STATUS_UNCONFIRMED", false, "Pazarama yanıtı bu barkod için sonuç içermiyor.");
    }

    private static string? Text(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var value)) return null;
        var text = value.ValueKind switch { JsonValueKind.String => value.GetString(), JsonValueKind.Number => value.GetRawText(), _ => null };
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }
}
