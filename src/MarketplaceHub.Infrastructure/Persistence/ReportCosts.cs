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

    // An order not billed yet is costed at the store's current cargo tariff: the amount most of its recent shipping
    // lines were billed at (98.34 TL in Oct 2026), not their mean, which the odd heavier parcel and an old tariff
    // still inside the window pull away from what Trendyol bills a usual parcel now. Ties go to the dearer amount.
    public static ReportCargoSample? Tariff(IReadOnlyCollection<(decimal Amount, string? OrderNumber)> shipping)
    {
        if (shipping.Count == 0) return null;
        var tariff = shipping.GroupBy(line => line.Amount).OrderByDescending(group => group.Count()).ThenByDescending(group => group.Key).First().Key;
        var orders = shipping.Select(line => line.OrderNumber).Distinct(StringComparer.Ordinal).Count();
        return new(tariff * orders, orders);
    }
}

internal sealed class ReportFinanceRow
{
    public Guid ConnectionId { get; init; }
    public string Source { get; init; } = "";
    public string ExternalId { get; init; } = "";
    public string RequestedType { get; init; } = "";
    // Trendyol's name of the record ("Platform Hizmet Bedeli", "Uluslararası Hizmet Bedeli"…).
    public string? TransactionType { get; init; }
    public string Category { get; init; } = MarketplaceFinanceCategories.Other;
    public DateTimeOffset TransactionDate { get; init; }
    public string? OrderNumber { get; init; }
    public string? Barcode { get; init; }
    public decimal Debt { get; init; }
    public decimal Credit { get; init; }
    public decimal? CommissionAmount { get; init; }
    // The payout the record was paid in or taken from (Trendyol's paymentOrderId).
    public string? PaymentOrderId { get; init; }
    // A share of a per-sale fee invoice split over the orders of its payout (ReportFeeAllocation): the invoice's id.
    public string? AllocatedFrom { get; init; }
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
    // Trendyol charges these per order but writes them to the statement only when it settles the order, so for an order
    // without a settlement record they are estimated: the platform service fee (10.99 TL + 20 % VAT), the international
    // service fee of a micro-export order (6 % of its sale; 24 Oct 2026: 34.22 TL on a 570.34 TL Greek order) and the
    // e-commerce withholding tax (Law 7524: 1 % of the VAT-free domestic sale).
    internal const decimal PlatformServiceFeePerOrder = 13.19m;
    internal const decimal InternationalServiceFeeRate = 0.06m;
    internal const decimal StoppageRate = 0.01m;
    // The VAT inside Trendyol's invoices (commission, cargo, service fee, advertising).
    internal const decimal ExpenseVatRate = 20m;

