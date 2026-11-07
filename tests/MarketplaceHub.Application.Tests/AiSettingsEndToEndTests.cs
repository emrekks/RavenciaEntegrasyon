using System.Net;
using System.Text.Json;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Ai;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.AspNetCore.TestHost;
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

    // Claude's Skills API as a fake: records what would have been uploaded.
    private sealed class FakeSkillsApi : IClaudeClient
    {
        public List<(string? ExistingSkillId, IReadOnlyList<string> Paths)> Uploads { get; } = [];
        public Task<ClaudeUsage> TestAsync(string apiKey, string model, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ClaudeSkillResult> RunSkillAsync(ClaudeSkillRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<byte[]> DownloadFileAsync(string apiKey, string fileId, long maxBytes, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ClaudeSkillUpload> UploadSkillAsync(string apiKey, string displayTitle, IReadOnlyList<ClaudeSkillFile> files, string? existingSkillId, CancellationToken cancellationToken)
        {
            Uploads.Add((existingSkillId, files.Select(file => file.Path).ToList()));
            return Task.FromResult(new ClaudeSkillUpload(existingSkillId ?? $"skill_{Uploads.Count}", $"skver_{Uploads.Count}"));
        }
    }

    // The owner's .skill file through the endpoint: checked, sent as a new skill (its name differs from the bundled
    // one), then as a new version of it; a broken file is refused before anything is sent.
    [PostgreSqlFact]
    public async Task An_uploaded_skill_file_is_checked_and_sent_as_a_skill_then_as_its_new_version()
    {
        var fake = new FakeSkillsApi();
        using var host = fixture.Host!.WithWebHostBuilder(builder => builder.ConfigureTestServices(services => services.AddSingleton<IClaudeClient>(fake)));
        using var admin = host.CreateClient();
        var csrf = await fixture.Host.CsrfAsync(admin);
        using (var signIn = await admin.SendAsync(ApiTestHost.Json(HttpMethod.Post, "/api/v1/auth/login", csrf, new { email = fixture.Email(MembershipRole.Administrator), password = ApiEndToEndFixture.Password })))
            Assert.Equal(HttpStatusCode.OK, signIn.StatusCode);
        using (var current = await admin.GetAsync("/api/v1/settings/ai"))
        {
            var request = ApiTestHost.Json(HttpMethod.Put, "/api/v1/settings/ai", csrf, new { apiKey = "sk-ant-api03-e2e-skill-file-upload-3333", defaultModel = "claude-sonnet-5-5" });
            if (current.Headers.ETag is { } etag) request.Headers.TryAddWithoutValidation("If-Match", etag.Tag);
            using var saved = await admin.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        }

        async Task<HttpResponseMessage> UploadAsync(byte[] bytes, string fileName)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/settings/ai/skills/{AiSkillCatalog.SizeChartKey}/upload");
            request.Headers.Add("X-CSRF-TOKEN", csrf);
            var form = new MultipartFormDataContent();
            form.Add(new ByteArrayContent(bytes), "file", fileName);
            request.Content = form;
            return await admin.SendAsync(request);
        }
        const string skillMd = "---\nname: ravencia-beden-tablosu\ndescription: Ravencia beden tablosu.\n---\n";
        var skill = AiSkillPackageTests.Zip(("ravencia-beden-tablosu/SKILL.md", skillMd), ("ravencia-beden-tablosu/scripts/render_size_chart.py", "print(1)"));

        using (var traversal = await UploadAsync(AiSkillPackageTests.Zip(("a/SKILL.md", skillMd), ("a/../../x.py", "1")), "kotu.skill"))
            Assert.Equal((HttpStatusCode)422, traversal.StatusCode);
        using (var wrongType = await UploadAsync(skill, "beceri.txt"))
            Assert.Equal((HttpStatusCode)422, wrongType.StatusCode);
        Assert.Empty(fake.Uploads);

        using (var first = await UploadAsync(skill, "ravencia-beden-tablosu.skill"))
        {
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
            var uploaded = JsonDocument.Parse(await first.Content.ReadAsStringAsync()).RootElement.GetProperty("skills").EnumerateArray().Single(item => item.GetProperty("key").GetString() == AiSkillCatalog.SizeChartKey);
            Assert.True(uploaded.GetProperty("upToDate").GetBoolean());
            Assert.Equal("FILE", uploaded.GetProperty("uploaded").GetProperty("source").GetString());
            Assert.Equal("ravencia-beden-tablosu.skill", uploaded.GetProperty("uploaded").GetProperty("fileName").GetString());
        }
        using (var second = await UploadAsync(skill, "ravencia-beden-tablosu.skill"))
            Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        Assert.Equal(2, fake.Uploads.Count);
        Assert.Null(fake.Uploads[0].ExistingSkillId);
        Assert.Equal("skill_1", fake.Uploads[1].ExistingSkillId);
        Assert.Equal(["ravencia-beden-tablosu/SKILL.md", "ravencia-beden-tablosu/scripts/render_size_chart.py"], fake.Uploads[1].Paths);

        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.True(await db.AiUsageLogs.AnyAsync(x => x.TenantId == fixture.TenantId && x.Feature == "SKILL_UPLOAD" && x.SubjectLabel == "Beden Tablosu · ravencia-beden-tablosu.skill"));
    }
}
