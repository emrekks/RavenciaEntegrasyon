using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class InvoiceWorkspaceCustomerNameTests
{
    [Fact]
    public void Reads_hepsiburada_customer_name_from_the_customer_snapshot()
    {
        Assert.Equal(
            "Aylin Örnek",
            InvoicingBillingService.InvoiceWorkspaceCustomerName(
                "{\"name\":\"Aylin Örnek\"}",
                "{}"));
    }

    [Fact]
    public void Prefers_structured_first_and_last_name_when_available()
    {
        Assert.Equal(
            "Aylin Örnek",
            InvoicingBillingService.InvoiceWorkspaceCustomerName(
                "{\"firstName\":\"Aylin\",\"lastName\":\"Örnek\",\"name\":\"Farklı Ad\"}",
                "{}"));
    }

    [Fact]
    public void Falls_back_to_shipment_name_when_invoice_address_has_no_name()
    {
        Assert.Equal(
            "Teslim Alan",
            InvoicingBillingService.InvoiceWorkspaceCustomerName(
                "{}",
                "{}",
                "{\"recipientName\":\"Teslim Alan\"}"));
    }
}
