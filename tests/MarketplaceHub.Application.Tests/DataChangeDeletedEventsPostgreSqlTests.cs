using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// FATURA-FINANS-3: a row deleted through the tracker raises its aggregate's change event like an added or changed one,
// once per aggregate and save.
[Collection("postgres-integration")]
public sealed class DataChangeDeletedEventsPostgreSqlTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    [PostgreSqlFact]
    public async Task ADeletedRow_RaisesOneEventForItsAggregate()
    {
        var now = DateTimeOffset.UtcNow;
        var tenant = new Tenant { Id = Guid.CreateVersion7(), Code = $"del-{Guid.NewGuid():N}", DisplayName = "del", Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = now, UpdatedAt = now, Version = 1 };
        var product = new Product { Id = Guid.CreateVersion7(), TenantId = tenant.Id, Title = "Silinen Varyant", Description = "", Status = ProductStatus.Draft, CreatedAt = now, UpdatedAt = now, Version = 1 };
        ProductVariant Variant(int index)
        {
            var sku = $"DEL-{index}-{Guid.NewGuid():N}"[..30];
            return new ProductVariant { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ProductId = product.Id, Sku = sku, SkuNormalized = sku.ToUpperInvariant(), OptionSignature = $"Beden: {index}", Status = ProductStatus.Draft, SortOrder = index, CreatedAt = now, UpdatedAt = now, Version = 1 };
        }
        var variants = new[] { Variant(0), Variant(1), Variant(2) };
        await using (var seed = fixture.CreateContext())
        {
            seed.Tenants.Add(tenant);
            seed.Products.Add(product);
            seed.ProductVariants.AddRange(variants);
            await seed.SaveChangesAsync();
            await seed.IntegrationOutboxEvents.Where(x => x.TenantId == tenant.Id).ExecuteDeleteAsync();
        }

        await using (var db = fixture.CreateContext())
        {
            db.ProductVariants.Remove(await db.ProductVariants.SingleAsync(x => x.Id == variants[2].Id));
            await db.SaveChangesAsync();
        }
        await using (var check = fixture.CreateContext())
        {
            var deleted = await check.IntegrationOutboxEvents.AsNoTracking().SingleAsync(x => x.TenantId == tenant.Id);
            Assert.Equal(("products", "deleted", "Product", (Guid?)product.Id), (deleted.ResourceType, deleted.OperationType, deleted.AggregateType, deleted.AggregateId));
            await check.IntegrationOutboxEvents.Where(x => x.TenantId == tenant.Id).ExecuteDeleteAsync();
        }

        // A delete and a change of the same aggregate in one save: one event, as before.
        await using (var db = fixture.CreateContext())
        {
            var card = await db.Products.SingleAsync(x => x.Id == product.Id);
            card.Title = "Yeni Ad";
            card.Version++;
            db.ProductVariants.Remove(await db.ProductVariants.SingleAsync(x => x.Id == variants[1].Id));
            await db.SaveChangesAsync();
        }
        await using (var check = fixture.CreateContext())
            Assert.Equal("updated", (await check.IntegrationOutboxEvents.AsNoTracking().SingleAsync(x => x.TenantId == tenant.Id)).OperationType);
    }

    // A deleted return claim is announced as a change of its order, so a closed report of the order's period is dropped
    // (ReportCache cannot place a claim that is gone).
    [PostgreSqlFact]
    public async Task ADeletedReturnClaim_IsAnnouncedOnItsOrder_AndDropsTheReportOfItsPeriod()
    {
        var now = DateTimeOffset.UtcNow;
        var tenant = new Tenant { Id = Guid.CreateVersion7(), Code = $"del-claim-{Guid.NewGuid():N}", DisplayName = "del", Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = now, UpdatedAt = now, Version = 1 };
        var connection = new PlatformConnection { Id = Guid.CreateVersion7(), TenantId = tenant.Id, PublicId = Guid.CreateVersion7(), PlatformCode = "TRENDYOL", Environment = "STAGE", DisplayName = "t", ExternalStoreId = $"s-{Guid.NewGuid():N}"[..12], Status = "ACTIVE", ApiVersion = "V1" };
        var order = new Order { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, ExternalOrderId = "D-1", OrderNumber = "D-1", Currency = "TRY", OrderedAt = now.AddDays(-3), LastRemoteModifiedAt = now, CustomerSnapshotJson = "{}", ShipmentAddressSnapshotJson = "{}", InvoiceAddressSnapshotJson = "{}", DerivedStatus = "DELIVERED", CreatedAt = now, UpdatedAt = now, Version = 1 };
        var claim = new ReturnClaim { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, OrderId = order.Id, ExternalClaimId = "C-1", Status = ReturnClaimStatus.Requested, RawStatus = "Created", CreatedAt = now, UpdatedAt = now, Version = 1 };
        await using (var seed = fixture.CreateContext())
        {
            seed.Tenants.Add(tenant);
            seed.PlatformConnections.Add(connection);
            seed.Orders.Add(order);
            seed.ReturnClaims.Add(claim);
            await seed.SaveChangesAsync();
            await seed.IntegrationOutboxEvents.Where(x => x.TenantId == tenant.Id).ExecuteDeleteAsync();
        }
        var computedAt = DateTimeOffset.UtcNow;
        await using (var db = fixture.CreateContext())
        {
            db.ReturnClaims.Remove(await db.ReturnClaims.SingleAsync(x => x.Id == claim.Id));
            await db.SaveChangesAsync();
        }
        await using (var check = fixture.CreateContext())
        {
            var announced = await check.IntegrationOutboxEvents.AsNoTracking().SingleAsync(x => x.TenantId == tenant.Id);
            Assert.Equal(("returns", "deleted", "Order", (Guid?)order.Id), (announced.ResourceType, announced.OperationType, announced.AggregateType, announced.AggregateId));
            var entry = new ReportCache.Entry(null!, computedAt.AddSeconds(-1), now.AddDays(-7), now.AddDays(-1), true);
            Assert.True(await ReportCache.ChangedSinceAsync(check, tenant.Id, entry, CancellationToken.None));
            var otherPeriod = entry with { Start = now.AddDays(-30), End = now.AddDays(-20) };
            Assert.False(await ReportCache.ChangedSinceAsync(check, tenant.Id, otherPeriod, CancellationToken.None));
        }
    }
}
