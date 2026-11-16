using System.Linq.Expressions;

namespace MarketplaceHub.Infrastructure.Persistence;

// SIPARIS-STOK-3 (the code-neutral part, 16 Nov 2026): which order is a micro export is decided by two different rules
// today, and which one holds is the owner's decision. Both are written here side by side, unchanged, and
// OrderMicroExportRulesTests lists the inputs on which they disagree. To make one rule hold everywhere, point the other
// one's caller at it (one line: Label in OrderSnapshotReader.Customer, or Filter in OrderQueryService.MicroExportOrder).
internal static class OrderMicroExportRules
{
    // Rule 1, the order-type label (Customer().IsMicroExport -> MIKRO_IHRACAT on the order list, the order detail and the
    // Returns list): any of the package's micro, microExport or 3pByTrendyol fields true (as text: true, 1, YES, EVET;
    // wherever JsonText finds them), a shipmentPackageType or orderType text holding MICRO or İHRAC, or the old PM3 /
    // Arvato export partner as the customer's name. It does not look at the marketplace.
    public static bool Label(string customerJson, string customerName)
    {
        var microText = OrderSnapshotReader.JsonText(customerJson, "shipmentPackageType", "orderType");
        // Trendyol exports through its 3P partner model explicitly return micro=false. The documented
        // 3pByTrendyol=true signal is still an export order and must be presented as such to operators.
        // Historical Stage snapshots may predate the export flags while retaining Trendyol's
        // documented PM3/Arvato export-partner identity. Keep that narrow legacy signal so
        // existing export orders are not presented as domestic orders.
        var legacyExportPartner = OrderSnapshotReader.IsLegacyTrendyolExportPartner(customerName);
        return OrderSnapshotReader.JsonBool(customerJson, "micro", "microExport", "3pByTrendyol") || microText?.Contains("MICRO", StringComparison.OrdinalIgnoreCase) == true || microText?.Contains("İHRAC", StringComparison.OrdinalIgnoreCase) == true || legacyExportPartner;
    }

    // Rule 2, the "Fatura Bölgesi" filter (MICRO_EXPORT / TR), the "Mikro İhracat" filter and card: an order of a Trendyol
    // connection whose package snapshot holds "micro":true (the row's flag icon, RowMarks, reads the same field). The
    // snapshot part is an expression the database runs; FilterSnapshot compiles the same one.
    public static readonly Expression<Func<string, bool>> FilterSnapshotRule = customerJson =>
        customerJson.Contains("\"micro\":true") || customerJson.Contains("\"micro\": true");

    private static readonly Func<string, bool> FilterSnapshotCompiled = FilterSnapshotRule.Compile();

    public const string FilterPlatformCode = "TRENDYOL";

    // Rule 2 in memory, for one order of a connection of this marketplace.
    public static bool Filter(string? platformCode, string customerJson) =>
        platformCode == FilterPlatformCode && FilterSnapshotCompiled(customerJson);
}
