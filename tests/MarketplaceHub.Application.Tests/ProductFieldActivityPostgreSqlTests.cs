using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// Ürün Hareketleri › Ürün Bilgisi on PostgreSQL: Toplu Değer Ata and Toplu KDV Ata write their information field
// changes (old → new, the user, Toplu Düzenleme) into the same save, a repeat writes nothing, the list shows them under
// the INFO kind and its movement filter, and a search reaches every matching variant however many there are.
[Collection("postgres-integration")]
public sealed class ProductFieldActivityPostgreSqlTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>, IAsyncLifetime
{
    private static readonly System.Collections.Concurrent.ConcurrentBag<Guid> CreatedTenants = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        var tenantIds = CreatedTenants.ToArray();
        if (tenantIds.Length == 0) return;
        await using var db = fixture.CreateContext();
        await db.ProductChangeEntries.Where(x => tenantIds.Contains(x.TenantId)).ExecuteDeleteAsync();
    }

    [PostgreSqlFact]
    public async Task Bulk_field_and_bulk_vat_record_their_changes_and_the_list_shows_them_as_product_information()
    {
        var seed = await Seed(variantCount: 2);
        var userId = Guid.CreateVersion7();
        await using (var db = fixture.CreateContext())
        {
            var catalog = Catalog(db);
            var desi = await catalog.BulkSetFieldAsync(seed.Tenant.Id, new BulkProductFieldCommand([seed.ProductId], BulkProductFields.Desi, "2,5"), CancellationToken.None, userId, "corr-desi");
            Assert.True(desi.Succeeded, desi.Error?.Message);
        }
        await using (var db = fixture.CreateContext())
        {
            var brand = await Catalog(db).BulkSetFieldAsync(seed.Tenant.Id, new BulkProductFieldCommand([seed.ProductId], BulkProductFields.Brand, seed.NewBrandId.ToString("D")), CancellationToken.None, userId, "corr-brand");
            Assert.True(brand.Succeeded, brand.Error?.Message);
        }
        await using (var db = fixture.CreateContext())
        {
            var vat = await Catalog(db).BulkSetVatAsync(seed.Tenant.Id, new BulkProductVatCommand([seed.ProductId], 20m, "EXCLUDED"), CancellationToken.None, userId, "corr-vat");
            Assert.True(vat.Succeeded, vat.Error?.Message);
            // The same value again changes nothing and writes nothing.
            var again = await Catalog(db).BulkSetVatAsync(seed.Tenant.Id, new BulkProductVatCommand([seed.ProductId], 20m, "EXCLUDED"), CancellationToken.None, userId, "corr-vat-2");
            Assert.True(again.Succeeded, again.Error?.Message);
        }

        await using var check = fixture.CreateContext();
        var rows = await check.ProductChangeEntries.AsNoTracking().Where(x => x.TenantId == seed.Tenant.Id && x.Kind == ProductChangeKinds.Field).ToListAsync();
        Assert.All(rows, row => Assert.Equal((ProductChangeSources.BulkEdit, (Guid?)userId), (row.Source, row.ActorUserId)));
        Assert.Equal(2, rows.Count(x => x.Field == ProductChangeFields.Desi && x.VariantId != null && x.PreviousValue == "1" && x.NewValue == "2,5"));
        Assert.Single(rows, x => x.Field == ProductChangeFields.Brand && x.VariantId == null && x.PreviousValue == "Eski Marka" && x.NewValue == "Yeni Marka");
        Assert.Single(rows, x => x.Field == ProductChangeFields.VatRate && x.PreviousValue == "%10" && x.NewValue == "%20");
        Assert.Single(rows, x => x.Field == ProductChangeFields.VatInclusion && x.PreviousValue == "Dahil" && x.NewValue == "Hariç");
        Assert.Equal(5, rows.Count);

        var service = new ProductActivityService(check);
        var info = (await service.PageAsync(seed.Tenant.Id, new ProductActivityQuery(Kinds: [ProductActivityKinds.Info]), CancellationToken.None)).Value!.Items;
        Assert.Equal(5, info.Count);
        Assert.All(info, item => Assert.Equal((ProductActivityKinds.Info, ProductActivityTypes.Field, ProductChangeSources.BulkEdit), (item.Kind, item.Type, item.Source)));
        var vatRow = Assert.Single(info, item => item.Field == ProductChangeFields.VatRate);
        Assert.Equal(("%10", "%20", seed.Title), (vatRow.PreviousValue, vatRow.NewValue, vatRow.ProductTitle));
        // The movement filter: KDV is the rate and the inclusion; Desi alone its variant rows.
        Assert.Equal(2, (await service.PageAsync(seed.Tenant.Id, new ProductActivityQuery(Movements: [ProductActivityMovements.Vat]), CancellationToken.None)).Value!.Items.Count);
        Assert.Equal(2, (await service.PageAsync(seed.Tenant.Id, new ProductActivityQuery(Movements: [ProductActivityMovements.Desi]), CancellationToken.None)).Value!.Items.Count);
        Assert.Empty((await service.PageAsync(seed.Tenant.Id, new ProductActivityQuery(Kinds: [ProductActivityKinds.Price], Movements: [ProductActivityMovements.Desi]), CancellationToken.None)).Value!.Items);
        // A search by a variant's code finds its own desi row and the card-wide rows of its product.
        Assert.Equal(4, (await service.PageAsync(seed.Tenant.Id, new ProductActivityQuery(Kinds: [ProductActivityKinds.Info], Search: seed.FirstSku), CancellationToken.None)).Value!.Items.Count);
        var counts = (await service.CountsAsync(seed.Tenant.Id, new ProductActivityQuery(), CancellationToken.None)).Value!;
        Assert.Equal(5, Assert.Single(counts, x => x.Kind == ProductActivityKinds.Info).Count);
    }

    [PostgreSqlFact]
    public async Task A_search_matching_more_than_a_thousand_variants_misses_none_of_their_rows()
    {
        var seed = await Seed(variantCount: 1_100);
        await using (var db = fixture.CreateContext())
        {
            var now = DateTimeOffset.UtcNow;
            var variantIds = await db.ProductVariants.AsNoTracking().Where(x => x.TenantId == seed.Tenant.Id).Select(x => x.Id).ToListAsync();
            db.ProductChangeEntries.AddRange(variantIds.Select((id, index) => ProductChangeLog.Field(seed.Tenant.Id, seed.ProductId, id, ProductChangeFields.Cost, null, ProductChangeFields.Money(index + 1), ProductChangeSources.FileImport, null, "corr-many", now.AddSeconds(-index))!));
            await db.SaveChangesAsync();
        }

        await using var check = fixture.CreateContext();
        var service = new ProductActivityService(check);
        // The title names every variant of the card: all 1 100 rows count, none is cut away.
        var counts = (await service.CountsAsync(seed.Tenant.Id, new ProductActivityQuery(Search: seed.Title), CancellationToken.None)).Value!;
        Assert.Equal(1_100, Assert.Single(counts, x => x.Kind == ProductActivityKinds.Info).Count);
        // The list reaches the same rows: its page is the newest of all 1 100.
        Assert.Equal(ProductActivityQuery.DefaultLimit, (await service.PageAsync(seed.Tenant.Id, new ProductActivityQuery(Search: seed.Title), CancellationToken.None)).Value!.Items.Count);
        Assert.Empty((await service.PageAsync(seed.Tenant.Id, new ProductActivityQuery(Search: "yok-boyle-bir-urun"), CancellationToken.None)).Value!.Items);
    }

    private CatalogService Catalog(AppDbContext db) => new(db, null!, new ConfigurationBuilder().Build(), fixture.TimeProvider, new MemoryCache(new MemoryCacheOptions()));

    private sealed record Seeded(Tenant Tenant, Guid ProductId, Guid NewBrandId, string Title, string FirstSku);

    // One card under "Eski Marka" with KDV %10 dahil and the given number of variants (desi 1, no model code), and an
    // active "Yeni Marka".
    private async Task<Seeded> Seed(int variantCount)
    {
        var now = DateTimeOffset.UtcNow;
        var tag = Guid.NewGuid().ToString("N")[..8];
        var tenant = new Tenant { Id = Guid.CreateVersion7(), Code = $"field-activity-{Guid.NewGuid():N}", DisplayName = "field activity", Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = now, UpdatedAt = now, Version = 1 };
        CreatedTenants.Add(tenant.Id);
        var oldBrand = new Brand { Id = Guid.CreateVersion7(), TenantId = tenant.Id, Name = "Eski Marka", NormalizedName = $"ESKI MARKA {tag}", CreatedAt = now, UpdatedAt = now };
        var newBrand = new Brand { Id = Guid.CreateVersion7(), TenantId = tenant.Id, Name = "Yeni Marka", NormalizedName = $"YENI MARKA {tag}", CreatedAt = now, UpdatedAt = now };
        var title = $"Bilgi Bluz {tag}";
        var product = new Product { Id = Guid.CreateVersion7(), TenantId = tenant.Id, Title = title, Description = "", BrandId = oldBrand.Id, VatRate = 10m, VatInclusion = "INCLUDED", Status = ProductStatus.Active, CreatedAt = now, UpdatedAt = now };
        var variants = Enumerable.Range(0, variantCount).Select(index =>
        {
            var sku = $"INF-{tag}-{index:D4}";
            return new ProductVariant { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ProductId = product.Id, SortOrder = index, Sku = sku, SkuNormalized = sku, OptionSignature = $"Beden: {index}", Desi = 1m, CreatedAt = now, UpdatedAt = now };
        }).ToList();
        await using var db = fixture.CreateContext();
        db.Tenants.Add(tenant);
        db.Brands.AddRange(oldBrand, newBrand);
        db.Products.Add(product);
        db.ProductVariants.AddRange(variants);
        await db.SaveChangesAsync();
        return new(tenant, product.Id, newBrand.Id, title, variants[0].Sku);
    }
}
