using MarketplaceHub.Infrastructure.Persistence;

namespace MarketplaceHub.Application.Tests;

public sealed class F3ReturnCustomerNameTests
{
    [Fact]
    public void Uses_invoice_address_before_delivery_address_when_provider_customer_name_is_a_placeholder()
    {
        var name = F3SalesService.ResolveCustomerName(
            """{"customerFirstName":"Adı","customerLastName":"Soyadı"}""",
            """{"invoiceAddress":{"firstName":"Fatura","lastName":"Alıcısı"}}""",
            """{"shipmentAddress":{"firstName":"Ayşe","lastName":"Yılmaz"}}""");

        Assert.Equal("Fatura Alıcısı", name);
    }

    [Fact]
    public void Keeps_real_customer_name_before_address_fallbacks()
    {
        var name = F3SalesService.ResolveCustomerName(
            """{"customerFirstName":"Müşteri","customerLastName":"Adı"}""",
            """{"invoiceAddress":{"firstName":"Fatura","lastName":"Alıcısı"}}""",
            """{"shipmentAddress":{"firstName":"Teslimat","lastName":"Alıcısı"}}""");

        Assert.Equal("Müşteri Adı", name);
    }

    [Fact]
    public void Returns_dash_when_provider_does_not_send_a_meaningful_name()
    {
        var name = F3SalesService.ResolveCustomerName(
            """{"customerFirstName":"Adı","customerLastName":"Soyadı"}""",
            """{}""",
            """{}""");

        Assert.Equal("—", name);
    }
}
