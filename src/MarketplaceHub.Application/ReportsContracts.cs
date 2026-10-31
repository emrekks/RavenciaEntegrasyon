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
    IReadOnlyList<string> DataNotes,
    ReportCostsView Costs,
    ReportGeographyView Geography,
    IReadOnlyList<ReportPlatformHeatmapView>? PlatformHeatmaps = null,
    IReadOnlyList<ReportProductView>? Rising = null,
    IReadOnlyList<ReportProductView>? Falling = null,
    ReportPreviousAccountView? PreviousAccount = null);

// The previous period's commission, product cost and Trendyol statement, read the same way as the period's, so the
// overview can compare its profit, margin and profit over cost with the period before.
public sealed record ReportPreviousAccountView(ReportCommissionView Commission, ReportCostView Cost, ReportCostsView Costs);

// One platform's orders by weekday (Monday first) and hour, as Heatmap; summed over platforms they make Heatmap.
// Rising and Falling (on ReportSalesView): the products whose units sold rose or fell most against the previous period.
public sealed record ReportPlatformHeatmapView(string PlatformCode, IReadOnlyList<IReadOnlyList<int>> Heatmap);

// The sales map. Cities holds the orders delivered in Turkey only; Countries splits every order by the country it goes
// to: an order of a Trendyol cross-border storefront (SA, AZ, BG…) by its storefront, since its address is Trendyol's
// export depot in Istanbul, else by the address's country (Turkey when none is given). A Trendyol order built from a
// return claim whose storefront no read has named goes to "Bilinmiyor" (Known false): neither Turkey nor abroad. The
// previous period's cities and countries give each its change; AbroadOrders: the orders left out of Cities for going abroad.
public sealed record ReportGeographyView(
    IReadOnlyList<ReportGroupView> Countries,
    IReadOnlyList<ReportGroupView> PreviousCities,
    IReadOnlyList<ReportGroupView> PreviousCountries,
    int AbroadOrders);

// Orders: non-cancelled orders. CancelRate: cancelled / (orders + cancelled). SoldQuantity: ordered minus cancelled
// units of non-cancelled orders. ReturnedQuantity: units sent back (a returned order whole; otherwise the larger of the
// line's returned quantity and its accepted return claims). ReturnClaims: claims that were not rejected or cancelled.
// GrossSales: what the customers paid (cancelled packages left out); NetRevenue: GrossSales less what went back (the
// overview's revenue). AverageOrderValue: GrossSales / orders with an amount. ListAmount: the same orders at their list
// price, before the marketplace's discounts; DiscountAmount = ListAmount - GrossSales.
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
    int OtherCurrencyOrders,
    decimal ListAmount,
    decimal DiscountAmount,
    // The returned units at their list price, before the marketplace's discounts, as Trendyol's sales report shows returns
    // (1-14 Oct 2026: 20 units, 14,435.58 at list price, 13,994.18 paid). ReturnedAmount stays what was paid: it is taken
    // off GrossSales for NetRevenue.
    decimal ReturnedListAmount = 0m);

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

// A variant's option as the catalogue names it, e.g. Beden: M, Renk: Siyah.
public sealed record ReportProductOptionView(string Name, string Value);

// NetAmount: SalesAmount less what went back. PreviousSoldQuantity: the units sold in the previous period (0 when none);
// Available: the variant's stock now, for the variants whose stock was read (null otherwise).
// SalesAmount: the line's share of what the customer paid for the order. ModelCode, ImageUrl and Options come from the
// catalogue for a line matched to a variant (the picture: the variant's own, else the product's); null or empty otherwise.
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
    decimal CommissionCoveredAmount,
    string? ModelCode = null,
    string? ImageUrl = null,
    IReadOnlyList<ReportProductOptionView>? Options = null,
    decimal NetAmount = 0m,
    decimal? PreviousSoldQuantity = null,
    decimal? Available = null);

public sealed record ReportReasonView(string Reason, bool Known, int Count);

public sealed record ReportCancelGroupView(string Key, string Label, int Orders, int CancelledOrders, decimal CancelledQuantity, decimal? CancelRate);

