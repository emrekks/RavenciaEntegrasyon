using System.Text.Json;
using MarketplaceHub.Application;

namespace MarketplaceHub.Infrastructure.Adapters.Hepsiburada;

internal static partial class HepsiburadaJsonMapper
{
    public static string ProductImportTrackingId(JsonElement root)
    {
        var data = Unwrap(root);
        var trackingId = First(Text(root, "trackingId", "traceId"), Text(data, "trackingId", "traceId"));
        return !string.IsNullOrWhiteSpace(trackingId) ? trackingId : throw new JsonException("Hepsiburada ürün aktarım yanıtında trackingId yok.");
    }

    public static string InventoryUploadId(JsonElement root)
    {
        var data = Unwrap(root);
        var uploadId = First(Text(root, "inventoryUploadId", "uploadId", "id"), Text(data, "inventoryUploadId", "uploadId", "id"));
        return !string.IsNullOrWhiteSpace(uploadId) ? uploadId : throw new JsonException("Hepsiburada listing yanıtında inventoryUploadId yok.");
    }

    // prefix: the operation id's kind; the shipping-info upload is read back the same way (LISTING_SHIPPING:).
    public static RemoteOperationStatus InventoryUploadStatus(JsonElement root, string uploadId, string prefix = "LISTING_INVENTORY:")
    {
        var data = Unwrap(root);
        var rawStatus = First(Text(root, "status", "uploadStatus", "inventoryStatus"), Text(data, "status", "uploadStatus", "inventoryStatus"));
        if (string.IsNullOrWhiteSpace(rawStatus)) throw new JsonException("Hepsiburada listing yükleme durumu eksik.");
        var normalized = rawStatus.Trim().Replace(' ', '_').Replace('-', '_').ToUpperInvariant();
        var status = normalized switch
        {
            // C4: the listing API answers Ready (queued), InProgress and Done (applied; per-SKU errors[] below).
            "PROCESSING" or "IN_PROGRESS" or "INPROGRESS" or "READY" or "PENDING" or "QUEUED" => "IN_PROGRESS",
            "DONE" or "COMPLETED" or "COMPLETE" or "SUCCESS" or "SUCCEEDED" or "FAILED" or "PARTIAL_SUCCESS" or "COMPLETED_WITH_ERRORS" => "COMPLETED",
            _ => throw new JsonException("Hepsiburada listing yükleme durumu tanınmıyor.")
        };
        if (status == "IN_PROGRESS") return new($"{prefix}{uploadId}", status, []);

        var errorItems = Find(data, "errors", "validationErrors", "errorList");
        if (errorItems.ValueKind == JsonValueKind.Undefined) errorItems = Find(root, "errors", "validationErrors", "errorList");
        if (errorItems.ValueKind is not (JsonValueKind.Array or JsonValueKind.Undefined or JsonValueKind.Null))
            throw new JsonException("Hepsiburada listing hata listesi beklenen türde değil.");
        var lines = new List<RemoteOperationLine>();
        if (errorItems.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in errorItems.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) throw new JsonException("Hepsiburada listing hata satırı nesne değil.");
                var merchantSku = Text(item, "merchantSku", "MerchantSku", "sellerSku", "SellerSku");
                var hbSku = Text(item, "hepsiburadaSku", "HepsiburadaSku", "hbSku", "HBSKU");
                var externalKey = First(merchantSku, hbSku);
                if (string.IsNullOrWhiteSpace(externalKey))
                    throw new JsonException("Hepsiburada listing hata satırında merchantSku veya hbSku yok.");
                var errorCode = Text(item, "errorCode", "code", "message");
                var nestedErrors = Find(item, "errors", "messages");
                if (nestedErrors.ValueKind == JsonValueKind.Array)
                {
                    var firstNested = nestedErrors.EnumerateArray().Select(entry => entry.ValueKind == JsonValueKind.Object
                        ? Text(entry, "string", "message", "error", "code")
                        : entry.ValueKind == JsonValueKind.String ? entry.GetString() : null).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
                    errorCode ??= firstNested;
                }
                if (string.IsNullOrWhiteSpace(errorCode)) errorCode = "REMOTE_VALIDATION_FAILED";
                lines.Add(new(externalKey.Trim(), false, hbSku, errorCode, false, "REJECTED"));
            }
        }
        if (lines.GroupBy(line => line.ExternalKey, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
            throw new JsonException("Hepsiburada listing sonuçlarında yinelenen SKU var.");
        // Done lists the refused SKUs alone, so a SKU it leaves out was applied. A failed upload says nothing of the SKUs
        // it leaves out: without any it is refused whole, with some those it leaves out stay unconfirmed. An error state
        // that names no SKU is refused whole as well.
        if (lines.Count == 0 && normalized is "FAILED" or "PARTIAL_SUCCESS" or "COMPLETED_WITH_ERRORS") return new($"{prefix}{uploadId}", "REJECTED", []);
        return new($"{prefix}{uploadId}", status, lines, UnlistedLinesUnconfirmed: normalized == "FAILED");
    }

    public static RemoteOperationStatus ProductImportStatus(JsonElement root, string trackingId)
    {
        var data = Unwrap(root);
        // The documented answer carries importStatus on every line of data, not at the top (live 27 Oct 2026): a line
        // still PROCESSING keeps the whole file in progress; otherwise the lines' common state stands.
        var importStatus = First(Text(root, "importStatus"), data.ValueKind == JsonValueKind.Object ? Text(data, "importStatus", "status") : null)
            ?? LineImportStatus(data.ValueKind == JsonValueKind.Array ? data : Find(data, "items", "products", "productStatuses", "productList", "results"));
        if (string.IsNullOrWhiteSpace(importStatus)) throw new JsonException("Hepsiburada ürün aktarım durumu eksik.");
        var normalizedStatus = importStatus.Trim().ToUpperInvariant();
        var status = normalizedStatus switch
        {
            "PROCESSING" or "IN_PROGRESS" or "PENDING" => "IN_PROGRESS",
            "SUCCESS" or "COMPLETED" or "DONE" or "FAILED" or "PARTIAL_SUCCESS" => "COMPLETED",
            _ => throw new JsonException("Hepsiburada ürün aktarım durumu tanınmıyor.")
        };

        var items = Find(data, "items", "products", "productStatuses", "productList", "results");
        if (items.ValueKind == JsonValueKind.Undefined) items = data.ValueKind == JsonValueKind.Array ? data : Find(root, "items", "products", "productStatuses", "productList", "results");
        var lines = new List<RemoteOperationLine>();
        if (items.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in items.EnumerateArray())
            {
                var merchantSku = Text(item, "merchantSku", "MerchantSku", "sellerSku", "SellerSku");
                var productStatus = Text(item, "productStatus", "status", "Status");
                if (string.IsNullOrWhiteSpace(merchantSku) || string.IsNullOrWhiteSpace(productStatus)) throw new JsonException("Hepsiburada ürün aktarım satırında merchantSku veya productStatus yok.");
                var normalizedProductStatus = NormalizeProductStatus(productStatus);
                var succeeded = normalizedProductStatus is "SUCCESS" or "ACCEPTED" or "APPROVED" or "APPROVAL_PENDING" or "WAITING_APPROVAL" or "IN_REVIEW" or "PRODUCT_CREATED"
                    or "WAITING" or "INCELENECEK" or "GOREV_ACILMIS" or "KATALOG_SURECINDE" or "MATCHED" or "ESLESEN" or "PRE_MATCHED" or "MATCHED_WITH_STAGED" or "ON_KATALOG_ESLESEN" or "CREATED" or "SATISA_HAZIR" or "SALE_READY";
                var rejected = normalizedProductStatus is "MISSING_INFO" or "URUN_BILGILERI_EKSIK" or "REJECTED" or "FAILED" or "INVALID";
                if (!succeeded && !rejected) throw new JsonException("Hepsiburada ürün durum satırında tanınmayan ürün durumu var.");
                var errorCode = succeeded ? null : ProductImportErrorCode(item);
                lines.Add(new(merchantSku.Trim(), succeeded, Text(item, "hbSku", "HBSku"), errorCode, false, normalizedProductStatus));
            }
        }
        else if (items.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null))
            throw new JsonException("Hepsiburada ürün aktarım satır listesi beklenen türde değil.");

        if (status == "COMPLETED" && lines.Count > 0 && lines.GroupBy(x => x.ExternalKey, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
            throw new JsonException("Hepsiburada ürün aktarım yanıtında merchantSku yineleniyor.");
        return new(trackingId, status, lines);
    }

    // What an unrecognised import status answer looks like, for the job's error text: its top-level field names, the
    // line count and the status values met (import and product statuses only, product data, no customer data). The
    // answer is not logged anywhere else, so without this a mismatch (live 27 Oct 2026) could not be told apart.
    private static string? LineImportStatus(JsonElement items)
    {
        if (items.ValueKind != JsonValueKind.Array) return null;
        var statuses = items.EnumerateArray()
            .Select(item => item.ValueKind == JsonValueKind.Object ? Text(item, "importStatus") : null)
            .Where(status => !string.IsNullOrWhiteSpace(status))
            .Select(status => status!.Trim().ToUpperInvariant())
            .ToList();
        if (statuses.Count == 0) return null;
        return statuses.FirstOrDefault(status => status is "PROCESSING" or "IN_PROGRESS" or "PENDING") ?? statuses[0];
    }

    public static string ProductImportStatusShape(JsonElement root)
    {
        static string Safe(string? value) => new((value ?? "").Trim().Take(40).Select(character => char.IsLetterOrDigit(character) || character is '_' or '-' or ' ' ? character : '?').ToArray());
        var parts = new List<string>();
        if (root.ValueKind == JsonValueKind.Object)
            parts.Add("alanlar: " + string.Join(",", root.EnumerateObject().Select(property => Safe(property.Name)).Take(12)));
        else parts.Add("kök: " + root.ValueKind);
        var data = Unwrap(root);
        var items = data.ValueKind == JsonValueKind.Array ? data : Find(data, "items", "products", "productStatuses", "productList", "results");
        if (items.ValueKind == JsonValueKind.Array) parts.Add($"satır: {items.GetArrayLength()}");
        // The first line's field names, a null or empty one marked, so a line that lacks merchantSku or productStatus
        // shows what it carries instead (live 27 Oct 2026: three lines, neither field found).
        if (items.ValueKind == JsonValueKind.Array && items.GetArrayLength() > 0 && items[0].ValueKind == JsonValueKind.Object)
            parts.Add("satır alanları: " + string.Join(",", items[0].EnumerateObject().Take(20).Select(property =>
                Safe(property.Name) + (property.Value.ValueKind is JsonValueKind.Null || (property.Value.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(property.Value.GetString())) ? "(boş)" : ""))));
        var statuses = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var source in new[] { root, data })
            if (source.ValueKind == JsonValueKind.Object && Text(source, "importStatus", "status") is { } value) statuses.Add("aktarım=" + Safe(value));
        if (items.ValueKind == JsonValueKind.Array)
            foreach (var item in items.EnumerateArray().Take(20))
                if (item.ValueKind == JsonValueKind.Object && Text(item, "productStatus", "status", "Status") is { } value) statuses.Add("ürün=" + Safe(value));
        if (statuses.Count > 0) parts.Add("durumlar: " + string.Join(",", statuses.Take(6)));
        return string.Join("; ", parts);
    }

    public static int? ProductImportTotalCount(JsonElement root)
    {
        var data = Unwrap(root);
        return Integer(root, "totalElements", "totalCount", "total") ?? Integer(data, "totalElements", "totalCount", "total");
    }

    private static string ProductImportErrorCode(JsonElement item)
    {
        var errors = Find(item, "validationResults", "errors", "errorMessages", "messages");
        if (errors.ValueKind == JsonValueKind.Array)
        {
            foreach (var error in errors.EnumerateArray())
            {
                var code = Text(error, "code", "errorCode", "field", "message");
                if (!string.IsNullOrWhiteSpace(code)) return code;
            }
        }
        return Text(item, "errorCode", "rejectionCode") ?? "REMOTE_VALIDATION_FAILED";
    }

    private static string NormalizeProductStatus(string value)
    {
        var normalized = value.Trim().ToUpperInvariant()
            .Replace('İ', 'I').Replace('ı', 'I').Replace('Ş', 'S').Replace('ş', 'S')
            .Replace('Ğ', 'G').Replace('ğ', 'G').Replace('Ü', 'U').Replace('ü', 'U')
            .Replace('Ö', 'O').Replace('ö', 'O').Replace('Ç', 'C').Replace('ç', 'C');
        return string.Join('_', normalized.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }
}
