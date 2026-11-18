using System.Net;
using System.Text;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Adapters.Pazarama;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// 17 Nov 2026 from 22:27Z the Pazarama token service answered every request with an empty 200 (and two 504s) right after
// the order service said "İşleminiz şu anda gerçekleştirilemiyor"; each order, return and question read went to manual
// review as PAZARAMA_TOKEN_INVALID. An empty answer is a passing remote failure: retried, held briefly, job requeued.
public sealed class PazaramaTokenRecoveryTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{}")]
    [InlineData("null")]
    public void AnEmptyTokenAnswerIsAPassingRemoteErrorThatRequeuesTheJob(string body)
    {
        var token = PazaramaHttpClient.TokenResponse(HttpStatusCode.OK, body);

        Assert.False(token.IsSuccess);
        Assert.Equal("PAZARAMA_TOKEN_REMOTE_ERROR", token.Error!.Code);
        Assert.Equal(AdapterErrorClass.Remote5xx, token.Error.Class);
        var result = JobExecutionResult.FromAdapterError(token.Error);
        Assert.Equal(JobCompletionKind.Retry, result.Kind);
        Assert.True(ApiHealthSignalPolicy.IsRemoteSide(token.Error.Code));
    }

    [Fact]
    public void AnAnswerWithFieldsButNoTokenStaysAContractQuestionAndRefusedCredentialsStayARefusal()
    {
        var unknown = PazaramaHttpClient.TokenResponse(HttpStatusCode.OK, """{"token_type":"Bearer"}""");
        var refused = PazaramaHttpClient.TokenResponse(HttpStatusCode.BadRequest, """{"error":"invalid_client"}""");

        Assert.Equal("PAZARAMA_TOKEN_INVALID", unknown.Error!.Code);
        Assert.Equal(JobCompletionKind.ManualReview, JobExecutionResult.FromAdapterError(unknown.Error).Kind);
        Assert.Equal("PAZARAMA_CREDENTIALS_REJECTED", refused.Error!.Code);
        Assert.Equal(JobCompletionKind.Blocked, JobExecutionResult.FromAdapterError(refused.Error).Kind);
    }

    [Fact]
    public async Task AnEmptyAnswerIsAskedAgainWithBackoffUntilATokenComes()
    {
        var answers = new Queue<PazaramaToken>([Failure("PAZARAMA_TOKEN_REMOTE_ERROR", AdapterErrorClass.Remote5xx), Failure("PAZARAMA_REMOTE_ERROR", AdapterErrorClass.Remote5xx), new("ok", TimeSpan.FromHours(1), null)]);
        var attempts = new List<int>();
        var waits = new List<TimeSpan>();

        var token = await PazaramaHttpClient.IssueWithRetriesAsync(
            attempt => { attempts.Add(attempt); return Task.FromResult(answers.Dequeue()); },
            wait => { waits.Add(wait); return Task.CompletedTask; },
            PazaramaHttpClient.DefaultTokenRetryDelays);

        Assert.True(token.IsSuccess);
        Assert.Equal([1, 2, 3], attempts);
        Assert.Equal([TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5)], waits);
    }

    [Fact]
    public async Task RetriesAreBoundedAndTheLastFailureIsReturned()
    {
        var calls = 0;

        var token = await PazaramaHttpClient.IssueWithRetriesAsync(
            _ => { calls++; return Task.FromResult(Failure("PAZARAMA_TOKEN_REMOTE_ERROR", AdapterErrorClass.Remote5xx)); },
            _ => Task.CompletedTask,
            PazaramaHttpClient.DefaultTokenRetryDelays);

        Assert.Equal(3, calls);
        Assert.Equal("PAZARAMA_TOKEN_REMOTE_ERROR", token.Error!.Code);
    }

    [Theory]
    [InlineData("PAZARAMA_CREDENTIALS_REJECTED", AdapterErrorClass.Authentication)]
    [InlineData("PAZARAMA_TOKEN_INVALID", AdapterErrorClass.ContractViolation)]
    [InlineData("PAZARAMA_TIMEOUT", AdapterErrorClass.TransientNetwork)]
    [InlineData("PAZARAMA_RATE_LIMITED", AdapterErrorClass.RateLimit)]
    [InlineData("PAZARAMA_GATEWAY_BLOCKED", AdapterErrorClass.RateLimit)]
    public async Task ARefusalATimeoutOrANamedWaitIsNotAskedAgainAtOnce(string code, AdapterErrorClass @class)
    {
        var calls = 0;

        await PazaramaHttpClient.IssueWithRetriesAsync(
            _ => { calls++; return Task.FromResult(Failure(code, @class)); },
            _ => Task.CompletedTask,
            PazaramaHttpClient.DefaultTokenRetryDelays);

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task APassingFailureIsHeldBrieflySoConcurrentCallersDoNotEachAsk()
    {
        var clock = new ManualClock(new DateTimeOffset(2026, 11, 17, 22, 27, 0, TimeSpan.Zero));
        var cache = new PazaramaTokenCache(clock);
        var credentialId = Guid.NewGuid();
        var issued = 0;
        var release = new TaskCompletionSource();
        async Task<PazaramaToken> Empty(CancellationToken _) { Interlocked.Increment(ref issued); await release.Task; return Failure("PAZARAMA_TOKEN_REMOTE_ERROR", AdapterErrorClass.Remote5xx); }

        var callers = Enumerable.Range(0, 8).Select(_ => cache.GetAsync(credentialId, Empty, CancellationToken.None)).ToArray();
        release.SetResult();
        var answers = await Task.WhenAll(callers);
        clock.Advance(PazaramaTokenCache.FailureHold - TimeSpan.FromSeconds(1));
        await cache.GetAsync(credentialId, Empty, CancellationToken.None);

        Assert.Equal(1, issued);
        Assert.All(answers, answer => Assert.Equal("PAZARAMA_TOKEN_REMOTE_ERROR", answer.Error!.Code));

        clock.Advance(TimeSpan.FromSeconds(2));
        await cache.GetAsync(credentialId, Empty, CancellationToken.None);
        Assert.Equal(2, issued);
    }

    [Fact]
    public async Task ARefusedCredentialIsNotHeldAndA401ForgetsAHeldFailure()
    {
        var cache = new PazaramaTokenCache(TimeProvider.System);
        var credentialId = Guid.NewGuid();
        var issued = 0;
        Task<PazaramaToken> Refused(CancellationToken _) { issued++; return Task.FromResult(Failure("PAZARAMA_CREDENTIALS_REJECTED", AdapterErrorClass.Authentication)); }
        Task<PazaramaToken> Empty(CancellationToken _) { issued++; return Task.FromResult(Failure("PAZARAMA_TOKEN_REMOTE_ERROR", AdapterErrorClass.Remote5xx)); }

        await cache.GetAsync(credentialId, Refused, CancellationToken.None);
        await cache.GetAsync(credentialId, Refused, CancellationToken.None);
        Assert.Equal(2, issued);

        await cache.GetAsync(credentialId, Empty, CancellationToken.None);
        cache.Forget(credentialId);
        await cache.GetAsync(credentialId, Empty, CancellationToken.None);
        Assert.Equal(4, issued);
    }

    [Fact]
    public async Task AnEmptyTokenAnswerIsRetriedHeldAndLoggedWithItsOutlineButNoValue()
    {
        var handler = new TokenHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("", Encoding.UTF8, "application/json") });
        var logger = new CapturingLogger();
        var client = Client(handler, logger, new PazaramaTokenCache(TimeProvider.System));

        var first = await client.TestAsync(Account(), CancellationToken.None);
        var second = await client.TestAsync(Account(), CancellationToken.None);

        Assert.Equal("PAZARAMA_TOKEN_REMOTE_ERROR", first.Error!.Code);
        Assert.Equal("PAZARAMA_TOKEN_REMOTE_ERROR", second.Error!.Code);
        Assert.Equal(3, handler.TokenRequests);
        Assert.Equal(3, logger.Lines.Count);
        Assert.All(logger.Lines, line => Assert.Contains("Body: boş (application/json, 0 bayt)", line));
        Assert.Contains("Attempt: 3", logger.Lines[2]);
    }

    [Fact]
    public async Task ARefusedTokenLogsTheOAuthErrorAndAnUnknownAnswerItsFieldNamesNeverTheirValues()
    {
        var bodies = new Queue<(HttpStatusCode, string)>([(HttpStatusCode.BadRequest, """{"error":"invalid_client"}"""), (HttpStatusCode.OK, """{"result":{"jwt":"super-secret-value"}}""")]);
        var handler = new TokenHandler(_ => { var (status, body) = bodies.Dequeue(); return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") }; });
        var logger = new CapturingLogger();
        var client = Client(handler, logger, new PazaramaTokenCache(TimeProvider.System));

        await client.TestAsync(Account(), CancellationToken.None);
        await client.TestAsync(Account(), CancellationToken.None);

        Assert.Equal(2, handler.TokenRequests);
        Assert.Contains("OAuthError: invalid_client", logger.Lines[0]);
        Assert.Contains("Status: 400", logger.Lines[0]);
        Assert.Contains("result.jwt", logger.Lines[1]);
        Assert.DoesNotContain("super-secret-value", string.Join("\n", logger.Lines));
        Assert.DoesNotContain("my-client-secret", string.Join("\n", logger.Lines));
    }

    [Theory]
    [InlineData("İşleminiz şu anda gerçekleştirilemiyor. Lütfen daha sonra tekrar deneyiniz.")]
    [InlineData("Lütfen daha sonra tekrar deneyiniz")]
    public void ABusyTokenEnvelopeIsAPassingRemoteErrorNotARefusedKey(string message)
    {
        var token = PazaramaHttpClient.TokenResponse(HttpStatusCode.OK, $$"""{"data":null,"success":false,"userMessage":"{{message}}"}""");

        Assert.Equal("PAZARAMA_TOKEN_REMOTE_ERROR", token.Error!.Code);
        Assert.Equal(AdapterErrorClass.Remote5xx, token.Error.Class);
        Assert.Contains(message, token.Error.SafeMessage);
        Assert.Equal(JobCompletionKind.Retry, JobExecutionResult.FromAdapterError(token.Error).Kind);
    }

    [Theory]
    [InlineData("""{"data":null,"success":false,"userMessage":"Oturum bilgileriniz alınamadı"}""", "Oturum bilgileriniz alınamadı")]
    [InlineData("""{"data":null,"success":false}""", "mesaj yok")]
    public void AnUnclearSuccessFalseIsUnknownNotARefusedKey(string body, string shown)
    {
        var token = PazaramaHttpClient.TokenResponse(HttpStatusCode.OK, body);

        Assert.Equal("PAZARAMA_TOKEN_INVALID", token.Error!.Code);
        Assert.Equal(AdapterErrorClass.ContractViolation, token.Error.Class);
        Assert.Contains(shown, token.Error.SafeMessage);
        Assert.Contains("anahtar reddi sayılmadı", token.Error.SafeMessage);
    }

    [Fact]
    public void A5xxWithAnUnclearEnvelopeIsTheTokenServicesServerError()
    {
        var token = PazaramaHttpClient.TokenResponse(HttpStatusCode.ServiceUnavailable, """{"success":false,"userMessage":"Sistem hatası"}""");

        Assert.Equal("PAZARAMA_TOKEN_REMOTE_ERROR", token.Error!.Code);
        Assert.Equal(AdapterErrorClass.Remote5xx, token.Error.Class);
    }

    // The review's cases: a refusal word, or a lone "client" / "istemci" / "anahtar", is no refused key; the key named
    // as a phrase and refused, or "yetkisiz" / "unauthorized", is. "Access denied" stays unknown (the safe side).
    [Theory]
    [InlineData("Sistem hatalı çalışıyor", false)]
    [InlineData("Invalid request format", false)]
    [InlineData("Geçersiz işlem", false)]
    [InlineData("İstemci isteği hatalı biçimde", false)]
    [InlineData("Client error, invalid request body", false)]
    [InlineData("Anahtar kelime bulunamadı", false)]
    [InlineData("Geçersiz anahtar", false)]
    [InlineData("Access denied", false)]
    [InlineData("Erişim reddedildi", false)]
    [InlineData("GEÇERSİZ İSTEMCİ KİMLİĞİ", true)]
    [InlineData("YETKİSİZ", true)]
    [InlineData("Unauthorized", true)]
    [InlineData("Invalid client credentials", true)]
    [InlineData("Bad credentials", true)]
    [InlineData("Kullanıcı adı veya şifre hatalı", true)]
    [InlineData("Geçersiz API anahtarı", true)]
    [InlineData("INVALID CLIENT ID", true)]
    public void OnlyARefusalThatNamesTheKeyIsARefusedKey(string message, bool refused)
    {
        var token = PazaramaHttpClient.TokenResponse(HttpStatusCode.OK, $$"""{"success":false,"userMessage":"{{message}}"}""");

        Assert.Equal(refused, PazaramaHttpClient.SaysCredentialsRefused(message));
        Assert.Equal(refused ? "PAZARAMA_CREDENTIALS_REJECTED" : "PAZARAMA_TOKEN_INVALID", token.Error!.Code);
    }

    [Theory]
    [InlineData("<!DOCTYPE html><html><body>Bakım</body></html>")]
    [InlineData("<html><body>maintenance</body></html>")]
    [InlineData("[]")]
    public void AnHtmlPageOrAnEmptyListInA200IsAPassingRemoteError(string body)
    {
        var token = PazaramaHttpClient.TokenResponse(HttpStatusCode.OK, body);

        Assert.Equal("PAZARAMA_TOKEN_REMOTE_ERROR", token.Error!.Code);
        Assert.Equal(JobCompletionKind.Retry, JobExecutionResult.FromAdapterError(token.Error).Kind);
    }

    [Fact]
    public void A400WithNoBodyIsUnknownNeitherRetriedNorARefusedKey()
    {
        var token = PazaramaHttpClient.TokenResponse(HttpStatusCode.BadRequest, "");

        Assert.Equal("PAZARAMA_TOKEN_INVALID", token.Error!.Code);
        Assert.Equal(AdapterErrorClass.ContractViolation, token.Error.Class);
    }

    [Fact]
    public void TheOAuthErrorIsKeptUpTo64CharactersOnly()
    {
        var at64 = new string('e', 64);
        var at65 = new string('e', 65);

        Assert.Equal(at64, PazaramaHttpClient.OAuthError($$"""{"error":"{{at64}}"}"""));
        Assert.Null(PazaramaHttpClient.OAuthError($$"""{"error":"{{at65}}"}"""));
        Assert.Null(PazaramaHttpClient.OAuthError("""{"error":""}"""));
        Assert.Null(PazaramaHttpClient.OAuthError("not json"));
    }

    [Fact]
    public async Task TheLoggedSizeIsInBytes()
    {
        // 7 characters, 8 bytes in UTF-8.
        var handler = new TokenHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"ş":1}""", Encoding.UTF8, "application/json") });
        var logger = new CapturingLogger();

        await Client(handler, logger, new PazaramaTokenCache(TimeProvider.System)).TestAsync(Account(), CancellationToken.None);

        Assert.Contains("(application/json, 8 bayt)", Assert.Single(logger.Lines));
    }

    [Fact]
    public async Task AHeldFailureIsNotKeptLongerWhenTheClockGoesBack()
    {
        var clock = new ManualClock(new DateTimeOffset(2026, 11, 17, 22, 27, 0, TimeSpan.Zero));
        var cache = new PazaramaTokenCache(clock);
        var credentialId = Guid.NewGuid();
        var issued = 0;
        Task<PazaramaToken> Empty(CancellationToken _) { issued++; return Task.FromResult(Failure("PAZARAMA_TOKEN_REMOTE_ERROR", AdapterErrorClass.Remote5xx)); }

        await cache.GetAsync(credentialId, Empty, CancellationToken.None);
        clock.Advance(TimeSpan.FromHours(-1));
        await cache.GetAsync(credentialId, Empty, CancellationToken.None);

        Assert.Equal(2, issued);
    }

    private static PazaramaToken Failure(string code, AdapterErrorClass @class) => new(null, TimeSpan.Zero, new(@class, code, code, 200, null, null));

    private static PazaramaHttpClient Client(TokenHandler handler, CapturingLogger logger, PazaramaTokenCache cache) => new(
        new Factory(handler), null!, cache, Options.Create(new PazaramaOptions()), TimeProvider.System, logger, new ConfigurationBuilder().Build())
    { TokenRetryDelays = [TimeSpan.Zero, TimeSpan.Zero] };

    private static readonly Guid CredentialId = Guid.NewGuid();

    private static PazaramaRequestContext Account() => new(
        new PlatformConnection { PlatformCode = "PAZARAMA", Environment = "PRODUCTION", Status = "ACTIVE", DisplayName = "Pazarama", ExternalStoreId = "Ravencia", ApiVersion = "V1", SettingsJson = "{}" },
        CredentialId, "my-client-id", "my-client-secret");

    private sealed class TokenHandler(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public int TokenRequests;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath != "/connect/token") throw new InvalidOperationException("Only the token service is expected.");
            Interlocked.Increment(ref TokenRequests);
            return Task.FromResult(answer(request));
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class CapturingLogger : ILogger<PazaramaHttpClient>
    {
        public List<string> Lines { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var line = formatter(state, exception);
            if (line.StartsWith("Pazarama token", StringComparison.Ordinal)) Lines.Add(line);
        }
    }

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset current = now;
        public void Advance(TimeSpan by) => current += by;
        public override DateTimeOffset GetUtcNow() => current;
    }
}
