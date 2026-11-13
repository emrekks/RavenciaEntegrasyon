using System.Net;
using MarketplaceHub.Api.Catalog;
using MarketplaceHub.Api.Security;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Backups;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// The security review of 13 Nov 2026: where the off-site copy goes is the owner's, the bell writes only its user's own
// notifications with links inside the panel, the Google consent address is never replayed, the stage probe is elevated.
public sealed class SecurityAudit20261113Tests
{
    [Theory]
    [InlineData("PUT", "/api/v1/settings/offsite-backup", "OWNER")]
    [InlineData("POST", "/api/v1/settings/offsite-backup/google/authorize", "OWNER")]
    [InlineData("POST", "/api/v1/settings/offsite-backup/google/complete", "OWNER")]
    [InlineData("POST", "/api/v1/settings/offsite-backup/google/disconnect", "OWNER")]
    [InlineData("POST", "/api/v1/settings/offsite-backup/run", "ADMINISTRATOR,OWNER")]
    [InlineData("POST", "/api/v1/settings/offsite-backup/test", "ADMINISTRATOR,OWNER")]
    [InlineData("GET", "/api/v1/settings/offsite-backup", "ADMINISTRATOR,OWNER")]
    [InlineData("POST", "/api/v1/invoices/11111111-1111-1111-1111-111111111111/stage-capability-probe-jobs", "ADMINISTRATOR,OWNER")]
    public void Routes_TakeTheirRoles(string method, string path, string roles) =>
        Assert.Equal(roles.Split(',').Order(StringComparer.Ordinal), RoleAuthorizationMiddleware.RolesFor(method, path)!.Order(StringComparer.Ordinal));

    [Fact]
    public void TheGoogleConsentAddressIsNeverStoredForReplay()
    {
        Assert.True(SecretResponseRoutes.Matches("POST", "/api/v1/settings/offsite-backup/google/authorize"));
        Assert.True(SecretResponseRoutes.Matches("POST", "/API/v1/Settings/offsite-backup/google/authorize/"));
        Assert.False(SecretResponseRoutes.Matches("POST", "/api/v1/settings/offsite-backup/google/disconnect"));
    }

    [Theory]
    [InlineData("/orders/o1", "/orders/o1")]
    [InlineData(" /settings?tab=offsite ", "/settings?tab=offsite")]
    [InlineData("https://evil.example/x", null)]
    [InlineData("//evil.example", null)]
    [InlineData("/\\evil.example", null)]
    [InlineData("javascript:alert(1)", null)]
    [InlineData("orders/1", null)]
    [InlineData("/a\nb", null)]
    [InlineData(null, null)]
    public void ANotificationLinksOnlyInsideThePanel(string? href, string? expected) => Assert.Equal(expected, PanelNotificationPolicy.InternalHref(href));

    [Fact]
    public void AttentionAndServerKeysAreTheServers()
    {
        Assert.True(PanelNotificationPolicy.IsServerKey("attention:cursor:c1:RETURNS:100"));
        Assert.True(PanelNotificationPolicy.IsServerKey(" Attention:x"));
        Assert.True(PanelNotificationPolicy.IsServerKey("offsite-backup:destination:1"));
        Assert.False(PanelNotificationPolicy.IsServerKey("n-123"));
        Assert.False(PanelNotificationPolicy.IsServerKey(null));
    }

