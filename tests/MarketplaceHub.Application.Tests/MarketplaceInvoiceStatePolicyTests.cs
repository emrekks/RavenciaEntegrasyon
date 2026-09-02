using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Adapters.Trendyol.Mapping;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class MarketplaceInvoiceStatePolicyTests
{
    [Theory]
    [InlineData("Invoiced", MarketplaceInvoiceStatus.Invoiced)]
    [InlineData("NotInvoiced", MarketplaceInvoiceStatus.NotInvoiced)]
    [InlineData("Received", MarketplaceInvoiceStatus.Received)]
    [InlineData("Rejected", MarketplaceInvoiceStatus.Rejected)]
    public void FromRemote_MapsExplicitMarketplaceInvoiceState(string rawStatus, MarketplaceInvoiceStatus expected)
    {
        Assert.Equal(expected, MarketplaceInvoiceStatePolicy.FromRemote(rawStatus));
    }

    [Fact]
    public void InvoicedStateCannotBeDowngradedByAnOlderOrIncompleteSnapshot()
    {
        var observedAt = DateTimeOffset.Parse("2026-09-01T10:00:00Z");

        Assert.False(MarketplaceInvoiceStatePolicy.ShouldApply(
            MarketplaceInvoiceStatus.Invoiced, observedAt, observedAt,
            MarketplaceInvoiceStatus.NotInvoiced, observedAt.AddMinutes(-1), observedAt.AddMinutes(-1)));
        Assert.False(MarketplaceInvoiceStatePolicy.ShouldApply(
            MarketplaceInvoiceStatus.Invoiced, observedAt, observedAt,
            MarketplaceInvoiceStatus.Unknown, null, observedAt.AddMinutes(1)));
    }

    [Fact]
    public void ConflictingStatesWithTheSameProviderTimestampAreIgnored()
    {
        var sourceAt = DateTimeOffset.Parse("2026-09-01T10:00:00Z");

        Assert.False(MarketplaceInvoiceStatePolicy.ShouldApply(
            MarketplaceInvoiceStatus.NotInvoiced, sourceAt, sourceAt,
            MarketplaceInvoiceStatus.Invoiced, sourceAt, sourceAt.AddMinutes(1)));
    }

    [Fact]
    public void MapperCarriesPackageInvoiceFieldsAlongsideShipmentState()
    {
        const string json = """
            {"content":[{"id":"pkg-1","orderNumber":"ord-1","status":"Delivered","lastModifiedDate":1760000000000,"invoiceStatus":"Invoiced","invoiceNumber":"INV-1","invoiceLink":"https://example.test/invoice.pdf","lines":[{"lineId":"line-1","stockCode":"SKU-1","productName":"Test","quantity":1,"lineItemPrice":10}]}]}
            """;

        var result = TrendyolJsonMapper.Orders(json);
        var invoice = Assert.Single(Assert.Single(result.Items).Packages).Invoice;

        Assert.NotNull(invoice);
        Assert.Equal("Invoiced", invoice.RawStatus);
        Assert.Equal("INV-1", invoice.InvoiceNumber);
        Assert.Equal("https://example.test/invoice.pdf", invoice.InvoiceUrl);
    }
}
