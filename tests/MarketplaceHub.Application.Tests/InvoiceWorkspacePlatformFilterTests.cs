using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class InvoiceWorkspacePlatformFilterTests
{
    [Fact]
    public void SelectedHepsiburadaShowsOnlyHepsiburadaRows()
    {
        var selected = new[] { "HEPSIBURADA" };

        Assert.True(InvoicingBillingService.MatchesWorkspacePlatformFilter(selected, "HEPSIBURADA"));
        Assert.False(InvoicingBillingService.MatchesWorkspacePlatformFilter(selected, "TRENDYOL"));
    }

    [Fact]
    public void EmptyPlatformSelectionShowsEveryPlatform()
    {
        Assert.True(InvoicingBillingService.MatchesWorkspacePlatformFilter([], "HEPSIBURADA"));
        Assert.True(InvoicingBillingService.MatchesWorkspacePlatformFilter(null, "TRENDYOL"));
    }

    [Fact]
    public void PlatformSelectionIgnoresLetterCaseAndSurroundingWhitespace()
    {
        Assert.True(InvoicingBillingService.MatchesWorkspacePlatformFilter([" hepsiburada "], "HEPSIBURADA"));
    }
}
