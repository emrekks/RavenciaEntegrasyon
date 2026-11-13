using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Adapters.Shopify;
using MarketplaceHub.Infrastructure.Persistence;
using MarketplaceHub.Infrastructure.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// Scan 13 Nov 2026 #2: opening a webhook that ends in a timeout or a server fault may still have registered it at the
// marketplace. The row is no longer dropped blind: the marketplace's list decides (a webhook at this address is
// adopted, none removes the row), and an unreadable list keeps the row for the next opening or the hourly upkeep.
[Collection("postgres-integration")]
public sealed class WebhookRegistrationSettlementPostgreSqlTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    private static readonly TokenHasher Hasher = new(new byte[32]);

    [Fact]
    public void TheRouteTokenOfAReceivingAddressIsReadBack_AndOtherAddressesAreNot()
    {
        var service = Service(null!, new ScriptedPort(), TimeProvider.System);
        Assert.Equal(Hasher.Hash("route-token-1"), service.RouteTokenHashOf($"https://panel.ravencia.com/api/v1/hooks/{Guid.NewGuid():D}/route-token-1"));
        Assert.Null(service.RouteTokenHashOf("https://panel.ravencia.com/api/v1/other/route-token-1"));
        Assert.Null(service.RouteTokenHashOf(null));
    }

    [Fact]
    public void AShopifyRegistrationIsAdoptedOnlyWhenEveryTopicIsThere()
    {
        var all = ShopifyHttpClient.WebhookTopics.Select((topic, index) => new RemoteWebhook($"{index + 1}", "u", "ACTIVE", null, topic)).ToList();
        Assert.Equal(string.Join(',', all.Select(x => x.Id)), InstantNotificationService.AdoptableIds("SHOPIFY", all));
        Assert.Null(InstantNotificationService.AdoptableIds("SHOPIFY", all.Skip(1).ToList()));
        Assert.Equal("t-1", InstantNotificationService.AdoptableIds("TRENDYOL", [new("t-1", "u", "ACTIVE", null)]));
        Assert.Null(InstantNotificationService.AdoptableIds("TRENDYOL", []));
    }

    [Fact]
    public void ShopifyFindsATimedOutTopicAtTheAddress()
    {
        IReadOnlyList<RemoteWebhook> listed = [new("10", "https://a/hooks/x/t", "ACTIVE", null, "ORDERS_CREATE"), new("11", "https://other", "ACTIVE", null, "ORDERS_UPDATED")];
        Assert.Equal("10", ShopifyHttpClient.FindSubscription(listed, "ORDERS_CREATE", "https://a/hooks/x/t"));
        Assert.Null(ShopifyHttpClient.FindSubscription(listed, "ORDERS_UPDATED", "https://a/hooks/x/t"));
    }

    [PostgreSqlFact]
    public async Task ATrendyolOpeningThatTimedOut_AdoptsTheWebhookTrendyolMade()
    {
        var seed = await SeedAsync("settle-adopt", "TRENDYOL");
        var port = new ScriptedPort { FailCreate = true, CreateRegistersAnyway = true };
        try
        {
            await using var db = fixture.CreateContext();
            var result = await Service(db, port, TimeProvider.System).EnableAsync(seed.TenantId, seed.ConnectionId, "enable", CancellationToken.None);

            Assert.Null(result.Error);
            var subscription = await db.WebhookSubscriptions.AsNoTracking().SingleAsync(x => x.TenantId == seed.TenantId);
            Assert.Equal("ACTIVE", subscription.Status);
            Assert.Equal("remote-1", subscription.ExternalSubscriptionId);
            Assert.NotNull(subscription.VerifiedAt);
            Assert.Empty(port.Deleted);
        }
        finally { await DeleteAsync(seed.TenantId); }
    }

    [PostgreSqlFact]
    public async Task ATrendyolOpeningThatTimedOut_WithNothingAtTrendyol_RemovesTheRow()
    {
        var seed = await SeedAsync("settle-absent", "TRENDYOL");
        var port = new ScriptedPort { FailCreate = true };
        try
        {
            await using var db = fixture.CreateContext();
            var result = await Service(db, port, TimeProvider.System).EnableAsync(seed.TenantId, seed.ConnectionId, "enable", CancellationToken.None);

            Assert.Equal("WEBHOOK_REGISTRATION_FAILED", result.Error!.Code);
            Assert.False(await db.WebhookSubscriptions.AnyAsync(x => x.TenantId == seed.TenantId));
        }
        finally { await DeleteAsync(seed.TenantId); }
    }

    [PostgreSqlFact]
    public async Task AnUnreadableList_KeepsTheRow_AndTheHourlyUpkeepAdoptsItLater()
    {
        var seed = await SeedAsync("settle-later", "TRENDYOL");
        var port = new ScriptedPort { FailCreate = true, CreateRegistersAnyway = true, FailList = true };
        try
        {
            await using (var db = fixture.CreateContext())
            {
                var result = await Service(db, port, TimeProvider.System).EnableAsync(seed.TenantId, seed.ConnectionId, "enable", CancellationToken.None);
                Assert.Equal("WEBHOOK_REGISTRATION_UNCERTAIN", result.Error!.Code);
                var kept = await db.WebhookSubscriptions.AsNoTracking().SingleAsync(x => x.TenantId == seed.TenantId);
                Assert.Equal("ACTIVE", kept.Status);
                Assert.Null(kept.ExternalSubscriptionId);
            }

            // Within the grace the upkeep leaves it alone (an opening may still be in flight).
            port.FailList = false;
            await using (var db = fixture.CreateContext())
            {
                await Service(db, port, TimeProvider.System).RetryConnectionWebhooksAsync(CancellationToken.None);
                Assert.Null((await db.WebhookSubscriptions.AsNoTracking().SingleAsync(x => x.TenantId == seed.TenantId)).ExternalSubscriptionId);
            }
            await using (var db = fixture.CreateContext())
            {
                Assert.True(await Service(db, port, new ShiftedClock(TimeSpan.FromMinutes(10))).RetryConnectionWebhooksAsync(CancellationToken.None) >= 1);
                Assert.Equal("remote-1", (await db.WebhookSubscriptions.AsNoTracking().SingleAsync(x => x.TenantId == seed.TenantId)).ExternalSubscriptionId);
            }
            Assert.Equal(1, port.Creates);
        }
        finally { await DeleteAsync(seed.TenantId); }
    }

    [PostgreSqlFact]
    public async Task AShopifyOpeningLeftIncomplete_RemovesWhatItFindsAndTheRow()
    {
        var seed = await SeedAsync("settle-shopify", "SHOPIFY");
        var port = new ScriptedPort { FailCreate = true, CreateRegistersAnyway = true, ShopifyTopicsRegistered = 2 };
        try
        {
            await using var db = fixture.CreateContext();
            var result = await Service(db, port, TimeProvider.System).EnableAsync(seed.TenantId, seed.ConnectionId, "enable", CancellationToken.None);

            Assert.Equal("WEBHOOK_REGISTRATION_FAILED", result.Error!.Code);
            Assert.Equal(["remote-1", "remote-2"], port.Deleted);
            Assert.False(await db.WebhookSubscriptions.AnyAsync(x => x.TenantId == seed.TenantId));
        }
        finally { await DeleteAsync(seed.TenantId); }
    }

    [PostgreSqlFact]
    public async Task AShopifyOpeningWithEveryTopicRegistered_IsAdopted()
    {
        var seed = await SeedAsync("settle-shopify-all", "SHOPIFY");
        var port = new ScriptedPort { FailCreate = true, CreateRegistersAnyway = true, ShopifyTopicsRegistered = ShopifyHttpClient.WebhookTopics.Length };
        try
        {
            await using var db = fixture.CreateContext();
            var result = await Service(db, port, TimeProvider.System).EnableAsync(seed.TenantId, seed.ConnectionId, "enable", CancellationToken.None);

            Assert.Null(result.Error);
            Assert.Equal(string.Join(',', Enumerable.Range(1, ShopifyHttpClient.WebhookTopics.Length).Select(index => $"remote-{index}")),
                (await db.WebhookSubscriptions.AsNoTracking().SingleAsync(x => x.TenantId == seed.TenantId)).ExternalSubscriptionId);
        }
        finally { await DeleteAsync(seed.TenantId); }
    }

    private static InstantNotificationService Service(AppDbContext db, ScriptedPort port, TimeProvider clock) =>
        new(db, port, Hasher, new EphemeralDataProtectionProvider(),
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Marketplace:PublicBaseUrl"] = "https://panel.ravencia.com" }).Build(),
            clock, NullLogger<InstantNotificationService>.Instance);

    private sealed record Seed(Guid TenantId, Guid ConnectionId);

    private async Task<Seed> SeedAsync(string code, string platformCode)
    {
        var tenant = new Tenant { Id = Guid.CreateVersion7(), Code = $"{code}-{Guid.NewGuid():N}", DisplayName = code, Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow, Version = 1 };
        var connection = new PlatformConnection { Id = Guid.CreateVersion7(), TenantId = tenant.Id, PublicId = Guid.CreateVersion7(), DisplayName = "Webhook test mağazası", Environment = "STAGE", ExternalStoreId = "seller-17", Status = "ACTIVE", ApiVersion = "V1", PlatformCode = platformCode, Version = 1 };
        await using var db = fixture.CreateContext();
        db.Tenants.Add(tenant);
        db.PlatformConnections.Add(connection);
        await db.SaveChangesAsync();
        return new(tenant.Id, connection.Id);
    }

    private async Task DeleteAsync(Guid tenantId)
    {
        await using var db = fixture.CreateContext();
        await db.IntegrationOutboxEvents.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.IntegrationJobs.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.WebhookSubscriptions.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.OperationalIssues.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.SyncCursors.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.PlatformConnections.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.Tenants.Where(row => row.Id == tenantId).ExecuteDeleteAsync();
    }

    private sealed class ShiftedClock(TimeSpan shift) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow + shift;
    }

    // A marketplace whose create call times out; with CreateRegistersAnyway it registered the address all the same
    // (Shopify: the first ShopifyTopicsRegistered topics, Trendyol one webhook).
    private sealed class ScriptedPort : IWebhookRegistrationPort
    {
        private readonly List<RemoteWebhook> remote = [];
        public bool FailCreate { get; set; }
        public bool CreateRegistersAnyway { get; set; }
        public bool FailList { get; set; }
        public int ShopifyTopicsRegistered { get; set; }
        public int Creates { get; private set; }
        public List<string> Deleted { get; } = [];
        private static AdapterError Timeout => new(AdapterErrorClass.TransientNetwork, "REMOTE_TIMEOUT", "Pazaryeri isteği zaman aşımına uğradı.", null, null, null);

        public Task<AdapterResult<IReadOnlyList<RemoteWebhook>>> ListWebhooksAsync(AdapterContext context, CancellationToken cancellationToken) =>
            Task.FromResult(FailList
                ? AdapterResult<IReadOnlyList<RemoteWebhook>>.Failure(Timeout)
                : AdapterResult<IReadOnlyList<RemoteWebhook>>.Success([.. remote, new RemoteWebhook("someone-else", "https://elsewhere.example/hook", "ACTIVE", null)]));

        public Task<AdapterResult<string>> CreateWebhookAsync(AdapterContext context, string url, string username, string password, CancellationToken cancellationToken)
        {
            Creates++;
            if (CreateRegistersAnyway)
            {
                if (ShopifyTopicsRegistered > 0)
                    remote.AddRange(ShopifyHttpClient.WebhookTopics.Take(ShopifyTopicsRegistered).Select((topic, index) => new RemoteWebhook($"remote-{index + 1}", url, "ACTIVE", null, topic)));
                else
                    remote.Add(new RemoteWebhook($"remote-{remote.Count + 1}", url, "ACTIVE", username));
            }
            return Task.FromResult(FailCreate ? AdapterResult<string>.Failure(Timeout) : AdapterResult<string>.Success($"remote-{remote.Count}"));
        }

        public Task<AdapterResult<bool>> DeleteWebhookAsync(AdapterContext context, string externalId, CancellationToken cancellationToken)
        {
            Deleted.Add(externalId);
            remote.RemoveAll(webhook => webhook.Id == externalId);
            return Task.FromResult(AdapterResult<bool>.Success(true));
        }

        public Task<AdapterResult<string?>> RestoreMissingWebhooksAsync(AdapterContext context, string externalId, CancellationToken cancellationToken) =>
            Task.FromResult(AdapterResult<string?>.Success(null));

        public Task<AdapterResult<bool>> ActivateWebhookAsync(AdapterContext context, string externalId, CancellationToken cancellationToken) =>
            Task.FromResult(AdapterResult<bool>.Success(true));
    }
}