// CancelledQuantity: units of cancelled orders plus units cancelled out of orders that went on. Reasons: one per
// cancelled order and per partly cancelled line, by the reason the marketplace gave (or "Bilinmiyor"). CancelledAmount: the
// list price of those units (TRY orders only).
public sealed record ReportCancellationView(
    int CancelledOrders,
    decimal CancelledQuantity,
    decimal? CancelRate,
    decimal WarningRate,
    IReadOnlyList<ReportCancelGroupView> Platforms,
    IReadOnlyList<ReportReasonView> Reasons,
    decimal CancelledAmount = 0m);

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

// Real marketplace costs, read from the marketplace's own accounting (today Trendyol's current account statement of
// production stores), next to the estimates above, which keep their meaning. Source is "trendyol-finance" when the
// period has real records, else "estimate": then RealCostTotal and Commission are null and the lists are empty, never
// zeros. A record tied to an order counts in the period of the order; one without (service fee, withholding tax,
// penalty, a cargo invoice whose lines could not be read) by its own date. Amounts are what the seller was charged
// (debt - credit); a negative amount was paid back or credited (sales are negative).
// LastSyncedAt: the oldest last finished read among the report's Trendyol stores (null while one was never read);
// CoveredFrom/CoveredThrough: the dates every one of them has read. FinanceOrders: the period's Trendyol orders (not
// cancelled, TRY); SettledOrders: those with a settlement record (Trendyol writes them after delivery).
public sealed record ReportCostsView(
    string Source,
    DateTimeOffset? LastSyncedAt,
    DateTimeOffset? CoveredFrom,
    DateTimeOffset? CoveredThrough,
    int FinanceOrders,
    int SettledOrders,
    decimal? Coverage,
    decimal? RealCostTotal,
    ReportRealCommissionView? Commission,
    ReportCargoCostView? Cargo,
    IReadOnlyList<ReportCostLineView> Lines,
    IReadOnlyList<ReportCostChannelView> Channels,
    IReadOnlyList<ReportCostProductView> Products,
    ReportNetVatView? NetVat = null);

// The VAT the seller pays over the period's Trendyol orders, estimated: the VAT inside the domestic sales (a micro-export
// sale carries none) less the VAT inside the product cost (the entered cost taken as VAT-inclusive at the line's rate)
// and inside Trendyol's commission, cargo, platform service fee and advertising (20 %; the international service fee is
// VAT-free). Net is what is left to pay; below zero it is VAT carried forward.
public sealed record ReportNetVatView(decimal SalesVat, decimal ProductCostVat, decimal ExpenseVat, decimal Net);

// One category (SALE, RETURN, DISCOUNT, COUPON, COMMISSION, CARGO, SERVICE_FEE, STOPPAGE, PENALTY, EARLY_PAYMENT,
// ADVERTISING, OTHER). IsCost: added to RealCostTotal (OTHER is not: an unrecognised record may go either way).
// COMMISSION is the commission of the settlement lines plus the commission corrections (Trendyol's commission invoice
// bills that same commission and is not added again); CARGO the lines of the cargo invoices. OrderLinked: the part
// tied to an order of the period; Unlinked: the part counted by date.
// Estimated: the part of Amount estimated for orders Trendyol has not settled yet (service fees and withholding tax).
public sealed record ReportCostLineView(string Category, string Label, bool IsCost, decimal Amount, int Records, decimal OrderLinked, decimal Unlinked, decimal Estimated = 0m);

// The commission of the period's orders, real where Trendyol settled it: Real over the orders with settlement records
// (RealOrders), what the estimate said for those same orders, the estimate for every other order (all platforms), and
// Combined = Real + EstimatedForUnsettled.
public sealed record ReportRealCommissionView(decimal Real, int RealOrders, decimal EstimatedForSettled, decimal EstimatedForUnsettled, decimal Combined);

