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
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1760000000000), invoice.SourceUpdatedAt);
    }

    [Fact]
    public void DirectOrderRead_MergesEveryPackageBeforeReconciliation()
    {
        const string json = """
            {"content":[
              {"id":"pkg-1","orderNumber":"ord-1","status":"Delivered","lastModifiedDate":1760000000000,"invoiceStatus":"NotInvoiced","packageTotalPrice":10,"lines":[{"lineId":"line-1","stockCode":"SKU-1","productName":"First","quantity":1,"lineItemPrice":10}]},
              {"id":"pkg-2","orderNumber":"ord-1","status":"Delivered","lastModifiedDate":1760000100000,"invoiceStatus":"Invoiced","invoiceNumber":"INV-2","invoiceLink":"https://example.test/invoice-2.pdf","packageTotalPrice":20,"lines":[{"lineId":"line-2","stockCode":"SKU-2","productName":"Second","quantity":1,"lineItemPrice":20}]}
            ]}
            """;

        var page = TrendyolJsonMapper.Orders(json);
        var order = TrendyolJsonMapper.MergeOrderPackages(page.Items, "ord-1");

        Assert.NotNull(order);
        Assert.Equal(2, order.Packages.Count);
        Assert.Equal(2, order.Lines.Count);
        Assert.Equal(30m, order.NetAmount);
        Assert.Contains(order.Packages, package => package.ExternalPackageId == "pkg-2" && package.Invoice?.RawStatus == "Invoiced");
    }

    [Fact]
    public void InvoiceIssueDate_DoesNotBlockLaterMarketplaceStatusTransition()
    {
        const string receivedJson = """
            {"content":[{"id":"pkg-1","orderNumber":"ord-1","status":"Delivered","lastModifiedDate":1760000000000,"invoiceDateTime":1759000000000,"invoiceStatus":"Received","lines":[]}]}
            """;
        const string invoicedJson = """
            {"content":[{"id":"pkg-1","orderNumber":"ord-1","status":"Delivered","lastModifiedDate":1760000100000,"invoiceDateTime":1759000000000,"invoiceStatus":"Invoiced","invoiceLink":"https://example.test/invoice.pdf","lines":[]}]}
            """;

        var received = Assert.Single(Assert.Single(TrendyolJsonMapper.Orders(receivedJson).Items).Packages).Invoice!;
        var invoiced = Assert.Single(Assert.Single(TrendyolJsonMapper.Orders(invoicedJson).Items).Packages).Invoice!;

        Assert.True(invoiced.SourceUpdatedAt > received.SourceUpdatedAt);
        Assert.True(MarketplaceInvoiceStatePolicy.ShouldApply(
            MarketplaceInvoiceStatus.Received, received.SourceUpdatedAt, received.SourceUpdatedAt,
            MarketplaceInvoiceStatus.Invoiced, invoiced.SourceUpdatedAt, invoiced.SourceUpdatedAt!.Value));
    }
}
