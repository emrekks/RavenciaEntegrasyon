using MarketplaceHub.Application;

namespace MarketplaceHub.Infrastructure.Adapters.Trendyol;

internal static class TrendyolCatalogCursorPolicy
{
    private const string ApprovedPrefix = "approved:";
    private const string PendingApprovalPrefix = "pending:";

    public static (bool IsPendingApproval, string? Cursor) Resolve(bool includePendingApproval, string? cursor)
    {
        if (!includePendingApproval || string.IsNullOrWhiteSpace(cursor)) return (false, cursor);
        if (cursor.StartsWith(PendingApprovalPrefix, StringComparison.Ordinal))
            return (true, cursor[PendingApprovalPrefix.Length..]);
        if (cursor.StartsWith(ApprovedPrefix, StringComparison.Ordinal))
            return (false, cursor[ApprovedPrefix.Length..]);
        return (false, cursor);
    }

    public static AdapterPageResult<T> Advance<T>(AdapterPageResult<T> page, bool includePendingApproval, bool isPendingApprovalPage)
    {
        if (!includePendingApproval) return page;
        if (page.HasMore && string.IsNullOrWhiteSpace(page.NextCursor)) return page;

        var nextCursor = page.NextCursor is not null
            ? (isPendingApprovalPage ? PendingApprovalPrefix : ApprovedPrefix) + page.NextCursor
            : isPendingApprovalPage ? null : PendingApprovalPrefix + "p:0";
        return page with { NextCursor = nextCursor, HasMore = page.HasMore || nextCursor is not null };
    }
}
