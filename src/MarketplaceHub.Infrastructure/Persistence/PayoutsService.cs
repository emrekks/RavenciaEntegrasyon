using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Adapters.Trendyol;
using MarketplaceHub.Infrastructure.Jobs.Handlers;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

// Hakediş (PayoutContracts.cs): Trendyol's statement records and payouts and Pazarama's payment agreements, read as
// stored. The rules that turn a record into money and a status are in PayoutBuilder.
public sealed class PayoutsService(AppDbContext db, TimeProvider timeProvider) : IPayoutsService
{
    internal const int DaysBack = 90;
    private const int LinesPageSize = 100;
    private static readonly string[] Platforms = ["TRENDYOL", "PAZARAMA", "N11", "HEPSIBURADA"];

    public async Task<ServiceResult<PayoutView>> SummaryAsync(Guid tenantId, PayoutQuery query, CancellationToken cancellationToken)
    {
        var (stores, today) = await ScopeAsync(tenantId, query, cancellationToken);
        var ids = stores.Keys.Where(id => Platforms.Contains(stores[id].PlatformCode)).ToArray();
        var since = PayoutBuilder.DayStart(today.AddDays(-DaysBack));
        var rows = await Rows(tenantId, ids)
            // Every record not paid yet (the balance), and the last days' payouts with what they paid.
            .Where(x => x.PaymentOrderId == null || x.PaymentDate >= since || x.Source == MarketplaceFinanceSources.MoneyMovements && x.TransactionDate >= since
                || x.PaymentOrderId != null && db.MarketplacePayoutOrders.Any(payout => payout.TenantId == tenantId && payout.ConnectionId == x.ConnectionId
                    && payout.ExternalId == x.PaymentOrderId && payout.PayoutDate >= since))
            .ToListAsync(cancellationToken);
        rows = await WithCargoRegionsAsync(tenantId, rows, cancellationToken);
        var payouts = await db.MarketplacePayoutOrders.AsNoTracking()
            .Where(x => x.TenantId == tenantId && ids.Contains(x.ConnectionId) && x.PayoutDate >= since)
            .Select(x => new PayoutOrderRow(x.ConnectionId, x.ExternalId, x.Amount, x.PayoutDate, x.RegionCode, x.RegionName, x.LinesReadAt != null))
            .ToListAsync(cancellationToken);
        var synced = await db.MarketplaceFinanceSyncStates.AsNoTracking()
            .Where(x => x.TenantId == tenantId && ids.Contains(x.ConnectionId))
            .ToDictionaryAsync(x => x.ConnectionId, x => x.LastSyncedAt, cancellationToken);
        // Pazarama, n11 and Hepsiburada keep no sync state: a store with records read is read, its last read the newest record's.
        var pazaramaRead = await db.MarketplaceFinanceTransactions.AsNoTracking()
            .Where(x => x.TenantId == tenantId && ids.Contains(x.ConnectionId) && (x.Source == PazaramaFinanceSyncJobHandler.Source || x.Source == N11SettlementSyncJobHandler.Source || x.Source == HepsiburadaFinanceSyncJobHandler.Source))
            .GroupBy(x => x.ConnectionId).Select(group => new { group.Key, Last = group.Max(x => x.UpdatedAt) })
            .ToDictionaryAsync(x => x.Key, x => (DateTimeOffset?)x.Last, cancellationToken);
        var coverage = stores.Values.OrderBy(x => x.PlatformCode).ThenBy(x => x.StoreName).Select(store =>
        {
            if (!Platforms.Contains(store.PlatformCode)) return new PayoutCoverageView(store.Id, store.PlatformCode, store.StoreName, "NOT_SUPPORTED", null);
            var last = synced.GetValueOrDefault(store.Id) ?? pazaramaRead.GetValueOrDefault(store.Id);
            return new PayoutCoverageView(store.Id, store.PlatformCode, store.StoreName, last is null ? "NOT_READ" : "READ", last);
        }).ToList();
        // The orders on their way to the customer (not in the balance yet). Only lira orders: another currency would need a rate.
        var pendingStatuses = PayoutBuilder.PendingDeliveryStatuses;
        var pending = await db.Orders.AsNoTracking()
            .Where(x => x.TenantId == tenantId && ids.Contains(x.ConnectionId) && pendingStatuses.Contains(x.DerivedStatus) && x.Currency == "TRY")
            // A month-old Hepsiburada order with no package that still reads open is left out, as Genel Bakış's Bekleyen
            // Siparişler does (DashboardReadService.PendingOrderMetricsQuery).
            .ExcludeStaleUnpackaged(db, tenantId, timeProvider.GetUtcNow())
            .Select(x => new PendingOrderRow(x.ConnectionId, x.StoreFrontCode, x.NetAmount))
            .ToListAsync(cancellationToken);
        var forecast = await ForecastAsync(tenantId, stores, rows, cancellationToken);
        return ServiceResult<PayoutView>.Ok(PayoutBuilder.Build(rows, payouts, stores, today, DaysBack, coverage, pending, forecast));
    }

    // Trendyol writes "Türkiye" on the cargo invoices of Avrupa and Azerbaycan orders too (live, Oct-Nov 2026: the Greek,
    // Bulgarian and Romanian parcels' invoices, paid with the Avrupa payout; an Azerbaycan parcel's, taken off the
    // Azerbaycan payout): an open cargo invoice whose parcels' orders all lie in one region stands in that region.
    private async Task<List<PayoutRow>> WithCargoRegionsAsync(Guid tenantId, List<PayoutRow> rows, CancellationToken cancellationToken)
    {
        var invoices = rows.Where(x => x.PlatformCode == "TRENDYOL" && x.PaymentOrderId == null && x.Source == TrendyolFinanceRequestContract.OtherFinancials
            && x.Category == MarketplaceFinanceCategories.Cargo).ToList();
        if (invoices.Count == 0) return rows;
        var serials = invoices.Select(x => x.InvoiceNumber ?? x.ExternalId).Distinct().ToArray();
        var connections = invoices.Select(x => x.ConnectionId).Distinct().ToArray();
        var items = await db.MarketplaceCargoInvoiceItems.AsNoTracking()
            .Where(x => x.TenantId == tenantId && connections.Contains(x.ConnectionId) && serials.Contains(x.InvoiceSerialNumber) && x.OrderNumber != null)
            .Select(x => new { x.ConnectionId, x.InvoiceSerialNumber, OrderNumber = x.OrderNumber! })
            .ToListAsync(cancellationToken);
        if (items.Count == 0) return rows;
        var orderNumbers = items.Select(x => x.OrderNumber).Distinct().ToArray();
        var orders = (await db.MarketplaceFinanceTransactions.AsNoTracking()
                .Where(x => x.TenantId == tenantId && connections.Contains(x.ConnectionId) && x.Source == TrendyolFinanceRequestContract.Settlements && x.OrderNumber != null && orderNumbers.Contains(x.OrderNumber))
                .Select(x => new { x.ConnectionId, OrderNumber = x.OrderNumber!, x.Country, x.Affiliate })
                .Distinct()
                .ToListAsync(cancellationToken))
            .GroupBy(x => (x.ConnectionId, x.OrderNumber))
            .ToDictionary(group => group.Key, group => group.Select(x => PayoutBuilder.Region("TRENDYOL", x.Country, x.Affiliate)).Distinct().ToList());
        var regions = items.GroupBy(x => (x.ConnectionId, x.InvoiceSerialNumber))
            .Select(group => (group.Key, Regions: group.SelectMany(x => orders.GetValueOrDefault((x.ConnectionId, x.OrderNumber)) ?? [PayoutBuilder.UnknownRegion]).Distinct().ToList()))
            .Where(x => x.Regions.Count == 1 && x.Regions[0] != PayoutBuilder.UnknownRegion)
            .ToDictionary(x => x.Key, x => x.Regions[0]);
        return rows.Select(row => row.PlatformCode == "TRENDYOL" && row.PaymentOrderId == null && row.Category == MarketplaceFinanceCategories.Cargo
            && regions.TryGetValue((row.ConnectionId, row.InvoiceNumber ?? row.ExternalId), out var region) ? row with { RegionOverride = region } : row).ToList();
    }

