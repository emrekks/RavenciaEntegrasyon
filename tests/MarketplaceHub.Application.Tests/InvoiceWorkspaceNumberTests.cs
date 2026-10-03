using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class InvoiceWorkspaceNumberTests
{
    [Fact]
    public void Prefers_the_local_invoice_number_when_both_sources_have_values()
    {
        Assert.Equal("LOCAL-2026-001", InvoicingBillingService.ResolveInvoiceNumber("LOCAL-2026-001", "TY-2026-001"));
    }

    [Fact]
    public void Falls_back_to_marketplace_number_when_local_number_is_missing()
    {
        Assert.Equal("TY-2026-001", InvoicingBillingService.ResolveInvoiceNumber(null, "TY-2026-001"));
    }

    [Fact]
    public void Returns_null_when_neither_source_has_a_number()
    {
        Assert.Null(InvoicingBillingService.ResolveInvoiceNumber(" ", null));
    }
}
