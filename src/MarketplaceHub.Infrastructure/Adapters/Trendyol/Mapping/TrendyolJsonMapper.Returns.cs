using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using MarketplaceHub.Application;
using ReturnCustomerRequestPolicy = MarketplaceHub.Domain.ReturnCustomerRequestPolicy;
using ReturnRejectedPackageStates = MarketplaceHub.Domain.ReturnRejectedPackageStates;

namespace MarketplaceHub.Infrastructure.Adapters.Trendyol.Mapping;

public static partial class TrendyolJsonMapper
{
    // pageSize and requestedPage are the request's own: a page without totalPages (or page) is not taken as the last one
    // (B-2), it is followed while it comes back full.
    public static AdapterPageResult<RemoteReturnClaim> Returns(string json, int? pageSize = null, int? requestedPage = null)
    {
        using var document = JsonDocument.Parse(json); var root = document.RootElement; var rows = new List<RemoteReturnClaim>();
        var received = 0;
        foreach (var claim in ClaimContent(root))
        {
            received++;
            var claimId = Text(claim, "claimId", "id"); var orderNumber = Text(claim, "orderNumber"); if (claimId.Length == 0 || orderNumber.Length == 0) continue;
            var lines = ReturnLines(claim);
            var status = ClaimStatus(claim);
            var lastModifiedAt = Instant(claim, "lastModifiedDate");
            var claimDate = FlexibleInstant(claim, "claimDate", "createdDate", "requestDate");
            var remoteDueAt = FlexibleInstant(claim, "autoApproveDate", "actionDueDate", "dueDate");
            // Without a deadline from Trendyol the usual rule is applied, and the
            // claim says so: the panel shows it as an estimate.
            var actionDueAt = remoteDueAt
                ?? (IsWaitingInAction(status) && lastModifiedAt is { } waitingInActionAt ? (DateTimeOffset?)waitingInActionAt.AddHours(48) : null)
                ?? (claimDate is { } requestedAt ? (DateTimeOffset?)requestedAt.AddDays(7) : null);
            rows.Add(new(claimId, orderNumber, status, ClaimReasonCode(claim), ClaimReasonText(claim), actionDueAt, lastModifiedAt, lines, claim.GetRawText(), ReturnCargoProvider(claim), ReturnCargoField(claim, "cargoTrackingNumber", "cargoSenderNumber", "trackingNumber"), ReturnCargoField(claim, "cargoTrackingLink", "trackingLink"),
                ActionDueAtEstimated: remoteDueAt is null && actionDueAt is not null, RejectedPackageState: RejectedPackageState(claim, status),
                // The request date is the documented claimDate only (GMT epoch milliseconds); no other field stands in.
                RequestedAt: FlexibleInstant(claim, "claimDate"),
                CustomerRequest: ReturnCustomerRequestPolicy.FromTrendyol(ClaimReasonCode(claim)),
                ReplacementPackage: ReplacementPackage(claim), ReplacementInfoPresent: ReplacementInfo(claim) is not null));
        }
        var page = NullableLong(root, "page") ?? requestedPage ?? 0;
        var hasMore = NullableLong(root, "totalPages") is { } totalPages
            ? totalPages > 0 && page + 1 < totalPages
            : received > 0 && pageSize is > 0 && received >= pageSize;
        return new(rows, hasMore ? (page + 1).ToString(CultureInfo.InvariantCulture) : null, hasMore);
    }

