using System.Net;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Adapters.N11;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class N11AdapterTests
{
    private const string ProductPage = """
        {"content":[{"n11ProductId":123456789,"sellerId":9876543,"sellerNickname":"İstanbul Mağaza","stockCode":"SKU-1"}],
         "pageable":{"pageNumber":0,"pageSize":1},"totalElements":1,"totalPages":1,"number":0,"size":1}
        """;
    private const string EmptyProductPage = """{"content":[],"totalElements":0,"totalPages":0,"number":0,"size":1}""";
    private const string PackagePage = """{"pageCount":1,"totalPages":1,"page":0,"size":1,"content":[]}""";

    [Fact]
    public void EndpointsUseTheDocumentedRestPaths()
    {
        var start = DateTimeOffset.FromUnixTimeMilliseconds(1_724_274_054_460);
        var end = DateTimeOffset.FromUnixTimeMilliseconds(1_725_310_828_346);

        Assert.Equal("ms/product-query?page=0&size=1", N11HttpClient.ProductQuery(0, 1));
        Assert.Equal("rest/delivery/v1/shipmentPackages?startDate=1724274054460&endDate=1725310828346&page=2&size=100&orderByDirection=DESC", N11HttpClient.ShipmentPackages(start, end, 2, 100));
    }

    [Fact]
    public async Task ConnectionTestSendsAppKeyHeadersAndReturnsTheSellerIdentity()
    {
        var handler = new RoutingHandler(("ms/product-query", HttpStatusCode.OK, ProductPage));
        var result = await Client(handler).TestAsync(Account("istanbul mağaza"), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("N11", result.Value!.PlatformCode);
        Assert.Equal("istanbul mağaza", result.Value.ExternalStoreId);
        Assert.Equal("9876543", result.Value.ScopeFingerprint);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://api.n11.com/ms/product-query?page=0&size=1", request.Uri.ToString());
        Assert.Equal("key-1", request.AppKey);
        Assert.Equal("secret-1", request.AppSecret);
    }

    [Fact]
    public async Task ConnectionTestRejectsKeysOfAnotherStore()
    {
        var handler = new RoutingHandler(("ms/product-query", HttpStatusCode.OK, ProductPage));
        var result = await Client(handler).TestAsync(Account("Başka Mağaza"), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("N11_STORE_MISMATCH", result.Error!.Code);
        Assert.Contains("İstanbul Mağaza", result.Error.SafeMessage);
    }

    [Fact]
    public async Task ConnectionTestPassesForAStoreWithoutProducts()
    {
        var handler = new RoutingHandler(("ms/product-query", HttpStatusCode.OK, EmptyProductPage));
        var result = await Client(handler).TestAsync(Account("Yeni Mağaza"), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("Yeni Mağaza", result.Value!.ScopeFingerprint);
    }

    [Fact]
    public async Task RejectedCredentialsMapToAnAuthenticationError()
    {
        var handler = new RoutingHandler(("ms/product-query", HttpStatusCode.Unauthorized, """{"message":"Unauthorized"}"""));
        var result = await Client(handler).TestAsync(Account("İstanbul Mağaza"), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(AdapterErrorClass.Authentication, result.Error!.Class);
        Assert.Equal("N11_CREDENTIALS_REJECTED", result.Error.Code);
    }

    [Fact]
    public async Task CapabilityProbeProvesProductAndOrderReadsOnly()
    {
        var handler = new RoutingHandler(
            ("ms/product-query", HttpStatusCode.OK, ProductPage),
            ("rest/delivery/v1/shipmentPackages", HttpStatusCode.OK, PackagePage));
        var result = await Client(handler).DiscoverCapabilitiesAsync(Account("İstanbul Mağaza"), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(
            new[] { MarketplaceCapabilities.ConnectionTest, MarketplaceCapabilities.ProductRead, MarketplaceCapabilities.OrderRead },
            result.Value!.Select(x => x.Code).ToArray());
        Assert.All(result.Value!, evidence =>
        {
            Assert.Equal("SUPPORTED", evidence.SupportLevel);
            Assert.Equal("developer.n11.com", new Uri(evidence.SourceUrl).Host);
        });
        Assert.All(handler.Requests, request => Assert.Equal(HttpMethod.Get.Method, request.Method));
    }

    [Fact]
    public async Task CapabilityProbeLeavesOrderReadUnknownWhenPackagesFail()
    {
        var handler = new RoutingHandler(
            ("ms/product-query", HttpStatusCode.OK, ProductPage),
            ("rest/delivery/v1/shipmentPackages", HttpStatusCode.Forbidden, "{}"));
        var result = await Client(handler).DiscoverCapabilitiesAsync(Account("İstanbul Mağaza"), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("UNKNOWN", result.Value!.Single(x => x.Code == MarketplaceCapabilities.OrderRead).SupportLevel);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, AdapterErrorClass.RateLimit, "N11_RATE_LIMITED")]
    [InlineData(HttpStatusCode.BadGateway, AdapterErrorClass.Remote5xx, "N11_REMOTE_ERROR")]
    [InlineData(HttpStatusCode.NotFound, AdapterErrorClass.NotFound, "N11_NOT_FOUND")]
    [InlineData(HttpStatusCode.BadRequest, AdapterErrorClass.Validation, "N11_REQUEST_REJECTED")]
    public void ErrorsMapByStatus(HttpStatusCode status, AdapterErrorClass expectedClass, string expectedCode)
    {
        var error = N11HttpClient.Error(status, """{"reasons":["Satıcı stok kodu kullanılmaktadır."]}""", null);

        Assert.Equal(expectedClass, error.Class);
        Assert.Equal(expectedCode, error.Code);
        if (status == HttpStatusCode.BadRequest) Assert.Contains("Satıcı stok kodu kullanılmaktadır.", error.SafeMessage);
    }

    [Theory]
    [InlineData(" abc-123 ", "abc-123")]
    [InlineData("​abc﻿123 ", "abc123")]
    [InlineData("a b\tc\r\n", "abc")]
    public void CredentialFormatDropsSpacesAndInvisibleCharacters(string pasted, string expected) =>
        Assert.Equal(expected, N11CredentialFormat.Normalize(pasted));

    [Theory]
    [InlineData("anahtarİ")]
    [InlineData("şifre")]
    [InlineData("   ")]
    [InlineData(null)]
    public void CredentialFormatRejectsNonAsciiOrEmptyValues(string? pasted) =>
        Assert.Null(N11CredentialFormat.Normalize(pasted));

    [Fact]
    public async Task NonAsciiCredentialIsReportedWithoutSendingTheRequest()
    {
        var handler = new RoutingHandler(("ms/product-query", HttpStatusCode.OK, ProductPage));
        var account = Account("İstanbul Mağaza") with { AppSecret = "gizliş" };

        var result = await Client(handler).TestAsync(account, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("N11_CREDENTIAL_FORMAT_INVALID", result.Error!.Code);
        Assert.Empty(handler.Requests);
    }

    private static N11RequestContext Account(string store) => new(
        new PlatformConnection { PlatformCode = "N11", Environment = "PRODUCTION", DisplayName = "N11", ExternalStoreId = store, Status = "DRAFT", ApiVersion = "V1" },
        "key-1",
        "secret-1");

    private static N11HttpClient Client(HttpMessageHandler handler) => new(
        new FixedHttpClientFactory(handler),
        null!,
        Options.Create(new N11Options()),
        TimeProvider.System,
        NullLogger<N11HttpClient>.Instance);

    private sealed class FixedHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class RoutingHandler(params (string PathPrefix, HttpStatusCode Status, string Body)[] routes) : HttpMessageHandler
    {
        public List<(string Method, Uri Uri, string? AppKey, string? AppSecret)> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.Method.Method, request.RequestUri!,
                request.Headers.TryGetValues("appkey", out var keys) ? keys.Single() : null,
                request.Headers.TryGetValues("appsecret", out var secrets) ? secrets.Single() : null));
            var route = routes.First(x => request.RequestUri!.AbsolutePath.TrimStart('/').StartsWith(x.PathPrefix, StringComparison.Ordinal));
            return Task.FromResult(new HttpResponseMessage(route.Status)
            {
                Content = new StringContent(route.Body, System.Text.Encoding.UTF8, "application/json")
            });
        }
    }
}
