namespace MarketplaceHub.Application;

// The sales report (Raporlar page). Orders are taken by their real order time in the tenant's time zone, with the same
// rules as the overview's revenue (DashboardReadService): cancelled orders are not sales, a returned order or the
// returned part of one is taken off. Quantities count every non-cancelled order, also one whose amount is unknown.
// Amounts are TRY (₺); an order in another currency is counted in units only and named in DataNotes.
// Whatever cannot be told (city, district, brand, category, cargo company, reason, commission) goes to a
// "Bilinmiyor" bucket, so every breakdown adds up to the totals.
public sealed record ReportSalesQuery(
    DateOnly? From,
    DateOnly? To,
    IReadOnlyList<string>? PlatformCodes,
    IReadOnlyList<Guid>? ConnectionIds,
    string? Granularity,
    bool IncludeTest);

public sealed record ReportSalesView(
    DateOnly From,
    DateOnly To,
    DateOnly PreviousFrom,
    DateOnly PreviousTo,
    string Granularity,
    bool IncludesTest,
    DateTimeOffset GeneratedAt,
    ReportSummaryView Summary,
    ReportSummaryView PreviousSummary,
    ReportCommissionView Commission,
    ReportCostView Cost,
    IReadOnlyList<ReportGroupView> Platforms,
    IReadOnlyList<ReportGroupView> Cities,
    IReadOnlyList<ReportTimePointView> TimeSeries,
    IReadOnlyList<ReportTimePointView> PreviousTimeSeries,
    IReadOnlyList<ReportProductView> TopProducts,
    IReadOnlyList<ReportProductView> TopReturned,
    IReadOnlyList<ReportGroupView> Brands,
    IReadOnlyList<ReportGroupView> Categories,
    IReadOnlyList<ReportReasonView> ReturnReasons,
    ReportCancellationView Cancellations,
    IReadOnlyList<ReportCargoView> Cargo,
    IReadOnlyList<IReadOnlyList<int>> Heatmap,
    ReportCustomerView Customers,
    ReportInvoiceView Invoices,
    ReportStockView Stock,
    IReadOnlyList<ReportChannelView> Channels,
    IReadOnlyList<string> DataNotes);

// Orders: non-cancelled orders. CancelRate: cancelled / (orders + cancelled). SoldQuantity: ordered minus cancelled
// units of non-cancelled orders. ReturnedQuantity: units sent back (a returned order whole; otherwise the larger of the
// line's returned quantity and its accepted return claims). ReturnClaims: claims that were not rejected or cancelled.
// GrossSales: what the customers paid (cancelled packages left out); NetRevenue: GrossSales less what went back (the
// overview's revenue). AverageOrderValue: GrossSales / orders with an amount.
public sealed record ReportSummaryView(
    int Orders,
    int CancelledOrders,
    decimal? CancelRate,
    decimal SoldQuantity,
    decimal ReturnedQuantity,
    decimal KeptQuantity,
    int ReturnClaims,
    decimal? ReturnRate,
    int OrdersWithAmount,
    decimal GrossSales,
    decimal ReturnedAmount,
    decimal NetRevenue,
    decimal? AverageOrderValue,
    int OrdersWithoutCity,
    int OrdersWithoutAmount,
    int OtherCurrencyOrders);

// Estimated from the commission the marketplace sent on each order line (Trendyol and n11 rate, Hepsiburada amount,
// PTT AVM rate), over the kept units. CoveredRevenue: the net revenue of lines that carry one; Coverage: its share of
// NetRevenue. EstimatedPayout = CoveredRevenue - EstimatedCommission: an estimate, never the settled payment.
public sealed record ReportCommissionView(decimal CoveredRevenue, decimal? Coverage, decimal EstimatedCommission, decimal EstimatedPayout, decimal? EffectiveRate);

// Cost only over kept units whose variant has a cost price (the current one, not the one at the sale). CostedRevenue
// is those units' share of what the customer paid; GrossProfit = CostedRevenue - CostTotal. NetProfit is over units
// that carry both a cost and a commission: their revenue - cost - estimated commission (before cargo and VAT).
public sealed record ReportCostView(
    decimal KeptQuantity,
    decimal CostedQuantity,
    decimal? Coverage,
    decimal CostTotal,
    decimal CostedRevenue,
    decimal GrossProfit,
    decimal? GrossMargin,
    decimal ProfitQuantity,
    decimal? ProfitCoverage,
    decimal ProfitRevenue,
    decimal ProfitCost,
    decimal ProfitCommission,
    decimal NetProfit,
    decimal? NetMargin,
    int CostedVariants);