    // getClaims rejectedPackageInfo, read on a rejected claim only: the field is missing while no package back to the
    // customer exists, cargoTrackingLink comes once the package is on its way, dontShipBack says it is never sent back.
    // A value of another shape is not guessed at (null).
    internal static string? RejectedPackageState(JsonElement claim, string rawStatus)
    {
        if (!string.Equals(rawStatus, "Rejected", StringComparison.OrdinalIgnoreCase)) return null;
        if (!claim.TryGetProperty("rejectedPackageInfo", out var package) && !claim.TryGetProperty("rejectedpackageinfo", out package))
            return ReturnRejectedPackageStates.None;
        if (package.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return ReturnRejectedPackageStates.None;
        if (package.ValueKind != JsonValueKind.Object) return null;
        if (package.TryGetProperty("dontShipBack", out var dontShipBack) && dontShipBack.ValueKind == JsonValueKind.True) return ReturnRejectedPackageStates.DontShipBack;
        return string.IsNullOrWhiteSpace(NullText(package, "cargoTrackingLink")) ? ReturnRejectedPackageStates.AwaitingCargo : ReturnRejectedPackageStates.Shipped;
    }

    // getClaims replacementOutboundpackageinfo ("Değişim giden paket bilgisi (değişim yoksa boş)", trendyol-api-son.md:6850,
    // :17450): the exchange package when it names its package id (packageid, not 0); its carrier fields as given. A
    // missing, empty or id-less block is no known package (null), never "no exchange".
    internal static RemoteReplacementPackage? ReplacementPackage(JsonElement claim)
    {
        if (ReplacementInfo(claim) is not { ValueKind: JsonValueKind.Object } package) return null;
        if (NullText(package, "packageid", "packageId")?.Trim() is not { Length: > 0 } packageId || packageId == "0") return null;
        return new(Clip(packageId, 64)!, Clip(NullText(package, "cargoProviderName"), 128), Clip(NullText(package, "cargoTrackingNumber", "cargoSenderNumber"), 128), Clip(NullText(package, "cargoTrackingLink"), 2048));
    }

    // The block as the claim carries it (any value), or null when the claim has no such field.
    private static JsonElement? ReplacementInfo(JsonElement claim) =>
        claim.TryGetProperty("replacementOutboundpackageinfo", out var package) || claim.TryGetProperty("replacementOutboundPackageInfo", out package) ? package : null;

    public static IReadOnlyList<RemoteReturnLine> ReturnLines(string json)
    {
        using var document = JsonDocument.Parse(json);
        return ReturnLines(document.RootElement);
    }

    // getClaimItemAudits: an array of status changes (previousStatus, newStatus, userInfoDocument.executorApp and
    // executorUser, date in epoch milliseconds), oldest first. An entry without a new status or a date is left out.
    public static IReadOnlyList<RemoteReturnLineAudit> ReturnLineAudits(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var items = root.ValueKind == JsonValueKind.Array ? root : root.ValueKind == JsonValueKind.Object && root.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array ? content : default;
        if (items.ValueKind != JsonValueKind.Array) return [];
        var audits = new List<RemoteReturnLineAudit>();
        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var newStatus = NullText(item, "newStatus");
            // O9: a null or text date is no date (TryGetInt64 would throw on it); the entry is left out like a missing one.
            if (string.IsNullOrWhiteSpace(newStatus) || !item.TryGetProperty("date", out var date) || RemoteJson.EpochMilliseconds(date) is not { } at) continue;
            var user = item.TryGetProperty("userInfoDocument", out var info) && info.ValueKind == JsonValueKind.Object ? info : default;
            audits.Add(new(Clip(NullText(item, "previousStatus"), 64), newStatus.Trim(), user.ValueKind == JsonValueKind.Object ? Clip(NullText(user, "executorApp"), 64) : null, user.ValueKind == JsonValueKind.Object ? Clip(NullText(user, "executorUser"), 128) : null, at));
        }
        return audits.OrderBy(x => x.At).ToList();
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
                lines.Add(new(lineId, entry.ExternalOrderLineId, quantity, entry.AlternateExternalOrderLineIds, ItemStatus(entry.Item), Clip(NullText(entry.Item, "acceptDetail"), 32), Clip(NullText(entry.Item, "note"), 1000), Flag(entry.Item, "resolved"), Flag(entry.Item, "acceptedBySeller")));
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

        // D13: a claim with neither an order date nor a claim date is not projected as an order placed in 1970. Nothing is
        // guessed (ADAPTOR-TY-HB-1): a claim without its order's own date (getClaims carries orderDate) is not dated by
        // its claim date, a line without a readable price is not priced 0 and an unknown currency is not taken as lira;
        // each builds no order and the claim waits (RETURN_ORDER_NOT_FOUND) until the order itself is read.
        if (OrderDate(claim) is not { } ordered) return null;
        // getClaims has no currency field (trendyol-api-son.md, getClaims sample): lira, as the claim is read for the TR
        // core channel only; a currency it does send must be one the panel knows.
        var rawCurrency = NullText(claim, "currencyCode");
        var currency = rawCurrency is null ? "TRY" : RefusedOrders.Currency(rawCurrency);
        if (currency is null) return null;
        var packageOccurredAt = Instant(claim, "lastModifiedDate") ?? Instant(claim, "claimDate") ?? ordered;
        // This is a partial order projection reconstructed from a return claim.
        // Its claim timestamp can be newer than the original order timestamp,
        // but it must not prevent a later authoritative order/package read from
        // replacing the incomplete customer, address, and invoice snapshots.
        var modified = DateTimeOffset.UnixEpoch;
        // A "Teslim edilemeyen gönderi" claim (reason UNDELIVERED) is a package the carrier could not deliver and brought
        // back: it never reached the customer, so it is not projected as delivered (live 20 Oct 2026: nine such orders
        // showed "Teslim Edildi" with the claim's date as their delivery date).
        var undelivered = string.Equals(ClaimReasonCode(claim), "UNDELIVERED", StringComparison.OrdinalIgnoreCase);
        var lineStatus = undelivered ? "UnDelivered" : "Delivered";
        var lines = new List<RemoteOrderLine>();
        foreach (var entry in ClaimLineEntries(claim))
        {
            if (entry.OrderLine is not { } orderLine) continue;
            if (!AddReturnOrderLine(lines, entry.ExternalOrderLineId, orderLine, Decimal(entry.Item, "quantity", "returnedQuantity"), lineStatus)) return null;
        }

        var gross = Decimal(claim, "packageGrossAmount", "grossAmount", "packageTotalPrice");
        if (gross == 0) gross = lines.Sum(x => x.UnitPrice * x.Quantity);
        var packageId = Text(claim, "orderOutboundPackageId", "orderShipmentPackageId");
        if (string.IsNullOrWhiteSpace(packageId)) packageId = $"return-claim:{Text(claim, "claimId", "id")}";
        var allocations = lines.Select(x => new RemotePackageAllocation(x.ExternalLineId, x.Quantity, 0, x.Quantity, undelivered ? 0 : x.Quantity, 0)).ToList();
        var package = new RemotePackage(packageId, null, lineStatus, packageOccurredAt,
            NullText(claim, "cargoProviderName", "cargoProviderCode", "cargoProvider"),
            NullText(claim, "cargoTrackingNumber", "cargoSenderNumber", "trackingNumber"), allocations, gross, 0, gross, CreatedBy: NormalizeCreatedBy(NullText(claim, "createdBy")));
        return new(orderNumber, orderNumber, ordered, modified, currency, gross, 0, gross,
            ReturnClaimCustomerSnapshot(claim), ObjectSnapshot(claim, "shipmentAddress"), ObjectSnapshot(claim, "invoiceAddress"), lines, [package], claim.GetRawText(),
            StoreFrontCode: ClaimStorefront(claim));
    }

