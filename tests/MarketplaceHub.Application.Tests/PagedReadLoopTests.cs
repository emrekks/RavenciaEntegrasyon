using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// ISLEYICI-11 (15 Nov 2026): the shared page walk and its guards.
public sealed class PagedReadLoopTests
{
    // pages[i] is the next cursor page i gives (null: the last page); keys[i] the page's key.
    private static async Task<(PagedReadEnd End, List<string?> Read)> WalkAsync(PageGuard guard, string?[] next, string?[]? keys = null, CancellationTokenSource? cancel = null, int? cancelAfter = null)
    {
        var read = new List<string?>();
        var end = await PagedReadLoop.RunAsync(null, guard, (cursor, index) =>
        {
            read.Add(cursor);
            if (cancelAfter == index) cancel!.Cancel();
            return Task.FromResult<PageStep?>(new PageStep(next[Math.Min(index, next.Length - 1)], keys?[Math.Min(index, keys.Length - 1)]));
        }, cancel?.Token ?? CancellationToken.None);
        return (end, read);
    }

    [Fact]
    public async Task AWalkEndsWhenAPageNamesNoNextCursor()
    {
        var (end, read) = await WalkAsync(new PageGuard(), ["2", "3", null]);
        Assert.Equal(PagedReadStop.Completed, end.Stop);
        Assert.Equal([null, "2", "3"], read);
        Assert.Equal(3, end.PagesRead);
        Assert.Null(end.Cursor);
    }

    [Fact]
    public async Task ThePageCapStopsBeforeTheNextRead_AndSaysSo()
    {
        var (end, read) = await WalkAsync(new PageGuard(MaxPages: 3), ["2", "3", "4", "5", null]);
        Assert.Equal(PagedReadStop.PageCap, end.Stop);
        Assert.Equal(3, read.Count);
        Assert.Equal(3, end.PagesRead);
        Assert.Equal("4", end.Cursor);
    }

    [Fact]
    public async Task AWalkThatEndsOnItsLastAllowedPageIsComplete()
    {
        var (end, _) = await WalkAsync(new PageGuard(MaxPages: 3), ["2", "3", null]);
        Assert.Equal(PagedReadStop.Completed, end.Stop);
    }

    [Fact]
    public async Task ARepeatedCursorStopsTheWalkOnlyWhenGuarded()
    {
        var (guarded, read) = await WalkAsync(new PageGuard(StopOnRepeatedCursor: true), ["2", "2", "2"]);
        Assert.Equal(PagedReadStop.RepeatedCursor, guarded.Stop);
        Assert.Equal([null, "2"], read);
        Assert.Equal("2", guarded.Cursor);

        var (unguarded, _) = await WalkAsync(new PageGuard(MaxPages: 5), ["2", "2", "2"]);
        Assert.Equal(PagedReadStop.PageCap, unguarded.Stop);
    }

    [Fact]
    public async Task ARepeatedPageStopsTheWalk()
    {
        var (end, read) = await WalkAsync(new PageGuard(PageRepeated: (previous, key) => previous is not null && previous == key), ["2", "3", "4"], ["a", "b", "b"]);
        Assert.Equal(PagedReadStop.RepeatedPage, end.Stop);
        Assert.Equal(3, read.Count);
    }

    [Fact]
    public async Task ACancelledRunStopsBetweenPages_UnlessTheGuardLetsTheNextReadRun()
    {
        using var first = new CancellationTokenSource();
        var (stopped, read) = await WalkAsync(new PageGuard(), ["2", "3", null], cancel: first, cancelAfter: 0);
        Assert.Equal(PagedReadStop.Cancelled, stopped.Stop);
        Assert.Single(read);
        Assert.Equal("2", stopped.Cursor);

        using var second = new CancellationTokenSource();
        var (walked, readOn) = await WalkAsync(new PageGuard(StopOnCancellation: false), ["2", "3", null], cancel: second, cancelAfter: 0);
        Assert.Equal(PagedReadStop.Completed, walked.Stop);
        Assert.Equal(3, readOn.Count);
    }

    // ISLEYICI-11 (16 Nov 2026): the guards for a walk that keeps its own loop stop at the same pages RunAsync stops at.
    [Fact]
    public void AHandWrittenWalkStopsAtTheSamePagesAsTheSharedLoop()
    {
        var capped = new PageWalk(new PageGuard(MaxPages: 3, StopOnRepeatedCursor: true));
        Assert.Null(capped.After(null, "2"));
        Assert.Null(capped.After("2", "3"));
        Assert.Equal(PagedReadStop.PageCap, capped.After("3", "4"));
        Assert.Equal(3, capped.PagesRead);

        var lastPage = new PageWalk(new PageGuard(MaxPages: 3));
        Assert.Null(lastPage.After(null, "2"));
        Assert.Null(lastPage.After("2", "3"));
        Assert.Null(lastPage.After("3", null));

        var repeated = new PageWalk(new PageGuard(StopOnRepeatedCursor: true));
        Assert.Null(repeated.After(null, "2"));
        Assert.Equal(PagedReadStop.RepeatedCursor, repeated.After("2", "2"));
        // A first page with no cursor that names no cursor is not a repeat, it is the end.
        Assert.Null(new PageWalk(new PageGuard(StopOnRepeatedCursor: true)).After(null, null));

        var unguarded = new PageWalk(new PageGuard());
        for (var page = 0; page < 10; page++) Assert.Null(unguarded.After("2", "2"));

        var samePage = new PageWalk(new PageGuard(PageRepeated: (previous, key) => previous is not null && previous == key));
        Assert.Null(samePage.After(null, "2", "a"));
        Assert.Equal(PagedReadStop.RepeatedPage, samePage.After("2", "3", "a"));
    }

    [Fact]
    public void ATrendyolReturnPageIsRepeatedOnlyWhenTheSamePageWouldBeAskedAgain()
    {
        var state = new ReturnReadService.ReturnSyncState("v", 0, 3);
        Assert.True(ReturnReadService.ReturnPageRepeated(state, ReturnReadService.NextReturnPage(state, "3")));
        Assert.False(ReturnReadService.ReturnPageRepeated(state, ReturnReadService.NextReturnPage(state, "4")));
        Assert.False(ReturnReadService.ReturnPageRepeated(state, ReturnReadService.NextReturnPage(state, null)));
        var opaque = ReturnReadService.NextReturnPage(state, "s:Accepted=2");
        Assert.True(ReturnReadService.ReturnPageRepeated(opaque, ReturnReadService.NextReturnPage(opaque, "s:Accepted=2")));
        Assert.False(ReturnReadService.ReturnPageRepeated(opaque, ReturnReadService.NextReturnPage(opaque, "s:Accepted=3")));
    }

    [Fact]
    public async Task AReaderThatStopsEndsTheWalk()
    {
        var calls = 0;
        var end = await PagedReadLoop.RunAsync("start", new PageGuard(), (cursor, index) => { calls++; return Task.FromResult<PageStep?>(null); }, CancellationToken.None);
        Assert.Equal(PagedReadStop.StoppedByReader, end.Stop);
        Assert.Equal("start", end.Cursor);
        Assert.Equal(1, calls);
    }
}
