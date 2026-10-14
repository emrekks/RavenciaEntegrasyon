using MarketplaceHub.Application;

namespace MarketplaceHub.Infrastructure.Persistence;

internal sealed record MarketplaceQuestionLiveCursor(int StatusIndex, int KindIndex, int PageIndex);

internal static class MarketplaceQuestionLiveCursorPolicy
{
    public static MarketplaceQuestionLiveCursor FirstPage(int pageBase) => new(0, 0, pageBase);

    public static MarketplaceQuestionLiveCursor? Advance(
        MarketplaceQuestionLiveCursor cursor,
        MarketplaceQuestionPage result,
        int pageSize,
        int pageBase,
        int statusCount,
        int kindCount)
    {
        var zeroBasedPageIndex = Math.Max(0, cursor.PageIndex - pageBase);
        var hasAnotherPage = result.TotalPages <= 0 || zeroBasedPageIndex + 1 < result.TotalPages;
        if (result.Items.Count >= pageSize && hasAnotherPage)
            return cursor with { PageIndex = cursor.PageIndex + 1 };

        if (cursor.StatusIndex + 1 < statusCount)
            return new(cursor.StatusIndex + 1, cursor.KindIndex, pageBase);

        if (cursor.KindIndex + 1 < kindCount)
            return new(0, cursor.KindIndex + 1, pageBase);

        return null;
    }
}
