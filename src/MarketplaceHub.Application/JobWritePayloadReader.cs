using System.Text.Json;

namespace MarketplaceHub.Application;

// What a product write job carried to the marketplace, read only from its stored payload (no marketplace call): the
// product, the parts it sends (one marketplace request each), the fields the items of each part hold, the barcodes and
// attribute ids in them, and the marketplace request ids the job kept. A part with no items was skipped without a call.
public sealed record JobWriteSummaryView(
    string? ProductTitle,
    IReadOnlyList<string> ModelCodes,
    string? Mode,
    string? Phase,
    IReadOnlyList<JobWritePartView> Parts,
    IReadOnlyList<JobWriteRequestView> Requests)
{
    // Other write jobs (JobSentPayloadReader): a line about what the job sends, why it was sent (a price/stock trigger)
    // and each item's stored answer.
    public string? Note { get; init; }
    public string? Trigger { get; init; }
    public IReadOnlyList<JobSentItemView>? Items { get; init; }
}

// Key: CREATE, UNAPPROVED (the whole product, a card not yet approved), CONTENT, VARIANTS or DELIVERY. ClearedFields:
// the fields an item sent as null. Trendyol deletes such a field on the product, so the drawer lists them ("silinir")
// instead of hiding them. SharedCards and SharedItems: the part went in one request together with other cards of the same
// send (a grouped Trendyol update): how many cards and items that request carried in all.
public sealed record JobWritePartView(string Key, int ItemCount, IReadOnlyList<string> Fields, IReadOnlyList<string> Barcodes, int ContentCount, IReadOnlyList<JobWriteAttributeView> Attributes, IReadOnlyList<string>? ClearedFields = null, int? SharedCards = null, int? SharedItems = null);

// Name: the marketplace's attribute name from the connection's reference data; null when it is not known.
public sealed record JobWriteAttributeView(string Id, string? Name);

// Key: PRODUCT (the product request's batch id) or the phase the id belongs to (e.g. POLL_DELIVERY).
public sealed record JobWriteRequestView(string Key, string Id);

public static class JobWritePayloadReader
{
    // Product jobs about one card. An approval check sends nothing: it only names the request it reads the answer of.
    public static bool AppliesTo(string jobType) => jobType is MarketplaceJobTypes.ProductCreate or MarketplaceJobTypes.ProductUpdate
        or MarketplaceJobTypes.ProductArchive or MarketplaceJobTypes.ProductApprovalReconcile;

