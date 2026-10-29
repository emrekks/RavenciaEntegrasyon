using System.Net;
using System.Text;
using System.Text.Json;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Adapters;
using MarketplaceHub.Infrastructure.Adapters.TrendyolEFaturam;
using MarketplaceHub.Infrastructure.Adapters.TrendyolEFaturam.Contracts;
using MarketplaceHub.Infrastructure.Adapters.TrendyolEFaturam.ErrorMapping;
using MarketplaceHub.Infrastructure.Adapters.TrendyolEFaturam.Mapping;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class TrendyolEFaturamSessionTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-10-20T09:00:00Z");

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.Conflict)]
    public async Task Sign_in_server_failure_gets_a_retryable_pre_request_code(HttpStatusCode status)
    {
        var handler = new SessionHandler(_ => new HttpResponseMessage(status));
        var (client, _) = Client(handler);

        var access = await client.AcquireAccess(Context(), CancellationToken.None);

        Assert.False(access.IsSuccess);
        Assert.Equal(InvoiceSubmissionOutcomePolicy.SignInUnavailableCode, access.Error!.Code);
        Assert.Equal(AdapterErrorClass.TransientNetwork, access.Error.Class);
        // The processor must not park this submission as "outcome unknown": no fiscal request was sent.
        Assert.False(InvoiceSubmissionOutcomePolicy.IsOutcomeUnknown(access.Error));
        Assert.Equal(JobCompletionKind.Retry, JobExecutionResult.FromAdapterError(access.Error).Kind);
    }

    [Fact]
    public async Task Sign_in_network_failure_gets_the_same_pre_request_code()
    {
        var handler = new SessionHandler(_ => throw new HttpRequestException("connection refused"));
        var (client, _) = Client(handler);

        var access = await client.AcquireAccess(Context(), CancellationToken.None);

        Assert.Equal(InvoiceSubmissionOutcomePolicy.SignInUnavailableCode, access.Error!.Code);
        Assert.False(InvoiceSubmissionOutcomePolicy.IsOutcomeUnknown(access.Error));
    }

    [Fact]
    public async Task Refused_credentials_keep_their_authentication_code()
    {
        var handler = new SessionHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var (client, _) = Client(handler);

        var access = await client.AcquireAccess(Context(), CancellationToken.None);

        Assert.Equal("EFATURAM_AUTHENTICATION_FAILED", access.Error!.Code);
    }

    [Fact]
    public void Fiscal_request_transport_failure_is_still_an_unknown_outcome()
    {
        Assert.True(InvoiceSubmissionOutcomePolicy.IsOutcomeUnknown(new(AdapterErrorClass.Remote5xx, "EFATURAM_SERVER_ERROR", "x", 503, null, null)));
        Assert.True(InvoiceSubmissionOutcomePolicy.IsOutcomeUnknown(new(AdapterErrorClass.TransientNetwork, "EFATURAM_TIMEOUT", "x", null, null, null)));
        Assert.False(InvoiceSubmissionOutcomePolicy.IsOutcomeUnknown(new(AdapterErrorClass.Authentication, "EFATURAM_OPERATION_FORBIDDEN", "x", 403, null, null)));
    }

    // G-5 (29 Oct 2026): a 2xx answer to the creation request that cannot be read is an unknown outcome, never a failure,
    // and the UUID it carries is kept.
    [Fact]
    public void Unreadable_creation_answer_is_an_unknown_outcome_that_keeps_the_uuid()
    {
        const string uuid = "4f1e2d3c-1111-4222-8333-944455566677";
        Assert.True(InvoiceSubmissionOutcomePolicy.IsOutcomeUnknown(TrendyolEFaturamErrorMapper.UnreadableSubmissionAnswer("req-1")));

        var missingNumber = TrendyolEFaturamJsonMapper.PartialOutgoingInvoice($$"""{"invoiceUuid":"{{uuid}}","invoiceId":null,"status":"10"}""", "req-1");
        Assert.Equal(uuid, missingNumber!.ExternalReference);
        Assert.Equal(uuid, missingNumber.EttnUuid);
        Assert.Null(missingNumber.InvoiceNumber);

        var notJson = TrendyolEFaturamJsonMapper.PartialOutgoingInvoice($$"""{"invoiceUuid":"{{uuid}}","invoiceId":"GIB2026""", null);
        Assert.Equal(uuid, notJson!.ExternalReference);

        Assert.Null(TrendyolEFaturamJsonMapper.PartialOutgoingInvoice("<html>ok</html>", null));
        Assert.Null(TrendyolEFaturamJsonMapper.PartialOutgoingInvoice("""{"invoiceUuid":"not-a-uuid"}""", null));
    }

    [Fact]
    public async Task Access_token_is_reused_per_credential_until_shortly_before_it_expires()
    {
        var handler = new SessionHandler(_ => SignInResponse(Start.AddMinutes(30)));
        var (client, clock) = Client(handler);
        var credential = Guid.NewGuid();

        Assert.True((await client.AcquireAccess(Context(credential), CancellationToken.None)).IsSuccess);
        Assert.True((await client.AcquireAccess(Context(credential), CancellationToken.None)).IsSuccess);
        Assert.Equal(1, handler.SignInCount);

        // Another credential (e.g. a rotated password) never shares the token.
        await client.AcquireAccess(Context(Guid.NewGuid()), CancellationToken.None);
        Assert.Equal(2, handler.SignInCount);

        // Within the renewal margin before exp the token is replaced.
        clock.Now = Start.AddMinutes(30) - TrendyolEFaturamTokenCache.RenewBefore;
        await client.AcquireAccess(Context(credential), CancellationToken.None);
        Assert.Equal(3, handler.SignInCount);
    }

    [Fact]
    public async Task Connection_test_always_signs_in_again()
    {
        var handler = new SessionHandler(_ => SignInResponse(Start.AddMinutes(30)));
        var (client, _) = Client(handler);
        var credential = Guid.NewGuid();

        await client.AcquireAccess(Context(credential), CancellationToken.None);
        await client.AcquireAccess(Context(credential), CancellationToken.None, reuseCachedToken: false);

        Assert.Equal(2, handler.SignInCount);
    }

    [Fact]
    public async Task Rejected_access_token_is_forgotten()
    {
        var handler = new SessionHandler(request => request.RequestUri!.AbsolutePath.EndsWith("/signin", StringComparison.Ordinal)
            ? SignInResponse(Start.AddMinutes(30))
            : new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var (client, _) = Client(handler);
        var context = Context(Guid.NewGuid());

        var access = await client.AcquireAccess(context, CancellationToken.None);
        var rejected = await client.SendAuthorized(context, access.Value!.AccessToken, HttpMethod.Get, "api/invoice/documents/earchive/status/x", null, CancellationToken.None);
        await client.AcquireAccess(context, CancellationToken.None);

        Assert.Equal("EFATURAM_ACCESS_TOKEN_REJECTED", rejected.Error!.Code);
        Assert.Equal(2, handler.SignInCount);
    }

    [Fact]
    public async Task An_answer_over_the_response_limit_is_not_read_into_memory()
    {
        var handler = new SessionHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/signin", StringComparison.Ordinal)) return SignInResponse(Start.AddMinutes(30));
            var content = new StringContent("{}");
            if (request.RequestUri.AbsolutePath.EndsWith("/large", StringComparison.Ordinal))
                content.Headers.ContentLength = MarketplaceResponseLimits.MaxResponseBytes + 1;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });
        var (client, _) = Client(handler);
        var context = Context(Guid.NewGuid());
        var access = await client.AcquireAccess(context, CancellationToken.None);

        var small = await client.SendAuthorized(context, access.Value!.AccessToken, HttpMethod.Get, "api/invoice/documents/earchive/status/small", null, CancellationToken.None);
        var large = await client.SendAuthorized(context, access.Value.AccessToken, HttpMethod.Get, "api/invoice/documents/earchive/status/large", null, CancellationToken.None);

        Assert.Equal("{}", small.Value!.Body);
        Assert.Equal("EFATURAM_NETWORK_ERROR", large.Error!.Code);
    }

    [Fact]
    public async Task Token_without_exp_is_not_cached()
    {
        var handler = new SessionHandler(_ => SignInResponse(null));
        var (client, _) = Client(handler);
        var context = Context(Guid.NewGuid());

        await client.AcquireAccess(context, CancellationToken.None);
        await client.AcquireAccess(context, CancellationToken.None);

        Assert.Equal(2, handler.SignInCount);
    }

    private static (TrendyolEFaturamHttpClient Client, MutableClock Clock) Client(HttpMessageHandler handler)
    {
        var clock = new MutableClock { Now = Start };
        var client = new TrendyolEFaturamHttpClient(
            new FixedHttpClientFactory(handler),
            null!,
            new ConfigurationBuilder().Build(),
            Options.Create(new TrendyolEFaturamOptions()),
            null!,
            clock,
            NullLogger<TrendyolEFaturamHttpClient>.Instance,
            new TrendyolEFaturamTokenCache(clock));
        return (client, clock);
    }

    private static TrendyolEFaturamRequestContext Context(Guid? credentialId = null) => new(
        new PlatformConnection { PlatformCode = "TRENDYOL_EFATURAM", Environment = "STAGE", DisplayName = "E-Faturam", ExternalStoreId = "store", Status = "ACTIVE", ApiVersion = "1.0.0" },
        new Uri("https://stage-apigateway.example.test/"),
        new TrendyolEFaturamCredentialPayload("fatura@example.test", "test-only"),
        new TrendyolEFaturamConnectionSettings(false),
        credentialId ?? Guid.NewGuid());

    private static HttpResponseMessage SignInResponse(DateTimeOffset? expiresAt)
    {
        var claims = new Dictionary<string, object> { ["companyId"] = 41, ["userId"] = 7 };
        if (expiresAt is { } exp) claims["exp"] = exp.ToUnixTimeSeconds();
        static string Segment(string json) => Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var token = $"{Segment("{\"alg\":\"none\"}")}.{Segment(JsonSerializer.Serialize(claims))}.signature";
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        response.Headers.TryAddWithoutValidation("x-access-token", token);
        return response;
    }

    private sealed class MutableClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; }
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class FixedHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class SessionHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int SignInCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/signin", StringComparison.Ordinal)) SignInCount++;
            return Task.FromResult(respond(request));
        }
    }
}
