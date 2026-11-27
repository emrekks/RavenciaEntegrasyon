using System.Text.Json;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// "Siparişlerde bildirim olarak gelsin" (owner, 27 Nov 2026): a run that stores new orders leaves one shared "Yeni Sipariş"
// notification per store in the notification center, which the bell pulls (GET /notifications/new-orders). Invented
// order numbers only.
[Collection("postgres-integration")]
public sealed class NewOrderNoticePostgreSqlTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    [PostgreSqlFact]
    public async Task ARunWithNewOrdersLeavesOneNotificationPerStore()
    {
        var (tenant, first) = await SeedAsync("PAZARAMA");
        var second = await AddConnectionAsync(tenant.Id, "PAZARAMA", "İkinci Mağaza");
        await AddOlderOrderAsync(tenant.Id, first.Id);
        await AddOlderOrderAsync(tenant.Id, second.Id);
        var now = DateTimeOffset.UtcNow;

        await using (var db = fixture.CreateContext())
        {
            var processor = Processor(db);
            processor.BeginNewOrderNotices(bootstrapJob: false, "{}");
            await processor.UpsertOrders(tenant.Id, first.Id, [Remote("NTC-A-0001", now.AddMinutes(-5)), Remote("NTC-A-0002", now.AddMinutes(-4)), Remote("NTC-A-0003", now.AddMinutes(-3))], CancellationToken.None);
            await processor.UpsertOrders(tenant.Id, second.Id, [Remote("NTC-B-0001", now.AddMinutes(-2))], CancellationToken.None);
            await processor.RecordNewOrderNoticesAsync(tenant.Id);
        }

        await using var check = fixture.CreateContext();
        var rows = await check.PanelNotifications.AsNoTracking().Where(x => x.TenantId == tenant.Id).ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.All(rows, row =>
        {
            Assert.Null(row.UserId);
            Assert.StartsWith(NewOrderNoticePolicy.KeyPrefix, row.ClientKey);
            Assert.Equal(("ORDER", "INFO", NewOrderNoticePolicy.Title, 1), (row.Category, row.Severity, row.Title, row.Count));
            Assert.Null(row.ReadAt);
        });
        var many = rows.Single(x => x.ClientKey.Contains(first.Id.ToString("N"), StringComparison.Ordinal));
        Assert.Equal(("Pazarama: 3 yeni paket", NewOrderNoticePolicy.NewTabHref, "PAZARAMA", "PAZARAMA"), (many.Message, many.Href, many.PlatformCode, many.StoreName));
        using (var card = JsonDocument.Parse(many.Payload!))
        {
            Assert.Equal(many.ClientKey, card.RootElement.GetProperty("id").GetString());
            Assert.Equal(3, card.RootElement.GetProperty("items").GetArrayLength());
            Assert.False(card.RootElement.TryGetProperty("subject", out _));
        }
        var single = rows.Single(x => x.ClientKey.Contains(second.Id.ToString("N"), StringComparison.Ordinal));
        Assert.Equal(("Pazarama: 1 yeni paket, sipariş NTC-B-0001", "/orders?search=NTC-B-0001", "İkinci Mağaza"), (single.Message, single.Href, single.StoreName));
    }

    [PostgreSqlFact]
    public async Task AnOrderReadAgainRaisesNoSecondNotification()
    {
        var (tenant, connection) = await SeedAsync("PAZARAMA");
        await AddOlderOrderAsync(tenant.Id, connection.Id);
        var now = DateTimeOffset.UtcNow;
        await using (var db = fixture.CreateContext())
        {
            var processor = Processor(db);
            processor.BeginNewOrderNotices(false, "{}");
            await processor.UpsertOrders(tenant.Id, connection.Id, [Remote("NTC-R-0001", now.AddMinutes(-10))], CancellationToken.None);
            await processor.RecordNewOrderNoticesAsync(tenant.Id);
        }
        // The next run reads the same order with a newer change (a status update): it is not new any more.
        await using (var db = fixture.CreateContext())
        {
            var processor = Processor(db);
            processor.BeginNewOrderNotices(false, "{}");
            await processor.UpsertOrders(tenant.Id, connection.Id, [Remote("NTC-R-0001", now.AddMinutes(-10), modifiedAt: now.AddMinutes(-1), status: "Picking")], CancellationToken.None);
            await processor.RecordNewOrderNoticesAsync(tenant.Id);
        }
        await using var check = fixture.CreateContext();
        Assert.Equal(1, await check.PanelNotifications.CountAsync(x => x.TenantId == tenant.Id));
    }

    [PostgreSqlFact]
    public async Task FirstImportsFullReadsAndOldOrdersRaiseNothing()
    {
        var now = DateTimeOffset.UtcNow;
        // A store's first read (no order before the run), as after a new connection or a reset.
        var (fresh, freshConnection) = await SeedAsync("PAZARAMA");
        await RunAsync(fresh.Id, freshConnection.Id, false, "{}", Remote("NTC-F-0001", now.AddMinutes(-5)));
        // The activation's first import, and a read asked to read in full.
        var (boot, bootConnection) = await SeedAsync("PAZARAMA");
        await AddOlderOrderAsync(boot.Id, bootConnection.Id);
        await RunAsync(boot.Id, bootConnection.Id, true, "{}", Remote("NTC-F-0002", now.AddMinutes(-5)));
        await RunAsync(boot.Id, bootConnection.Id, false, JsonSerializer.Serialize(new { connectionId = bootConnection.Id, full = true }), Remote("NTC-F-0003", now.AddMinutes(-5)));
        // An order first seen more than a day after it was placed.
        await RunAsync(boot.Id, bootConnection.Id, false, "{}", Remote("NTC-F-0004", now.AddHours(-30)));

        await using var check = fixture.CreateContext();
        Assert.False(await check.PanelNotifications.AnyAsync(x => x.TenantId == fresh.Id || x.TenantId == boot.Id));
        // The orders themselves were stored all the same.
        Assert.Equal(1, await check.Orders.CountAsync(x => x.TenantId == fresh.Id));
        Assert.Equal(4, await check.Orders.CountAsync(x => x.TenantId == boot.Id));
        Assert.True(NewOrderNoticePolicy.IsEligible(now.AddHours(-23), "NEW", now));
        Assert.False(NewOrderNoticePolicy.IsEligible(now.AddMinutes(-1), "CANCELLED", now));
    }

    [PostgreSqlFact]
    public async Task TheBellListsOnlyItsOwnTenantsSharedNewOrderNotifications()
    {
        var (tenantA, connectionA) = await SeedAsync("PAZARAMA");
        var (tenantB, connectionB) = await SeedAsync("PAZARAMA");
        await AddOlderOrderAsync(tenantA.Id, connectionA.Id);
        await AddOlderOrderAsync(tenantB.Id, connectionB.Id);
        var now = DateTimeOffset.UtcNow;
        await RunAsync(tenantA.Id, connectionA.Id, false, "{}", Remote("NTC-T-0001", now.AddMinutes(-3)));
        await RunAsync(tenantB.Id, connectionB.Id, false, "{}", Remote("NTC-T-0002", now.AddMinutes(-3)));
        var user = Guid.CreateVersion7();
        await using (var db = fixture.CreateContext())
        {
            // A person's own order notification is not a new-order notice, and a browser cannot write the server's key.
            await new PanelNotificationService(db, fixture.TimeProvider).SaveAsync(tenantA.Id, user,
                [new PanelNotificationInput("user-order-1", "SUCCESS", "ORDER", "Sipariş Onaylandı", "Onaylandı.")], CancellationToken.None);
            Assert.True(PanelNotificationPolicy.IsServerKey($"{NewOrderNoticePolicy.KeyPrefix}x"));
        }
        await using var check = fixture.CreateContext();
        var service = new PanelNotificationService(check, fixture.TimeProvider);
        var listedA = await service.NewOrdersAsync(tenantA.Id, user, CancellationToken.None);
        var listedB = await service.NewOrdersAsync(tenantB.Id, user, CancellationToken.None);
        Assert.Equal("Pazarama: 1 yeni paket, sipariş NTC-T-0001", Assert.Single(listedA).Message);
        Assert.True(Assert.Single(listedA).Shared);
        Assert.Equal("Pazarama: 1 yeni paket, sipariş NTC-T-0002", Assert.Single(listedB).Message);
        // Read in one browser: read for the tenant (the bell marks it by its key).
        Assert.Equal(1, await service.MarkReadAsync(tenantA.Id, user, new PanelNotificationSelection(ClientKeys: [listedA[0].ClientKey]), CancellationToken.None));
        Assert.NotNull(Assert.Single(await service.NewOrdersAsync(tenantA.Id, Guid.CreateVersion7(), CancellationToken.None)).ReadAt);
        Assert.Null(Assert.Single(await service.NewOrdersAsync(tenantB.Id, user, CancellationToken.None)).ReadAt);
    }

    [PostgreSqlFact]
    public async Task AFailedNotificationWriteNeverFailsTheOrderImport()
    {
        var (tenant, connection) = await SeedAsync("PAZARAMA");
        await AddOlderOrderAsync(tenant.Id, connection.Id);
        var suffix = connection.Id.ToString("N");
        // The notification center refuses this store's notice (as a full disk or a lock timeout would).
        await using (var db = fixture.CreateContext())
        {
            // The suffix is a Guid's hex digits, nothing a caller typed.
            var create = $"""
                CREATE FUNCTION ops.refuse_notice_{suffix}() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN IF NEW."ClientKey" LIKE 'new-orders:{suffix}%' THEN RAISE EXCEPTION 'refused for the test'; END IF; RETURN NEW; END $$;
                CREATE TRIGGER refuse_notice_{suffix} BEFORE INSERT ON ops.panel_notifications FOR EACH ROW EXECUTE FUNCTION ops.refuse_notice_{suffix}();
                """;
            await db.Database.ExecuteSqlRawAsync(create);
        }
        try
        {
            var now = DateTimeOffset.UtcNow;
            // Outside a transaction: the run ends normally.
            await RunAsync(tenant.Id, connection.Id, false, "{}", Remote("NTC-X-0001", now.AddMinutes(-3)));
            // Inside one: the savepoint keeps the transaction usable, so the run's later work commits.
            await using (var db = fixture.CreateContext())
            {
                await using var transaction = await db.Database.BeginTransactionAsync();
                var processor = Processor(db);
                processor.BeginNewOrderNotices(false, "{}");
                await processor.UpsertOrders(tenant.Id, connection.Id, [Remote("NTC-X-0002", now.AddMinutes(-2))], CancellationToken.None);
                await processor.RecordNewOrderNoticesAsync(tenant.Id);
                await processor.UpsertOrders(tenant.Id, connection.Id, [Remote("NTC-X-0003", now.AddMinutes(-1))], CancellationToken.None);
                await transaction.CommitAsync();
            }
            await using var check = fixture.CreateContext();
            Assert.Equal(4, await check.Orders.CountAsync(x => x.TenantId == tenant.Id));
            Assert.False(await check.PanelNotifications.AnyAsync(x => x.TenantId == tenant.Id));
        }
        finally
        {
            await using var db = fixture.CreateContext();
            var drop = $"DROP TRIGGER IF EXISTS refuse_notice_{suffix} ON ops.panel_notifications; DROP FUNCTION IF EXISTS ops.refuse_notice_{suffix}();";
            await db.Database.ExecuteSqlRawAsync(drop);
        }
    }

    // The job processor writes the notice at the end of a run by itself: a PttAVM order read by number stores a new order.
    [PostgreSqlFact]
    public async Task AJobRunWritesItsNoticeWhenItEnds()
    {
        var (tenant, connection) = await SeedAsync("PTTAVM");
        await AddOlderOrderAsync(tenant.Id, connection.Id);
        var order = Remote("PTT-NTC0001-271126", DateTimeOffset.UtcNow.AddMinutes(-4));
        await using (var db = fixture.CreateContext())
        {
            var processor = new MarketplaceJobProcessor(db, null!, null!, null!, null!, new SingleOrderPort(order), null!, null!, null!, null!, null!, new ConfigurationBuilder().Build(), fixture.TimeProvider);
            var result = await processor.ProcessAsync(tenant.Id, connection.Id, MarketplaceJobTypes.PttAvmOrderSync,
                JsonSerializer.Serialize(new { connectionId = connection.Id, externalOrderId = order.ExternalOrderId }), "new-order-notice", CancellationToken.None);
            Assert.True(result.Succeeded, result.ErrorSummary);
        }
        await using var check = fixture.CreateContext();
        var row = await check.PanelNotifications.AsNoTracking().SingleAsync(x => x.TenantId == tenant.Id);
        Assert.Equal(("PttAVM: 1 yeni paket, sipariş PTT-NTC0001-271126", "/orders?search=PTT-NTC0001-271126"), (row.Message, row.Href));
    }

    private sealed class SingleOrderPort(RemoteOrder order) : IOrderPort
    {
        public Task<AdapterResult<AdapterPageResult<RemoteOrder>>> PollAsync(AdapterContext context, OrderPollWindow window, AdapterPageRequest page, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<RemoteOrder>> GetAsync(AdapterContext context, string externalOrderId, CancellationToken cancellationToken) => Task.FromResult(AdapterResult<RemoteOrder>.Success(order, null));
        public Task<AdapterResult<PackageActionResult>> ExecutePackageActionAsync(AdapterContext context, PackageActionCommand command, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<IReadOnlyList<RemoteCargoCompany>>> GetChangeableCargoCompaniesAsync(AdapterContext context, string externalPackageId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<IReadOnlyList<RemotePackageableLine>>> GetPackageableLineItemsAsync(AdapterContext context, string externalLineItemId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<CreateOrderPackageResult>> CreateOrderPackageAsync(AdapterContext context, CreateOrderPackageCommand command, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<bool>> CreateCommonLabelAsync(AdapterContext context, CommonLabelRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<CommonLabelDocument>> GetCommonLabelAsync(AdapterContext context, string cargoTrackingNumber, CancellationToken cancellationToken, string format = "ZPL") => throw new NotSupportedException();
        public Task<AdapterResult<StageTestOrderResult>> CreateStageTestOrderAsync(AdapterContext context, string barcode, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private async Task RunAsync(Guid tenantId, Guid connectionId, bool bootstrap, string payload, params RemoteOrder[] orders)
    {
        await using var db = fixture.CreateContext();
        var processor = Processor(db);
        processor.BeginNewOrderNotices(bootstrap, payload);
        await processor.UpsertOrders(tenantId, connectionId, orders, CancellationToken.None);
        await processor.RecordNewOrderNoticesAsync(tenantId);
    }

    private MarketplaceJobProcessor Processor(AppDbContext db) =>
        new(db, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, new ConfigurationBuilder().Build(), fixture.TimeProvider);

    // An invented one-line, one-package order in the package statuses Pazarama's mapper uses.
    private static RemoteOrder Remote(string number, DateTimeOffset orderedAt, DateTimeOffset? modifiedAt = null, string status = "Created")
    {
        var modified = modifiedAt ?? orderedAt;
        return new RemoteOrder(number, number, orderedAt, modified, "TRY", 100m, 0m, 100m, "{}", "{}", "{}",
            [new RemoteOrderLine($"{number}-1", "SKU-NOTICE", null, "Deneme Ürünü", 1m, 100m, 20m, status)],
            [new RemotePackage($"pkg-{number}", null, status, modified, null, null, [new RemotePackageAllocation($"{number}-1", 1m, 0m, 0m, 0m, 0m)], 100m, 0m, 100m)],
            "{}");
    }

    private async Task AddOlderOrderAsync(Guid tenantId, Guid connectionId)
    {
        var then = DateTimeOffset.UtcNow.AddDays(-3);
        await using var db = fixture.CreateContext();
        db.Orders.Add(new Order { Id = Guid.CreateVersion7(), TenantId = tenantId, ConnectionId = connectionId, ExternalOrderId = $"old-{Guid.NewGuid():N}", OrderNumber = "OLD-0001", Currency = "TRY", OrderedAt = then, LastRemoteModifiedAt = then, CustomerSnapshotJson = "{}", ShipmentAddressSnapshotJson = "{}", InvoiceAddressSnapshotJson = "{}", DerivedStatus = "DELIVERED", CreatedAt = then, UpdatedAt = then, Version = 1 });
        await db.SaveChangesAsync();
    }

    private async Task<(Tenant Tenant, PlatformConnection Connection)> SeedAsync(string platformCode)
    {
        var now = fixture.Now;
        var tenant = new Tenant { Id = Guid.CreateVersion7(), Code = $"ntc-{Guid.NewGuid():N}", DisplayName = "new-order-notice", Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = now, UpdatedAt = now, Version = 1 };
        await using (var db = fixture.CreateContext())
        {
            db.Tenants.Add(tenant);
            await db.SaveChangesAsync();
        }
        return (tenant, await AddConnectionAsync(tenant.Id, platformCode, platformCode));
    }

    private async Task<PlatformConnection> AddConnectionAsync(Guid tenantId, string platformCode, string displayName)
    {
        var connection = new PlatformConnection { Id = Guid.CreateVersion7(), TenantId = tenantId, PublicId = Guid.CreateVersion7(), PlatformCode = platformCode, Environment = "PRODUCTION", DisplayName = displayName, ExternalStoreId = $"store-{Guid.NewGuid():N}", Status = "ACTIVE", ApiVersion = "V1", SettingsJson = "{}" };
        await using var db = fixture.CreateContext();
        db.PlatformConnections.Add(connection);
        await db.SaveChangesAsync();
        return connection;
    }
}