    private static readonly Dictionary<string, string> Labels = new(StringComparer.Ordinal)
    {
        [MarketplaceFinanceCategories.Sale] = "Satış",
        [MarketplaceFinanceCategories.Return] = "İade",
        [MarketplaceFinanceCategories.Discount] = "İndirim",
        [MarketplaceFinanceCategories.Coupon] = "Kupon",
        [MarketplaceFinanceCategories.Commission] = "Komisyon",
        [MarketplaceFinanceCategories.Cargo] = "Kargo",
        [MarketplaceFinanceCategories.ServiceFee] = "Platform hizmet bedeli",
        [MarketplaceFinanceCategories.PaymentFee] = "Ödeme komisyonu",
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
        // A store whose statement has been read is costed even before Trendyol wrote a record of the period (yesterday's
        // orders): everything is then estimated from its own history.
        // An order without an amount (its sale is not counted either) is not costed: its cargo would weigh on no sale.
        var platformOrders = orders.Where(order => order.Currency == "TRY" && order.Amount > 0 && ReportPlatformCosts.Covers(order.PlatformCode)).ToList();
        var read = finance is not null && (finance.Rows.Count > 0 || finance.CargoItems.Count > 0 || finance.States.Values.Any(state => state?.CoveredThrough is not null));
        if (!read && platformOrders.Count == 0)
            return new(EstimateSource, lastSynced, coveredFrom, coveredThrough, financeOrders.Count, 0, null, null, null, null, [], [], [], Unlinked: []);
        finance ??= new ReportFinance([], [], new HashSet<(Guid, string)>(), new Dictionary<Guid, MarketplaceFinanceSyncState?>());
        // A Trendyol store whose statement is not read is not costed from it (its fees stay unknown).
        if (!read) financeOrders = [];

        // The period's orders (any status) by store and order number; a record of another period's order is not here.
        var periodOrders = input.Orders.GroupBy(order => (order.ConnectionId, order.OrderNumber)).ToDictionary(group => group.Key, group => group.First().Id);
        Guid? OrderOf(Guid connectionId, string? orderNumber) =>
            orderNumber is not null && periodOrders.TryGetValue((connectionId, orderNumber), out var id) ? id : null;

        // The costs of the period's orders: their own records and, where there is none, the estimates.
        var lines = new Dictionary<string, LineTotal>(StringComparer.Ordinal);
        LineTotal Line(string category) => lines.TryGetValue(category, out var total) ? total : lines[category] = new();
        // The records of the period's dates tied to no order of the period (advertising, a penalty, a record of an order
        // never imported, a service fee or withholding tax invoice whose payout's orders are not read yet or that names
        // no payout): kept apart from the period's orders' costs (5 Nov 2026 they turned the day's +4,922 TL order profit
        // into a 633 TL loss); the panel takes their total off the net profit in a line of its own after the product cost
        // (owner, 5 Nov 2026: every expense is off Net Kâr), and never off a single order's profit. The service fee and withholding tax invoices of a read payout are split over its orders
        // (ReportFeeAllocation) and count with those orders, in their period.
        var unlinked = new Dictionary<string, LineTotal>(StringComparer.Ordinal);
        LineTotal Unlinked(string category) => unlinked.TryGetValue(category, out var total) ? total : unlinked[category] = new();
        var realCommission = new Dictionary<Guid, decimal>();
        var settled = new HashSet<Guid>();
        // The period's orders with a service fee or withholding tax record of their own (a share of their payout's
        // invoice, ReportFeeAllocation), whose fee is then not estimated, and the VAT-bearing service fee of those records.
        var linkedFees = new HashSet<(Guid Order, string Category)>();
        var linkedDomesticFees = 0m;
        var channelCosts = new Dictionary<(Guid ConnectionId, string Category), decimal>();
        var financeStores = new HashSet<Guid>();
        var products = new Dictionary<string, ProductTotal>(StringComparer.Ordinal);
        var linesByOrder = orders.ToDictionary(order => order.Order.Id, order => order.Lines);
        // A Trendyol line without a commission (none on the line) is estimated at its store's rate over the
        // period's lines that have one, so the commission is not left short.
        var commissionRates = orders.Where(order => IsFinanceStore(input, order.Order.ConnectionId))
            .SelectMany(order => order.Lines.Where(line => line.Commission is not null && line.KeptAmount > 0).Select(line => (order.Order.ConnectionId, line)))
            .GroupBy(item => item.ConnectionId)
            .ToDictionary(group => group.Key, group => group.Sum(item => item.line.Commission!.Value) / group.Sum(item => item.line.KeptAmount));
        decimal Estimated(ReportBuilder.OrderFact order) => order.Lines.Sum(line => line.Commission
            ?? (commissionRates.TryGetValue(order.Order.ConnectionId, out var rate) ? line.KeptAmount * rate : 0m));

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
            // A row stored as OTHER that is a charge (an unrecognised deduction invoice) counts as one.
            var category = TrendyolFinanceCategorizer.Charged(row.Source, row.RequestedType, row.Category, row.Debt, row.Credit);
            var amount = TrendyolFinanceCategorizer.NetCost(row.Debt, row.Credit);

            // Settlement lines carry their commission; a commission invoice of the other financials is charged as a whole.
            var commission = row.Source == TrendyolFinanceRequestContract.Settlements
                ? TrendyolFinanceCategorizer.CommissionCost(row.RequestedType, row.CommissionAmount, row.Debt, row.Credit)
                : category == MarketplaceFinanceCategories.Commission ? amount : 0m;
            var charged = commission != 0m || category == MarketplaceFinanceCategories.Commission;
            if (orderId is not { } order)
            {
                if (category != MarketplaceFinanceCategories.Commission) Unlinked(category).Add(amount);
                if (charged) Unlinked(MarketplaceFinanceCategories.Commission).Add(commission);
                continue;
            }

            if (row.Source == TrendyolFinanceRequestContract.Settlements) settled.Add(order);
            if (category != MarketplaceFinanceCategories.Commission) Line(category).Add(amount);
            if (charged)
            {
                Line(MarketplaceFinanceCategories.Commission).Add(commission);
                realCommission[order] = realCommission.GetValueOrDefault(order) + commission;
            }
            else if (TrendyolFinanceCategorizer.IsCost(category))
            {
                var key = (row.ConnectionId, category);
                channelCosts[key] = channelCosts.GetValueOrDefault(key) + amount;
                if (category is MarketplaceFinanceCategories.ServiceFee or MarketplaceFinanceCategories.Stoppage) linkedFees.Add((order, category));
            }
            if (row.Category == MarketplaceFinanceCategories.ServiceFee && !IsInternationalFee(row)) linkedDomesticFees += amount;

            // By product: a record whose barcode is on a line of its order.
            if (!string.IsNullOrWhiteSpace(row.Barcode) && linesByOrder.TryGetValue(order, out var orderLines)
                && orderLines.FirstOrDefault(line => string.Equals(line.Row.Barcode?.Trim(), row.Barcode.Trim(), StringComparison.Ordinal)) is { } matched)
            {
                var productKey = matched.Row.VariantId?.ToString("D") ?? $"barcode:{row.Barcode.Trim()}";
                if (!products.TryGetValue(productKey, out var product)) products[productKey] = product = new(matched.Row, row.Barcode.Trim());
                product.Records++;
                product.Commission += commission;
                if (category == MarketplaceFinanceCategories.Discount) product.Discount += amount;
                if (category == MarketplaceFinanceCategories.Coupon) product.Coupon += amount;
            }
        }
        var billed = new HashSet<Guid>();
        foreach (var item in finance.CargoItems)
        {
            financeStores.Add(item.ConnectionId);
            // A cargo line of no order of the period (an order never imported, an invoice read as a whole) is shown apart.
            if (OrderOf(item.ConnectionId, item.OrderNumber) is not { } cargoOrder)
            {
                Unlinked(MarketplaceFinanceCategories.Cargo).Add(item.Amount);
                continue;
            }
            if (!item.IsReturn) billed.Add(cargoOrder);
            Line(MarketplaceFinanceCategories.Cargo).Add(item.Amount);
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

        // The orders of the platforms whose statement is not read, costed from their tariffs (ReportPlatformCosts).
        var carriers = input.Packages.Where(package => !string.IsNullOrWhiteSpace(package.CargoProvider))
            .GroupBy(package => package.OrderId).ToDictionary(group => group.Key, group => group.First().CargoProvider);
        decimal platformCargo = 0m, platformFees = 0m, platformVatFees = 0m;
        var desiInput = input.Desi ?? ReportDesiInput.None;
        var desiSources = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var order in platformOrders)
        {
            var (desi, desiSource) = ReportParcelDesi.ForOrder(
                desiInput.DeclaredByOrder.TryGetValue(order.Order.Id, out var declared) ? declared : null,
                order.Lines.Select(line => (line.Row.Desi, line.Row.ProductId, line.Row.CategoryId, line.Active)),
                desiInput.Learned);
            desiSources[desiSource] = desiSources.GetValueOrDefault(desiSource) + 1;
            var keptVatFree = order.Country == ReportBuckets.Turkey ? order.Lines.Sum(VatFree) : 0m;
            var cost = ReportPlatformCosts.For(order.PlatformCode, order.Amount, keptVatFree, order.Back > 0 && order.Kept == 0m, desi, carriers.GetValueOrDefault(order.Order.Id));
            var store = order.Order.ConnectionId;
            Line(MarketplaceFinanceCategories.ServiceFee).Estimate(cost.ServiceFee);
            Line(MarketplaceFinanceCategories.Stoppage).Estimate(cost.Stoppage);
            Line(MarketplaceFinanceCategories.PaymentFee).Estimate(cost.PaymentFee);
            foreach (var (category, amount) in new[] { (MarketplaceFinanceCategories.ServiceFee, cost.ServiceFee), (MarketplaceFinanceCategories.Stoppage, cost.Stoppage), (MarketplaceFinanceCategories.PaymentFee, cost.PaymentFee) })
                if (amount != 0m) channelCosts[(store, category)] = channelCosts.GetValueOrDefault((store, category)) + amount;
            estimatedCargo[store] = estimatedCargo.GetValueOrDefault(store) + cost.Cargo;
            platformCargo += cost.Cargo;
            platformFees += cost.ServiceFee;
            // Shopify's own fees (iyzico's BSMV, Shopify's dollar invoice) carry no VAT to deduct.
            if (order.PlatformCode != "SHOPIFY") platformVatFees += cost.ServiceFee;
            financeStores.Add(store);
        }
        foreach (var category in new[] { MarketplaceFinanceCategories.ServiceFee, MarketplaceFinanceCategories.Stoppage, MarketplaceFinanceCategories.PaymentFee })
            if (lines.TryGetValue(category, out var empty) && empty.Amount == 0m && empty.Records == 0) lines.Remove(category);
        platformCargo = Round(platformCargo);
        foreach (var store in estimatedCargo.Keys.ToList()) estimatedCargo[store] = Round(estimatedCargo[store]);
        var allEstimated = (cargoEstimate ?? 0m) + platformCargo;
        var cargoView = new ReportCargoCostView(realCargo, financeOrders.Count(order => billed.Contains(order.Order.Id)), unbilledCount,
            sampleOrders > 0 ? Round(samples.Sum(sample => sample.Amount) / sampleOrders) : null, sampleOrders,
            cargoEstimate is null && platformOrders.Count == 0 ? null : allEstimated, realCargo + allEstimated, platformOrders.Count, platformCargo,
            platformOrders.Count == 0 ? null : new ReportDesiSourcesView(
                desiSources.GetValueOrDefault(ReportParcelDesi.Marketplace), desiSources.GetValueOrDefault(ReportParcelDesi.Manual),
                desiSources.GetValueOrDefault(ReportParcelDesi.Measured), desiSources.GetValueOrDefault(ReportParcelDesi.Default), desiInput.Learned.Parcels));

