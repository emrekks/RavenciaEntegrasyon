using System.Net;
using System.Text;
using System.Text.Json;
using MarketplaceHub.Infrastructure.Adapters;
using MarketplaceHub.Infrastructure.Adapters.Hepsiburada;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// Deep audit 20 Oct 2026, Hepsiburada contract items (C4, C17, C19-C21, C31-C33, C42).
public sealed class HepsiburadaContractTests
{
    [Theory]
    [InlineData("Ready", "IN_PROGRESS")]
    [InlineData("InProgress", "IN_PROGRESS")]
    [InlineData("Done", "COMPLETED")]
    public async Task TheListingUploadStatusesAreRead(string remoteStatus, string expected)
    {
        var handler = new Script(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = Json($$"""{"id":"71","status":"{{remoteStatus}}","total":2,"errors":[{"elementNo":1,"merchantSku":"SKU-2","hepsiburadaSku":"HB-2","errors":["OutOfPriceRange"]}]}""")
        });

        var result = await Client(handler).GetOperationAsync(Account(), "LISTING_INVENTORY:71", CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Code);
        Assert.Equal(expected, result.Value!.Status);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://listing.example/listings/merchantid/m-1/inventory-uploads/id/71", request.Uri.AbsoluteUri);
        if (expected == "COMPLETED")
        {
            var line = Assert.Single(result.Value.Lines);
            Assert.Equal("SKU-2", line.ExternalKey);
            Assert.Equal("OutOfPriceRange", line.ErrorCode);
        }
    }

    [Fact]
    public async Task AProductUpdate401IsToldApartFromAWrongServiceKey()
    {
        var handler = new Script(new HttpResponseMessage(HttpStatusCode.Unauthorized));

        var result = await Client(handler).GetOperationAsync(Account() with { CatalogBaseAddress = new Uri("https://mpop.example/product/") }, "PRODUCT_UPDATE:track-1", CancellationToken.None);

        Assert.Equal("HEPSIBURADA_PRODUCT_UPDATE_AUTH_REJECTED", result.Error?.Code);
        Assert.Equal(AdapterErrorClass.Authentication, result.Error!.Class);
        Assert.Equal("/ticket-api/api/integrator/status/track-1", Assert.Single(handler.Requests).Uri.AbsolutePath);
        Assert.Equal("HEPSIBURADA_CREDENTIALS_REJECTED", HepsiburadaHttpClient.Error(HttpStatusCode.Unauthorized, null, null).Code);
    }

    [Fact]
    public async Task TheCommonLabelAsksForJsonAndExplainsAMissingCommonBarcode()
    {
        var handler = new Script(new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = Json("""{"message":"common barcode not found"}""") });
        var factory = new Factory(handler);

        var result = await Client(factory).GetCommonLabelAsync(Account(), "PKG-1", "ZPL", CancellationToken.None);

        Assert.Equal("HEPSIBURADA_COMMON_LABEL_UNAVAILABLE", result.Error?.Code);
        Assert.Contains("ortak barkod", result.Error!.SafeMessage, StringComparison.Ordinal);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://oms.example/packages/merchantid/m-1/packagenumber/PKG-1/labels?format=zpl", request.Uri.AbsoluteUri);
        Assert.Contains("application/json", request.Accept);
        Assert.Contains("application/pdf", request.Accept);
        Assert.Equal(MarketplaceResponseLimits.MaxResponseBytes, Assert.Single(factory.Created).MaxResponseContentBufferSize);
    }

    [Theory]
    [InlineData("Ravencia Entegrasyon", true)]
    [InlineData("ravencia_tests/1.0", true)]
    [InlineData("Ravencİa", false)]
    [InlineData("invalid\r\nX-Header: value", false)]
    public void TheIntegratorNameIsSentAsWrittenWhenItIsPrintableAscii(string name, bool sent)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://oms.example/orders");

        Assert.Equal(sent, HepsiburadaHttpClient.ApplyAuthentication(request, Account() with { IntegratorName = name }));
        if (sent)
        {
            Assert.True(request.Headers.NonValidated.TryGetValues("User-Agent", out var values));
            Assert.Equal(name, values.ToString());
        }
    }

    [Theory]
    [InlineData("newrequest", "NewRequest")]
    [InlineData("AWAITING_PRE_APPROVAL", "AwaitingPreApproval")]
    [InlineData("Refunded", "Refunded")]
    public void ClaimStatusesGoInThePathInTheirDocumentedCasing(string requested, string sent)
    {
        Assert.True(HepsiburadaHttpClient.TryClaimStatus(requested, out var status));
        Assert.Equal(sent, status);
        Assert.EndsWith($"/status/{sent}?offset=0&limit=10", HepsiburadaHttpClient.Claims(Account(), status, "offset=0&limit=10"), StringComparison.Ordinal);
    }

    [Fact]
    public void TheInventoryUploadCarriesAtMostOneThousandItems()
    {
        var items = string.Join(',', Enumerable.Range(1, 1001).Select(index => $$"""{"hepsiburadaSku":"HB-{{index}}","merchantSku":"SKU-{{index}}","price":10,"availableStock":1}"""));
        Assert.Equal(1000, HepsiburadaHttpClient.MaxInventoryUploadItems);
        Assert.False(HepsiburadaHttpClient.ValidPriceInventoryPayload($$"""{"merchantId":"m-1","items":[{{items}}]}""", "m-1"));
    }

    [Fact]
    public void TheQuestionListReadsTotalItemCountStatusSevenAndStockCode()
    {
        using var document = JsonDocument.Parse("""{"data":[{"issueNumber":"HB-7","status":7,"createdAt":"2026-10-02T10:00:00Z","product":{"sku":"HBCV0001","stockCode":"SKU-9","name":"Elbise"}}],"currentPage":1,"totalPageCount":3,"totalItemCount":61}""");

        var page = HepsiburadaQuestionMapper.Page(document, new("PRODUCT", null, null, null, 1, 25));

        var question = Assert.Single(page.Items);
        Assert.Equal("ANSWERED", question.Status);
        Assert.Equal("SKU-9", question.ProductSku);
        Assert.Equal(61, page.TotalElements);
        Assert.Equal(3, page.TotalPages);
    }

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    private static HepsiburadaHttpClient Client(Script handler) => Client(new Factory(handler));

    private static HepsiburadaHttpClient Client(Factory factory) => new(
        factory, null!, Options.Create(new HepsiburadaOptions { AuthenticationMode = "BASIC" }), TimeProvider.System, new ConfigurationBuilder().Build(), NullLogger<HepsiburadaHttpClient>.Instance);

    private static HepsiburadaRequestContext Account() => new(
        new MarketplaceHub.Domain.PlatformConnection { PlatformCode = "HEPSIBURADA", Environment = "PRODUCTION", DisplayName = "fixture", ExternalStoreId = "m-1", Status = "ACTIVE", ApiVersion = "V1.0" },
        new Uri("https://oms.example/"), new Uri("https://listing.example/"), "integrator", "fixture-key")
    { IntegratorName = "ravencia_tests/1.0" };

    private sealed record Recorded(HttpMethod Method, Uri Uri, string[] Accept);

    private sealed class Script(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private int next;
        public List<Recorded> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new(request.Method, request.RequestUri!, request.Headers.Accept.Select(value => value.MediaType ?? "").ToArray()));
            if (next >= responses.Length) throw new InvalidOperationException($"Unexpected request {request.Method} {request.RequestUri}");
            return Task.FromResult(responses[next++]);
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public List<HttpClient> Created { get; } = [];

        public HttpClient CreateClient(string name)
        {
            var client = new HttpClient(handler, disposeHandler: false);
            Created.Add(client);
            return client;
        }
    }
}