// The cargo of the period's Trendyol orders. Trendyol bills cargo weeks after shipping, so a recent order
// often has no cargo invoice line yet. Real: the cargo invoice lines counted (BilledOrders of the period have one);
// UnbilledOrders: the period's Trendyol orders without one, estimated at the store's cargo tariff: the amount most
// shipping lines of its last 30 days were billed at (AveragePerOrder; SampleOrders billed orders behind it). Estimated is null when no store has such a sample;
// Combined = Real + Estimated.
// PlatformOrders / PlatformEstimated: the orders of the platforms whose statement is not read (Hepsiburada, n11,
// Pazarama, PTT AVM, Shopify) and their cargo from the platforms' tariffs; part of Estimated and Combined.
// DesiSources: of those orders, how many had their parcel's desi from the marketplace's order (MARKETPLACE), the
// products' entered desi (MANUAL), the desi measured on billed Trendyol parcels (MEASURED) or a store-wide default
// (DEFAULT); MeasuredParcels: the billed parcels the measured desi was learned from.
public sealed record ReportCargoCostView(decimal Real, int BilledOrders, int UnbilledOrders, decimal? AveragePerOrder, int SampleOrders, decimal? Estimated, decimal Combined,
    int PlatformOrders = 0, decimal PlatformEstimated = 0m, ReportDesiSourcesView? DesiSources = null);

public sealed record ReportDesiSourcesView(int Marketplace, int Manual, int Measured, int Default, int MeasuredParcels);

// One store. RealCommission: its settled orders and its commission records tied to no order of the period;
// EstimatedCommission: its other orders; Amounts: its real costs by category (commission apart); EstimatedCargo: the
// cargo of its orders not billed yet. Source: "trendyol-finance" for a store with records, else "estimate".
public sealed record ReportCostChannelView(Guid ConnectionId, string PlatformCode, string DisplayName, string Source, decimal RealCommission, decimal EstimatedCommission,
    IReadOnlyList<ReportCostAmountView> Amounts, decimal EstimatedCargo, decimal Total);

public sealed record ReportCostAmountView(string Category, decimal Amount);

// Records that carry a barcode found on a line of their order (the settlement lines). Cargo and fees belong to the
// order or the store and are not shared out to products.
public sealed record ReportCostProductView(Guid? VariantId, string Title, string Sku, string Barcode, decimal Commission, decimal Discount, decimal Coupon, int Records);

// Sipariş Kârlılık Analizi: each order of the period with what it leaves. Sort: date (default), amount, profit,
// profitOnCost or margin; Search matches the order number or a line's title, barcode or stock code.
public sealed record ReportOrderProfitQuery(DateOnly? From, DateOnly? To, IReadOnlyList<string>? PlatformCodes, string? Search, string? Sort, bool Descending, int Page, int PageSize);

// One charge of an order: Estimated when it is not the marketplace's own figure yet, Source where it comes from. Amount
// null: unknown (a Trendyol cargo with no invoice and no tariff to estimate it at), and so the order's profit.
public sealed record ReportOrderProfitCostView(string Key, string Label, decimal? Amount, bool Estimated, string? Source);

// One line: the kept units and their paid amount, the commission rate and amount the marketplace sent (null: none), the
// variant's current cost and VAT rate.
public sealed record ReportOrderProfitLineView(Guid? VariantId, Guid? ProductId, string Title, string? Barcode, string Sku, decimal Quantity, decimal ReturnedQuantity,
    decimal Amount, decimal? CommissionRate, decimal? Commission, decimal? CostPrice, decimal VatRate, string? ImageUrl = null, string? Options = null);

// Amount: what the customer kept paying (returned units out). Cost: the kept units' product cost (null when a line has
// none, and so Profit; Profit is null too when a charge is unknown). ProfitOnCost = Profit / Cost, Margin = Profit / Amount, in per cent. State: SOLD,
// PARTLY_RETURNED or RETURNED. MicroExport: an order of a storefront abroad.
public sealed record ReportOrderProfitView(Guid OrderId, string OrderNumber, string PlatformCode, string StoreName, string? StoreFront, bool MicroExport,
    DateTimeOffset OrderedAt, string State, decimal Amount, decimal? Cost, decimal? Profit, decimal? ProfitOnCost, decimal? Margin,
    IReadOnlyList<ReportOrderProfitCostView> Costs, IReadOnlyList<ReportOrderProfitLineView> Lines);

// A page of orders and the totals of every order the filter keeps. Amount: all of them. Profit, ProfitAmount (their
// amount) and Margin = Profit / ProfitAmount: only the ProfitOrders whose profit is known (Profit null when none is).
// MissingCost: orders with a line without a product cost; MissingCargo: orders whose cargo is unknown.
public sealed record ReportOrderProfitPage(IReadOnlyList<ReportOrderProfitView> Items, int TotalCount, int Page, int PageSize, decimal Amount, decimal? Profit, int MissingCost,
    decimal ProfitAmount, int ProfitOrders, decimal? Margin, int MissingCargo);

