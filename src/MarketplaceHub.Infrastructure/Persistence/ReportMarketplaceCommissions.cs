using MarketplaceHub.Infrastructure.Jobs.Handlers;

namespace MarketplaceHub.Infrastructure.Persistence;

// The real commission of the Hepsiburada, n11 and Pazarama orders, from each marketplace's own finance records (Trendyol's
// statement has its own path in ReportCosts): what the marketplace charged the order, net of what it gave back (a
// refund, a correction), as a cost to the seller. An order whose records are missing, incomplete or say nothing of the
// commission is not here, and the report keeps its estimate, labelled as one (unknown stays unknown). Nothing here is
// written back or added as a record: the payouts (PayoutsService) read the same rows unchanged.
internal static class ReportMarketplaceCommissions
{
    // Hepsiburada's commission record types (Kayıt Bazlı Muhasebe Servisi, "Kayıt Tipi Bilgileri"): the commission
    // invoice, the commission given back on a return (two kinds) and on an order abroad, and the VAT-difference
    // correction of a commission invoice. Read by the type itself, so a row stored before OverseasCommissionRefund had a
    // category of its own counts too.
    internal static readonly IReadOnlySet<string> HepsiburadaTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Commission", "CommissionRefund", "CommissionInvoiceRefund", "CommissionCorrection", "OverseasCommissionRefund"
    };

    internal const string HepsiburadaCharge = "Commission";

    // Where the real commission of an order comes from, as the order's profit names it.
    public static string SourceLabel(string platformCode) => platformCode switch
    {
        "HEPSIBURADA" => "Hepsiburada muhasebe kayıtlarından (komisyon faturası, iadesi ve düzeltmesi)",
        "N11" => "n11 hakediş kayıtlarından",
        "PAZARAMA" => "Pazarama finans hareketlerinden",
        _ => "Pazaryerinin finans kayıtlarından"
    };

    // ActiveLines: the order's lines with units not cancelled; Packages: the Hepsiburada package numbers of its current
    // packages that are not cancelled (empty for the other marketplaces).
    internal sealed record Order(Guid Id, Guid ConnectionId, string PlatformCode, string OrderNumber, int ActiveLines, IReadOnlyCollection<string> Packages);

    // A finance record as the reports read it. PackageNumber: Hepsiburada's packageNumber (ShipmentPackageId).
    internal sealed record Record(Guid ConnectionId, string Source, string ExternalId, string? TransactionType, string Category, string? OrderNumber, string? PackageNumber,
        decimal Debt, decimal Credit, decimal? CommissionAmount);

    public static Dictionary<Guid, decimal> ByOrder(IReadOnlyList<Order> orders, IReadOnlyList<Record> records)
    {
        var result = new Dictionary<Guid, decimal>();
        var byNumber = records.Where(record => record.OrderNumber is not null)
            .GroupBy(record => (record.ConnectionId, record.OrderNumber!))
            .ToDictionary(group => group.Key, group => group.ToList());
        // A Hepsiburada record that names only its package belongs to the order holding that package.
        var byPackage = records.Where(record => record.Source == HepsiburadaFinanceSyncJobHandler.Source && record.OrderNumber is null && record.PackageNumber is not null)
            .GroupBy(record => (record.ConnectionId, record.PackageNumber!))
            .ToDictionary(group => group.Key, group => group.ToList());
        foreach (var order in orders)
        {
            var own = byNumber.GetValueOrDefault((order.ConnectionId, order.OrderNumber)) ?? [];
            decimal? commission = order.PlatformCode switch
            {
                "PAZARAMA" => Pazarama(own),
                "N11" => N11(own, order.ActiveLines),
                "HEPSIBURADA" => Hepsiburada(own.Concat(order.Packages.SelectMany(package => byPackage.GetValueOrDefault((order.ConnectionId, package)) ?? [])).ToList(), order.Packages),
                _ => null
            };
            if (commission is { } value) result[order.Id] = Math.Round(value, 2, MidpointRounding.AwayFromZero);
        }
        return result;
    }

    // Pazarama: the commission of the order's money movements, stored as the cost to the seller (negative on a sale,
    // given back on a refund; PazaramaFinanceSyncJobHandler.Fill). A movement without its commission leaves the order's
    // commission unknown.
    private static decimal? Pazarama(IReadOnlyList<Record> own)
    {
        var movements = own.Where(record => record.Source == PazaramaFinanceSyncJobHandler.Source).ToList();
        if (movements.Count == 0 || movements.Any(record => record.CommissionAmount is null)) return null;
        return movements.Sum(record => record.CommissionAmount!.Value);
    }

    // n11: each order item's settlement line carries its commission (n11's "commission"; a fee it leaves out is read as
    // 0, N11SettlementMapper). A sale line charges it, a return line (negative settlement) gives it back, whatever sign
    // n11 writes it with. Real only when n11 settled at least as many items as the order has lines (an order whose items
    // settle on different days is not cut short) and when some line names a commission: all 0 is n11 leaving it out.
    private static decimal? N11(IReadOnlyList<Record> own, int activeLines)
    {
        var lines = own.Where(record => record.Source == N11SettlementSyncJobHandler.Source).ToList();
        if (lines.Count == 0 || lines.All(record => (record.CommissionAmount ?? 0m) == 0m)) return null;
        var items = lines.Where(record => record.Category != Domain.MarketplaceFinanceCategories.Return)
            .Select(record => ItemId(record.ExternalId)).Distinct(StringComparer.Ordinal).Count();
        if (items < activeLines) return null;
        return lines.Sum(record => (record.Category == Domain.MarketplaceFinanceCategories.Return ? -1m : 1m) * Math.Abs(record.CommissionAmount ?? 0m));
    }

    // Hepsiburada: the commission records of the order, net (an expense is debt, an income credit). Real only with a
    // commission invoice of its own, and, when the records name their packages, one for every current package of the
    // order: a package billed later would leave the sum short.
    private static decimal? Hepsiburada(IReadOnlyList<Record> own, IReadOnlyCollection<string> packages)
    {
        var commission = own.Where(record => record.Source == HepsiburadaFinanceSyncJobHandler.Source && record.TransactionType is { } type && HepsiburadaTypes.Contains(type))
            .DistinctBy(record => record.ExternalId).ToList();
        var charges = commission.Where(record => string.Equals(record.TransactionType, HepsiburadaCharge, StringComparison.OrdinalIgnoreCase)).ToList();
        if (charges.Count == 0) return null;
        var billed = charges.Where(record => record.PackageNumber is not null).Select(record => record.PackageNumber!).ToHashSet(StringComparer.Ordinal);
        if (billed.Count > 0 && packages.Any(package => !billed.Contains(package))) return null;
        return commission.Sum(record => record.Debt - record.Credit);
    }

    // The order item of an n11 settlement line, stored as "{orderItemId}:{yyyyMMdd}" (N11SettlementSyncJobHandler.ExternalId).
    private static string ItemId(string externalId)
    {
        var at = externalId.LastIndexOf(':');
        return at > 0 ? externalId[..at] : externalId;
    }
}
