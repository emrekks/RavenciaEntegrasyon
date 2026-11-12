using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using MarketplaceHub.Infrastructure.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// Pasife Al closes the connection's webhook at the marketplace and activation opens it again; a marketplace that
// refuses either call never stops the connection's own change, and the hourly upkeep tries again.
public sealed partial class PostgreSqlTenantIsolationTests
{
    [PostgreSqlFact]
    public async Task PasifeAl_ClosesTheWebhookAndActivationOpensItAgain()
    {
        var context = await SeedWebhookPauseAsync("webhook-pause", "TRENDYOL");
        try
        {
            await using (var db = fixture.CreateContext())
                Assert.Null((await context.Notifications(db).EnableAsync(context.TenantId, context.ConnectionId, "enable", CancellationToken.None)).Error);

            await using (var db = fixture.CreateContext())
            {
                var deactivated = await context.Connections(db).SetActiveAsync(context.TenantId, context.ConnectionId, await VersionAsync(db, context), false, CancellationToken.None);
                Assert.Null(deactivated.Error);
                Assert.Equal("DISABLED", deactivated.Value!.Status);
                Assert.Null(deactivated.Value.WebhookNotice);
                Assert.Equal(["remote-webhook-1"], context.Port.Deleted);
                Assert.Equal(InstantNotificationService.PausedStatus, (await db.WebhookSubscriptions.SingleAsync(x => x.TenantId == context.TenantId)).Status);
                var view = await context.Notifications(db).StatusAsync(context.TenantId, context.ConnectionId, "status", CancellationToken.None);
                Assert.Equal("OFF", view.Value!.State);
                Assert.Contains("kendiliğinden açar", view.Value.Message);
            }

            await using (var db = fixture.CreateContext())
            {
                await PassConnectionTestAsync(db, context);
                var activated = await context.Connections(db).SetActiveAsync(context.TenantId, context.ConnectionId, await VersionAsync(db, context), true, CancellationToken.None);
                Assert.Null(activated.Error);
                Assert.Equal("ACTIVE", activated.Value!.Status);
                Assert.Null(activated.Value.WebhookNotice);
                Assert.Equal(2, context.Port.Created);
                var subscriptions = await db.WebhookSubscriptions.AsNoTracking().Where(x => x.TenantId == context.TenantId).ToListAsync();
                Assert.Single(subscriptions, x => x.Status == "ACTIVE");
                Assert.DoesNotContain(subscriptions, x => x.Status == InstantNotificationService.PausedStatus);
                // The activation's catch-up read (the bootstrap with the full order recovery) is queued as before.
                Assert.True(await db.IntegrationJobs.AnyAsync(x => x.TenantId == context.TenantId && x.JobDedupKey.StartsWith(MarketplaceJobTypes.ActivationBootstrapPrefix)));
            }
        }
        finally
        {
            await DeleteWebhookPauseTenantAsync(context.TenantId);
        }
    }

