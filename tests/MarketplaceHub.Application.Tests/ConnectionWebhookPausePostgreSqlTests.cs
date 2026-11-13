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
[Collection("postgres-integration")]
public sealed class ConnectionWebhookPausePostgreSqlTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
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
                // Trendyol: set passive with its deactivate service, never removed; the row keeps its id and address.
                Assert.Equal(["remote-webhook-1"], context.Port.Deactivated);
                Assert.Empty(context.Port.Deleted);
                var paused = await db.WebhookSubscriptions.SingleAsync(x => x.TenantId == context.TenantId);
                Assert.Equal(InstantNotificationService.PausedStatus, paused.Status);
                Assert.Equal("remote-webhook-1", paused.ExternalSubscriptionId);
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
                // The same Trendyol webhook is activated again: no new registration, the same row and address.
                Assert.Equal(1, context.Port.Created);
                Assert.Equal(["remote-webhook-1"], context.Port.Activated);
                var subscriptions = await db.WebhookSubscriptions.AsNoTracking().Where(x => x.TenantId == context.TenantId).ToListAsync();
                Assert.Equal("remote-webhook-1", Assert.Single(subscriptions, x => x.Status == "ACTIVE").ExternalSubscriptionId);
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

    // A Trendyol webhook that does not activate again is replaced: one Trendyol no longer has (404) is not removed, one
    // that refuses otherwise is removed first so no passive copy stays registered.
    [PostgreSqlTheory]
    [InlineData(AdapterErrorClass.NotFound, false)]
    [InlineData(AdapterErrorClass.Remote5xx, true)]
    public async Task Activation_RemakesTheTrendyolWebhookWhenItDoesNotActivate(AdapterErrorClass refusal, bool removesTheOldOne)
    {
        var context = await SeedWebhookPauseAsync("webhook-activate-fallback", "TRENDYOL");
        try
        {
            await using (var db = fixture.CreateContext())
                Assert.Null((await context.Notifications(db).EnableAsync(context.TenantId, context.ConnectionId, "enable", CancellationToken.None)).Error);
            await using (var db = fixture.CreateContext())
                Assert.Null((await context.Connections(db).SetActiveAsync(context.TenantId, context.ConnectionId, await VersionAsync(db, context), false, CancellationToken.None)).Value!.WebhookNotice);

            context.Port.FailActivate = refusal;
            await using (var db = fixture.CreateContext())
            {
                await PassConnectionTestAsync(db, context);
                var activated = await context.Connections(db).SetActiveAsync(context.TenantId, context.ConnectionId, await VersionAsync(db, context), true, CancellationToken.None);
                Assert.Null(activated.Value!.WebhookNotice);
                Assert.Equal(2, context.Port.Created);
                Assert.Equal(removesTheOldOne ? new[] { "remote-webhook-1" } : Array.Empty<string>(), context.Port.Deleted);
                var subscriptions = await db.WebhookSubscriptions.AsNoTracking().Where(x => x.TenantId == context.TenantId).ToListAsync();
                Assert.Equal("remote-webhook-2", Assert.Single(subscriptions, x => x.Status == "ACTIVE").ExternalSubscriptionId);
                Assert.DoesNotContain(subscriptions, x => x.Status == InstantNotificationService.PausedStatus);
            }
        }
        finally
        {
            await DeleteWebhookPauseTenantAsync(context.TenantId);
        }
    }

    // Webhook'u Kapat (also what Bağlantıyı Sil runs) on a connection whose Trendyol webhook Pasife Al set passive removes
    // it at Trendyol, and it no longer opens with the connection.
    [PostgreSqlFact]
    public async Task ClosingAPassiveConnectionsTrendyolWebhook_RemovesItAtTrendyol()
    {
        var context = await SeedWebhookPauseAsync("webhook-passive-close", "TRENDYOL");
        try
        {
            await using (var db = fixture.CreateContext())
                Assert.Null((await context.Notifications(db).EnableAsync(context.TenantId, context.ConnectionId, "enable", CancellationToken.None)).Error);
            await using (var db = fixture.CreateContext())
                await context.Connections(db).SetActiveAsync(context.TenantId, context.ConnectionId, await VersionAsync(db, context), false, CancellationToken.None);

            await using (var db = fixture.CreateContext())
            {
                Assert.Null((await context.Notifications(db).DisableAsync(context.TenantId, context.ConnectionId, "close", CancellationToken.None)).Error);
                Assert.Equal(["remote-webhook-1"], context.Port.Deleted);
                Assert.Equal("DELETED", (await db.WebhookSubscriptions.AsNoTracking().SingleAsync(x => x.TenantId == context.TenantId)).Status);
            }
        }
        finally
        {
            await DeleteWebhookPauseTenantAsync(context.TenantId);
        }
    }

    // Shopify removes a subscription after eight failed deliveries in a row. A registration the panel keeps as open but
    // Shopify no longer lists is opened anew, on activation and in the hourly upkeep, signed with the secret it had.
    [PostgreSqlFact]
    public async Task AShopifyRegistrationShopifyRemoved_IsOpenedAgainOnActivationAndHourly()
    {
        var context = await SeedWebhookPauseAsync("webhook-shopify-removed", "SHOPIFY");
        try
        {
            await using (var db = fixture.CreateContext())
                Assert.Null((await context.Notifications(db).EnableAsync(context.TenantId, context.ConnectionId, "enable", CancellationToken.None, "shpss_removed_secret")).Error);

            // Hourly upkeep: Shopify lists none of them any more.
            context.Port.ShopifyRemovedAll = true;
            await using (var db = fixture.CreateContext())
            {
                Assert.Equal(1, await context.Notifications(db).ReactivatePassiveAsync(CancellationToken.None));
                var subscriptions = await db.WebhookSubscriptions.AsNoTracking().Where(x => x.TenantId == context.TenantId).ToListAsync();
                var reopened = Assert.Single(subscriptions, x => x.Status == "ACTIVE");
                Assert.Equal("remote-webhook-2", reopened.ExternalSubscriptionId);
                Assert.Equal("HMAC_SHA256", reopened.AuthenticationType);
                Assert.Single(subscriptions, x => x.Status == "DELETED");
            }

            // Activation of a connection whose registration stayed open (its close failed while Pasif): checked too.
            context.Port.FailDelete = true;
            await using (var db = fixture.CreateContext())
                await context.Connections(db).SetActiveAsync(context.TenantId, context.ConnectionId, await VersionAsync(db, context), false, CancellationToken.None);
            context.Port.FailDelete = false;
            await using (var db = fixture.CreateContext())
            {
                await PassConnectionTestAsync(db, context);
                var activated = await context.Connections(db).SetActiveAsync(context.TenantId, context.ConnectionId, await VersionAsync(db, context), true, CancellationToken.None);
                Assert.Null(activated.Value!.WebhookNotice);
                Assert.Equal(3, context.Port.Created);
                Assert.Equal("remote-webhook-3", (await db.WebhookSubscriptions.AsNoTracking().SingleAsync(x => x.TenantId == context.TenantId && x.Status == "ACTIVE")).ExternalSubscriptionId);
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
        var tenant = new Tenant { Id = Guid.CreateVersion7(), Code = $"{code}-{Guid.NewGuid():N}", DisplayName = code, Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow, Version = 1 };
        var connection = new PlatformConnection { Id = Guid.CreateVersion7(), TenantId = tenant.Id, PublicId = Guid.CreateVersion7(), DisplayName = "Webhook test mağazası", Environment = "STAGE", ExternalStoreId = "seller-17", Status = "ACTIVE", ApiVersion = "V1", PlatformCode = platformCode };

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
        await using var db = fixture.CreateContext();
        await db.IntegrationOutboxEvents.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.IntegrationJobs.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.WebhookSubscriptions.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.PlatformCapabilities.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.ConnectionSyncPolicies.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.OperationalIssues.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.SyncCursors.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.PlatformConnections.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.Tenants.Where(row => row.Id == tenantId).ExecuteDeleteAsync();
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

        // Shopify's answer when it lists none of the subscriptions any more (eight failed deliveries remove one).
        public bool ShopifyRemovedAll { get; set; }
        public List<string> Restored { get; } = [];

        public Task<AdapterResult<string?>> RestoreMissingWebhooksAsync(AdapterContext context, string externalId, CancellationToken cancellationToken)
        {
            Restored.Add(externalId);
            return Task.FromResult(ShopifyRemovedAll
                ? AdapterResult<string?>.Failure(new(AdapterErrorClass.NotFound, MarketplaceHub.Infrastructure.Adapters.Shopify.ShopifyHttpClient.WebhooksRemovedCode, "Shopify panelin bütün bildirim aboneliklerini kaldırmış.", 404, null, null))
                : AdapterResult<string?>.Success(null));
        }

        public List<string> Activated { get; } = [];
        public List<string> Deactivated { get; } = [];
        // The class of the activation's refusal (null: activated).
        public AdapterErrorClass? FailActivate { get; set; }

        public Task<AdapterResult<bool>> ActivateWebhookAsync(AdapterContext context, string externalId, CancellationToken cancellationToken)
        {
            if (FailActivate is { } errorClass) return Task.FromResult(AdapterResult<bool>.Failure(new(errorClass, "TEST_ACTIVATE_REFUSED", "Etkinleştirilemedi.", errorClass == AdapterErrorClass.NotFound ? 404 : 500, null, null)));
            Activated.Add(externalId);
            return Task.FromResult(AdapterResult<bool>.Success(true));
        }

        public Task<AdapterResult<bool>> DeactivateWebhookAsync(AdapterContext context, string externalId, CancellationToken cancellationToken)
        {
            if (FailDelete) return Task.FromResult(AdapterResult<bool>.Failure(Refused));
            Deactivated.Add(externalId);
            return Task.FromResult(AdapterResult<bool>.Success(true));
        }
    }
}