        // The service fees and the withholding tax of the period's Trendyol orders. Trendyol bills them on invoices of a
        // payment day tied to no order (those are in Unlinked, by their date), so every order without a record of its own
        // is estimated, settled or not; the old invoices and these estimates are never added together.
        foreach (var store in financeOrders.GroupBy(order => order.Order.ConnectionId))
        {
            var feeOrders = store.Where(order => !linkedFees.Contains((order.Order.Id, MarketplaceFinanceCategories.ServiceFee))).ToList();
            var taxOrders = store.Where(order => !linkedFees.Contains((order.Order.Id, MarketplaceFinanceCategories.Stoppage))).ToList();
            if (feeOrders.Count == 0 && taxOrders.Count == 0) continue;
            var serviceFee = Round(feeOrders.Count * PlatformServiceFeePerOrder + feeOrders.Where(order => ReportBuckets.Abroad(order.Country)).Sum(order => order.Kept) * InternationalServiceFeeRate);
            var stoppage = Round(taxOrders.Where(order => order.Country == ReportBuckets.Turkey).SelectMany(order => order.Lines).Sum(VatFree) * StoppageRate);
            Line(MarketplaceFinanceCategories.ServiceFee).Estimate(serviceFee);
            Line(MarketplaceFinanceCategories.Stoppage).Estimate(stoppage);
            channelCosts[(store.Key, MarketplaceFinanceCategories.ServiceFee)] = channelCosts.GetValueOrDefault((store.Key, MarketplaceFinanceCategories.ServiceFee)) + serviceFee;
            channelCosts[(store.Key, MarketplaceFinanceCategories.Stoppage)] = channelCosts.GetValueOrDefault((store.Key, MarketplaceFinanceCategories.Stoppage)) + stoppage;
            financeStores.Add(store.Key);
        }