    [PostgreSqlFact]
    public async Task PasifeAl_KeepsTheConnectionChangeWhenTheMarketplaceRefusesAndRetriesHourly()
    {
        var context = await SeedWebhookPauseAsync("webhook-pause-fail", "SHOPIFY");
        try
        {
            await using (var db = fixture.CreateContext())
                Assert.Null((await context.Notifications(db).EnableAsync(context.TenantId, context.ConnectionId, "enable", CancellationToken.None, "shpss_pause_secret")).Error);

            // The close is refused: the connection is still set Pasif, the webhook stays and a retry is kept.
            context.Port.FailDelete = true;
            await using (var db = fixture.CreateContext())
            {
                var deactivated = await context.Connections(db).SetActiveAsync(context.TenantId, context.ConnectionId, await VersionAsync(db, context), false, CancellationToken.None);
                Assert.Null(deactivated.Error);
                Assert.Equal("DISABLED", deactivated.Value!.Status);
                Assert.Contains("Shopify webhook'u kapatılamadı", deactivated.Value.WebhookNotice);
                Assert.Contains("Webhook'u Kapat", deactivated.Value.WebhookNotice);
                Assert.Equal("ACTIVE", (await db.WebhookSubscriptions.SingleAsync(x => x.TenantId == context.TenantId)).Status);
                Assert.True(await db.OperationalIssues.AnyAsync(x => x.TenantId == context.TenantId && x.DedupeKey == InstantNotificationService.PausePendingKey(context.ConnectionId) && x.Status == IssueStatus.Open));
            }

            context.Port.FailDelete = false;
            await using (var db = fixture.CreateContext())
            {
                Assert.True(await context.Notifications(db).RetryConnectionWebhooksAsync(CancellationToken.None) >= 1);
                Assert.Equal(InstantNotificationService.PausedStatus, (await db.WebhookSubscriptions.SingleAsync(x => x.TenantId == context.TenantId)).Status);
                Assert.False(await db.OperationalIssues.AnyAsync(x => x.TenantId == context.TenantId && x.DedupeKey.StartsWith("webhook-pause:") && x.Status != IssueStatus.Resolved));
            }

            // The reopening is refused: the connection is still activated, with a warning that names Webhook'u Aç.
            context.Port.FailCreate = true;
            await using (var db = fixture.CreateContext())
            {
                await PassConnectionTestAsync(db, context);
                var activated = await context.Connections(db).SetActiveAsync(context.TenantId, context.ConnectionId, await VersionAsync(db, context), true, CancellationToken.None);
                Assert.Null(activated.Error);
                Assert.Equal("ACTIVE", activated.Value!.Status);
                Assert.Contains("yeniden açılamadı", activated.Value.WebhookNotice);
                Assert.Contains("Webhook'u Aç", activated.Value.WebhookNotice);
                Assert.Equal(InstantNotificationService.PausedStatus, (await db.WebhookSubscriptions.SingleAsync(x => x.TenantId == context.TenantId)).Status);
                var view = await context.Notifications(db).StatusAsync(context.TenantId, context.ConnectionId, "status", CancellationToken.None);
                Assert.Equal("OFF", view.Value!.State);
                Assert.Contains("Webhook'u Aç", view.Value.Message);
            }

            context.Port.FailCreate = false;
            await using (var db = fixture.CreateContext())
            {
                Assert.True(await context.Notifications(db).RetryConnectionWebhooksAsync(CancellationToken.None) >= 1);
                var subscriptions = await db.WebhookSubscriptions.AsNoTracking().Where(x => x.TenantId == context.TenantId).ToListAsync();
                var reopened = Assert.Single(subscriptions, x => x.Status == "ACTIVE");
                // The reopened Shopify webhook is signed with the secret the closed one had.
                Assert.Equal("HMAC_SHA256", reopened.AuthenticationType);
                Assert.DoesNotContain(subscriptions, x => x.Status == InstantNotificationService.PausedStatus);
                Assert.False(await db.OperationalIssues.AnyAsync(x => x.TenantId == context.TenantId && x.DedupeKey.StartsWith("webhook-resume:") && x.Status != IssueStatus.Resolved));
            }
        }
        finally
        {
            await DeleteWebhookPauseTenantAsync(context.TenantId);
        }
    }

    [PostgreSqlFact]
    public async Task PasifeAl_LeavesAHepsiburadaWebhookAsItIsWithItsNote()
    {
        var context = await SeedWebhookPauseAsync("webhook-pause-hb", "HEPSIBURADA");
        try
        {
            await using (var db = fixture.CreateContext())
                Assert.Null((await context.Notifications(db).EnableAsync(context.TenantId, context.ConnectionId, "enable", CancellationToken.None)).Error);
            await using (var db = fixture.CreateContext())
            {
                var deactivated = await context.Connections(db).SetActiveAsync(context.TenantId, context.ConnectionId, await VersionAsync(db, context), false, CancellationToken.None);
                Assert.Null(deactivated.Error);
                Assert.Equal(InstantNotificationService.HepsiburadaPauseNote, deactivated.Value!.WebhookNotice);
                Assert.Empty(context.Port.Deleted);
                Assert.Equal("ACTIVE", (await db.WebhookSubscriptions.SingleAsync(x => x.TenantId == context.TenantId)).Status);
            }
        }
        finally
        {
            await DeleteWebhookPauseTenantAsync(context.TenantId);
        }
    }

    private sealed record WebhookPauseContext(Guid TenantId, Guid ConnectionId, SwitchableWebhookPort Port, Func<AppDbContext, InstantNotificationService> Notifications, Func<AppDbContext, MarketplaceConnectionService> Connections);

