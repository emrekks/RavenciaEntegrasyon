using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// SIPARIS-STOK-4: a label print sets LabelPrintedAt only, so the cached tab counters must see it at once: under
// "Etiket Durumu: Basılmadı" the counter falls with the list instead of waiting up to five minutes.
[Collection("postgres-integration")]
public sealed class OrderSummaryCachePostgreSqlTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    [PostgreSqlFact]
    public async Task ACachedCounter_FollowsALabelPrint()
    {
        var now = DateTimeOffset.UtcNow;
        var tenant = new Tenant { Id = Guid.CreateVersion7(), Code = $"sc-{Guid.NewGuid():N}"[..16], DisplayName = "sc", Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = now, UpdatedAt = now, Version = 1 };
        var connection = new PlatformConnection { Id = Guid.CreateVersion7(), TenantId = tenant.Id, PublicId = Guid.CreateVersion7(), PlatformCode = "TRENDYOL", Environment = "STAGE", DisplayName = "sc", ExternalStoreId = $"s-{Guid.NewGuid():N}"[..12], Status = "ACTIVE", ApiVersion = "V1" };
        var orders = Enumerable.Range(0, 2).Select(index => new Order
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenant.Id,
            ConnectionId = connection.Id,
            ExternalOrderId = $"sc-{index}",
            OrderNumber = $"SC{index}",
            Currency = "TRY",
            OrderedAt = now.AddMinutes(-index),
            LastRemoteModifiedAt = now,
            CustomerSnapshotJson = "{}",
            ShipmentAddressSnapshotJson = "{}",
            InvoiceAddressSnapshotJson = "{}",
            DerivedStatus = "NEW",
            CreatedAt = now,
            UpdatedAt = now,
            Version = 1
        }).ToList();
        try
        {
            await using (var db = fixture.CreateContext())
            {
                db.Tenants.Add(tenant);
                db.PlatformConnections.Add(connection);
                db.Orders.AddRange(orders);
                await db.SaveChangesAsync();
            }
            var notPrinted = new OrderListQuery(LabelPrinted: false);
            await using (var db = fixture.CreateContext())
                Assert.Equal(2, (await Service(db).OrderSummaryAsync(tenant.Id, notPrinted, CancellationToken.None, ["ALL"], allowCached: true)).All);
            await using (var db = fixture.CreateContext())
                Assert.True(await Service(db).MarkLabelPrintedAsync(tenant.Id, orders[0].Id, CancellationToken.None));
            await using (var db = fixture.CreateContext())
            {
                var service = Service(db);
                var list = await service.OrdersAsync(tenant.Id, 100, 1, null, notPrinted, CancellationToken.None);
                var counter = await service.OrderSummaryAsync(tenant.Id, notPrinted, CancellationToken.None, ["ALL"], allowCached: true);
                Assert.Equal(1, list.TotalCount);
                Assert.Equal(list.TotalCount, counter.All);
            }
        }
        finally
        {
            await using var db = fixture.CreateContext();
            await db.IntegrationOutboxEvents.Where(row => row.TenantId == tenant.Id).ExecuteDeleteAsync();
            await db.Orders.Where(row => row.TenantId == tenant.Id).ExecuteDeleteAsync();
            await db.PlatformConnections.Where(row => row.TenantId == tenant.Id).ExecuteDeleteAsync();
            await db.Tenants.Where(row => row.Id == tenant.Id).ExecuteDeleteAsync();
        }
    }

    // SIPARIS-STOK-4: an order passes its ship-by time without any change the stamp sees; the Geciken counter is
    // counted fresh on every call while the other tabs keep their cached answer.
    [PostgreSqlFact]
    public async Task TheLateCounter_FollowsTheClock_WhileTheOtherTabsStayCached()
    {
        // A minute past the hour: the clock moves 20 minutes within the hour the stamp names.
        var real = DateTimeOffset.UtcNow;
        var now = new DateTimeOffset(real.Year, real.Month, real.Day, real.Hour, 1, 0, TimeSpan.Zero);
        var clock = new MovableClock(now);
        var tenant = new Tenant { Id = Guid.CreateVersion7(), Code = $"sl-{Guid.NewGuid():N}"[..16], DisplayName = "sl", Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = now, UpdatedAt = now, Version = 1 };
        var connection = new PlatformConnection { Id = Guid.CreateVersion7(), TenantId = tenant.Id, PublicId = Guid.CreateVersion7(), PlatformCode = "TRENDYOL", Environment = "STAGE", DisplayName = "sl", ExternalStoreId = $"s-{Guid.NewGuid():N}"[..12], Status = "ACTIVE", ApiVersion = "V1" };
        var order = new Order
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenant.Id,
            ConnectionId = connection.Id,
            ExternalOrderId = "sl-0",
            OrderNumber = "SL0",
            Currency = "TRY",
            OrderedAt = now.AddMinutes(-10),
            LastRemoteModifiedAt = now,
            ShipmentDueAt = now.AddMinutes(10),
            CustomerSnapshotJson = "{}",
            ShipmentAddressSnapshotJson = "{}",
            InvoiceAddressSnapshotJson = "{}",
            DerivedStatus = "NEW",
            CreatedAt = now,
            UpdatedAt = now,
            Version = 1
        };
        try
        {
            await using (var db = fixture.CreateContext())
            {
                db.Tenants.Add(tenant);
                db.PlatformConnections.Add(connection);
                db.Orders.Add(order);
                db.Orders.Add(new Order
                {
                    Id = Guid.CreateVersion7(),
                    TenantId = tenant.Id,
                    ConnectionId = connection.Id,
                    ExternalOrderId = "sl-1",
                    OrderNumber = "SL1",
                    Currency = "TRY",
                    OrderedAt = now.AddMinutes(-20),
                    LastRemoteModifiedAt = now,
                    CustomerSnapshotJson = "{}",
                    ShipmentAddressSnapshotJson = "{}",
                    InvoiceAddressSnapshotJson = "{}",
                    DerivedStatus = "NEW",
                    CreatedAt = now,
                    UpdatedAt = now,
                    Version = 1
                });
                await db.SaveChangesAsync();
            }
            var query = new OrderListQuery();
            string[] tabs = ["ALL", "CANCELLED", "LATE"];
            await using (var db = fixture.CreateContext())
            {
                var before = await Service(db, clock).OrderSummaryAsync(tenant.Id, query, CancellationToken.None, tabs, allowCached: true);
                Assert.Equal((2, 0, 0), (before.All, before.Cancelled, before.Late));
            }

            // The ship-by time passes; nothing the stamp sees changes. The second order is cancelled behind the
            // stamp's back (no UpdatedAt change): the Cancelled counter still answers from the cache.
            clock.Now = now.AddMinutes(20);
            await using (var db = fixture.CreateContext())
                await db.Orders.Where(row => row.TenantId == tenant.Id && row.ExternalOrderId == "sl-1").ExecuteUpdateAsync(set => set.SetProperty(row => row.DerivedStatus, "CANCELLED"));
            await using (var db = fixture.CreateContext())
            {
                var service = Service(db, clock);
                var after = await service.OrderSummaryAsync(tenant.Id, query, CancellationToken.None, tabs, allowCached: true);
                var fresh = await service.OrderSummaryAsync(tenant.Id, query, CancellationToken.None, tabs);
                Assert.Equal((1, 1), (fresh.Late, fresh.Cancelled));
                Assert.Equal(fresh.Late, after.Late);
                Assert.Equal(1, after.LateByConnection![connection.Id.ToString("D")]);
                Assert.Equal((2, 0), (after.All, after.Cancelled));
            }
        }
        finally
        {
            await using var db = fixture.CreateContext();
            await db.IntegrationOutboxEvents.Where(row => row.TenantId == tenant.Id).ExecuteDeleteAsync();
            await db.Orders.Where(row => row.TenantId == tenant.Id).ExecuteDeleteAsync();
            await db.PlatformConnections.Where(row => row.TenantId == tenant.Id).ExecuteDeleteAsync();
            await db.Tenants.Where(row => row.Id == tenant.Id).ExecuteDeleteAsync();
        }
    }

    private sealed class MovableClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private MarketplaceSalesService Service(AppDbContext db, TimeProvider? clock = null) =>
        new(db, new CursorCodec(new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider(), fixture.TimeProvider), new ConfigurationBuilder().Build(), null!, null!, null!, null!, clock ?? TimeProvider.System);
}
