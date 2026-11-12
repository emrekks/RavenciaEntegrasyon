using Xunit;
using MarketplaceHub.Infrastructure.Persistence;

namespace MarketplaceHub.Application.Tests;

// A card the marketplace accepted and is still approving ("Onay Bekliyor") is not sent again by Yayınla
// (guide 9.1.13): Trendyol's CREATE_ACCEPTED and Hepsiburada's IMPORT_ACCEPTED count as a send on its way.
public sealed class PublicationInProgressStatusTests
{
    [Theory]
    [InlineData("CREATE_ACCEPTED")]
    [InlineData("IMPORT_ACCEPTED")]
    [InlineData("approval_pending")]
    [InlineData("BATCH_SUBMITTED")]
    public void Accepted_and_waiting_cards_count_as_in_progress(string status) =>
        Assert.True(CatalogService.IsPlatformUpdateInProgress(status));

    [Theory]
    [InlineData("LIVE")]
    [InlineData("CREATE_REJECTED")]
    [InlineData("UNKNOWN")]
    [InlineData(null)]
    public void Settled_cards_do_not(string? status) =>
        Assert.False(CatalogService.IsPlatformUpdateInProgress(status));
}
