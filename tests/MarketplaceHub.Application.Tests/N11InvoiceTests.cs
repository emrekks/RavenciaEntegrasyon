using MarketplaceHub.Application;
using MarketplaceHub.Infrastructure.Adapters.N11;
using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class N11InvoiceTests
{
    private const string ContentLink = "https://panel.example.com/api/v1/public/invoice-documents/abc-DEF_123/content";
    private const string PdfLink = "https://panel.example.com/api/v1/public/invoice-documents/abc-DEF_123/invoice.pdf";

    [Fact]
    public void DocumentBoundLinksGetAPdfAliasOtherLinksStayUnchanged()
    {
        Assert.Equal(PdfLink, InvoiceDocumentLink.PdfAlias(ContentLink));
        Assert.Equal("https://provider.example.com/doc/content", InvoiceDocumentLink.PdfAlias("https://provider.example.com/doc/content"));
    }

    [Fact]
    public void DeliveryPayloadBecomesTheSaveLinkRequest()
    {
        var issuedAt = new DateTimeOffset(2026, 10, 15, 9, 30, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var request = N11HttpClient.InvoiceLinkRequest($$"""{"invoiceLink":"{{ContentLink}}","orderNumber":"205412345678","invoiceNumber":"RVN2026000000123","invoiceDateTime":{{issuedAt}}}""");

        Assert.Null(request.Error);
        Assert.Equal(PdfLink, request.Link);
        Assert.Equal(ContentLink, request.StoredLink);
        Assert.Equal("205412345678", request.OrderNumber);
        Assert.Equal(issuedAt.ToString(System.Globalization.CultureInfo.InvariantCulture), request.InvoiceDateTime);
    }

    [Theory]
    [InlineData("N112026000000001", true)]
    [InlineData("RVN2026000000123", true)]
    [InlineData("A1B2099123456789", true)]
    [InlineData("RVN2019000000123", false)]
    [InlineData("RVN20260000001", false)]
    [InlineData("RVN202600000012X", false)]
    [InlineData("RV-2026000000123", false)]
    public void InvoiceNumberFollowsN11sRule(string number, bool valid)
    {
        Assert.Equal(valid, N11HttpClient.IsInvoiceNumber(number));
        var request = N11HttpClient.InvoiceLinkRequest($$"""{"invoiceLink":"{{ContentLink}}","orderNumber":"205412345678","invoiceNumber":"{{number}}","invoiceDateTime":1780903800000}""");
        Assert.Equal(valid, request.Error is null);
    }

    [Theory]
    [InlineData("""{"invoiceLink":"http://panel.example.com/a.pdf","orderNumber":"1","invoiceNumber":"X","invoiceDateTime":1}""")]
    [InlineData("""{"invoiceLink":"https://provider.example.com/download?id=1","orderNumber":"1","invoiceNumber":"X","invoiceDateTime":1}""")]
    [InlineData("""{"invoiceLink":"https://panel.example.com/a.pdf","invoiceNumber":"X","invoiceDateTime":1}""")]
    public void LinksN11WouldRejectAreStoppedBeforeTheCall(string payload)
    {
        Assert.NotNull(N11HttpClient.InvoiceLinkRequest(payload).Error);
    }

    [Fact]
    public void SoapSuccessReturnsTheResponseElement()
    {
        const string body = """
            <SOAP-ENV:Envelope xmlns:SOAP-ENV="http://schemas.xmlsoap.org/soap/envelope/"><SOAP-ENV:Body>
              <ns3:SaveLinkSellerInvoiceResponse xmlns:ns3="http://www.n11.com/ws/schemas">
                <result><status>success</status></result>
                <url>https://panel.example.com/x/invoice.pdf</url><orderNumber>205412345678</orderNumber>
                <invoiceDateTime>15/10/2026 12:30</invoiceDateTime><invoiceNumber>RVN2026000000123</invoiceNumber>
              </ns3:SaveLinkSellerInvoiceResponse>
            </SOAP-ENV:Body></SOAP-ENV:Envelope>
            """;

        var (response, error) = N11HttpClient.SoapResponse(body, "SaveLinkSellerInvoice");

        Assert.Null(error);
        Assert.Equal("RVN2026000000123", N11HttpClient.Child(response, "invoiceNumber"));
    }

    [Fact]
    public void SoapFailureCarriesN11sMessage()
    {
        const string body = """
            <SOAP-ENV:Envelope xmlns:SOAP-ENV="http://schemas.xmlsoap.org/soap/envelope/"><SOAP-ENV:Body>
              <ns3:SaveLinkSellerInvoiceResponse xmlns:ns3="http://www.n11.com/ws/schemas">
                <result><status>failure</status><errorCode>SELLER_API.invalidOrder</errorCode><errorMessage>Sipariş bulunamadı</errorMessage></result>
              </ns3:SaveLinkSellerInvoiceResponse>
            </SOAP-ENV:Body></SOAP-ENV:Envelope>
            """;

        var (response, error) = N11HttpClient.SoapResponse(body, "SaveLinkSellerInvoice");

        Assert.Null(response);
        Assert.Equal(AdapterErrorClass.Validation, error!.Class);
        Assert.Contains("Sipariş bulunamadı", error.SafeMessage);
    }

    [Fact]
    public void EnvelopeEscapesValues()
    {
        var envelope = N11HttpClient.SoapElement("url", "https://a.example.com/x?a=1&b=2");

        Assert.Equal("<url>https://a.example.com/x?a=1&amp;b=2</url>", envelope);
    }
}