    // What the Trendyol payout days to come need for their estimated deductions (PayoutBuilder.Estimate): each store's cargo
    // tariff, the orders whose cargo is billed already and the VAT rate of the products their open records sold.
    private async Task<PayoutForecast> ForecastAsync(Guid tenantId, IReadOnlyDictionary<Guid, PayoutStore> stores, IReadOnlyList<PayoutRow> rows, CancellationToken cancellationToken)
    {
        var open = rows.Where(x => x.PlatformCode == "TRENDYOL" && x.PaymentOrderId == null && x.Source == TrendyolFinanceRequestContract.Settlements && x.OrderNumber != null).ToList();
        var connections = open.Select(x => x.ConnectionId).Distinct().Where(stores.ContainsKey).ToArray();
        var now = timeProvider.GetUtcNow();
        var tariffs = new Dictionary<Guid, decimal>();
        foreach (var connection in connections)
            if ((await ReportsService.CargoTariffAsync(db, tenantId, connection, now, cancellationToken))?.PerOrder is { } perOrder) tariffs[connection] = perOrder;
        var orderNumbers = open.Select(x => x.OrderNumber!).Distinct().ToArray();
        var billed = (await db.MarketplaceCargoInvoiceItems.AsNoTracking()
                .Where(x => x.TenantId == tenantId && connections.Contains(x.ConnectionId) && x.OrderNumber != null && orderNumbers.Contains(x.OrderNumber))
                .Select(x => new ReportCargoItemRow { ConnectionId = x.ConnectionId, InvoiceSerialNumber = x.InvoiceSerialNumber, OrderNumber = x.OrderNumber, ShipmentPackageType = x.ShipmentPackageType, Amount = x.Amount })
                .ToListAsync(cancellationToken));
        var vat = await db.OrderLines.AsNoTracking()
            .Where(line => line.TenantId == tenantId && line.Barcode != null)
            .Join(db.Orders.AsNoTracking().Where(order => order.TenantId == tenantId && connections.Contains(order.ConnectionId) && orderNumbers.Contains(order.OrderNumber)),
                line => line.OrderId, order => order.Id, (line, order) => new { order.ConnectionId, order.OrderNumber, Barcode = line.Barcode!, line.VatRate })
            .ToListAsync(cancellationToken);
        return new(tariffs,
            billed.Where(x => !x.IsReturn).Select(x => (x.ConnectionId, x.OrderNumber!)).ToHashSet(),
            billed.Where(x => x.IsReturn).Select(x => (x.ConnectionId, x.OrderNumber!)).ToHashSet(),
            vat.GroupBy(x => (x.ConnectionId, x.OrderNumber, x.Barcode)).ToDictionary(group => group.Key, group => group.Max(x => x.VatRate)));
    }

    public async Task<ServiceResult<PayoutLinesPage>> LinesAsync(Guid tenantId, PayoutQuery scope, PayoutLinesQuery query, CancellationToken cancellationToken)
    {
        var (stores, today) = await ScopeAsync(tenantId, scope, cancellationToken);
        var ids = stores.Keys.Where(id => Platforms.Contains(stores[id].PlatformCode) && (query.ConnectionId is null || id == query.ConnectionId)).ToArray();
        var rows = Rows(tenantId, ids);
        var search = query.Search?.Trim();
        if (!string.IsNullOrEmpty(search))
            rows = rows.Where(x => x.OrderNumber == search || x.InvoiceNumber == search || x.ExternalId == search || x.ShipmentPackageId == search);
        else if (!string.IsNullOrEmpty(query.PayoutId))
            rows = rows.Where(x => x.PaymentOrderId == query.PayoutId);
        // The open records expected on the day (PayoutBuilder.Expect decides the day, so it is applied after reading).
        else if (query.Day is not null) rows = PayoutBuilder.OpenOnADay(rows);
        else return ServiceResult<PayoutLinesPage>.Fail("PAYOUT_LINES_QUERY_INVALID", "Bir ödeme, bir gün ya da arama gerekir.", 400);
        var read = await WithCargoRegionsAsync(tenantId, await rows.OrderBy(x => x.TransactionDate).ToListAsync(cancellationToken), cancellationToken);
        var payoutOrders = await db.MarketplacePayoutOrders.AsNoTracking()
            .Where(x => x.TenantId == tenantId && ids.Contains(x.ConnectionId))
            .Select(x => new PayoutOrderRow(x.ConnectionId, x.ExternalId, x.Amount, x.PayoutDate, x.RegionCode, x.RegionName, x.LinesReadAt != null))
            .ToListAsync(cancellationToken);
        var lastPaid = PayoutBuilder.LastPaid(payoutOrders);
        // A day's open records leave out those a payout not read yet has paid, as the summary does (PayoutBuilder.AwaitsRead).
        var unread = PayoutBuilder.PaidUnread(payoutOrders);
        var all = read
            .Where(PayoutBuilder.Counts)
            .Select(row => (Row: row, Line: PayoutBuilder.Line(row, today)))
            .Where(x => !string.IsNullOrEmpty(search) || !string.IsNullOrEmpty(query.PayoutId)
                || PayoutBuilder.IsLira(x.Row) && query.Kind == "PAID_DAY" && x.Line.Status == PayoutBuilder.Paid && x.Line.DueDay == query.Day
                || PayoutBuilder.IsLira(x.Row) && query.Kind != "PAID_DAY" && PayoutBuilder.Open(x.Line) && !PayoutBuilder.AwaitsRead(x.Row, x.Line, unread) && PayoutBuilder.Expect(x.Row, x.Line, today, lastPaid) is var expect && expect.Day == query.Day && (query.Kind is null || expect.Kind == query.Kind)
                    && (query.Region is null || expect.Region == query.Region))
            .Select(x => x.Line)
            // A day's open records stand in the region whose payout takes them (Expect above); a paid day's in their own.
            .Where(line => query.Region is null || !string.IsNullOrEmpty(search) || !string.IsNullOrEmpty(query.PayoutId) || query.Kind != "PAID_DAY" || line.Region == query.Region)
            .ToList();
        var page = Math.Max(0, query.Page);
        return ServiceResult<PayoutLinesPage>.Ok(new(all.Skip(page * LinesPageSize).Take(LinesPageSize).ToList(), all.Count, page, LinesPageSize, all.Where(x => x.Currency == PayoutBuilder.Lira).Sum(x => x.Net)));
    }

    private IQueryable<PayoutRow> Rows(Guid tenantId, Guid[] ids) =>
        db.MarketplaceFinanceTransactions.AsNoTracking()
            .Where(x => x.TenantId == tenantId && ids.Contains(x.ConnectionId))
            .Select(x => new PayoutRow
            {
                ConnectionId = x.ConnectionId,
                PlatformCode = x.PlatformCode,
                Source = x.Source,
                ExternalId = x.ExternalId,
                RequestedType = x.RequestedType,
                TransactionSubType = x.TransactionSubType,
                TransactionType = x.TransactionType,
                Category = x.Category,
                TransactionDate = x.TransactionDate,
                OrderNumber = x.OrderNumber,
                Barcode = x.Barcode,
                ShipmentPackageId = x.ShipmentPackageId,
                InvoiceNumber = x.InvoiceNumber,
                Debt = x.Debt,
                Credit = x.Credit,
                CommissionAmount = x.CommissionAmount,
                CommissionRate = x.CommissionRate,
                SellerRevenue = x.SellerRevenue,
                PaymentOrderId = x.PaymentOrderId,
                PaymentDate = x.PaymentDate,
                Country = x.Country,
                Affiliate = x.Affiliate,
                Currency = x.Currency
            });

