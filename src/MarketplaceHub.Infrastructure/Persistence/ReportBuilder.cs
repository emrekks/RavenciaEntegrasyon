using System.Globalization;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;

namespace MarketplaceHub.Infrastructure.Persistence;

internal sealed record ReportPeriod(DateOnly From, DateOnly To, DateTimeOffset Start, DateTimeOffset End)
{
    public int Days => To.DayNumber - From.DayNumber + 1;
}

internal sealed record ReportCatalogNames(IReadOnlyDictionary<Guid, string> Brands, IReadOnlyDictionary<Guid, string> Categories);

internal sealed record ReportInput(
    ReportPeriod Period,
    string Granularity,
    TimeZoneInfo Timezone,
    IReadOnlyDictionary<Guid, ReportChannelView> Channels,
    IReadOnlyList<ReportOrderRow> Orders,
    IReadOnlyDictionary<Guid, ReportOrderExtra> OrderExtras,
    IReadOnlyList<ReportLineRow> Lines,
    IReadOnlyDictionary<Guid, ReportLineExtra> LineExtras,
    IReadOnlyList<ReportClaimRow> Claims,
    IReadOnlyList<ReportPackageRow> Packages,
    ReportCatalogNames Names,
    bool IncludesTest);

internal sealed class ReportOrderRow
{
    public Guid Id { get; init; }
    public Guid ConnectionId { get; init; }
    public string Currency { get; init; } = "TRY";
    public string DerivedStatus { get; init; } = "";
    public decimal Amount { get; init; }
    public decimal PackagesBack { get; init; }
    public decimal LineValue { get; init; }
    public decimal LineBack { get; init; }
    public DateTimeOffset RealAt { get; init; }
    public DateTimeOffset OrderedAt { get; init; }
    public DateTimeOffset? ShipmentDueAt { get; init; }
}

internal sealed class ReportOrderExtra
{
    public Guid Id { get; init; }
    public string? CityName { get; init; }
    public string? City { get; init; }
    public string? ShippingCity { get; init; }
    public string? Province { get; init; }
    public string? DistrictName { get; init; }
    public string? District { get; init; }
    public string? ShippingDistrict { get; init; }
    public string? Town { get; init; }
    public string? ShippingTown { get; init; }
    public string? CustomerKey { get; init; }
}

internal sealed class ReportLineRow
{
    public Guid Id { get; init; }
    public Guid OrderId { get; init; }
    public Guid? VariantId { get; init; }
    public Guid? ProductId { get; init; }
    public Guid? BrandId { get; init; }
    public Guid? CategoryId { get; init; }
    public string Title { get; init; } = "";
    public string Sku { get; init; } = "";
    public decimal UnitPrice { get; init; }
    public decimal OrderedQuantity { get; init; }
    public decimal CancelledQuantity { get; init; }
    public decimal ReturnedQuantity { get; init; }
    public decimal AcceptedReturnQuantity { get; init; }
    public decimal? CostPrice { get; init; }
}

internal sealed class ReportLineExtra
{
    public Guid Id { get; init; }
    public string? CommissionType { get; init; }
    public string? Commission { get; init; }
    public string? CommissionRate { get; init; }
    public string? Komisyon { get; init; }
    public string? CancelReason { get; init; }
}

internal sealed class ReportClaimRow
{
    public Guid OrderId { get; init; }
    public ReturnClaimStatus Status { get; init; }
    public string? ReasonCode { get; init; }
    public string? ReasonText { get; init; }
}

internal sealed class ReportPackageRow
{
    public Guid Id { get; init; }
    public Guid OrderId { get; init; }
    public string? CargoProvider { get; init; }
    public ShipmentPackageStatus Status { get; init; }
    public decimal Amount { get; init; }
    public MarketplaceInvoiceStatus MarketplaceInvoiceStatus { get; init; }
    public string? ManualInvoiceStatus { get; init; }
    public bool PanelInvoiced { get; init; }
    public DateTimeOffset? ShippedAt { get; init; }
    public DateTimeOffset? DeliveredAt { get; init; }
}

// Buckets of the time series, the test-store rule, the address and commission readings.
public static class ReportBuckets
{
    public const string Day = "day";
    public const string Week = "week";
    public const string Month = "month";

    // A chosen granularity, or one that keeps the chart readable: days up to two months, weeks up to half a year.
    public static string? Resolve(string? requested, DateOnly from, DateOnly to)
    {
        var value = requested?.Trim().ToLowerInvariant();
        if (value is Day or Week or Month) return value;
        if (!string.IsNullOrEmpty(value)) return null;
        var days = to.DayNumber - from.DayNumber + 1;
        return days <= 62 ? Day : days <= 190 ? Week : Month;
    }

    public static DateOnly Start(DateOnly day, string granularity) => granularity switch
    {
        Week => day.AddDays(-(((int)day.DayOfWeek + 6) % 7)),
        Month => new DateOnly(day.Year, day.Month, 1),
        _ => day
    };

