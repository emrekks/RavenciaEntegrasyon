using System.Net;
using System.Text;
using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure;
using MarketplaceHub.Infrastructure.Adapters;
using MarketplaceHub.Infrastructure.Adapters.PttAvm;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class PttAvmAdapterTests
{
    private const string ApiKey = "ptt-api-key-1234";
    private const string AccessToken = "ptt-access-token-5678";

    [Fact]
    public void PttAvmIsAMarketplaceWithItsOwnConnectionTest()
    {
        Assert.True(ActiveIntegrationScope.Contains("PTTAVM"));
        Assert.True(ActiveIntegrationScope.IsMarketplace("PTTAVM"));
        Assert.Equal(MarketplaceJobTypes.PttAvmConnectionTest, MarketplaceJobTypes.ForPlatform("PTTAVM", MarketplaceJobTypes.ConnectionTest));
        Assert.Equal("PTTAVM_CONNECTION_TEST", MarketplaceJobTypes.PttAvmConnectionTest);
        Assert.Contains(MarketplaceJobTypes.PttAvmConnectionTest, MarketplaceJobTypes.ProcessorJobTypes);
        Assert.Equal("developers.pttavm.com", CapabilityEvidencePolicy.OfficialDocumentationHost("PTTAVM"));
        Assert.Equal("PTT AVM", JobOperationsService.Marketplace("PTTAVM_CONNECTION_TEST"));
        Assert.Equal("PTT AVM", JobOperationsService.Marketplace("PRICE_INVENTORY_SYNC", "PTTAVM"));
        Assert.Contains("ORDERS", ConnectionDataResetPolicy.ScopesFor("PTTAVM"));
        // Invoice creation starts off on a new connection.
        Assert.False(MarketplaceInvoiceCreationPolicy.IsEnabled("PTTAVM", """{"ExternalWritesEnabled":false,"InvoiceCreationEnabled":false}"""));
    }

    [Fact]
    public void PttAvmConnectionsCarryOnlyThePhaseOneReadCapabilities() =>
        Assert.Equal(
            [MarketplaceCapabilities.ConnectionTest, MarketplaceCapabilities.OrderRead, MarketplaceCapabilities.ProductRead, MarketplaceCapabilities.ReferenceRead],
            MarketplaceConnectionService.PttAvmCapabilityCodes);

    [Fact]
    public void PttAvmConnectionsRouteToThePttAvmAdapter()
    {
        object trendyol = new(), shopify = new(), pttAvm = new();

        Assert.Same(pttAvm, MarketplacePortRouter.SelectPort("pttavm", trendyol, shopify, pttAvm: pttAvm));
        Assert.Throws<InvalidOperationException>(() => MarketplacePortRouter.SelectPort("PTTAVM", trendyol, shopify));
    }

    [Fact]
    public async Task EveryRequestCarriesTheKeyTheTokenAndAFreshCorrelationId()
    {
        var handler = new CapturingHandler(_ => Respond(HttpStatusCode.OK, """{"success":true,"main_category":[{"id":"1","name":"Elektronik","updated_at":"2023-06-13T15:24:53"}],"error":{"error_code":null,"error_message":null}}"""));
        var client = Client(handler);

        var first = await client.TestAsync(Account("PRODUCTION", "DRAFT", false), CancellationToken.None);
        await client.TestAsync(Account("PRODUCTION", "DRAFT", false), CancellationToken.None);

        Assert.True(first.IsSuccess, first.Error?.SafeMessage);
        Assert.Equal("PTTAVM", first.Value!.PlatformCode);
        var request = handler.Requests[0];
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("https://integration-api.pttavm.com/api/v1/categories/main", request.Uri);
        Assert.Equal(ApiKey, request.Headers["Api-Key"]);
        Assert.Equal(AccessToken, request.Headers["Access-Token"]);
        Assert.True(Guid.TryParse(request.Headers["X-Correlation-Id"], out _));
        Assert.NotEqual(handler.Requests[0].Headers["X-Correlation-Id"], handler.Requests[1].Headers["X-Correlation-Id"]);
        Assert.Contains("application/json", request.Headers["Accept"]);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task ARefusedKeyIsReportedAsRejectedCredentialsWithoutLeakingThem(HttpStatusCode status)
    {
        var logger = new CapturingLogger();
        var client = Client(new CapturingHandler(_ => Respond(status, """{"success":false,"error":{"error_code":"401","error_message":"Geçersiz token"}}""")), logger: logger);

        var result = await client.TestAsync(Account("PRODUCTION", "DRAFT", false), CancellationToken.None);

        Assert.Equal("PTTAVM_CREDENTIALS_REJECTED", result.Error!.Code);
        Assert.Equal(AdapterErrorClass.Authentication, result.Error.Class);
        Assert.Contains("Hesap Yönetimi › Entegrasyon Bilgileri", result.Error.SafeMessage);
        Assert.Contains("Ravencia", result.Error.SafeMessage);
        Assert.Contains("Geçersiz token", result.Error.SafeMessage);
        Assert.NotEmpty(logger.Messages);
        Assert.All(logger.Messages, message =>
        {
            Assert.DoesNotContain(ApiKey, message);
            Assert.DoesNotContain(AccessToken, message);
        });
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, "PTTAVM_REMOTE_ERROR", AdapterErrorClass.Remote5xx)]
    [InlineData(HttpStatusCode.TooManyRequests, "PTTAVM_RATE_LIMITED", AdapterErrorClass.RateLimit)]
    [InlineData(HttpStatusCode.RequestTimeout, "PTTAVM_REMOTE_TIMEOUT", AdapterErrorClass.TransientNetwork)]
    public void OtherFailuresAreTransient(HttpStatusCode status, string code, AdapterErrorClass @class)
    {
        var error = PttAvmHttpClient.Error(status, "", null);

        Assert.Equal(code, error.Code);
        Assert.Equal(@class, error.Class);
    }

    [Fact]
    public void AGatewayBlockPageIsNotACredentialsRefusal()
    {
        var error = PttAvmHttpClient.Error(HttpStatusCode.Forbidden, "<!DOCTYPE html><html><body>blocked</body></html>", null);

        Assert.Equal("PTTAVM_GATEWAY_BLOCKED", error.Code);
        Assert.Equal(AdapterErrorClass.RateLimit, error.Class);
    }

    [Fact]
    public void ASuccessfulHttpAnswerWithSuccessFalseIsARejection()
    {
        using var refused = JsonDocument.Parse("""{"success":false,"main_category":null,"error":{"error_code":"E1","error_message":"Kategori bulunamadı"}}""");
        using var accepted = JsonDocument.Parse("""{"success":true,"main_category":[],"error":{"error_code":null,"error_message":null}}""");
        using var bareList = JsonDocument.Parse("[]");

        Assert.Contains("Kategori bulunamadı", PttAvmHttpClient.EnvelopeError(refused.RootElement)!.SafeMessage);
        Assert.Null(PttAvmHttpClient.EnvelopeError(accepted.RootElement));
        Assert.Null(PttAvmHttpClient.EnvelopeError(bareList.RootElement));
        Assert.True(PttAvmHttpClient.MainCategoryList(accepted.RootElement));
        Assert.False(PttAvmHttpClient.MainCategoryList(bareList.RootElement));
    }

    [Fact]
    public async Task AnUnexpectedCategoryAnswerNamesItsFieldsButNotTheirValues()
    {
        var client = Client(new CapturingHandler(_ => Respond(HttpStatusCode.OK, """{"items":"secret-value"}""")));

        var result = await client.TestAsync(Account("PRODUCTION", "DRAFT", false), CancellationToken.None);

        Assert.Equal("PTTAVM_CONNECTION_CONTRACT_INVALID", result.Error!.Code);
        Assert.Contains("items", result.Error.SafeMessage);
        Assert.DoesNotContain("secret-value", result.Error.SafeMessage);
    }

    [Fact]
    public async Task TheProbeProvesReadsOnlyWithTheDocumentedAnswers()
    {
        var handler = new CapturingHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/api/v1/categories/main" => Respond(HttpStatusCode.OK, """{"success":true,"main_category":[]}"""),
            "/api/v1/orders/search" => Respond(HttpStatusCode.OK, "[]"),
            _ => Respond(HttpStatusCode.BadRequest, """{"success":false,"error":{"error_code":"400","error_message":"Barkod bulunamadı"}}""")
        });
        var client = Client(handler, new DateTimeOffset(2026, 10, 21, 22, 30, 0, TimeSpan.Zero));

        var result = await client.DiscoverCapabilitiesAsync(Account("PRODUCTION", "DRAFT", false), CancellationToken.None);

        Assert.True(result.IsSuccess);
        var evidence = result.Value!.ToDictionary(x => x.Code, x => x.SupportLevel);
        Assert.Equal("SUPPORTED", evidence[MarketplaceCapabilities.ConnectionTest]);
        Assert.Equal("SUPPORTED", evidence[MarketplaceCapabilities.ReferenceRead]);
        Assert.Equal("SUPPORTED", evidence[MarketplaceCapabilities.OrderRead]);
        Assert.Equal("UNKNOWN", evidence[MarketplaceCapabilities.ProductRead]);
        Assert.All(result.Value!, item => Assert.StartsWith("https://developers.pttavm.com/", item.SourceUrl));
        // 22:30 UTC is already the 22nd in Turkey.
        var orders = handler.Requests.Single(x => x.Uri.Contains("orders/search", StringComparison.Ordinal));
        Assert.EndsWith("orders/search?startDate=2026-10-21&endDate=2026-10-22&isActiveOrders=false", orders.Uri);
        var products = handler.Requests.Single(x => x.Uri.EndsWith("products/get-by-barcodes", StringComparison.Ordinal));
        Assert.Equal(HttpMethod.Post, products.Method);
        Assert.Equal($$"""{"barcodes":["{{PttAvmHttpClient.ProbeBarcode}}"]}""", products.Body);
    }

    [Fact]
    public async Task AProductListAnswerProvesProductRead()
    {
        var handler = new CapturingHandler(request => request.RequestUri!.AbsolutePath == "/api/v1/categories/main"
            ? Respond(HttpStatusCode.OK, """{"success":true,"main_category":[]}""")
            : Respond(HttpStatusCode.OK, "[]"));

        var result = await Client(handler).DiscoverCapabilitiesAsync(Account("PRODUCTION", "DRAFT", false), CancellationToken.None);

        Assert.All(result.Value!, item => Assert.Equal("SUPPORTED", item.SupportLevel));
    }

    [Theory]
    [InlineData("https://integration-api.pttavm.com/api/v1/orders/search?startDate=2026-10-01", "orders")]
    [InlineData("https://integration-api.pttavm.com/api/v1/orders/123/cargo-infos", "orders")]
    [InlineData("https://integration-api.pttavm.com/api/v1/products/upsert", "product-write")]
    [InlineData("https://integration-api.pttavm.com/api/v1/products/stock-prices", "product-write")]
    [InlineData("https://integration-api.pttavm.com/api/v1/products/search", "reads")]
    [InlineData("https://integration-api.pttavm.com/api/v1/categories/main", "reads")]
    [InlineData("https://shipment.pttavm.com/api/v1/create-barcode", "shipment")]
    public void RequestsAreCountedInTheirPttAvmBucket(string url, string bucket) =>
        Assert.Equal(bucket, PttAvmRateLimitHandler.BucketFor(new Uri(url)));

    [Fact]
    public void ProductWritesAreSpacedFifteenSecondsApart()
    {
        var options = new PttAvmOptions();

        Assert.Equal(1d / 15, PttAvmRateLimitHandler.BucketShape(options, "product-write").TokensPerSecond, 6);
        Assert.Equal(1d, PttAvmRateLimitHandler.BucketShape(options, "product-write").Capacity);
    }

    [Theory]
    [InlineData("PRODUCTION", "VERIFIED")]
    [InlineData("PRODUCTION", "ACTIVE")]
    public void WritesPassOnlyWhenEverySwitchIsOn(string environment, string status) =>
        Assert.Null(Client(new CapturingHandler(_ => throw new InvalidOperationException()), globalWrites: true).WriteBlocked<bool>(Account(environment, status, true), "test"));

    [Theory]
    [InlineData(false, "PRODUCTION", "VERIFIED", true)]
    [InlineData(true, "PRODUCTION", "VERIFIED", false)]
    [InlineData(true, "STAGE", "VERIFIED", true)]
    [InlineData(true, "PRODUCTION", "DRAFT", true)]
    public void AnyClosedSwitchBlocksTheWrite(bool global, string environment, string status, bool connectionWrites)
    {
        var blocked = Client(new CapturingHandler(_ => throw new InvalidOperationException()), globalWrites: global).WriteBlocked<bool>(Account(environment, status, connectionWrites), "fiyat-stok");

        Assert.Equal("PTTAVM_EXTERNAL_WRITES_DISABLED", blocked!.Error!.Code);
        Assert.Contains("fiyat-stok gönderilmedi", blocked.Error.SafeMessage);
    }

    [Fact]
    public void LoggedRequestContextDoesNotPrintTheKeyOrTheToken()
    {
        var context = Account("PRODUCTION", "ACTIVE", false);

        Assert.DoesNotContain(ApiKey, context.ToString());
        Assert.DoesNotContain(AccessToken, context.ToString());
        Assert.DoesNotContain(AccessToken, PttAvmRateLimitHandler.StoreKey(AccessToken));
    }

    private static PttAvmHttpClient Client(CapturingHandler handler, DateTimeOffset? now = null, bool globalWrites = false, ILogger<PttAvmHttpClient>? logger = null) => new(
        new SingleClientFactory(handler), null!, Options.Create(new PttAvmOptions()), new FixedClock(now ?? DateTimeOffset.UtcNow), logger ?? new CapturingLogger(),
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["FeatureFlags:ExternalWrites"] = globalWrites ? "true" : "false" }).Build());

    private static PttAvmRequestContext Account(string environment, string status, bool connectionWrites) => new(
        new PlatformConnection
        {
            Id = Guid.NewGuid(),
            PlatformCode = "PTTAVM",
            Environment = environment,
            Status = status,
            DisplayName = "PTT AVM",
            ExternalStoreId = "Ravencia",
            ApiVersion = "V1",
            SettingsJson = connectionWrites ? """{"ExternalWritesEnabled":true}""" : """{"ExternalWritesEnabled":false}"""
        },
        ApiKey, AccessToken);

    private static HttpResponseMessage Respond(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed record CapturedRequest(HttpMethod Method, string Uri, Dictionary<string, string> Headers, string? Body);

    private sealed class CapturingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<CapturedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var headers = request.Headers.ToDictionary(x => x.Key, x => string.Join(",", x.Value), StringComparer.OrdinalIgnoreCase);
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new(request.Method, request.RequestUri!.ToString(), headers, body));
            return respond(request);
        }
    }

    private sealed class SingleClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            Assert.Equal("PttAvm", name);
            return new HttpClient(handler, disposeHandler: false);
        }
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class CapturingLogger : ILogger<PttAvmHttpClient>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception) + " " + state);
    }
}
