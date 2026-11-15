using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// ISLEYICI-3 (15 Nov 2026): a catalog import writes its own marketplace on the price history, not SHOPIFY for every
// observe-only marketplace.
public sealed class CatalogImportPriceHistorySourceTests
{
    [Theory]
    [InlineData("HEPSIBURADA", true, "HEPSIBURADA")]
    [InlineData("N11", true, "N11")]
    [InlineData("PAZARAMA", true, "PAZARAMA")]
    [InlineData("PTTAVM", true, "PTTAVM")]
    [InlineData("SHOPIFY", true, "SHOPIFY")]
    [InlineData("TRENDYOL", false, "TRENDYOL")]
    [InlineData("hepsiburada", true, "HEPSIBURADA")]
    [InlineData(null, true, "SHOPIFY")]
    [InlineData(null, false, "TRENDYOL")]
    public void The_source_is_the_connections_marketplace(string? platformCode, bool observeOnly, string expected)
    {
        Assert.Equal(expected, MarketplaceJobProcessor.CatalogImportSource(platformCode, observeOnly));
    }
}
