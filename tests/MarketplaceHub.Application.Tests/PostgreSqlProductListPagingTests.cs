using System.Text.Json;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// KATALOG-10: the product list paged in SQL by the stored model link gives the in-memory grouping's rows, order, counts and
// cursors on every page, for every filter it serves, over cards without a model code, the two-brand code (KENE01),
// archived and draft cards and a search that finds one colour only; a tenant whose links do not give the model-code
// rule's grouping (a link that failed, one code under two brands with two cards of one) keeps the in-memory path.
[Collection("postgres-integration")]
public sealed class PostgreSqlProductListPagingTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    private static Product AddCard(AppDbContext db, Guid tenantId, Guid? brandId, string? modelCode, string colour, string title, DateTimeOffset createdAt, ProductStatus status = ProductStatus.Active)
    {
        var card = new Product { Id = Guid.CreateVersion7(), TenantId = tenantId, Title = title, Description = "Açıklama", BrandId = brandId, VatRate = 10m, VatInclusion = "INCLUDED", Status = status, CreatedAt = createdAt, UpdatedAt = createdAt, Version = 1 };
        db.Products.Add(card);
        var index = 0;
        foreach (var size in new[] { "M", "L" })
        {
            var sku = $"{modelCode ?? "X"}-{colour}-{size}-{Guid.NewGuid():N}"[..40];
            db.ProductVariants.Add(new ProductVariant { Id = Guid.CreateVersion7(), TenantId = tenantId, ProductId = card.Id, Sku = sku, SkuNormalized = sku.ToUpperInvariant(), ModelCode = modelCode, OptionSignature = $"Renk: {colour} | Beden: {size}", Desi = 1m, DefaultListPrice = 900m, DefaultSalePrice = 600m, Status = status, SortOrder = index++, CreatedAt = createdAt, UpdatedAt = createdAt, Version = 1 });
        }
        return card;
    }

    private sealed record Tenantseed(Guid TenantId, Guid Ravencia, Guid Lavian);

    private async Task<Tenantseed> SeedAsync(AppDbContext db)
    {
        var now = DateTimeOffset.UtcNow;
        var tenant = new Tenant { Id = Guid.CreateVersion7(), Code = $"paging-{Guid.NewGuid():N}", DisplayName = "paging", Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = now, UpdatedAt = now, Version = 1 };
        var seed = new Tenantseed(tenant.Id, Guid.CreateVersion7(), Guid.CreateVersion7());
        db.Tenants.Add(tenant);
        db.Brands.Add(new Brand { Id = seed.Ravencia, TenantId = tenant.Id, Name = "Ravencia", NormalizedName = "RAVENCIA", IsActive = true, CreatedAt = now, UpdatedAt = now, Version = 1 });
        db.Brands.Add(new Brand { Id = seed.Lavian, TenantId = tenant.Id, Name = "Lavian", NormalizedName = "LAVIAN", IsActive = true, CreatedAt = now, UpdatedAt = now, Version = 1 });
        var at = now.AddDays(-1);
        // Models of several colours, interleaved in time; one colour archived, one a draft; two cards created at the same
        // instant (the id breaks the tie); spellings of one code; codeless cards; KENE01 under two brands.
        AddCard(db, tenant.Id, seed.Ravencia, "MZ100", "Siyah", "Kadın Siyah Etek", at.AddMinutes(1));
        AddCard(db, tenant.Id, seed.Ravencia, "MZ200", "Siyah", "Kadın Siyah Bluz", at.AddMinutes(2));
        AddCard(db, tenant.Id, seed.Ravencia, "mz-100", "Mavi", "Kadın Mavi Etek", at.AddMinutes(3));
        AddCard(db, tenant.Id, seed.Ravencia, null, "Beyaz", "Kodsuz Gömlek", at.AddMinutes(4));
        AddCard(db, tenant.Id, seed.Ravencia, "MZ200", "Gri", "Kadın Gri Bluz", at.AddMinutes(5), ProductStatus.Archived);
        AddCard(db, tenant.Id, seed.Ravencia, "MZ300", "Siyah", "Kadın Siyah Ceket", at.AddMinutes(6), ProductStatus.Draft);
        AddCard(db, tenant.Id, seed.Lavian, "KENE01", "Mavi", "Kadın Mavi Kot Etek", at.AddMinutes(7));
        AddCard(db, tenant.Id, seed.Ravencia, "KENE01", "Siyah", "Kadın Siyah Kot Etek", at.AddMinutes(7));
        AddCard(db, tenant.Id, seed.Ravencia, "MZ300", "Bordo", "Kadın Bordo Ceket", at.AddMinutes(8));
        AddCard(db, tenant.Id, null, "MZ400", "Siyah", "Markasız Şort", at.AddMinutes(9));
        AddCard(db, tenant.Id, null, null, "Gri", "Kodsuz Markasız Atlet", at.AddMinutes(9));
        AddCard(db, tenant.Id, seed.Ravencia, "MZ100", "Gri", "Kadın Gri Etek", at.AddMinutes(10));
        await db.SaveChangesAsync();
        return seed;
    }

    private CatalogService Service(AppDbContext db, CursorCodec codec) =>
        new(db, codec, new ConfigurationBuilder().Build(), fixture.TimeProvider, new MemoryCache(new MemoryCacheOptions()));

    // Every page of both paths: the same items, flags, counts and cursor positions.
    private static async Task AssertSameListsAsync(AppDbContext db, Guid tenantId)
    {
        var codec = new CursorCodec(new EphemeralDataProtectionProvider(), TimeProvider.System);
        string Position(string? cursor) => cursor is null ? "-" : codec.TryDecodeProduct(cursor, out var createdAt, out var id) ? $"{createdAt.UtcTicks}:{id}" : "invalid";
        foreach (var (status, search) in new (string?, string?)[] { (null, null), ("ACTIVE", null), ("ARCHIVED", null), ("DRAFT", null), (null, "Etek"), (null, "Gri"), ("ACTIVE", "Kot") })
            foreach (var limit in new[] { 1, 2, 3, 50 })
            {
                // A fresh service per path: the count cache is the service's own.
                var sqlService = new CatalogService(db, codec, new ConfigurationBuilder().Build(), TimeProvider.System, new MemoryCache(new MemoryCacheOptions()));
                var memoryService = new CatalogService(db, codec, new ConfigurationBuilder().Build(), TimeProvider.System, new MemoryCache(new MemoryCacheOptions()));
                string? sqlAfter = null, memoryAfter = null;
                for (var page = 0; page < 20; page++)
                {
                    var sql = await sqlService.ListProductsAsync(tenantId, limit, sqlAfter, status, search, null, null, null, sqlPaging: true, CancellationToken.None);
                    var memory = await memoryService.ListProductsAsync(tenantId, limit, memoryAfter, status, search, null, null, null, sqlPaging: false, CancellationToken.None);
                    var label = $"status {status}, search {search}, limit {limit}, page {page}";
                    Assert.True(JsonSerializer.Serialize(memory.Items) == JsonSerializer.Serialize(sql.Items), label);
                    Assert.True((memory.HasMore, memory.TotalCount, Position(memory.NextCursor)) == (sql.HasMore, sql.TotalCount, Position(sql.NextCursor)), label);
                    if (!sql.HasMore) break;
                    (sqlAfter, memoryAfter) = (sql.NextCursor, memory.NextCursor);
                }
            }
    }

    [PostgreSqlFact]
    public async Task TheSqlPaging_ListsWhatTheInMemoryGroupingLists()
    {
        await using var db = fixture.CreateContext();
        var seed = await SeedAsync(db);
        var codec = new CursorCodec(new EphemeralDataProtectionProvider(), TimeProvider.System);
        Assert.True(await Service(db, codec).FamilyIdMatchesModelCodeRuleAsync(seed.TenantId, CancellationToken.None));
        await AssertSameListsAsync(db, seed.TenantId);
    }

    [PostgreSqlFact]
    public async Task ATenantWhoseLinksDifferFromTheModelCodeRule_KeepsTheInMemoryPath()
    {
        await using var db = fixture.CreateContext();
        var seed = await SeedAsync(db);
        var codec = new CursorCodec(new EphemeralDataProtectionProvider(), TimeProvider.System);
        var service = Service(db, codec);
        // A link that failed after its save: the card is its own row in SQL, one of MZ100 in memory.
        var gri = await db.Products.AsNoTracking().SingleAsync(x => x.TenantId == seed.TenantId && x.Title == "Kadın Gri Etek");
        await db.Database.ExecuteSqlInterpolatedAsync($"""UPDATE catalog.products SET "FamilyId" = NULL WHERE "Id" = {gri.Id}""");
        Assert.False(await service.FamilyIdMatchesModelCodeRuleAsync(seed.TenantId, CancellationToken.None));
        await AssertSameListsAsync(db, seed.TenantId);

        // Relinked (the scan): the SQL path again. Then KENE01 gets a second Lavian card: two cards of one brand stay
        // unlinked (the code is ambiguous), one row in memory, two in SQL: the in-memory path.
        await ProductModelScan.RunTenantAsync(db, seed.TenantId, DateTimeOffset.UtcNow, CancellationToken.None);
        Assert.True(await service.FamilyIdMatchesModelCodeRuleAsync(seed.TenantId, CancellationToken.None));
        AddCard(db, seed.TenantId, seed.Lavian, "KENE01", "Gri", "Kadın Gri Kot Etek", DateTimeOffset.UtcNow.AddHours(-1));
        await db.SaveChangesAsync();
        Assert.False(await service.FamilyIdMatchesModelCodeRuleAsync(seed.TenantId, CancellationToken.None));
        await AssertSameListsAsync(db, seed.TenantId);
    }
}
