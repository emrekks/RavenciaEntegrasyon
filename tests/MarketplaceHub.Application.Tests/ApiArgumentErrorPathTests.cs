using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MarketplaceHub.Api.Security;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// An ArgumentException a handler throws (a page size or cursor out of range) is answered 400 in the shared shape:
// by the global ApiExceptionHandler, and in the Catalog, Invoicing and Marketplace groups already inside the endpoint
// (EndpointHelpers.WithArgumentErrorsAnswered, kept so IdempotencyMiddleware records a keyed write's 400 as a
// completed refusal). These requests pin the body and headers the panel gets from those groups.
public sealed class ApiArgumentErrorPathTests(ApiRouteProbeHost fixture) : IClassFixture<ApiRouteProbeHost>
{
    [Theory]
    [InlineData("/api/v1/catalog/categories?limit=0", "INVALID_REQUEST", "limit 1-200 arasında olmalıdır.")]
    [InlineData("/api/v1/products?limit=999", "INVALID_REQUEST", "limit 1-200 arasında olmalıdır.")]
    [InlineData("/api/v1/invoices?limit=0", "INVALID_REQUEST", "limit 1-200 arasında olmalıdır.")]
    [InlineData("/api/v1/orders?limit=0", "INVALID_REQUEST", "limit 1-200 arasında olmalıdır.")]
    [InlineData("/api/v1/shipments?limit=0", "INVALID_REQUEST", "limit 1-200 arasında olmalıdır.")]
    public async Task ArgumentException_IsTheSameBadRequestInEveryGroup(string path, string code, string title)
    {
        using var client = fixture.Host.ClientFromOwnNetwork();
        client.DefaultRequestHeaders.Add(ApiRouteTable.ProbeRoleHeader, "OWNER");

        using var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ApiProblems.ContentType, response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(code, problem.GetProperty("code").GetString());
        Assert.Equal(title, problem.GetProperty("title").GetString());
        Assert.Equal(400, problem.GetProperty("status").GetInt32());
        Assert.False(problem.GetProperty("retryable").GetBoolean());
        Assert.Equal($"https://marketplacehub.invalid/problems/{code.ToLowerInvariant().Replace('_', '-')}", problem.GetProperty("type").GetString());
        Assert.False(string.IsNullOrEmpty(problem.GetProperty("correlationId").GetString()));
        // The headers the pipeline set before the handler ran stay on the answer.
        Assert.Equal(problem.GetProperty("correlationId").GetString(), response.Headers.GetValues("X-Correlation-ID").Single());
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
    }
}
