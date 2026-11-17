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
    // every marketplace (and "ALL") × state, under each stock filter and status, over links and listings that reach each
    // rule's edge. Every edge is a named card placed by hand (EdgeCard), so it exists on any machine and database
    // collation: a held listing (waiting, refused) over a matched link, broken links (NOT_FOUND, REJECTED) known there or
    // never known, a broken link on an archived card or under an archived listing, a broken link that still holds on a
    // second connection, sale closures beyond stock / for stock 0 / without a reason, flagged publications and listings, an
    // update on its way over a refused listing, a disabled publication, a passive connection and a card without variants.
    // Unknown stays unknown: a link without a health or sale state, a listing UNKNOWN. A seeded random fill over the other
    // cards (in an order fixed in code, never the database's) adds mixed families.
    [PostgreSqlFact]
    public async Task ThePlatformStateFilter_InTheDatabase_ListsWhatTheInMemoryReferenceLists()
    {
        await using var db = fixture.CreateContext();
        var seed = await SeedAsync(db);
        var tenantId = seed.TenantId;
        var now = DateTimeOffset.UtcNow;
        PlatformConnection Connection(string code, string status) => new() { Id = Guid.CreateVersion7(), TenantId = tenantId, PublicId = Guid.CreateVersion7(), PlatformCode = code, Environment = "STAGE", DisplayName = $"{code} {status}", ExternalStoreId = $"s-{Guid.NewGuid():N}"[..12], Status = status, ApiVersion = "V2", Version = 1 };
        var trendyol = Connection("TRENDYOL", "ACTIVE");
        var trendyolSecond = Connection("TRENDYOL", "VERIFIED");
        var hepsiburada = Connection("HEPSIBURADA", "ACTIVE");
        var pazaramaPassive = Connection("PAZARAMA", "PASSIVE");
        var connections = new[] { trendyol, trendyolSecond, hepsiburada, pazaramaPassive };
        db.PlatformConnections.AddRange(connections);

        // The random fill: the base cards in title order and each card's variants in row order, both fixed in code.
        var baseCards = db.ChangeTracker.Entries<Product>().Select(entry => entry.Entity).Where(card => card.TenantId == tenantId).OrderBy(card => card.Title, StringComparer.Ordinal).ToList();
        var baseVariants = baseCards.SelectMany(card => db.ChangeTracker.Entries<ProductVariant>().Select(entry => entry.Entity).Where(variant => variant.ProductId == card.Id).OrderBy(variant => variant.SortOrder)).ToList();
        var random = new Random(20261116);
        string[] profileStatuses = ["LIVE", "ARCHIVED", "REJECTED", "APPROVAL_PENDING", "UPDATE_QUEUED", "UNKNOWN", "BLOCKED", "APPROVAL_PARTIAL_PENDING"];
        string[] listingStatuses = ["LIVE", ImportedApprovalStates.Pending, ImportedApprovalStates.Rejected, "UNKNOWN", "BLOCKED"];
        string?[] healths = [null, MarketplaceVariantLinkHealth.NotFound, MarketplaceVariantLinkHealth.Rejected, MarketplaceVariantLinkHealth.Suspect, MarketplaceVariantLinkHealth.Unverified];
        string?[] saleStates = [null, ListingSaleState.OnSale, ListingSaleState.OffSale];
        string?[] saleReasons = [null, ListingSaleState.StockZero, ListingSaleState.BySeller];
        var profiles = new Dictionary<(Guid ProductId, Guid ConnectionId), ChannelListingProfile>();
        foreach (var card in baseCards)
            foreach (var connection in connections)
            {
                if (random.Next(3) == 0) continue;
                var profile = new ChannelListingProfile { Id = Guid.CreateVersion7(), TenantId = tenantId, ConnectionId = connection.Id, ProductId = card.Id, Enabled = random.Next(6) != 0, DesiredStatus = "LIVE", ActualStatus = profileStatuses[random.Next(profileStatuses.Length)] };
                profiles[(card.Id, connection.Id)] = profile;
                db.ChannelListingProfiles.Add(profile);
            }
        for (var index = 0; index < baseVariants.Count; index++)
        {
            var variant = baseVariants[index];
            if (index % 5 != 0)
            {
                var onHand = (index % 7) switch { 0 => 0m, 1 => -2m, _ => random.Next(1, 9) };
                db.InventoryItems.Add(new InventoryItem { Id = Guid.CreateVersion7(), TenantId = tenantId, VariantId = variant.Id, LocationCode = "MAIN", OnHand = onHand, Available = Math.Max(0, onHand) });
            }
            foreach (var connection in connections)
            {
                if (random.Next(4) != 0)
                    db.MarketplaceVariantLinks.Add(new MarketplaceVariantLink
                    {
                        Id = Guid.CreateVersion7(),
                        TenantId = tenantId,
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
                    db.ChannelListingVariants.Add(new ChannelListingVariant { Id = Guid.CreateVersion7(), TenantId = tenantId, ProfileId = profile.Id, VariantId = variant.Id, DesiredStatus = "LIVE", ActualStatus = listingStatuses[random.Next(listingStatuses.Length)] });
            }
        }

        // The edges, each on its own card (a model of its own: two sizes, M and L), placed by hand.
        var edgeAt = now.AddDays(-3);
        (Product Card, ProductVariant M, ProductVariant L) EdgeCard(string code, ProductStatus status = ProductStatus.Active)
        {
            edgeAt = edgeAt.AddMinutes(1);
            var card = AddCard(db, tenantId, seed.Ravencia, code, "Siyah", $"Kenar {code}", edgeAt, status);
            var sizes = db.ChangeTracker.Entries<ProductVariant>().Select(entry => entry.Entity).Where(variant => variant.ProductId == card.Id).OrderBy(variant => variant.SortOrder).ToList();
            db.InventoryItems.Add(new InventoryItem { Id = Guid.CreateVersion7(), TenantId = tenantId, VariantId = sizes[0].Id, LocationCode = "MAIN", OnHand = 3m, Available = 3m });
            return (card, sizes[0], sizes[1]);
        }
        MarketplaceVariantLink Link(ProductVariant variant, PlatformConnection connection, string? health = null, bool known = true, string? saleState = null, string? saleReason = null) =>
            db.MarketplaceVariantLinks.Add(new MarketplaceVariantLink { Id = Guid.CreateVersion7(), TenantId = tenantId, ConnectionId = connection.Id, VariantId = variant.Id, ExternalId = $"EDGE-{Guid.NewGuid():N}", HealthStatus = health, LastSeenAt = known ? now.AddDays(-3) : null, SaleState = saleState, SaleStateReason = saleReason, Version = 1 }).Entity;
        ChannelListingProfile Profile(Product card, PlatformConnection connection, string status, bool enabled = true) =>
            db.ChannelListingProfiles.Add(new ChannelListingProfile { Id = Guid.CreateVersion7(), TenantId = tenantId, ConnectionId = connection.Id, ProductId = card.Id, Enabled = enabled, DesiredStatus = "LIVE", ActualStatus = status }).Entity;
        ChannelListingVariant Listing(ChannelListingProfile profile, ProductVariant variant, string status) =>
            db.ChannelListingVariants.Add(new ChannelListingVariant { Id = Guid.CreateVersion7(), TenantId = tenantId, ProfileId = profile.Id, VariantId = variant.Id, DesiredStatus = "LIVE", ActualStatus = status }).Entity;

        // A held listing over a matched link: M waits, L was refused; both have a healthy link there.
        var held = EdgeCard("EDGE01");
        var heldProfile = Profile(held.Card, trendyol, "LIVE");
        Listing(heldProfile, held.M, ImportedApprovalStates.Pending);
        Listing(heldProfile, held.L, ImportedApprovalStates.Rejected);
        Link(held.M, trendyol);
        Link(held.L, trendyol);
        // A broken link known there (M), beside a healthy one (L): partly found.
        var brokenKnown = EdgeCard("EDGE02");
        Link(brokenKnown.M, trendyol, MarketplaceVariantLinkHealth.NotFound);
        Link(brokenKnown.L, trendyol);
        // Broken links never known there (no scan saw them, no live publication): no listing at all.
        var brokenUnknown = EdgeCard("EDGE03");
        Link(brokenUnknown.M, trendyol, MarketplaceVariantLinkHealth.Rejected, known: false);
        Link(brokenUnknown.L, hepsiburada, MarketplaceVariantLinkHealth.NotFound, known: false);
        // A broken link on an archived card: never missing.
        var archivedCard = EdgeCard("EDGE04", ProductStatus.Archived);
        Link(archivedCard.M, trendyol, MarketplaceVariantLinkHealth.NotFound);
        Link(archivedCard.L, trendyol, MarketplaceVariantLinkHealth.NotFound);
        // A broken link under an enabled archived listing: never missing.
        var archivedListing = EdgeCard("EDGE05");
        Profile(archivedListing.Card, trendyol, "ARCHIVED");
        Link(archivedListing.M, trendyol, MarketplaceVariantLinkHealth.NotFound);
        Link(archivedListing.L, trendyol, MarketplaceVariantLinkHealth.NotFound);
        // Broken on one Trendyol connection, holding on the other.
        var otherConnection = EdgeCard("EDGE06");
        Link(otherConnection.M, trendyol, MarketplaceVariantLinkHealth.NotFound);
        Link(otherConnection.M, trendyolSecond);
        Link(otherConnection.L, trendyol, MarketplaceVariantLinkHealth.NotFound);
        // Closed beyond stock (by the seller) and closed without a named reason.
        var closedBeyond = EdgeCard("EDGE07");
        Link(closedBeyond.M, trendyol, saleState: ListingSaleState.OffSale, saleReason: ListingSaleState.BySeller);
        Link(closedBeyond.L, hepsiburada, saleState: ListingSaleState.OffSale);
        // Closed for a stock of 0 only, and an unknown sale state: neither is "Satışa Kapalı".
        var closedForStock = EdgeCard("EDGE08");
        Link(closedForStock.M, trendyol, saleState: ListingSaleState.OffSale, saleReason: ListingSaleState.StockZero);
        Link(closedForStock.L, trendyol);
        // A refused listing whose card's update is on its way (waiting, not failed), and a refused publication.
        var resent = EdgeCard("EDGE09");
        Listing(Profile(resent.Card, trendyol, "UPDATE_QUEUED"), resent.M, ImportedApprovalStates.Rejected);
        Profile(resent.Card, hepsiburada, "REJECTED");
        // A publication waiting for approval, and a disabled one with a refused listing (never counts).
        var waiting = EdgeCard("EDGE10");
        Profile(waiting.Card, hepsiburada, "APPROVAL_PENDING");
        Listing(Profile(waiting.Card, trendyol, "LIVE", enabled: false), waiting.M, ImportedApprovalStates.Rejected);
        // Matched by a live listing alone (no link), and an UNKNOWN listing beside it.
        var listedOnly = EdgeCard("EDGE11");
        var listedProfile = Profile(listedOnly.Card, hepsiburada, "LIVE");
        Listing(listedProfile, listedOnly.M, "LIVE");
        Listing(listedProfile, listedOnly.L, "UNKNOWN");
        // A card without variants.
        db.Products.Add(new Product { Id = Guid.CreateVersion7(), TenantId = tenantId, Title = "Varyantsız Kart", Description = "Açıklama", BrandId = seed.Ravencia, VatRate = 10m, VatInclusion = "INCLUDED", Status = ProductStatus.Active, CreatedAt = now.AddDays(-2), UpdatedAt = now.AddDays(-2), Version = 1 });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var codec = new CursorCodec(new EphemeralDataProtectionProvider(), TimeProvider.System);
        Assert.True(await Service(db, codec).FamilyIdMatchesModelCodeRuleAsync(tenantId, CancellationToken.None));

        // Each edge is in the database as placed (every read scoped to this tenant).
        var links = await db.MarketplaceVariantLinks.Where(x => x.TenantId == tenantId).ToListAsync();
        var listings = await (from listing in db.ChannelListingVariants
                              join profile in db.ChannelListingProfiles on new { listing.TenantId, Id = listing.ProfileId } equals new { profile.TenantId, profile.Id }
                              where listing.TenantId == tenantId
                              select new { listing.VariantId, profile.ConnectionId, profile.Enabled, Listing = listing.ActualStatus, Profile = profile.ActualStatus }).ToListAsync();
        var storedProfiles = await db.ChannelListingProfiles.Where(x => x.TenantId == tenantId).ToListAsync();
        var storedCards = await db.Products.Where(x => x.TenantId == tenantId).ToDictionaryAsync(x => x.Id);
        bool HasLink(ProductVariant variant, PlatformConnection connection, Func<MarketplaceVariantLink, bool> test) => links.Any(link => link.VariantId == variant.Id && link.ConnectionId == connection.Id && test(link));
        var edges = new Dictionary<string, bool>
        {
            ["held listing over a link"] = listings.Any(listing => listing.Enabled && listing.VariantId == held.M.Id && listing.Listing == ImportedApprovalStates.Pending) && HasLink(held.M, trendyol, link => link.HealthStatus is null),
            ["broken link known there"] = HasLink(brokenKnown.M, trendyol, link => MarketplaceVariantLinkHealth.IsBroken(link.HealthStatus) && link.LastSeenAt is not null),
            ["broken link never known"] = HasLink(brokenUnknown.M, trendyol, link => MarketplaceVariantLinkHealth.IsBroken(link.HealthStatus) && link.LastSeenAt is null),
            ["broken link on an archived card"] = storedCards[archivedCard.Card.Id].Status == ProductStatus.Archived && HasLink(archivedCard.M, trendyol, link => MarketplaceVariantLinkHealth.IsBroken(link.HealthStatus)),
            ["broken link under an archived listing"] = storedProfiles.Any(profile => profile.ProductId == archivedListing.Card.Id && profile.ConnectionId == trendyol.Id && profile.Enabled && profile.ActualStatus == "ARCHIVED") && HasLink(archivedListing.M, trendyol, link => MarketplaceVariantLinkHealth.IsBroken(link.HealthStatus)),
            ["broken on one Trendyol connection, holding on the other"] = HasLink(otherConnection.M, trendyol, link => MarketplaceVariantLinkHealth.IsBroken(link.HealthStatus)) && HasLink(otherConnection.M, trendyolSecond, link => link.HealthStatus is null),
            ["closed beyond stock"] = HasLink(closedBeyond.M, trendyol, link => link.SaleState == ListingSaleState.OffSale && link.SaleStateReason == ListingSaleState.BySeller),
            ["closed without a reason"] = HasLink(closedBeyond.L, hepsiburada, link => link.SaleState == ListingSaleState.OffSale && link.SaleStateReason is null),
            ["closed for stock 0"] = HasLink(closedForStock.M, trendyol, link => link.SaleState == ListingSaleState.OffSale && link.SaleStateReason == ListingSaleState.StockZero),
            ["unknown sale state"] = HasLink(closedForStock.L, trendyol, link => link.SaleState is null),
            ["refused listing under an update on its way"] = listings.Any(listing => listing.Enabled && listing.VariantId == resent.M.Id && listing.Listing == ImportedApprovalStates.Rejected && ProductListingHealth.IsUpdateInFlight(listing.Profile)),
            ["disabled publication"] = listings.Any(listing => !listing.Enabled && listing.VariantId == waiting.M.Id),
            ["matched by a listing alone"] = listings.Any(listing => listing.VariantId == listedOnly.M.Id && listing.Listing == "LIVE") && !links.Any(link => link.VariantId == listedOnly.M.Id),
        };
        Assert.True(edges.Values.All(present => present), string.Join(", ", edges.Where(edge => !edge.Value).Select(edge => edge.Key)));

        // One service per path for the whole matrix: the count cache is keyed by every filter, so a combination never
        // reads another's count.
        var sqlService = Service(db, codec);
        var memoryService = Service(db, codec);
        string Position(string? cursor) => cursor is null ? "-" : codec.TryDecodeProduct(cursor, out var createdAt, out var id) ? $"{createdAt.UtcTicks}:{id}" : "invalid";
        var compared = 0;
        var outcomes = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
        // Stock and status narrow the cards before the platform filter, the same way on both paths: each is compared on
        // its own (paging is compared on the unnarrowed list, where the pages are most).
        (string? Stock, string? Status, int Limit)[] narrowings = [(null, null, 3), ("OUT", null, 50), ("LOW", null, 50), ("GAP", null, 50), ("OK", null, 50), (null, "ACTIVE", 50), (null, "ARCHIVED", 50)];
        foreach (var code in new[] { "TRENDYOL", "HEPSIBURADA", "PAZARAMA", "ALL" })
            foreach (var state in new[] { "ACTIVE", "PARTIAL", "PASSIVE", "NOT_FOUND", "OFF_SALE", "ERROR", "APPROVAL" })
                foreach (var (stock, status, limit) in narrowings)
                {
                    var platform = $"{code}:{state}";
                    string? sqlAfter = null, memoryAfter = null;
                    for (var page = 0; page < 30; page++)
                    {
                        var sql = await sqlService.ListProductsAsync(tenantId, limit, sqlAfter, status, null, platform, stock, null, sqlPaging: true, CancellationToken.None);
                        var memory = await memoryService.ListProductsAsync(tenantId, limit, memoryAfter, status, null, platform, stock, null, sqlPaging: false, CancellationToken.None);
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
        Assert.True(compared > 196, $"{compared} pages");
        // The seed is not trivial: the states split the models differently on each marketplace that has a connection.
        Assert.True(outcomes["TRENDYOL"].Count >= 5, string.Join(",", outcomes["TRENDYOL"]));
        Assert.True(outcomes["HEPSIBURADA"].Count >= 4, string.Join(",", outcomes["HEPSIBURADA"]));
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

        // Relinked (the scan): the SQL path again. KATALOG-14 phase D5a: KENE01 gets a second Lavian card; the code is
        // still ambiguous, but each brand's cards of it hold a model of their own brand, so the two Lavian cards are one
        // row in SQL as in memory: the SQL path stays.
        await ProductModelScan.RunTenantAsync(db, seed.TenantId, DateTimeOffset.UtcNow, CancellationToken.None);
        Assert.True(await service.FamilyIdMatchesModelCodeRuleAsync(seed.TenantId, CancellationToken.None));
        AddCard(db, seed.TenantId, seed.Lavian, "KENE01", "Gri", "Kadın Gri Kot Etek", DateTimeOffset.UtcNow.AddHours(-1));
        await db.SaveChangesAsync();
        Assert.True(await service.FamilyIdMatchesModelCodeRuleAsync(seed.TenantId, CancellationToken.None));
        await AssertSameListsAsync(db, seed.TenantId);
    }
}
