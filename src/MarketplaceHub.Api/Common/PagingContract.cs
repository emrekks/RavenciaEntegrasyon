using MarketplaceHub.Application;

namespace MarketplaceHub.Api.Common;

// The API's paging contract (API-GUVENLIK-8), for new endpoints and for the later wave that moves the panel and the
// API onto it together. Today four formats are on the wire; none may change while an open browser tab still sends
// the old parameters, so existing endpoints keep theirs:
//   1. Cursor: ?limit=&after= → { items, nextCursor, hasMore, totalCount? } (Application.PageResult<T>): products,
//      catalog lists, connections, orders, returns, shipments, invoices, imports, jobs/page, price-stock-send-log,
//      product-activity. limit 1-200, default 50 (EndpointHelpers.PageSize); the send log, the activity log and GET /jobs
//      (not /jobs/page) take limit unchecked here and cap it in their services (100, 100, 1000 per the 15 Nov scan).
//   2. Page number + limit: ?page=&limit= → { items, page, limit | pageSize, totalCount, ... }: questions
//      (MarketplaceQuestionListPage: page, limit), reports/order-profitability (ReportOrderProfitPage: page,
//      pageSize, limit ≤ 200), commission tariff rows (CommissionTariffRowPage).
//   3. pageNumber + pageSize: invoice-workspace/page → { items, totalCount, pageNumber, pageSize, totalPages, counts }.
//   4. Page only: reports/payouts/lines ?page= → { items, totalCount, page, pageSize, net } (fixed page size).
// The contract below is format 1 for lists and NumberedPage for page-numbered reports; neither is adopted by an
// existing endpoint here, because each would change parameter names or member names the panel sends or reads.

/// <summary>A cursor list request: up to <see cref="Limit"/> items after the opaque <see cref="After"/> cursor.</summary>
public readonly record struct CursorPageRequest(int Limit, string? After)
{
    /// <summary>From the query's limit and after: limit 1-200, 50 when omitted (400 INVALID_REQUEST otherwise).</summary>
    public static CursorPageRequest From(int? limit, string? after) => new(EndpointHelpers.PageSize(limit), string.IsNullOrWhiteSpace(after) ? null : after);
}

/// <summary>A page-numbered request: the 1-based <see cref="Page"/> of <see cref="PageSize"/> items.</summary>
public readonly record struct NumberedPageRequest(int Page, int PageSize)
{
    /// <summary>From the query's page and pageSize: page 1-100000 (1 when omitted), pageSize as a cursor list's limit.</summary>
    public static NumberedPageRequest From(int? page, int? pageSize) => new(EndpointHelpers.PageNumber(page), EndpointHelpers.PageSize(pageSize));

    public int Skip => (Page - 1) * PageSize;
}

/// <summary>A page-numbered answer: { items, page, pageSize, totalCount }.</summary>
public sealed record NumberedPage<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount);

/// <summary>The cursor list answer is Application's PageResult: { items, nextCursor, hasMore, totalCount? }.</summary>
public static class CursorPage
{
    public static PageResult<T> Of<T>(IReadOnlyList<T> items, string? nextCursor, int? totalCount = null) => new(items, nextCursor, nextCursor is not null, totalCount);
}
