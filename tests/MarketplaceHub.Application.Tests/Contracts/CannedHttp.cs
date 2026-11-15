using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace MarketplaceHub.Application.Tests.Contracts;

// One canned marketplace answer (status, headers, body) for driving an adapter's whole request path without a network:
// the error classification is then pinned through the adapter's own send code, whichever helper it delegates to.
public sealed class CannedHttp(HttpStatusCode status, string body, string mediaType = "application/json", Action<HttpResponseMessage>? configure = null)
    : HttpMessageHandler, IHttpClientFactory
{
    public int Requests { get; private set; }
    public List<string> Paths { get; } = [];

    public HttpClient CreateClient(string name) => new(this, disposeHandler: false) { BaseAddress = new Uri("https://marketplace.example.test/") };

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests++;
        Paths.Add(request.RequestUri?.PathAndQuery ?? "");
        var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, mediaType), RequestMessage = request };
        configure?.Invoke(response);
        return Task.FromResult(response);
    }

    // The documented error answers every marketplace classifier distinguishes.
    public static CannedHttp RateLimited(int retryAfterSeconds, string body = """{"errors":[{"message":"too.many.requests"}]}""") =>
        new(HttpStatusCode.TooManyRequests, body, configure: response => response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(retryAfterSeconds)));

    public static CannedHttp Unauthorized(string body = """{"message":"Unauthorized"}""") => new(HttpStatusCode.Unauthorized, body);

    public static CannedHttp GatewayBlocked() => new(HttpStatusCode.Forbidden, ContractFixture.Read("Common/gateway-403.html"), "text/html");

    public static CannedHttp Fixture(HttpStatusCode status, string fixturePath) => new(status, ContractFixture.Read(fixturePath));
}
