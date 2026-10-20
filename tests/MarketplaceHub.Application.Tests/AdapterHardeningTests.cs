using System.Net;
using System.Text;
using System.Text.Json;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Adapters;
using MarketplaceHub.Infrastructure.Adapters.Hepsiburada;
using MarketplaceHub.Infrastructure.Adapters.Pazarama;
using MarketplaceHub.Infrastructure.Adapters.Shopify;
using MarketplaceHub.Infrastructure.Adapters.Trendyol;
using MarketplaceHub.Infrastructure.Adapters.TrendyolEFaturam;
using MarketplaceHub.Infrastructure.Adapters.TrendyolEFaturam.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class AdapterHardeningTests
{
    private static readonly PlatformConnection Connection = new() { PlatformCode = "X", Environment = "PRODUCTION", Status = "ACTIVE", DisplayName = "x", ExternalStoreId = "store", ApiVersion = "V1" };

    [Fact]
    public void ContextsThatCarrySecretsDoNotPrintThem()
    {
        var printed = new[]
        {
            new TrendyolRequestContext(Connection, new Uri("https://unit.test/"), "secret-api-key", "secret-api-secret", "store - SelfIntegration", false).ToString(),
            new HepsiburadaRequestContext(Connection, new Uri("https://oms.test/"), new Uri("https://listing.test/"), "integrator", "secret-service-key").ToString(),
            new ShopifyRequestContext(Connection, "store", "2026-10", "secret-access-token", DateTimeOffset.MaxValue).ToString(),
            new TrendyolEFaturamCredentialPayload("owner@example.com", "secret-password").ToString(),
            new TrendyolEFaturamRequestContext(Connection, new Uri("https://efaturam.test/"), new("owner@example.com", "secret-password"), new(false), Guid.NewGuid()).ToString(),
            new TrendyolEFaturamAccessContext("secret-bearer-token", 7, 9).ToString()
        };

        Assert.All(printed, text =>
        {
            Assert.DoesNotContain("secret", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("owner@example.com", text, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task AnOversizedBufferedAnswerFailsInsteadOfFillingMemory()
    {
        var oversized = new string('x', (int)MarketplaceResponseLimits.MaxResponseBytes + 1024);
        var client = new HepsiburadaHttpClient(new Factory(new Answer(oversized)), null!, Options.Create(new HepsiburadaOptions { AuthenticationMode = "BASIC" }), TimeProvider.System, new ConfigurationBuilder().Build(), NullLogger<HepsiburadaHttpClient>.Instance);
        var account = new HepsiburadaRequestContext(Connection, new Uri("https://oms.example/"), new Uri("https://listing.example/"), "integrator", "key") { IntegratorName = "ravencia_tests/1.0" };

        var result = await client.GetPackageTrackingInfoAsync(account, "P-1", CancellationToken.None);

        Assert.Equal("HEPSIBURADA_NETWORK_ERROR", result.Error!.Code);
    }

    [Fact]
    public async Task AStreamedAnswerIsReadUpToTheLimitOnly()
    {
        using var small = new StringContent("{\"ok\":true}", Encoding.UTF8, "application/json");
        using var large = new StreamContent(new MemoryStream(new byte[4096]));

        Assert.Equal("{\"ok\":true}", await small.ReadBoundedStringAsync(CancellationToken.None, maxBytes: 1024));
        await Assert.ThrowsAsync<HttpRequestException>(() => large.ReadBoundedStringAsync(CancellationToken.None, maxBytes: 1024));
    }

    [Fact]
    public async Task AnAnswerAnnouncedLargerThanTheLimitIsNotRead()
    {
        using var content = new StringContent("short");
        content.Headers.ContentLength = 10_000;

        await Assert.ThrowsAsync<HttpRequestException>(() => content.ReadBoundedStringAsync(CancellationToken.None, maxBytes: 1024));
    }

    [Theory]
    [InlineData("2026-10-19T14:19:58", "2026-10-19T11:19:58Z")]
    [InlineData("2026-10-19T14:19:58Z", "2026-10-19T14:19:58Z")]
    [InlineData("2026-10-19T14:19:58+03:00", "2026-10-19T11:19:58Z")]
    public void TurkishMarketplaceTimestampsWithoutAnOffsetAreTurkeyTime(string text, string expected) =>
        Assert.Equal(DateTimeOffset.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), TurkeyTime.Parse(text));

    [Fact]
    public void HepsiburadaQuestionDatesFollowTheSameTurkeyTimePolicyAsItsOrders()
    {
        using var document = JsonDocument.Parse("""{"data":{"issue":{"number":"HB-1","status":"WaitingForAnswer","createdAt":"2026-10-19T14:19:58","lastContent":"Soru"}}}""");

        var question = HepsiburadaQuestionMapper.Single(document, "PRODUCT");

        Assert.Equal(new DateTimeOffset(2026, 10, 19, 11, 19, 58, TimeSpan.Zero), question!.CreatedAt);
    }

    [Fact]
    public void PazaramaItemHistoryWithoutAnOffsetIsTurkeyTimeLikeTheOrderDate()
    {
        using var json = JsonDocument.Parse("""
        {"data":[{"orderId":"o-1","orderNumber":283534307,"orderDate":"2026-02-17 17:23","orderAmount":7,"currency":"TL",
          "items":[{"orderItemId":"i-1","orderItemStatus":3,"orderItemStatusName":"x",
            "orderItemStatusHistory":[{"historyStatus":3,"historyCreatedDate":"2026-02-17T17:24:54"}],
            "quantity":1,"salePrice":{"currency":"TL","value":7},"totalPrice":{"currency":"TL","value":7},
            "product":{"productId":"p","name":"Ürün","stockCode":"212","code":"testurunu001","vatRate":10}}]}]}
        """);

        var order = PazaramaOrderMapper.Page(json.RootElement, 100).Items.Single();

        Assert.Equal(new DateTimeOffset(2026, 2, 17, 14, 23, 0, TimeSpan.Zero), order.OrderedAt);
        Assert.Equal(new DateTimeOffset(2026, 2, 17, 14, 24, 54, TimeSpan.Zero), order.LastModifiedAt);
    }

    private sealed class Answer(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