    // The storefront a claim names, if it names one: a storefront code, or the order's country as the order package and
    // its webhook carry it (orderCountryCode). getClaims is read without a storefront header and its address is Trendyol's
    // Istanbul export depot for a cross-border order, so a claim naming none leaves the storefront unknown (null), never TR.
    private static string? ClaimStorefront(JsonElement claim) =>
        NullText(claim, "storeFrontCode", "storefrontCode", "orderCountryCode")?.Trim().ToUpperInvariant() is { Length: 2 } code
            && TrendyolReadStorefronts.Codes.Contains(code, StringComparer.Ordinal)
            ? code
            : null;

    // A customer snapshot read for a claim-built order whose storefront is still unknown keeps the RETURN_CLAIM mark, so
    // the order hydration still completes it and the report keeps its country unknown.
    public static string WithReturnClaimReadModelMark(string customerSnapshotJson)
    {
        JsonObject snapshot;
        try { snapshot = JsonNode.Parse(customerSnapshotJson) as JsonObject ?? new JsonObject(); }
        catch (JsonException) { snapshot = new JsonObject(); }
        snapshot["_ravenciaReadModelSource"] = "RETURN_CLAIM";
        return snapshot.ToJsonString();
    }

    public static bool IsReturnClaimReadModelSnapshot(string? customerSnapshotJson)
    {
        if (string.IsNullOrWhiteSpace(customerSnapshotJson)) return false;
        try
        {
            using var document = JsonDocument.Parse(customerSnapshotJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            if (root.TryGetProperty("_ravenciaReadModelSource", out var source)
                && string.Equals(source.GetString(), "RETURN_CLAIM", StringComparison.Ordinal)) return true;

            // Older reconstructed rows were stored before the explicit marker
            // existed. These claim-only identity fields let reconciliation
            // repair them without treating a claim timestamp as order truth.
            return root.TryGetProperty("claimId", out _)
                && root.TryGetProperty("claimDate", out _)
                && (root.TryGetProperty("orderOutboundPackageId", out _)
                    || root.TryGetProperty("orderShipmentPackageId", out _));
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string ReturnClaimCustomerSnapshot(JsonElement claim)
    {
        var snapshot = JsonNode.Parse(CustomerSnapshot(claim)) as JsonObject ?? new JsonObject();
        snapshot["_ravenciaReadModelSource"] = "RETURN_CLAIM";
        return snapshot.ToJsonString();
    }

    // False when the order line carries no readable price: the caller builds no order rather than one priced 0.
    private static bool AddReturnOrderLine(List<RemoteOrderLine> lines, string externalLineId, JsonElement orderLine, decimal quantity, string status)
    {
        if (quantity <= 0) quantity = 1;
        var index = lines.FindIndex(x => string.Equals(x.ExternalLineId, externalLineId, StringComparison.Ordinal));
        if (index >= 0) { lines[index] = lines[index] with { Quantity = lines[index].Quantity + quantity }; return true; }
        if (!TryDecimal(orderLine, out var unitPrice, "price", "lineUnitPrice", "amount")) return false;
        lines.Add(new(externalLineId, Text(orderLine, "merchantSku", "stockCode", "sku"), NullText(orderLine, "barcode"),
            Text(orderLine, "productName", "title"), quantity, unitPrice,
            TryDecimal(orderLine, out var vatRate, "vatRate") ? vatRate : null, status, orderLine.GetRawText()));
        return true;
    }

    private static string? NormalizeCreatedBy(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToLowerInvariant();

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
    // A claim page: its content list, or, live since 5 Nov 2026, no list at all on an empty claimItemStatus bucket. That
    // answer is "no claims" only when the page says so itself (totalElements 0); anything else without the list stays a
    // broken contract, its top-level keys named in the error so the log shows what came instead.
    private static IEnumerable<JsonElement> ClaimContent(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array) return content.EnumerateArray();
        if (root.ValueKind == JsonValueKind.Object && (!root.TryGetProperty("content", out var missing) || missing.ValueKind == JsonValueKind.Null)
            && NullableLong(root, "totalElements") == 0) return [];
        var keys = root.ValueKind == JsonValueKind.Object ? string.Join(',', root.EnumerateObject().Select(property => $"{property.Name}:{property.Value.ValueKind}")) : root.ValueKind.ToString();
        throw new JsonException($"Trendyol claim page has no content list ({keys}).");
    }

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
    // A claim item's true/false field; null when Trendyol leaves it out, so an unknown outcome is not read as "no".
    private static bool? Flag(JsonElement item, string name) => item.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;
    private static string? Clip(string? value, int length) => string.IsNullOrWhiteSpace(value) ? null : value.Trim()[..Math.Min(value.Trim().Length, length)];
    private static string? ItemStatus(JsonElement item)
    {
        if (!item.TryGetProperty("claimItemStatus", out var status)) return null;
        var text = status.ValueKind == JsonValueKind.Object ? Text(status, "name") : status.ValueKind == JsonValueKind.String ? status.GetString() : null;
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }
    private static string ClaimStatus(JsonElement claim) { if (claim.TryGetProperty("claimItemStatus", out var value)) return value.ValueKind == JsonValueKind.Object ? Text(value, "name") : value.ToString(); foreach (var item in ClaimItems(claim)) if (item.TryGetProperty("claimItemStatus", out var status)) return status.ValueKind == JsonValueKind.Object ? Text(status, "name") : status.ToString(); return ""; }
    private static bool IsWaitingInAction(string status) => string.Equals(status, "WaitingInAction", StringComparison.OrdinalIgnoreCase)
        || string.Equals(status, "WAITING_IN_ACTION", StringComparison.OrdinalIgnoreCase);
    private static string? ClaimReasonCode(JsonElement claim) => NestedReason(claim, "code");
    private static string? ClaimReasonText(JsonElement claim)
    {
        foreach (var item in ClaimItems(claim))
        {
            var customerNote = NullText(item, "customerNote");
            if (!string.IsNullOrWhiteSpace(customerNote)) return customerNote.Trim();
        }
        return NestedReason(claim, "name");
    }
    private static string? NestedReason(JsonElement claim, string field) { foreach (var item in ClaimItems(claim)) if (item.TryGetProperty("customerClaimItemReason", out var reason)) return NullText(reason, field); return null; }
    private static string? ReturnCargoProvider(JsonElement claim) =>
        ReturnCargoField(claim, "cargoProviderName", "cargoProviderCode", "cargoProvider");
    private static string? ReturnCargoField(JsonElement claim, params string[] names)
    {
        // Trendyol puts a rejected return shipment back to the customer in
        // rejectedPackageInfo. replacementOutboundpackageinfo is for exchange
        // shipments and must not replace the rejected return's cargo details.
        var rejectedCargo = NestedCargoText(claim, "rejectedPackageInfo", names);
        if (!string.IsNullOrWhiteSpace(rejectedCargo)) return rejectedCargo;

        var rootValue = ScalarText(claim, names);
        if (!string.IsNullOrWhiteSpace(rootValue)) return rootValue;

        return NestedCargoText(claim, "returnPackageInfo", names);
    }
    private static string? NestedCargoText(JsonElement value, string objectName, params string[] names)
    {
        if (!value.TryGetProperty(objectName, out var nested)) return null;
        if (nested.ValueKind == JsonValueKind.Object) return ScalarText(nested, names);
        if (nested.ValueKind != JsonValueKind.Array) return null;
        foreach (var item in nested.EnumerateArray())
        {
            var result = item.ValueKind == JsonValueKind.Object ? ScalarText(item, names) : null;
            if (!string.IsNullOrWhiteSpace(result)) return result;
        }
        return null;
    }
}
