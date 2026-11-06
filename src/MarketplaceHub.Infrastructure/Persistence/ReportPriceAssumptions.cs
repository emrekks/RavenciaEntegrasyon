using MarketplaceHub.Application;
using MarketplaceHub.Infrastructure.Adapters.Trendyol;

namespace MarketplaceHub.Infrastructure.Persistence;

// The product editor's Fiyat Hesapla hints (owner, 6 Nov 2026: "hangi platformda kaç aldığı çıksın giyim için"): what
// each store took of the owner's clothing orders of the last Days days, costed order by order as the reports cost them
// (ReportOrderProfit): the commission the marketplace charged (its own record) or the one estimated from the order's
// rate, the store's cargo tariff and its service fee. Nothing is invented: a store without enough clothing orders has
// no rate and a cargo the data does not give stays unknown.
internal static class ReportPriceAssumptions
{
    public const int Days = 60;
    // Fewer orders than this say too little of a store's rate to show one.
    public const int MinimumOrders = 5;
    private const string Clothing = "giyim";

    // A marketplace category path under clothing: one of its segments names "Giyim" (Trendyol's "Giyim / Üst Giyim / Bluz",
    // n11's "Kadın Giyim & Aksesuar / Bluz", Pazarama's "Moda / Kadın Giyim / Kadın Bluz"); "Aksesuar / Çanta" is not.
    public static bool IsClothingPath(string? path) =>
        !string.IsNullOrWhiteSpace(path) && TrendyolFinanceCategorizer.Fold(path).Contains(Clothing, StringComparison.Ordinal);

    // The panel categories that are clothing: those one of whose marketplace mappings sits under a clothing path. The
    // panel's own tree is flat (Bluz, Tunik, Omuz Çantası…), so the marketplaces' trees tell clothing apart. Null when
    // no category is mapped to a known path: clothing cannot be told apart, and every sale counts.
    public static IReadOnlySet<Guid>? ClothingCategories(IEnumerable<(Guid CategoryId, string? Path)> mappedPaths)
    {
        var known = mappedPaths.Where(item => !string.IsNullOrWhiteSpace(item.Path)).ToList();
        if (known.Count == 0) return null;
        return known.Where(item => IsClothingPath(item.Path)).Select(item => item.CategoryId).ToHashSet();
    }

