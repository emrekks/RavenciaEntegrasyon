namespace MarketplaceHub.Application;

// İşlem Takibi › İşler › an iş's "Gidenler ve Değişenler": what a price/stock job sent, line by line, and what a
// product import changed in the panel, read only from the records that already keep it (the price/stock send log and
// Ürün Hareketleri). Read-only: it starts no send and changes no setting.
public static class JobChangeModes
{
    // The price/stock send log rows of the job itself (PRICE_INVENTORY_SYNC) or of the send jobs it queued (a stock
    // reconcile or a stock dispatch).
    public const string SendLog = "SEND_LOG";
    // A product import's price and stock changes in the panel, from Ürün Hareketleri: the import's own connection, within
    // the job's run (the records keep no job id; the window and the connection name the job).
    public const string ProductImport = "PRODUCT_IMPORT";
    // A job type that sends or changes no price or stock.
    public const string None = "NONE";
}

// Why a stock/price job shows no line: what the job itself recorded, never a guess.
public static class JobChangeOutcomes
{
    // The job finished without queuing a send (and nothing says it held one back).
    public const string NoChanges = "NO_CHANGES";
    // The connection's automatic price/stock writes (or its master write switch) are off.
    public const string AutomaticWritesOff = "AUTOMATIC_WRITES_OFF";
    // The send waits for an order read that started after the change (owner decision C).
    public const string OrderReadPending = "ORDER_READ_PENDING";
    // An earlier automatic send of the connection was still on its way; the next pass goes on.
    public const string PushInFlight = "PUSH_IN_FLIGHT";
    // The stock was held because the price write policy is off (the request carries the price too).
    public const string PricePolicyHeld = "PRICE_POLICY_HELD";
    // The price or stock moved to a newer version before the send; the old values were not sent.
    public const string Superseded = "SUPERSEDED";
    // The job has not finished; its lines are not known yet.
    public const string Running = "RUNNING";
}

// The summary above the list. A null count is not known for this job (never shown as 0).
public sealed record JobChangeCounts(
    int? Scanned,
    int? Sent,
    int? Applied,
    int? Unchanged,
    int? Pending,
    int? Rejected,
    int? Uncertain,
    int? Superseded,
    int? PriceChanged,
    int? StockChanged);

// A send job a scan or a dispatch queued: a link to its own detail.
public sealed record JobSpawnedJobView(Guid Id, string JobType, string Status, DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt, string? LastErrorCode);

// The section's head: what kind of list it is, why it is empty when the job recorded why, the counts and the send jobs.
// SourceTrigger: why the send went (PriceStockSendTriggers); SourceOrderNumber: the order whose line queued a stock
// dispatch (the number only, nothing about the buyer).
public sealed record JobChangesSummaryView(
    string Mode,
    string? Outcome,
    JobChangeCounts Counts,
    IReadOnlyList<JobSpawnedJobView> SpawnedJobs,
    string? SourceTrigger,
    string? SourceOrderNumber);

// The kinds of a line (JobChangeLineView.Kind).
public static class JobChangeLineKinds
{
    public const string Stock = "STOCK";
    public const string Price = "PRICE";
    // A price/stock request line carries both.
    public const string StockAndPrice = "STOCK_PRICE";
}

// One line of the list. A send line: the values the request carried (New*) against those the marketplace had last
// confirmed for the same code (Previous*, null: not known), its Source (PriceStockSendTriggers) and Result
// (PriceStockSendResults), JobId the send job. A product import's change: the panel's stock (on-hand before → after) or
// the store price before → after, Source as Ürün Hareketleri names it and Result CHANGED. A null New* was not sent.
public sealed record JobChangeLineView(
    Guid Id,
    DateTimeOffset OccurredAt,
    string Kind,
    Guid? ProductId,
    string? ProductTitle,
    string? ImageUrl,
    string? ModelCode,
    string? Barcode,
    string? StockCode,
    string? OptionText,
    decimal? PreviousQuantity,
    decimal? NewQuantity,
    decimal? PreviousSalePrice,
    decimal? NewSalePrice,
    decimal? PreviousListPrice,
    decimal? NewListPrice,
    string? Currency,
    string Source,
    string Result,
    string? ErrorCode,
    string? ErrorMessage,
    Guid? ConnectionId,
    string? PlatformCode,
    string? StoreName,
    Guid? JobId);

public static class JobChangeResults
{
    // A product import's change (the panel took the marketplace's value).
    public const string Changed = "CHANGED";
}

