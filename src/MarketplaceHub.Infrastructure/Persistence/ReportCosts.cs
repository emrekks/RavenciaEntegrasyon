using System.Globalization;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Adapters.Trendyol;

namespace MarketplaceHub.Infrastructure.Persistence;

// The accounting records the report reads (ReportsService.FinanceAsync): those tied to an order of the period and
// those of the period's dates that belong to no known order, the cargo invoice lines of the same, which cargo invoices
// have lines (their lines count instead of the invoice), each store's recent cargo per billed order and how far each
// store has been read.
internal sealed class ReportFinance(
    IReadOnlyList<ReportFinanceRow> rows,
    IReadOnlyList<ReportCargoItemRow> cargoItems,
    IReadOnlySet<(Guid ConnectionId, string Serial)> itemizedInvoices,
    IReadOnlyDictionary<Guid, MarketplaceFinanceSyncState?> states,
    IReadOnlyDictionary<Guid, ReportCargoSample>? cargoSamples = null)
{
    public IReadOnlyList<ReportFinanceRow> Rows { get; } = rows;
    public IReadOnlyList<ReportCargoItemRow> CargoItems { get; } = cargoItems;
    public IReadOnlySet<(Guid ConnectionId, string Serial)> ItemizedInvoices { get; } = itemizedInvoices;
    // Every Trendyol production store of the report, with its read state (null: never read).
    public IReadOnlyDictionary<Guid, MarketplaceFinanceSyncState?> States { get; } = states;
    // By store: the shipping cargo billed per order over the cargo invoices of the 60 days before the period's end.
    public IReadOnlyDictionary<Guid, ReportCargoSample> CargoSamples { get; } = cargoSamples ?? new Dictionary<Guid, ReportCargoSample>();
}

// Shipping cargo (return cargo apart) billed for Orders distinct orders, Amount in total.
internal sealed record ReportCargoSample(decimal Amount, int Orders)
{
    public decimal? PerOrder => Orders > 0 ? Amount / Orders : null;
}

internal sealed class ReportFinanceRow
{
    public Guid ConnectionId { get; init; }
    public string Source { get; init; } = "";
    public string ExternalId { get; init; } = "";
    public string RequestedType { get; init; } = "";
    public string Category { get; init; } = MarketplaceFinanceCategories.Other;
    public DateTimeOffset TransactionDate { get; init; }
    public string? OrderNumber { get; init; }
    public string? Barcode { get; init; }
    public decimal Debt { get; init; }
    public decimal Credit { get; init; }
    public decimal? CommissionAmount { get; init; }
}

internal sealed class ReportCargoItemRow
{
    public Guid ConnectionId { get; init; }
    public string InvoiceSerialNumber { get; init; } = "";
    public string? OrderNumber { get; init; }
    public string? ShipmentPackageType { get; init; }
    public decimal Amount { get; init; }

    // "İade Kargo Bedeli": the cargo of a return; "Gönderi Kargo Bedeli": of the shipment.
    public bool IsReturn => TrendyolFinanceCategorizer.Fold(ShipmentPackageType).Contains("iade", StringComparison.Ordinal);
}

internal static class ReportCosts
{
    public const string FinanceSource = "trendyol-finance";
    public const string EstimateSource = "estimate";
    private const int ProductCount = 50;

    private static readonly Dictionary<string, string> Labels = new(StringComparer.Ordinal)
    {
        [MarketplaceFinanceCategories.Sale] = "Satış",
        [MarketplaceFinanceCategories.Return] = "İade",
        [MarketplaceFinanceCategories.Discount] = "İndirim",
        [MarketplaceFinanceCategories.Coupon] = "Kupon",
        [MarketplaceFinanceCategories.Commission] = "Komisyon",
        [MarketplaceFinanceCategories.Cargo] = "Kargo",
        [MarketplaceFinanceCategories.ServiceFee] = "Platform hizmet bedeli",
        [MarketplaceFinanceCategories.Stoppage] = "Stopaj",
        [MarketplaceFinanceCategories.Penalty] = "Ceza",
        [MarketplaceFinanceCategories.EarlyPayment] = "Erken ödeme",
        [MarketplaceFinanceCategories.Advertising] = "Reklam",
        [MarketplaceFinanceCategories.OtherDeduction] = "Diğer kesinti",
        [MarketplaceFinanceCategories.Other] = "Diğer"
    };