    private async Task<(Dictionary<Guid, PayoutStore> Stores, DateOnly Today)> ScopeAsync(Guid tenantId, PayoutQuery query, CancellationToken cancellationToken)
    {
        var timezone = DashboardReadService.ResolveTimezone(await db.Tenants.AsNoTracking().Where(x => x.Id == tenantId).Select(x => x.Timezone).SingleOrDefaultAsync(cancellationToken));
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(timeProvider.GetUtcNow(), timezone).DateTime);
        var excluded = DashboardRevenuePolicy.ExcludedConnectionStatuses;
        var platforms = query.PlatformCodes?.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim().ToUpperInvariant()).ToHashSet(StringComparer.Ordinal);
        var connections = query.ConnectionIds?.ToHashSet();
        var stores = (await db.PlatformConnections.AsNoTracking()
                .Where(x => x.TenantId == tenantId && !excluded.Contains(x.Status))
                .Select(x => new { x.Id, x.PlatformCode, x.DisplayName, x.Environment })
                .ToListAsync(cancellationToken))
            .Select(x => new PayoutStore(x.Id, x.PlatformCode.Trim().ToUpperInvariant(), x.DisplayName, ReportBuckets.IsTestEnvironment(x.Environment)))
            .Where(x => query.IncludeTest || !x.IsTest)
            .Where(x => platforms is not { Count: > 0 } || platforms.Contains(x.PlatformCode))
            .Where(x => connections is not { Count: > 0 } || connections.Contains(x.Id))
            .Where(x => x.PlatformCode != "TRENDYOL_EFATURAM")
            .ToDictionary(x => x.Id);
        return (stores, today);
    }
}

internal sealed record PayoutStore(Guid Id, string PlatformCode, string StoreName, bool IsTest);

// An order accepted or shipped but not delivered yet: its store, storefront (Trendyol: TR, SA, RO…) and net amount.
internal sealed record PendingOrderRow(Guid ConnectionId, string? StoreFrontCode, decimal Amount);

// LinesRead: Trendyol lists a payout when it pays it and its records a day later (LinesReadAt); until then the records
// that already carry its id (a deduction or two) are not its total.
internal sealed record PayoutOrderRow(Guid ConnectionId, string ExternalId, decimal Amount, DateTimeOffset? PayoutDate, string? RegionCode, string? RegionName, bool LinesRead = true);

internal sealed record PayoutRow
{
    public Guid ConnectionId { get; init; }
    public string PlatformCode { get; init; } = "";
    public string Source { get; init; } = "";
    public string ExternalId { get; init; } = "";
    public string RequestedType { get; init; } = "";
    public string? TransactionSubType { get; init; }
    public string? TransactionType { get; init; }
    public string Category { get; init; } = MarketplaceFinanceCategories.Other;
    public DateTimeOffset TransactionDate { get; init; }
    public string? OrderNumber { get; init; }
    public string? Barcode { get; init; }
    public string? ShipmentPackageId { get; init; }
    public string? InvoiceNumber { get; init; }
    public decimal Debt { get; init; }
    public decimal Credit { get; init; }
    public decimal? CommissionAmount { get; init; }
    public decimal? CommissionRate { get; init; }
    public decimal? SellerRevenue { get; init; }
    public string? PaymentOrderId { get; init; }
    public DateTimeOffset? PaymentDate { get; init; }
    public string? Country { get; init; }
    public string? Affiliate { get; init; }
    // The record's currency as the marketplace wrote it; null or empty is the lira.
    public string? Currency { get; init; }
    // A Trendyol cargo invoice's region as its parcels' orders tell (PayoutsService.CargoInvoiceRegionsAsync): Trendyol
    // writes "Türkiye" on the cargo invoices of Avrupa and Azerbaycan orders too.
    public string? RegionOverride { get; init; }
}

// What a Trendyol payout day's estimate needs beyond its records: the store's cargo tariff per parcel, the orders whose
// shipment and return cargo is billed already, and the VAT rate of each sold product (order, barcode).
internal sealed record PayoutForecast(
    IReadOnlyDictionary<Guid, decimal> CargoTariff,
    IReadOnlySet<(Guid ConnectionId, string OrderNumber)> ShippingBilled,
    IReadOnlySet<(Guid ConnectionId, string OrderNumber)> ReturnBilled,
    IReadOnlyDictionary<(Guid ConnectionId, string OrderNumber, string Barcode), decimal> VatRates);

// The rules of the payout view, pure so they are tested alone.
internal static class PayoutBuilder
{
    public const string Paid = "PAID";
    public const string Expected = "EXPECTED";
    public const string Unknown = "UNKNOWN";
    public const string TrGulf = "TR_GULF";
    public const string Europe = "EUROPE";
    public const string Azerbaijan = "AZ";
    public const string UnknownRegion = "UNKNOWN";
    // The other marketplaces pay without regions; their records carry none.
    public const string NoRegion = "NONE";
    // The marketplaces whose payouts are read, in the order their cards stand.
    internal static readonly string[] PayoutPlatformOrder = ["TRENDYOL", "HEPSIBURADA", "N11", "PAZARAMA"];

    private static readonly TimeZoneInfo Istanbul = DashboardReadService.ResolveTimezone("Europe/Istanbul");

    // Trendyol's storefront countries by its payout regions (Türkiye-Körfez, Avrupa, Azerbaycan). A country no rule
    // names is UNKNOWN, never Türkiye.
    private static readonly HashSet<string> TrGulfCountries = new(StringComparer.OrdinalIgnoreCase)
        { "Türkiye", "Turkiye", "Suudi Arabistan", "Birleşik Arap Emirlikleri", "Katar", "Umman", "Kuveyt", "Bahreyn" };
    private static readonly HashSet<string> EuropeCountries = new(StringComparer.OrdinalIgnoreCase)
        { "Bulgaristan", "Romanya", "Yunanistan", "Almanya", "Slovakya", "Çekya", "Çek Cumhuriyeti", "Macaristan", "Polonya", "Hırvatistan", "Slovenya", "Avusturya" };