        // The commission is real where the marketplace's own record charged it: Trendyol's settled orders (above) and the
        // Hepsiburada, n11 and Pazarama orders whose finance records were read (their lines carry it, ReportBuilder.Facts);
        // the rest is estimated.
        bool Real(ReportBuilder.OrderFact order) => settled.Contains(order.Order.Id) || order.RealCommission;
        decimal RealAmount(ReportBuilder.OrderFact order) => settled.Contains(order.Order.Id) ? realCommission.GetValueOrDefault(order.Order.Id) : Estimated(order);
        var settledFacts = orders.Where(order => settled.Contains(order.Order.Id)).ToList();
        var realFacts = orders.Where(Real).ToList();
        var trendyolReal = Round(settledFacts.Sum(order => realCommission.GetValueOrDefault(order.Order.Id)));
        var real = Round(realFacts.Sum(RealAmount));
        var estimatedSettled = Round(realFacts.Sum(order => settled.Contains(order.Order.Id) ? Estimated(order) : order.EstimatedCommission ?? 0m));
        var estimatedOther = Round(orders.Where(order => !Real(order)).Sum(Estimated));
        var commissionView = new ReportRealCommissionView(real, realFacts.Count, estimatedSettled, estimatedOther, real + estimatedOther);
        var settledFinanceOrders = financeOrders.Count(order => settled.Contains(order.Order.Id));