    public static ReportCostsView Build(ReportInput input, IReadOnlyList<ReportBuilder.OrderFact> orders)
    {
        var finance = input.Finance;
        var financeOrders = orders.Where(order => IsFinanceStore(input, order.Order.ConnectionId) && order.Currency == "TRY").ToList();
        var states = finance?.States ?? new Dictionary<Guid, MarketplaceFinanceSyncState?>();
        var lastSynced = states.Count == 0 || states.Values.Any(state => state?.LastSyncedAt is null) ? null : states.Values.Min(state => state!.LastSyncedAt);
        var coveredFrom = states.Count == 0 || states.Values.Any(state => state?.CoveredFrom is null) ? null : states.Values.Max(state => state!.CoveredFrom);
        var coveredThrough = states.Count == 0 || states.Values.Any(state => state?.CoveredThrough is null) ? null : states.Values.Min(state => state!.CoveredThrough);
        if (finance is null || finance.Rows.Count == 0 && finance.CargoItems.Count == 0)
            return new(EstimateSource, lastSynced, coveredFrom, coveredThrough, financeOrders.Count, 0, null, null, null, null, [], [], []);

        // The period's orders (any status) by store and order number; a record of another period's order is not here.
        var periodOrders = input.Orders.GroupBy(order => (order.ConnectionId, order.OrderNumber)).ToDictionary(group => group.Key, group => group.First().Id);
        Guid? OrderOf(Guid connectionId, string? orderNumber) =>
            orderNumber is not null && periodOrders.TryGetValue((connectionId, orderNumber), out var id) ? id : null;

        var lines = new Dictionary<string, LineTotal>(StringComparer.Ordinal);
        LineTotal Line(string category) => lines.TryGetValue(category, out var total) ? total : lines[category] = new();
        var realCommission = new Dictionary<Guid, decimal>();
        // Commission records tied to no order of the period (a commission invoice, a correction of an unknown order).
        var unlinkedCommission = new Dictionary<Guid, decimal>();
        var settled = new HashSet<Guid>();
        var channelCosts = new Dictionary<(Guid ConnectionId, string Category), decimal>();
        var financeStores = new HashSet<Guid>();
        var products = new Dictionary<string, ProductTotal>(StringComparer.Ordinal);
        var linesByOrder = orders.ToDictionary(order => order.Order.Id, order => order.Lines);

        foreach (var row in finance.Rows)
        {
            // A cargo invoice whose lines were read counts through its lines (below), not as a whole.
            if (TrendyolFinanceCategorizer.IsCargoInvoice(row.Source, row.RequestedType, row.Category) && finance.ItemizedInvoices.Contains((row.ConnectionId, row.ExternalId)))
                continue;
            financeStores.Add(row.ConnectionId);
            // Trendyol's commission invoice bills the commission its settlement lines already carry (over 90 days of one
            // store: 258,470 TL of invoices against 251,482 TL of settled commission); counting it would charge it twice.
            if (IsCommissionInvoice(row)) continue;
            var orderId = OrderOf(row.ConnectionId, row.OrderNumber);
            if (orderId is { } settledOrder && row.Source == TrendyolFinanceRequestContract.Settlements) settled.Add(settledOrder);
            // A row stored as OTHER that is a charge (an unrecognised deduction invoice) counts as one.
            var category = TrendyolFinanceCategorizer.Charged(row.Source, row.RequestedType, row.Category, row.Debt, row.Credit);

            // Settlement lines carry their commission; a commission invoice of the other financials is charged as a whole.
            var commission = row.Source == TrendyolFinanceRequestContract.Settlements
                ? TrendyolFinanceCategorizer.CommissionCost(row.RequestedType, row.CommissionAmount, row.Debt, row.Credit)
                : category == MarketplaceFinanceCategories.Commission ? TrendyolFinanceCategorizer.NetCost(row.Debt, row.Credit) : 0m;
            if (category != MarketplaceFinanceCategories.Commission)
                Line(category).Add(TrendyolFinanceCategorizer.NetCost(row.Debt, row.Credit), orderId is not null);
            if (commission != 0m || category == MarketplaceFinanceCategories.Commission)
            {
                Line(MarketplaceFinanceCategories.Commission).Add(commission, orderId is not null);
                if (orderId is { } commissionOrder) realCommission[commissionOrder] = realCommission.GetValueOrDefault(commissionOrder) + commission;
                else unlinkedCommission[row.ConnectionId] = unlinkedCommission.GetValueOrDefault(row.ConnectionId) + commission;
            }
            else if (TrendyolFinanceCategorizer.IsCost(category))
            {
                var key = (row.ConnectionId, category);
                channelCosts[key] = channelCosts.GetValueOrDefault(key) + TrendyolFinanceCategorizer.NetCost(row.Debt, row.Credit);
            }

            // By product: a record whose barcode is on a line of its order.
            if (orderId is { } productOrder && !string.IsNullOrWhiteSpace(row.Barcode) && linesByOrder.TryGetValue(productOrder, out var orderLines)
                && orderLines.FirstOrDefault(line => string.Equals(line.Row.Barcode?.Trim(), row.Barcode.Trim(), StringComparison.Ordinal)) is { } matched)
            {
                var productKey = matched.Row.VariantId?.ToString("D") ?? $"barcode:{row.Barcode.Trim()}";
                if (!products.TryGetValue(productKey, out var product)) products[productKey] = product = new(matched.Row, row.Barcode.Trim());
                product.Records++;
                product.Commission += commission;
                if (category == MarketplaceFinanceCategories.Discount) product.Discount += TrendyolFinanceCategorizer.NetCost(row.Debt, row.Credit);
                if (category == MarketplaceFinanceCategories.Coupon) product.Coupon += TrendyolFinanceCategorizer.NetCost(row.Debt, row.Credit);
            }
        }
        var billed = new HashSet<Guid>();
        foreach (var item in finance.CargoItems)
        {
            financeStores.Add(item.ConnectionId);
            var cargoOrder = OrderOf(item.ConnectionId, item.OrderNumber);
            if (cargoOrder is { } billedOrder && !item.IsReturn) billed.Add(billedOrder);
            Line(MarketplaceFinanceCategories.Cargo).Add(item.Amount, cargoOrder is not null);
            var key = (item.ConnectionId, MarketplaceFinanceCategories.Cargo);
            channelCosts[key] = channelCosts.GetValueOrDefault(key) + item.Amount;
        }

        // The period's Trendyol orders without a shipping cargo line yet, at their store's recent cargo per order.
        var unbilledByStore = financeOrders.Where(order => !billed.Contains(order.Order.Id)).GroupBy(order => order.Order.ConnectionId).ToDictionary(group => group.Key, group => group.Count());
        var estimatedCargo = new Dictionary<Guid, decimal>();
        foreach (var (storeId, count) in unbilledByStore)
            if (finance.CargoSamples.GetValueOrDefault(storeId)?.PerOrder is { } perOrder)
                estimatedCargo[storeId] = Round(count * perOrder);
        var samples = finance.CargoSamples.Where(sample => financeOrders.Any(order => order.Order.ConnectionId == sample.Key) && sample.Value.Orders > 0).Select(sample => sample.Value).ToList();
        var sampleOrders = samples.Sum(sample => sample.Orders);
        var realCargo = Round(lines.TryGetValue(MarketplaceFinanceCategories.Cargo, out var cargoLine) ? cargoLine.Amount : 0m);
        var unbilledCount = unbilledByStore.Values.Sum();
        decimal? cargoEstimate = unbilledCount == 0 ? 0m : estimatedCargo.Count > 0 ? estimatedCargo.Values.Sum() : null;
        var cargoView = new ReportCargoCostView(realCargo, financeOrders.Count(order => billed.Contains(order.Order.Id)), unbilledCount,
            sampleOrders > 0 ? Round(samples.Sum(sample => sample.Amount) / sampleOrders) : null, sampleOrders, cargoEstimate, realCargo + (cargoEstimate ?? 0m));

        var settledFacts = orders.Where(order => settled.Contains(order.Order.Id)).ToList();
        var real = Round(settledFacts.Sum(order => realCommission.GetValueOrDefault(order.Order.Id)));
        var estimatedSettled = Round(settledFacts.Sum(Estimated));
        var estimatedOther = Round(orders.Where(order => !settled.Contains(order.Order.Id)).Sum(Estimated));
        var commissionView = new ReportRealCommissionView(real, settledFacts.Count, estimatedSettled, estimatedOther, real + estimatedOther);
        var settledFinanceOrders = financeOrders.Count(order => settled.Contains(order.Order.Id));

        var lineViews = MarketplaceFinanceCategories.All.Where(lines.ContainsKey)
            .Select(category => new ReportCostLineView(category, Labels[category], TrendyolFinanceCategorizer.IsCost(category), Round(lines[category].Amount), lines[category].Records,
                Round(lines[category].Linked), Round(lines[category].Unlinked)))
            .ToList();
        var channels = input.Channels.Values
            .Where(channel => orders.Any(order => order.Order.ConnectionId == channel.Id) || financeStores.Contains(channel.Id))
            .Select(channel =>
            {
                var own = orders.Where(order => order.Order.ConnectionId == channel.Id).ToList();
                var channelReal = Round(own.Where(order => settled.Contains(order.Order.Id)).Sum(order => realCommission.GetValueOrDefault(order.Order.Id))
                    + unlinkedCommission.GetValueOrDefault(channel.Id));
                var channelEstimate = Round(own.Where(order => !settled.Contains(order.Order.Id)).Sum(Estimated));
                var amounts = MarketplaceFinanceCategories.Costs.Where(category => category != MarketplaceFinanceCategories.Commission && channelCosts.ContainsKey((channel.Id, category)))
                    .Select(category => new ReportCostAmountView(category, Round(channelCosts[(channel.Id, category)])))
                    .ToList();
                var channelCargo = estimatedCargo.GetValueOrDefault(channel.Id);
                return new ReportCostChannelView(channel.Id, channel.PlatformCode, channel.DisplayName, financeStores.Contains(channel.Id) ? FinanceSource : EstimateSource,
                    channelReal, channelEstimate, amounts, channelCargo, channelReal + channelEstimate + amounts.Sum(amount => amount.Amount) + channelCargo);
            })
            .OrderByDescending(channel => channel.Total).ThenBy(channel => channel.DisplayName, StringComparer.Ordinal)
            .ToList();
        var productViews = products.Values
            .OrderByDescending(product => product.Commission).ThenBy(product => product.Line.Title, StringComparer.Ordinal)
            .Take(ProductCount)
            .Select(product => new ReportCostProductView(product.Line.VariantId, product.Line.Title, product.Line.Sku, product.Barcode, Round(product.Commission),
                Round(product.Discount), Round(product.Coupon), product.Records))
            .ToList();
        var total = Round(lineViews.Where(line => line.IsCost).Sum(line => line.Amount));
        return new(FinanceSource, lastSynced, coveredFrom, coveredThrough, financeOrders.Count, settledFinanceOrders,
            financeOrders.Count > 0 ? Math.Round((decimal)settledFinanceOrders / financeOrders.Count, 4) : null,
            total, commissionView, cargoView, lineViews, channels, productViews);
    }

