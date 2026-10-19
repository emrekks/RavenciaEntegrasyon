using MarketplaceHub.Application;
using MarketplaceHub.Infrastructure.Adapters.Trendyol;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class TrendyolCatalogCursorPolicyTests
{
    [Fact]
    public void ResolveReadsThePassFromTheCursorPrefix()
    {
        Assert.Equal((TrendyolCatalogPass.Approved, (string?)null), TrendyolCatalogCursorPolicy.Resolve(null));
        Assert.Equal((TrendyolCatalogPass.Approved, (string?)"p:3"), TrendyolCatalogCursorPolicy.Resolve("approved:p:3"));
        Assert.Equal((TrendyolCatalogPass.ContentChanged, (string?)"p:1"), TrendyolCatalogCursorPolicy.Resolve("content:p:1"));
        Assert.Equal((TrendyolCatalogPass.PendingApproval, (string?)"t:token"), TrendyolCatalogCursorPolicy.Resolve("pending:t:token"));
        Assert.Equal((TrendyolCatalogPass.Approved, (string?)"p:3"), TrendyolCatalogCursorPolicy.Resolve("p:3"));
    }

    [Fact]
    public void AFullReadGoesFromApprovedToPendingProducts()
    {
        Assert.Equal("approved:p:1", TrendyolCatalogCursorPolicy.Advance(Page("p:1"), TrendyolCatalogPass.Approved, incremental: false, includePendingApproval: true).NextCursor);
        Assert.Equal("pending:p:0", TrendyolCatalogCursorPolicy.Advance(Page(null), TrendyolCatalogPass.Approved, false, true).NextCursor);
        Assert.Equal("pending:t:next", TrendyolCatalogCursorPolicy.Advance(Page("t:next"), TrendyolCatalogPass.PendingApproval, false, true).NextCursor);
        var completed = TrendyolCatalogCursorPolicy.Advance(Page(null), TrendyolCatalogPass.PendingApproval, false, true);
        Assert.False(completed.HasMore);
        Assert.Null(completed.NextCursor);
    }

    [Fact]
    public void AnIncrementalReadAlsoReadsContentChangesBeforePendingProducts()
    {
        Assert.Equal("content:p:0", TrendyolCatalogCursorPolicy.Advance(Page(null), TrendyolCatalogPass.Approved, incremental: true, includePendingApproval: false).NextCursor);
        Assert.Equal("content:p:2", TrendyolCatalogCursorPolicy.Advance(Page("p:2"), TrendyolCatalogPass.ContentChanged, true, false).NextCursor);
        Assert.Null(TrendyolCatalogCursorPolicy.Advance(Page(null), TrendyolCatalogPass.ContentChanged, true, false).NextCursor);
        Assert.Equal("pending:p:0", TrendyolCatalogCursorPolicy.Advance(Page(null), TrendyolCatalogPass.ContentChanged, true, true).NextCursor);
    }

    [Fact]
    public void ASinglePassReadKeepsTheMarketplaceCursor()
    {
        Assert.Equal("p:4", TrendyolCatalogCursorPolicy.Advance(Page("p:4"), TrendyolCatalogPass.Approved, false, false).NextCursor);
    }

    private static AdapterPageResult<int> Page(string? next) => new([1], next, next is not null, null);
}
