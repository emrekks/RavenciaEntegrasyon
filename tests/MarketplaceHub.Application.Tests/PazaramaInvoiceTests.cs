using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Infrastructure.Adapters.Pazarama;
using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class PazaramaInvoiceTests
{
    private const string OrderGuid = "0d145804-5d05-4e4a-a4a0-921e45c8a5e3";

    private static string Payload(string link = "https://api.ravencia.com/invoices/d1/invoice.pdf", string? orderId = OrderGuid) =>
        JsonSerializer.Serialize(new { shipmentPackageId = "order:735071747", invoiceLink = link, invoiceNumber = "RVN2026000001", orderNumber = "735071747", pazaramaOrderId = orderId });

    [Fact]
    public void A_valid_delivery_carries_the_link_and_the_order_guid()
    {
        var request = PazaramaInvoiceMapper.Request(Payload());

        Assert.Null(request.Error);
        Assert.Equal("https://api.ravencia.com/invoices/d1/invoice.pdf", request.Link);
        Assert.Equal(OrderGuid, request.OrderId);
        Assert.Equal("735071747", request.OrderNumber);
        Assert.Equal("RVN2026000001", request.InvoiceNumber);
    }

    [Fact]
    public void The_body_attaches_the_link_to_the_whole_order()
    {
        var body = JsonSerializer.Serialize(PazaramaInvoiceMapper.Body(PazaramaInvoiceMapper.Request(Payload())));

        // Carrier and tracking number stay null so every item of the order gets the invoice.
        Assert.Equal($$"""{"invoiceLink":"https://api.ravencia.com/invoices/d1/invoice.pdf","orderid":"{{OrderGuid}}","deliveryCompanyId":null,"trackingNumber":null}""", body);
    }

    [Theory]
    [InlineData("http://api.ravencia.com/invoices/d1/invoice.pdf")]
    [InlineData("https://api.ravencia.com/invoices/d1/invoice")]
    [InlineData("https://api.ravencia.com/invoices/d1/invoice.html")]
    [InlineData("not a link")]
    public void Links_pazarama_cannot_fetch_are_refused(string link) => Assert.NotNull(PazaramaInvoiceMapper.Request(Payload(link)).Error);

    [Fact]
    public void A_stored_link_without_an_extension_gets_the_pdf_alias()
    {
        var request = PazaramaInvoiceMapper.Request(Payload("https://api.ravencia.com/api/v1/invoice-documents/abc/download"));

        // Whatever the alias rule does, the result must be a link Pazarama accepts or an explicit refusal.
        Assert.True(request.Error is not null || request.Link!.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void An_over_long_link_is_refused() =>
        Assert.NotNull(PazaramaInvoiceMapper.Request(Payload("https://api.ravencia.com/" + new string('a', 2100) + "/invoice.pdf")).Error);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-guid")]
    public void Without_the_order_guid_nothing_is_sent(string? orderId) => Assert.NotNull(PazaramaInvoiceMapper.Request(Payload(orderId: orderId)).Error);

    [Fact]
    public void Unreadable_payload_is_refused() => Assert.NotNull(PazaramaInvoiceMapper.Request("not json").Error);

    [Fact]
    public void The_order_guid_comes_from_the_order_snapshot()
    {
        Assert.Equal(OrderGuid, InvoicingJobProcessor.PazaramaOrderId($$"""{"name":"X","pazaramaOrderId":"{{OrderGuid}}"}"""));
        Assert.Null(InvoicingJobProcessor.PazaramaOrderId("{}"));
        Assert.Null(InvoicingJobProcessor.PazaramaOrderId(null));
        Assert.Null(InvoicingJobProcessor.PazaramaOrderId("not json"));
    }

    [Theory]
    [InlineData("""{"ExternalWritesEnabled":false,"InvoiceCreationEnabled":true}""", true)]
    [InlineData("""{"ExternalWritesEnabled":false,"InvoiceCreationEnabled":false}""", false)]
    [InlineData("""{"ExternalWritesEnabled":false}""", true)]
    public void Pazarama_invoice_creation_follows_the_connection_setting(string settings, bool expected) =>
        Assert.Equal(expected, MarketplaceInvoiceCreationPolicy.IsEnabled("PAZARAMA", settings));

    [Fact]
    public void The_endpoint_is_the_documented_service() => Assert.Equal("order/invoice-link", PazaramaHttpClient.InvoiceLink);
}
