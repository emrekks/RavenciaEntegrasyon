using System.Text.Json;
using MarketplaceHub.Application;

namespace MarketplaceHub.Infrastructure.Adapters.Hepsiburada;

internal static partial class HepsiburadaJsonMapper
{
    public static bool InvoiceUploaded(JsonElement root)
    {
        var status = InvoiceStatus(root);
        return NormalizeInvoiceStatus(status) switch
        {
            "INVOICED" => true,
            "NOT_INVOICED" => false,
            _ => throw new JsonException("Hepsiburada sipariş yanıtında sipariş, kalem veya paket düzeyinde geçerli fatura durumu yok.")
        };
    }

    public static string? InvoiceStatus(JsonElement root) => InvoiceStatus(root, out _);

    // infoMissing: some lines (or packages) say whether an invoice is attached and others leave it out. The order's
    // invoice state is then unknown (null), never concluded from the lines that do say it (5 Nov 2026, DO3).
    public static string? InvoiceStatus(JsonElement root, out bool infoMissing)
    {
        infoMissing = false;
        var order = Unwrap(root);
        var orderStatus = InvoiceStatusValue(order);
        if (orderStatus is not null) return orderStatus;

        var lines = Find(order, "lineItems", "LineItems", "items", "Items", "orderItems", "OrderItems");
        if (lines.ValueKind == JsonValueKind.Object && TryFind(lines, out var nested, "items", "Items", "lineItems", "LineItems")) lines = nested;
        var lineStatus = UniformInvoiceStatus(lines, out infoMissing);
        if (infoMissing) return null;
        if (lineStatus is not null) return lineStatus;

        var packageStatus = UniformInvoiceStatus(Find(order, "packages", "Packages", "shipments", "Shipments"), out infoMissing);
        return infoMissing ? null : packageStatus;
    }

    public static RemotePackageInvoiceObservation? InvoiceObservationForPackage(JsonElement root, string packageNumber) =>
        InvoiceObservationForPackage(root, packageNumber, out _);

    // infoMissing: the package's own items leave the invoice information out on some of them: unknown, and the
    // order-level fallback is not consulted for it.
    public static RemotePackageInvoiceObservation? InvoiceObservationForPackage(JsonElement root, string packageNumber, out bool infoMissing)
    {
        infoMissing = false;
        var order = Unwrap(root);
        var packages = Find(order, "packages", "Packages", "shipments", "Shipments");
        if (packages.ValueKind != JsonValueKind.Array)
        {
            // The order detail has no package list: each line item names its package and whether
            // that package has an invoice, so one package is judged by its own items only.
            var groups = ItemPackageGroups(order);
            if (groups.Count == 0) return InvoiceObservation(order);
            if (!groups.TryGetValue(packageNumber.Trim(), out var items)) return null;
            if (items.Any(item => InvoiceStatusValue(item) is null))
            {
                infoMissing = true;
                return null;
            }
            return UniformItemInvoiceStatus(items) is { } itemStatus
                ? new RemotePackageInvoiceObservation(itemStatus, null, null, null)
                : null;
        }

        foreach (var package in packages.EnumerateArray())
        {
            var remoteNumber = Text(package, "packageNumber", "PackageNumber", "packageId", "PackageId", "id", "Id");
            if (string.Equals(remoteNumber?.Trim(), packageNumber.Trim(), StringComparison.Ordinal))
                return InvoiceObservation(package);
        }
        return null;
    }

    public static IReadOnlyDictionary<string, string> PackageInvoiceStatuses(JsonElement root)
    {
        var order = Unwrap(root);
        var packages = Find(order, "packages", "Packages", "shipments", "Shipments");
        var statuses = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (packages.ValueKind != JsonValueKind.Array)
        {
            foreach (var (packageNumber, items) in ItemPackageGroups(order))
                if (UniformItemInvoiceStatus(items) is { } itemStatus) statuses[packageNumber] = itemStatus;
            return statuses;
        }

        foreach (var package in packages.EnumerateArray())
        {
            var packageNumber = Text(package, "packageNumber", "PackageNumber", "packageId", "PackageId", "id", "Id");
            var invoiceStatus = InvoiceStatusValue(package);
            if (!string.IsNullOrWhiteSpace(packageNumber) && invoiceStatus is not null)
                statuses[packageNumber.Trim()] = invoiceStatus;
        }
        return statuses;
    }

