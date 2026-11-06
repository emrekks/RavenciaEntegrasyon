using System.Net;
using System.Text.Json;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Ai;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// Ayarlar › Yapay Zekâ through the API: owner/administrator only, the Claude key is write-only, encrypted and audited.
[Collection("postgres-integration")]
public sealed class AiSettingsEndToEndTests(ApiEndToEndFixture fixture) : IClassFixture<ApiEndToEndFixture>
{
    private const string Key = "sk-ant-api03-e2e-secret-value-123456";

    [PostgreSqlFact]
    public async Task Settings_AreForOwnersAndAdministrators_TheKeyIsNeverReturned()
    {
        var (admin, csrf, adminSignIn) = await fixture.SignInAsync(MembershipRole.Administrator);
        var (operations, operationsCsrf, operationsSignIn) = await fixture.SignInAsync(MembershipRole.Operations);
        using (admin)
        using (adminSignIn)
        using (operations)
        using (operationsSignIn)
        {
            using var forbidden = await operations.GetAsync("/api/v1/settings/ai");
            Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
            using var forbiddenSave = await operations.SendAsync(ApiTestHost.Json(HttpMethod.Put, "/api/v1/settings/ai", operationsCsrf, new { apiKey = Key, defaultModel = "claude-sonnet-5-5" }));
            Assert.Equal(HttpStatusCode.Forbidden, forbiddenSave.StatusCode);

            // Another test of this class may have saved first: each save goes over the version read (If-Match).
            HttpRequestMessage Put(object body, System.Net.Http.Headers.EntityTagHeaderValue? etag)
            {
                var request = ApiTestHost.Json(HttpMethod.Put, "/api/v1/settings/ai", csrf, body);
                if (etag is not null) request.Headers.TryAddWithoutValidation("If-Match", etag.Tag);
                return request;
            }
            using var before = await admin.GetAsync("/api/v1/settings/ai");
            using var invalid = await admin.SendAsync(Put(new { apiKey = "not-a-key", defaultModel = "claude-sonnet-5-5" }, before.Headers.ETag));
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);

            using var saved = await admin.SendAsync(Put(new { apiKey = Key, defaultModel = "claude-sonnet-5-5" }, before.Headers.ETag));
            var savedText = await saved.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
            Assert.DoesNotContain(Key, savedText, StringComparison.Ordinal);
            Assert.True(JsonDocument.Parse(savedText).RootElement.GetProperty("apiKeySaved").GetBoolean());

            // D9: a stored setting is not written blind: without If-Match the save is refused (428).
            using var blind = await admin.SendAsync(ApiTestHost.Json(HttpMethod.Put, "/api/v1/settings/ai", csrf, new { apiKey = "", defaultModel = "claude-opus-5-5" }));
            Assert.Equal((HttpStatusCode)428, blind.StatusCode);

            // A blank key keeps the saved one.
            var keep = ApiTestHost.Json(HttpMethod.Put, "/api/v1/settings/ai", csrf, new { apiKey = "", defaultModel = "claude-opus-5-5" });
            keep.Headers.TryAddWithoutValidation("If-Match", saved.Headers.ETag!.Tag);
            using var kept = await admin.SendAsync(keep);
            Assert.Equal(HttpStatusCode.OK, kept.StatusCode);

            using var read = await admin.GetAsync("/api/v1/settings/ai");
            var readText = await read.Content.ReadAsStringAsync();
            Assert.DoesNotContain(Key, readText, StringComparison.Ordinal);
            var view = JsonDocument.Parse(readText).RootElement;
            Assert.True(view.GetProperty("apiKeySaved").GetBoolean());
            Assert.Equal("claude-opus-5-5", view.GetProperty("defaultModel").GetString());
            Assert.Contains(view.GetProperty("skills").EnumerateArray(), skill => skill.GetProperty("key").GetString() == AiSkillCatalog.SizeChartKey);

            await using var scope = fixture.Host!.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var row = await db.TenantSettings.AsNoTracking().SingleAsync(x => x.TenantId == fixture.TenantId && x.Key == AiSettingsService.SettingKey);
            Assert.DoesNotContain(Key, row.ValueJson, StringComparison.Ordinal);
            var credentials = await scope.ServiceProvider.GetRequiredService<AiSettingsService>().LoadCredentialsAsync(fixture.TenantId, CancellationToken.None);
            Assert.Equal(Key, credentials?.ApiKey);
            Assert.False(await db.AuditLogs.AnyAsync(x => x.Action.StartsWith("AI_") && x.Reason.Contains(Key)));
        }
    }

    // O9: skills uploaded with one key live in its workspace; a new key forgets them ("Yüklenmedi"), the same key keeps them.
    [PostgreSqlFact]
    public async Task A_new_key_forgets_the_uploaded_skills_and_the_same_key_keeps_them()
    {
        const string first = "sk-ant-api03-e2e-first-workspace-1111";
        const string second = "sk-ant-api03-e2e-other-workspace-2222";
        var (admin, csrf, signIn) = await fixture.SignInAsync(MembershipRole.Administrator);
        using (admin)
        using (signIn)
        {
            async Task<JsonElement> SaveAsync(string apiKey)
            {
                using var current = await admin.GetAsync("/api/v1/settings/ai");
                var request = ApiTestHost.Json(HttpMethod.Put, "/api/v1/settings/ai", csrf, new { apiKey, defaultModel = "claude-sonnet-5-5" });
                if (current.Headers.ETag is { } etag) request.Headers.TryAddWithoutValidation("If-Match", etag.Tag);
                using var saved = await admin.SendAsync(request);
                Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
                return JsonDocument.Parse(await saved.Content.ReadAsStringAsync()).RootElement.Clone();
            }
            async Task RecordSkillAsync()
            {
                await using var scope = fixture.Host!.Services.CreateAsyncScope();
                var service = scope.ServiceProvider.GetRequiredService<AiSettingsService>();
                var email = fixture.Email(MembershipRole.Administrator);
                var userId = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Users.AsNoTracking().Where(user => user.Email == email).Select(user => user.Id).SingleAsync();
                await service.RecordSkillAsync(fixture.TenantId, userId, "e2e", AiSkillCatalog.Find(AiSkillCatalog.SizeChartKey)!, new ClaudeSkillUpload("skill_e2e", "1"), CancellationToken.None);
            }
            static bool Uploaded(JsonElement view) => view.GetProperty("skills").EnumerateArray().Single(skill => skill.GetProperty("key").GetString() == AiSkillCatalog.SizeChartKey).GetProperty("uploaded").ValueKind == JsonValueKind.Object;

            await SaveAsync(first);
            await RecordSkillAsync();
            Assert.True(Uploaded(await SaveAsync(first)));
            Assert.False(Uploaded(await SaveAsync(second)));
        }
    }
}