// Stok raporu: the stock on hand now (one warehouse, MAIN) with its cost and its sale value, and how fast it sells. Stock is
// a point-in-time value; the period (From-To) only sets the sales used for the cover days. With platforms picked, only
// the variants with an active offer on one of their stores count. All money is VAT included, as the panel stores it.
public sealed record ReportStockQuery(DateOnly? From, DateOnly? To, IReadOnlyList<string>? PlatformCodes, bool IncludeTest);

// Capital: on-hand units × cost of the variants with a cost (MissingCost* the others); Value: on-hand units × the panel
// price, as the product editor shows it (MissingPriceUnits have none); Profit and Margin over the units with both.
// LowStockProducts: stocked products a colour of which has half its sizes or more out of stock, as the product list
// counts them. SoldUnits: the period's units kept (sold less returned); CoverDays: the stock over the period's daily
// sales. Dead*: the stocked variants older than DeadDays with no unit kept in the last DeadDays days.
public sealed record ReportStockSummaryView(decimal Units, decimal AvailableUnits, int Products, int StockedProducts, int Variants, int StockedVariants, int EmptyVariants,
    int LowStockProducts, decimal Capital, decimal? CapitalExVat, int MissingCostVariants, decimal MissingCostUnits, decimal? Value, decimal PricedUnits,
    decimal MissingPriceUnits, decimal? Profit, decimal? Margin, decimal? ProfitOnCost, decimal SoldUnits, decimal? DailySales, decimal? CoverDays,
    int DeadVariants, decimal DeadUnits, decimal DeadCapital);

// One bucket of the stocked variants (a category, colour, size, price band or cover band). Value and PricedUnits at the
// panel prices; Capital over the variants with a cost.
public sealed record ReportStockGroupView(string Key, string Label, bool Known, int Products, int Variants, decimal Units, decimal Capital, decimal? Value,
    decimal PricedUnits, decimal SoldUnits, decimal? CoverDays);

// One store: its offers among the counted variants, the on-hand units of the variants it has an active offer for
// (ListedUnits) and of the stocked ones it has none for (UnlistedUnits), and the listed units at its own prices.
public sealed record ReportStockPlatformView(Guid ConnectionId, string PlatformCode, string StoreName, bool IsTest, int Offers, int ActiveOffers, decimal ListedUnits,
    decimal UnlistedUnits, decimal? Value, decimal PricedUnits, decimal? AveragePrice);

// A product (VariantId null) or a variant (Options "Siyah / L") with its stock, capital and value, the period's and the
// last DeadDays days' units kept and its cover days at the period's pace.
public sealed record ReportStockItemView(string Key, Guid ProductId, Guid? VariantId, string Title, string? Options, string? ModelCode, string Category, string? ImageUrl,
    decimal Units, decimal? Capital, decimal? Value, decimal SoldUnits, decimal RecentSoldUnits, decimal? CoverDays);

public sealed record ReportInventoryView(DateTimeOffset GeneratedAt, DateOnly From, DateOnly To, int PeriodDays, int DeadDays,
    ReportStockSummaryView Summary, IReadOnlyList<ReportStockPlatformView> Platforms, IReadOnlyList<ReportStockGroupView> Categories,
    IReadOnlyList<ReportStockGroupView> Colors, IReadOnlyList<ReportStockGroupView> Sizes, IReadOnlyList<ReportStockGroupView> PriceBands,
    IReadOnlyList<ReportStockGroupView> Cover, IReadOnlyList<ReportStockItemView> TopCapital, IReadOnlyList<ReportStockItemView> Dead,
    IReadOnlyList<ReportChannelView> Channels);

public interface IReportsService
{
    Task<ServiceResult<ReportSalesView>> SalesAsync(Guid tenantId, ReportSalesQuery query, CancellationToken cancellationToken);
    Task<ServiceResult<ReportOrderProfitPage>> OrderProfitabilityAsync(Guid tenantId, ReportOrderProfitQuery query, CancellationToken cancellationToken);
    Task<ServiceResult<ReportInventoryView>> StockAsync(Guid tenantId, ReportStockQuery query, CancellationToken cancellationToken);
}
