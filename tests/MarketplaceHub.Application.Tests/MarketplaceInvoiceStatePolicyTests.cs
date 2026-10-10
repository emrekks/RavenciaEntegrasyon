using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Adapters.Trendyol.Mapping;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class MarketplaceInvoiceStatePolicyTests
{
    [Theory]
    [InlineData("Invoiced", MarketplaceInvoiceStatus.Invoiced)]
    [InlineData("NotInvoiced", MarketplaceInvoiceStatus.NotInvoiced)]
    [InlineData("Deleted", MarketplaceInvoiceStatus.NotInvoiced)]
    [InlineData("Invoice removed", MarketplaceInvoiceStatus.NotInvoiced)]
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
    public void FreshReadCanCorrectAConflictingStateEvenWhenProviderTimestampIsUnchanged()
    {
        var sourceAt = DateTimeOffset.Parse("2026-09-01T10:00:00Z");

        Assert.True(MarketplaceInvoiceStatePolicy.ShouldApply(
            MarketplaceInvoiceStatus.NotInvoiced, sourceAt, sourceAt,
            MarketplaceInvoiceStatus.Invoiced, sourceAt, sourceAt.AddMinutes(1)));
    }

    [Fact]
    public void NewMarketplaceReferenceReplacesTheStoredValueWhileEmptyReferencePreservesIt()
    {
        Assert.Equal("INV-2", MarketplaceInvoiceStatePolicy.PreferNonEmptyReference("INV-1", " INV-2 "));
        Assert.Equal("INV-1", MarketplaceInvoiceStatePolicy.PreferNonEmptyReference("INV-1", "  "));
    }

    [Fact]
    public void ReferenceReadbackUsesProviderTimestampAndFallsBackToObservationTime()
    {
        var sourceAt = DateTimeOffset.Parse("2026-09-01T10:00:00Z");
        var observedAt = sourceAt.AddMinutes(2);

        Assert.False(MarketplaceInvoiceStatePolicy.ShouldApplyReferenceUpdate(
            sourceAt, observedAt, sourceAt.AddMinutes(-1), observedAt.AddMinutes(1)));
        Assert.False(MarketplaceInvoiceStatePolicy.ShouldApplyReferenceUpdate(
            sourceAt, observedAt, null, observedAt.AddMinutes(-1)));
        Assert.True(MarketplaceInvoiceStatePolicy.ShouldApplyReferenceUpdate(
            sourceAt, observedAt, null, observedAt.AddMinutes(1)));
        Assert.True(MarketplaceInvoiceStatePolicy.ShouldApplyReferenceUpdate(
            null, null, sourceAt, observedAt));
    }

    [Fact]
    public void MapperCarriesPackageInvoiceFieldsAlongsideShipmentState()
    {
        const string json = """
            {"content":[{"id":"pkg-1","orderNumber":"ord-1","status":"Delivered","lastModifiedDate":1760000000000,"invoiceStatus":"Invoiced","invoiceNumber":"INV-1","invoiceLink":"https://example.test/invoice.pdf","lines":[{"lineId":"line-1","stockCode":"SKU-1","productName":"Test","quantity":1,"lineItemPrice":10,"vatRate":20}]}]}
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
    public void MapperCapturesTrendyolInvoiceRemovalWithoutAStaleDocumentLink()
    {
        const string json = """
            {"content":[{"id":"pkg-1","orderNumber":"ord-1","status":"Delivered","lastModifiedDate":1760000100000,"invoiceStatus":"NotInvoiced","lines":[{"lineId":"line-1","stockCode":"SKU-1","productName":"Test","quantity":1,"lineItemPrice":10,"vatRate":20}]}]}
            """;

        var invoice = Assert.Single(Assert.Single(TrendyolJsonMapper.Orders(json).Items).Packages).Invoice;

        Assert.NotNull(invoice);
        Assert.Equal("NotInvoiced", invoice.RawStatus);
        Assert.Null(invoice.InvoiceNumber);
        Assert.Null(invoice.InvoiceUrl);
    }

    [Fact]
    public void MapperAcceptsDocumentedV2ShipmentPackageAndLinePriceFields()
    {
        const string json = """
            {"content":[{"shipmentPackageId":3330111111,"orderNumber":"10654411111","status":"Delivered","lastModifiedDate":1760000000000,"packageGrossAmount":498.90,"packageTotalDiscount":0,"packageTotalPrice":498.90,"lines":[{"lineId":4765111111,"stockCode":"SKU-1","productName":"Test","quantity":1,"lineUnitPrice":498.90,"vatRate":20,"barcode":"8683772071724"}]}]}
            """;

        var page = TrendyolJsonMapper.Orders(json);
        var order = Assert.Single(page.Items);
        var package = Assert.Single(order.Packages);
        var line = Assert.Single(order.Lines);

        Assert.Equal("3330111111", package.ExternalPackageId);
        Assert.Equal("10654411111", order.OrderNumber);
        Assert.Equal(498.90m, line.UnitPrice);
        Assert.Empty(page.Issues!);
    }

    [Fact]
    public void MapperReadsNestedCarrierAndNumericTrackingFieldsForShipmentPackages()
    {
        const string json = """
            {"content":[{"shipmentPackageId":3330111111,"orderNumber":"10654411111","status":"Delivered","lastModifiedDate":1760000000000,"cargoProvider":{"name":"HepsiJet","shortName":"HEPSIJET"},"cargoTrackingNumber":62755229958101,"lines":[{"lineId":4765111111,"stockCode":"SKU-1","productName":"Test","quantity":1,"lineUnitPrice":498.90,"vatRate":20,"barcode":"8683772071724"}]}]}
            """;

        var package = Assert.Single(Assert.Single(TrendyolJsonMapper.Orders(json).Items).Packages);

        Assert.Equal("HepsiJet", package.CargoProviderExternalId);
        Assert.Equal("62755229958101", package.CargoTrackingNumber);
    }

    [Fact]
    public void InvoiceNumberOrLinkAloneDoesNotProveMarketplaceInvoice()
    {
        Assert.Equal(MarketplaceInvoiceStatus.Unknown,
            MarketplaceInvoiceStatePolicy.FromRemote(null, "Delivered", "INV-1", "https://example.test/invoice.pdf"));
    }

    [Fact]
    public void MapperKeepsPackageWhenProductLineIsInvalid()
    {
        const string json = """
            {"content":[{"id":"pkg-1","orderNumber":"ord-1","status":"Created","lastModifiedDate":1760000000000,"lines":[{"lineId":"line-1","stockCode":"SKU-1","productName":"Test","quantity":1}]}]}
            """;

        var result = TrendyolJsonMapper.Orders(json);

        var order = Assert.Single(result.Items);
        Assert.Equal("ord-1", order.ExternalOrderId);
        Assert.Empty(order.Lines);
        Assert.Equal("pkg-1", Assert.Single(order.Packages).ExternalPackageId);
        var issue = Assert.Single(result.Issues!);
        Assert.Equal("ORDER_PACKAGE_LINE_INVALID", issue.Code);
        Assert.Equal("pkg-1:line-1", issue.Identity);
        Assert.Contains("unit price", issue.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MapperKeepsValidPackagesWhenAnotherPackageIsMalformed()
    {
        const string json = """
            {"content":[
              {"id":"bad-pkg","orderNumber":"ord-1","status":"Created","lines":[{"lineId":"bad-line","stockCode":"SKU-BAD","productName":"Bad","quantity":1}]},
              {"id":"good-pkg","orderNumber":"ord-2","status":"Created","lines":[{"lineId":"good-line","stockCode":"SKU-GOOD","productName":"Good","quantity":1,"lineItemPrice":10,"vatRate":20}]}
            ]}
            """;

        var result = TrendyolJsonMapper.Orders(json);

        Assert.Equal(2, result.Items.Count);
        Assert.Contains(result.Items, order => order.Packages.Single().ExternalPackageId == "bad-pkg" && order.Lines.Count == 0);
        Assert.Contains(result.Items, order => order.Packages.Single().ExternalPackageId == "good-pkg" && order.Lines.Count == 1);
        var issue = Assert.Single(result.Issues!);
        Assert.Equal("bad-pkg:bad-line", issue.Identity);
    }

    [Fact]
    public void MapperKeepsValidLinesAndPackageWhenAnotherLineIsInvalid()
    {
        const string json = """
            {"content":[{"id":"pkg-1","orderNumber":"ord-1","status":"Shipped","packageTotalPrice":20,"lines":[
              {"lineId":"bad-line","stockCode":"SKU-BAD","productName":"Bad","quantity":1},
              {"lineId":"good-line","stockCode":"SKU-GOOD","productName":"Good","quantity":1,"lineItemPrice":20,"vatRate":20}
            ]}]}
            """;

        var result = TrendyolJsonMapper.Orders(json);

        var order = Assert.Single(result.Items);
        Assert.Equal("pkg-1", Assert.Single(order.Packages).ExternalPackageId);
        Assert.Equal("good-line", Assert.Single(order.Lines).ExternalLineId);
        Assert.Equal("ORDER_PACKAGE_LINE_INVALID", Assert.Single(result.Issues!).Code);
    }

    [Fact]
    public void MapperReportsPackageWithMissingIdentityInsteadOfDroppingSilently()
    {
        const string json = """
            {"content":[{"orderNumber":"ord-1","status":"Created"}]}
            """;

        var result = TrendyolJsonMapper.Orders(json);

        Assert.Empty(result.Items);
        var issue = Assert.Single(result.Issues!);
        Assert.Equal("ORDER_PACKAGE_INVALID", issue.Code);
        Assert.Equal("ord-1", issue.Identity);
    }

    [Fact]
    public void DirectOrderRead_MergesEveryPackageBeforeReconciliation()
    {
        const string json = """
            {"content":[
              {"id":"pkg-1","orderNumber":"ord-1","status":"Delivered","lastModifiedDate":1760000000000,"invoiceStatus":"NotInvoiced","packageTotalPrice":10,"lines":[{"lineId":"line-1","stockCode":"SKU-1","productName":"First","quantity":1,"lineItemPrice":10,"vatRate":20}]},
              {"id":"pkg-2","orderNumber":"ord-1","status":"Delivered","lastModifiedDate":1760000100000,"invoiceStatus":"Invoiced","invoiceNumber":"INV-2","invoiceLink":"https://example.test/invoice-2.pdf","packageTotalPrice":20,"lines":[{"lineId":"line-2","stockCode":"SKU-2","productName":"Second","quantity":1,"lineItemPrice":20,"vatRate":20}]}
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
    public void PackageRead_ProvidesAllOrderLinesForHistoricalOrderFallback()
    {
        const string json = """
            {"content":[{"id":"3968176322","orderNumber":"11376153333","status":"Delivered","lastModifiedDate":1783441320000,"packageTotalPrice":1355.20,"packageTotalDiscount":83.70,"lines":[
              {"lineId":"line-1","stockCode":"RYP00204","productName":"First item","quantity":1,"lineItemPrice":539.90,"vatRate":10},
              {"lineId":"line-2","stockCode":"MZ043DDC05","productName":"Second item","quantity":1,"lineItemPrice":899.00,"vatRate":10}
            ]}]}
            """;

        var package = TrendyolJsonMapper.ShipmentPackage(json, "3968176322");

        Assert.NotNull(package?.OrderSnapshot);
        Assert.Equal(2, package.OrderSnapshot!.Lines.Count);
        Assert.Single(package.OrderSnapshot.Packages);
        Assert.Equal(1_355.20m, package.OrderSnapshot.NetAmount);
        Assert.Equal(2, TrendyolJsonMapper.MergeOrderPackages([package.OrderSnapshot], "11376153333")!.Lines.Count);
    }

    [Fact]
    public void InvoiceIssueDate_DoesNotBlockLaterMarketplaceStatusTransition()
    {
        const string receivedJson = """
            {"content":[{"id":"pkg-1","orderNumber":"ord-1","status":"Delivered","lastModifiedDate":1760000000000,"invoiceDateTime":1759000000000,"invoiceStatus":"Received","lines":[{"lineId":"line-1","stockCode":"SKU-1","productName":"Test","quantity":1,"lineItemPrice":10,"vatRate":20}]}]}
            """;
        const string invoicedJson = """
            {"content":[{"id":"pkg-1","orderNumber":"ord-1","status":"Delivered","lastModifiedDate":1760000100000,"invoiceDateTime":1759000000000,"invoiceStatus":"Invoiced","invoiceLink":"https://example.test/invoice.pdf","lines":[{"lineId":"line-1","stockCode":"SKU-1","productName":"Test","quantity":1,"lineItemPrice":10,"vatRate":20}]}]}
            """;

        var received = Assert.Single(Assert.Single(TrendyolJsonMapper.Orders(receivedJson).Items).Packages).Invoice!;
        var invoiced = Assert.Single(Assert.Single(TrendyolJsonMapper.Orders(invoicedJson).Items).Packages).Invoice!;

        Assert.True(invoiced.SourceUpdatedAt > received.SourceUpdatedAt);
        Assert.True(MarketplaceInvoiceStatePolicy.ShouldApply(
            MarketplaceInvoiceStatus.Received, received.SourceUpdatedAt, received.SourceUpdatedAt,
            MarketplaceInvoiceStatus.Invoiced, invoiced.SourceUpdatedAt, invoiced.SourceUpdatedAt!.Value));
    }
}
