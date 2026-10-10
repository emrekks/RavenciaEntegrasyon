using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class InvoiceWorkspaceTabPolicyTests
{
    [Theory]
    [InlineData("FATURA_ISLENIYOR", false, true)]
    [InlineData("FATURA_REDDEDILDI", false, true)]
    [InlineData("FATURA_BEKLIYOR", true, true)]
    [InlineData("FATURA_KONTROLDE", false, true)]
    [InlineData("FATURA_KESILDI", false, false)]
    public void ProcessingInvoiceStaysOutOfInvoicedTab(string invoiceStatus, bool canCreateInvoice, bool expectedUninvoiced)
    {
        Assert.Equal(expectedUninvoiced, InvoicingBillingService.IsWorkspaceInvoiceUninvoiced(invoiceStatus, canCreateInvoice));
    }
}