    private async Task<WebhookPauseContext> SeedWebhookPauseAsync(string code, string platformCode)
    {
        var tenant = NewTenant(code);
        var connection = NewQuestionConnection(tenant);
        connection.PlatformCode = platformCode;
        connection.Version = 1;
        connection.LastSuccessAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        await using (var seedDb = fixture.CreateContext())
        {
            seedDb.Tenants.Add(tenant);
            seedDb.PlatformConnections.Add(connection);
            seedDb.PlatformCapabilities.Add(new PlatformCapability { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, Code = MarketplaceCapabilities.ConnectionTest, SupportLevel = CapabilitySupportLevel.Supported, ApiVersion = connection.ApiVersion, Environment = connection.Environment, StoreScope = connection.ExternalStoreId, VerifiedAt = DateTimeOffset.UtcNow, Version = 1 });
            await seedDb.SaveChangesAsync();
        }
        var port = new SwitchableWebhookPort();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Marketplace:PublicBaseUrl"] = "https://panel.ravencia.com" }).Build();
        var dataProtection = new EphemeralDataProtectionProvider();
        var hasher = new TokenHasher(new byte[32]);
        InstantNotificationService Notifications(AppDbContext db) => new(db, port, hasher, dataProtection, configuration, fixture.TimeProvider, NullLogger<InstantNotificationService>.Instance);
        MarketplaceConnectionService Connections(AppDbContext db) => new(db, new CursorCodec(dataProtection, fixture.TimeProvider), dataProtection, hasher, fixture.TimeProvider, configuration, Notifications(db));
        return new(tenant.Id, connection.Id, port, Notifications, Connections);
    }

    private static Task<long> VersionAsync(AppDbContext db, WebhookPauseContext context) =>
        db.PlatformConnections.AsNoTracking().Where(x => x.TenantId == context.TenantId && x.Id == context.ConnectionId).Select(x => x.Version).SingleAsync();

    // A connection set Pasif needs a new successful test before it can be activated.
    private static Task PassConnectionTestAsync(AppDbContext db, WebhookPauseContext context) =>
        db.PlatformConnections.Where(x => x.TenantId == context.TenantId && x.Id == context.ConnectionId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.LastSuccessAt, DateTimeOffset.UtcNow.AddSeconds(5)));

    private async Task DeleteWebhookPauseTenantAsync(Guid tenantId)
    {
        await using (var db = fixture.CreateContext())
        {
            await db.IntegrationJobs.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
            await db.WebhookSubscriptions.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
            await db.PlatformCapabilities.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
            await db.ConnectionSyncPolicies.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        }
        await DeleteInvoiceTestTenantAsync(tenantId);
    }

    private sealed class SwitchableWebhookPort : IWebhookRegistrationPort
    {
        public int Created { get; private set; }
        public List<string> Deleted { get; } = [];
        public bool FailDelete { get; set; }
        public bool FailCreate { get; set; }
        private static AdapterError Refused => new(AdapterErrorClass.TransientNetwork, "TEST_REFUSED", "Pazaryeri yanıt vermedi.", 503, null, null);

        public Task<AdapterResult<IReadOnlyList<RemoteWebhook>>> ListWebhooksAsync(AdapterContext context, CancellationToken cancellationToken) =>
            Task.FromResult(AdapterResult<IReadOnlyList<RemoteWebhook>>.Success([]));

        public Task<AdapterResult<string>> CreateWebhookAsync(AdapterContext context, string url, string username, string password, CancellationToken cancellationToken)
        {
            if (FailCreate) return Task.FromResult(AdapterResult<string>.Failure(Refused));
            Created++;
            return Task.FromResult(AdapterResult<string>.Success($"remote-webhook-{Created}"));
        }

        public Task<AdapterResult<bool>> DeleteWebhookAsync(AdapterContext context, string externalId, CancellationToken cancellationToken)
        {
            if (FailDelete) return Task.FromResult(AdapterResult<bool>.Failure(Refused));
            Deleted.Add(externalId);
            return Task.FromResult(AdapterResult<bool>.Success(true));
        }

        public Task<AdapterResult<string?>> RestoreMissingWebhooksAsync(AdapterContext context, string externalId, CancellationToken cancellationToken) =>
            Task.FromResult(AdapterResult<string?>.Success(null));

        public Task<AdapterResult<bool>> ActivateWebhookAsync(AdapterContext context, string externalId, CancellationToken cancellationToken) =>
            Task.FromResult(AdapterResult<bool>.Success(true));
    }
}