    public static IEnumerable<DateOnly> Starts(DateOnly from, DateOnly to, string granularity)
    {
        for (var start = Start(from, granularity); start <= to; start = granularity switch { Week => start.AddDays(7), Month => start.AddMonths(1), _ => start.AddDays(1) })
            yield return start;
    }

    // Only a production store is a real one; a stage, sandbox or test store holds test orders.
    public static bool IsTestEnvironment(string? environment) => !string.Equals(environment?.Trim(), "PRODUCTION", StringComparison.OrdinalIgnoreCase);

    // The first of the address's city fields that names a province.
    public static string? Province(params string?[] candidates)
    {
        foreach (var candidate in candidates)
            if (TurkishProvinces.Normalize(candidate) is { } province) return province;
        return null;
    }

    // A district as written, trimmed and in Turkish title case ("KADIKÖY" -> "Kadıköy"); null when none is given.
    public static string? District(params string?[] candidates)
    {
        var tr = CultureInfo.GetCultureInfo("tr-TR");
        foreach (var candidate in candidates)
        {
            var value = candidate?.Trim();
            if (string.IsNullOrEmpty(value) || value.All(char.IsDigit)) continue;
            return string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                .Select(word => word.Length == 1 ? word.ToUpper(tr) : char.ToUpper(word[0], tr) + word[1..].ToLower(tr)));
        }
        return null;
    }

    // The commission of a line's kept units, estimated from what the marketplace sent on the line, or null when it
    // sent nothing usable. A rate (Trendyol "commission", n11 "commissionRate", PTT AVM "Komisyon") is taken of the
    // kept units' paid amount; Hepsiburada's "commission" is the amount for the whole line, shared out to kept units.
    public static decimal? EstimatedCommission(string platformCode, string? commissionType, string? commission, string? commissionRate, string? komisyon,
        decimal keptAmount, decimal keptUnits, decimal activeUnits)
    {
        if (keptUnits <= 0) return 0m;
        switch (platformCode)
        {
            case "TRENDYOL":
                return Rate(commission) is { } trendyolRate ? Math.Round(keptAmount * trendyolRate / 100m, 2) : null;
            case "HEPSIBURADA":
                if (commissionType == "object" && Number(commission) is { } amount && amount >= 0 && activeUnits > 0)
                    return Math.Round(amount * keptUnits / activeUnits, 2);
                return Rate(commissionRate) is { } hepsiburadaRate ? Math.Round(keptAmount * hepsiburadaRate / 100m, 2) : null;
            case "N11":
                return Rate(commissionRate) is { } n11Rate ? Math.Round(keptAmount * n11Rate / 100m, 2) : null;
            case "PTTAVM":
                return Rate(komisyon) is { } pttRate ? Math.Round(keptAmount * pttRate / 100m, 2) : null;
            default:
                return null;
        }
    }

    private static decimal? Rate(string? value) => Number(value) is { } rate && rate > 0 && rate <= 100 ? rate : null;

    private static decimal? Number(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        // "21.5" and "20,5" are both a decimal; no marketplace sends a thousands separator in a rate or line amount.
        var text = value.Trim().TrimEnd('%').Trim();
        if (text.Contains(',') && !text.Contains('.')) text = text.Replace(',', '.');
        return decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number) ? number : null;
    }
}

internal static class ReportBuilder
{
    internal const string Unknown = TurkishProvinces.Unknown;
    private const string Try = "TRY";
    private const int TopCount = 20;
    // Trendyol's seller score guidance: cancelling more than 2% of orders hurts the store.
    private const decimal CancelWarningRate = 0.02m;

    // The variants whose stock the report shows (the best sellers), so stock is read for those only.
    public static Guid[] StockCandidates(ReportInput input) =>
        Products(Facts(input).Orders, input.Names)
            .Where(x => x.VariantId != null)
            .OrderByDescending(x => x.SoldQuantity - x.ReturnedQuantity)
            .Take(TopCount * 5)
            .Select(x => x.VariantId!.Value)
            .ToArray();

