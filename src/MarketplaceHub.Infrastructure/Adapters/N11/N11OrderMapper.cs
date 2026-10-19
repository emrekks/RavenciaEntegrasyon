using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using MarketplaceHub.Application;

namespace MarketplaceHub.Infrastructure.Adapters.N11;

// Maps the n11 shipment package list (GET rest/delivery/v1/shipmentPackages)
// to the package-shaped order stream the processor already merges for
// Trendyol: one RemoteOrder per package, keyed by the n11 order number.
public static class N11OrderMapper
{
    public static AdapterPageResult<RemoteOrder> Packages(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            throw new JsonException("n11 shipment package page has no content array.");

        var rows = new List<RemoteOrder>();
        var issues = new List<AdapterPageIssue>();
        foreach (var package in content.EnumerateArray())
        {
            var orderNumber = Text(package, "orderNumber");
            if (orderNumber is null)
            {
                issues.Add(new("ORDER_PACKAGE_INVALID", Text(package, "id") ?? "unknown", "N11 sipariş paketi içeri alınmadı: sipariş numarası yok."));
                continue;
            }
            try { rows.Add(Package(package, orderNumber, issues)); }
            catch (JsonException exception)
            {
                issues.Add(new("ORDER_PACKAGE_INVALID", Text(package, "id") ?? orderNumber, $"N11 sipariş paketi {orderNumber} içeri alınmadı: {exception.Message}"));
            }
        }

        var page = Long(root, "page") ?? Long(root, "number") ?? 0;
        var totalPages = Long(root, "totalPages") ?? 0;
        var hasMore = content.GetArrayLength() > 0 && page + 1 < totalPages;
        var total = Long(root, "totalElements");
        return new(LinkSplitPackages(rows), hasMore ? (page + 1).ToString(CultureInfo.InvariantCulture) : null, hasMore, total is > int.MaxValue ? int.MaxValue : (int?)total, issues);
    }

    private static RemoteOrder Package(JsonElement package, string orderNumber, List<AdapterPageIssue> issues)
    {
        var cargoCode = Text(package, "cargoTrackingNumber");
        // Location-specific deliveries come without a package number; the
        // cargo campaign code (or the order itself) identifies them instead.
        var packageId = Text(package, "id") ?? (cargoCode is null ? $"order:{orderNumber}" : $"cargo:{cargoCode}");
        var lines = new List<RemoteOrderLine>();
        var allocations = new List<RemotePackageAllocation>();
        decimal gross = 0, net = 0;
        var hasInvoiceAmounts = true;
        if (package.TryGetProperty("lines", out var lineArray) && lineArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var line in lineArray.EnumerateArray())
            {
                var lineId = Text(line, "orderLineId");
                if (lineId is null || Decimal(line, "quantity") is not { } quantity || quantity <= 0 || Decimal(line, "price") is not { } price || price < 0)
                {
                    issues.Add(new("ORDER_PACKAGE_LINE_INVALID", $"{packageId}:{lineId ?? "?"}", $"N11 sipariş paketi {packageId} alındı ancak bir ürün satırında satır kimliği, adet veya fiyat eksik."));
                    continue;
                }
                var barcode = Text(line, "barcode");
                var sku = Text(line, "stockCode") ?? barcode ?? "";
                var title = Text(line, "productName") ?? sku;
                lines.Add(new(lineId, sku, barcode, title, quantity, price, Decimal(line, "vatRate") ?? 0, Text(line, "orderItemLineItemStatusName") ?? "", line.GetRawText()));
                allocations.Add(new(lineId, quantity, 0, 0, 0, 0));
                gross += price * quantity;
                if (Decimal(line, "sellerInvoiceAmount") is { } invoiceAmount) net += invoiceAmount;
                else hasInvoiceAmounts = false;
            }
        }
        if (lines.Count == 0) throw new JsonException("package has no usable product line");

