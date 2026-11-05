using System.Globalization;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Adapters.Shopify;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// Shopify reports a line's tax in LineItem.taxLines (each rate a fraction) and whether the prices carry it in
// Order.taxesIncluded (shopify-admin-graphql-referans-son.md:32611-32712, 6665). Before 5 Nov 2026 every Shopify line
// was stored at 0% and the unknown-VAT gate skipped Shopify.
public sealed class ShopifyLineVatRateTests
{
    [Fact]
    public void TheLineRateIsTheSumOfItsTaxLinesAsAPercentage()
    {
        var order = Map(taxesIncluded: true, net: 120m, discount: 0m, Line(1, 1, 120m, "[{\"rate\":0.2}]"), Line(2, 1, 0m, "[{\"rate\":0.18},{\"rate\":0.02}]"));

        Assert.Equal(20m, order.Lines.Single(line => line.ExternalLineId == "1").VatRate);
        Assert.Equal(20m, order.Lines.Single(line => line.ExternalLineId == "2").VatRate);
        Assert.Equal(120m, order.Lines.Single(line => line.ExternalLineId == "1").UnitPrice);
        Assert.All(order.Lines, line => Assert.False(InvoiceIssuePolicy.IsLineVatRateUnknown("SHOPIFY", line.SourceSnapshotJson)));
    }

    [Fact]
    public void AReportedZeroRateIsKnown()
    {
        var line = Assert.Single(Map(taxesIncluded: true, net: 50m, discount: 0m, Line(1, 1, 50m, "[{\"rate\":0}]")).Lines);

        Assert.Equal(0m, line.VatRate);
        Assert.False(InvoiceIssuePolicy.IsLineVatRateUnknown("SHOPIFY", line.SourceSnapshotJson));
    }

    // No tax line, or one without a rate: the rate is unknown, never 0. The product card's rate is used, else the
    // invoice stops, as on every other marketplace.
    [Theory]
    [InlineData("[]")]
    [InlineData("[{\"rate\":null}]")]
    [InlineData("[{\"rate\":0.2},{\"title\":\"KDV\"}]")]
    [InlineData(null)]
    public void ALineWithoutAReadableRateIsUnknown(string? taxLines)
    {
        var line = Assert.Single(Map(taxesIncluded: true, net: 100m, discount: 0m, Line(1, 1, 100m, taxLines)).Lines);

        Assert.Null(line.VatRate);
        Assert.True(InvoiceIssuePolicy.IsLineVatRateUnknown("SHOPIFY", line.SourceSnapshotJson));
        Assert.Equal(10m, InvoiceIssuePolicy.ResolveLineVatRate("SHOPIFY", line.SourceSnapshotJson, 0m, 10m));
        Assert.Null(InvoiceIssuePolicy.ResolveLineVatRate("SHOPIFY", line.SourceSnapshotJson, 0m, null));
    }

    // taxesIncluded false: the line price and the discount leave the tax out while the order total holds it. The line
    // takes its tax, the discount its share, and the invoice (VAT-inclusive prices) agrees with what was paid.
    [Fact]
    public void PricesWithoutTaxTakeTheirTaxSoTheInvoiceMatchesThePaidTotal()
    {
        // 2 × 100 before tax, 20 discount before tax, 20% on 180 = 36: the customer paid 216.
        var order = Map(taxesIncluded: false, net: 216m, discount: 20m, Line(1, 2, 100m, "[{\"rate\":0.2,\"title\":\"KDV\"}]"));

        var line = Assert.Single(order.Lines);
        Assert.Equal((120m, 20m), (line.UnitPrice, line.VatRate));
        Assert.Equal((240m, 24m, 216m), (order.GrossAmount, order.DiscountAmount, order.NetAmount));
        var package = Assert.Single(order.Packages);
        var sources = new[] { new InvoicePackageLineSource(Guid.NewGuid(), "Ürün", "SKU-1", 2m, line.UnitPrice, line.VatRate!.Value) };
        Assert.True(InvoiceAmounts.TryCalculatePackage(sources, package.GrossAmount, package.DiscountAmount, package.NetAmount, out var calculation, out var error), error);
        Assert.Equal((180m, 36m, 216m), (calculation!.TaxExclusiveTotal, calculation.TaxTotal, calculation.PayableTotal));
    }

    [Fact]
    public void PricesWithTaxStayAsReported()
    {
        // 2 × 120 with tax, 24 discount with tax: the customer paid 216, of which 36 is tax.
        var order = Map(taxesIncluded: true, net: 216m, discount: 24m, Line(1, 2, 120m, "[{\"rate\":0.2}]"));

        var line = Assert.Single(order.Lines);
        Assert.Equal((120m, 20m), (line.UnitPrice, line.VatRate));
        Assert.Equal((240m, 24m, 216m), (order.GrossAmount, order.DiscountAmount, order.NetAmount));
        var package = Assert.Single(order.Packages);
        var sources = new[] { new InvoicePackageLineSource(Guid.NewGuid(), "Ürün", "SKU-1", 2m, line.UnitPrice, line.VatRate!.Value) };
        Assert.True(InvoiceAmounts.TryCalculatePackage(sources, package.GrossAmount, package.DiscountAmount, package.NetAmount, out var calculation, out var error), error);
        Assert.Equal((180m, 36m, 216m), (calculation!.TaxExclusiveTotal, calculation.TaxTotal, calculation.PayableTotal));
    }

    // A line Shopify charged no tax on (no tax line) already carries what was paid, whatever taxesIncluded says.
    [Fact]
    public void ALineWithoutTaxKeepsItsPriceWhenTaxesAreNotIncluded()
    {
        var order = Map(taxesIncluded: false, net: 100m, discount: 0m, Line(1, 1, 100m, "[]"));

        Assert.Equal(100m, Assert.Single(order.Lines).UnitPrice);
        Assert.Equal((100m, 0m, 100m), (order.GrossAmount, order.DiscountAmount, order.NetAmount));
    }

    private static string Amount(decimal value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Line(int id, int quantity, decimal price, string? taxLines) =>
        $$$"""{"id":"gid://shopify/LineItem/{{{id}}}","name":"Ürün","sku":"SKU-{{{id}}}","quantity":{{{quantity}}},"currentQuantity":{{{quantity}}},"originalUnitPriceSet":{"shopMoney":{"amount":"{{{Amount(price)}}}","currencyCode":"TRY"}},{{{(taxLines is null ? "" : $"\"taxLines\":{taxLines},")}}}"variant":null}""";

    private static RemoteOrder Map(bool taxesIncluded, decimal net, decimal discount, params string[] lines)
    {
        using var json = System.Text.Json.JsonDocument.Parse($$$"""
            {"id":"gid://shopify/Order/1","name":"#1001","createdAt":"2026-10-19T10:00:00Z","updatedAt":"2026-10-19T12:00:00Z","cancelledAt":null,
             "displayFinancialStatus":"PAID","email":null,"phone":null,"taxesIncluded":{{{(taxesIncluded ? "true" : "false")}}},
             "currentTotalPriceSet":{"shopMoney":{"amount":"{{{Amount(net)}}}","currencyCode":"TRY"}},
             "totalDiscountsSet":{"shopMoney":{"amount":"{{{Amount(discount)}}}","currencyCode":"TRY"}},
             "currentTotalDiscountsSet":{"shopMoney":{"amount":"{{{Amount(discount)}}}","currencyCode":"TRY"}},
             "lineItems":{"nodes":[{{{string.Join(",", lines)}}}]},
             "fulfillments":[],"refunds":[]}
            """);
        return ShopifyHttpClient.MapOrder(json.RootElement);
    }
}
