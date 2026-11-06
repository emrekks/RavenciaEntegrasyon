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

            using var invalid = await admin.SendAsync(ApiTestHost.Json(HttpMethod.Put, "/api/v1/settings/ai", csrf, new { apiKey = "not-a-key", defaultModel = "claude-sonnet-5-5" }));
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);

            using var saved = await admin.SendAsync(ApiTestHost.Json(HttpMethod.Put, "/api/v1/settings/ai", csrf, new { apiKey = Key, defaultModel = "claude-sonnet-5-5" }));
            var savedText = await saved.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
            Assert.DoesNotContain(Key, savedText, StringComparison.Ordinal);
            Assert.True(JsonDocument.Parse(savedText).RootElement.GetProperty("apiKeySaved").GetBoolean());

            // A blank key keeps the saved one.
            using var kept = await admin.SendAsync(ApiTestHost.Json(HttpMethod.Put, "/api/v1/settings/ai", csrf, new { apiKey = "", defaultModel = "claude-opus-5-5" }));
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
}
