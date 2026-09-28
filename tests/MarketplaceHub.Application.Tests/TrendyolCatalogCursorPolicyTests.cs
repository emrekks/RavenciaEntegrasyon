using MarketplaceHub.Infrastructure.Adapters.Trendyol;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class TrendyolCatalogCursorPolicyTests
{
    [Fact]
    public void ResolveStartsAtApprovedCatalogAndUnwrapsBothCursorSources()
    {
        Assert.Equal((false, (string?)null), TrendyolCatalogCursorPolicy.Resolve(true, null));
        Assert.Equal((false, (string?)"p:3"), TrendyolCatalogCursorPolicy.Resolve(true, "approved:p:3"));
        Assert.Equal((true, (string?)"t:token"), TrendyolCatalogCursorPolicy.Resolve(true, "pending:t:token"));
        Assert.Equal((false, (string?)"p:3"), TrendyolCatalogCursorPolicy.Resolve(false, "p:3"));
    }

    [Fact]
    public void AdvanceReadsApprovedPagesBeforePendingApprovalPages()
    {
        var approvedPage = new AdapterPageResult<int>([1], "p:1", true, 2);
        var lastApprovedPage = new AdapterPageResult<int>([2], null, false, 2);
        var pendingPage = new AdapterPageResult<int>([3], "t:next", true, 1);
        var lastPendingPage = new AdapterPageResult<int>([4], null, false, 1);

        Assert.Equal("approved:p:1", TrendyolCatalogCursorPolicy.Advance(approvedPage, true, false).NextCursor);
        var pendingStart = TrendyolCatalogCursorPolicy.Advance(lastApprovedPage, true, false);
        Assert.True(pendingStart.HasMore);
        Assert.Equal("pending:p:0", pendingStart.NextCursor);
        Assert.Equal("pending:t:next", TrendyolCatalogCursorPolicy.Advance(pendingPage, true, true).NextCursor);
        var completed = TrendyolCatalogCursorPolicy.Advance(lastPendingPage, true, true);
        Assert.False(completed.HasMore);
        Assert.Null(completed.NextCursor);
    }
}
