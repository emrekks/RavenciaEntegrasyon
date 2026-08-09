using System.Globalization;
using System.Text.Json;
using MarketplaceHub.Application;

namespace MarketplaceHub.Infrastructure.Adapters.Trendyol.Mapping;

public static class TrendyolJsonMapper
{
    public static AdapterPageResult<RemoteOrder> Orders(string json)
    {
        using var document = JsonDocument.Parse(json); var root = document.RootElement;
        var rows = new List<RemoteOrder>();
        foreach (var package in Content(root))
        {
            var externalPackageId = Text(package, "shipmentPackageId"); var orderNumber = Text(package, "orderNumber");
            if (string.IsNullOrWhiteSpace(externalPackageId) || string.IsNullOrWhiteSpace(orderNumber)) continue;
            var lines = new List<RemoteOrderLine>(); var allocations = new List<RemotePackageAllocation>();
            if (package.TryGetProperty("lines", out var lineArray) && lineArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var line in lineArray.EnumerateArray())
                {
                    var externalLineId = Text(line, "lineId", "id"); if (string.IsNullOrWhiteSpace(externalLineId)) continue;
                    var quantity = Decimal(line, "quantity"); var rawStatus = Text(line, "orderLineItemStatusName");
                    lines.Add(new(externalLineId, Text(line, "stockCode", "merchantSku"), NullText(line, "barcode"), Text(line, "productName"), quantity, Decimal(line, "lineUnitPrice", "price", "amount"), Decimal(line, "vatRate", "vatBaseAmount"), rawStatus, line.GetRawText()));
                    allocations.Add(new(externalLineId, quantity, 0, 0, 0, 0));
                }
            }
            var gross = Decimal(package, "packageGrossAmount", "grossAmount", "packageTotalPrice");
            var discount = Decimal(package, "packageSellerDiscount", "totalDiscount") + Decimal(package, "packageTyDiscount", "totalTyDiscount");
            var net = Decimal(package, "packageTotalPrice", "totalPrice");
            var rawStatusPackage = Text(package, "shipmentPackageStatus", "status"); var modified = Instant(package, "lastModifiedDate") ?? Instant(package, "orderDate") ?? DateTimeOffset.UnixEpoch; var ordered = Instant(package, "orderDate") ?? modified;
            var remotePackage = new RemotePackage(externalPackageId, FirstArrayText(package, "originPackageIds"), rawStatusPackage, modified, NullText(package, "cargoProviderName"), NullText(package, "cargoTrackingNumber", "cargoSenderNumber"), allocations, gross, discount, net);
            rows.Add(new(orderNumber, orderNumber, ordered, modified, Text(package, "currencyCode"), gross, discount, net,
                Snapshot(package,
                    "customerFirstName", "customerLastName", "customerEmail", "customerPhone", "customerPhoneNumber", "phone", "phoneNumber", "commercial", "micro", "microExport", "3pByTrendyol", "shipmentPackageType", "orderType", "eInvoiceAvailable", "isEInvoice",
                    "customerTaxNumber", "taxNumber", "identityNumber", "customerIdentityNumber", "tcIdentityNumber",
                    "estimatedDeliveryStartDate", "estimatedDeliveryEndDate", "agreedDeliveryDate", "lastDeliveryDate", "deliveryDate", "fastDelivery",
                    "cargoProviderName", "cargoTrackingNumber", "cargoSenderNumber", "invoiceStatus", "invoiceNumber", "invoiceLink", "invoiceRejectedReasonKeys"),
                Snapshot(package, "shipmentAddress"), Snapshot(package, "invoiceAddress"), lines, [remotePackage], package.GetRawText()));
        }
        return new(rows, NullText(root, "nextCursor"), Bool(root, "hasMore"));
    }

    public static AdapterPageResult<RemoteProduct> Products(string json)
    {
        using var document = JsonDocument.Parse(json); var root = document.RootElement; var rows = new List<RemoteProduct>();
        foreach (var product in Content(root))
        {
            var contentId = Text(product, "contentId");
            if (product.TryGetProperty("variants", out var variants) && variants.ValueKind == JsonValueKind.Array)
                foreach (var variant in variants.EnumerateArray()) rows.Add(new(contentId, NullText(variant, "variantId"), NullText(variant, "barcode"), NullText(variant, "stockCode"), product.GetRawText()));
        }
        var currentPage = Long(root, "page");
        var pageSize = Math.Max(1, Long(root, "size"));
        var totalPages = Long(root, "totalPages");
        var token = NullText(root, "nextPageToken");
        string? next = null;
        if (totalPages > 0 && currentPage + 1 < totalPages && (currentPage + 1) * pageSize < 10_000)
            next = $"p:{currentPage + 1}";
        else if (!string.IsNullOrWhiteSpace(token))
            next = $"t:{token}";
        return new(rows, next, !string.IsNullOrWhiteSpace(next));
    }

    public static RemotePublicationStatus? ApprovedPublicationStatus(string json, string barcode)
    {
        using var document = JsonDocument.Parse(json);
        foreach (var product in Content(document.RootElement))
        {
            var contentId = NullText(product, "contentId");
            if (!product.TryGetProperty("variants", out var variants) || variants.ValueKind != JsonValueKind.Array) continue;
            foreach (var variant in variants.EnumerateArray())
            {
                var remoteBarcode = NullText(variant, "barcode");
                if (!string.Equals(remoteBarcode, barcode, StringComparison.OrdinalIgnoreCase)) continue;
                var status = Bool(variant, "blacklisted") ? "BLACKLISTED"
                    : Bool(variant, "locked") ? "LOCKED"
                    : Bool(variant, "archived") ? "ARCHIVED"
                    : "APPROVED";
                return new(remoteBarcode!, status, contentId, NullText(variant, "variantId"), null, variant.GetRawText());
            }
        }
        return null;
    }

    public static RemotePublicationStatus? UnapprovedPublicationStatus(string json, string barcode)
    {
        using var document = JsonDocument.Parse(json);
        foreach (var product in Content(document.RootElement))
        {
            var remoteBarcode = NullText(product, "barcode");
            if (!string.Equals(remoteBarcode, barcode, StringComparison.OrdinalIgnoreCase)) continue;
            var rawStatus = Text(product, "status");
            var status = string.Equals(rawStatus, "pendingApproval", StringComparison.OrdinalIgnoreCase) ? "PENDING_APPROVAL"
                : string.Equals(rawStatus, "rejected", StringComparison.OrdinalIgnoreCase) ? "REJECTED"
                : "UNKNOWN";
            string? rejection = null;
            if (product.TryGetProperty("rejectReasonDetails", out var reasons) && reasons.ValueKind == JsonValueKind.Array)
                rejection = reasons.EnumerateArray().Select(reason => NullText(reason, "rejectReason")).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
            return new(remoteBarcode!, status, null, null, rejection, product.GetRawText());
        }
        return null;
    }

    public static RemoteOperationStatus Batch(string json, string requestedId)
    {
        using var document = JsonDocument.Parse(json); var root = document.RootElement; var lines = new List<RemoteOperationLine>();
        if (root.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
            foreach (var item in items.EnumerateArray())
            {
                var key = "";
                if (item.TryGetProperty("requestItem", out var request) && request.ValueKind == JsonValueKind.Object)
                    key = Text(request, "barcode", "contentId", "stockCode");
                if (string.IsNullOrWhiteSpace(key)) key = Text(item, "barcode", "contentId", "stockCode");
                var status = Text(item, "status"); var succeeded = string.Equals(status, "SUCCESS", StringComparison.OrdinalIgnoreCase); var failure = item.TryGetProperty("failureReasons", out var reasons) && reasons.ValueKind == JsonValueKind.Array ? reasons.EnumerateArray().Select(x => x.ToString()).FirstOrDefault() : null;
                lines.Add(new(key, succeeded, null, failure, !succeeded && !string.Equals(status, "FAILED", StringComparison.OrdinalIgnoreCase)));
            }
        return new(NullText(root, "batchRequestId") ?? requestedId, Text(root, "status"), lines);
    }

    public static AdapterPageResult<RemoteReturnClaim> Returns(string json)
    {
        using var document = JsonDocument.Parse(json); var root = document.RootElement; var rows = new List<RemoteReturnClaim>();
        foreach (var claim in Content(root))
        {
            var claimId = Text(claim, "claimId", "id"); var orderNumber = Text(claim, "orderNumber"); if (claimId.Length == 0 || orderNumber.Length == 0) continue;
            var lines = new List<RemoteReturnLine>();
            if (claim.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
                foreach (var item in items.EnumerateArray())
                {
                    var lineId = Text(item, "id"); var orderLineId = Text(item, "orderLineItemId"); if (lineId.Length > 0 && orderLineId.Length > 0) lines.Add(new(lineId, orderLineId, Decimal(item, "quantity")));
                }
            var status = ClaimStatus(claim); rows.Add(new(claimId, orderNumber, status, ClaimReasonCode(claim), ClaimReasonText(claim), FlexibleInstant(claim, "autoApproveDate", "actionDueDate", "dueDate"), Instant(claim, "lastModifiedDate") ?? DateTimeOffset.UnixEpoch, lines, claim.GetRawText()));
        }
        var page = Long(root, "page"); var totalPages = Long(root, "totalPages"); var hasMore = totalPages > 0 && page + 1 < totalPages;
        return new(rows, hasMore ? (page + 1).ToString(CultureInfo.InvariantCulture) : null, hasMore);
    }


    public static IReadOnlyList<ReturnIssueReason> ReturnIssueReasons(string json)
    {
        using var document = JsonDocument.Parse(json); var root = document.RootElement; var rows = new List<ReturnIssueReason>();
        IEnumerable<JsonElement> items = root.ValueKind == JsonValueKind.Array
            ? root.EnumerateArray()
            : root.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array
                ? content.EnumerateArray()
                : root.TryGetProperty("claimIssueReasons", out var reasons) && reasons.ValueKind == JsonValueKind.Array
                    ? reasons.EnumerateArray()
                    : Enumerable.Empty<JsonElement>();
        foreach (var item in items)
        {
            var id = Text(item, "id", "claimIssueReasonId", "reasonId");
            var name = Text(item, "name", "description", "reason");
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name)) continue;
            var evidenceRequired = BoolAny(item, "evidenceRequired", "isEvidenceRequired", "imageRequired", "isImageRequired") ?? id is not ("1651" or "451" or "2101");
            rows.Add(new(id, name, evidenceRequired));
        }
        return rows.GroupBy(x => x.Id, StringComparer.Ordinal).Select(x => x.First()).OrderBy(x => x.Name, StringComparer.Create(new CultureInfo("tr-TR"), true)).ToList();
    }

    public static IReadOnlyList<RemoteReferenceItem> References(string resourceType, string json, string? parentExternalId)
    {
        using var document = JsonDocument.Parse(json); var root = document.RootElement; var rows = new List<RemoteReferenceItem>();
        if (resourceType == "BRANDS")
        {
            var array = root.TryGetProperty("brands", out var brands) ? brands : root;
            if (array.ValueKind == JsonValueKind.Array) foreach (var item in array.EnumerateArray()) rows.Add(Reference(resourceType, item, "id", "name", null, 0, true));
        }
        else if (resourceType == "CATEGORY_ATTRIBUTES" && root.TryGetProperty("categoryAttributes", out var attributes) && attributes.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in attributes.EnumerateArray()) if (item.TryGetProperty("attribute", out var attribute)) rows.Add(new(resourceType, Text(attribute, "id"), parentExternalId, Text(attribute, "name"), Text(attribute, "name"), 0, true, true, item.GetRawText(), Bool(item, "required"), Bool(item, "allowCustom"), Bool(item, "allowMultipleAttributeValues")));
        }
        else if (resourceType == "ATTRIBUTE_VALUES")
        {
            foreach (var item in Content(root)) rows.Add(Reference(resourceType, item, "attributeValueId", "attributeValue", parentExternalId, 0, true));
        }
        else if (resourceType == "CATEGORIES" && root.TryGetProperty("categories", out var categories) && categories.ValueKind == JsonValueKind.Array) FlattenCategories(categories, null, "", 0, rows);
        return rows;
    }

    private static void FlattenCategories(JsonElement categories, string? parentId, string parentPath, int depth, List<RemoteReferenceItem> rows)
    {
        foreach (var item in categories.EnumerateArray())
        {
            var id = Text(item, "id"); var name = Text(item, "name"); var path = parentPath.Length == 0 ? name : $"{parentPath} / {name}"; var hasChildren = item.TryGetProperty("subCategories", out var children) && children.ValueKind == JsonValueKind.Array && children.GetArrayLength() > 0;
            rows.Add(new("CATEGORIES", id, parentId, name, path, depth, !hasChildren, true, item.GetRawText())); if (hasChildren) FlattenCategories(children, id, path, depth + 1, rows);
        }
    }

    private static RemoteReferenceItem Reference(string type, JsonElement item, string idName, string nameName, string? parent, int depth, bool leaf) { var name = Text(item, nameName); return new(type, Text(item, idName), parent, name, name, depth, leaf, true, item.GetRawText()); }
    private static IEnumerable<JsonElement> Content(JsonElement root) => root.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array ? content.EnumerateArray() : [];
    private static string ClaimStatus(JsonElement claim) { if (claim.TryGetProperty("claimItemStatus", out var value)) return value.ValueKind == JsonValueKind.Object ? Text(value, "name") : value.ToString(); if (claim.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array) foreach (var item in items.EnumerateArray()) if (item.TryGetProperty("claimItemStatus", out var status)) return status.ValueKind == JsonValueKind.Object ? Text(status, "name") : status.ToString(); return ""; }
    private static string? ClaimReasonCode(JsonElement claim) => NestedReason(claim, "code");
    private static string? ClaimReasonText(JsonElement claim) => NestedReason(claim, "name");
    private static string? NestedReason(JsonElement claim, string field) { if (!claim.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array) return null; foreach (var item in items.EnumerateArray()) if (item.TryGetProperty("customerClaimItemReason", out var reason)) return NullText(reason, field); return null; }
    private static string Snapshot(JsonElement value, params string[] fields) { var map = new Dictionary<string, JsonElement>(); foreach (var field in fields) if (value.TryGetProperty(field, out var item)) map[field] = item.Clone(); return JsonSerializer.Serialize(map); }
    private static string? FirstArrayText(JsonElement value, string field) => value.TryGetProperty(field, out var array) && array.ValueKind == JsonValueKind.Array && array.GetArrayLength() > 0 ? array[0].ToString() : null;
    private static string Text(JsonElement value, params string[] names) => NullText(value, names) ?? "";
    private static string? NullText(JsonElement value, params string[] names) { foreach (var name in names) if (value.TryGetProperty(name, out var item) && item.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)) return item.ToString(); return null; }
    private static decimal Decimal(JsonElement value, params string[] names) { foreach (var name in names) if (value.TryGetProperty(name, out var item) && (item.TryGetDecimal(out var result) || decimal.TryParse(item.ToString(), CultureInfo.InvariantCulture, out result))) return result; return 0; }
    private static long Long(JsonElement value, string name) => value.TryGetProperty(name, out var item) && item.TryGetInt64(out var result) ? result : 0;
    private static bool Bool(JsonElement value, string name) => value.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.True;
    private static bool? BoolAny(JsonElement value, params string[] names)
    {
        foreach (var name in names)
        {
            if (!value.TryGetProperty(name, out var item)) continue;
            if (item.ValueKind == JsonValueKind.True) return true;
            if (item.ValueKind == JsonValueKind.False) return false;
            if (bool.TryParse(item.ToString(), out var parsed)) return parsed;
        }
        return null;
    }
    private static DateTimeOffset? Instant(JsonElement value, string name) => value.TryGetProperty(name, out var item) && item.TryGetInt64(out var milliseconds) ? DateTimeOffset.FromUnixTimeMilliseconds(milliseconds) : null;
    private static DateTimeOffset? FlexibleInstant(JsonElement value, params string[] names)
    {
        foreach (var name in names)
        {
            if (!value.TryGetProperty(name, out var item) || item.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) continue;
            if (item.TryGetInt64(out var milliseconds))
            {
                try { return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds); } catch (ArgumentOutOfRangeException) { }
            }
            if (DateTimeOffset.TryParse(item.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)) return parsed;
        }
        return null;
    }
}
