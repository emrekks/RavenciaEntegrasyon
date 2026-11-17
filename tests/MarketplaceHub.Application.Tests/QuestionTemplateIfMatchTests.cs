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

    // API-GUVENLIK-8 (owner decision 16 Nov 2026): creating a template and drawing a size chart take an Idempotency-Key; the
    // same request with the same key replays the first answer and creates (or draws, a paid call) nothing again.
    [PostgreSqlFact]
    public async Task ATemplateAndASizeChartAreKeyProtected_ARepeatReplaysTheFirstAnswer()
    {
        var (client, csrf, signIn) = await fixture.SignInAsync(MembershipRole.Operations, ownNetwork: true);
        using (client)
        using (signIn)
        {
            var title = $"Tekrar {fixture.Run}";
            using (var withoutKey = await client.SendAsync(ApiTestHost.Json(HttpMethod.Post, "/api/v1/question-templates", csrf, new { title, text = "Bir kez." })))
            {
                Assert.Equal(HttpStatusCode.BadRequest, withoutKey.StatusCode);
                Assert.Equal("IDEMPOTENCY_KEY_REQUIRED", (await ApiTestHost.ProblemAsync(withoutKey)).GetProperty("code").GetString());
            }
            var key = $"template-{Guid.NewGuid():N}";
            Guid firstId;
            using (var first = await client.SendAsync(Keyed(HttpMethod.Post, "/api/v1/question-templates", csrf, new { title, text = "Bir kez." }, key)))
            {
                Assert.Equal(HttpStatusCode.Created, first.StatusCode);
                firstId = (await first.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
            }
            using (var repeat = await client.SendAsync(Keyed(HttpMethod.Post, "/api/v1/question-templates", csrf, new { title, text = "Bir kez." }, key)))
            {
                Assert.Equal(HttpStatusCode.Created, repeat.StatusCode);
                Assert.Equal("true", repeat.Headers.GetValues("Idempotency-Replayed").Single());
                Assert.Equal(firstId, (await repeat.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid());
            }
            using (var list = await client.GetAsync("/api/v1/question-templates"))
                Assert.Single((await list.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray(), item => item.GetProperty("title").GetString() == title);

            var chart = $"/api/v1/products/{Guid.NewGuid()}/size-chart/generate";
            var measurements = new { columns = new[] { "Göğüs" }, rows = new[] { new { size = "M", values = new decimal?[] { 50m } } } };
            using (var withoutKey = await client.SendAsync(ApiTestHost.Json(HttpMethod.Post, chart, csrf, measurements)))
            {
                Assert.Equal(HttpStatusCode.BadRequest, withoutKey.StatusCode);
                Assert.Equal("IDEMPOTENCY_KEY_REQUIRED", (await ApiTestHost.ProblemAsync(withoutKey)).GetProperty("code").GetString());
            }
            var chartKey = $"size-chart-{Guid.NewGuid():N}";
            string firstAnswer;
            using (var first = await client.SendAsync(Keyed(HttpMethod.Post, chart, csrf, measurements, chartKey)))
            {
                Assert.True((int)first.StatusCode < 500, $"{first.StatusCode}");
                firstAnswer = await first.Content.ReadAsStringAsync();
            }
            using (var repeat = await client.SendAsync(Keyed(HttpMethod.Post, chart, csrf, measurements, chartKey)))
            {
                Assert.Equal("true", repeat.Headers.GetValues("Idempotency-Replayed").Single());
                Assert.Equal(firstAnswer, await repeat.Content.ReadAsStringAsync());
            }
        }
    }

    private static HttpRequestMessage Keyed(HttpMethod method, string path, string csrf, object body, string key)
    {
        var request = ApiTestHost.Json(method, path, csrf, body);
        request.Headers.Add("Idempotency-Key", key);
        return request;
    }

    private static HttpRequestMessage Request(HttpMethod method, string path, string csrf, object? body, string? ifMatch = null)
    {
        var request = ApiTestHost.Json(method, path, csrf, body);
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        if (ifMatch is not null) request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        return request;
    }
}
