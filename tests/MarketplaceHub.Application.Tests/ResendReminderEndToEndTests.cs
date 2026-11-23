using System.Net;
using System.Text.Json;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// Siparişler > Yeniden Gönderim: the number the owner typed before 23 Nov 2026. Since then the gear keeps records
// (/manual-resends); the old count is read by every signed-in role ("Eski Hatırlatma: N") and only lowered or cleared by
// owners and administrators, never raised.
[Collection("postgres-integration")]
public sealed class ResendReminderEndToEndTests(ApiEndToEndFixture fixture) : IClassFixture<ApiEndToEndFixture>
{
    [PostgreSqlFact]
    public async Task OldReminder_IsOnlyClearedNeverRaised_AndOnlyElevatedRolesWrite()
    {
        var (admin, csrf, adminSignIn) = await fixture.SignInAsync(MembershipRole.Administrator);
        var (operations, operationsCsrf, operationsSignIn) = await fixture.SignInAsync(MembershipRole.Operations);
        using (admin)
        using (adminSignIn)
        using (operations)
        using (operationsSignIn)
        {
            using var forbidden = await operations.SendAsync(ApiTestHost.Json(HttpMethod.Put, "/api/v1/settings/resend-reminder", operationsCsrf, new { count = 0 }));
            Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);

            HttpRequestMessage Put(object body, System.Net.Http.Headers.EntityTagHeaderValue? etag)
            {
                var request = ApiTestHost.Json(HttpMethod.Put, "/api/v1/settings/resend-reminder", csrf, body);
                if (etag is not null) request.Headers.TryAddWithoutValidation("If-Match", etag.Tag);
                return request;
            }

            using var before = await admin.GetAsync("/api/v1/settings/resend-reminder");
            Assert.Equal(HttpStatusCode.OK, before.StatusCode);
            using var negative = await admin.SendAsync(Put(new { count = -1 }, before.Headers.ETag));
            Assert.Equal(HttpStatusCode.BadRequest, negative.StatusCode);
            using var fraction = await admin.SendAsync(Put(new { count = 1.5 }, before.Headers.ETag));
            Assert.Equal(HttpStatusCode.BadRequest, fraction.StatusCode);
            // A new count is no longer typed: re-shipments are records now.
            using var raised = await admin.SendAsync(Put(new { count = 4 }, before.Headers.ETag));
            Assert.Equal(HttpStatusCode.Conflict, raised.StatusCode);
            Assert.Equal("RESEND_REMINDER_RECORDS_ONLY", (await ApiTestHost.ProblemAsync(raised)).GetProperty("code").GetString());

            // A count saved before the change (seeded as the old panel wrote it) is shown to every role until cleared.
            await using (var scope = fixture.Host!.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var setting = await db.TenantSettings.SingleOrDefaultAsync(x => x.TenantId == fixture.TenantId && x.Key == "orders-resend-reminder");
                if (setting is null) db.TenantSettings.Add(new TenantSetting { TenantId = fixture.TenantId, Key = "orders-resend-reminder", ValueJson = """{"count":4}""", UpdatedAt = DateTimeOffset.UtcNow, Version = 1 });
                else { setting.ValueJson = """{"count":4}"""; setting.Version++; }
                await db.SaveChangesAsync();
            }
            using var read = await operations.GetAsync("/api/v1/settings/resend-reminder");
            Assert.Equal(4, JsonDocument.Parse(await read.Content.ReadAsStringAsync()).RootElement.GetProperty("count").GetInt32());

            using var reset = await admin.SendAsync(Put(new { count = 0 }, read.Headers.ETag));
            Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
            using var again = await admin.GetAsync("/api/v1/settings/resend-reminder");
            Assert.Equal(0, JsonDocument.Parse(await again.Content.ReadAsStringAsync()).RootElement.GetProperty("count").GetInt32());
        }
    }
}
