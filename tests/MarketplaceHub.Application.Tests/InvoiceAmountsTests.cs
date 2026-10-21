using MarketplaceHub.Domain;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class InvoiceAmountsTests
{
    [Fact]
    public void ResolvesOrderTotalsUsingPackageAggregatesWhenEveryActivePackageHasTotals()
    {
        var packages = new[]
        {
            new InvoicePackageTotalSource(100m, 120m, 20m),
            new InvoicePackageTotalSource(200m, 240m, 40m)
        };

        var totals = InvoiceAmounts.ResolveOrderTotals(999m, 888m, 77m, packages);

        Assert.Equal(new InvoiceOrderTotals(360m, 60m, 300m), totals);
    }

    [Fact]
    public void ResolvesOrderTotalsFromOrderWhenPackagePayableDataIsIncomplete()
    {
        var packages = new[]
        {
            new InvoicePackageTotalSource(100m, 120m, 20m),
            new InvoicePackageTotalSource(0m, 240m, 40m)
        };

        var totals = InvoiceAmounts.ResolveOrderTotals(300m, 360m, 60m, packages);

        Assert.Equal(new InvoiceOrderTotals(360m, 60m, 300m), totals);
    }

    [Fact]
    public void AllocatesDiscountAndVatAcrossPackageLinesWithoutChangingPayableTotal()
    {
        var sources = new[]
        {
            new InvoicePackageLineSource(Guid.NewGuid(), "Ürün A", "A", 1m, 110m, 10m),
            new InvoicePackageLineSource(Guid.NewGuid(), "Ürün B", "B", 1m, 120m, 20m)
        };

        Assert.True(InvoiceAmounts.TryCalculatePackage(sources, 230m, 23m, 207m, out var result, out var error));
        Assert.Null(error);
        Assert.NotNull(result);
        Assert.Equal(180m, result.TaxExclusiveTotal);
        Assert.Equal(20m, result.DiscountTotal);
        Assert.Equal(27m, result.TaxTotal);
        Assert.Equal(207m, result.PayableTotal);
        Assert.Equal(new[] { 99m, 108m }, result.Lines.Select(line => line.PayableAmount));
    }

    [Fact]
    public void PutsTheRoundingKurusOnALineThatCarriesVat()
    {
        var sources = new[]
        {
            new InvoicePackageLineSource(Guid.NewGuid(), "KDV'li", "A", 1m, 100m, 20m),
            new InvoicePackageLineSource(Guid.NewGuid(), "KDV'siz", "B", 1m, 50m, 0m)
        };

        Assert.True(InvoiceAmounts.TryCalculatePackage(sources, 150.01m, 0m, 150.01m, out var result, out var error));
        Assert.Null(error);
        Assert.NotNull(result);
        Assert.Equal(0m, result.Lines[1].VatAmount);
        Assert.Equal(50m, result.Lines[1].PayableAmount);
        Assert.Equal(100.01m, result.Lines[0].PayableAmount);
        Assert.Equal(16.68m, result.Lines[0].VatAmount);
        Assert.Equal(150.01m, result.Lines.Sum(line => line.PayableAmount));
        Assert.Equal(result.PayableTotal, result.TaxExclusiveTotal + result.TaxTotal);
    }

    [Fact]
    public void PutsTheRoundingKurusOnTheNetWhenNoLineCarriesVat()
    {
        var sources = new[]
        {
            new InvoicePackageLineSource(Guid.NewGuid(), "A", "A", 1m, 100m, 0m),
            new InvoicePackageLineSource(Guid.NewGuid(), "B", "B", 2m, 25m, 0m)
        };

        Assert.True(InvoiceAmounts.TryCalculatePackage(sources, 149.99m, 0m, 149.99m, out var result, out var error));
        Assert.Null(error);
        Assert.NotNull(result);
        Assert.All(result.Lines, line => Assert.Equal(0m, line.VatAmount));
        Assert.Equal(49.99m, result.Lines[1].PayableAmount);
        Assert.Equal(24.995m, result.Lines[1].UnitPrice);
        Assert.Equal(0m, result.TaxTotal);
        Assert.Equal(149.99m, result.TaxExclusiveTotal);
    }

    [Theory]
    [InlineData(200, 23, 207)]
    [InlineData(230, 40, 207)]
    [InlineData(230, 23, 220)]
    public void RejectsPackageTotalsThatDoNotReconcile(decimal gross, decimal discount, decimal payable)
    {
        var sources = new[] { new InvoicePackageLineSource(Guid.NewGuid(), "Ürün", "SKU", 1m, 230m, 10m) };

        Assert.False(InvoiceAmounts.TryCalculatePackage(sources, gross, discount, payable, out var result, out var error));
        Assert.Null(result);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void SelectsEInvoiceOnlyForCommercialCustomerWithAvailableEInvoiceAddress()
    {
        Assert.Equal("TEMELFATURA", InvoiceAmounts.TrendyolInvoiceType("{\"commercial\":true}", "{\"invoiceAddress\":{\"eInvoiceAvailable\":true}}"));
        Assert.Equal("EARSIVFATURA", InvoiceAmounts.TrendyolInvoiceType("{\"commercial\":false}", "{\"invoiceAddress\":{\"eInvoiceAvailable\":true}}"));
        Assert.Equal("EARSIVFATURA", InvoiceAmounts.TrendyolInvoiceType("{\"commercial\":true}", "{}"));
    }

    [Fact]
    public void Reads_e_invoice_availability_from_the_flat_trendyol_invoice_address_snapshot()
    {
        // TrendyolJsonMapper stores the invoiceAddress object itself as the snapshot root.
        const string flat = "{\"company\":\"Örnek A.Ş.\",\"taxNumber\":\"1234567890\",\"eInvoiceAvailable\":true}";

        Assert.Equal("TEMELFATURA", InvoiceAmounts.TrendyolInvoiceType("{\"commercial\":true}", flat));
        Assert.Equal("EARSIVFATURA", InvoiceAmounts.TrendyolInvoiceType("{\"commercial\":true}", "{\"eInvoiceAvailable\":false}"));
        Assert.Equal("EARSIVFATURA", InvoiceAmounts.TrendyolInvoiceType("{\"commercial\":false}", flat));
    }

    [Fact]
    public void Flat_e_invoice_flag_wins_over_a_nested_one()
    {
        Assert.Equal("EARSIVFATURA", InvoiceAmounts.TrendyolInvoiceType("{\"commercial\":true}", "{\"eInvoiceAvailable\":false,\"invoiceAddress\":{\"eInvoiceAvailable\":true}}"));
        Assert.Equal("EARSIVFATURA", InvoiceAmounts.TrendyolInvoiceType("[]", "[]"));
    }
}
