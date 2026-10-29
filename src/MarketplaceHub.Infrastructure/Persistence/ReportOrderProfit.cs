using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Adapters.Trendyol;

namespace MarketplaceHub.Infrastructure.Persistence;

// Each order's profit, as the report costs the whole period (ReportCosts) but order by order: what the customer kept
// paying, less the product cost, the commission, the cargo (shipping and return), the platform's fees, the withholding
// tax and the net VAT. Trendyol's statement gives the real amounts of a settled order and its billed cargo; the rest is
// estimated the way the report estimates it (the line's commission rate, the store's current cargo tariff, 13.19 TL
// service fee, 1 % withholding tax) and the other platforms are costed from their tariffs (ReportPlatformCosts).
internal static class ReportOrderProfit
{
    public static List<ReportOrderProfitView> Build(ReportInput input, IReadOnlyList<ReportBuilder.OrderFact> orders)
    {
        var finance = input.Finance;
        var byNumber = orders.GroupBy(order => (order.Order.ConnectionId, order.Order.OrderNumber)).ToDictionary(group => group.Key, group => group.First().Order.Id);
        Guid? OrderOf(Guid connectionId, string? orderNumber) =>
            orderNumber is not null && byNumber.TryGetValue((connectionId, orderNumber), out var id) ? id : null;

        var commission = new Dictionary<Guid, decimal>();
        var settled = new HashSet<Guid>();
        var fees = new Dictionary<(Guid Order, string Category), decimal>();
        var domesticFee = new Dictionary<Guid, decimal>();
        var cargo = new Dictionary<Guid, decimal>();
        var shipped = new HashSet<Guid>();
        var returnBilled = new HashSet<Guid>();
        if (finance is not null)
        {
            foreach (var row in finance.Rows)
            {
                if (TrendyolFinanceCategorizer.IsCargoInvoice(row.Source, row.RequestedType, row.Category) && finance.ItemizedInvoices.Contains((row.ConnectionId, row.ExternalId))) continue;
                if (ReportCosts.IsCommissionInvoice(row)) continue;
                if (OrderOf(row.ConnectionId, row.OrderNumber) is not { } orderId) continue;
                if (row.Source == TrendyolFinanceRequestContract.Settlements) settled.Add(orderId);
                var category = TrendyolFinanceCategorizer.Charged(row.Source, row.RequestedType, row.Category, row.Debt, row.Credit);
                var charged = row.Source == TrendyolFinanceRequestContract.Settlements
                    ? TrendyolFinanceCategorizer.CommissionCost(row.RequestedType, row.CommissionAmount, row.Debt, row.Credit)
                    : category == MarketplaceFinanceCategories.Commission ? TrendyolFinanceCategorizer.NetCost(row.Debt, row.Credit) : 0m;
                if (charged != 0m || category == MarketplaceFinanceCategories.Commission)
                {
                    commission[orderId] = commission.GetValueOrDefault(orderId) + charged;
                    continue;
                }
                if (!TrendyolFinanceCategorizer.IsCost(category)) continue;
                var amount = TrendyolFinanceCategorizer.NetCost(row.Debt, row.Credit);
                if (category == MarketplaceFinanceCategories.Cargo) { cargo[orderId] = cargo.GetValueOrDefault(orderId) + amount; shipped.Add(orderId); continue; }
                fees[(orderId, category)] = fees.GetValueOrDefault((orderId, category)) + amount;
                if (category == MarketplaceFinanceCategories.ServiceFee && !ReportCosts.IsInternationalFee(row)) domesticFee[orderId] = domesticFee.GetValueOrDefault(orderId) + amount;
            }
            foreach (var item in finance.CargoItems)
            {
                if (OrderOf(item.ConnectionId, item.OrderNumber) is not { } orderId) continue;
                cargo[orderId] = cargo.GetValueOrDefault(orderId) + item.Amount;
                if (item.IsReturn) returnBilled.Add(orderId); else shipped.Add(orderId);
            }
        }

        // A line without a commission is estimated at its store's rate over the period's lines that have one.
        var storeRates = orders.SelectMany(order => order.Lines.Where(line => line.Commission is not null && line.KeptAmount > 0).Select(line => (order.Order.ConnectionId, line)))
            .GroupBy(item => item.ConnectionId)
            .ToDictionary(group => group.Key, group => group.Sum(item => item.line.Commission!.Value) / group.Sum(item => item.line.KeptAmount));
        var carriers = input.Packages.Where(package => !string.IsNullOrWhiteSpace(package.CargoProvider))
            .GroupBy(package => package.OrderId).ToDictionary(group => group.Key, group => group.First().CargoProvider);
        var desiInput = input.Desi ?? ReportDesiInput.None;

        var views = new List<ReportOrderProfitView>(orders.Count);
        foreach (var order in orders.Where(order => order.Currency == "TRY" && (order.Amount > 0 || order.Back > 0)))
        {
            var id = order.Order.Id;
            var store = order.Order.ConnectionId;
            var domestic = order.Country == ReportBuckets.Turkey;
            // An order whose country is unknown (ReportBuckets.UnknownCountry) is neither: no domestic VAT or withholding, no
            // international fee.
            var abroad = ReportBuckets.Abroad(order.Country);
            var trendyol = order.PlatformCode == "TRENDYOL";
            var keptVatFree = domestic ? order.Lines.Sum(ReportCosts.VatFree) : 0m;
            var costs = new List<ReportOrderProfitCostView>();

            // The product cost of the kept units; unknown when a kept line has none.
            var keptLines = order.Lines.Where(line => line.KeptUnits > 0).ToList();
            decimal? productCost = keptLines.All(line => line.Row.CostPrice is > 0) ? Round(keptLines.Sum(line => line.Row.CostPrice!.Value * line.KeptUnits)) : null;

            var lineCommission = order.Lines.Sum(line => line.Commission ?? (storeRates.TryGetValue(store, out var rate) ? line.KeptAmount * rate : 0m));
            var realCommission = settled.Contains(id) && commission.ContainsKey(id);
            var orderCommission = Round(realCommission ? commission[id] : lineCommission);
            costs.Add(new("commission", "Komisyon", orderCommission, !realCommission, realCommission ? "Trendyol cari hesabından"
                : order.Lines.Any(line => line.CommissionFromTariff) ? "Komisyon tarifesinden"
                // The line's rate is read again on every order sync, so Trendyol's discounted rate, sent once the order is
                // being processed, replaces the first one.
                : trendyol ? "Siparişten gelen oran; Trendyol indirimli komisyonu sipariş işleme alınınca bildirir, oran o zaman güncellenir"
                : "Siparişteki komisyon oranından"));

            decimal? orderCargo;
            decimal serviceFee, stoppage, paymentFee = 0m, vatFees;
            if (trendyol)
            {
                var perOrder = finance?.CargoSamples.GetValueOrDefault(store)?.PerOrder;
                var billed = cargo.GetValueOrDefault(id);
                var unbilled = (!shipped.Contains(id) ? 1 : 0) + (order.Back > 0 && !returnBilled.Contains(id) ? 1 : 0);
                var estimated = unbilled * (perOrder ?? 0m);
                // A cargo neither billed nor estimable (no store tariff: no cargo billed in the 60 days before the period's
                // end) is unknown, and so the profit, as the report's cargo is (ReportCosts.Notes); never 0 "from the invoice".
                orderCargo = unbilled > 0 && perOrder is null ? null : Round(billed + estimated);
                costs.Add(new("cargo", order.Back > 0 ? "Kargo (Gönderi ve İade)" : "Kargo", orderCargo, estimated > 0,
                    orderCargo is null ? "Kargo faturası henüz yok ve son 60 günde faturalanmış kargo olmadığı için tahmin edilemedi"
                    : estimated > 0 ? "Mağazanın güncel kargo tarifesinden; fatura kesilince kesin tutar yansır" : "Kargo faturasından"));
                var settledFees = settled.Contains(id);
                serviceFee = Round(settledFees ? fees.GetValueOrDefault((id, MarketplaceFinanceCategories.ServiceFee))
                    : ReportCosts.PlatformServiceFeePerOrder + (abroad ? order.Kept * ReportCosts.InternationalServiceFeeRate : 0m));
                stoppage = Round(settledFees ? fees.GetValueOrDefault((id, MarketplaceFinanceCategories.Stoppage)) : keptVatFree * ReportCosts.StoppageRate);
                vatFees = settledFees ? domesticFee.GetValueOrDefault(id) : ReportCosts.PlatformServiceFeePerOrder;
                costs.Add(new("serviceFee", abroad ? "Hizmet Bedeli (Uluslararası Dahil)" : "Hizmet Bedeli", serviceFee, !settledFees,
                    settledFees ? "Trendyol cari hesabından" : abroad ? "10,99 ₺ + KDV ve satışın %6'sı" : "Sipariş başına 10,99 ₺ + KDV"));
                costs.Add(new("stoppage", "Stopaj", stoppage, !settledFees, settledFees ? "Trendyol cari hesabından" : "KDV hariç satışın %1'i"));
            }
            else
            {
                var (desi, _) = ReportParcelDesi.ForOrder(
                    desiInput.DeclaredByOrder.TryGetValue(id, out var declared) ? declared : null,
                    order.Lines.Select(line => (line.Row.Desi, line.Row.ProductId, line.Row.CategoryId, line.Active)), desiInput.Learned);
                var estimate = ReportPlatformCosts.Covers(order.PlatformCode)
                    ? ReportPlatformCosts.For(order.PlatformCode, order.Amount, keptVatFree, order.Back > 0 && order.Kept == 0m, desi, carriers.GetValueOrDefault(id))
                    : new ReportPlatformCosts.Estimate(0m, 0m, 0m, 0m);
                orderCargo = Round(estimate.Cargo);
                serviceFee = Round(estimate.ServiceFee);
                stoppage = Round(estimate.Stoppage);
                paymentFee = Round(estimate.PaymentFee);
                vatFees = order.PlatformCode == "SHOPIFY" ? 0m : estimate.ServiceFee;
                costs.Add(new("cargo", "Kargo", orderCargo, true, "Platformun 2026 kargo tarifesinden"));
                costs.Add(new("serviceFee", "Hizmet Bedeli", serviceFee, true, "Platformun 2026 tarifesinden"));
                costs.Add(new("stoppage", "Stopaj", stoppage, true, "KDV hariç satışın %1'i"));
                if (paymentFee != 0m) costs.Add(new("paymentFee", "Ödeme Komisyonu", paymentFee, true, "Ödeme kuruluşunun oranından"));
            }
            // Other charges Trendyol tied to the order (a penalty, an early payment fee…).
            var other = Round(fees.Where(fee => fee.Key.Order == id && fee.Key.Category is not (MarketplaceFinanceCategories.ServiceFee or MarketplaceFinanceCategories.Stoppage)).Sum(fee => fee.Value));
            if (other != 0m) costs.Add(new("other", "Diğer Kesinti", other, false, "Trendyol cari hesabından"));

            // Net VAT: the VAT in the kept domestic sale, less the VAT in the product cost and in the platform's charges.
            var salesVat = domestic ? order.Lines.Sum(line => ReportCosts.Vat(line.KeptAmount, line.Row.VatRate)) : 0m;
            var costVat = keptLines.Where(line => line.Row.CostPrice is > 0).Sum(line => ReportCosts.Vat(line.Row.CostPrice!.Value * line.KeptUnits, line.Row.VatRate));
            // Each VAT rounded to kuruş on its own, as the invoices carry it (25 Oct 2026: 42.36 − 14.55 − 16.00 − 16.39 − 2.20).
            // An unknown cargo leaves its VAT, and so the net VAT, unknown.
            decimal? netVat = orderCargo is { } knownCargo
                ? Round(salesVat) - Round(costVat) - Round(ReportCosts.Vat(orderCommission, ReportCosts.ExpenseVatRate))
                    - Round(ReportCosts.Vat(knownCargo, ReportCosts.ExpenseVatRate)) - Round(ReportCosts.Vat(vatFees, ReportCosts.ExpenseVatRate))
                : null;
            costs.Add(new("netVat", "Net KDV", netVat, false, netVat is null ? "Kargo bilinmediği için hesaplanamadı" : "Satış KDV'si − maliyet, komisyon, kargo ve hizmet bedeli KDV'si"));

            var amount = Round(order.Kept);
            decimal? profit = productCost is { } knownCost && orderCargo is { } cargoCost && netVat is { } vat
                ? Round(amount - knownCost - orderCommission - cargoCost - serviceFee - stoppage - paymentFee - other - vat) : null;
            var state = order.Back > 0 && order.Kept == 0m ? "RETURNED" : order.Back > 0 ? "PARTLY_RETURNED" : "SOLD";
            views.Add(new ReportOrderProfitView(id, order.Order.OrderNumber, order.PlatformCode, input.Channels.TryGetValue(store, out var channel) ? channel.DisplayName : "",
                order.Order.StoreFrontCode, abroad, order.Order.RealAt, state, amount, productCost, profit,
                profit is { } p && productCost is > 0 ? Math.Round(p / productCost.Value * 100m, 2) : null,
                profit is { } q && amount > 0 ? Math.Round(q / amount * 100m, 2) : null,
                costs,
                order.Lines.Select(line => new ReportOrderProfitLineView(line.Row.VariantId, line.Row.ProductId, line.Row.Title, line.Row.Barcode, line.Row.Sku, line.KeptUnits, line.Back,
                    Round(line.KeptAmount), line.Commission is { } lc && line.KeptAmount > 0 ? Math.Round(lc / line.KeptAmount * 100m, 2) : null, line.Commission is { } c ? Round(c) : null,
                    line.Row.CostPrice, line.Row.VatRate)).ToList()));
        }
        return views;
    }

    // The page's totals: the amount of every order, but the profit and the margin only of the orders whose profit is
    // known, so an order without a cost (or with an unknown cargo) neither counts as 0 profit nor pulls the margin down.
    public static OrderTotals Totals(IReadOnlyCollection<ReportOrderProfitView> orders)
    {
        var known = orders.Where(order => order.Profit is not null).ToList();
        var profit = known.Count == 0 ? (decimal?)null : known.Sum(order => order.Profit!.Value);
        var profitAmount = known.Sum(order => order.Amount);
        return new(orders.Sum(order => order.Amount), profit, profitAmount, known.Count,
            profit is { } p && profitAmount > 0 ? Math.Round(p / profitAmount * 100m, 2) : null,
            orders.Count(order => order.Cost is null),
            orders.Count(order => order.Costs.Any(cost => cost.Key == "cargo" && cost.Amount is null)));
    }

    internal sealed record OrderTotals(decimal Amount, decimal? Profit, decimal ProfitAmount, int ProfitOrders, decimal? Margin, int MissingCost, int MissingCargo);

    private static decimal Round(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);
}