        var lineViews = MarketplaceFinanceCategories.All.Where(lines.ContainsKey)
            .Select(category => new ReportCostLineView(category, Labels.GetValueOrDefault(category, category), TrendyolFinanceCategorizer.IsCost(category), Round(lines[category].Amount), lines[category].Records,
                Round(lines[category].Linked), Round(lines[category].Estimated)))
            .ToList();
        var unlinkedViews = MarketplaceFinanceCategories.All.Where(category => unlinked.TryGetValue(category, out var line) && Round(line.Amount) != 0m)
            .Select(category => new ReportUnlinkedCostView(category, Labels.GetValueOrDefault(category, category), TrendyolFinanceCategorizer.IsCost(category), Round(unlinked[category].Amount), unlinked[category].Records))
            .ToList();
        var unlinkedTotal = unlinkedViews.Where(line => line.IsCost).Sum(line => line.Amount);
        var channels = input.Channels.Values
            .Where(channel => orders.Any(order => order.Order.ConnectionId == channel.Id) || financeStores.Contains(channel.Id))
            .Select(channel =>
            {
                var own = orders.Where(order => order.Order.ConnectionId == channel.Id).ToList();
                var channelReal = Round(own.Where(Real).Sum(RealAmount));
                var channelEstimate = Round(own.Where(order => !Real(order)).Sum(Estimated));
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

        // The net VAT of the Trendyol orders: the VAT in the domestic sales, less the VAT in their product cost and in
        // Trendyol's VAT-inclusive charges (the international service fee and the withholding tax carry none) on the
        // period's orders; a record tied to no order of the period (Unlinked) carries no VAT of theirs.
        var trendyolCommission = trendyolReal + Round(financeOrders.Where(order => !settled.Contains(order.Order.Id)).Sum(Estimated));
        var trendyolFees = linkedDomesticFees + financeOrders.Count(order => !linkedFees.Contains((order.Order.Id, MarketplaceFinanceCategories.ServiceFee))) * PlatformServiceFeePerOrder;
        var advertising = lines.TryGetValue(MarketplaceFinanceCategories.Advertising, out var adLine) ? adLine.Amount : 0m;
        var vatOrders = financeOrders.Concat(platformOrders).ToList();
        var platformCommission = Round(platformOrders.Sum(Estimated));
        var salesVat = vatOrders.Where(order => order.Country == ReportBuckets.Turkey).SelectMany(order => order.Lines).Sum(line => Vat(line.KeptAmount, line.Row.VatRate));
        var costVat = vatOrders.SelectMany(order => order.Lines).Where(line => line.Row.CostPrice is > 0).Sum(line => Vat(line.Row.CostPrice!.Value * line.KeptUnits, line.Row.VatRate));
        var expenseVat = Vat(trendyolCommission + platformCommission + cargoView.Combined + trendyolFees + platformVatFees + advertising, ExpenseVatRate);
        var netVat = new ReportNetVatView(Round(salesVat), Round(costVat), Round(expenseVat), Round(salesVat - costVat - expenseVat));
        return new(read ? FinanceSource : EstimateSource, lastSynced, coveredFrom, coveredThrough, financeOrders.Count, settledFinanceOrders,
            financeOrders.Count > 0 ? Math.Round((decimal)settledFinanceOrders / financeOrders.Count, 4) : null,
            total, commissionView, cargoView, lineViews, channels, productViews, netVat, unlinkedViews, unlinkedTotal);
    }

    internal static bool IsCommissionInvoice(ReportFinanceRow row) =>
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
                ? "Gerçek masraf verisi yok: seçili mağazalarda canlı Trendyol mağazası yok; masraflar ve pazaryeri kaydı olmayan siparişlerin komisyonu tahminidir."
                : input.Finance!.States.Values.All(state => state?.CoveredThrough is null)
                    ? "Gerçek masraf verisi yok: Trendyol finans kayıtları henüz okunmadı; masraflar ve pazaryeri kaydı olmayan siparişlerin komisyonu tahminidir."
                    : "Trendyol finans kayıtlarında bu döneme ait kayıt yok; masraflar ve pazaryeri kaydı olmayan siparişlerin komisyonu tahminidir.";
            if (PlatformNote(costs, tr) is { } unread) yield return unread;
            if (RealCommissionNote(costs, tr) is { } read) yield return read;
            yield break;
        }
        if (PlatformNote(costs, tr) is { } platforms) yield return platforms;
        yield return $"Gerçek masraflar Trendyol cari hesap ekstresinden okunur{(costs.LastSyncedAt is { } synced ? $" (son okuma {Time(synced)})" : " (okuma sürüyor)")}: siparişe bağlı kalemler siparişin dönemine yazılır. Trendyol hizmet bedelini ve stopajı ödeme günü faturasıyla keser; bu faturalar ödemenin siparişlerine paylaştırılır (hizmet bedeli paket başına, uluslararası hizmet bedeli yurt dışı satışa, stopaj satışa göre) ve o siparişlerle kâra girer. Faturası henüz gelmeyen siparişlerin hizmet bedeli ve stopajı tahmin edilir. Dönemin siparişlerine bağlı olmayan kesintiler (reklam, ceza) kâra dahil edilmez, ayrıca gösterilir{(costs.UnlinkedTotal != 0m ? $" ({Money(costs.UnlinkedTotal)})" : "")}.";
        // The per-sale fee invoices left with no order: their payout's orders are not read yet, or they name no payout.
        var unsplit = input.Finance!.Rows.Where(row => row.Source == TrendyolFinanceRequestContract.OtherFinancials && row.OrderNumber is null
            && row.Category is MarketplaceFinanceCategories.ServiceFee or MarketplaceFinanceCategories.Stoppage).ToList();
        var awaiting = unsplit.Where(ReportFeeAllocation.IsPerSaleInvoice).Sum(row => TrendyolFinanceCategorizer.NetCost(row.Debt, row.Credit));
        var unreferenced = unsplit.Where(row => string.IsNullOrEmpty(row.PaymentOrderId)).Sum(row => TrendyolFinanceCategorizer.NetCost(row.Debt, row.Credit));
        if (awaiting != 0m)
            yield return $"Siparişe bağlı olmayan kesintilerdeki hizmet bedeli ve stopajın {Money(awaiting)} kadarı, Trendyol'un ödeme kayıtlarını henüz vermediği ödemelere ait; ödeme kayıtları okununca (ödemeden bir gün sonra) siparişlerine paylaştırılır ve bu listeden çıkar.";
        if (unreferenced != 0m)
            yield return $"{Money(unreferenced)} hizmet bedeli ve stopaj faturasında Trendyol ne sipariş ne ödeme bilgisi veriyor; hangi siparişe ait olduğu bilinmediği için siparişe bağlı olmayan kesintilerde kalır.";
        if (costs.Coverage is { } coverage)
            yield return $"Trendyol siparişlerinin {costs.SettledOrders.ToString("N0", tr)} / {costs.FinanceOrders.ToString("N0", tr)} kadarında (%{coverage * 100:N1}) hesap kesimi var; Trendyol kaydı teslimattan sonra yazar, kalan siparişlerin komisyonu tahminidir.";
        if (RealCommissionNote(costs, tr) is { } real) yield return real;
        if (costs.CoveredFrom is { } from && costs.CoveredThrough is { } through
            && (input.Period.Start < from || input.Period.End > through))
            yield return $"Trendyol finans kayıtları {Date(from)} – {Date(through)} aralığında okundu; dönemin bu aralık dışındaki kısmının masrafları eksik olabilir.";
        var others = costs.Lines.Where(line => line.Category == MarketplaceFinanceCategories.Other).Select(line => (line.Records, line.Amount))
            .Concat((costs.Unlinked ?? []).Where(line => line.Category == MarketplaceFinanceCategories.Other).Select(line => (line.Records, line.Amount))).ToList();
        if (others.Sum(other => other.Records) is > 0 and var otherRecords)
            yield return $"Sınıflandırılamayan {otherRecords.ToString("N0", tr)} finans kaydı ({Money(others.Sum(other => other.Amount))}) \"Diğer\" altında gösterilir ve toplam masrafa eklenmez.";
        var rejected = input.Finance!.States.Values.Where(state => !string.IsNullOrWhiteSpace(state?.RejectedTypes)).SelectMany(state => state!.RejectedTypes!.Split(',')).Distinct(StringComparer.Ordinal).ToList();
        if (rejected.Count > 0)
            yield return $"Trendyol şu kayıt türlerini vermedi, bu türler masraflarda yok: {string.Join(", ", rejected)}.";
        yield return "Trendyol'un komisyon faturaları, hesap kesimindeki komisyonun faturasıdır; komisyona ikinci kez eklenmez.";
        if (costs.Cargo is { } cargo && (cargo.BilledOrders > 0 || cargo.UnbilledOrders > 0))
        {
            var billed = $"Kargo, Trendyol kargo faturalarının paket satırlarından okunur: {cargo.BilledOrders.ToString("N0", tr)} siparişin kargo faturası geldi ({Money(cargo.Real)}).";
            yield return cargo.UnbilledOrders == 0
                ? billed
                : cargo.Estimated is { } estimated && cargo.AveragePerOrder is { } average
                    ? $"{billed} Trendyol kargoyu gönderiden haftalar sonra faturalar; faturası henüz gelmeyen {cargo.UnbilledOrders.ToString("N0", tr)} siparişin kargosu mağazanın güncel kargo tarifesiyle ({Money(average)}: son 30 günün {cargo.SampleOrders.ToString("N0", tr)} faturalı siparişinde en sık kesilen tutar) tahmin edildi: {Money(estimated - cargo.PlatformEstimated)}."
                    : $"{billed} Faturası henüz gelmeyen {cargo.UnbilledOrders.ToString("N0", tr)} siparişin kargosu tahmin edilemedi (son 60 günde faturalanmış kargo yok); kargo eksiktir.";
        }
        yield return "Kargo ve hizmet bedelleri ürünlere paylaştırılmaz.";
    }

    // What the platforms costed from their tariffs (ReportPlatformCosts) rest on.
    private static string? PlatformNote(ReportCostsView costs, CultureInfo tr) =>
        costs.Cargo is { PlatformOrders: > 0 } cargo
            ? $"Hepsiburada, n11, Pazarama, PTT AVM ve Shopify'ın {cargo.PlatformOrders.ToString("N0", tr)} siparişinin kargosu ({cargo.PlatformEstimated.ToString("N2", tr)} ₺), hizmet bedelleri ve stopajı pazaryerlerinin 2026 tarifelerinden tahmin edildi: kargo, paketin kargo firması ve ürünlerin desisiyle (desi girilmemiş ürün 1 desi sayılır, barem sınırları uygulanır); tamamen iade edilen siparişte dönüş kargosu da satıcıya yazılır. Shopify'da sipariş başına 120 ₺ kargo ve kartlı ödeme (iyzico %4,29 + 0,25 ₺, Shopify %2) varsayıldı."
            : null;

    // The orders whose commission the marketplace's own record gave (Trendyol's settlement, Hepsiburada's commission
    // records, n11's settlement lines, Pazarama's money movements), beside what the estimate said for them.
    private static string? RealCommissionNote(ReportCostsView costs, CultureInfo tr) =>
        costs.Commission is { RealOrders: > 0 } commission
            ? $"Gerçek Komisyon: pazaryerinin kendi kaydı olan {commission.RealOrders.ToString("N0", tr)} siparişte (Trendyol hesap kesimi, Hepsiburada muhasebe kayıtları, n11 hakediş kayıtları, Pazarama finans hareketleri) komisyon tahmin yerine gerçek tutarla (iadeler ve düzeltmeler düşülmüş) alındı: {commission.Real.ToString("N2", tr)} ₺, aynı siparişlerin tahmini {commission.EstimatedForSettled.ToString("N2", tr)} ₺. Kaydı olmayan siparişlerin komisyonu Tahmini Komisyon'dur: {commission.EstimatedForUnsettled.ToString("N2", tr)} ₺."
            : null;

    private static bool IsFinanceStore(ReportInput input, Guid connectionId) =>
        input.Channels.TryGetValue(connectionId, out var channel) && channel.PlatformCode == "TRENDYOL" && !channel.IsTest;

    // The VAT inside a VAT-inclusive amount at a rate in percent, and a line's kept sale without its VAT.
    internal static decimal Vat(decimal amount, decimal rate) => rate > 0 ? amount * rate / (100m + rate) : 0m;
    internal static decimal VatFree(ReportBuilder.LineFact line) => line.KeptAmount - Vat(line.KeptAmount, line.Row.VatRate);

    internal static bool IsInternationalFee(ReportFinanceRow row) =>
        TrendyolFinanceCategorizer.Fold(row.TransactionType).Contains("uluslararasi", StringComparison.Ordinal);

    private static decimal Round(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    private sealed class LineTotal
    {
        public decimal Amount;
        public decimal Linked;
        public decimal Estimated;
        public int Records;

        public void Estimate(decimal amount)
        {
            Amount += amount;
            Estimated += amount;
        }

        public void Add(decimal amount)
        {
            Amount += amount;
            Linked += amount;
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
