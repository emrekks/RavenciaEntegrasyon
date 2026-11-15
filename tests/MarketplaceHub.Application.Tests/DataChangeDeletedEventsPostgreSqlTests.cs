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
}