    // Hepsiburada's service charges in its accounting records (live, Sep-Nov 2026): PaymentServiceCostReflection on every
    // payment (~0.86 % of it) and its refund, and ProcessingFeeExpense (13.19 TL) on a few orders only. The reports cost
    // a 13.19 TL fee on every order (ReportPlatformCosts); the hint shows what the records charged.
    internal static readonly IReadOnlySet<string> HepsiburadaServiceFeeTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "ProcessingFeeExpense", "ProcessingFeeExpenseRefund", "PaymentServiceCostReflection", "PaymentServiceCostReflectionRefund"
    };

    // A marketplace record of a service charge on an order, as a cost to the seller (debt − credit).
    internal sealed record FeeRecord(Guid ConnectionId, string OrderNumber, decimal Amount);

    public static PriceAssumptionsView Build(ReportInput input, IReadOnlyList<ReportBuilder.OrderFact> orders, IReadOnlySet<Guid>? clothing,
        IReadOnlyList<FeeRecord>? feeRecords = null)
    {
        var fees = (feeRecords ?? []).GroupBy(record => (record.ConnectionId, record.OrderNumber)).ToDictionary(group => group.Key, group => group.Sum(record => record.Amount));
        // An order counts when every line it still holds is of a clothing category; a line of no category, or of an
        // unmapped one, leaves the order out (unknown is not clothing).
        bool IsClothing(ReportBuilder.OrderFact order) =>
            clothing is null || order.Lines.Where(line => line.Active > 0).All(line => line.Row.CategoryId is { } category && clothing.Contains(category));
        var picked = orders.Where(IsClothing).ToList();
        // Every order is costed, as the reports cost the period (a line without a commission takes its store's rate over
        // all the period's lines), and the clothing ones are read from it.
        var profits = ReportOrderProfit.Build(input, orders).ToDictionary(view => view.OrderId);
        var carriers = input.Packages.Where(package => !string.IsNullOrWhiteSpace(package.CargoProvider))
            .GroupBy(package => package.OrderId).ToDictionary(group => group.Key, group => group.First().CargoProvider);
        var desiInput = input.Desi ?? ReportDesiInput.None;

        var stores = new List<PriceAssumptionStoreView>();
        foreach (var channel in input.Channels.Values.Where(channel => !channel.IsTest && Order(channel.PlatformCode) < int.MaxValue)
                     .OrderBy(channel => Order(channel.PlatformCode)).ThenBy(channel => channel.DisplayName, StringComparer.Ordinal))
        {
            var own = picked.Where(order => order.Order.ConnectionId == channel.Id && order.Currency == "TRY").ToList();
            // The orders with a kept sale and a known commission: the commission's share of what the customer kept paying.
            var sample = own.Select(order => profits.GetValueOrDefault(order.Order.Id)).OfType<ReportOrderProfitView>()
                .Where(view => view.Amount > 0 && view.Costs.FirstOrDefault(cost => cost.Key == "commission")?.Amount is not null)
                .ToList();
            var sales = sample.Sum(view => view.Amount);
            var commission = sample.Sum(view => view.Costs.First(cost => cost.Key == "commission").Amount!.Value);
            var takesCommission = !ReportCosts.TakesNoCommission(channel.PlatformCode);
            decimal? rate = !takesCommission ? 0m
                : sample.Count >= MinimumOrders && sales > 0 ? Math.Round(commission / sales * 100m, 2, MidpointRounding.AwayFromZero) : null;
            // Hepsiburada's rate is without VAT; it charges 20 % VAT on top (ReportBuckets.EstimatedCommission).
            decimal? beforeVat = channel.PlatformCode == "HEPSIBURADA" && rate is { } charged
                ? Math.Round(charged / ReportBuckets.HepsiburadaCommissionVat, 2, MidpointRounding.AwayFromZero) : null;
            var real = sample.Count(view => !view.Costs.First(cost => cost.Key == "commission").Estimated);

            var (cargo, cargoSource) = channel.PlatformCode switch
            {
                // Trendyol's current tariff from its cargo invoices (ReportCargoSample.Tariff: 98.34 TL in Oct 2026).
                "TRENDYOL" => (input.Finance?.CargoSamples.GetValueOrDefault(channel.Id)?.PerOrder is { } perOrder ? Round(perOrder) : (decimal?)null, "invoice"),
                "SHOPIFY" => (ReportPlatformCosts.ShopifyCargo, "assumption"),
                _ => (Mode(own.Where(order => order.Amount > 0).Select(order => Round(OneWayCargo(order, carriers, desiInput)))), "tariff")
            };

            var fee = ReportPlatformCosts.ServiceFee(channel.PlatformCode);
            var trendyol = channel.PlatformCode == "TRENDYOL";
            var shopify = channel.PlatformCode == "SHOPIFY";
            var (serviceFee, serviceFeeRate, feeSource, feeOrders) = (trendyol ? ReportCosts.PlatformServiceFeePerOrder : Round(fee.PerOrder),
                Math.Round(fee.OfSale * 100m, 2, MidpointRounding.AwayFromZero), "tariff", 0);
            // Hepsiburada: the mean of what its records charged the clothing orders it has billed (a commission record of
            // their own), once there are enough of them.
            if (channel.PlatformCode == "HEPSIBURADA")
            {
                var billed = sample.Where(view => !view.Costs.First(cost => cost.Key == "commission").Estimated).ToList();
                if (billed.Count >= MinimumOrders)
                    (serviceFee, serviceFeeRate, feeSource, feeOrders) = (Round(billed.Sum(view => fees.GetValueOrDefault((channel.Id, view.OrderNumber))) / billed.Count), 0m, "records", billed.Count);
            }
            stores.Add(new PriceAssumptionStoreView(channel.Id, channel.PlatformCode, channel.DisplayName, sample.Count, Round(sales), rate, beforeVat, takesCommission, real,
                cargo, cargo is null ? null : cargoSource, serviceFee, serviceFeeRate, feeSource, feeOrders,
                trendyol ? ReportCosts.AzerbaijanServiceFeePerOrder : null, trendyol ? ReportCosts.InternationalServiceFeeRate * 100m : null,
                shopify ? (ReportPlatformCosts.IyzicoRate + ReportPlatformCosts.ShopifyFeeRate) * 100m : null, shopify ? ReportPlatformCosts.IyzicoFixed : null));
        }
        return new PriceAssumptionsView(input.Period.From, input.Period.To, input.Period.Days, clothing is not null, MinimumOrders, stores);
    }

    // One parcel of the order out (no return), as the reports cost it from the platform's tariff (ReportOrderProfit).
    private static decimal OneWayCargo(ReportBuilder.OrderFact order, IReadOnlyDictionary<Guid, string?> carriers, ReportDesiInput desiInput)
    {
        var (desi, _) = ReportParcelDesi.ForOrder(
            desiInput.DeclaredByOrder.TryGetValue(order.Order.Id, out var declared) ? declared : null,
            order.Lines.Select(line => (line.Row.Desi, line.Row.ProductId, line.Row.CategoryId, line.Active)), desiInput.Learned);
        return ReportPlatformCosts.For(order.PlatformCode, order.Amount, 0m, false, desi, carriers.GetValueOrDefault(order.Order.Id)).Cargo;
    }

    // The amount most orders were costed at (ties to the dearer), as Trendyol's tariff is read; null without orders.
    internal static decimal? Mode(IEnumerable<decimal> amounts)
    {
        var list = amounts.ToList();
        return list.Count == 0 ? null : list.GroupBy(amount => amount).OrderByDescending(group => group.Count()).ThenByDescending(group => group.Key).First().Key;
    }

    // The marketplaces the owner sells on, in the order the hint lists them.
    private static int Order(string platformCode) => platformCode switch
    {
        "TRENDYOL" => 0,
        "HEPSIBURADA" => 1,
        "N11" => 2,
        "PAZARAMA" => 3,
        "PTTAVM" => 4,
        "SHOPIFY" => 5,
        _ => int.MaxValue
    };

    private static decimal Round(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);
}
