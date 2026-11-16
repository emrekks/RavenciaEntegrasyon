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

    // KATALOG-10 (round 7): the stock filters, the platform-state filters, the price and stock sorts and the summary read in
    // the database give the in-memory list's rows, order, counts and cursors on every page.
    [PostgreSqlFact]
    public async Task FiltersSortsAndSummary_InTheDatabase_ListWhatTheInMemoryPathLists()
    {
        await using var db = fixture.CreateContext();
        var seed = await SeedAsync(db);
        var connection = new PlatformConnection { Id = Guid.CreateVersion7(), TenantId = seed.TenantId, PublicId = Guid.CreateVersion7(), PlatformCode = "TRENDYOL", Environment = "STAGE", DisplayName = "Trendyol", ExternalStoreId = $"s-{Guid.NewGuid():N}"[..12], Status = "ACTIVE", ApiVersion = "V2", Version = 1 };
        db.PlatformConnections.Add(connection);
        var variants = await db.ProductVariants.Where(x => x.TenantId == seed.TenantId).OrderBy(x => x.Sku).ToListAsync();
        var random = new Random(20261116);
        for (var index = 0; index < variants.Count; index++)
        {
            var variant = variants[index];
            // Stock: none, zero, below zero, some; an item more on a few (two MAIN rows summed).
            if (index % 5 != 0)
            {
                var onHand = (index % 7) switch { 0 => 0m, 1 => -2m, _ => random.Next(1, 9) };
                db.InventoryItems.Add(new InventoryItem { Id = Guid.CreateVersion7(), TenantId = seed.TenantId, VariantId = variant.Id, LocationCode = "MAIN", OnHand = onHand, Available = Math.Max(0, onHand) });
            }
            // Offers: none, a passive one, an active one with another price, a zero price.
            if (index % 3 != 0)
                db.ChannelOffers.Add(new ChannelOffer { Id = Guid.CreateVersion7(), TenantId = seed.TenantId, ConnectionId = connection.Id, VariantId = variant.Id, ListPrice = 900m, SalePrice = index % 4 == 0 ? 0m : 100m + index * 10, Currency = "TRY", VatRate = 10m, VatInclusion = "INCLUDED", RoundingMode = "HALF_EVEN", Status = index % 2 == 0 ? "ACTIVE" : "PASSIVE", Version = 1 });
            // Links on Trendyol: matched, one the marketplace no longer knows, none.
            if (index % 4 is 1 or 2)
                db.MarketplaceVariantLinks.Add(new MarketplaceVariantLink { Id = Guid.CreateVersion7(), TenantId = seed.TenantId, ConnectionId = connection.Id, VariantId = variant.Id, ExternalId = $"EXT-{Guid.NewGuid():N}", HealthStatus = index % 4 == 2 ? "NOT_FOUND" : null, Version = 1 });
        }
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var codec = new CursorCodec(new EphemeralDataProtectionProvider(), TimeProvider.System);
        Assert.True(await Service(db, codec).FamilyIdMatchesModelCodeRuleAsync(seed.TenantId, CancellationToken.None));

        string Position(string? cursor, string? sort) => cursor is null ? "-"
            : sort is null ? codec.TryDecodeProduct(cursor, out var createdAt, out var id) ? $"{createdAt.UtcTicks}:{id}" : "invalid"
            : codec.TryDecodeProductSort(cursor, ProductCatalogSort.Parse(sort)!.Value, out var key) ? $"{key.Metric}:{key.CreatedAt.UtcTicks}:{key.Id}" : "invalid";
        var compared = 0;
        foreach (var stock in new string?[] { null, "OUT", "LOW", "GAP", "OK", "SOMETHING" })
            foreach (var platform in new string?[] { null, "TRENDYOL:ACTIVE", "TRENDYOL:PASSIVE", "TRENDYOL:PARTIAL", "ALL:NOT_FOUND" })
                foreach (var sort in new string?[] { null, "price-asc", "price-desc", "stock-asc", "stock-desc" })
                    foreach (var (status, limit) in new (string?, int)[] { (null, 2), ("ACTIVE", 50) })
                    {
                        var sqlService = new CatalogService(db, codec, new ConfigurationBuilder().Build(), TimeProvider.System, new MemoryCache(new MemoryCacheOptions()));
                        var memoryService = new CatalogService(db, codec, new ConfigurationBuilder().Build(), TimeProvider.System, new MemoryCache(new MemoryCacheOptions()));
                        string? sqlAfter = null, memoryAfter = null;
                        for (var page = 0; page < 20; page++)
                        {
                            var sql = await sqlService.ListProductsAsync(seed.TenantId, limit, sqlAfter, status, null, platform, stock, sort, sqlPaging: true, CancellationToken.None);
                            var memory = await memoryService.ListProductsAsync(seed.TenantId, limit, memoryAfter, status, null, platform, stock, sort, sqlPaging: false, CancellationToken.None);
                            var label = $"stock {stock}, platform {platform}, sort {sort}, status {status}, page {page}";
                            Assert.True(JsonSerializer.Serialize(memory.Items) == JsonSerializer.Serialize(sql.Items), label);
                            Assert.True((memory.HasMore, memory.TotalCount, Position(memory.NextCursor, sort)) == (sql.HasMore, sql.TotalCount, Position(sql.NextCursor, sort)), label);
                            compared++;
                            if (!sql.HasMore) break;
                            (sqlAfter, memoryAfter) = (sql.NextCursor, memory.NextCursor);
                        }
                    }
        Assert.True(compared > 300, $"{compared} pages");

        var summaryService = Service(db, codec);
        Assert.Equal(JsonSerializer.Serialize(await summaryService.ProductSummaryInMemoryAsync(seed.TenantId, CancellationToken.None)), JsonSerializer.Serialize(await summaryService.ProductSummaryAsync(seed.TenantId, CancellationToken.None)));
    }

    // The rule check is kept for a short time and dropped by a save of a card or variant of the tenant.
    [PostgreSqlFact]
    public async Task TheRuleCheck_IsCached_AndDroppedByACatalogSave()
    {
        await using var db = fixture.CreateContext();
        var seed = await SeedAsync(db);
        var codec = new CursorCodec(new EphemeralDataProtectionProvider(), TimeProvider.System);
        var service = Service(db, codec);
        Assert.True(await service.FamilyIdMatchesModelCodeRuleCachedAsync(seed.TenantId, CancellationToken.None));
        // Behind the tracker's back: the cached answer stands.
        var gri = await db.Products.AsNoTracking().SingleAsync(x => x.TenantId == seed.TenantId && x.Title == "Kadın Gri Etek");
        await db.Database.ExecuteSqlInterpolatedAsync($"""UPDATE catalog.products SET "FamilyId" = NULL WHERE "Id" = {gri.Id}""");
        Assert.True(await service.FamilyIdMatchesModelCodeRuleCachedAsync(seed.TenantId, CancellationToken.None));
        // A tracked save of a card of the tenant drops it.
        var card = await db.Products.SingleAsync(x => x.TenantId == seed.TenantId && x.Title == "Kodsuz Gömlek");
        card.Description = "Yeni açıklama";
        card.Version++;
        await db.SaveChangesAsync();
        Assert.False(await service.FamilyIdMatchesModelCodeRuleCachedAsync(seed.TenantId, CancellationToken.None));
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