        // sellerInvoiceAmount is the store's billable amount per line: price x
        // quantity minus the store's own discounts (n11 discounts are not the
        // store's and stay out of the seller's invoice).
        if (!hasInvoiceAmounts) net = gross - lines.Sum(line => Decimal(line.SourceSnapshotJson, "totalSellerDiscountPrice") ?? 0);
        net = Math.Clamp(net, 0, gross);
        var modified = Instant(package, "lastModifiedDate") ?? DateTimeOffset.UnixEpoch;
        var ordered = CreatedAt(package) ?? modified;
        var status = Text(package, "shipmentPackageStatus") ?? "";
        var remotePackage = new RemotePackage(packageId, null, status, modified, Text(package, "cargoProviderName") ?? Text(package, "shipmentCompanyId"), cargoCode, allocations, gross, gross - net, net);
        return new(orderNumber, orderNumber, ordered, modified, "TRY", gross, gross - net, net,
            CustomerSnapshot(package), AddressSnapshot(package, "shippingAddress"), AddressSnapshot(package, "billingAddress"),
            lines, [remotePackage], package.GetRawText(), Instant(package, "agreedDeliveryDate"));
    }

    // A split leaves the original package as UnPacked and creates new packages
    // for the same order. n11 does not name the origin, so packages of an order
    // that has exactly one UnPacked package in the page are marked as its
    // replacements; the order merge then stops counting the original lines.
    private static IReadOnlyList<RemoteOrder> LinkSplitPackages(List<RemoteOrder> rows)
    {
        var origins = rows
            .SelectMany(row => row.Packages.Select(package => (row.ExternalOrderId, Package: package)))
            .Where(x => IsUnpacked(x.Package.RawStatus))
            .GroupBy(x => x.ExternalOrderId, StringComparer.Ordinal)
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single().Package.ExternalPackageId, StringComparer.Ordinal);
        if (origins.Count == 0) return rows;
        return rows.Select(row => origins.TryGetValue(row.ExternalOrderId, out var origin)
                ? row with { Packages = row.Packages.Select(package => IsUnpacked(package.RawStatus) ? package : package with { OriginExternalPackageId = origin }).ToList() }
                : row)
            .ToList();
    }

    private static bool IsUnpacked(string rawStatus) => string.Equals(rawStatus.Trim(), "UnPacked", StringComparison.OrdinalIgnoreCase);

    private static DateTimeOffset? CreatedAt(JsonElement package)
    {
        if (!package.TryGetProperty("packageHistories", out var histories) || histories.ValueKind != JsonValueKind.Array) return null;
        DateTimeOffset? created = null, earliest = null;
        foreach (var history in histories.EnumerateArray())
        {
            if (Instant(history, "createdDate") is not { } at) continue;
            if (earliest is null || at < earliest) earliest = at;
            if (string.Equals(Text(history, "status"), "Created", StringComparison.OrdinalIgnoreCase) && (created is null || at < created)) created = at;
        }
        return created ?? earliest;
    }

    // Keeps the raw package fields and adds the names the panel and invoicing
    // read for every platform (customerName, customerFirstName/LastName, email).
    internal static string CustomerSnapshot(JsonElement package)
    {
        var snapshot = JsonNode.Parse(package.GetRawText())!.AsObject();
        snapshot.Remove("lines");
        var fullName = Text(package, "customerfullName")
            ?? (package.TryGetProperty("billingAddress", out var billing) ? Text(billing, "fullName") : null);
        if (fullName is not null)
        {
            var (first, last) = SplitName(fullName);
            snapshot["customerName"] = fullName;
            snapshot["customerFirstName"] = first;
            snapshot["customerLastName"] = last;
        }
        if (Text(package, "customerEmail") is { } email) snapshot["email"] = email;
        return snapshot.ToJsonString();
    }

    // n11 sends one free-text address line; it is kept as fullAddress and
    // address1 next to the city, district and neighbourhood fields.
    internal static string AddressSnapshot(JsonElement package, string field)
    {
        if (!package.TryGetProperty(field, out var address) || address.ValueKind != JsonValueKind.Object) return "{}";
        var snapshot = JsonNode.Parse(address.GetRawText())!.AsObject();
        if (Text(address, "address") is { } line)
        {
            snapshot["fullAddress"] = line;
            snapshot["address1"] = line;
        }
        if (Text(address, "fullName") is { } fullName)
        {
            var (first, last) = SplitName(fullName);
            snapshot["firstName"] = first;
            snapshot["lastName"] = last;
        }
        if (Text(address, "gsm") is { } phone) snapshot["phone"] = phone;
        return snapshot.ToJsonString();
    }

    private static (string First, string Last) SplitName(string fullName)
    {
        var name = fullName.Trim();
        var separator = name.LastIndexOf(' ');
        return separator <= 0 ? (name, "") : (name[..separator].Trim(), name[(separator + 1)..].Trim());
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

    private static decimal? Decimal(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number)) return number;
        return value.ValueKind == JsonValueKind.String && decimal.TryParse(value.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
    }

    private static decimal? Decimal(string json, string property)
    {
        using var document = JsonDocument.Parse(json);
        return Decimal(document.RootElement, property);
    }

    private static long? Long(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : null;

    // n11 dates are epoch milliseconds.
    private static DateTimeOffset? Instant(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var milliseconds) && milliseconds > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds(milliseconds)
            : null;
}
