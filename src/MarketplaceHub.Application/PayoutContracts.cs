namespace MarketplaceHub.Application;

// Hakediş: what the marketplaces will pay and have paid, from their own accounting records (Trendyol's current account
// statement and payouts, Pazarama's order payment agreements). Amounts are TRY. Nothing here is a marketplace balance:
// "expected" is the sum of the records not paid yet as they stand, an estimate until it matches the marketplace's screen.
public sealed record PayoutQuery(IReadOnlyList<string>? PlatformCodes, IReadOnlyList<Guid>? ConnectionIds, bool IncludeTest);

// The records of one group (a store's due day in a region, or one payout), or every record matching a search.
public sealed record PayoutLinesQuery(Guid? ConnectionId, string? PayoutId, DateOnly? Day, string? Region, string? Search, int Page);

// Expected: the balance, every record not paid yet (Trendyol's "Toplam Güncel Bakiye" is the same sum). Unknown: its
// part whose due day has passed with no payout seen yet (not "unpaid": Avrupa is paid about monthly, and a payout is
// read a day after it is made). EarlyPaymentsTaken: the early payments (Trendyol CashAdvance) of the last DaysBack days.
public sealed record PayoutView(
    DateOnly Today,
    int DaysBack,
    decimal Expected,
    int ExpectedRecords,
    PayoutGroupView? Next,
    PayoutGroupView? LastPaid,
    decimal Unknown,
    int UnknownRecords,
    decimal EarlyPaymentsTaken,
    int EarlyPayments,
    IReadOnlyList<PayoutRegionView> Regions,
    IReadOnlyList<PayoutGroupView> Upcoming,
    IReadOnlyList<PayoutGroupView> Past,
    IReadOnlyList<PayoutCoverageView> Coverage);

// Region: TR_GULF (Türkiye and the Gulf storefronts), EUROPE, AZ, UNKNOWN (a country no rule names).
public sealed record PayoutRegionView(string Region, decimal Expected, int Records, DateOnly? NextDay, decimal? NextAmount, decimal Unknown);

// Kind PAYOUT_DAY: a store's open records expected on an estimated payout day in a region (Trendyol Türkiye-Körfez);
// DUE_DAY: on their due day (the other regions and marketplaces; Status EXPECTED, an estimate; for Pazarama's paid
// agreements Status PAID). Kind PAYOUT: a payout the marketplace made (Status PAID); Amount is the marketplace's own figure, LinesAmount what
// the records it paid add up to (null when none was read yet). RegionName: as the marketplace named the payout's region.
public sealed record PayoutGroupView(string Key, string Kind, Guid ConnectionId, string PlatformCode, string StoreName, string Region, string? RegionName,
    DateOnly Day, decimal Amount, decimal? LinesAmount, int Records, string Status);

// State READ: the store's records are read (LastSyncedAt: the last finished read); NOT_READ: not yet; NOT_SUPPORTED:
// the marketplace gives no payout records to read (or they are not read yet in this version).
public sealed record PayoutCoverageView(Guid ConnectionId, string PlatformCode, string StoreName, string State, DateTimeOffset? LastSyncedAt);

// One record. Net: what it adds to (or takes from) the payout. Status PAID, EXPECTED or UNKNOWN as in PayoutView.
public sealed record PayoutLineView(string ExternalId, Guid ConnectionId, string PlatformCode, string? TransactionType, string Category, DateTimeOffset TransactionDate,
    DateOnly? DueDay, string? OrderNumber, string? InvoiceNumber, string? Country, string Region, decimal Gross, decimal? Commission, decimal Net, string Status, string? PaymentOrderId);

public sealed record PayoutLinesPage(IReadOnlyList<PayoutLineView> Items, int TotalCount, int Page, int PageSize, decimal Net);

public interface IPayoutsService
{
    Task<ServiceResult<PayoutView>> SummaryAsync(Guid tenantId, PayoutQuery query, CancellationToken cancellationToken);
    Task<ServiceResult<PayoutLinesPage>> LinesAsync(Guid tenantId, PayoutQuery scope, PayoutLinesQuery query, CancellationToken cancellationToken);
}
