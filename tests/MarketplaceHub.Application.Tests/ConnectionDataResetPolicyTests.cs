using MarketplaceHub.Application;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class ConnectionDataResetPolicyTests
{
    [Fact]
    public void TrendyolSupportsOrderAndReferenceDataReset()
    {
        Assert.Equal(
            new[] { "ORDERS", "RETURNS", "INVOICES", "PRODUCTS", "CATEGORIES", "CATEGORY_ATTRIBUTES", "BRANDS", "COMMISSION_TARIFFS", "PROMOTION_OFFERS" }.Order(StringComparer.Ordinal),
            ConnectionDataResetPolicy.ScopesFor("TRENDYOL").Order(StringComparer.Ordinal));
    }

    [Fact]
    public void OnlyTrendyolOffersThePromotionFiles()
    {
        foreach (var platform in new[] { "PAZARAMA", "PTTAVM", "HEPSIBURADA", "N11", "SHOPIFY" })
        {
            Assert.DoesNotContain("COMMISSION_TARIFFS", ConnectionDataResetPolicy.ScopesFor(platform));
            Assert.DoesNotContain("PROMOTION_OFFERS", ConnectionDataResetPolicy.ScopesFor(platform));
        }
    }

    [Fact]
    public void HepsiburadaDoesNotOfferBrandReset()
    {
        var scopes = ConnectionDataResetPolicy.ScopesFor("HEPSIBURADA");
        Assert.Contains("ORDERS", scopes);
        Assert.Contains("CATEGORIES", scopes);
        Assert.DoesNotContain("BRANDS", scopes);
    }

    [Fact]
    public void N11OffersCategoryButNotBrandReset()
    {
        var scopes = ConnectionDataResetPolicy.ScopesFor("N11");
        Assert.Contains("ORDERS", scopes);
        Assert.Contains("PRODUCTS", scopes);
        Assert.Contains("CATEGORY_ATTRIBUTES", scopes);
        Assert.DoesNotContain("BRANDS", scopes);
    }

    [Fact]
    public void EInvoiceConnectionCanOnlyResetInvoices()
    {
        Assert.Equal(new[] { "INVOICES" }, ConnectionDataResetPolicy.ScopesFor("TRENDYOL_EFATURAM"));
    }

    [Fact]
    public void UnsupportedPlatformsCannotResetConnectionData()
    {
        Assert.Empty(ConnectionDataResetPolicy.ScopesFor("UNKNOWN"));
    }
}
