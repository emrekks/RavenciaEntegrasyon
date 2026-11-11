using System.Net;
using System.Text.Json;
using MarketplaceHub.Domain;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// Siparişler > Yeniden Gönderim reminder: a per-tenant number the owner types; readable by every signed-in role, written by owners and administrators.
[Collection("postgres-integration")]
public sealed class ResendReminderEndToEndTests(ApiEndToEndFixture fixture) : IClassFixture<ApiEndToEndFixture>
{
    [PostgreSqlFact]
    public async Task Reminder_IsSavedPerTenant_ValidatedAndOnlyElevatedRolesWrite()
    {
        var (admin, csrf, adminSignIn) = await fixture.SignInAsync(MembershipRole.Administrator);
        var (operations, operationsCsrf, operationsSignIn) = await fixture.SignInAsync(MembershipRole.Operations);
        using (admin)
        using (adminSignIn)
        using (operations)
        using (operationsSignIn)
        {
            using var forbidden = await operations.SendAsync(ApiTestHost.Json(HttpMethod.Put, "/api/v1/settings/resend-reminder", operationsCsrf, new { count = 3 }));
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

            using var saved = await admin.SendAsync(Put(new { count = 4 }, before.Headers.ETag));
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
            Assert.Equal(4, JsonDocument.Parse(await saved.Content.ReadAsStringAsync()).RootElement.GetProperty("count").GetInt32());

            // Any signed-in role reads it.
            using var read = await operations.GetAsync("/api/v1/settings/resend-reminder");
            Assert.Equal(4, JsonDocument.Parse(await read.Content.ReadAsStringAsync()).RootElement.GetProperty("count").GetInt32());

            using var reset = await admin.SendAsync(Put(new { count = 0 }, saved.Headers.ETag));
            Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
            using var again = await admin.GetAsync("/api/v1/settings/resend-reminder");
            Assert.Equal(0, JsonDocument.Parse(await again.Content.ReadAsStringAsync()).RootElement.GetProperty("count").GetInt32());
        }
    }
}
