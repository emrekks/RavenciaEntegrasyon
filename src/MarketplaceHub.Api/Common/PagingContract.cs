using MarketplaceHub.Application;

namespace MarketplaceHub.Api.Common;

// The API's paging contract (API-GUVENLIK-8), for new endpoints and for the later wave that moves the panel and the
// API onto it together. Today four formats are on the wire; none may change while an open browser tab still sends
// the old parameters, so existing endpoints keep theirs:
//   1. Cursor: ?limit=&after= → { items, nextCursor, hasMore, totalCount? } (Application.PageResult<T>): products,
//      catalog lists, connections, orders, returns, shipments, invoices, imports, jobs/page, price-stock-send-log,
//      product-activity, notifications. limit 1-200, default 50 (EndpointHelpers.PageSize); jobs/page, the send log,
//      the activity log and the notifications take limit unchecked here and clamp it in their services (1000, 100, 100,
//      100). Orders and returns also take a 1-based ?page= next to the cursor ("cursor+page").
//   2. Page number + limit: ?page=&limit= → { items, page, limit | pageSize, totalCount, ... }: questions
//      (MarketplaceQuestionListPage: page, limit), reports/order-profitability (ReportOrderProfitPage: page,
//      pageSize, limit ≤ 200), commission tariff rows (CommissionTariffRowPage).
//   3. pageNumber + pageSize: invoice-workspace/page → { items, totalCount, pageNumber, pageSize, totalPages, counts }.
//   4. Page only: reports/payouts/lines ?page= → { items, totalCount, page, pageSize, net } (fixed page size).
// The OpenAPI document names each list's format (x-paging) from the table below, with every parameter's default and range.
// The contract below is format 1 for lists and NumberedPage for page-numbered reports; neither is adopted by an
// existing endpoint here, because each would change parameter names or member names the panel sends or reads.

/// <summary>
/// A paging query parameter as the endpoint reads it: refused (400) outside Minimum-Maximum when <see cref="Refused"/>,
/// else brought into the range by the service; <see cref="Default"/> when omitted.
/// </summary>
public readonly record struct PagingRange(int Minimum, int? Maximum, int? Default, bool Refused = true, string? Note = null);

/// <summary>One of the paging formats on the wire, as the OpenAPI document names it (x-paging).</summary>
public readonly record struct PagingFormat(string Name, IReadOnlyDictionary<string, PagingRange> Ranges)
{
    public PagingRange? RangeOf(string parameter) => Ranges.TryGetValue(parameter, out var range) ? range : null;
}

public static class PagingContract
{
    public const string Cursor = "cursor";
    public const string CursorWithPage = "cursor+page";
    public const string PageLimit = "page+limit";
    public const string PageNumberPageSize = "pageNumber+pageSize";
    public const string PageOnly = "page";

    private static readonly PagingRange CheckedLimit = new(1, EndpointHelpers.MaximumPageSize, EndpointHelpers.DefaultPageSize);

    private static PagingFormat Format(string name, params (string Parameter, PagingRange Range)[] ranges) =>
        new(name, ranges.ToDictionary(entry => entry.Parameter, entry => entry.Range, StringComparer.Ordinal));

    // Every list operation that pages, by its route in the document, with the parameters exactly as its handler and
    // service read them today (15 Nov 2026). ApiPagingContractTests holds this table equal to the document's query
    // parameters, so an endpoint that starts paging must be added here.
    private static readonly Dictionary<string, PagingFormat> Lists = new(StringComparer.Ordinal)
    {
        // 1. Cursor, limit checked by EndpointHelpers.PageSize.
        ["/api/v1/catalog/attributes"] = Format(Cursor, ("limit", CheckedLimit)),
        ["/api/v1/catalog/brands"] = Format(Cursor, ("limit", CheckedLimit)),
        ["/api/v1/catalog/categories"] = Format(Cursor, ("limit", CheckedLimit)),
        ["/api/v1/connections"] = Format(Cursor, ("limit", CheckedLimit)),
        ["/api/v1/imports"] = Format(Cursor, ("limit", CheckedLimit)),
        ["/api/v1/imports/{id}/candidates"] = Format(Cursor, ("limit", CheckedLimit)),
        ["/api/v1/inventory"] = Format(Cursor, ("limit", CheckedLimit)),
        ["/api/v1/invoices"] = Format(Cursor, ("limit", CheckedLimit)),
        ["/api/v1/products"] = Format(Cursor, ("limit", CheckedLimit)),
        ["/api/v1/shipments"] = Format(Cursor, ("limit", CheckedLimit)),
        // 1. Cursor, limit brought into range by the service (JobListQuery, PanelNotificationQuery, PriceStockSendLogQuery, ProductActivityQuery).
        ["/api/v1/jobs/page"] = Format(Cursor, ("limit", new(1, JobListQuery.MaximumLimit, JobListQuery.DefaultLimit, Refused: false))),
        ["/api/v1/notifications"] = Format(Cursor, ("limit", new(1, 100, 50, Refused: false))),
        ["/api/v1/price-stock-send-log"] = Format(Cursor, ("limit", new(1, PriceStockSendLogQuery.MaximumLimit, PriceStockSendLogQuery.DefaultLimit, Refused: false))),
        ["/api/v1/product-activity"] = Format(Cursor, ("limit", new(1, ProductActivityQuery.MaximumLimit, ProductActivityQuery.DefaultLimit, Refused: false))),
        // 1b. Cursor that also takes a 1-based page (the card lists jump to a page; the answer is still PageResult).
        ["/api/v1/orders"] = Format(CursorWithPage, ("limit", CheckedLimit), ("page", new(1, EndpointHelpers.MaximumPageNumber, 1))),
        ["/api/v1/returns"] = Format(CursorWithPage, ("limit", CheckedLimit), ("page", new(1, EndpointHelpers.MaximumPageNumber, 1))),
        // 2. Page number + limit.
        ["/api/v1/questions"] = Format(PageLimit, ("page", new(1, null, 1, Refused: false)), ("limit", new(1, 100, 50, Refused: false))),
        ["/api/v1/reports/order-profitability"] = Format(PageLimit, ("page", new(0, 100_000, 0)), ("limit", new(1, 200, 50))),
        ["/api/v1/connections/{connectionId}/commission-tariffs/{tariffId}/rows"] = Format(PageLimit, ("page", new(0, 100_000, 0)), ("limit", new(1, 200, 50))),
        // 3. pageNumber + pageSize.
        ["/api/v1/invoice-workspace/page"] = Format(PageNumberPageSize, ("pageNumber", new(1, null, 1, Refused: false)), ("pageSize", new(20, 200, 20, Refused: false, Note: "20, 50, 100 or 200; any other value reads as 20."))),
        // 4. Page only, a fixed page size.
        ["/api/v1/reports/payouts/lines"] = Format(PageOnly, ("page", new(0, 10_000, 0)))
    };

    /// <summary>The lists that page, by route.</summary>
    public static IReadOnlyDictionary<string, PagingFormat> PagedLists => Lists;

    /// <summary>The paging format of a list operation by its document route; null for an operation that pages nothing.</summary>
    public static PagingFormat? FormatOf(string path, IReadOnlySet<string> query) => Lists.TryGetValue(path, out var format) ? format : null;
}

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
