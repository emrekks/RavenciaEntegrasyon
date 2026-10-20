using System.Xml.Linq;
using MarketplaceHub.Infrastructure.Adapters.N11;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// Deep audit 20 Oct 2026, n11 contract items (C8, C9, C23, C34, C37).
public sealed class N11ContractTests
{
    private const string Link = "https://panel.example.com/x/invoice.pdf";

    [Theory]
    [InlineData("<url>" + Link + "</url><orderNumber>205412345678</orderNumber>", true)]
    [InlineData("<url>" + Link + "</url><orderNumber>205412345678</orderNumber><invoiceNumber>RVN2026000000123</invoiceNumber>", true)]
    [InlineData("<url>" + Link + "</url><orderNumber>205412345678</orderNumber><invoiceNumber>RVN2026000000999</invoiceNumber>", false)]
    [InlineData("<url>" + Link + "</url><orderNumber>205400000000</orderNumber>", false)]
    [InlineData("<url>https://panel.example.com/other.pdf</url><orderNumber>205412345678</orderNumber>", false)]
    [InlineData("<orderNumber>205412345678</orderNumber>", false)]
    // An answer that echoes the link but not the order number is still verified by the link.
    [InlineData("<url>" + Link + "</url>", true)]
    [InlineData("<url>" + Link + "</url><orderNumber></orderNumber>", true)]
    [InlineData("<url>" + Link + "</url><invoiceNumber>RVN2026000000999</invoiceNumber>", false)]
    public void ADomesticInvoiceLinkIsVerifiedByTheEchoedLinkAndOrder(string echoed, bool verified)
    {
        var request = N11HttpClient.InvoiceLinkRequest($$"""{"invoiceLink":"{{Link}}","orderNumber":"205412345678","invoiceNumber":"RVN2026000000123","invoiceDateTime":1780903800000}""");
        Assert.Null(request.Error);
        var response = XElement.Parse("<SaveLinkSellerInvoiceResponse><result><status>success</status></result>" + echoed + "</SaveLinkSellerInvoiceResponse>");

        Assert.Equal(verified, N11HttpClient.InvoiceLinkVerified(response, request));
    }

    [Fact]
    public void TheReturnListAsksForEverySenderAfterThePeriod()
    {
        var body = N11HttpClient.ClaimReturnListBody(new DateTimeOffset(2026, 9, 1, 22, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 10, 15, 9, 0, 0, TimeSpan.Zero), 0);

        Assert.Equal("<searchData><status>ALL</status><period><startDate>02/09/2026</startDate><endDate>15/10/2026</endDate></period><sender>ALL</sender></searchData><pagingData><currentPage>0</currentPage></pagingData>", body);
    }

    [Fact]
    public void TheProductQueryListsEverySender() =>
        Assert.EndsWith("&sender=ALL", N11HttpClient.ProductPage(0, 250, new ProductReadFilter(null, Barcode: "SKU-1")), StringComparison.Ordinal);

    [Theory]
    [InlineData("<denyReasonTypeDataList><list><denyReasonTypeData><id>1</id><value>Kullanılmış</value></denyReasonTypeData></list><list><denyReasonTypeData><id>2</id><value>Eksik</value></denyReasonTypeData></list></denyReasonTypeDataList>")]
    [InlineData("<denyReasonTypeDataList><list><id>1</id><value>Kullanılmış</value></list><list><id>2</id><value>Eksik</value></list></denyReasonTypeDataList>")]
    public void DenyReasonsAreReadInBothShapes(string list)
    {
        var response = XElement.Parse("<ClaimReturnDenyReasonTypesResponse><result><status>success</status></result>" + list + "</ClaimReturnDenyReasonTypesResponse>");

        var reasons = N11HttpClient.DenyReasons(response);

        Assert.Equal(["1:Kullanılmış", "2:Eksik"], reasons.Select(reason => $"{reason.Id}:{reason.Name}").ToArray());
    }

    [Theory]
    [InlineData(2049)]
    [InlineData(0)]
    public async Task AnAnswerOutsideN11sLengthIsRefusedBeforeAnyCall(int length)
    {
        var handler = new NoCalls();
        var client = new N11HttpClient(new Factory(handler), null!, Options.Create(new N11Options()), TimeProvider.System, NullLogger<N11HttpClient>.Instance, new ConfigurationBuilder().Build());

        var result = await client.AnswerQuestionAsync(new AdapterContext(Guid.NewGuid(), Guid.NewGuid(), "c", "k", DateTimeOffset.UtcNow.AddMinutes(1)), "77", "PRODUCT", new string('a', length), CancellationToken.None);

        Assert.Equal("N11_ANSWER_LENGTH_INVALID", result.Error?.Code);
        Assert.Equal(0, handler.Calls);
        Assert.Equal(2048, N11HttpClient.MaxAnswerLength);
    }

    [Fact]
    public async Task AQuestionReportIsRefusedWithoutAnyCall()
    {
        var handler = new NoCalls();
        var client = new N11HttpClient(new Factory(handler), null!, Options.Create(new N11Options()), TimeProvider.System, NullLogger<N11HttpClient>.Instance, new ConfigurationBuilder().Build());

        var result = await client.ReportQuestionAsync(new AdapterContext(Guid.NewGuid(), Guid.NewGuid(), "c", "k", DateTimeOffset.UtcNow.AddMinutes(1)), "77", "PRODUCT", new("ADVERTISEMENT", "Reklam", null), CancellationToken.None);

        Assert.Equal("QUESTION_REPORT_UNSUPPORTED", result.Error?.Code);
        Assert.Equal(0, handler.Calls);
        Assert.False(client.ReportCapability("N11").Supported);
    }

    private sealed class NoCalls : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            throw new InvalidOperationException("No request is expected.");
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