    [Fact]
    public void AnAttentionNoticeIsRecordedForTheWholeTenantUnderTheBellsKey()
    {
        var connection = Guid.NewGuid();
        var input = AttentionNoticePolicy.CenterInput(new AttentionNoticeView("cursor:c1:RETURNS", "100", AttentionNoticePolicy.SyncCursorFailingCode, "İade okuması art arda başarısız", "Üç kez hata.", connection, "trendyol", "Mağaza", "RETURNS", 3, null, null, null, []));
        Assert.Equal(("attention:cursor:c1:RETURNS:100", true, PanelNotificationCategories.Attention, $"/integrations/{connection}", "TRENDYOL"), (input.ClientKey, input.Shared, input.Category, input.Href, input.PlatformCode));
        var product = Guid.NewGuid();
        var expired = AttentionNoticePolicy.CenterInput(new AttentionNoticeView("approval", "7", AttentionNoticePolicy.ApprovalDeadlineExpiredCode, "Onay süresi doldu", "x", connection, "trendyol", "Mağaza", null, 1, null, null, null, [new AttentionNoticeItemView(product, "Kart", [])]));
        Assert.Equal($"/products/{product}", expired.Href);
    }

    [Fact]
    public void ADestinationChangeIsDescribed_OtherChangesAreNot()
    {
        var before = View(age: "age1aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", account: "owner@gmail.com", connectedAt: new DateTimeOffset(2026, 11, 13, 15, 0, 0, TimeSpan.Zero));
        Assert.Null(OffsiteBackupChangeAlert.Describe(before, before with { KeepDays = 30 }, "owner@example.com"));
        var newKey = OffsiteBackupChangeAlert.Describe(before, before with { AgeRecipient = "age1bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb" }, "admin@example.com");
        Assert.Contains("age açık anahtarı", newKey, StringComparison.Ordinal);
        Assert.Contains("admin@example.com", newKey, StringComparison.Ordinal);
        var newAccount = OffsiteBackupChangeAlert.Describe(before, before with { Account = "someone@gmail.com", ConnectedAt = before.ConnectedAt!.Value.AddMinutes(5) }, "owner@example.com");
        Assert.Contains("someone@gmail.com", newAccount, StringComparison.Ordinal);
        // A disconnect is not a new destination.
        Assert.Null(OffsiteBackupChangeAlert.Describe(before, before with { Connected = false, Account = null, ConnectedAt = null }, "owner@example.com"));
    }

    private static OffsiteBackupView View(string age, string account, DateTimeOffset connectedAt) =>
        new(true, age, "NAME", "Ravencia Yedek", 14, 12, "x.apps.googleusercontent.com", true, true, account, connectedAt, false, null, null, null, null, null, null, null, null, 0, null, null, null, null, null, 1);
}

[Collection("postgres-integration")]
public sealed class NotificationWriteEndToEndTests(ApiEndToEndFixture fixture) : IClassFixture<ApiEndToEndFixture>
{
    [PostgreSqlFact]
    public async Task ABrowserWritesOnlyItsUsersOwnNotifications_WithLinksInsideThePanel()
    {
        var (client, csrf, signIn) = await fixture.SignInAsync(MembershipRole.Operations);
        using (client)
        using (signIn)
        {
            var key = $"bell-{Guid.NewGuid():N}";
            var items = new object[]
            {
                new { clientKey = key, severity = "ERROR", category = "ORDER", title = "Sahte", message = "Herkese görünsün", href = "https://evil.example/login", shared = true, read = false },
                new { clientKey = $"attention:offsite-backup:{Guid.NewGuid():N}", severity = "ERROR", category = "ATTENTION", title = "Sahte uyarı", message = "x", href = "/settings", shared = true, read = false }
            };
            using var response = await client.SendAsync(ApiTestHost.Json(HttpMethod.Post, "/api/v1/notifications", csrf, new { items }));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            await using var scope = fixture.Host!.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var userId = await db.Users.Where(user => user.Email == fixture.Email(MembershipRole.Operations)).Select(user => user.Id).SingleAsync();
            var row = await db.PanelNotifications.AsNoTracking().SingleAsync(x => x.TenantId == fixture.TenantId && x.ClientKey == key);
            Assert.Equal(userId, row.UserId);
            Assert.Null(row.Href);
            Assert.False(await db.PanelNotifications.AnyAsync(x => x.TenantId == fixture.TenantId && x.ClientKey.StartsWith("attention:offsite-backup:") && x.Title == "Sahte uyarı"));
        }
    }
}
