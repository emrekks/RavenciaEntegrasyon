using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class InvoiceWorkspaceTabPolicyTests
{
    [Theory]
    [InlineData("FATURA_ISLENIYOR", false, false, false)]
    [InlineData("FATURA_PLATFORMA_BEKLIYOR", false, false, false)]
    [InlineData("FATURA_REDDEDILDI", false, false, true)]
    [InlineData("FATURA_PLATFORMA_AKTARILMADI", false, false, true)]
    [InlineData("FATURA_BEKLIYOR", true, false, true)]
    [InlineData("FATURA_KONTROLDE", false, false, false)]
    [InlineData("FATURA_KESILDI", false, false, false)]
    [InlineData("FATURA_PLATFORMA_AKTARILMADI", false, true, false)]
    [InlineData("FATURA_BEKLIYOR", true, true, false)]
    public void MarketplaceInvoiceObservationTakesPrecedenceForWorkspaceTab(string invoiceStatus, bool canCreateInvoice, bool marketplaceInvoicePresent, bool expectedUninvoiced)
    {
        Assert.Equal(expectedUninvoiced, InvoicingBillingService.IsWorkspaceInvoiceUninvoiced(
            invoiceStatus,
            canCreateInvoice,
            marketplaceInvoicePresent));
    }

    [Fact]
    public void AnIssuedInvoiceOnItsWayToTheMarketplaceIsWithTheInvoicedOnes()
    {
        Assert.False(InvoicingBillingService.IsWorkspaceInvoiceUninvoiced("FATURA_ISLENIYOR", canCreateInvoice: false, marketplaceInvoicePresent: false));
        Assert.True(InvoicingBillingService.IsWorkspaceInvoiceUninvoiced("FATURA_PLATFORMA_AKTARILMADI", canCreateInvoice: false, marketplaceInvoicePresent: false));
    }
}
