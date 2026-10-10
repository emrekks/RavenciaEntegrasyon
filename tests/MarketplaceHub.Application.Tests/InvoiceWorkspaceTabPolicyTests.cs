using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class InvoiceWorkspaceTabPolicyTests
{
    [Theory]
    [InlineData("FATURA_ISLENIYOR", false, true)]
    [InlineData("FATURA_REDDEDILDI", false, true)]
    [InlineData("MANUAL_REVIEW", false, true)]
    [InlineData("MARKETPLACE_FAILED", false, true)]
    [InlineData("REJECTED", false, true)]
    [InlineData("VALIDATION_FAILED", false, true)]
    [InlineData("UNKNOWN_RESULT", false, true)]
    [InlineData("FATURA_KONTROLDE", false, true)]
    [InlineData("FATURA_IPTAL", false, true)]
    [InlineData("FATURA_BEKLIYOR", true, true)]
    [InlineData("FATURA_KESILDI", false, false)]
    [InlineData("FATURA_YUKLENDI", false, false)]
    [InlineData("COMPLETED", false, false)]
    [InlineData("INVOICED", false, false)]
    public void OnlyConfirmedOrUploadedInvoicesAppearInInvoicedTab(string invoiceStatus, bool canCreateInvoice, bool expectedUninvoiced)
    {
        Assert.Equal(expectedUninvoiced, InvoicingBillingService.IsWorkspaceInvoiceUninvoiced(invoiceStatus, canCreateInvoice));
    }

    [Fact]
    public void InvoiceStatusComparisonIsCaseInsensitive()
    {
        Assert.False(InvoicingBillingService.IsWorkspaceInvoiceUninvoiced("invoiced", canCreateInvoice: false));
    }
}