    private static bool IsCommissionInvoice(ReportFinanceRow row) =>
        row.Source == TrendyolFinanceRequestContract.OtherFinancials
        && string.Equals(row.RequestedType, TrendyolFinanceCategorizer.DeductionInvoices, StringComparison.OrdinalIgnoreCase)
        && row.Category == MarketplaceFinanceCategories.Commission;

    // The notes the costs add to the report's DataNotes.
    public static IEnumerable<string> Notes(ReportCostsView costs, ReportInput input)
    {
        var tr = CultureInfo.GetCultureInfo("tr-TR");
        string Money(decimal value) => $"{value.ToString("N2", tr)} ₺";
        string Time(DateTimeOffset value) => TimeZoneInfo.ConvertTime(value, input.Timezone).ToString("d MMMM yyyy HH:mm", tr);
        string Date(DateTimeOffset value) => TimeZoneInfo.ConvertTime(value, input.Timezone).ToString("d MMMM yyyy", tr);
        var stores = input.Finance?.States.Count ?? 0;
        if (costs.Source != FinanceSource)
        {
            yield return stores == 0
                ? "Gerçek masraf verisi yok: seçili mağazalarda canlı Trendyol mağazası yok; komisyon ve masraflar tahminidir."
                : input.Finance!.States.Values.All(state => state?.CoveredThrough is null)
                    ? "Gerçek masraf verisi yok: Trendyol finans kayıtları henüz okunmadı; komisyon ve masraflar tahminidir."
                    : "Trendyol finans kayıtlarında bu döneme ait kayıt yok; komisyon ve masraflar tahminidir.";
            yield break;
        }
        yield return $"Gerçek masraflar Trendyol cari hesap ekstresinden okunur{(costs.LastSyncedAt is { } synced ? $" (son okuma {Time(synced)})" : " (okuma sürüyor)")}: siparişe bağlı kalemler siparişin dönemine, siparişe bağlı olmayanlar (hizmet bedeli, stopaj, ceza) işlem tarihine göre yazılır.";
        if (costs.Coverage is { } coverage)
            yield return $"Trendyol siparişlerinin {costs.SettledOrders.ToString("N0", tr)} / {costs.FinanceOrders.ToString("N0", tr)} kadarında (%{coverage * 100:N1}) hesap kesimi var; Trendyol kaydı teslimattan sonra yazar, kalan siparişlerin komisyonu tahminidir.";
        if (costs.Commission is { RealOrders: > 0 } commission)
            yield return $"Komisyon, hesap kesimi olan {commission.RealOrders.ToString("N0", tr)} siparişte tahmin yerine gerçek tutarla alındı: gerçek {Money(commission.Real)}, aynı siparişlerin tahmini {Money(commission.EstimatedForSettled)}. Komisyon tahmini alanları tahmin olarak kalır.";
        if (costs.CoveredFrom is { } from && costs.CoveredThrough is { } through
            && (input.Period.Start < from || input.Period.End > through))
            yield return $"Trendyol finans kayıtları {Date(from)} – {Date(through)} aralığında okundu; dönemin bu aralık dışındaki kısmının masrafları eksik olabilir.";
        if (costs.Lines.FirstOrDefault(line => line.Category == MarketplaceFinanceCategories.Other) is { Records: > 0 } other)
            yield return $"Sınıflandırılamayan {other.Records.ToString("N0", tr)} finans kaydı ({Money(other.Amount)}) \"Diğer\" altında gösterilir ve toplam masrafa eklenmez.";
        var rejected = input.Finance!.States.Values.Where(state => !string.IsNullOrWhiteSpace(state?.RejectedTypes)).SelectMany(state => state!.RejectedTypes!.Split(',')).Distinct(StringComparer.Ordinal).ToList();
        if (rejected.Count > 0)
            yield return $"Trendyol şu kayıt türlerini vermedi, bu türler masraflarda yok: {string.Join(", ", rejected)}.";
        yield return "Trendyol'un komisyon faturaları, hesap kesimindeki komisyonun faturasıdır; komisyona ikinci kez eklenmez.";
        if (costs.Cargo is { } cargo)
        {
            var billed = $"Kargo, Trendyol kargo faturalarının paket satırlarından okunur: {cargo.BilledOrders.ToString("N0", tr)} siparişin kargo faturası geldi ({Money(cargo.Real)}).";
            yield return cargo.UnbilledOrders == 0
                ? billed
                : cargo.Estimated is { } estimated && cargo.AveragePerOrder is { } average
                    ? $"{billed} Trendyol kargoyu gönderiden haftalar sonra faturalar; faturası henüz gelmeyen {cargo.UnbilledOrders.ToString("N0", tr)} siparişin kargosu son 60 günün sipariş başına ortalamasıyla ({Money(average)}, {cargo.SampleOrders.ToString("N0", tr)} sipariş) tahmin edildi: {Money(estimated)}."
                    : $"{billed} Faturası henüz gelmeyen {cargo.UnbilledOrders.ToString("N0", tr)} siparişin kargosu tahmin edilemedi (son 60 günde faturalanmış kargo yok); kargo eksiktir.";
        }
        yield return "Kargo ve hizmet bedelleri ürünlere paylaştırılmaz.";
    }

    private static bool IsFinanceStore(ReportInput input, Guid connectionId) =>
        input.Channels.TryGetValue(connectionId, out var channel) && channel.PlatformCode == "TRENDYOL" && !channel.IsTest;

    private static decimal Estimated(ReportBuilder.OrderFact order) => order.Lines.Where(line => line.Commission is not null).Sum(line => line.Commission!.Value);

    private static decimal Round(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    private sealed class LineTotal
    {
        public decimal Amount;
        public decimal Linked;
        public decimal Unlinked;
        public int Records;

        public void Add(decimal amount, bool linked)
        {
            Amount += amount;
            if (linked) Linked += amount; else Unlinked += amount;
            Records++;
        }
    }

    private sealed class ProductTotal(ReportLineRow line, string barcode)
    {
        public ReportLineRow Line { get; } = line;
        public string Barcode { get; } = barcode;
        public decimal Commission;
        public decimal Discount;
        public decimal Coupon;
        public int Records;
    }
}
