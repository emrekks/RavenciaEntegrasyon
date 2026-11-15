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

    private MarketplaceSalesService Service(AppDbContext db) =>
        new(db, new CursorCodec(new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider(), fixture.TimeProvider), new ConfigurationBuilder().Build(), null!, null!, null!, null!, TimeProvider.System);
}