    // Line items grouped by the package number each one carries (Hepsiburada's order detail).
    internal static IReadOnlyDictionary<string, IReadOnlyList<JsonElement>> ItemPackageGroups(JsonElement order)
    {
        var lines = Find(order, "lineItems", "LineItems", "items", "Items", "orderItems", "OrderItems");
        if (lines.ValueKind == JsonValueKind.Object && TryFind(lines, out var nested, "items", "Items", "lineItems", "LineItems")) lines = nested;
        if (lines.ValueKind != JsonValueKind.Array) return new Dictionary<string, IReadOnlyList<JsonElement>>(StringComparer.OrdinalIgnoreCase);
        return lines.EnumerateArray()
            .Select(item => (Package: Text(item, "packageNumber", "PackageNumber")?.Trim(), Item: item))
            .Where(entry => !string.IsNullOrEmpty(entry.Package))
            .GroupBy(entry => entry.Package!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<JsonElement>)group.Select(entry => entry.Item).ToArray(), StringComparer.OrdinalIgnoreCase);
    }

    private static string? UniformItemInvoiceStatus(IEnumerable<JsonElement> items)
    {
        var statuses = items.Select(InvoiceStatusValue).Distinct(StringComparer.Ordinal).ToArray();
        return statuses.Length == 1 ? statuses[0] : null;
    }

    // The one invoice status every element reports. An element without one does not drop out: when some elements say it
    // and others do not, gap is set and the status is unknown (null). Elements that all leave it out say nothing at
    // this level (null, no gap), and the caller looks elsewhere.
    private static string? UniformInvoiceStatus(JsonElement elements, out bool gap)
    {
        gap = false;
        if (elements.ValueKind != JsonValueKind.Array) return null;
        var raw = elements.EnumerateArray().Select(InvoiceStatusValue).ToArray();
        if (raw.Length == 0 || raw.All(status => status is null)) return null;
        if (raw.Any(status => status is null))
        {
            gap = true;
            return null;
        }
        var statuses = raw
            .Select(status => NormalizeInvoiceStatus(status) ?? status!.Trim().ToUpperInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return statuses.Length == 1 ? statuses[0] : null;
    }

    private static string? InvoiceStatusValue(JsonElement source)
    {
        var status = Text(source, "marketplaceInvoiceStatus", "MarketplaceInvoiceStatus", "invoiceStatus", "InvoiceStatus");
        if (!string.IsNullOrWhiteSpace(status)) return NormalizeInvoiceStatus(status);
        var uploaded = Boolean(source, "hasInvoice", "HasInvoice");
        return uploaded is { } hasInvoice ? hasInvoice ? "INVOICED" : "NOT_INVOICED" : null;
    }

    private static string? NormalizeInvoiceStatus(string? status)
    {
        var normalized = status?.Trim().ToUpperInvariant().Replace('-', '_').Replace(' ', '_');
        return normalized switch
        {
            "INVOICED" or "INVOICE" or "COMPLETED" or "UPLOADED" => "INVOICED",
            "NOTINVOICED" or "NOT_INVOICED" or "UNINVOICED" or "WAITING" or "WAITING_FOR_INVOICE"
                or "DELETED" or "REMOVED" or "INVOICE_DELETED" or "INVOICE_REMOVED" or "NO_INVOICE" or "NO_INVOICE_UPLOADED"
                or "NOT_UPLOADED" or "INVOICE_NOT_UPLOADED" => "NOT_INVOICED",
            "RECEIVED" or "PROCESSING" or "PENDING" => "RECEIVED",
            "REJECTED" or "FAILED" => "REJECTED",
            _ => null
        };
    }
    private static RemotePackageInvoiceObservation? InvoiceObservation(JsonElement source)
    {
        var status = Text(source, "invoiceStatus", "InvoiceStatus");
        status = NormalizeInvoiceStatus(status) ?? status;
        var uploaded = Boolean(source, "hasInvoice", "HasInvoice");
        status ??= uploaded is { } hasInvoice ? (hasInvoice ? "INVOICED" : "NOT_INVOICED") : null;
        var invoiceNumber = Text(source, "invoiceNumber", "InvoiceNumber", "invoiceNo", "InvoiceNo");
        var invoiceUrl = Text(source, "invoiceLink", "InvoiceLink", "invoiceUrl", "InvoiceUrl", "invoiceDocumentUrl", "InvoiceDocumentUrl");
        var sourceUpdatedAt = Date(source, "invoiceUpdatedAt", "InvoiceUpdatedAt");
        var referencesAreAuthoritative = Find(source, "invoiceNumber", "InvoiceNumber", "invoiceNo", "InvoiceNo").ValueKind != JsonValueKind.Undefined
            && Find(source, "invoiceLink", "InvoiceLink", "invoiceUrl", "InvoiceUrl", "invoiceDocumentUrl", "InvoiceDocumentUrl").ValueKind != JsonValueKind.Undefined;
        return status is null && invoiceNumber is null && invoiceUrl is null
            ? null
            : new(status, invoiceNumber, invoiceUrl, sourceUpdatedAt, referencesAreAuthoritative);
    }
}
