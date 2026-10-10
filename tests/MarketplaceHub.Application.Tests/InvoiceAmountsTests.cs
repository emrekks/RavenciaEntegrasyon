using MarketplaceHub.Domain;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class InvoiceAmountsTests
{
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
}
