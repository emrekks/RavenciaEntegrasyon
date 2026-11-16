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

    // KATALOG-10 (round 8): the platform-state filter decided in the database lists what the in-memory reference lists, for
    // every marketplace (and "ALL") × state × stock filter × status, over links and listings that reach each rule's edge:
    // a held listing (waiting, refused) over a matched link, broken links (NOT_FOUND, REJECTED) known there or never known,
    // a broken link on an archived card or under an archived listing, a broken link that still holds on a second
    // connection, sale closures beyond stock / for stock 0 / without a reason, flagged publications and listings, an update
    // on its way over a refused listing, a passive connection and a card without variants. Unknown stays unknown: a link
    // without a health or sale state, a listing UNKNOWN.
    [PostgreSqlFact]
    public async Task ThePlatformStateFilter_InTheDatabase_ListsWhatTheInMemoryReferenceLists()
    {
        await using var db = fixture.CreateContext();
        var seed = await SeedAsync(db);
        var now = DateTimeOffset.UtcNow;
        var variantless = new Product { Id = Guid.CreateVersion7(), TenantId = seed.TenantId, Title = "Varyantsız Kart", Description = "Açıklama", BrandId = seed.Ravencia, VatRate = 10m, VatInclusion = "INCLUDED", Status = ProductStatus.Active, CreatedAt = now.AddDays(-2), UpdatedAt = now.AddDays(-2), Version = 1 };
        db.Products.Add(variantless);
        PlatformConnection Connection(string code, string status) => new() { Id = Guid.CreateVersion7(), TenantId = seed.TenantId, PublicId = Guid.CreateVersion7(), PlatformCode = code, Environment = "STAGE", DisplayName = $"{code} {status}", ExternalStoreId = $"s-{Guid.NewGuid():N}"[..12], Status = status, ApiVersion = "V2", Version = 1 };
        var trendyol = Connection("TRENDYOL", "ACTIVE");
        var trendyolSecond = Connection("TRENDYOL", "VERIFIED");
        var hepsiburada = Connection("HEPSIBURADA", "ACTIVE");
        var pazaramaPassive = Connection("PAZARAMA", "PASSIVE");
        var connections = new[] { trendyol, trendyolSecond, hepsiburada, pazaramaPassive };
        db.PlatformConnections.AddRange(connections);
        await db.SaveChangesAsync();

        var products = await db.Products.Where(x => x.TenantId == seed.TenantId).OrderBy(x => x.Title).ToListAsync();
        var variants = await db.ProductVariants.Where(x => x.TenantId == seed.TenantId).OrderBy(x => x.Sku).ToListAsync();
        var random = new Random(20261116);
        string[] profileStatuses = ["LIVE", "ARCHIVED", "REJECTED", "APPROVAL_PENDING", "UPDATE_QUEUED", "UNKNOWN", "BLOCKED", "APPROVAL_PARTIAL_PENDING"];
        string[] listingStatuses = ["LIVE", ImportedApprovalStates.Pending, ImportedApprovalStates.Rejected, "UNKNOWN", "BLOCKED"];
        string?[] healths = [null, MarketplaceVariantLinkHealth.NotFound, MarketplaceVariantLinkHealth.Rejected, MarketplaceVariantLinkHealth.Suspect, MarketplaceVariantLinkHealth.Unverified];
        string?[] saleStates = [null, ListingSaleState.OnSale, ListingSaleState.OffSale];
        string?[] saleReasons = [null, ListingSaleState.StockZero, ListingSaleState.BySeller];
        var profiles = new Dictionary<(Guid ProductId, Guid ConnectionId), ChannelListingProfile>();
        var addedListings = new List<ChannelListingVariant>();
        foreach (var product in products)
            foreach (var connection in connections)
            {
                if (random.Next(3) == 0) continue;
                var profile = new ChannelListingProfile { Id = Guid.CreateVersion7(), TenantId = seed.TenantId, ConnectionId = connection.Id, ProductId = product.Id, Enabled = random.Next(6) != 0, DesiredStatus = "LIVE", ActualStatus = profileStatuses[random.Next(profileStatuses.Length)] };
                profiles[(product.Id, connection.Id)] = profile;
                db.ChannelListingProfiles.Add(profile);
            }
        for (var index = 0; index < variants.Count; index++)
        {
            var variant = variants[index];
            if (index % 5 != 0)
            {
                var onHand = (index % 7) switch { 0 => 0m, 1 => -2m, _ => random.Next(1, 9) };
                db.InventoryItems.Add(new InventoryItem { Id = Guid.CreateVersion7(), TenantId = seed.TenantId, VariantId = variant.Id, LocationCode = "MAIN", OnHand = onHand, Available = Math.Max(0, onHand) });
            }
            foreach (var connection in connections)
            {
                if (random.Next(4) != 0)
                    db.MarketplaceVariantLinks.Add(new MarketplaceVariantLink
                    {
                        Id = Guid.CreateVersion7(),
                        TenantId = seed.TenantId,
                        ConnectionId = connection.Id,
                        VariantId = variant.Id,
                        ExternalId = $"EXT-{Guid.NewGuid():N}",
                        HealthStatus = healths[random.Next(healths.Length)],
                        LastSeenAt = random.Next(2) == 0 ? now.AddDays(-3) : null,
                        SaleState = saleStates[random.Next(saleStates.Length)],
                        SaleStateReason = saleReasons[random.Next(saleReasons.Length)],
                        Version = 1
                    });
                if (profiles.TryGetValue((variant.ProductId, connection.Id), out var profile) && random.Next(3) != 0)
                    addedListings.Add(db.ChannelListingVariants.Add(new ChannelListingVariant { Id = Guid.CreateVersion7(), TenantId = seed.TenantId, ProfileId = profile.Id, VariantId = variant.Id, DesiredStatus = "LIVE", ActualStatus = listingStatuses[random.Next(listingStatuses.Length)] }).Entity);
            }
        }
        // A refused listing whose card's update is on its way ("Onaya Gönderildi": waiting, not failed).
        var resent = addedListings.First(listing => profiles.Values.Single(profile => profile.Id == listing.ProfileId).Enabled);
        resent.ActualStatus = ImportedApprovalStates.Rejected;
        profiles.Values.Single(profile => profile.Id == resent.ProfileId).ActualStatus = "UPDATE_QUEUED";
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var codec = new CursorCodec(new EphemeralDataProtectionProvider(), TimeProvider.System);
        Assert.True(await Service(db, codec).FamilyIdMatchesModelCodeRuleAsync(seed.TenantId, CancellationToken.None));

        // Each rule's edge is in the seed.
        var seededLinks = await db.MarketplaceVariantLinks.Where(x => x.TenantId == seed.TenantId).ToListAsync();
        var seededListings = await (from listing in db.ChannelListingVariants
                                    join profile in db.ChannelListingProfiles on listing.ProfileId equals profile.Id
                                    where listing.TenantId == seed.TenantId
                                    select new { listing.VariantId, profile.ConnectionId, profile.ProductId, profile.Enabled, Listing = listing.ActualStatus, Profile = profile.ActualStatus }).ToListAsync();
        var seededProfiles = await db.ChannelListingProfiles.Where(x => x.TenantId == seed.TenantId).ToListAsync();
        var productOf = variants.ToDictionary(x => x.Id, x => x.ProductId);
        var archivedCards = products.Where(x => x.Status == ProductStatus.Archived).Select(x => x.Id).ToHashSet();
        var edges = new Dictionary<string, bool>
        {
            ["held listing over a link"] = seededListings.Any(listing => listing.Enabled && ImportedApprovalStates.IsHeldListing(listing.Listing) && seededLinks.Any(link => link.VariantId == listing.VariantId && link.ConnectionId == listing.ConnectionId)),
            ["broken link known there"] = seededLinks.Any(link => MarketplaceVariantLinkHealth.IsBroken(link.HealthStatus) && link.LastSeenAt is not null),
            ["broken link never known"] = seededLinks.Any(link => MarketplaceVariantLinkHealth.IsBroken(link.HealthStatus) && link.LastSeenAt is null),
            ["broken link on an archived card"] = seededLinks.Any(link => MarketplaceVariantLinkHealth.IsBroken(link.HealthStatus) && archivedCards.Contains(productOf[link.VariantId])),
            ["broken link under an archived listing"] = seededLinks.Any(link => MarketplaceVariantLinkHealth.IsBroken(link.HealthStatus) && seededProfiles.Any(profile => profile.Enabled && profile.ActualStatus == "ARCHIVED" && profile.ProductId == productOf[link.VariantId] && profile.ConnectionId == link.ConnectionId)),
            ["closed beyond stock"] = seededLinks.Any(link => link.SaleState == ListingSaleState.OffSale && link.SaleStateReason == ListingSaleState.BySeller),
            ["closed without a reason"] = seededLinks.Any(link => link.SaleState == ListingSaleState.OffSale && link.SaleStateReason is null),
            ["closed for stock 0"] = seededLinks.Any(link => link.SaleState == ListingSaleState.OffSale && link.SaleStateReason == ListingSaleState.StockZero),
            ["unknown sale state"] = seededLinks.Any(link => link.SaleState is null),
            ["refused listing under an update on its way"] = seededListings.Any(listing => listing.Enabled && listing.Listing == ImportedApprovalStates.Rejected && ProductListingHealth.IsUpdateInFlight(listing.Profile)),
            ["broken on one Trendyol connection, holding on the other"] = seededLinks.Any(link => link.ConnectionId == trendyol.Id && MarketplaceVariantLinkHealth.IsBroken(link.HealthStatus)
                && seededLinks.Any(other => other.ConnectionId == trendyolSecond.Id && other.VariantId == link.VariantId && other.HealthStatus is null)),
        };
        Assert.True(edges.Values.All(present => present), string.Join(", ", edges.Where(edge => !edge.Value).Select(edge => edge.Key)));

        string Position(string? cursor) => cursor is null ? "-" : codec.TryDecodeProduct(cursor, out var createdAt, out var id) ? $"{createdAt.UtcTicks}:{id}" : "invalid";
        var compared = 0;
        var outcomes = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
        foreach (var code in new[] { "TRENDYOL", "HEPSIBURADA", "PAZARAMA", "ALL" })
            foreach (var state in new[] { "ACTIVE", "PARTIAL", "PASSIVE", "NOT_FOUND", "OFF_SALE", "ERROR", "APPROVAL" })
                foreach (var stock in new string?[] { null, "OUT", "LOW", "GAP", "OK" })
                    foreach (var (status, limit) in new (string?, int)[] { (null, 4), ("ACTIVE", 50), ("ARCHIVED", 50) })
                    {
                        var platform = $"{code}:{state}";
                        var sqlService = new CatalogService(db, codec, new ConfigurationBuilder().Build(), TimeProvider.System, new MemoryCache(new MemoryCacheOptions()));
                        var memoryService = new CatalogService(db, codec, new ConfigurationBuilder().Build(), TimeProvider.System, new MemoryCache(new MemoryCacheOptions()));
                        string? sqlAfter = null, memoryAfter = null;
                        for (var page = 0; page < 20; page++)
                        {
                            var sql = await sqlService.ListProductsAsync(seed.TenantId, limit, sqlAfter, status, null, platform, stock, null, sqlPaging: true, CancellationToken.None);
                            var memory = await memoryService.ListProductsAsync(seed.TenantId, limit, memoryAfter, status, null, platform, stock, null, sqlPaging: false, CancellationToken.None);
                            var label = $"platform {platform}, stock {stock}, status {status}, page {page}";
                            Assert.True(JsonSerializer.Serialize(memory.Items) == JsonSerializer.Serialize(sql.Items), label);
                            Assert.True((memory.HasMore, memory.TotalCount, Position(memory.NextCursor)) == (sql.HasMore, sql.TotalCount, Position(sql.NextCursor)), label);
                            if (page == 0 && stock is null && status is null)
                                (outcomes.TryGetValue(code, out var seen) ? seen : outcomes[code] = []).Add(sql.TotalCount ?? -1);
                            compared++;
                            if (!sql.HasMore) break;
                            (sqlAfter, memoryAfter) = (sql.NextCursor, memory.NextCursor);
                        }
                    }
        Assert.True(compared > 420, $"{compared} pages");
        // The seed is not trivial: the states split the models differently on each marketplace that has a connection.
        Assert.True(outcomes["TRENDYOL"].Count >= 4, string.Join(",", outcomes["TRENDYOL"]));
        Assert.True(outcomes["HEPSIBURADA"].Count >= 3, string.Join(",", outcomes["HEPSIBURADA"]));
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