// One bucket of a breakdown (platform, store, city, district, brand, category). Known is false for "Bilinmiyor".
// Children: the next level (a platform's stores, a city's districts); Split: the same bucket by platform.
public sealed record ReportGroupView(
    string Key,
    string Label,
    bool Known,
    bool IsTest,
    int Orders,
    decimal SoldQuantity,
    decimal ReturnedQuantity,
    int ReturnClaims,
    decimal? ReturnRate,
    int OrdersWithAmount,
    decimal GrossSales,
    decimal NetRevenue,
    decimal EstimatedCommission,
    decimal CommissionCoveredRevenue,
    IReadOnlyList<ReportGroupView> Children,
    IReadOnlyList<ReportGroupView> Split);

// Start: the first day of the bucket (a day, a Monday-started week or a month), in the tenant's time zone.
public sealed record ReportTimePointView(DateOnly Start, int Orders, decimal SoldQuantity, decimal ReturnedQuantity, decimal NetRevenue, int CancelledOrders);

// SalesAmount: the line's share of what the customer paid for the order.
public sealed record ReportProductView(
    Guid? VariantId,
    Guid? ProductId,
    string Title,
    string Sku,
    string Brand,
    string Category,
    decimal SoldQuantity,
    decimal ReturnedQuantity,
    decimal? ReturnRate,
    decimal SalesAmount,
    decimal EstimatedCommission,
    decimal CommissionCoveredAmount);

public sealed record ReportReasonView(string Reason, bool Known, int Count);

public sealed record ReportCancelGroupView(string Key, string Label, int Orders, int CancelledOrders, decimal CancelledQuantity, decimal? CancelRate);

// CancelledQuantity: units of cancelled orders plus units cancelled out of orders that went on. Reasons: one per
// cancelled order and per partly cancelled line, by the reason the marketplace gave (or "Bilinmiyor").
public sealed record ReportCancellationView(
    int CancelledOrders,
    decimal CancelledQuantity,
    decimal? CancelRate,
    decimal WarningRate,
    IReadOnlyList<ReportCancelGroupView> Platforms,
    IReadOnlyList<ReportReasonView> Reasons);

// Packages: current, not cancelled packages of the period's orders. The hours come from the status times the
// marketplace reported (order -> first "shipped", "shipped" -> first "delivered"); Measured* say how many packages
// had both times. A late shipment left after the order's shipping deadline.
public sealed record ReportCargoView(
    string Label,
    bool Known,
    int Packages,
    decimal? Share,
    decimal? AverageHoursToShip,
    int MeasuredShip,
    decimal? AverageHoursToDeliver,
    int MeasuredDeliver,
    int LateShipments,
    int MeasuredLate,
    decimal? LateRate);

// Customers by a one-way key built in the database (the marketplace's customer id, else e-mail, else phone); no
// personal data leaves it. A store's customer id is its own, so one person on two marketplaces counts twice.
public sealed record ReportCustomerView(int IdentifiedOrders, int UnidentifiedOrders, int UniqueCustomers, int RepeatCustomers, decimal? RepeatRate, decimal? OrdersPerCustomer);

public sealed record ReportInvoiceBucketView(string Key, string Label, int Packages, decimal Amount);

public sealed record ReportInvoicePlatformView(string PlatformCode, int Invoiced, int NotInvoiced, int Unknown);

// Packages of the period by invoice evidence: a panel invoice that was issued, a status set by hand, or the
// marketplace's own word; none of them is "Bilinmiyor".
public sealed record ReportInvoiceView(IReadOnlyList<ReportInvoiceBucketView> Buckets, IReadOnlyList<ReportInvoicePlatformView> Platforms);

// Available: the MAIN warehouse's available stock now (null: the variant has no stock record). DailyAverage: kept
// units / days of the period; DaysLeft: Available / DailyAverage.
public sealed record ReportStockRowView(Guid? VariantId, string Title, string Sku, decimal SoldQuantity, decimal? Available, decimal? DailyAverage, decimal? DaysLeft);

public sealed record ReportStockView(int PeriodDays, IReadOnlyList<ReportStockRowView> BestSellers, IReadOnlyList<ReportStockRowView> SoldOut);

public sealed record ReportChannelView(Guid Id, string PlatformCode, string DisplayName, bool IsTest);

public interface IReportsService
{
    Task<ServiceResult<ReportSalesView>> SalesAsync(Guid tenantId, ReportSalesQuery query, CancellationToken cancellationToken);
}
