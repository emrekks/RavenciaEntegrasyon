using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// Owner, 23 Nov 2026 ("Platform seçtirsin ve sipariş no yazdırsın"): the Yeniden Gönderim gear keeps records. A record names
// an order the panel holds for that connection; the order is listed under Yeniden Gönderimler ("Elle Eklendi") and counted
// by the same rule as the list until the owner marks it handed to the carrier. An unknown number stores nothing, the same
// order is refused twice, a record can be removed, and nothing reaches a marketplace.
[Collection("postgres-integration")]
public sealed class ManualResendPostgreSqlTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    [PostgreSqlFact]
    public async Task A_hand_added_order_is_listed_and_counted_until_marked_shipped_and_leaves_when_removed()
    {
        var (tenant, connection, other) = await SeedAsync();
        var order = await SeedOrderAsync(tenant.Id, connection.Id, "1000000101");
        var untouched = await SeedOrderAsync(tenant.Id, connection.Id, "1000000102");
        var user = Guid.NewGuid();

        var before = await ReadAsync(tenant.Id);
        Assert.Equal(0, before.Count);
        Assert.Empty(before.Resent);

        ManualResendView added;
        await using (var db = fixture.CreateContext())
        {
            // Typed as the panel shows it (with #): the same order.
            var result = await new ManualResendService(db, fixture.TimeProvider).AddAsync(tenant.Id, user, new ManualResendCreateCommand(connection.Id, " #1000000101 "), "corr-add", CancellationToken.None);
            Assert.True(result.Succeeded, result.Error?.Message);
            added = result.Value!;
            Assert.Equal((order, "1000000101", connection.Id, "TRENDYOL", (DateTimeOffset?)null), (added.OrderId, added.OrderNumber, added.ConnectionId, added.PlatformCode, added.ShippedAt));
            Assert.Equal(1, await db.AuditLogs.CountAsync(x => x.TenantId == tenant.Id && x.Action == "MANUAL_RESEND_ADDED" && x.TargetId == added.Id.ToString("D")));
            // Nothing else is touched: no job, no stock, no invoice.
            Assert.Equal(0, await db.IntegrationJobs.CountAsync(x => x.TenantId == tenant.Id));
            Assert.Equal(0, await db.Invoices.CountAsync(x => x.TenantId == tenant.Id));
        }

        var listed = await ReadAsync(tenant.Id);
        Assert.Equal(1, listed.Count);
        Assert.Equal([order], listed.Unshipped.Select(x => x.Id));
        var row = Assert.Single(listed.Resent);
        Assert.Equal((added.Id, false), (row.ManualResend!.Id, row.ManualResend.Shipped));
        Assert.DoesNotContain(listed.Resent, x => x.Id == untouched);
        Assert.Equal(1, listed.Stores[connection.Id.ToString("D")]);

        // The same order again (as typed or by its number), an unknown number and another connection's store are refused.
        await using (var db = fixture.CreateContext())
        {
            var service = new ManualResendService(db, fixture.TimeProvider);
            var twice = await service.AddAsync(tenant.Id, user, new ManualResendCreateCommand(connection.Id, "1000000101"), "corr-twice", CancellationToken.None);
            Assert.Equal((409, ManualResendService.DuplicateCode), (twice.Error!.Status, twice.Error.Code));
            var unknown = await service.AddAsync(tenant.Id, user, new ManualResendCreateCommand(connection.Id, "1000009999"), "corr-unknown", CancellationToken.None);
            Assert.Equal((404, ManualResendService.OrderNotFoundCode), (unknown.Error!.Status, unknown.Error.Code));
            var wrongStore = await service.AddAsync(tenant.Id, user, new ManualResendCreateCommand(other.Id, "1000000102"), "corr-other", CancellationToken.None);
            Assert.Equal((404, ManualResendService.OrderNotFoundCode), (wrongStore.Error!.Status, wrongStore.Error.Code));
            var empty = await service.AddAsync(tenant.Id, user, new ManualResendCreateCommand(connection.Id, " # "), "corr-empty", CancellationToken.None);
            Assert.Equal(422, empty.Error!.Status);
            var foreign = await service.AddAsync(Guid.NewGuid(), user, new ManualResendCreateCommand(connection.Id, "1000000102"), "corr-foreign", CancellationToken.None);
            Assert.Equal((422, ManualResendService.ConnectionInvalidCode), (foreign.Error!.Status, foreign.Error.Code));
            Assert.Equal(1, await db.ManualResends.CountAsync(x => x.TenantId == tenant.Id));
        }

        // Kargoya Verildi: out of the count and the waiting list, still under Yeniden Gönderimler.
        await using (var db = fixture.CreateContext())
        {
            var service = new ManualResendService(db, fixture.TimeProvider);
            var marked = await service.MarkShippedAsync(tenant.Id, user, added.Id, added.Version, true, "corr-ship", CancellationToken.None);
            Assert.True(marked.Succeeded, marked.Error?.Message);
            Assert.Equal(409, (await service.MarkShippedAsync(tenant.Id, user, added.Id, marked.Value!.Version, true, "corr-again", CancellationToken.None)).Error!.Status);
            Assert.Equal(412, (await service.MarkShippedAsync(tenant.Id, user, added.Id, added.Version, false, "corr-stale", CancellationToken.None)).Error!.Status);
            Assert.Equal(404, (await service.MarkShippedAsync(Guid.NewGuid(), user, added.Id, marked.Value.Version, false, "corr-foreign", CancellationToken.None)).Error!.Status);
        }
        var shipped = await ReadAsync(tenant.Id);
        Assert.Equal(0, shipped.Count);
        Assert.Empty(shipped.Unshipped);
        Assert.True(Assert.Single(shipped.Resent).ManualResend!.Shipped);

        // Another tenant cannot remove it; the owner can, and the order leaves the tab.
        await using (var db = fixture.CreateContext())
        {
            var service = new ManualResendService(db, fixture.TimeProvider);
            Assert.Equal(404, (await service.RemoveAsync(Guid.NewGuid(), user, added.Id, "corr-foreign", CancellationToken.None)).Error!.Status);
            Assert.True((await service.RemoveAsync(tenant.Id, user, added.Id, "corr-remove", CancellationToken.None)).Succeeded);
            Assert.Equal(1, await db.AuditLogs.CountAsync(x => x.TenantId == tenant.Id && x.Action == "MANUAL_RESEND_REMOVED"));
            Assert.Empty((await service.ListAsync(tenant.Id, CancellationToken.None)).Items);
        }
        var removed = await ReadAsync(tenant.Id);
        Assert.Empty(removed.Resent);
        Assert.Equal(0, removed.Count);
    }

    [PostgreSqlFact]
    public async Task Deleting_the_order_takes_its_record()
    {
        var (tenant, connection, _) = await SeedAsync();
        var order = await SeedOrderAsync(tenant.Id, connection.Id, "1000000201");
        await using (var db = fixture.CreateContext())
            Assert.True((await new ManualResendService(db, fixture.TimeProvider).AddAsync(tenant.Id, Guid.NewGuid(), new ManualResendCreateCommand(connection.Id, "1000000201"), "corr", CancellationToken.None)).Succeeded);
        await using (var db = fixture.CreateContext())
        {
            await db.OrderLines.Where(x => x.TenantId == tenant.Id && x.OrderId == order).ExecuteDeleteAsync();
            await db.Orders.Where(x => x.TenantId == tenant.Id && x.Id == order).ExecuteDeleteAsync();
            Assert.Equal(0, await db.ManualResends.CountAsync(x => x.TenantId == tenant.Id));
        }
    }

    [Theory]
    [InlineData(" 1000000301 ", "1000000301")]
    [InlineData("#1000000301", "1000000301")]
    [InlineData(" # ", null)]
    [InlineData(null, null)]
    public void The_typed_number_is_trimmed_and_its_hash_dropped(string? typed, string? expected) =>
        Assert.Equal(expected, ManualResendService.NormalizeOrderNumber(typed));

    private async Task<(int Count, IReadOnlyList<OrderListView> Resent, IReadOnlyList<OrderListView> Unshipped, IReadOnlyDictionary<string, int> Stores)> ReadAsync(Guid tenantId)
    {
        await using var db = fixture.CreateContext();
        var service = new MarketplaceSalesService(db, new CursorCodec(new EphemeralDataProtectionProvider(), fixture.TimeProvider), new ConfigurationBuilder().Build(), null!, null!, null!, null!, TimeProvider.System);
        var resent = await service.OrdersAsync(tenantId, 50, 1, null, new OrderListQuery(Status: "RESENT"), CancellationToken.None);
        var unshipped = await service.OrdersAsync(tenantId, 50, 1, null, new OrderListQuery(Status: "RESENT", ResentNotShipped: true), CancellationToken.None);
        // The card: the summary over the not-shipped resends, cached like the live one.
        var summary = await service.OrderSummaryAsync(tenantId, new OrderListQuery(ResentNotShipped: true), CancellationToken.None, ["RESENT"], allowCached: true);
        Assert.Equal(unshipped.Items.Count, summary.Resent);
        return (summary.Resent, resent.Items, unshipped.Items, summary.ResentByConnection ?? new Dictionary<string, int>());
    }

    private async Task<Guid> SeedOrderAsync(Guid tenantId, Guid connectionId, string number)
    {
        var order = new Order { Id = Guid.CreateVersion7(), TenantId = tenantId, ConnectionId = connectionId, ExternalOrderId = number, OrderNumber = number, Currency = "TRY", OrderedAt = fixture.Now.AddDays(-5), LastRemoteModifiedAt = fixture.Now.AddDays(-5), CustomerSnapshotJson = "{}", ShipmentAddressSnapshotJson = "{}", InvoiceAddressSnapshotJson = "{}", DerivedStatus = "DELIVERED", CreatedAt = fixture.Now, UpdatedAt = fixture.Now, Version = 1 };
        await using var db = fixture.CreateContext();
        db.Orders.Add(order);
        db.OrderLines.Add(new OrderLine { Id = Guid.CreateVersion7(), TenantId = tenantId, OrderId = order.Id, ExternalLineId = $"{number}-1", Sku = "SKU", TitleSnapshot = "Ürün", RawStatus = "Delivered", OrderedQuantity = 1, ShippedQuantity = 1, DeliveredQuantity = 1, UnitPrice = 100, VatRate = 10, Version = 1 });
        await db.SaveChangesAsync();
        return order.Id;
    }

    private async Task<(Tenant Tenant, PlatformConnection Connection, PlatformConnection Other)> SeedAsync()
    {
        var now = fixture.Now;
        var tenant = new Tenant { Id = Guid.CreateVersion7(), Code = $"mrs-{Guid.NewGuid():N}", DisplayName = "mrs", Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = now, UpdatedAt = now, Version = 1 };
        var connection = new PlatformConnection { Id = Guid.CreateVersion7(), TenantId = tenant.Id, PublicId = Guid.CreateVersion7(), PlatformCode = "TRENDYOL", Environment = "PRODUCTION", DisplayName = "Trendyol", ExternalStoreId = $"store-{Guid.NewGuid():N}", Status = "ACTIVE", ApiVersion = "V1", SettingsJson = "{}" };
        var other = new PlatformConnection { Id = Guid.CreateVersion7(), TenantId = tenant.Id, PublicId = Guid.CreateVersion7(), PlatformCode = "HEPSIBURADA", Environment = "PRODUCTION", DisplayName = "Hepsiburada", ExternalStoreId = $"store-{Guid.NewGuid():N}", Status = "ACTIVE", ApiVersion = "V1", SettingsJson = "{}" };
        await using var db = fixture.CreateContext();
        db.Tenants.Add(tenant);
        db.PlatformConnections.AddRange(connection, other);
        await db.SaveChangesAsync();
        return (tenant, connection, other);
    }
}
