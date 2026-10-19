using System.Net;
using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure;
using MarketplaceHub.Infrastructure.Adapters.Pazarama;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class PazaramaAdapterTests
{
    [Fact]
    public void TokenResponseCarriesTheAccessTokenAndLifetime()
    {
        var token = PazaramaHttpClient.TokenResponse(HttpStatusCode.OK, """{"access_token":"abc","expires_in":3600,"token_type":"Bearer","scope":"merchantgatewayapi.fullaccess"}""");

        Assert.True(token.IsSuccess);
        Assert.Equal("abc", token.AccessToken);
        Assert.Equal(TimeSpan.FromHours(1), token.ExpiresIn);
        Assert.DoesNotContain("abc", token.ToString());
    }

    [Theory]
    [InlineData("""{"data":{"accessToken":"env-token","expiresIn":1800,"tokenType":"Bearer"},"success":true,"messageCode":null}""", "env-token", 1800)]
    [InlineData("""{"data":{"access_token":"env2","expires_in":900},"success":true}""", "env2", 900)]
    [InlineData("""{"AccessToken":"upper","ExpiresIn":60}""", "upper", 60)]
    [InlineData("""{"data":"bare-token","success":true}""", "bare-token", 3600)]
    public void TokenInsideThePazaramaEnvelopeIsRead(string body, string expectedToken, int seconds)
    {
        var token = PazaramaHttpClient.TokenResponse(HttpStatusCode.OK, body);

        Assert.True(token.IsSuccess);
        Assert.Equal(expectedToken, token.AccessToken);
        Assert.Equal(TimeSpan.FromSeconds(seconds), token.ExpiresIn);
    }

    [Fact]
    public void AnEnvelopeSayingSuccessFalseIsRejectedCredentialsWithTheMessage()
    {
        var token = PazaramaHttpClient.TokenResponse(HttpStatusCode.OK, """{"data":null,"success":false,"userMessage":"Geçersiz anahtar"}""");

        Assert.Equal("PAZARAMA_CREDENTIALS_REJECTED", token.Error!.Code);
        Assert.Contains("Geçersiz anahtar", token.Error.SafeMessage);
    }

    [Fact]
    public void AnUnknownTokenAnswerNamesItsFieldsButNeverTheirValues()
    {
        var token = PazaramaHttpClient.TokenResponse(HttpStatusCode.OK, """{"result":{"jwt":"super-secret-value"},"ok":true}""");

        Assert.Equal("PAZARAMA_TOKEN_INVALID", token.Error!.Code);
        Assert.Contains("result.jwt", token.Error.SafeMessage);
        Assert.DoesNotContain("super-secret-value", token.Error.SafeMessage);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, """{"error":"invalid_client"}""", "PAZARAMA_CREDENTIALS_REJECTED")]
    [InlineData(HttpStatusCode.Unauthorized, "", "PAZARAMA_CREDENTIALS_REJECTED")]
    [InlineData(HttpStatusCode.BadRequest, """{"error":"invalid_scope"}""", "PAZARAMA_SCOPE_DENIED")]
    [InlineData(HttpStatusCode.TooManyRequests, "", "PAZARAMA_RATE_LIMITED")]
    [InlineData(HttpStatusCode.BadGateway, "", "PAZARAMA_REMOTE_ERROR")]
    [InlineData(HttpStatusCode.OK, """{"token_type":"Bearer"}""", "PAZARAMA_TOKEN_INVALID")]
    public void TokenFailuresAreClassified(HttpStatusCode status, string body, string code)
    {
        var token = PazaramaHttpClient.TokenResponse(status, body);

        Assert.False(token.IsSuccess);
        Assert.Equal(code, token.Error!.Code);
    }

    [Fact]
    public void ASuccessfulHttpAnswerWithSuccessFalseIsARejection()
    {
        using var refused = JsonDocument.Parse("""{"data":null,"success":false,"messageCode":"CAT_133","message":"Aradığınız Ürün Kodu (Barkod) ürünleriniz arasında bulunmaktadır.","userMessage":"Aradığınız Ürün Kodu (Barkod) ürünleriniz arasında bulunmaktadır."}""");
        using var accepted = JsonDocument.Parse("""{"data":[],"success":true,"messageCode":null,"message":null,"userMessage":null,"fromCache":false}""");

        var error = PazaramaHttpClient.EnvelopeError(refused.RootElement);

        Assert.Equal("PAZARAMA_REQUEST_REJECTED", error!.Code);
        Assert.Contains("ürünleriniz arasında bulunmaktadır", error.SafeMessage);
        Assert.Null(PazaramaHttpClient.EnvelopeError(accepted.RootElement));
    }

    [Fact]
    public void RejectionWithOnlyACodeNamesTheCode()
    {
        using var refused = JsonDocument.Parse("""{"success":false,"messageCode":"ORD_12","message":"null","userMessage":null}""");

        Assert.Contains("ORD_12", PazaramaHttpClient.EnvelopeError(refused.RootElement)!.SafeMessage);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "PAZARAMA_CREDENTIALS_REJECTED", AdapterErrorClass.Authentication)]
    [InlineData(HttpStatusCode.Forbidden, "PAZARAMA_ACCESS_DENIED", AdapterErrorClass.Authentication)]
    [InlineData(HttpStatusCode.TooManyRequests, "PAZARAMA_RATE_LIMITED", AdapterErrorClass.RateLimit)]
    [InlineData(HttpStatusCode.NotFound, "PAZARAMA_NOT_FOUND", AdapterErrorClass.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError, "PAZARAMA_REMOTE_ERROR", AdapterErrorClass.Remote5xx)]
    [InlineData(HttpStatusCode.BadRequest, "PAZARAMA_REQUEST_REJECTED", AdapterErrorClass.Validation)]
    public void HttpErrorsAreMapped(HttpStatusCode status, string code, AdapterErrorClass @class)
    {
        var error = PazaramaHttpClient.Error(status, """{"userMessage":"Geçersiz istek"}""", null);

        Assert.Equal(code, error.Code);
        Assert.Equal(@class, error.Class);
    }

    [Fact]
    public void AccessDeniedKeepsPazaramasWordsAndWaitsFifteenMinutes()
    {
        // A 403 arrives with a token Pazarama has just issued, so the credentials are not the problem.
        var error = PazaramaHttpClient.Error(HttpStatusCode.Forbidden, """{"userMessage":"Merchant API access is suspended"}""", null);

        Assert.Equal("PAZARAMA_ACCESS_DENIED", error.Code);
        Assert.Contains("Merchant API access is suspended", error.SafeMessage);
        Assert.DoesNotContain("Client Id ve Client Secret değerlerini girin", error.SafeMessage);
        Assert.Equal(TimeSpan.FromMinutes(15), error.RetryAfter);
    }

    [Fact]
    public void AGatewayBlockPageIsNotAnAccessRefusal()
    {
        // Seen live: Pazarama's gateway answered 403 with a 15 KB HTML page, and the next call went through.
        var error = PazaramaHttpClient.Error(HttpStatusCode.Forbidden, "<!DOCTYPE html><html><head><title>Access denied</title></head><body>...</body></html>", null);

        Assert.Equal("PAZARAMA_GATEWAY_BLOCKED", error.Code);
        Assert.Equal(AdapterErrorClass.RateLimit, error.Class);
        Assert.Equal(TimeSpan.FromMinutes(15), error.RetryAfter);
        Assert.Contains("izinli listeye", error.SafeMessage);
        Assert.True(PazaramaHttpClient.LooksLikeHtml("  <html lang=\"en\">"));
        Assert.False(PazaramaHttpClient.LooksLikeHtml("{\"userMessage\":\"x\"}"));
        Assert.False(PazaramaHttpClient.LooksLikeHtml(""));
    }

    [Fact]
    public void ValidationProblemDetailsAreReadable()
    {
        Assert.Equal("StartDate zorunludur.", PazaramaHttpClient.RemoteMessage("""{"errors":{"StartDate":["StartDate zorunludur."]}}"""));
    }

    [Fact]
    public void OrderProbeAsksForYesterdayThroughTomorrowInTurkeyTime()
    {
        // 22:30 UTC is already the next day in Turkey (UTC+3).
        var body = JsonSerializer.Serialize(PazaramaHttpClient.OrderProbeBody(new DateTimeOffset(2026, 10, 15, 22, 30, 0, TimeSpan.Zero)));

        Assert.Equal("""{"pageSize":1,"pageNumber":1,"startDate":"2026-10-15","endDate":"2026-10-17"}""", body);
    }

    [Theory]
    [InlineData("https://isortagimapi.pazarama.com/product/updatePrice-v2", "price-stock")]
    [InlineData("https://isortagimapi.pazarama.com/product/updateStock-v2", "price-stock")]
    [InlineData("https://isortagimapi.pazarama.com/product/create", "product-create")]
    [InlineData("https://isortagimapi.pazarama.com/order/getOrdersForApi", "orders")]
    [InlineData("https://isortagimapi.pazarama.com/order/updateOrderStatus", "orders")]
    [InlineData("https://isortagimapi.pazarama.com/brand/getBrands?Page=1", "reads")]
    [InlineData("https://isortagimapi.pazarama.com/product/products/approved?Size=1", "reads")]
    public void RequestsAreCountedInTheirPazaramaBucket(string url, string bucket) =>
        Assert.Equal(bucket, PazaramaRateLimitHandler.BucketFor(new Uri(url)));

    [Fact]
    public void TokenRequestsAreNotCounted() =>
        Assert.Null(PazaramaRateLimitHandler.BucketFor(new Uri("https://isortagimgiris.pazarama.com/connect/token")));

    [Fact]
    public void PriceStockAndCreateAreSpacedAboveTheDocumentedTenSeconds()
    {
        var options = new PazaramaOptions();

        Assert.Equal(1d / 12.5, PazaramaRateLimitHandler.BucketShape(options, "price-stock").TokensPerSecond, 6);
        Assert.Equal(1d / 12.5, PazaramaRateLimitHandler.BucketShape(options, "product-create").TokensPerSecond, 6);
        Assert.Equal(1d, PazaramaRateLimitHandler.BucketShape(options, "price-stock").Capacity);
    }

    [Theory]
    [InlineData("PRODUCTION", "VERIFIED")]
    [InlineData("PRODUCTION", "ACTIVE")]
    public void WritesPassOnlyWhenEverySwitchIsOn(string environment, string status) =>
        Assert.Null(Client(true).WriteBlocked<bool>(Account(environment, status, true), "test"));

    [Theory]
    [InlineData(false, "PRODUCTION", "VERIFIED", true)]
    [InlineData(true, "PRODUCTION", "VERIFIED", false)]
    [InlineData(true, "STAGE", "VERIFIED", true)]
    [InlineData(true, "PRODUCTION", "DRAFT", true)]
    public void AnyClosedSwitchBlocksTheWrite(bool global, string environment, string status, bool connectionWrites)
    {
        var blocked = Client(global).WriteBlocked<bool>(Account(environment, status, connectionWrites), "fiyat-stok");

        Assert.Equal("PAZARAMA_EXTERNAL_WRITES_DISABLED", blocked!.Error!.Code);
        Assert.Contains("fiyat-stok gönderilmedi", blocked.Error.SafeMessage);
    }

    [Fact]
    public void LoggedRequestContextDoesNotPrintTheClientSecret()
    {
        var context = Account("PRODUCTION", "ACTIVE", false);

        Assert.DoesNotContain("my-client-secret", context.ToString());
        Assert.DoesNotContain("my-client-id", context.ToString());
    }

    [Fact]
    public async Task OneTokenIsSharedUntilItIsAboutToExpire()
    {
        var clock = new ManualClock(new DateTimeOffset(2026, 10, 15, 10, 0, 0, TimeSpan.Zero));
        var cache = new PazaramaTokenCache(clock);
        var credentialId = Guid.NewGuid();
        var issued = 0;
        Task<PazaramaToken> Issue(CancellationToken _) { issued++; return Task.FromResult(new PazaramaToken($"t{issued}", TimeSpan.FromHours(1), null)); }

        var first = await cache.GetAsync(credentialId, Issue, CancellationToken.None);
        clock.Advance(TimeSpan.FromMinutes(54));
        var second = await cache.GetAsync(credentialId, Issue, CancellationToken.None);
        clock.Advance(TimeSpan.FromMinutes(2));
        var renewed = await cache.GetAsync(credentialId, Issue, CancellationToken.None);

        Assert.Equal("t1", first.AccessToken);
        Assert.Equal("t1", second.AccessToken);
        Assert.Equal("t2", renewed.AccessToken);
    }

    [Fact]
    public async Task AForgottenOrFailedTokenIsRequestedAgain()
    {
        var cache = new PazaramaTokenCache(TimeProvider.System);
        var credentialId = Guid.NewGuid();
        var issued = 0;
        Task<PazaramaToken> Fail(CancellationToken _) { issued++; return Task.FromResult(new PazaramaToken(null, TimeSpan.Zero, new(AdapterErrorClass.Authentication, "PAZARAMA_CREDENTIALS_REJECTED", "x", 401, null, null))); }
        Task<PazaramaToken> Issue(CancellationToken _) { issued++; return Task.FromResult(new PazaramaToken("ok", TimeSpan.FromHours(1), null)); }

        await cache.GetAsync(credentialId, Fail, CancellationToken.None);
        await cache.GetAsync(credentialId, Issue, CancellationToken.None);
        cache.Forget(credentialId);
        await cache.GetAsync(credentialId, Issue, CancellationToken.None);

        Assert.Equal(3, issued);
    }

    [Fact]
    public void PazaramaConnectionsRouteToThePazaramaAdapter()
    {
        object trendyol = new(), shopify = new(), pazarama = new();

        Assert.Same(pazarama, MarketplacePortRouter.SelectPort("pazarama", trendyol, shopify, pazarama: pazarama));
        Assert.Throws<InvalidOperationException>(() => MarketplacePortRouter.SelectPort("PAZARAMA", trendyol, shopify));
    }

    [Fact]
    public void PazaramaIsAMarketplaceWithItsOwnConnectionTestAndInvoicingFollowsTheConnectionSetting()
    {
        Assert.True(ActiveIntegrationScope.IsMarketplace("PAZARAMA"));
        Assert.Equal(MarketplaceJobTypes.PazaramaConnectionTest, MarketplaceJobTypes.ForPlatform("PAZARAMA", MarketplaceJobTypes.ConnectionTest));
        Assert.Equal(MarketplaceJobTypes.PazaramaOrderSync, MarketplaceJobTypes.ForPlatform("PAZARAMA", MarketplaceJobTypes.OrderSync));
        Assert.Equal(MarketplaceJobTypes.PriceInventorySync, MarketplaceJobTypes.ForPlatform("PAZARAMA", MarketplaceJobTypes.PriceInventorySync));
        Assert.True(MarketplaceInvoiceCreationPolicy.IsEnabled("PAZARAMA", """{"ExternalWritesEnabled":false,"InvoiceCreationEnabled":true}"""));
        Assert.False(MarketplaceInvoiceCreationPolicy.IsEnabled("PAZARAMA", """{"ExternalWritesEnabled":false,"InvoiceCreationEnabled":false}"""));
        Assert.Equal("isortagim.pazarama.com", CapabilityEvidencePolicy.OfficialDocumentationHost("PAZARAMA"));
        Assert.Equal("Pazarama", JobOperationsService.Marketplace("PAZARAMA_CONNECTION_TEST"));
        Assert.Equal("Pazarama", JobOperationsService.Marketplace("PRICE_INVENTORY_SYNC", "PAZARAMA"));
    }

    private static PazaramaHttpClient Client(bool globalWrites) => new(
        new NoHttpFactory(), null!, new PazaramaTokenCache(TimeProvider.System), Options.Create(new PazaramaOptions()), TimeProvider.System, NullLogger<PazaramaHttpClient>.Instance,
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["FeatureFlags:ExternalWrites"] = globalWrites ? "true" : "false" }).Build());

    private static PazaramaRequestContext Account(string environment, string status, bool connectionWrites) => new(
        new PlatformConnection
        {
            PlatformCode = "PAZARAMA",
            Environment = environment,
            Status = status,
            DisplayName = "Pazarama",
            ExternalStoreId = "Ravencia",
            ApiVersion = "V1",
            SettingsJson = connectionWrites ? """{"ExternalWritesEnabled":true}""" : """{"ExternalWritesEnabled":false}"""
        },
        Guid.NewGuid(), "my-client-id", "my-client-secret");

    private sealed class NoHttpFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new InvalidOperationException("No request is expected.");
    }

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset current = now;
        public void Advance(TimeSpan by) => current += by;
        public override DateTimeOffset GetUtcNow() => current;
    }
}
