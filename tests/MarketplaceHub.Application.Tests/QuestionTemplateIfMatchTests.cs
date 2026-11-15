using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MarketplaceHub.Domain;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// A hazır cevap at version 1-9 has an If-Match of four characters ("v1" in quotes); the template endpoints refused
// anything shorter than five, so a new template could be neither edited nor deleted from the panel (found 15 Nov 2026 by
// ApiResponseSchemaTests).
[Collection("postgres-integration")]
public sealed class QuestionTemplateIfMatchTests(ApiEndToEndFixture fixture) : IClassFixture<ApiEndToEndFixture>
{
    [PostgreSqlFact]
    public async Task ANewTemplateIsEditedAndDeletedWithItsSingleDigitVersion()
    {
        var (client, csrf, signIn) = await fixture.SignInAsync(MembershipRole.Operations, ownNetwork: true);
        using (client)
        using (signIn)
        {
            using var created = await client.SendAsync(Request(HttpMethod.Post, "/api/v1/question-templates", csrf, new { title = "Kargo", text = "Yarın kargoda." }));
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            var template = await created.Content.ReadFromJsonAsync<JsonElement>();
            var id = template.GetProperty("id").GetGuid();
            Assert.Equal(1, template.GetProperty("version").GetInt64());

            using var updated = await client.SendAsync(Request(HttpMethod.Put, $"/api/v1/question-templates/{id}", csrf, new { title = "Kargo", text = "Bugün kargoda." }, "\"v1\""));
            Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
            var version = (await updated.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("version").GetInt64();

            using var stale = await client.SendAsync(Request(HttpMethod.Delete, $"/api/v1/question-templates/{id}", csrf, null, "\"v1\""));
            Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
            using var deleted = await client.SendAsync(Request(HttpMethod.Delete, $"/api/v1/question-templates/{id}", csrf, null, $"\"v{version}\""));
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

            using var malformed = await client.SendAsync(Request(HttpMethod.Delete, $"/api/v1/question-templates/{id}", csrf, null, "\"v\""));
            Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        }
    }

    private static HttpRequestMessage Request(HttpMethod method, string path, string csrf, object? body, string? ifMatch = null)
    {
        var request = ApiTestHost.Json(method, path, csrf, body);
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        if (ifMatch is not null) request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        return request;
    }
}
