using System.Text.Json;
using MarketplaceHub.Application;

namespace MarketplaceHub.Infrastructure.Adapters.Hepsiburada;

internal static partial class HepsiburadaJsonMapper
{
    public static (IReadOnlyList<JsonElement> Items, int? TotalCount) PackagePage(JsonElement root)
    {
        var data = Unwrap(root);
        var items = Find(data, "items", "Items", "packages", "Packages", "packageList", "PackageList", "content", "Content", "results", "Results");
        if (items.ValueKind == JsonValueKind.Undefined && data.ValueKind == JsonValueKind.Array) items = data;

        var totalCount = Integer(data, "totalCount", "TotalCount", "totalElements", "TotalElements")
            ?? Integer(root, "totalCount", "TotalCount", "totalElements", "TotalElements");
        if (items.ValueKind != JsonValueKind.Array)
        {
            if (totalCount == 0) return ([], totalCount);
            throw new JsonException("Hepsiburada paket sayfa yanıtında paket listesi yok.");
        }

        return (items.EnumerateArray().ToArray(), totalCount);
    }

    // Lines without an order number are skipped: they cannot name an order to read again.
    public static (IReadOnlyList<RemoteCancelledOrderLine> Items, int? TotalCount) CancelledOrderLines(JsonElement root)
    {
        var (items, totalCount) = PackagePage(root);
        var result = new List<RemoteCancelledOrderLine>(items.Count);
        foreach (var item in items)
        {
            var orderNumber = Text(item, "orderNumber", "OrderNumber", "orderNo");
            if (string.IsNullOrWhiteSpace(orderNumber)) continue;
            result.Add(new(orderNumber.Trim(), Text(item, "lineItemId", "LineItemId", "id"), Date(item, "cancelDate", "CancelDate", "cancelledDate")));
        }
        return (result, totalCount);
    }

    public static (IReadOnlyList<RemoteMissingInvoicePackage> Items, int? TotalCount) MissingInvoicePackages(JsonElement root)
    {
        var (items, totalCount) = PackagePage(root);
        var result = new List<RemoteMissingInvoicePackage>(items.Count);
        foreach (var item in items)
        {
            var orderNumbers = TextValues(item, "orderNumber", "orderNo", "orderId", "orderNumbers");
            var packageNumber = Text(item, "packageNumber", "packageNo", "packageId");
            if (orderNumbers.Count == 0 || string.IsNullOrWhiteSpace(packageNumber))
            {
                var missing = string.Join(" ve ", new[]
                {
                    orderNumbers.Count == 0 ? "sipariş numarası" : null,
                    string.IsNullOrWhiteSpace(packageNumber) ? "paket numarası" : null
                }.Where(value => value is not null));
                var fields = item.ValueKind == JsonValueKind.Object
                    ? string.Join(", ", item.EnumerateObject().Select(property => new string(property.Name.Where(character => char.IsLetterOrDigit(character) || character is '_' or '-').Take(40).ToArray())).Take(30))
                    : item.ValueKind.ToString();
                throw new JsonException($"Hepsiburada eksik fatura kaydında {missing} yok. Yanıt alanları: {fields}.");
            }
            foreach (var orderNumber in orderNumbers)
                result.Add(new(orderNumber, packageNumber.Trim(), Text(item, "status", "orderStatus")));
        }

        return (result, totalCount);
    }