    // The start of a calendar day in Istanbul, as UTC (Trendyol's due days and Hepsiburada's times are Istanbul's).
    public static DateTimeOffset DayStart(DateOnly day) =>
        new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), Istanbul.GetUtcOffset(day.ToDateTime(TimeOnly.MinValue))).ToUniversalTime();

    public static DateOnly Day(DateTimeOffset value) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(value, Istanbul).DateTime);

    // An order's state while it is accepted, being prepared or on its way: not delivered, cancelled or returned. Partly
    // cancelled and manual-review orders are left out (their amount still holds the cancelled part, or their state is
    // not known).
    internal static readonly string[] PendingDeliveryStatuses = ["NEW", "PROCESSING", "READY_TO_SHIP", "SHIPPED"];

    // Trendyol's storefronts by its payout regions; a storefront no rule names (or none read yet) is UNKNOWN.
    private static readonly HashSet<string> TrGulfStoreFronts = new(StringComparer.OrdinalIgnoreCase) { "TR", "SA", "AE", "QA", "OM", "KW", "BH" };
    private static readonly HashSet<string> EuropeStoreFronts = new(StringComparer.OrdinalIgnoreCase) { "BG", "RO", "GR", "DE", "SK", "CZ", "HU", "PL", "HR", "SI", "AT" };

    public static string StoreFrontRegion(string platformCode, string? storeFront)
    {
        if (platformCode != "TRENDYOL") return NoRegion;
        var code = storeFront?.Trim();
        if (string.IsNullOrEmpty(code)) return UnknownRegion;
        if (string.Equals(code, "AZ", StringComparison.OrdinalIgnoreCase)) return Azerbaijan;
        if (EuropeStoreFronts.Contains(code)) return Europe;
        return TrGulfStoreFronts.Contains(code) ? TrGulf : UnknownRegion;
    }

    public static string Region(string platformCode, string? country, string? affiliate)
    {
        if (platformCode != "TRENDYOL") return NoRegion;
        if (string.Equals(affiliate, "TRENDYOLAZJV", StringComparison.OrdinalIgnoreCase)) return Azerbaijan;
        if (country is null) return UnknownRegion;
        if (EuropeCountries.Contains(country)) return Europe;
        return TrGulfCountries.Contains(country) ? TrGulf : UnknownRegion;
    }

    // Whether a record moves the payout. A commission invoice only documents the commission every sale already paid
    // through its seller revenue (counting it again took ~288 thousand TL off in Nov 2026); money movements are payouts.
    public static bool Counts(PayoutRow row) =>
        row.Source != MarketplaceFinanceSources.MoneyMovements
        && !(row.Source == TrendyolFinanceRequestContract.OtherFinancials && string.Equals(row.RequestedType, TrendyolFinanceCategorizer.DeductionInvoices, StringComparison.OrdinalIgnoreCase)
             && row.Category == MarketplaceFinanceCategories.Commission)
        && !IsHepsiburadaTotal(row);

    // Hepsiburada writes each payout once more as a TotalPayment record, the sum of the records it paid (to the kuruş on
    // every week read in Sep-Nov 2026): the payout's own figure, never one more record.
    internal static bool IsHepsiburadaTotal(PayoutRow row) =>
        row.Source == HepsiburadaFinanceSyncJobHandler.Source && string.Equals(row.TransactionType, "TotalPayment", StringComparison.OrdinalIgnoreCase);

    // What a record adds to the payout: a statement line its seller revenue (commission off), signed by its debt or
    // credit; any other record its credit less its debt. Pazarama's agreement and n11's settlement line carry the net
    // payout themselves.
    public static decimal Net(PayoutRow row)
    {
        if (row.Source is PazaramaFinanceSyncJobHandler.Source or N11SettlementSyncJobHandler.Source) return row.SellerRevenue ?? row.Credit - row.Debt;
        if (row.Source == TrendyolFinanceRequestContract.Settlements && row.SellerRevenue is { } revenue)
            return Math.Sign(row.Credit - row.Debt) * Math.Abs(revenue);
        return row.Credit - row.Debt;
    }

    // Paid once a payout carries the record (Pazarama: as it says); expected while its due day is to come; unknown
    // when the day has passed with no payout seen (the payout may not be read yet: never "unpaid").
    public static string Status(PayoutRow row, DateOnly today)
    {
        if (row.Source == PazaramaFinanceSyncJobHandler.Source)
        {
            var text = TrendyolFinanceCategorizer.Fold(row.TransactionType);
            if (text.Contains("odeme yapildi", StringComparison.Ordinal)) return Paid;
            if (text.Contains("bekliyor", StringComparison.Ordinal)) return Expected;
            // A "Satış" or "İade" row names no state: its due day still to come is expected (as n11); past and unpaid stays unknown.
            return row.PaymentDate is { } pazaramaDue && Day(pazaramaDue) >= today ? Expected : Unknown;
        }
        // n11 names a day's state in a word its WSDL does not list; a word that says paid or waiting decides.
        if (row.Source == N11SettlementSyncJobHandler.Source)
        {
            var text = TrendyolFinanceCategorizer.Fold(row.TransactionType);
            if (text.Contains("odendi", StringComparison.Ordinal) || text.Contains("odeme yapildi", StringComparison.Ordinal) || text.Contains("paid", StringComparison.Ordinal)) return Paid;
            if (text.Contains("bekl", StringComparison.Ordinal) || text.Contains("waiting", StringComparison.Ordinal)) return Expected;
            // Otherwise its payment day tells: past, n11 sent the money.
            return DueDay(row) is { } paid && paid < today ? Paid : Expected;
        }
        // Hepsiburada says it: Paid, or WillBePaid (expected until its due day, then overdue).
        if (row.Source == HepsiburadaFinanceSyncJobHandler.Source && string.Equals(row.TransactionSubType, "Paid", StringComparison.OrdinalIgnoreCase)) return Paid;
        if (!string.IsNullOrEmpty(row.PaymentOrderId)) return Paid;
        return row.PaymentDate is { } due && PaidOn(row, Day(due)) >= today ? Expected : Unknown;
    }

    public const string Lira = "TRY";

    // A record in lira: only these add up to the balance, the payouts and the lines' total; another currency stays apart.
    internal static bool IsLira(PayoutRow row) => string.IsNullOrWhiteSpace(row.Currency) || string.Equals(row.Currency.Trim(), Lira, StringComparison.OrdinalIgnoreCase);

    internal static string CurrencyOf(PayoutRow row) => IsLira(row) ? Lira : row.Currency!.Trim().ToUpperInvariant();

    public static PayoutLineView Line(PayoutRow row, DateOnly today) =>
        new(row.ExternalId, row.ConnectionId, row.PlatformCode, row.TransactionType, row.Category, row.TransactionDate, DueDay(row),
            row.OrderNumber, row.InvoiceNumber, row.Country, row.RegionOverride ?? Region(row.PlatformCode, row.Country, row.Affiliate), row.Credit - row.Debt, Commission(row), Net(row), Status(row, today), row.PaymentOrderId, row.Barcode, CurrencyOf(row));

    // The day the money is due. n11 pays on Thursdays ("hak ediş tutarları her perşembe günü ödenir"; a holiday moves
    // it, 30 Oct 2026). A sent record's remittance day (havale tarihi, stored as the payment day) is the transfer day
    // (15 Oct, 30 Oct, 5 Nov 2026 in n11's Para Transfer Geçmişi); a waiting one's is not: Wait_Remittance with
    // remittanceDate Wednesday 11 Nov was paid Thursday 12 Nov. A waiting record is due the Thursday on or after it.
    internal static DateOnly? DueDay(PayoutRow row) => row.PaymentDate is not { } due ? null
        : row.Source == N11SettlementSyncJobHandler.Source && TrendyolFinanceCategorizer.Fold(row.TransactionType).Contains("wait", StringComparison.Ordinal)
            ? WeekdayOnOrAfter(Day(due), DayOfWeek.Thursday)
            : Day(due);

    // The record's commission in lira. n11 sends a rate, not an amount: its line's commission is worked out from it
    // (N11SettlementSyncJobHandler.Commission; a row stored before 5 Nov 2026 holds the rate in CommissionAmount).
    internal static decimal? Commission(PayoutRow row) => row.Source == N11SettlementSyncJobHandler.Source
        ? N11SettlementSyncJobHandler.Commission(row.CommissionRate, row.CommissionRate is null ? row.CommissionAmount : null, row.Credit, row.Category)
        : row.CommissionAmount;

    // Whether a record still waits for its payout: not paid, whatever its due day (Avrupa is paid about monthly, so a
    // record past its due day may simply wait for the next payout). One with no due day read (Pazarama and Hepsiburada
    // can write one) is unpaid too: it counts in the balance and waits apart, UNDATED.
    public static bool Open(PayoutLineView line) => line.Status is Expected or Unknown;

    // The records a day's open group can hold, before Expect places them: every record no payout carries. One with no
    // due day stands on today, UNDATED, so it is read too, or its UNDATED group opens empty.
    internal static IQueryable<PayoutRow> OpenOnADay(IQueryable<PayoutRow> rows) => rows.Where(x => x.PaymentOrderId == null);

    // The day an open record is expected to be paid on, and the region whose payout takes it. Türkiye-Körfez and
    // Azerbaycan, as Trendyol paid them in October-November 2026 and as its "Ödeme Özeti" of 7 and 11 Nov 2026 estimated
    // them: Monday's payout carries the sales due Monday to Wednesday, Thursday's those due Thursday to Sunday, and a
    // deduction goes with the first payout on or after its due day. Trendyol's calendar marks Tuesday and Friday "Erken
    // Ödeme" (an early payout the seller asks for), never a regular payout day. A public holiday moves a payout (29 Oct
    // 2026 to the 30th), so the day is an estimate. Avrupa is paid on the fourth Monday of the month (the Monday of the
    // 22nd-28th: 22 Jun, 27 Jul, 24 Aug, 28 Sep, 26 Oct 2026 and, in the Ödeme Özeti, 23 Nov), everything due up to that
    // day. A day already past moves to the next one to come. Kind: PAYOUT_DAY (estimated payout day) or DUE_DAY; a
    // record with no due day is UNDATED (shown on today, never in a payout's estimate).
    // LastPaid: the last payout read with its records, by store and region. A deduction whose payout day that payout has
    // reached without taking it is not that region's to take. A Türkiye-Körfez one went to Avrupa every time (Aug-Nov
    // 2026: each of the 55 deductions the first Türkiye payout after their billing left out, the cargo and platform fee
    // invoices of Avrupa orders written "Türkiye" and the micro-export return fees, was paid by the next Avrupa payout),
    // so it is expected there when the store is paid in Avrupa; otherwise it waits apart, OVERDUE.
    public static (DateOnly Day, string Kind, string Region) Expect(PayoutRow row, PayoutLineView line, DateOnly today, IReadOnlyDictionary<(Guid, string), DateOnly>? lastPaid = null)
    {
        var (day, kind) = ExpectDay(row, line, today, lastPaid, out var region);
        return (day, kind, region);
    }

    private static (DateOnly Day, string Kind) ExpectDay(PayoutRow row, PayoutLineView line, DateOnly today, IReadOnlyDictionary<(Guid, string), DateOnly>? lastPaid, out string region)
    {
        region = line.Region;
        if (line.DueDay is not { } due) return (today, Undated);
        var (day, kind) = PayDay(row, due, lastPaid, ref region);
        // Past due (PaidOn) with no payout seen: it waits apart, OVERDUE, and counts in Vadesi Geçmiş Bekleyen alone, never in
        // a day to come, the next payout or a region's or marketplace's next payout (owner, 13 Nov 2026: one record, one place).
        if (kind == "OVERDUE" || PaidOn(row, due) < today) return (today, "OVERDUE");
        // A Trendyol sale whose payout day (the one before its due day) has passed while it is not due yet: the next one.
        if (day < today) return (region == Europe ? EuropePayoutDayOnOrAfter(today) : PayoutDayOnOrAfter(today), kind);
        return (day, kind);
    }

    // The day a record due on a day is paid, before any record is past due.
    // Hepsiburada pays every Tuesday the records due since the Tuesday before (Sep-Nov 2026: due Wednesday 28 Oct, paid
    // Tuesday 3 Nov; due on a Tuesday, paid that day), deductions with them. Avrupa is paid on the fourth Monday. The other
    // marketplaces and an unknown Trendyol region pay on the due day (DUE_DAY).
    private static (DateOnly Day, string Kind) PayDay(PayoutRow row, DateOnly due, IReadOnlyDictionary<(Guid, string), DateOnly>? lastPaid, ref string region)
    {
        if (row.PlatformCode == "HEPSIBURADA") return (WeekdayOnOrAfter(due, DayOfWeek.Tuesday), "PAYOUT_DAY");
        if (row.PlatformCode == "TRENDYOL" && region == Europe) return (EuropePayoutDayOnOrAfter(due), "PAYOUT_DAY");
        if (row.PlatformCode != "TRENDYOL" || region is not (TrGulf or Azerbaijan)) return (due, "DUE_DAY");
        var settlement = row.Source == TrendyolFinanceRequestContract.Settlements;
        var day = settlement ? PayoutDayOnOrBefore(due) : PayoutDayOnOrAfter(due);
        if (!settlement && lastPaid is not null && lastPaid.TryGetValue((row.ConnectionId, region), out var last) && day <= last)
        {
            if (region != TrGulf || !lastPaid.ContainsKey((row.ConnectionId, Europe))) return (day, "OVERDUE");
            region = Europe;
            return (EuropePayoutDayOnOrAfter(due), "PAYOUT_DAY");
        }
        return (day, "PAYOUT_DAY");
    }

    // The last day a record due on a day is paid on time, after which it is past due. Hepsiburada pays every Tuesday what
    // fell due since the Tuesday before, so its record is past due only once that Tuesday has passed (owner, 13 Nov 2026);
    // every other record once its due day has passed.
    private static DateOnly PaidOn(PayoutRow row, DateOnly due) =>
        row.PlatformCode == "HEPSIBURADA" ? WeekdayOnOrAfter(due, DayOfWeek.Tuesday) : due;

    // Avrupa's payout day: the fourth Monday of the month (its 22nd-28th), the first on or after the day.
    internal static DateOnly EuropePayoutDayOnOrAfter(DateOnly day)
    {
        var monday = WeekdayOnOrAfter(new DateOnly(day.Year, day.Month, 22), DayOfWeek.Monday);
        if (monday >= day) return monday;
        var next = new DateOnly(day.Year, day.Month, 1).AddMonths(1);
        return WeekdayOnOrAfter(new DateOnly(next.Year, next.Month, 22), DayOfWeek.Monday);
    }

    private static DateOnly WeekdayOnOrAfter(DateOnly day, DayOfWeek weekday) => day.AddDays(((int)weekday - (int)day.DayOfWeek + 7) % 7);

    private static DateOnly PayoutDayOnOrBefore(DateOnly day) => day.DayOfWeek switch
    {
        DayOfWeek.Monday or DayOfWeek.Thursday => day,
        DayOfWeek.Tuesday or DayOfWeek.Friday => day.AddDays(-1),
        DayOfWeek.Wednesday or DayOfWeek.Saturday => day.AddDays(-2),
        _ => day.AddDays(-3)
    };

    private static DateOnly PayoutDayOnOrAfter(DateOnly day) => day.DayOfWeek switch
    {
        DayOfWeek.Monday or DayOfWeek.Thursday => day,
        DayOfWeek.Tuesday => day.AddDays(2),
        DayOfWeek.Wednesday => day.AddDays(1),
        DayOfWeek.Friday => day.AddDays(3),
        DayOfWeek.Saturday => day.AddDays(2),
        _ => day.AddDays(1)
    };

    public static PayoutView Build(IReadOnlyList<PayoutRow> rows, IReadOnlyList<PayoutOrderRow> payouts, IReadOnlyDictionary<Guid, PayoutStore> stores, DateOnly today, int daysBack,
        IReadOnlyList<PayoutCoverageView> coverage, IReadOnlyList<PendingOrderRow>? pendingOrders = null, PayoutForecast? forecast = null)
    {
        var lines = rows.Where(Counts).Where(IsLira).Select(row => (Row: row, Line: Line(row, today))).ToList();
        // The records in another currency, each currency apart: never added to a lira figure.
        var otherCurrencies = rows.Where(Counts).Where(row => !IsLira(row)).Select(row => (Row: row, Line: Line(row, today))).Where(x => Open(x.Line))
            .GroupBy(x => x.Line.Currency).OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new PayoutCurrencyView(group.Key, Math.Round(group.Sum(x => x.Line.Net), 2), group.Count())).ToList();
        var lastPaid = LastPaid(payouts);
        // The orders not delivered yet, of the stores in view, by marketplace and Trendyol region.
        var pending = (pendingOrders ?? []).Where(order => stores.ContainsKey(order.ConnectionId))
            .Select(order => (Platform: stores[order.ConnectionId].PlatformCode, Region: StoreFrontRegion(stores[order.ConnectionId].PlatformCode, order.StoreFrontCode), order.Amount))
            .ToList();
        // The balance: every record not paid yet (Trendyol's "Toplam Güncel Bakiye" is the same sum: 295,540.76 TL on
        // 4 Nov 2026, to the kuruş). Overdue: its part past the due day, still waiting for a payout.
        // A Trendyol sale or return a payout made but not read yet has paid (AwaitsRead): out of the balance and the days
        // to come, counted apart (PaidUnread) so the page can say so.
        var unread = PaidUnread(payouts);
        var awaiting = lines.Where(x => Open(x.Line) && AwaitsRead(x.Row, x.Line, unread)).ToList();
        var open = lines.Where(x => Open(x.Line) && !AwaitsRead(x.Row, x.Line, unread)).Select(x => (x.Row, x.Line, Expect: Expect(x.Row, x.Line, today, lastPaid))).ToList();
        var overdue = open.Where(x => x.Expect.Kind == "OVERDUE").ToList();

        List<PayoutGroupView> Days(IEnumerable<(PayoutRow Row, PayoutLineView Line, (DateOnly Day, string Kind, string Region) Expect)> records) => records
            .GroupBy(x => (x.Row.ConnectionId, x.Expect.Day, x.Expect.Region, x.Expect.Kind))
            .Select(group =>
            {
                var records = group.Sum(x => x.Line.Net);
                // A Trendyol payout day to come also loses the deductions billed the evening before it: estimated as
                // Trendyol's own Ödeme Özeti does, the group's amount is the records less them.
                var estimates = forecast is not null && group.Key.Kind == "PAYOUT_DAY" && group.Key.Region is TrGulf or Azerbaijan or Europe
                    && stores.GetValueOrDefault(group.Key.ConnectionId)?.PlatformCode == "TRENDYOL"
                    ? Estimate(group.Select(x => (x.Row, x.Line)).ToList(), group.Key.Region, forecast) : [];
                var key = $"due:{group.Key.ConnectionId:N}:{group.Key.Day:yyyy-MM-dd}:{group.Key.Region}:{group.Key.Kind}";
                return estimates.Count == 0
                    ? Group(key, group.Key.Kind, stores, group.Key.ConnectionId, group.Key.Region, null, group.Key.Day, records, null, group.Count(), Expected)
                    : Group(key, group.Key.Kind, stores, group.Key.ConnectionId, group.Key.Region, null, group.Key.Day, records - estimates.Sum(x => x.Amount), records, group.Count(), Expected)
                        with
                    { Estimates = estimates };
            })
            .OrderBy(x => x.Day).ThenBy(x => x.PlatformCode).ThenBy(x => x.Region).ToList();
        var upcoming = Days(open);
        // The next payout, overall and per region and marketplace, counts only the records not past their due day (owner,
        // 13 Nov 2026, the guide's rule): one past it counts in Vadesi Geçmiş Bekleyen alone, never in Sıradaki Ödeme too.
        var coming = Days(open.Where(x => x.Expect.Kind != "OVERDUE"));
        // What each day to come pays once the days before it that netted to nothing or less are taken off (Carry).
        var carried = Carry(coming);
        decimal Payable(PayoutGroupView group) => carried.TryGetValue(group.Key, out var carry) ? carry.Payable : 0m;
        IGrouping<DateOnly, PayoutGroupView>? FirstPaying(IEnumerable<PayoutGroupView> groups) =>
            groups.Where(x => !Waiting(x.Kind)).GroupBy(x => x.Day).OrderBy(x => x.Key).FirstOrDefault(day => day.Sum(Payable) > 0);

        var totals = rows.Where(row => IsHepsiburadaTotal(row) && IsLira(row) && row.PaymentDate is not null)
            .GroupBy(row => (row.ConnectionId, Day: Day(row.PaymentDate!.Value))).ToDictionary(group => group.Key, group => group.Sum(row => row.Credit - row.Debt));
        var paidLines = lines.Where(x => !string.IsNullOrEmpty(x.Row.PaymentOrderId))
            .GroupBy(x => (x.Row.ConnectionId, x.Row.PaymentOrderId!))
            .ToDictionary(group => group.Key, group => (Net: group.Sum(x => x.Line.Net), Count: group.Count()));
        var past = payouts.Where(x => x.PayoutDate is not null && stores.ContainsKey(x.ConnectionId))
            .Select(payout =>
            {
                // A payout whose records are not read yet has no records total: the panel says so instead of a gap (5 Nov 2026:
                // 14 deductions read, −13,454.74 shown beside a 25,118.00 payout).
                var read = payout.LinesRead && paidLines.TryGetValue((payout.ConnectionId, payout.ExternalId), out var sum) ? sum : ((decimal Net, int Count)?)null;
                return Group($"payout:{payout.ConnectionId:N}:{payout.ExternalId}", "PAYOUT", stores, payout.ConnectionId, PayoutRegion(payout.RegionCode, payout.RegionName), payout.RegionName ?? payout.RegionCode,
                    Day(payout.PayoutDate!.Value), payout.Amount, read?.Net, read?.Count ?? 0, Paid);
            })
            // Pazarama's paid agreements, n11's and Hepsiburada's paid records, a store's day at a time; a Hepsiburada payout shows its
            // own TotalPayment figure, its records' sum beside it.
            .Concat(lines.Where(x => x.Row.Source is PazaramaFinanceSyncJobHandler.Source or N11SettlementSyncJobHandler.Source or HepsiburadaFinanceSyncJobHandler.Source && x.Line.Status == Paid && x.Line.DueDay is { } day && day >= today.AddDays(-daysBack))
                .GroupBy(x => (x.Row.ConnectionId, Day: x.Line.DueDay!.Value))
                .Select(group => Group($"paid:{group.Key.ConnectionId:N}:{group.Key.Day:yyyy-MM-dd}", "PAID_DAY", stores, group.Key.ConnectionId, NoRegion, null, group.Key.Day,
                    totals.TryGetValue((group.Key.ConnectionId, group.Key.Day), out var total) ? total :
                    group.Sum(x => x.Line.Net), group.Sum(x => x.Line.Net), group.Count(), Paid)))
            // Newest first; on one day the larger payout first, then a fixed key, so the order never depends on the query.
            .OrderByDescending(x => x.Day).ThenByDescending(x => x.Amount).ThenBy(x => x.Key, StringComparer.Ordinal).ToList();

        var regions = new[] { TrGulf, Europe, Azerbaijan, UnknownRegion }
            .Select(region =>
            {
                var due = open.Where(x => x.Expect.Region == region).ToList();
                var next = FirstPaying(coming.Where(x => x.Region == region));
                var waiting = pending.Where(x => x.Region == region).ToList();
                return new PayoutRegionView(region, due.Sum(x => x.Line.Net), due.Count, next?.Key, next?.Sum(Payable),
                    overdue.Where(x => x.Expect.Region == region).Sum(x => x.Line.Net), waiting.Sum(x => x.Amount), waiting.Count);
            })
            .Where(x => x.Region != UnknownRegion || x.Records > 0 || x.Unknown != 0 || x.PendingDeliveryOrders > 0)
            .ToList();
        // Every marketplace with a store in view, in the platforms' order; the region totals above are Trendyol's alone.
        var marketplaces = stores.Values.Select(store => store.PlatformCode).Where(code => PayoutPlatformOrder.Contains(code)).Distinct()
            .OrderBy(code => Array.IndexOf(PayoutPlatformOrder, code))
            .Select(code =>
            {
                var due = open.Where(x => x.Row.PlatformCode == code).ToList();
                var next = FirstPaying(coming.Where(x => x.PlatformCode == code));
                var waiting = pending.Where(x => x.Platform == code).ToList();
                return new PayoutPlatformView(code, due.Sum(x => x.Line.Net), due.Count, next?.Key, next?.Sum(Payable),
                    overdue.Where(x => x.Row.PlatformCode == code).Sum(x => x.Line.Net), waiting.Sum(x => x.Amount), waiting.Count);
            })
            .ToList();

        var early = rows.Where(x => IsLira(x) && x.Source == MarketplaceFinanceSources.MoneyMovements && string.Equals(x.RequestedType, "CashAdvance", StringComparison.OrdinalIgnoreCase)
            && Day(x.TransactionDate) >= today.AddDays(-daysBack)).ToList();
        // The next payout: the first day to come that pays anything once each store's earlier days that netted to
        // nothing or less are taken off it (the marketplace takes them off that store's next payout in the region:
        // Carry), the waiting overdue records left out. Carried: what those earlier days took off.
        var nextDay = FirstPaying(coming)?.Key;
        var next = nextDay is { } first
            ? coming.Where(x => x.Day == first && !Waiting(x.Kind)).GroupBy(x => 1).Select(group => group.First() with
            {
                Key = $"next:{first:yyyy-MM-dd}",
                Amount = Math.Round(group.Sum(Payable), 2),
                Carried = group.Sum(x => carried.TryGetValue(x.Key, out var carry) && carry.Payable > 0 ? carry.In : 0m) is var taken && taken != 0m ? Math.Round(taken, 2) : null,
                Records = group.Sum(x => x.Records),
                LinesAmount = group.Any(x => x.Estimates is not null) ? group.Sum(x => x.LinesAmount ?? x.Amount) : null,
                Estimates = group.Any(x => x.Estimates is not null) ? group.SelectMany(x => x.Estimates ?? []).ToList() : null
            }).Single()
            : null;
        return new(today, daysBack, open.Sum(x => x.Line.Net), open.Count, next, past.FirstOrDefault(x => x.Amount > 0) ?? past.FirstOrDefault(),
            overdue.Sum(x => x.Line.Net), overdue.Count, early.Sum(x => Math.Abs(x.Credit - x.Debt)), early.Count, regions, marketplaces, upcoming, past, coverage,
            pending.Sum(x => x.Amount), pending.Count, awaiting.Sum(x => x.Line.Net), awaiting.Count, otherCurrencies);
    }

    // Each day to come of a store in a region with what it pays: its own amount plus what the days before it carried
    // (In, zero or less). A day that nets to nothing or less pays nothing; the marketplace takes it off the store's next
    // payout in the region, so it is carried into the next day (scan, 13 Nov 2026: a Monday of deductions alone, −500,
    // before a Thursday of +2,000 pays about 1,500, not 2,000; and a Trendyol day below zero never takes another
    // marketplace's payout of the same day down). The waiting groups (overdue, undated) carry nothing.
    internal static Dictionary<string, (decimal In, decimal Payable)> Carry(IEnumerable<PayoutGroupView> upcoming)
    {
        var result = new Dictionary<string, (decimal In, decimal Payable)>(StringComparer.Ordinal);
        foreach (var store in upcoming.Where(x => !Waiting(x.Kind)).GroupBy(x => (x.ConnectionId, x.Region)))
        {
            var carry = 0m;
            foreach (var group in store.OrderBy(x => x.Day).ThenBy(x => x.Kind, StringComparer.Ordinal))
            {
                var total = group.Amount + carry;
                result[group.Key] = (carry, total > 0m ? total : 0m);
                carry = total > 0m ? 0m : total;
            }
        }
        return result;
    }

    // The day of each store's last payout in a region whose records were read (a payout is read a day after it is made;
    // before that it cannot tell what it left out).
    internal static Dictionary<(Guid, string), DateOnly> LastPaid(IEnumerable<PayoutOrderRow> payouts) =>
        payouts.Where(x => x.LinesRead && x.PayoutDate is not null)
            .GroupBy(x => (x.ConnectionId, PayoutRegion(x.RegionCode, x.RegionName)))
            .ToDictionary(group => group.Key, group => group.Max(x => Day(x.PayoutDate!.Value)));

    // The day of each store's newest payout in a region made after the last one read, whose records are not read yet.
    // Trendyol writes a payout's id on the records it paid only when they are read, a day after the payout
    // (TrendyolFinanceSyncJobHandler, readyBefore = now - 1 day); until then those records still read open.
    internal static Dictionary<(Guid, string), DateOnly> PaidUnread(IEnumerable<PayoutOrderRow> payouts)
    {
        var list = payouts.ToList();
        var read = LastPaid(list);
        return list.Where(x => !x.LinesRead && x.PayoutDate is not null)
            .GroupBy(x => (x.ConnectionId, PayoutRegion(x.RegionCode, x.RegionName)))
            .Select(group => (group.Key, Day: group.Max(x => Day(x.PayoutDate!.Value))))
            .Where(x => !read.TryGetValue(x.Key, out var last) || x.Day > last)
            .ToDictionary(x => x.Key, x => x.Day);
    }

    // Whether an open record was paid by a payout whose records are not read yet (PaidUnread): a Trendyol sale or return
    // (a statement line; the payout carries every one of them its day takes) whose own payout day, before any move to
    // a day to come, is on or before that payout. A deduction is left open: a payout does not always take one (LastPaid's
    // Avrupa rule), so only its read records can tell.
    internal static bool AwaitsRead(PayoutRow row, PayoutLineView line, IReadOnlyDictionary<(Guid, string), DateOnly> unread)
    {
        if (row.PlatformCode != "TRENDYOL" || row.Source != TrendyolFinanceRequestContract.Settlements || line.DueDay is not { } due) return false;
        if (!unread.TryGetValue((row.ConnectionId, line.Region), out var paid)) return false;
        DateOnly? day = line.Region switch
        {
            TrGulf or Azerbaijan => PayoutDayOnOrBefore(due),
            Europe => EuropePayoutDayOnOrAfter(due),
            _ => null
        };
        return day is { } payoutDay && payoutDay <= paid;
    }

    // Trendyol bills a payout's cargo, platform service fee, international service fee and withholding tax only the
    // evening before (and the morning of) the payout, yet its Ödeme Özeti takes them off the payouts to come already.
    // Rebuilt from the payouts of 30 Oct - 5 Nov 2026 and Trendyol's estimates of 7 Nov 2026 (Azerbaycan 858.08 and
    // 209.08 to the kuruş): per payout,
    // - cargo: every shipped package and every returned one at the parcel's tariff (98.34 TL for 0-2 desi; a heavier
    //   parcel or another carrier is dearer, 106.76-128.64 TL), unless the order's cargo is billed already;
    // - platform service fee: 13.19 TL a package (10.99 on Azerbaycan; a few packages pay 5.99);
    // - international service fee: 6 % of the micro-export storefronts' sales net of discounts (Gulf, Azerbaycan);
    // - withholding tax (Law 7524): 1 % of the domestic sales net of discounts and returns without their VAT, 1 % of the
    //   Gulf sales (no VAT); none on Azerbaycan.
    // A kind already billed for the payout (its invoice is among the day's records) is not estimated again. Trendyol knows
    // each parcel's desi and fee tier; the estimate takes the usual ones, so it can differ by a few lira a package.
    internal static IReadOnlyList<PayoutEstimateView> Estimate(IReadOnlyList<(PayoutRow Row, PayoutLineView Line)> records, string region, PayoutForecast forecast)
    {
        var settlements = records.Select(x => x.Row).Where(row => row.Source == TrendyolFinanceRequestContract.Settlements).ToList();
        if (settlements.Count == 0) return [];
        var invoices = records.Select(x => x.Row).Where(row => row.Source != TrendyolFinanceRequestContract.Settlements).ToList();
        var connection = settlements[0].ConnectionId;
        var azerbaijan = region == Azerbaijan;
        static string Package(PayoutRow row) => row.ShipmentPackageId ?? row.OrderNumber ?? row.ExternalId;
        static bool International(PayoutRow row) => TrendyolFinanceCategorizer.Fold(row.TransactionType).Contains("uluslararasi", StringComparison.Ordinal);
        bool Domestic(PayoutRow row) => !azerbaijan && TrendyolFinanceCategorizer.Fold(row.Country) == "turkiye";
        var sales = settlements.Where(row => row.Category == MarketplaceFinanceCategories.Sale).GroupBy(Package).Select(group => group.First()).ToList();
        var returns = settlements.Where(row => row.Category == MarketplaceFinanceCategories.Return).GroupBy(Package).Select(group => group.First()).ToList();
        var result = new List<PayoutEstimateView>();

        // Avrupa (live payouts of 28 Sep and 26 Oct 2026): its parcels' cargo and platform fee are billed through the month,
        // with the Türkiye payouts' deductions, a parcel's both on the same evening; the international fee (6 %) and
        // withholding tax (1 %) of all its sales the evening before its payout. Its returns cost no return cargo (a
        // micro-export return is billed as "Yurtdışı Operasyon Iade Bedeli").
        if (region == Europe)
        {
            var unbilled = sales.Count(row => row.OrderNumber is null || !forecast.ShippingBilled.Contains((connection, row.OrderNumber)));
            decimal? europeTariff = forecast.CargoTariff.TryGetValue(connection, out var europeKnown) ? europeKnown : null;
            if (unbilled > 0)
            {
                result.Add(new("CARGO", unbilled, europeTariff is { } rate ? Math.Round(unbilled * rate, 2) : 0m, europeTariff));
                result.Add(new("PLATFORM_FEE", unbilled, unbilled * ReportCosts.PlatformServiceFeePerOrder, ReportCosts.PlatformServiceFeePerOrder));
            }
            var europeNet = settlements.Sum(row => row.Credit - row.Debt);
            if (!invoices.Any(International))
                result.Add(new("INTERNATIONAL_FEE", sales.Count, Math.Max(0m, Math.Round(europeNet * ReportCosts.InternationalServiceFeeRate, 2)), ReportCosts.InternationalServiceFeeRate));
            if (!invoices.Any(row => row.Category == MarketplaceFinanceCategories.Stoppage) && europeNet > 0)
                result.Add(new("STOPPAGE", settlements.Count, Math.Round(europeNet * ReportCosts.StoppageRate, 2), ReportCosts.StoppageRate));
            return result;
        }

        if (!invoices.Any(row => row.Category == MarketplaceFinanceCategories.Cargo))
        {
            decimal? tariff = forecast.CargoTariff.TryGetValue(connection, out var known) ? known : null;
            var shipped = sales.Count(row => row.OrderNumber is null || !forecast.ShippingBilled.Contains((connection, row.OrderNumber)));
            var returned = returns.Count(row => row.OrderNumber is null || !forecast.ReturnBilled.Contains((connection, row.OrderNumber)));
            if (shipped > 0) result.Add(new("CARGO", shipped, tariff is { } rate ? Math.Round(shipped * rate, 2) : 0m, tariff));
            if (returned > 0) result.Add(new("RETURN_CARGO", returned, tariff is { } rate2 ? Math.Round(returned * rate2, 2) : 0m, tariff));
        }
        if (sales.Count > 0 && !invoices.Any(row => row.Category == MarketplaceFinanceCategories.ServiceFee && !International(row)))
        {
            var fee = azerbaijan ? ReportCosts.AzerbaijanServiceFeePerOrder : ReportCosts.PlatformServiceFeePerOrder;
            result.Add(new("PLATFORM_FEE", sales.Count, sales.Count * fee, fee));
        }
        var abroad = settlements.Where(row => !Domestic(row)).ToList();
        var abroadNet = abroad.Sum(row => row.Credit - row.Debt);
        if (abroad.Count > 0 && !invoices.Any(International))
            result.Add(new("INTERNATIONAL_FEE", sales.Count(row => !Domestic(row)), Math.Max(0m, Math.Round(abroadNet * ReportCosts.InternationalServiceFeeRate, 2)), ReportCosts.InternationalServiceFeeRate));
        if (!azerbaijan && !invoices.Any(row => row.Category == MarketplaceFinanceCategories.Stoppage))
        {
            var domestic = settlements.Where(Domestic).ToList();
            decimal? Vat(PayoutRow row) => row.OrderNumber is not null && row.Barcode is not null && forecast.VatRates.TryGetValue((connection, row.OrderNumber, row.Barcode), out var vat) ? vat : null;
            var known = domestic.Where(row => Vat(row) is not null).ToList();
            var vatFree = known.Sum(row => (row.Credit - row.Debt) / (1m + Vat(row)!.Value / 100m));
            var amount = Math.Round(vatFree * ReportCosts.StoppageRate, 2) + Math.Round(abroadNet * ReportCosts.StoppageRate, 2);
            if (amount > 0) result.Add(new("STOPPAGE", settlements.Count, amount, ReportCosts.StoppageRate, domestic.Count - known.Count));
        }
        return result;
    }

    public const string Undated = "UNDATED";

    // The open records that wait apart, in no payout's estimate: past their due day with no payout day known, or with no
    // due day at all.
    private static bool Waiting(string kind) => kind is "OVERDUE" or Undated;

    // A payout's region by Trendyol's code or name; live since Nov 2026: TR "Türkiye, Körfez", AZ "Azerbaycan",
    // CEE "Avrupa". One no rule names stays UNKNOWN and is shown by the name Trendyol gave it.
    internal static string PayoutRegion(string? code, string? name)
    {
        var upper = code?.Trim().ToUpperInvariant();
        var text = TrendyolFinanceCategorizer.Fold(name);
        if (upper == "AZ" || text.Contains("azerbaycan", StringComparison.Ordinal)) return Azerbaijan;
        if (upper is "CEE" or "EU" or "EUR" || text.Contains("avrupa", StringComparison.Ordinal)) return Europe;
        if (upper == "TR" || text.Contains("turkiye", StringComparison.Ordinal) || text.Contains("korfez", StringComparison.Ordinal)) return TrGulf;
        return UnknownRegion;
    }

    private static PayoutGroupView Group(string key, string kind, IReadOnlyDictionary<Guid, PayoutStore> stores, Guid connectionId, string region, string? regionName, DateOnly day,
        decimal amount, decimal? linesAmount, int records, string status)
    {
        var store = stores.GetValueOrDefault(connectionId);
        return new(key, kind, connectionId, store?.PlatformCode ?? "", store?.StoreName ?? "", region, regionName, day, Math.Round(amount, 2), linesAmount is { } lines ? Math.Round(lines, 2) : null, records, status);
    }
}
