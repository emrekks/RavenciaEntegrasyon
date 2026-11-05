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
        var payouts = await db.MarketplacePayoutOrders.AsNoTracking()
            .Where(x => x.TenantId == tenantId && ids.Contains(x.ConnectionId) && x.PayoutDate >= since)
            .Select(x => new PayoutOrderRow(x.ConnectionId, x.ExternalId, x.Amount, x.PayoutDate, x.RegionCode, x.RegionName))
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
            .Select(x => new PendingOrderRow(x.ConnectionId, x.StoreFrontCode, x.NetAmount))
            .ToListAsync(cancellationToken);
        return ServiceResult<PayoutView>.Ok(PayoutBuilder.Build(rows, payouts, stores, today, DaysBack, coverage, pending));
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
        else if (query.Day is not null) rows = rows.Where(x => x.PaymentOrderId == null && x.PaymentDate != null);
        else return ServiceResult<PayoutLinesPage>.Fail("PAYOUT_LINES_QUERY_INVALID", "Bir ödeme, bir gün ya da arama gerekir.", 400);
        var all = (await rows.OrderBy(x => x.TransactionDate).ToListAsync(cancellationToken))
            .Where(PayoutBuilder.Counts)
            .Select(row => (Row: row, Line: PayoutBuilder.Line(row, today)))
            .Where(x => !string.IsNullOrEmpty(search) || !string.IsNullOrEmpty(query.PayoutId)
                || query.Kind == "PAID_DAY" && x.Line.Status == PayoutBuilder.Paid && x.Line.DueDay == query.Day
                || query.Kind != "PAID_DAY" && PayoutBuilder.Open(x.Line) && PayoutBuilder.Expect(x.Row, x.Line, today) is var expect && expect.Day == query.Day && (query.Kind is null || expect.Kind == query.Kind))
            .Select(x => x.Line)
            .Where(line => query.Region is null || !string.IsNullOrEmpty(search) || line.Region == query.Region)
            .ToList();
        var page = Math.Max(0, query.Page);
        return ServiceResult<PayoutLinesPage>.Ok(new(all.Skip(page * LinesPageSize).Take(LinesPageSize).ToList(), all.Count, page, LinesPageSize, all.Sum(x => x.Net)));
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
                Affiliate = x.Affiliate
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