    // "barcode" is the package's cargo barcode ("Oluşturulan paketin kargo barkodudur", hepsiburada-api-son.md:9739),
    // not its tracking number (trackingInfoCode): the two agree for hepsiJET and differ for Aras Kargo.
    public static (string? Status, string? CargoCompany, string? TrackingInfoCode, string? OrderNumber, string? Barcode) PackageTrackingInfo(JsonElement root, string expectedPackageNumber)
    {
        var data = Unwrap(root);
        var items = Find(data, "items", "packages", "content");
        if (items.ValueKind == JsonValueKind.Undefined && data.ValueKind == JsonValueKind.Array) items = data;
        var candidates = items.ValueKind == JsonValueKind.Array ? items.EnumerateArray().ToArray() : [data];
        foreach (var item in candidates)
        {
            var packageNumber = Text(item, "packageNumber", "PackageNumber");
            if (!string.Equals(packageNumber, expectedPackageNumber, StringComparison.Ordinal)) continue;
            return (
                Text(item, "status", "Status"),
                Text(item, "cargoCompany", "CargoCompany", "cargoCompanyName", "CargoCompanyName"),
                Text(item, "trackingInfoCode", "TrackingInfoCode"),
                Text(item, "orderNumber", "OrderNumber", "orderNo", "OrderNo"),
                Text(item, "barcode", "Barcode"));
        }

        throw new JsonException("Hepsiburada kargo yanıtında istenen packageNumber bulunamadı.");
    }

    public static IReadOnlyList<RemoteCargoCompany> ChangeableCargoCompanies(JsonElement root)
    {
        var data = Unwrap(root);
        var items = Find(data, "items", "cargoCompanies", "companies", "content");
        if (items.ValueKind == JsonValueKind.Undefined && data.ValueKind == JsonValueKind.Array) items = data;
        // A package with one possible carrier may come back as a single object instead of a list.
        var single = items.ValueKind == JsonValueKind.Undefined && data.ValueKind == JsonValueKind.Object
            && Text(data, "shortName", "cargoCompanyShortName", "code", "id") is not null;
        if (items.ValueKind != JsonValueKind.Array && !single) throw new JsonException("Hepsiburada değiştirilebilir kargo firması yanıtında liste yok.");
        var companies = new List<RemoteCargoCompany>();
        foreach (var item in single ? [data] : items.EnumerateArray().ToArray())
        {
            var shortName = Text(item, "shortName", "cargoCompanyShortName", "code", "id");
            var name = Text(item, "name", "cargoCompanyName", "displayName") ?? shortName;
            if (string.IsNullOrWhiteSpace(shortName) || string.IsNullOrWhiteSpace(name)) throw new JsonException("Hepsiburada kargo firması kimliği veya adı eksik.");
            companies.Add(new(shortName.Trim(), name.Trim()));
        }
        return companies.DistinctBy(item => item.ShortName, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static IReadOnlyList<RemotePackageableLine> PackageableLineItems(JsonElement root)
    {
        var data = Unwrap(root);
        var items = Find(data, "items", "lineItems", "packageableLineItems", "content");
        if (items.ValueKind == JsonValueKind.Undefined && data.ValueKind == JsonValueKind.Array) items = data;
        if (items.ValueKind != JsonValueKind.Array) throw new JsonException("Hepsiburada paketlenebilir kalem yanıtında liste yok.");
        var lines = new List<RemotePackageableLine>();
        foreach (var item in items.EnumerateArray())
        {
            var id = Text(item, "id", "lineItemId", "orderLineId");
            var quantity = Integer(item, "quantity", "availableQuantity", "packageableQuantity") ?? 1;
            if (string.IsNullOrWhiteSpace(id) || quantity < 1) throw new JsonException("Hepsiburada paketlenebilir kalem kimliği veya miktarı eksik.");
            lines.Add(new(id.Trim(), quantity));
        }
        return lines.GroupBy(line => line.LineItemId, StringComparer.Ordinal).Select(group => new RemotePackageableLine(group.Key, group.Max(line => line.Quantity))).ToArray();
    }

    public static string PackageNumber(JsonElement root)
    {
        var data = Unwrap(root);
        var packageNumber = First(Text(root, "packageNumber", "PackageNumber"), Text(data, "packageNumber", "PackageNumber"));
        return !string.IsNullOrWhiteSpace(packageNumber) ? packageNumber.Trim() : throw new JsonException("Hepsiburada paket oluşturma yanıtında packageNumber yok.");
    }
}
