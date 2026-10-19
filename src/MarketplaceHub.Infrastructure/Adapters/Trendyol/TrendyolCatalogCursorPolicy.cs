using MarketplaceHub.Application;

namespace MarketplaceHub.Infrastructure.Adapters.Trendyol;

// A catalog read runs in passes, each with its own page cursor:
//  1. approved products (by variant change when incremental),
//  2. approved products whose content changed (incremental reads only: a title, description, image or
//     attribute change approved after review does not move the variant date),
//  3. products waiting for approval (when asked for).
// The cursor carries the pass as a prefix; a cursor without one is the approved pass.
internal enum TrendyolCatalogPass { Approved, ContentChanged, PendingApproval }

internal static class TrendyolCatalogCursorPolicy
{
    private const string ApprovedPrefix = "approved:";
    private const string ContentPrefix = "content:";
    private const string PendingApprovalPrefix = "pending:";

    public static (TrendyolCatalogPass Pass, string? Cursor) Resolve(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor)) return (TrendyolCatalogPass.Approved, cursor);
        if (cursor.StartsWith(PendingApprovalPrefix, StringComparison.Ordinal)) return (TrendyolCatalogPass.PendingApproval, cursor[PendingApprovalPrefix.Length..]);
        if (cursor.StartsWith(ContentPrefix, StringComparison.Ordinal)) return (TrendyolCatalogPass.ContentChanged, cursor[ContentPrefix.Length..]);
        if (cursor.StartsWith(ApprovedPrefix, StringComparison.Ordinal)) return (TrendyolCatalogPass.Approved, cursor[ApprovedPrefix.Length..]);
        return (TrendyolCatalogPass.Approved, cursor);
    }

    public static AdapterPageResult<T> Advance<T>(AdapterPageResult<T> page, TrendyolCatalogPass pass, bool incremental, bool includePendingApproval)
    {
        // A single-pass read keeps the marketplace cursor as it is.
        if (!incremental && !includePendingApproval) return page;
        if (page.HasMore && string.IsNullOrWhiteSpace(page.NextCursor)) return page;

        string? nextCursor;
        if (page.NextCursor is not null) nextCursor = Prefix(pass) + page.NextCursor;
        else
        {
            var nextPass = Next(pass, incremental, includePendingApproval);
            nextCursor = nextPass is { } following ? Prefix(following) + "p:0" : null;
        }
        return page with { NextCursor = nextCursor, HasMore = page.HasMore || nextCursor is not null };
    }

    private static TrendyolCatalogPass? Next(TrendyolCatalogPass pass, bool incremental, bool includePendingApproval) => pass switch
    {
        TrendyolCatalogPass.Approved when incremental => TrendyolCatalogPass.ContentChanged,
        TrendyolCatalogPass.Approved or TrendyolCatalogPass.ContentChanged when includePendingApproval => TrendyolCatalogPass.PendingApproval,
        _ => null
    };

    private static string Prefix(TrendyolCatalogPass pass) => pass switch
    {
        TrendyolCatalogPass.ContentChanged => ContentPrefix,
        TrendyolCatalogPass.PendingApproval => PendingApprovalPrefix,
        _ => ApprovedPrefix
    };
}