internal sealed record PayoutOrderRow(Guid ConnectionId, string ExternalId, decimal Amount, DateTimeOffset? PayoutDate, string? RegionCode, string? RegionName);

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
}

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
            return Unknown;
        }
        // n11 names a day's state in a word its WSDL does not list; a word that says paid or waiting decides.
        if (row.Source == N11SettlementSyncJobHandler.Source)
        {
            var text = TrendyolFinanceCategorizer.Fold(row.TransactionType);
            if (text.Contains("odendi", StringComparison.Ordinal) || text.Contains("odeme yapildi", StringComparison.Ordinal) || text.Contains("paid", StringComparison.Ordinal)) return Paid;
            if (text.Contains("bekl", StringComparison.Ordinal) || text.Contains("waiting", StringComparison.Ordinal)) return Expected;
            // Otherwise its remittance day (havale tarihi, stored as the payment day) tells: past, n11 sent the money.
            return row.PaymentDate is { } remitted && Day(remitted) < today ? Paid : Expected;
        }
        // Hepsiburada says it: Paid, or WillBePaid (expected until its due day, then overdue).
        if (row.Source == HepsiburadaFinanceSyncJobHandler.Source && string.Equals(row.TransactionSubType, "Paid", StringComparison.OrdinalIgnoreCase)) return Paid;
        if (!string.IsNullOrEmpty(row.PaymentOrderId)) return Paid;
        return row.PaymentDate is { } due && Day(due) >= today ? Expected : Unknown;
    }

    public static PayoutLineView Line(PayoutRow row, DateOnly today) =>
        new(row.ExternalId, row.ConnectionId, row.PlatformCode, row.TransactionType, row.Category, row.TransactionDate, row.PaymentDate is { } due ? Day(due) : null,
            row.OrderNumber, row.InvoiceNumber, row.Country, Region(row.PlatformCode, row.Country, row.Affiliate), row.Credit - row.Debt, Commission(row), Net(row), Status(row, today), row.PaymentOrderId, row.Barcode);

    // The record's commission in lira. n11 sends a rate, not an amount: its line's commission is worked out from it
    // (N11SettlementSyncJobHandler.Commission; a row stored before 5 Nov 2026 holds the rate in CommissionAmount).
    internal static decimal? Commission(PayoutRow row) => row.Source == N11SettlementSyncJobHandler.Source
        ? N11SettlementSyncJobHandler.Commission(row.CommissionRate, row.CommissionRate is null ? row.CommissionAmount : null, row.Credit, row.Category)
        : row.CommissionAmount;

    // Whether a record still waits for its payout: not paid, whatever its due day (Avrupa is paid about monthly, so a
    // record past its due day may simply wait for the next payout).
    public static bool Open(PayoutLineView line) => line.Status is Expected or Unknown && line.DueDay is not null;

    // The day an open record is expected to be paid on. Türkiye-Körfez, as Trendyol paid it in October-November 2026:
    // Monday's payout carries the sales due Monday to Wednesday, Thursday's those due Thursday to Sunday, and a deduction
    // goes with the first payout on or after its due day. A public holiday moves a payout (29 Oct 2026 to the 30th), so
    // the day is an estimate. The other regions' payout days are not known: their records stand on their due day.
    // A day already past moves to the next one to come. Kind: PAYOUT_DAY (estimated payout day) or DUE_DAY.
    public static (DateOnly Day, string Kind) Expect(PayoutRow row, PayoutLineView line, DateOnly today)
    {
        var due = line.DueDay!.Value;
        // A record past its due day with no payout day known (Avrupa is paid about monthly, Pazarama when it says) waits
        // apart, OVERDUE, never in the next payout's estimate.
        // Hepsiburada pays every Tuesday the records due since the Tuesday before (Sep-Nov 2026: due Wednesday 28 Oct, paid
        // Tuesday 3 Nov; due on a Tuesday, paid that day), deductions with them.
        if (row.PlatformCode == "HEPSIBURADA")
        {
            var tuesday = WeekdayOnOrAfter(due, DayOfWeek.Tuesday);
            return (tuesday < today ? WeekdayOnOrAfter(today, DayOfWeek.Tuesday) : tuesday, "PAYOUT_DAY");
        }
        if (row.PlatformCode != "TRENDYOL" || line.Region != TrGulf) return due < today ? (today, "OVERDUE") : (due, "DUE_DAY");
        var day = row.Source == TrendyolFinanceRequestContract.Settlements ? PayoutDayOnOrBefore(due) : PayoutDayOnOrAfter(due);
        return (day < today ? PayoutDayOnOrAfter(today) : day, "PAYOUT_DAY");
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
        IReadOnlyList<PayoutCoverageView> coverage, IReadOnlyList<PendingOrderRow>? pendingOrders = null)
    {
        var lines = rows.Where(Counts).Select(row => (Row: row, Line: Line(row, today))).ToList();
        // The orders not delivered yet, of the stores in view, by marketplace and Trendyol region.
        var pending = (pendingOrders ?? []).Where(order => stores.ContainsKey(order.ConnectionId))
            .Select(order => (Platform: stores[order.ConnectionId].PlatformCode, Region: StoreFrontRegion(stores[order.ConnectionId].PlatformCode, order.StoreFrontCode), order.Amount))
            .ToList();
        // The balance: every record not paid yet (Trendyol's "Toplam Güncel Bakiye" is the same sum: 295,540.76 TL on
        // 4 Nov 2026, to the kuruş). Overdue: its part past the due day, still waiting for a payout.
        var open = lines.Where(x => Open(x.Line)).Select(x => (x.Row, x.Line, Expect: Expect(x.Row, x.Line, today))).ToList();
        var overdue = open.Where(x => x.Line.DueDay < today).ToList();

        var upcoming = open
            .GroupBy(x => (x.Row.ConnectionId, x.Expect.Day, x.Line.Region, x.Expect.Kind))
            .Select(group => Group($"due:{group.Key.ConnectionId:N}:{group.Key.Day:yyyy-MM-dd}:{group.Key.Region}:{group.Key.Kind}", group.Key.Kind, stores, group.Key.ConnectionId, group.Key.Region, null,
                group.Key.Day, group.Sum(x => x.Line.Net), null, group.Count(), Expected))
            .OrderBy(x => x.Day).ThenBy(x => x.PlatformCode).ThenBy(x => x.Region).ToList();

        var totals = rows.Where(row => IsHepsiburadaTotal(row) && row.PaymentDate is not null)
            .GroupBy(row => (row.ConnectionId, Day: Day(row.PaymentDate!.Value))).ToDictionary(group => group.Key, group => group.Sum(row => row.Credit - row.Debt));
        var paidLines = lines.Where(x => !string.IsNullOrEmpty(x.Row.PaymentOrderId))
            .GroupBy(x => (x.Row.ConnectionId, x.Row.PaymentOrderId!))
            .ToDictionary(group => group.Key, group => (Net: group.Sum(x => x.Line.Net), Count: group.Count()));
        var past = payouts.Where(x => x.PayoutDate is not null && stores.ContainsKey(x.ConnectionId))
            .Select(payout =>
            {
                var read = paidLines.TryGetValue((payout.ConnectionId, payout.ExternalId), out var sum) ? sum : ((decimal Net, int Count)?)null;
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
            .OrderByDescending(x => x.Day).ThenBy(x => x.PlatformCode).ToList();

        var regions = new[] { TrGulf, Europe, Azerbaijan, UnknownRegion }
            .Select(region =>
            {
                var due = open.Where(x => x.Line.Region == region).ToList();
                var next = upcoming.Where(x => x.Region == region && x.Kind != "OVERDUE").GroupBy(x => x.Day).OrderBy(x => x.Key).FirstOrDefault(day => day.Sum(x => x.Amount) > 0);
                var waiting = pending.Where(x => x.Region == region).ToList();
                return new PayoutRegionView(region, due.Sum(x => x.Line.Net), due.Count, next?.Key, next?.Sum(x => x.Amount),
                    overdue.Where(x => x.Line.Region == region).Sum(x => x.Line.Net), waiting.Sum(x => x.Amount), waiting.Count);
            })
            .Where(x => x.Region != UnknownRegion || x.Records > 0 || x.Unknown != 0 || x.PendingDeliveryOrders > 0)
            .ToList();
        // Every marketplace with a store in view, in the platforms' order; the region totals above are Trendyol's alone.
        var marketplaces = stores.Values.Select(store => store.PlatformCode).Where(code => PayoutPlatformOrder.Contains(code)).Distinct()
            .OrderBy(code => Array.IndexOf(PayoutPlatformOrder, code))
            .Select(code =>
            {
                var due = open.Where(x => x.Row.PlatformCode == code).ToList();
                var next = upcoming.Where(x => x.PlatformCode == code && x.Kind != "OVERDUE").GroupBy(x => x.Day).OrderBy(x => x.Key).FirstOrDefault(day => day.Sum(x => x.Amount) > 0);
                var waiting = pending.Where(x => x.Platform == code).ToList();
                return new PayoutPlatformView(code, due.Sum(x => x.Line.Net), due.Count, next?.Key, next?.Sum(x => x.Amount),
                    overdue.Where(x => x.Row.PlatformCode == code).Sum(x => x.Line.Net), waiting.Sum(x => x.Amount), waiting.Count);
            })
            .ToList();

        var early = rows.Where(x => x.Source == MarketplaceFinanceSources.MoneyMovements && string.Equals(x.RequestedType, "CashAdvance", StringComparison.OrdinalIgnoreCase)
            && Day(x.TransactionDate) >= today.AddDays(-daysBack)).ToList();
        // The next payout: the first day to come whose estimate is above zero (a day that nets to nothing or less pays
        // nothing: the marketplace takes it off a later payout), the waiting overdue records left out.
        var nextDay = upcoming.Where(x => x.Kind != "OVERDUE").GroupBy(x => x.Day).OrderBy(x => x.Key).FirstOrDefault(day => day.Sum(x => x.Amount) > 0)?.Key;
        var next = nextDay is { } first
            ? upcoming.Where(x => x.Day == first && x.Kind != "OVERDUE").GroupBy(x => 1).Select(group => group.First() with { Key = $"next:{first:yyyy-MM-dd}", Amount = group.Sum(x => x.Amount), Records = group.Sum(x => x.Records) }).Single()
            : null;
        return new(today, daysBack, open.Sum(x => x.Line.Net), open.Count, next, past.FirstOrDefault(x => x.Kind == "PAYOUT") ?? past.FirstOrDefault(),
            overdue.Sum(x => x.Line.Net), overdue.Count, early.Sum(x => Math.Abs(x.Credit - x.Debt)), early.Count, regions, marketplaces, upcoming, past, coverage,
            pending.Sum(x => x.Amount), pending.Count);
    }

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