    public static JobWriteSummaryView? Read(string jobType, string payloadJson)
    {
        if (!AppliesTo(jobType)) return null;
        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            var parts = new List<JobWritePartView>();
            var requests = new List<JobWriteRequestView>();
            var mode = Text(root, "Mode");
            var phase = Text(root, "Phase");
            if (jobType != MarketplaceJobTypes.ProductUpdate)
            {
                var key = jobType == MarketplaceJobTypes.ProductCreate ? "CREATE" : jobType == MarketplaceJobTypes.ProductArchive ? "ARCHIVE" : null;
                if (key is not null && Part(key, Text(root, "PayloadJson")) is { } single) parts.Add(single);
                if (Text(root, "ExternalOperationId") is { } productRequestId) requests.Add(new("PRODUCT", productRequestId));
            }
            else
            {
                var content = Text(root, "ApprovedContentPayloadJson");
                var variants = Text(root, "ApprovedVariantPayloadJson");
                var delivery = Text(root, "ApprovedDeliveryPayloadJson");
                // An approved card goes in three requests (content, variants, delivery); otherwise the whole product goes
                // at once (PTT AVM sends that way whatever the mode, with the approved parts left empty).
                var approved = string.Equals(mode, "APPROVED", StringComparison.OrdinalIgnoreCase)
                    && (!string.IsNullOrWhiteSpace(content) || !string.IsNullOrWhiteSpace(variants));
                var sources = approved
                    ? new[] { ("CONTENT", content), ("VARIANTS", variants), ("DELIVERY", delivery) }
                    : new[] { ("UNAPPROVED", Text(root, "UnapprovedPayloadJson")), ("DELIVERY", delivery) };
                var shared = SharedRequests(root);
                foreach (var (key, json) in sources)
                    if (Part(key, json) is { } part)
                        parts.Add(shared.TryGetValue(key, out var request) ? part with { SharedCards = request.Cards, SharedItems = request.Items } : part);
                if (Text(root, "ProductOperationId") is { } productRequest) requests.Add(new("PRODUCT", productRequest));
                if (Text(root, "ExternalOperationId") is { } phaseRequest && requests.All(x => x.Id != phaseRequest))
                    requests.Add(new(phase ?? "PHASE", phaseRequest));
            }
            return new JobWriteSummaryView(null, [], mode, phase, parts, requests);
        }
        catch (JsonException) { return null; }
    }

    // One request's items: a root array, { items: [...] } or n11's { payload: { skus: [...] } }. Null when the part
    // holds no JSON at all (it is not part of this job); a part with an empty list counts zero items.
    public static JobWritePartView? Part(string key, string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            var items = Items(document.RootElement);
            if (items is null) return null;
            var fields = new List<string>();
            var cleared = new List<string>();
            var barcodes = new List<string>();
            var attributes = new List<string>();
            var contents = 0;
            var count = 0;
            foreach (var item in items.Value.EnumerateArray())
            {
                count++;
                if (item.ValueKind != JsonValueKind.Object) continue;
                foreach (var property in item.EnumerateObject())
                {
                    var target = property.Value.ValueKind == JsonValueKind.Null ? cleared : fields;
                    if (!target.Contains(property.Name, StringComparer.Ordinal)) target.Add(property.Name);
                }
                if ((Text(item, "barcode") ?? Text(item, "merchantSku") ?? Text(item, "stockCode") ?? Text(item, "code")) is { } barcode && !barcodes.Contains(barcode, StringComparer.OrdinalIgnoreCase))
                    barcodes.Add(barcode);
                if (Property(item, "contentId") is { ValueKind: JsonValueKind.Number or JsonValueKind.String }) contents++;
                if (Property(item, "attributes") is { ValueKind: JsonValueKind.Array } list)
                    foreach (var attribute in list.EnumerateArray())
                        if (attribute.ValueKind == JsonValueKind.Object && Property(attribute, "attributeId") is { } id
                            && (id.ValueKind == JsonValueKind.Number ? id.GetRawText() : id.ValueKind == JsonValueKind.String ? id.GetString() : null) is { Length: > 0 } attributeId
                            && !attributes.Contains(attributeId, StringComparer.Ordinal))
                            attributes.Add(attributeId);
            }
            return new JobWritePartView(key, count, fields, barcodes, contents, attributes.Select(x => new JobWriteAttributeView(x, null)).ToList(), cleared);
        }
        catch (JsonException) { return null; }
    }

    // The shared requests a grouped Trendyol update went in (ProductUpdateJobPayload.SharedRequests), by part.
    private static Dictionary<string, (int Cards, int Items)> SharedRequests(JsonElement root)
    {
        var shared = new Dictionary<string, (int Cards, int Items)>(StringComparer.OrdinalIgnoreCase);
        if (Property(root, "SharedRequests") is not { ValueKind: JsonValueKind.Array } list) return shared;
        foreach (var request in list.EnumerateArray())
            if (Text(request, "Part") is { } part
                && Property(request, "Cards") is { ValueKind: JsonValueKind.Number } cards && cards.TryGetInt32(out var cardCount)
                && Property(request, "Items") is { ValueKind: JsonValueKind.Number } items && items.TryGetInt32(out var itemCount))
                shared[part] = (cardCount, itemCount);
        return shared;
    }

    private static JsonElement? Items(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array) return root;
        if (root.ValueKind != JsonValueKind.Object) return null;
        if (Property(root, "items") is { ValueKind: JsonValueKind.Array } items) return items;
        if (Property(root, "payload") is { ValueKind: JsonValueKind.Object } payload && Property(payload, "skus") is { ValueKind: JsonValueKind.Array } skus) return skus;
        return null;
    }

    private static JsonElement? Property(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        foreach (var property in element.EnumerateObject())
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) return property.Value;
        return null;
    }

    private static string? Text(JsonElement element, string name) =>
        Property(element, name) is { ValueKind: JsonValueKind.String } value && value.GetString() is { Length: > 0 } text && !string.IsNullOrWhiteSpace(text) ? text : null;
}