    public static ReportSalesView Build(ReportInput input, ReportInput previous, IReadOnlyDictionary<Guid, decimal> stock)
    {
        var (orders, cancelled) = Facts(input);
        var (previousOrders, previousCancelled) = Facts(previous);
        var summary = Summary(orders, cancelled);
        var previousSummary = Summary(previousOrders, previousCancelled);
        var lines = orders.SelectMany(order => order.Lines.Select(line => (order, line))).ToList();
        var products = Products(orders, input.Names);

        var commission = CommissionView(orders, summary.NetRevenue);
        var cost = CostView(orders);
        var platforms = Groups(orders, order => order.PlatformCode, order => order.PlatformCode, _ => false,
            group => Groups(group, order => order.Order.ConnectionId.ToString("D"), order => input.Channels.TryGetValue(order.Order.ConnectionId, out var channel) ? channel.DisplayName : "?",
                order => input.Channels.TryGetValue(order.Order.ConnectionId, out var channel) && channel.IsTest, null, null), null);
        var cities = Groups(orders, order => order.City ?? Unknown, order => order.City ?? Unknown, _ => false,
            group => Groups(group, order => Fold(order.District), order => order.District ?? Unknown, _ => false, null, null),
            group => Groups(group, order => order.PlatformCode, order => order.PlatformCode, _ => false, null, null));
        var brands = LineGroups(lines, line => line.Row.BrandId is { } id && input.Names.Brands.TryGetValue(id, out var name) ? name : null);
        var categories = LineGroups(lines, line => line.Row.CategoryId is { } id && input.Names.Categories.TryGetValue(id, out var name) ? name : null);

        var validClaims = input.Claims.Where(x => x.Status is not (ReturnClaimStatus.Rejected or ReturnClaimStatus.Cancelled)).ToList();
        var reasons = Reasons(validClaims.Select(claim => Label(claim.ReasonText, claim.ReasonCode)));

        var stockView = StockView(products, stock, input.Period.Days);
        var view = new ReportSalesView(
            input.Period.From, input.Period.To, previous.Period.From, previous.Period.To, input.Granularity, input.IncludesTest, default,
            summary, previousSummary, commission, cost, platforms, cities,
            Series(orders, cancelled, input.Period, input.Granularity), Series(previousOrders, previousCancelled, previous.Period, input.Granularity),
            products.OrderByDescending(x => x.SoldQuantity).ThenBy(x => x.Title, StringComparer.Ordinal).Take(TopCount).ToList(),
            products.Where(x => x.ReturnedQuantity > 0).OrderByDescending(x => x.ReturnedQuantity).ThenBy(x => x.Title, StringComparer.Ordinal).Take(TopCount).ToList(),
            brands, categories, reasons,
            Cancellations(orders, cancelled, input),
            Cargo(orders, input.Packages),
            Heatmap(orders, input.Timezone),
            Customers(orders),
            Invoices(orders, input.Packages),
            stockView,
            [],
            Notes(summary, commission, cost, orders, cancelled, input));
        return view;
    }

