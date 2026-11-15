namespace MarketplaceHub.Infrastructure.Persistence;

// ISLEYICI-11 (15 Nov 2026): the page walk of a read that runs its pages within one job run. The caller reads and stores
// each page and says which cursor comes next (each marketplace keeps its own rule: HasMore with NextCursor, a page
// number, a rewalk); this loop only decides whether to go on, with explicit guards, and says why it stopped so the
// caller handles every stop on purpose. Nothing here stops silently: a page cap or a repeated page is returned as such,
// and what it means (an issue, a failure, a range left unread) stays the caller's decision.
internal static class PagedReadLoop
{
    // readPage(cursor, pageIndex): reads and stores one page, returns the next cursor (null: no more pages) and the page's
    // key for the repeated-page guard, or null to stop the walk itself (a failure it handled).
    public static async Task<PagedReadEnd> RunAsync(string? start, PageGuard guard, Func<string?, int, Task<PageStep?>> readPage, CancellationToken cancellationToken)
    {
        var cursor = start;
        string? previousKey = null;
        for (var pagesRead = 0; ; pagesRead++)
        {
            // The cap is checked before a read: a walk with more pages after MaxPages pages stops without reading them.
            if (guard.MaxPages is { } cap && pagesRead >= cap) return new(PagedReadStop.PageCap, cursor, pagesRead);
            var step = await readPage(cursor, pagesRead);
            if (step is not { } page) return new(PagedReadStop.StoppedByReader, cursor, pagesRead + 1);
            if (guard.PageRepeated is { } repeated && repeated(previousKey, page.PageKey)) return new(PagedReadStop.RepeatedPage, cursor, pagesRead + 1);
            previousKey = page.PageKey;
            if (page.Next is null) return new(PagedReadStop.Completed, null, pagesRead + 1);
            if (guard.StopOnRepeatedCursor && string.Equals(page.Next, cursor, StringComparison.Ordinal)) return new(PagedReadStop.RepeatedCursor, cursor, pagesRead + 1);
            cursor = page.Next;
            if (guard.StopOnCancellation && cancellationToken.IsCancellationRequested) return new(PagedReadStop.Cancelled, cursor, pagesRead + 1);
        }
    }
}

// MaxPages: the most pages one walk reads (null: no cap). StopOnRepeatedCursor: a next cursor equal to the one just read
// ends the walk. PageRepeated(previousKey, key): a page that repeats the one before it (a page number the marketplace
// ignores) ends the walk. StopOnCancellation: a cancelled run stops between two pages (false: the next read runs with the
// cancelled token and fails there, for a walk whose caller would otherwise take the stop for a finished read).
internal sealed record PageGuard(int? MaxPages = null, bool StopOnRepeatedCursor = false, Func<string?, string?, bool>? PageRepeated = null, bool StopOnCancellation = true);

internal readonly record struct PageStep(string? Next, string? PageKey = null);

// Cursor: the first cursor left unread at a page cap or a cancellation, the cursor just read at any other stop (null
// when the walk completed); PagesRead: the pages read, counting the last one.
internal readonly record struct PagedReadEnd(PagedReadStop Stop, string? Cursor, int PagesRead);

internal enum PagedReadStop
{
    // The last page said there is no next one.
    Completed,
    // MaxPages pages were read and there are more.
    PageCap,
    // The next cursor was the one just read.
    RepeatedCursor,
    // The page repeated the page before it.
    RepeatedPage,
    // The run was cancelled between two pages.
    Cancelled,
    // The page reader stopped the walk itself.
    StoppedByReader
}
