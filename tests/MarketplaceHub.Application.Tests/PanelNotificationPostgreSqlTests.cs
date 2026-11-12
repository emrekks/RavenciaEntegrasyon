using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// The notification center on PostgreSQL: a notification the bell sends is kept (a repeat send updates it), removing it
// from the bell only marks it dismissed and the center still lists it, the retention removes rows older than a week,
// and a tenant or a user never sees another's notifications.
[Collection("postgres-integration")]
public sealed class PanelNotificationPostgreSqlTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>, IAsyncLifetime
{
    private static readonly System.Collections.Concurrent.ConcurrentBag<Guid> CreatedTenants = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        var tenantIds = CreatedTenants.ToArray();
        if (tenantIds.Length == 0) return;
        await using var db = fixture.CreateContext();
        await db.PanelNotifications.Where(x => tenantIds.Contains(x.TenantId)).ExecuteDeleteAsync();
    }

    [PostgreSqlFact]
    public async Task A_sent_notification_is_kept_and_a_repeat_updates_the_same_row()
    {
        var tenant = await SeedTenant();
        var user = Guid.NewGuid();

        await Save(tenant, user, Input("bell-1", "Sipariş paketlendi."));
        await Save(tenant, user, Input("bell-1", "3 sipariş paketlendi.") with { Count = 3, Payload = "{\"id\":\"bell-1\"}" });

        var page = await Page(tenant, user);
        var row = Assert.Single(page.Items);
        Assert.Equal(("bell-1", "3 sipariş paketlendi.", 3, "ORDER", "SUCCESS"), (row.ClientKey, row.Message, row.Count, row.Category, row.Severity));
        Assert.Equal("{\"id\":\"bell-1\"}", row.Payload);
        Assert.Null(row.ReadAt);
        Assert.False(row.Shared);
    }

    [PostgreSqlFact]
    public async Task Dismissing_from_the_bell_keeps_the_row_in_the_center()
    {
        var tenant = await SeedTenant();
        var user = Guid.NewGuid();
        await Save(tenant, user, Input("bell-a", "Ürün gönderilemedi.") with { Severity = "ERROR", Category = "PRODUCT" }, Input("bell-b", "Fatura kesildi.") with { Category = "INVOICE" });

        await using (var db = fixture.CreateContext())
            Assert.Equal(1, await Service(db).DismissAsync(tenant, user, new(ClientKeys: ["bell-a"]), CancellationToken.None));
        await using (var db = fixture.CreateContext())
            Assert.Equal(2, await Service(db).MarkReadAsync(tenant, user, new(All: true), CancellationToken.None));

        var page = await Page(tenant, user);
        Assert.Equal(2, page.Items.Count);
        Assert.NotNull(page.Items.Single(x => x.ClientKey == "bell-a").DismissedAt);
        Assert.Null(page.Items.Single(x => x.ClientKey == "bell-b").DismissedAt);
        Assert.All(page.Items, item => Assert.NotNull(item.ReadAt));
        var errors = await Page(tenant, user, new(Severities: ["ERROR"]));
        Assert.Equal("bell-a", Assert.Single(errors.Items).ClientKey);
        var search = await Page(tenant, user, new(Search: "fatura"));
        Assert.Equal("bell-b", Assert.Single(search.Items).ClientKey);
        await using var check = fixture.CreateContext();
        Assert.Equal(new PanelNotificationSummary(2, 0, 1), await Service(check).SummaryAsync(tenant, user, CancellationToken.None));
    }

    [PostgreSqlFact]
    public async Task The_retention_removes_rows_older_than_a_week_only()
    {
        var tenant = await SeedTenant();
        var user = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await using (var db = fixture.CreateContext())
        {
            db.PanelNotifications.Add(Row(tenant, user, "old", now.AddDays(-8)));
            db.PanelNotifications.Add(Row(tenant, user, "recent", now.AddDays(-6)));
            await db.SaveChangesAsync();
        }
        // A notification older than the week is not kept when the browser moves its history over.
        await Save(tenant, user, Input("moved-old", "Eski bildirim.") with { CreatedAt = now.AddDays(-9) });

        Assert.Equal(["recent"], (await Page(tenant, user)).Items.Select(x => x.ClientKey).ToArray());
        await using (var db = fixture.CreateContext())
            Assert.True(await new JobRetentionService(db, fixture.TimeProvider).PurgePanelNotificationsAsync(PanelNotificationPolicy.Retention, 5, CancellationToken.None) >= 1);

        await using var check = fixture.CreateContext();
        Assert.Equal(["recent"], await check.PanelNotifications.Where(x => x.TenantId == tenant).Select(x => x.ClientKey).ToListAsync());
    }

    [PostgreSqlFact]
    public async Task A_tenant_and_a_user_see_only_their_own_and_the_shared_notifications()
    {
        var tenant = await SeedTenant();
        var other = await SeedTenant();
        var user = Guid.NewGuid();
        var colleague = Guid.NewGuid();
        await Save(tenant, user, Input("mine", "Benim bildirimim."), Input("attention-1", "Okuma art arda başarısız.") with { Shared = true, Category = "ATTENTION", Severity = "ERROR" });
        await Save(tenant, colleague, Input("theirs", "Başkasının bildirimi."));
        await Save(other, user, Input("other-tenant", "Başka kiracı."));
        // Another user cannot overwrite a row by sending its key.
        await Save(tenant, colleague, Input("mine", "Üzerine yazma denemesi."));

        Assert.Equal(["attention-1", "mine"], (await Page(tenant, user)).Items.Select(x => x.ClientKey).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(["attention-1", "theirs"], (await Page(tenant, colleague)).Items.Select(x => x.ClientKey).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(["other-tenant"], (await Page(other, user)).Items.Select(x => x.ClientKey).ToArray());
        Assert.Equal("Benim bildirimim.", (await Page(tenant, user)).Items.Single(x => x.ClientKey == "mine").Message);

        await using (var db = fixture.CreateContext())
            Assert.Equal(0, await Service(db).DismissAsync(other, user, new(ClientKeys: ["mine"]), CancellationToken.None));
        await using (var db = fixture.CreateContext())
            Assert.Equal(0, await Service(db).MarkReadAsync(tenant, colleague, new(ClientKeys: ["mine"]), CancellationToken.None));
        Assert.Null((await Page(tenant, user)).Items.Single(x => x.ClientKey == "mine").ReadAt);
    }

    private static PanelNotificationInput Input(string key, string message) => new(key, "SUCCESS", "ORDER", null, message);

    private static PanelNotification Row(Guid tenant, Guid user, string key, DateTimeOffset createdAt) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = tenant,
        UserId = user,
        ClientKey = key,
        Severity = "INFO",
        Category = "OTHER",
        Title = key,
        Message = key,
        CreatedAt = createdAt.ToUniversalTime(),
        UpdatedAt = createdAt.ToUniversalTime()
    };

    private PanelNotificationService Service(AppDbContext db) => new(db, TimeProvider.System);

    private async Task Save(Guid tenant, Guid user, params PanelNotificationInput[] items)
    {
        await using var db = fixture.CreateContext();
        var result = await Service(db).SaveAsync(tenant, user, items, CancellationToken.None);
        Assert.True(result.Succeeded);
    }

    private async Task<PageResult<PanelNotificationView>> Page(Guid tenant, Guid user, PanelNotificationQuery? query = null)
    {
        await using var db = fixture.CreateContext();
        var result = await Service(db).PageAsync(tenant, user, query ?? new(), CancellationToken.None);
        Assert.True(result.Succeeded);
        return result.Value!;
    }

    private async Task<Guid> SeedTenant()
    {
        var now = DateTimeOffset.UtcNow;
        var tenant = new Tenant { Id = Guid.CreateVersion7(), Code = $"notify-{Guid.NewGuid():N}", DisplayName = "notify", Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = now, UpdatedAt = now, Version = 1 };
        CreatedTenants.Add(tenant.Id);
        await using var db = fixture.CreateContext();
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();
        return tenant.Id;
    }
}