    // The period's non-cancelled orders with their units, money and buckets, and the cancelled ones apart.
    internal static (List<OrderFact> Orders, List<CancelledFact> Cancelled) Facts(ReportInput input)
    {
        var cancelledStatuses = DashboardRevenuePolicy.CancelledStatuses;
        var returnedStatuses = DashboardRevenuePolicy.ReturnedStatuses;
        var linesByOrder = input.Lines.GroupBy(x => x.OrderId).ToDictionary(x => x.Key, x => x.ToList());
        var claimsByOrder = input.Claims.Where(x => x.Status is not (ReturnClaimStatus.Rejected or ReturnClaimStatus.Cancelled))
            .GroupBy(x => x.OrderId).ToDictionary(x => x.Key, x => x.Count());
        var orders = new List<OrderFact>();
        var cancelled = new List<CancelledFact>();
        foreach (var order in input.Orders)
        {
            var platform = input.Channels.TryGetValue(order.ConnectionId, out var channel) ? channel.PlatformCode : "?";
            var day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(order.RealAt, input.Timezone).DateTime);
            var rows = linesByOrder.GetValueOrDefault(order.Id) ?? [];
            if (cancelledStatuses.Contains(order.DerivedStatus))
            {
                var reason = rows.Select(line => input.LineExtras.GetValueOrDefault(line.Id)?.CancelReason).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
                cancelled.Add(new(order, platform, day, rows.Sum(x => x.OrderedQuantity), reason));
                continue;
            }
            var returnedOrder = returnedStatuses.Contains(order.DerivedStatus);
            var currency = string.IsNullOrWhiteSpace(order.Currency) ? Try : order.Currency.Trim().ToUpperInvariant();
            var isTry = currency == Try;
            var linesBack = order.LineValue > 0 ? order.Amount * order.LineBack / order.LineValue : 0m;
            var returnedAmount = order.PackagesBack > linesBack ? order.PackagesBack : linesBack;
            var kept = returnedOrder ? 0m : Math.Max(0m, order.Amount - returnedAmount);
            var back = returnedOrder ? order.Amount : Math.Min(returnedAmount, order.Amount);
            // A line's money is its share of what the customer paid for the order.
            var scale = order.LineValue > 0 ? order.Amount / order.LineValue : 0m;
            var lines = new List<LineFact>();
            foreach (var line in rows)
            {
                var active = Math.Max(0m, line.OrderedQuantity - line.CancelledQuantity);
                var lineBack = returnedOrder ? active : Math.Min(active, Math.Max(line.ReturnedQuantity, line.AcceptedReturnQuantity));
                var paidUnit = isTry ? scale * line.UnitPrice : 0m;
                var keptUnits = active - lineBack;
                var extra = input.LineExtras.GetValueOrDefault(line.Id);
                var commission = isTry && extra is not null
                    ? ReportBuckets.EstimatedCommission(platform, extra.CommissionType, extra.Commission, extra.CommissionRate, extra.Komisyon, paidUnit * keptUnits, keptUnits, active)
                    : null;
                lines.Add(new(line, active, lineBack, paidUnit, commission, extra?.CancelReason));
            }
            var extras = input.OrderExtras.GetValueOrDefault(order.Id);
            var city = extras is null ? null : ReportBuckets.Province(extras.CityName, extras.City, extras.ShippingCity, extras.Province);
            var district = extras is null || city is null ? null : ReportBuckets.District(extras.DistrictName, extras.District, extras.ShippingDistrict, extras.Town, extras.ShippingTown);
            orders.Add(new(order, platform, currency, day, city, district, extras?.CustomerKey, isTry ? order.Amount : 0m, isTry ? back : 0m, isTry ? kept : 0m, lines,
                claimsByOrder.GetValueOrDefault(order.Id)));
        }
        return (orders, cancelled);
    }

    private static ReportSummaryView Summary(IReadOnlyList<OrderFact> orders, IReadOnlyList<CancelledFact> cancelled)
    {
        var sold = Sold(orders);
        var back = Back(orders);
        var priced = orders.Count(x => x.Amount > 0);
        var gross = orders.Sum(x => x.Amount);
        return new(orders.Count, cancelled.Count, Rate(cancelled.Count, orders.Count + cancelled.Count), sold, back, sold - back, orders.Sum(x => x.Claims), Rate(back, sold),
            priced, gross, orders.Sum(x => x.Back), orders.Sum(x => x.Kept), priced > 0 ? Math.Round(gross / priced, 2) : null,
            orders.Count(x => x.City is null), orders.Count(x => x.Amount <= 0 && x.Currency == Try), orders.Count(x => x.Currency != Try));
    }

    private static ReportCommissionView CommissionView(IReadOnlyList<OrderFact> orders, decimal netRevenue)
    {
        var lines = orders.SelectMany(x => x.Lines).Where(x => x.Commission is not null).ToList();
        var covered = Math.Round(lines.Sum(x => x.KeptAmount), 2);
        var commission = Math.Round(lines.Sum(x => x.Commission!.Value), 2);
        return new(covered, netRevenue > 0 ? Math.Round(Math.Min(1m, covered / netRevenue), 4) : null, commission, covered - commission, covered > 0 ? Math.Round(commission / covered, 4) : null);
    }

    private static ReportCostView CostView(IReadOnlyList<OrderFact> orders)
    {
        decimal kept = 0m, costed = 0m, costTotal = 0m, costedRevenue = 0m, profitUnits = 0m, profitRevenue = 0m, profitCost = 0m, profitCommission = 0m;
        var variants = new HashSet<Guid>();
        foreach (var line in orders.Where(x => x.Currency == Try).SelectMany(x => x.Lines))
        {
            if (line.KeptUnits <= 0) continue;
            kept += line.KeptUnits;
            if (line.Row.CostPrice is not > 0) continue;
            var lineCost = line.Row.CostPrice.Value * line.KeptUnits;
            costed += line.KeptUnits;
            costTotal += lineCost;
            costedRevenue += line.KeptAmount;
            if (line.Row.VariantId is { } variantId) variants.Add(variantId);
            if (line.Commission is not { } commission) continue;
            profitUnits += line.KeptUnits;
            profitRevenue += line.KeptAmount;
            profitCost += lineCost;
            profitCommission += commission;
        }
        var grossProfit = costedRevenue - costTotal;
        var netProfit = profitRevenue - profitCost - profitCommission;
        return new(kept, costed, kept > 0 ? Math.Round(costed / kept, 4) : null, Math.Round(costTotal, 2), Math.Round(costedRevenue, 2), Math.Round(grossProfit, 2),
            costedRevenue > 0 ? Math.Round(grossProfit / costedRevenue, 4) : null,
            profitUnits, kept > 0 ? Math.Round(profitUnits / kept, 4) : null, Math.Round(profitRevenue, 2), Math.Round(profitCost, 2), Math.Round(profitCommission, 2),
            Math.Round(netProfit, 2), profitRevenue > 0 ? Math.Round(netProfit / profitRevenue, 4) : null, variants.Count);
    }

    // Order-level buckets (platform, store, city, district): every order falls in exactly one, so they add up.
    private static List<ReportGroupView> Groups(IEnumerable<OrderFact> orders, Func<OrderFact, string> key, Func<OrderFact, string> label, Func<OrderFact, bool> isTest,
        Func<IEnumerable<OrderFact>, List<ReportGroupView>>? children, Func<IEnumerable<OrderFact>, List<ReportGroupView>>? split) =>
        orders.GroupBy(key, StringComparer.Ordinal)
            .Select(group =>
            {
                var rows = group.ToList();
                var name = label(rows[0]);
                var lines = rows.SelectMany(x => x.Lines).ToList();
                return new ReportGroupView(group.Key, name, name != Unknown, isTest(rows[0]), rows.Count, Sold(rows), Back(rows), rows.Sum(x => x.Claims), Rate(Back(rows), Sold(rows)),
                    rows.Count(x => x.Amount > 0), rows.Sum(x => x.Amount), rows.Sum(x => x.Kept),
                    Math.Round(lines.Sum(x => x.Commission ?? 0m), 2), Math.Round(lines.Where(x => x.Commission is not null).Sum(x => x.KeptAmount), 2),
                    children?.Invoke(rows) ?? [], split?.Invoke(rows) ?? []);
            })
            .OrderBy(x => x.Known ? 0 : 1).ThenByDescending(x => x.SoldQuantity).ThenBy(x => x.Label, StringComparer.Ordinal)
            .ToList();

    // Line-level buckets (brand, category): an order with lines of two brands counts once in each.
    private static List<ReportGroupView> LineGroups(IReadOnlyList<(OrderFact Order, LineFact Line)> lines, Func<LineFact, string?> name) =>
        lines.GroupBy(x => name(x.Line) ?? Unknown, StringComparer.Ordinal)
            .Select(group =>
            {
                var rows = group.ToList();
                var sold = rows.Sum(x => x.Line.Active);
                var back = rows.Sum(x => x.Line.Back);
                return new ReportGroupView(group.Key, group.Key, group.Key != Unknown, false, rows.Select(x => x.Order.Order.Id).Distinct().Count(), sold, back,
                    rows.Select(x => x.Order).Distinct().Sum(x => x.Claims), Rate(back, sold),
                    rows.Select(x => x.Order).Distinct().Count(x => x.Amount > 0),
                    Math.Round(rows.Sum(x => x.Line.PaidUnitPrice * x.Line.Active), 2), Math.Round(rows.Sum(x => x.Line.KeptAmount), 2),
                    Math.Round(rows.Sum(x => x.Line.Commission ?? 0m), 2), Math.Round(rows.Where(x => x.Line.Commission is not null).Sum(x => x.Line.KeptAmount), 2), [], []);
            })
            .OrderBy(x => x.Known ? 0 : 1).ThenByDescending(x => x.SoldQuantity).ThenBy(x => x.Label, StringComparer.Ordinal)
            .ToList();

    internal static List<ReportProductView> Products(IReadOnlyList<OrderFact> orders, ReportCatalogNames names) =>
        orders.SelectMany(order => order.Lines)
            .GroupBy(line => line.Row.VariantId is { } variantId ? variantId.ToString("N") : $"sku:{line.Row.Sku}", StringComparer.Ordinal)
            .Select(group =>
            {
                var first = group.First().Row;
                var sold = group.Sum(x => x.Active);
                var back = group.Sum(x => x.Back);
                var title = group.Select(x => x.Row.Title).Where(x => !string.IsNullOrWhiteSpace(x)).GroupBy(x => x).OrderByDescending(x => x.Count()).Select(x => x.Key).FirstOrDefault() ?? first.Sku;
                return new ReportProductView(first.VariantId, first.ProductId, title, first.Sku,
                    first.BrandId is { } brandId && names.Brands.TryGetValue(brandId, out var brand) ? brand : Unknown,
                    first.CategoryId is { } categoryId && names.Categories.TryGetValue(categoryId, out var category) ? category : Unknown,
                    sold, back, Rate(back, sold), Math.Round(group.Sum(x => x.PaidUnitPrice * x.Active), 2),
                    Math.Round(group.Sum(x => x.Commission ?? 0m), 2), Math.Round(group.Where(x => x.Commission is not null).Sum(x => x.KeptAmount), 2));
            })
            .ToList();

    private static List<ReportTimePointView> Series(IReadOnlyList<OrderFact> orders, IReadOnlyList<CancelledFact> cancelled, ReportPeriod period, string granularity)
    {
        var byBucket = orders.GroupBy(x => ReportBuckets.Start(x.Day, granularity)).ToDictionary(x => x.Key, x => x.ToList());
        var cancelledByBucket = cancelled.GroupBy(x => ReportBuckets.Start(x.Day, granularity)).ToDictionary(x => x.Key, x => x.Count());
        return ReportBuckets.Starts(period.From, period.To, granularity)
            .Select(start => byBucket.TryGetValue(start, out var rows)
                ? new ReportTimePointView(start, rows.Count, Sold(rows), Back(rows), rows.Sum(x => x.Kept), cancelledByBucket.GetValueOrDefault(start))
                : new ReportTimePointView(start, 0, 0m, 0m, 0m, cancelledByBucket.GetValueOrDefault(start)))
            .ToList();
    }

    private static ReportCancellationView Cancellations(IReadOnlyList<OrderFact> orders, IReadOnlyList<CancelledFact> cancelled, ReportInput input)
    {
        decimal PartlyCancelled(IEnumerable<OrderFact> rows) => rows.SelectMany(x => x.Lines).Sum(x => Math.Max(0m, x.Row.CancelledQuantity));
        var platforms = orders.Select(x => x.PlatformCode).Concat(cancelled.Select(x => x.PlatformCode)).Distinct(StringComparer.Ordinal)
            .Select(code =>
            {
                var live = orders.Where(x => x.PlatformCode == code).ToList();
                var gone = cancelled.Where(x => x.PlatformCode == code).ToList();
                return new ReportCancelGroupView(code, code, live.Count + gone.Count, gone.Count, gone.Sum(x => x.Quantity) + PartlyCancelled(live), Rate(gone.Count, live.Count + gone.Count));
            })
            .OrderByDescending(x => x.CancelledOrders).ThenBy(x => x.Label, StringComparer.Ordinal)
            .ToList();
        var reasons = Reasons(cancelled.Select(x => Label(x.Reason, null))
            .Concat(orders.SelectMany(x => x.Lines).Where(x => x.Row.CancelledQuantity > 0).Select(x => Label(x.CancelReason, null))));
        return new(cancelled.Count, cancelled.Sum(x => x.Quantity) + PartlyCancelled(orders), Rate(cancelled.Count, orders.Count + cancelled.Count), CancelWarningRate, platforms, reasons);
    }

    // Durations come from the stored status times; both ends of a duration are stored the same way for a marketplace,
    // so the store's clock offset cancels out. A duration outside 0-60 days is taken for a bad record and left out.
    private static List<ReportCargoView> Cargo(IReadOnlyList<OrderFact> orders, IReadOnlyList<ReportPackageRow> packages)
    {
        var byOrder = orders.ToDictionary(x => x.Order.Id);
        var rows = packages.Where(x => x.Status != ShipmentPackageStatus.Cancelled && byOrder.ContainsKey(x.OrderId)).ToList();
        var total = rows.Count;
        static double? Hours(DateTimeOffset? from, DateTimeOffset? to) =>
            from is { } start && to is { } end && end >= start && end - start <= TimeSpan.FromDays(60) ? (end - start).TotalHours : null;
        return rows.GroupBy(x => string.IsNullOrWhiteSpace(x.CargoProvider) ? Unknown : x.CargoProvider.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var ship = group.Select(x => Hours(byOrder[x.OrderId].Order.OrderedAt, x.ShippedAt)).Where(x => x is not null).Select(x => x!.Value).ToList();
                var deliver = group.Select(x => Hours(x.ShippedAt, x.DeliveredAt)).Where(x => x is not null).Select(x => x!.Value).ToList();
                var late = group.Where(x => x.ShippedAt is not null && byOrder[x.OrderId].Order.ShipmentDueAt is not null).ToList();
                var lateCount = late.Count(x => x.ShippedAt > byOrder[x.OrderId].Order.ShipmentDueAt);
                return new ReportCargoView(group.Key, group.Key != Unknown, group.Count(), total > 0 ? Math.Round((decimal)group.Count() / total, 4) : null,
                    ship.Count > 0 ? Math.Round((decimal)ship.Average(), 1) : null, ship.Count,
                    deliver.Count > 0 ? Math.Round((decimal)deliver.Average(), 1) : null, deliver.Count,
                    lateCount, late.Count, late.Count > 0 ? Math.Round((decimal)lateCount / late.Count, 4) : null);
            })
            .OrderBy(x => x.Known ? 0 : 1).ThenByDescending(x => x.Packages).ThenBy(x => x.Label, StringComparer.Ordinal)
            .ToList();
    }

    // Orders by weekday (Monday first) and hour of the tenant's local order time.
    private static List<IReadOnlyList<int>> Heatmap(IReadOnlyList<OrderFact> orders, TimeZoneInfo timezone)
    {
        var grid = Enumerable.Range(0, 7).Select(_ => new int[24]).ToArray();
        foreach (var order in orders)
        {
            var local = TimeZoneInfo.ConvertTime(order.Order.RealAt, timezone);
            grid[((int)local.DayOfWeek + 6) % 7][local.Hour]++;
        }
        return grid.Select(row => (IReadOnlyList<int>)row).ToList();
    }

    private static ReportCustomerView Customers(IReadOnlyList<OrderFact> orders)
    {
        var identified = orders.Where(x => !string.IsNullOrEmpty(x.CustomerKey)).ToList();
        var byCustomer = identified.GroupBy(x => x.CustomerKey!, StringComparer.Ordinal).Select(x => x.Count()).ToList();
        var repeat = byCustomer.Count(x => x > 1);
        return new(identified.Count, orders.Count - identified.Count, byCustomer.Count, repeat,
            byCustomer.Count > 0 ? Math.Round((decimal)repeat / byCustomer.Count, 4) : null,
            byCustomer.Count > 0 ? Math.Round((decimal)identified.Count / byCustomer.Count, 2) : null);
    }

    // A status set by hand decides; then an issued panel invoice; then the marketplace's own word; else unknown.
    internal static string InvoiceBucket(ReportPackageRow package) =>
        package.ManualInvoiceStatus?.Trim().ToUpperInvariant() switch
        {
            "UPLOADED" => "INVOICED",
            "PENDING" => "NOT_INVOICED",
            _ => package.PanelInvoiced || package.MarketplaceInvoiceStatus is MarketplaceInvoiceStatus.Invoiced or MarketplaceInvoiceStatus.Received ? "INVOICED"
                : package.MarketplaceInvoiceStatus is MarketplaceInvoiceStatus.NotInvoiced or MarketplaceInvoiceStatus.Rejected ? "NOT_INVOICED"
                : "UNKNOWN"
        };

    private static ReportInvoiceView Invoices(IReadOnlyList<OrderFact> orders, IReadOnlyList<ReportPackageRow> packages)
    {
        var byOrder = orders.ToDictionary(x => x.Order.Id);
        var rows = packages.Where(x => x.Status != ShipmentPackageStatus.Cancelled && byOrder.ContainsKey(x.OrderId))
            .Select(x => (Package: x, Order: byOrder[x.OrderId], Bucket: InvoiceBucket(x))).ToList();
        var labels = new[] { ("INVOICED", "Faturalı"), ("NOT_INVOICED", "Faturasız"), ("UNKNOWN", Unknown) };
        var buckets = labels.Select(item => new ReportInvoiceBucketView(item.Item1, item.Item2, rows.Count(x => x.Bucket == item.Item1),
            Math.Round(rows.Where(x => x.Bucket == item.Item1 && x.Order.Currency == Try).Sum(x => x.Package.Amount), 2))).ToList();
        var platforms = rows.GroupBy(x => x.Order.PlatformCode, StringComparer.Ordinal)
            .Select(group => new ReportInvoicePlatformView(group.Key, group.Count(x => x.Bucket == "INVOICED"), group.Count(x => x.Bucket == "NOT_INVOICED"), group.Count(x => x.Bucket == "UNKNOWN")))
            .OrderByDescending(x => x.Invoiced + x.NotInvoiced + x.Unknown).ToList();
        return new(buckets, platforms);
    }

    private static ReportStockView StockView(IReadOnlyList<ReportProductView> products, IReadOnlyDictionary<Guid, decimal> stock, int days)
    {
        ReportStockRowView Row(ReportProductView product)
        {
            var kept = product.SoldQuantity - product.ReturnedQuantity;
            var daily = days > 0 ? Math.Round(kept / days, 2) : (decimal?)null;
            decimal? available = product.VariantId is { } variantId && stock.TryGetValue(variantId, out var value) ? value : null;
            return new(product.VariantId, product.Title, product.Sku, kept, available, daily,
                available is { } onHand && daily is > 0 ? Math.Round(Math.Max(0m, onHand) / daily.Value, 1) : null);
        }
        var sellers = products.Where(x => x.VariantId != null && x.SoldQuantity - x.ReturnedQuantity > 0)
            .OrderByDescending(x => x.SoldQuantity - x.ReturnedQuantity).ThenBy(x => x.Title, StringComparer.Ordinal).ToList();
        return new(days, sellers.Take(TopCount).Select(Row).ToList(),
            sellers.Select(Row).Where(x => x.Available is <= 0).Take(TopCount * 2).ToList());
    }

    private static List<ReportReasonView> Reasons(IEnumerable<string> labels) =>
        labels.GroupBy(x => x, StringComparer.Ordinal)
            .Select(group => new ReportReasonView(group.Key, group.Key != Unknown, group.Count()))
            .OrderBy(x => x.Known ? 0 : 1).ThenByDescending(x => x.Count).ThenBy(x => x.Reason, StringComparer.Ordinal)
            .ToList();

    // The same reason under one name: the marketplace's text when it gave one, else its code, else "Bilinmiyor".
    internal static string Label(string? text, string? code)
    {
        var label = !string.IsNullOrWhiteSpace(text) ? text.Trim() : !string.IsNullOrWhiteSpace(code) ? code.Trim() : Unknown;
        return label.Length > 120 ? label[..120] : label;
    }

    private static string Fold(string? district) => district is null ? Unknown : district.ToUpper(CultureInfo.GetCultureInfo("tr-TR"));

    private static List<string> Notes(ReportSummaryView summary, ReportCommissionView commission, ReportCostView cost, IReadOnlyList<OrderFact> orders,
        IReadOnlyList<CancelledFact> cancelled, ReportInput input)
    {
        var tr = CultureInfo.GetCultureInfo("tr-TR");
        string Count(int value) => value.ToString("N0", tr);
        string Percent(decimal? value) => $"%{(value ?? 0m) * 100:N1}";
        var notes = new List<string>
        {
            "Satış tutarı müşterinin ödediği tutardır (iptal edilen paketler hariç); net ciro iade edilenler düşülerek Genel Bakış ile aynı kuralla hesaplanır.",
            "İadeler ve iptaller siparişin verildiği döneme yazılır; iade talebi sayısına reddedilen ve iptal edilen talepler girmez."
        };
        if (summary.OrdersWithoutAmount > 0)
        {
            var platforms = orders.Where(x => x.Amount <= 0 && x.Currency == Try).GroupBy(x => x.PlatformCode).OrderByDescending(x => x.Count())
                .Select(x => $"{x.Key} {Count(x.Count())}");
            notes.Add($"{Count(summary.OrdersWithoutAmount)} siparişte tutar yok ({string.Join(", ", platforms)}); adetlerde sayılır, tutarlarda 0 ₺ değil \"tutar yok\" olarak okunmalıdır."
                + (orders.Any(x => x.PlatformCode == "SHOPIFY" && x.Amount <= 0) ? " Shopify siparişlerinde tutar gelmiyor." : ""));
        }
        if (summary.OrdersWithoutCity > 0)
        {
            var platforms = orders.Where(x => x.City is null).GroupBy(x => x.PlatformCode).OrderByDescending(x => x.Count())
                .Select(x => $"{x.Key} {Count(x.Count())}");
            notes.Add($"{Count(summary.OrdersWithoutCity)} siparişin adresinde il okunamadı ({string.Join(", ", platforms)}); şehir tablosunda \"Bilinmiyor\" altında.");
        }
        if (summary.OtherCurrencyOrders > 0)
            notes.Add($"{Count(summary.OtherCurrencyOrders)} sipariş TRY dışında bir para biriminde; tutarlara girmez, yalnız adetlerde sayılır.");
        notes.Add(commission.CoveredRevenue <= 0
            ? "Komisyon bilgisi olan satış yok; tahmini komisyon ve hakediş hesaplanamaz."
            : $"Komisyon ve hakediş tahminidir: siparişte gelen komisyon oranı ya da tutarı üzerinden, iade edilmeyen adetler için hesaplanır; kesinleşmiş hakediş değildir. Komisyon bilgisi net cironun {Percent(commission.Coverage)} kadarında var; Pazarama ve Shopify komisyon göndermez, n11 oranları tutarsız olabilir.");
        if (cost.KeptQuantity > 0)
            notes.Add(cost.CostedQuantity <= 0
                ? "Satılan ürünlerin hiçbirinde maliyet girilmemiş; kâr hesaplanamaz."
                : $"Maliyet yalnız {cost.CostedQuantity.ToString("N0", tr)} / {cost.KeptQuantity.ToString("N0", tr)} adetlik satışta girilmiş ({Percent(cost.Coverage)}); kâr yalnız bu satışlar üzerinden hesaplanır.");
        notes.Add("Maliyet, ürün kartındaki güncel maliyettir (satış anındaki değil); kâra kargo ve KDV dahil değildir.");
        notes.Add("Kargo süreleri pazaryerinin bildirdiği durum zamanlarından hesaplanır (yaklaşık); zamanı olmayan paketler süre ortalamasına girmez.");
        notes.Add("Müşteri sayısı mağazanın müşteri numarası, e-posta ya da telefondan üretilen tek yönlü bir anahtarla sayılır; aynı kişi iki pazaryerinde iki müşteri sayılabilir.");
        if (cancelled.Count > 0)
            notes.Add($"{Count(cancelled.Count)} iptal edilen sipariş satışa ve adetlere girmez; İptaller sekmesinde gösterilir.");
        if (input.IncludesTest) notes.Add("Test ortamı mağazalarının siparişleri dahil.");
        return notes;
    }

    private static decimal Sold(IEnumerable<OrderFact> rows) => rows.Sum(x => x.Lines.Sum(line => line.Active));
    private static decimal Back(IEnumerable<OrderFact> rows) => rows.Sum(x => x.Lines.Sum(line => line.Back));
    private static decimal? Rate(decimal part, decimal whole) => whole > 0 ? Math.Round(part / whole, 4) : null;

    internal sealed record LineFact(ReportLineRow Row, decimal Active, decimal Back, decimal PaidUnitPrice, decimal? Commission, string? CancelReason)
    {
        public decimal KeptUnits => Active - Back;
        public decimal KeptAmount => PaidUnitPrice * KeptUnits;
    }

    internal sealed record OrderFact(ReportOrderRow Order, string PlatformCode, string Currency, DateOnly Day, string? City, string? District, string? CustomerKey,
        decimal Amount, decimal Back, decimal Kept, List<LineFact> Lines, int Claims);

    internal sealed record CancelledFact(ReportOrderRow Order, string PlatformCode, DateOnly Day, decimal Quantity, string? Reason);
}
