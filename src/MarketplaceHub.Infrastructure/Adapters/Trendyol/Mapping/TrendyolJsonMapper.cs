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
            var externalPackageId = Text(package, "id", "shipmentPackageId", "packageId"); var orderNumber = Text(package, "orderNumber");
            if (string.IsNullOrWhiteSpace(externalPackageId) || string.IsNullOrWhiteSpace(orderNumber)) continue;
            var lines = new List<RemoteOrderLine>(); var allocations = new List<RemotePackageAllocation>();
            if (package.TryGetProperty("lines", out var lineArray) && lineArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var line in lineArray.EnumerateArray())
                {
                    var externalLineId = Text(line, "lineId", "id"); if (string.IsNullOrWhiteSpace(externalLineId)) continue;
                    var quantity = Decimal(line, "quantity"); var rawStatus = Text(line, "orderLineItemStatusName");
                    lines.Add(new(externalLineId, Text(line, "stockCode", "merchantSku"), NullText(line, "barcode"), Text(line, "productName"), quantity, Decimal(line, "lineItemPrice", "lineUnitPrice", "lineGrossAmount", "price", "amount"), Decimal(line, "vatRate", "vatBaseAmount"), rawStatus, line.GetRawText()));
                    allocations.Add(new(externalLineId, quantity, 0, 0, 0, 0));
                }
            }
            var gross = Decimal(package, "packageGrossAmount", "grossAmount", "packageTotalPrice");
            var discount = Decimal(package, "packageTotalDiscount");
            if (discount == 0) discount = Decimal(package, "packageSellerDiscount", "totalDiscount") + Decimal(package, "packageTyDiscount", "totalTyDiscount");
            var net = Decimal(package, "packageTotalPrice", "totalPrice");
            // Trendyol's Yeni tab is driven by the top-level package status.
            // For newly created packages shipmentPackageStatus may already be
            // ReadyToShip while status is still Created; the latter is the
            // authoritative workflow state for the order projection.
            var rawStatusPackage = Text(package, "status", "shipmentPackageStatus"); var modified = Instant(package, "lastModifiedDate") ?? Instant(package, "orderDate") ?? DateTimeOffset.UnixEpoch; var ordered = Instant(package, "orderDate") ?? modified;
            var remotePackage = new RemotePackage(externalPackageId, FirstArrayText(package, "originPackageIds"), rawStatusPackage, modified, NullText(package, "cargoProviderName", "cargoProviderCode", "cargoProviderId", "cargoProvider"), NullText(package, "cargoTrackingNumber", "cargoSenderNumber", "trackingNumber"), allocations, gross, discount, net);
            rows.Add(new(orderNumber, orderNumber, ordered, modified, Text(package, "currencyCode"), gross, discount, net,
                CustomerSnapshot(package),
                ObjectSnapshot(package, "shipmentAddress"), ObjectSnapshot(package, "invoiceAddress"), lines, [remotePackage], package.GetRawText()));
        }
        var next = NullText(root, "nextCursor");
        var hasMore = Bool(root, "hasMore");
        if (string.IsNullOrWhiteSpace(next))
        {
            var currentPage = Long(root, "page");
            var totalPages = Long(root, "totalPages");
            if (totalPages > 0 && currentPage + 1 < totalPages)
            {
                next = $"p:{currentPage + 1}";
                hasMore = true;
            }
        }
        return new(rows, next, hasMore);
    }

    public static AdapterPageResult<RemoteProduct> Products(string json)
    {
        using var document = JsonDocument.Parse(json); var root = document.RootElement; var rows = new List<RemoteProduct>();
        foreach (var product in Content(root))
        {
            var contentId = Text(product, "contentId", "id", "productMainId");
            if (product.TryGetProperty("variants", out var variants) && variants.ValueKind == JsonValueKind.Array)
                foreach (var variant in variants.EnumerateArray()) rows.Add(new(contentId, NullText(variant, "variantId"), NullText(variant, "barcode"), NullText(variant, "stockCode"), product.GetRawText()));
            else if (!string.IsNullOrWhiteSpace(NullText(product, "barcode")))
                rows.Add(new(contentId, NullText(product, "variantId", "id"), NullText(product, "barcode"), NullText(product, "stockCode"), product.GetRawText()));
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

    public static AdapterPageResult<RemoteCatalogProduct> CatalogProducts(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var rows = Content(root)
            .Select(ProductSnapshot)
            .Where(x => !string.IsNullOrWhiteSpace(x.ExternalProductId))
            .ToList();
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

    public static RemoteCatalogProduct ProductSnapshot(string json)
    {
        using var document = JsonDocument.Parse(json);
        return ProductSnapshot(document.RootElement);
    }

    private static RemoteCatalogProduct ProductSnapshot(JsonElement product)
    {
        var productId = Text(product, "contentId", "id", "productMainId");
        var productMainId = NullText(product, "productMainId", "contentId");
        var title = Text(product, "title", "name");
        var description = NullText(product, "description") ?? "";
        var brand = NamedReference(product, "brand");
        var category = NamedReference(product, "category");
        var images = ImageUrls(product);
        var variants = new List<RemoteCatalogVariant>();
        if (product.TryGetProperty("variants", out var variantArray) && variantArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var variant in variantArray.EnumerateArray())
            {
                var variantId = NullText(variant, "variantId", "id", "barcode", "stockCode") ?? "";
                var barcode = NullText(variant, "barcode");
                var sku = NullText(variant, "stockCode", "merchantSku", "sku") ?? barcode ?? variantId;
                if (string.IsNullOrWhiteSpace(variantId) || string.IsNullOrWhiteSpace(sku)) continue;
                variants.Add(new(
                    variantId,
                    sku,
                    barcode,
                    NullText(variant, "modelCode", "productMainId") ?? productMainId,
                    Options(variant),
                    Bool(variant, "archived"),
                    DecimalNullable(variant, "salePrice") ?? DecimalNullable(variant, "price", "salePrice"),
                    DecimalNullable(variant, "listPrice") ?? DecimalNullable(variant, "price", "listPrice"),
                    DecimalNullable(variant, "vatRate"),
                    variant.GetRawText()));
            }
        }
        if (variants.Count == 0)
        {
            var barcode = NullText(product, "barcode");
            var sku = NullText(product, "stockCode", "merchantSku", "sku") ?? barcode;
            var variantId = NullText(product, "variantId", "id", "barcode", "stockCode");
            if (!string.IsNullOrWhiteSpace(variantId) && !string.IsNullOrWhiteSpace(sku))
                variants.Add(new(variantId!, sku!, barcode, NullText(product, "modelCode", "productMainId") ?? productMainId, Options(product), Bool(product, "archived"), DecimalNullable(product, "salePrice"), DecimalNullable(product, "listPrice"), DecimalNullable(product, "vatRate"), product.GetRawText()));
        }
        return new(productId, productMainId, title, description, brand.Id, brand.Name, category.Id, category.Name, images, variants, product.GetRawText());
    }

    private static (string? Id, string? Name) NamedReference(JsonElement value, string propertyName)
    {
        if (!value.TryGetProperty(propertyName, out var reference) || reference.ValueKind != JsonValueKind.Object) return (null, null);
        return (NullText(reference, "id", "code"), NullText(reference, "name", "title"));
    }

    private static IReadOnlyList<string> ImageUrls(JsonElement product)
    {
        if (!product.TryGetProperty("images", out var images) || images.ValueKind != JsonValueKind.Array) return [];
        return images.EnumerateArray()
            .Select(image => image.ValueKind == JsonValueKind.Object ? NullText(image, "url", "imageUrl") : image.ToString())
            .Where(url => !string.IsNullOrWhiteSpace(url))
            .Select(url => url!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToList();
    }

    private static IReadOnlyDictionary<string, string> Options(JsonElement value)
    {
        if (!value.TryGetProperty("attributes", out var attributes) || attributes.ValueKind != JsonValueKind.Array) return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        return attributes.EnumerateArray()
            .Select(attribute => (Key: NullText(attribute, "attributeName", "name"), Value: NullText(attribute, "attributeValue", "value")))
            .Where(x => !string.IsNullOrWhiteSpace(x.Key) && !string.IsNullOrWhiteSpace(x.Value))
            .GroupBy(x => x.Key!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.Last().Value!, StringComparer.OrdinalIgnoreCase);
    }

    public static RemoteProduct Product(string json, string barcode)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var contentId = Text(root, "contentId", "id", "productMainId");
        var variantId = NullText(root, "variantId", "id");
        var sku = NullText(root, "stockCode", "merchantSku");
        return new(contentId, variantId, NullText(root, "barcode") ?? barcode, sku, root.GetRawText());
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
            var lines = ReturnLines(claim);
            var status = ClaimStatus(claim); rows.Add(new(claimId, orderNumber, status, ClaimReasonCode(claim), ClaimReasonText(claim), FlexibleInstant(claim, "autoApproveDate", "actionDueDate", "dueDate"), Instant(claim, "lastModifiedDate") ?? DateTimeOffset.UnixEpoch, lines, claim.GetRawText(), NullText(claim, "cargoTrackingLink", "trackingLink")));
        }
        var page = Long(root, "page"); var totalPages = Long(root, "totalPages"); var hasMore = totalPages > 0 && page + 1 < totalPages;
        return new(rows, hasMore ? (page + 1).ToString(CultureInfo.InvariantCulture) : null, hasMore);
    }

    public static IReadOnlyList<RemoteReturnLine> ReturnLines(string json)
    {
        using var document = JsonDocument.Parse(json);
        return ReturnLines(document.RootElement);
    }

    private static IReadOnlyList<RemoteReturnLine> ReturnLines(JsonElement claim)
    {
        var lines = new List<RemoteReturnLine>();
        foreach (var entry in ClaimLineEntries(claim))
        {
            var lineId = Text(entry.Item, "id");
            var quantity = Decimal(entry.Item, "quantity", "returnedQuantity");
            if (quantity <= 0) quantity = 1;
            if (lineId.Length > 0 && entry.ExternalOrderLineId.Length > 0)
                lines.Add(new(lineId, entry.ExternalOrderLineId, quantity, entry.AlternateExternalOrderLineIds));
        }
        return lines
            .GroupBy(x => $"{x.ExternalLineId}:{x.ExternalOrderLineId}", StringComparer.Ordinal)
            .Select(group => group.First() with { Quantity = group.Sum(x => x.Quantity) })
            .ToList();
    }

    // getClaims contains enough order-line and customer identity data to
    // reconstruct a local read model when the order package itself is outside
    // Trendyol's order-history window. This is deliberately a read-only
    // fallback: it never calls a marketplace write endpoint and is replaced by
    // the authoritative order package if that package is later returned.
    public static RemoteOrder? OrderFromReturnClaim(string json)
    {
        using var document = JsonDocument.Parse(json); var claim = document.RootElement;
        var orderNumber = Text(claim, "orderNumber");
        if (string.IsNullOrWhiteSpace(orderNumber)) return null;

        var ordered = Instant(claim, "orderDate") ?? Instant(claim, "claimDate") ?? DateTimeOffset.UnixEpoch;
        var modified = Instant(claim, "lastModifiedDate") ?? Instant(claim, "claimDate") ?? ordered;
        var lines = new List<RemoteOrderLine>();
        foreach (var entry in ClaimLineEntries(claim))
        {
            if (entry.OrderLine is not { } orderLine) continue;
            AddReturnOrderLine(lines, entry.ExternalOrderLineId, orderLine, Decimal(entry.Item, "quantity", "returnedQuantity"));
        }

        var gross = Decimal(claim, "packageGrossAmount", "grossAmount", "packageTotalPrice");
        if (gross == 0) gross = lines.Sum(x => x.UnitPrice * x.Quantity);
        var packageId = Text(claim, "orderOutboundPackageId", "orderShipmentPackageId");
        if (string.IsNullOrWhiteSpace(packageId)) packageId = $"return-claim:{Text(claim, "claimId", "id")}";
        var allocations = lines.Select(x => new RemotePackageAllocation(x.ExternalLineId, x.Quantity, 0, x.Quantity, x.Quantity, 0)).ToList();
        var package = new RemotePackage(packageId, null, "Delivered", modified,
            NullText(claim, "cargoProviderName", "cargoProviderCode", "cargoProvider"),
            NullText(claim, "cargoTrackingNumber", "cargoSenderNumber", "trackingNumber"), allocations, gross, 0, gross);
        return new(orderNumber, orderNumber, ordered, modified, NullText(claim, "currencyCode") ?? "TRY", gross, 0, gross,
            CustomerSnapshot(claim), ObjectSnapshot(claim, "shipmentAddress"), ObjectSnapshot(claim, "invoiceAddress"), lines, [package], claim.GetRawText());
    }

    private static void AddReturnOrderLine(List<RemoteOrderLine> lines, string externalLineId, JsonElement orderLine, decimal quantity)
    {
        if (quantity <= 0) quantity = 1;
        var index = lines.FindIndex(x => string.Equals(x.ExternalLineId, externalLineId, StringComparison.Ordinal));
        if (index >= 0) { lines[index] = lines[index] with { Quantity = lines[index].Quantity + quantity }; return; }
        lines.Add(new(externalLineId, Text(orderLine, "merchantSku", "stockCode", "sku"), NullText(orderLine, "barcode"),
            Text(orderLine, "productName", "title"), quantity, Decimal(orderLine, "price", "lineUnitPrice", "amount"),
            Decimal(orderLine, "vatRate"), "Delivered", orderLine.GetRawText()));
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
            foreach (var item in attributes.EnumerateArray()) if (item.TryGetProperty("attribute", out var attribute)) rows.Add(new(resourceType, Text(attribute, "id"), parentExternalId, Text(attribute, "name"), Text(attribute, "name"), 0, true, true, item.GetRawText(), Bool(item, "required") || Bool(item, "isRequired"), Bool(item, "allowCustom") || Bool(item, "allowsCustomValue"), Bool(item, "allowMultipleAttributeValues") || Bool(item, "allowsMultipleValues")));
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
    private static IEnumerable<JsonElement> ClaimItems(JsonElement claim)
    {
        if (claim.TryGetProperty("claimItems", out var directClaimItems) && directClaimItems.ValueKind == JsonValueKind.Array)
            foreach (var claimItem in directClaimItems.EnumerateArray()) yield return claimItem;

        if (!claim.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array) yield break;
        foreach (var item in items.EnumerateArray())
        {
            if (item.TryGetProperty("claimItems", out var nestedClaimItems) && nestedClaimItems.ValueKind == JsonValueKind.Array)
            {
                foreach (var claimItem in nestedClaimItems.EnumerateArray()) yield return claimItem;
                continue;
            }

            // Historical fixtures exposed claim items directly in the items array.
            if (item.TryGetProperty("id", out _) && item.TryGetProperty("orderLineItemId", out _)) yield return item;
        }
    }
    private static IReadOnlyList<(JsonElement Item, string ExternalOrderLineId, JsonElement? OrderLine, IReadOnlyList<string> AlternateExternalOrderLineIds)> ClaimLineEntries(JsonElement claim)
    {
        var rows = new List<(JsonElement Item, string ExternalOrderLineId, JsonElement? OrderLine, IReadOnlyList<string> AlternateExternalOrderLineIds)>();
        WalkClaimLineEntries(claim, null, rows);
        return rows;
    }

    private static void WalkClaimLineEntries(JsonElement value, JsonElement? parentOrderLine, List<(JsonElement Item, string ExternalOrderLineId, JsonElement? OrderLine, IReadOnlyList<string> AlternateExternalOrderLineIds)> rows)
    {
        if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray()) WalkClaimLineEntries(item, parentOrderLine, rows);
            return;
        }
        if (value.ValueKind != JsonValueKind.Object) return;

        var currentOrderLine = parentOrderLine;
        if (value.TryGetProperty("orderLine", out var orderLine) && orderLine.ValueKind == JsonValueKind.Object)
            currentOrderLine = orderLine;

        if (value.TryGetProperty("claimItems", out var claimItems) && claimItems.ValueKind == JsonValueKind.Array)
        {
            foreach (var claimItem in claimItems.EnumerateArray())
            {
                // In the nested Trendyol shape, the parent orderLine.id is the
                // same identifier used by the order feed and therefore the
                // stable local OrderLine key. Some payloads also expose
                // orderLineItemId on the claim item, but that value is not the
                // order-line id used by the order snapshot. Keep it as a
                // fallback for the direct/root claimItems shape.
                var parentOrderLineId = currentOrderLine is { } parent ? Text(parent, "id", "lineId") : "";
                var claimOrderLineId = Text(claimItem, "orderLineItemId");
                var externalOrderLineId = string.IsNullOrWhiteSpace(parentOrderLineId) ? claimOrderLineId : parentOrderLineId;
                var alternates = new[] { parentOrderLineId, claimOrderLineId }
                    .Where(id => !string.IsNullOrWhiteSpace(id) && !string.Equals(id, externalOrderLineId, StringComparison.Ordinal))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                if (Text(claimItem, "id").Length > 0 && externalOrderLineId.Length > 0)
                    rows.Add((claimItem, externalOrderLineId, currentOrderLine, alternates));
            }
        }

        var directOrderLineId = Text(value, "orderLineItemId");
        if (Text(value, "id").Length > 0 && directOrderLineId.Length > 0)
            rows.Add((value, directOrderLineId, currentOrderLine, []));

        foreach (var property in value.EnumerateObject())
        {
            if (property.NameEquals("claimItems") || property.NameEquals("orderLine")) continue;
            if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                WalkClaimLineEntries(property.Value, currentOrderLine, rows);
        }
    }
    private static string ClaimStatus(JsonElement claim) { if (claim.TryGetProperty("claimItemStatus", out var value)) return value.ValueKind == JsonValueKind.Object ? Text(value, "name") : value.ToString(); foreach (var item in ClaimItems(claim)) if (item.TryGetProperty("claimItemStatus", out var status)) return status.ValueKind == JsonValueKind.Object ? Text(status, "name") : status.ToString(); return ""; }
    private static string? ClaimReasonCode(JsonElement claim) => NestedReason(claim, "code");
    private static string? ClaimReasonText(JsonElement claim) => NestedReason(claim, "name");
    private static string? NestedReason(JsonElement claim, string field) { foreach (var item in ClaimItems(claim)) if (item.TryGetProperty("customerClaimItemReason", out var reason)) return NullText(reason, field); return null; }
    private static string Snapshot(JsonElement value, params string[] fields) { var map = new Dictionary<string, JsonElement>(); foreach (var field in fields) if (value.TryGetProperty(field, out var item)) map[field] = item.Clone(); return JsonSerializer.Serialize(map); }
    private static string ObjectSnapshot(JsonElement value, string field) { if (value.TryGetProperty(field, out var item) && item.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)) return item.GetRawText(); return "{}"; }
    private static string CustomerSnapshot(JsonElement package)
    {
        var map = new Dictionary<string, JsonElement>();
        if (package.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in package.EnumerateObject())
                map[prop.Name] = prop.Value.Clone();
        }
        if ((!map.ContainsKey("customerFirstName") || string.IsNullOrWhiteSpace(map["customerFirstName"].ToString())) && package.TryGetProperty("shipmentAddress", out var sa))
        {
            if (sa.TryGetProperty("firstName", out var fn)) map["customerFirstName"] = fn.Clone();
            if (sa.TryGetProperty("lastName", out var ln)) map["customerLastName"] = ln.Clone();
        }
        if ((!map.ContainsKey("customerFirstName") || string.IsNullOrWhiteSpace(map["customerFirstName"].ToString())) && package.TryGetProperty("invoiceAddress", out var ia))
        {
            if (ia.TryGetProperty("firstName", out var fn)) map["customerFirstName"] = fn.Clone();
            if (ia.TryGetProperty("lastName", out var ln)) map["customerLastName"] = ln.Clone();
        }
        return JsonSerializer.Serialize(map);
    }

    private static string? FirstArrayText(JsonElement value, string field) => value.TryGetProperty(field, out var array) && array.ValueKind == JsonValueKind.Array && array.GetArrayLength() > 0 ? array[0].ToString() : null;
    private static string Text(JsonElement value, params string[] names) => NullText(value, names) ?? "";
    private static string? NullText(JsonElement value, params string[] names) { foreach (var name in names) if (value.TryGetProperty(name, out var item) && item.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)) return item.ToString(); return null; }
    private static decimal Decimal(JsonElement value, params string[] names) { foreach (var name in names) if (value.TryGetProperty(name, out var item) && (item.TryGetDecimal(out var result) || decimal.TryParse(item.ToString(), CultureInfo.InvariantCulture, out result))) return result; return 0; }
    private static decimal? DecimalNullable(JsonElement value, params string[] names)
    {
        foreach (var name in names)
        {
            if (!value.TryGetProperty(name, out var item)) continue;
            if (item.ValueKind == JsonValueKind.Object)
            {
                var nested = DecimalNullable(item, name is "price" ? "salePrice" : "listPrice");
                if (nested is not null) return nested;
            }
            else if (item.TryGetDecimal(out var result) || decimal.TryParse(item.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out result)) return result;
        }
        return null;
    }
    private static long Long(JsonElement value, string name) => value.TryGetProperty(name, out var item) && item.TryGetInt64(out var result) ? result : 0;
    private static bool Bool(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var item)) return false;
        return item.ValueKind == JsonValueKind.True
            || item.ValueKind == JsonValueKind.String && bool.TryParse(item.GetString(), out var parsed) && parsed
            || item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out var number) && number != 0;
    }
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