// The summary's counts that also filter the list (JobChangesQuery.Groups), named as JobChangeCounts names them: a send
// job's results (applied, unchanged, pending, rejected, uncertain, superseded) and an import's kinds (priceChanged,
// stockChanged). A group of the other mode matches nothing.
public static class JobChangeGroups
{
    public const string Applied = "applied";
    public const string Unchanged = "unchanged";
    public const string Pending = "pending";
    public const string Rejected = "rejected";
    public const string Uncertain = "uncertain";
    public const string Superseded = "superseded";
    public const string PriceChanged = "priceChanged";
    public const string StockChanged = "stockChanged";

    public static readonly IReadOnlyList<string> All = [Applied, Unchanged, Pending, Rejected, Uncertain, Superseded, PriceChanged, StockChanged];

    // The send log results a send group counts (JobChangesService.SummaryAsync counts them the same way).
    public static IReadOnlyList<string> SendResults(string group) => group switch
    {
        Applied => [PriceStockSendResults.Applied],
        Unchanged => [PriceStockSendResults.Unchanged],
        Pending => [PriceStockSendResults.Submitted],
        Rejected => [PriceStockSendResults.Rejected, PriceStockSendResults.Failed],
        Uncertain => [PriceStockSendResults.Unknown],
        Superseded => [PriceStockSendResults.Superseded],
        _ => []
    };

    // The Ürün Hareketleri kind an import group counts.
    public static string? ActivityKind(string group) => group switch
    {
        PriceChanged => ProductActivityKinds.Price,
        StockChanged => ProductActivityKinds.Stock,
        _ => null
    };

    // The known groups among the given ones (case-insensitive); null: no filter; empty: only unknown names, nothing matches.
    public static IReadOnlyList<string>? Parse(IReadOnlyList<string>? values)
    {
        var given = (values ?? []).Select(value => value.Trim()).Where(value => value.Length > 0).ToArray();
        if (given.Length == 0) return null;
        return All.Where(group => given.Contains(group, StringComparer.OrdinalIgnoreCase)).ToArray();
    }
}

// Two ways to page, the same rows in the same order (newest first, then by id, so no line shows on two pages):
//   - the cursor (the panel's list so far): limit 1-50 (default 50), after the previous page's NextCursor;
//   - page numbers: page (1-based) with pageSize 1-200 (default 25), when either is given.
// search names a barcode, stock code, model code or title; groups keeps the lines of the summary's counts it names.
public sealed record JobChangesQuery(int? Limit = null, string? After = null, string? Search = null, int? Page = null, int? PageSize = null, IReadOnlyList<string>? Groups = null)
{
    public const int DefaultLimit = 50;
    public const int MaximumLimit = 50;
    public const int DefaultPageSize = 25;
    public const int MaximumPageSize = 200;
    public const int MaximumPage = 100_000;
    public const int MaximumSpawnedJobs = 20;
    // The most lines an Excel export carries; a larger list is refused with a reason (narrow it with search or groups).
    public const int MaximumExportRows = 100_000;
    public int EffectiveLimit => Math.Clamp(Limit ?? DefaultLimit, 1, MaximumLimit);
    public bool Numbered => Page is not null || PageSize is not null;
    public int EffectivePage => Math.Clamp(Page ?? 1, 1, MaximumPage);
    public int EffectivePageSize => Math.Clamp(PageSize ?? DefaultPageSize, 1, MaximumPageSize);
    public int Skip => (EffectivePage - 1) * EffectivePageSize;
}

// The list's page: the cursor members (NextCursor, HasMore) as before, and TotalCount, every line the search and groups
// keep. Page and PageSize are the numbered page's (null on a cursor page). On a numbered page NextCursor is null and
// HasMore says whether a later page exists.
public sealed record JobChangesPageView(IReadOnlyList<JobChangeLineView> Items, string? NextCursor, bool HasMore, int? Page, int? PageSize, int TotalCount);

// One numbered page of a list and the count of every row its filters keep.
public sealed record CountedPage<T>(IReadOnlyList<T> Items, int TotalCount);

// A workbook written to a temporary file (deleted when the stream is closed): its download name and its content.
public sealed record JobExportFile(string FileName, Stream Content);

public interface IJobChangesService
{
    Task<ServiceResult<JobChangesSummaryView>> SummaryAsync(Guid tenantId, Guid jobId, CancellationToken cancellationToken);

    Task<ServiceResult<JobChangesPageView>> PageAsync(Guid tenantId, Guid jobId, JobChangesQuery query, CancellationToken cancellationToken);

    // Every line the query's search and groups keep, as an .xlsx (Limit, After, Page and PageSize are ignored).
    Task<ServiceResult<JobExportFile>> ExportAsync(Guid tenantId, Guid jobId, JobChangesQuery query, CancellationToken cancellationToken);
}
