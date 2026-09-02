using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class MarketplaceSalesInvoiceLabelTests
{
    [Theory]
    [InlineData("INVOICED")]
    [InlineData("Invoiced")]
    public void InvoicedPackageStatusIsPresentedAsIssuedInvoice(string rawStatus)
    {
        var label = MarketplaceSalesService.InvoiceLabel(null, "{}", [rawStatus]);

        Assert.Equal("FATURA_KESILDI", label);
    }

    [Fact]
    public void MissingRemoteInvoiceStatusIsPresentedAsUnknownInsteadOfWaiting()
    {
        var label = MarketplaceSalesService.InvoiceLabel(null, "{}", ["Delivered"]);

        Assert.Equal("FATURA_BILINMIYOR", label);
    }
}
