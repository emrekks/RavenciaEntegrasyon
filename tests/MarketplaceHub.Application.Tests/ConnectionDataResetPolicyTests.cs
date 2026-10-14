using MarketplaceHub.Application;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class ConnectionDataResetPolicyTests
{
    [Fact]
    public void TrendyolSupportsOrderAndReferenceDataReset()
    {
        Assert.Equal(
            new[] { "ORDERS", "RETURNS", "INVOICES", "PRODUCTS", "CATEGORIES", "CATEGORY_ATTRIBUTES", "BRANDS" }.Order(StringComparer.Ordinal),
            ConnectionDataResetPolicy.ScopesFor("TRENDYOL").Order(StringComparer.Ordinal));
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
