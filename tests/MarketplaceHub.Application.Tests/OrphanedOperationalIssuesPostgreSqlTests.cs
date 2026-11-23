using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// Y5/O7 (scan 23 Nov 2026): the owner deleted every product and imported again under new ids; about 1,500 issues keyed by
// the old variants (Web Color, PttAVM links), stale code clashes, stored imports and the ended observe period stayed open
// beside the real work. A product delete now resolves its issues, the daily pass resolves every issue whose subject is gone
// and leaves those whose subject still exists or cannot be read, and a re-detection under the new ids opens its own issue.
[Collection("postgres-integration")]
public sealed class OrphanedOperationalIssuesPostgreSqlTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    private sealed record Seed(Tenant Tenant, Guid CategoryId, PlatformConnection Ptt, PlatformConnection N11, PlatformConnection Trendyol);

    private static async Task<Seed> SeedAsync(AppDbContext db)
    {
        var now = DateTimeOffset.UtcNow;
        var tenant = new Tenant { Id = Guid.CreateVersion7(), Code = $"orphans-{Guid.NewGuid():N}", DisplayName = "orphans", Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = now, UpdatedAt = now, Version = 1 };
        var seed = new Seed(tenant, Guid.CreateVersion7(), Connection(tenant, "PTTAVM"), Connection(tenant, "N11"), Connection(tenant, "TRENDYOL"));
        db.Tenants.Add(tenant);
        db.Categories.Add(new Category { Id = seed.CategoryId, TenantId = tenant.Id, Name = "Etek", NormalizedName = "ETEK", Path = "Etek", IsLeaf = true, IsActive = true, CreatedAt = now, UpdatedAt = now, Version = 1 });
        db.PlatformConnections.AddRange(seed.Ptt, seed.N11, seed.Trendyol);
        await db.SaveChangesAsync();
        return seed;
    }

    private static PlatformConnection Connection(Tenant tenant, string platform) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = tenant.Id,
        PublicId = Guid.CreateVersion7(),
        PlatformCode = platform,
        Environment = "PRODUCTION",
        DisplayName = platform,
        ExternalStoreId = $"store-{Guid.NewGuid():N}",
        Status = "ACTIVE",
        ApiVersion = "V1",
        SettingsJson = "{}"
    };

    // A card with two sizes; the variants' stock codes are "<code>-M" and "<code>-L".
    private static (Product Card, ProductVariant M, ProductVariant L) AddCard(AppDbContext db, Seed seed, string code)
    {
        var now = DateTimeOffset.UtcNow;
        var card = new Product { Id = Guid.CreateVersion7(), TenantId = seed.Tenant.Id, Title = $"Kart {code}", Description = "x", CategoryId = seed.CategoryId, VatRate = 10m, VatInclusion = "INCLUDED", DeliveryDays = 1, Status = ProductStatus.Draft, CreatedAt = now, UpdatedAt = now, Version = 1 };
        db.Products.Add(card);
        ProductVariant Variant(string size, int order) => new() { Id = Guid.CreateVersion7(), TenantId = seed.Tenant.Id, ProductId = card.Id, Sku = $"{code}-{size}", SkuNormalized = $"{code}-{size}", Barcode = $"{code}{order}", BarcodeNormalized = $"{code}{order}", OptionSignature = $"Beden: {size}", DefaultListPrice = 900m, DefaultSalePrice = 600m, Status = ProductStatus.Draft, SortOrder = order, CreatedAt = now, UpdatedAt = now, Version = 1 };
        var m = Variant("M", 0);
        var l = Variant("L", 1);
        db.ProductVariants.AddRange(m, l);
        return (card, m, l);
    }

    private static OperationalIssue Issue(Seed seed, string key, string code) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = seed.Tenant.Id,
        DedupeKey = key,
        Code = code,
        Summary = "test",
        Status = IssueStatus.Open,
        FirstSeenAt = DateTimeOffset.UtcNow.AddDays(-6),
        LastSeenAt = DateTimeOffset.UtcNow.AddDays(-6),
        OccurrenceCount = 1
    };

    private static string WebColorKey(Product card, ProductVariant variant) => $"product-web-color-mapping:{card.Id}:{variant.Id}:{Guid.CreateVersion7()}";

    private CatalogService Service(AppDbContext db) =>
        new(db, new CursorCodec(new EphemeralDataProtectionProvider(), TimeProvider.System), new ConfigurationBuilder().Build(), fixture.TimeProvider, new MemoryCache(new MemoryCacheOptions()));

    private static async Task<Dictionary<string, OperationalIssue>> IssuesAsync(AppDbContext db, Guid tenantId) =>
        await db.OperationalIssues.AsNoTracking().Where(x => x.TenantId == tenantId).ToDictionaryAsync(x => x.DedupeKey);

    [PostgreSqlFact]
    public async Task DeletingAProductResolvesTheIssuesOfItsVariantsAndLeavesTheOtherCardsOpen()
    {
        Seed seed;
        Product deleted;
        string[] goneKeys, liveKeys;
        await using (var setup = fixture.CreateContext())
        {
            seed = await SeedAsync(setup);
            var gone = AddCard(setup, seed, "DEL1");
            var kept = AddCard(setup, seed, "KEEP1");
            deleted = gone.Card;
            goneKeys =
            [
                WebColorKey(gone.Card, gone.M),
                $"product-attribute-value:{gone.Card.Id}:{gone.L.Id}:{Guid.CreateVersion7()}:PAMUK",
                $"product-mapping-unmatched:{seed.Ptt.Id}:{gone.M.Id:N}",
                $"stock-projection-offer-invalid:{seed.Trendyol.Id:N}:{gone.L.Id:N}"
            ];
            liveKeys = [WebColorKey(kept.Card, kept.M), $"product-mapping-unmatched:{seed.Ptt.Id}:{kept.L.Id:N}", $"stock-projection-offer-invalid:{seed.Trendyol.Id:N}:{kept.M.Id:N}"];
            await setup.SaveChangesAsync();
            setup.OperationalIssues.AddRange(goneKeys.Concat(liveKeys).Select(key => Issue(seed, key, key.StartsWith("product-web", StringComparison.Ordinal) ? "PRODUCT_WEBCOLOR_MAPPING_REQUIRED" : "X")));
            await setup.SaveChangesAsync();
        }

        await using (var db = fixture.CreateContext())
        {
            var result = await Service(db).DeleteProductAsync(seed.Tenant.Id, deleted.Id, deleted.Version, CancellationToken.None);
            Assert.True(result.Succeeded, result.Error?.Message);
        }

        await using var check = fixture.CreateContext();
        var issues = await IssuesAsync(check, seed.Tenant.Id);
        Assert.All(goneKeys, key =>
        {
            Assert.Equal(IssueStatus.Resolved, issues[key].Status);
            Assert.NotNull(issues[key].ResolvedAt);
            Assert.EndsWith("panel varyantı artık yok.", issues[key].Summary, StringComparison.Ordinal);
        });
        Assert.All(liveKeys, key => Assert.Equal(IssueStatus.Open, issues[key].Status));
    }

    [PostgreSqlFact]
    public async Task ThePassResolvesIssuesWhoseSubjectIsGone_LeavesLiveAndUnreadableSubjectsOpen_AndIsIdempotent()
    {
        Seed seed;
        var closes = new List<string>();
        var stays = new List<string>();
        await using (var setup = fixture.CreateContext())
        {
            seed = await SeedAsync(setup);
            var live = AddCard(setup, seed, "LIVE1");
            var other = AddCard(setup, seed, "LIVE2");
            var deletedConnection = Connection(seed.Tenant, "PAZARAMA");
            deletedConnection.Status = "DELETED";
            setup.PlatformConnections.Add(deletedConnection);
            var at = DateTimeOffset.UtcNow;
            // Linked since: an N11 variant, an N11 model product, a PttAVM variant, a Trendyol product whose import failed.
            setup.MarketplaceVariantLinks.Add(new MarketplaceVariantLink { Id = Guid.CreateVersion7(), TenantId = seed.Tenant.Id, ConnectionId = seed.N11.Id, VariantId = live.M.Id, ExternalId = "N11-LINKED", Version = 1 });
            setup.MarketplaceVariantLinks.Add(new MarketplaceVariantLink { Id = Guid.CreateVersion7(), TenantId = seed.Tenant.Id, ConnectionId = seed.Ptt.Id, VariantId = live.L.Id, ExternalId = "LIVE1-L", Version = 1 });
            setup.MarketplaceProductLinks.Add(new MarketplaceProductLink { Id = Guid.CreateVersion7(), TenantId = seed.Tenant.Id, ConnectionId = seed.N11.Id, ProductId = live.Card.Id, ExternalId = "main:MODEL-1", Version = 1 });
            setup.MarketplaceProductLinks.Add(new MarketplaceProductLink { Id = Guid.CreateVersion7(), TenantId = seed.Tenant.Id, ConnectionId = seed.Trendyol.Id, ProductId = other.Card.Id, ExternalId = "861751633", Version = 1 });
            // A variant id that also names a product link: an N11 variant key is not closed by a product link.
            setup.MarketplaceProductLinks.Add(new MarketplaceProductLink { Id = Guid.CreateVersion7(), TenantId = seed.Tenant.Id, ConnectionId = seed.N11.Id, ProductId = other.Card.Id, ExternalId = "N11-SAME-ID", Version = 1 });
            // The listing check of N11: three runs completed (observe-only is two); of PttAVM: two (still observing).
            setup.SyncCursors.Add(new SyncCursor { Id = Guid.CreateVersion7(), TenantId = seed.Tenant.Id, ConnectionId = seed.N11.Id, ResourceType = ListingVerificationPolicy.StateCursorResource, OpaqueCursor = """{"runJobId":null,"completedRuns":3}""", Version = 1 });
            setup.SyncCursors.Add(new SyncCursor { Id = Guid.CreateVersion7(), TenantId = seed.Tenant.Id, ConnectionId = seed.Ptt.Id, ResourceType = ListingVerificationPolicy.StateCursorResource, OpaqueCursor = """{"runJobId":null,"completedRuns":2}""", Version = 1 });
            await setup.SaveChangesAsync();

            var goneVariant = Guid.CreateVersion7();
            var goneProduct = Guid.CreateVersion7();
            closes.AddRange(
            [
                $"product-web-color-mapping:{goneProduct}:{goneVariant}:{Guid.CreateVersion7()}",
                $"product-web-color-value:{goneProduct}:{goneVariant}:KIRMIZI",
                $"product-mapping-unmatched:{seed.Ptt.Id}:{goneVariant:N}",
                $"product-mapping-unmatched:{seed.Ptt.Id}:{live.L.Id:N}",
                $"stock-projection-offer-invalid:{seed.Trendyol.Id:N}:{goneVariant:N}",
                $"product-sync-model-code-conflict:{seed.Trendyol.Id}:{goneVariant:N}",
                $"product-mapping-unmatched:{seed.N11.Id}:N11-LINKED",
                $"product-mapping-unmatched:{seed.N11.Id}:main:MODEL-1",
                $"product-sync-import:{seed.Trendyol.Id}:861751633",
                $"listing-verify-observe:{seed.N11.Id}",
                $"product-mapping-unmatched:{deletedConnection.Id}:PZ-1",
                $"product-sync-barcode-conflict:{seed.N11.Id}:LIVE1-M"
            ]);
            stays.AddRange(
            [
                WebColorKey(live.Card, live.M),
                $"product-mapping-unmatched:{seed.Ptt.Id}:{live.M.Id:N}",
                $"stock-projection-offer-invalid:{seed.Trendyol.Id:N}:{other.M.Id:N}",
                $"product-mapping-unmatched:{seed.N11.Id}:N11-NOT-LINKED",
                $"product-mapping-unmatched:{seed.N11.Id}:N11-SAME-ID",
                $"product-mapping-unmatched:{seed.Ptt.Id}:ptt:PTT-NOT-LINKED",
                $"product-sync-import:{seed.Trendyol.Id}:999999999",
                $"product-sync-import:{seed.Trendyol.Id}:missing:abcdef",
                $"listing-verify-observe:{seed.Ptt.Id}",
                // Unreadable subjects: no uuid where the writer puts one; a 32-hex id on a connection that is not PttAVM.
                "product-web-color-mapping:not-a-guid:also-not:x",
                $"product-mapping-unmatched:{seed.N11.Id}:{goneVariant:N}",
                // Order and stock issues are not catalog subjects.
                $"order-line-variant-unmatched:{seed.Trendyol.Id:N}:LIVE9",
                $"stock-order-consumption:{Guid.CreateVersion7():N}"
            ]);
            string Code(string key) => key.StartsWith("product-sync-import:", StringComparison.Ordinal) ? "PRODUCT_IMPORT_FAILED"
                : key.StartsWith("listing-verify-observe:", StringComparison.Ordinal) ? "LISTING_VERIFY_OBSERVE"
                : key.StartsWith("product-sync-barcode-conflict:", StringComparison.Ordinal) ? OrphanedOperationalIssues.BarcodeConflictCode
                : key.StartsWith("product-mapping-unmatched:", StringComparison.Ordinal) ? "PRODUCT_MAPPING_UNMATCHED"
                : key.StartsWith("product-web-color", StringComparison.Ordinal) ? "PRODUCT_WEBCOLOR_MAPPING_REQUIRED" : "OTHER";
            setup.OperationalIssues.AddRange(closes.Concat(stays).Select(key => Issue(seed, key, Code(key))));
            // A code two cards still hold stays a clash: LIVE2-M is the stock code of one card and the barcode of another.
            var clash = AddCard(setup, seed, "CLASH");
            clash.M.BarcodeNormalized = "LIVE2-M";
            stays.Add($"product-sync-barcode-conflict:{seed.N11.Id}:LIVE2-M");
            setup.OperationalIssues.Add(Issue(seed, stays[^1], OrphanedOperationalIssues.BarcodeConflictCode));
            await setup.SaveChangesAsync();
        }

        await using (var db = fixture.CreateContext())
        {
            var counts = await OrphanedOperationalIssues.ResolveAsync(db, seed.Tenant.Id, DateTimeOffset.UtcNow, batchSize: 500, maxBatches: 5, CancellationToken.None);
            Assert.Equal(closes.Count, counts.Values.Sum());
            Assert.Equal(1, counts[OrphanedOperationalIssues.BarcodeConflictCode]);
            Assert.Equal(1, counts["LISTING_VERIFY_OBSERVE"]);
            Assert.Equal(1, counts["PRODUCT_IMPORT_FAILED"]);
        }

        await using (var check = fixture.CreateContext())
        {
            var issues = await IssuesAsync(check, seed.Tenant.Id);
            Assert.All(closes, key => Assert.True(issues[key].Status == IssueStatus.Resolved && issues[key].ResolvedAt is not null && issues[key].Summary.Contains("Otomatik kapatıldı", StringComparison.Ordinal), key));
            Assert.All(stays, key => Assert.True(issues[key].Status == IssueStatus.Open && issues[key].Summary == "test", key));
        }

        // A second run finds nothing.
        await using (var again = fixture.CreateContext())
            Assert.Empty(await OrphanedOperationalIssues.ResolveAsync(again, seed.Tenant.Id, DateTimeOffset.UtcNow, batchSize: 500, maxBatches: 5, CancellationToken.None));
    }

    [PostgreSqlFact]
    public async Task ThePassIsBoundedByItsBatches()
    {
        Seed seed;
        await using (var setup = fixture.CreateContext())
        {
            seed = await SeedAsync(setup);
            setup.OperationalIssues.AddRange(Enumerable.Range(0, 5).Select(_ => Issue(seed, $"product-web-color-mapping:{Guid.CreateVersion7()}:{Guid.CreateVersion7()}:{Guid.CreateVersion7()}", "PRODUCT_WEBCOLOR_MAPPING_REQUIRED")));
            await setup.SaveChangesAsync();
        }

        await using (var db = fixture.CreateContext())
        {
            Assert.Equal(4, (await OrphanedOperationalIssues.ResolveAsync(db, seed.Tenant.Id, DateTimeOffset.UtcNow, batchSize: 2, maxBatches: 2, CancellationToken.None))["PRODUCT_WEBCOLOR_MAPPING_REQUIRED"]);
            Assert.Equal(1, (await OrphanedOperationalIssues.ResolveAsync(db, seed.Tenant.Id, DateTimeOffset.UtcNow, batchSize: 2, maxBatches: 2, CancellationToken.None))["PRODUCT_WEBCOLOR_MAPPING_REQUIRED"]);
        }
        await using var check = fixture.CreateContext();
        Assert.Equal(0, await check.OperationalIssues.CountAsync(x => x.TenantId == seed.Tenant.Id && x.Status == IssueStatus.Open));
    }

    // The re-import after the delete names the new variants: their issues open under their own keys, the resolved ones of
    // the deleted variants stay resolved, and the next pass leaves the new ones open (their subjects exist).
    [PostgreSqlFact]
    public async Task AReDetectionUnderTheNewIdsOpensItsOwnIssueAndDoesNotReopenTheDeletedOnes()
    {
        Seed seed;
        Product oldCard;
        string oldKey, oldPttKey;
        await using (var setup = fixture.CreateContext())
        {
            seed = await SeedAsync(setup);
            var old = AddCard(setup, seed, "REIMP");
            oldCard = old.Card;
            oldKey = WebColorKey(old.Card, old.M);
            oldPttKey = $"product-mapping-unmatched:{seed.Ptt.Id}:{old.M.Id:N}";
            await setup.SaveChangesAsync();
            setup.OperationalIssues.AddRange(Issue(seed, oldKey, "PRODUCT_WEBCOLOR_MAPPING_REQUIRED"), Issue(seed, oldPttKey, "PRODUCT_MAPPING_UNMATCHED"));
            await setup.SaveChangesAsync();
        }
        await using (var db = fixture.CreateContext())
            Assert.True((await Service(db).DeleteProductAsync(seed.Tenant.Id, oldCard.Id, oldCard.Version, CancellationToken.None)).Succeeded);

        string newKey, newPttKey;
        await using (var import = fixture.CreateContext())
        {
            // The same codes come back as a new card with new ids, and the import finds the same problems again.
            var fresh = AddCard(import, seed, "REIMP");
            await import.SaveChangesAsync();
            newKey = WebColorKey(fresh.Card, fresh.M);
            newPttKey = $"product-mapping-unmatched:{seed.Ptt.Id}:{fresh.M.Id:N}";
            var recorder = new IssueRecorder(import);
            await recorder.RecordAsync(seed.Tenant.Id, newKey, "PRODUCT_WEBCOLOR_MAPPING_REQUIRED", "yeni", DateTimeOffset.UtcNow, CancellationToken.None);
            await recorder.RecordAsync(seed.Tenant.Id, newPttKey, "PRODUCT_MAPPING_UNMATCHED", "yeni", DateTimeOffset.UtcNow, CancellationToken.None);
            await import.SaveChangesAsync();
            Assert.Empty(await OrphanedOperationalIssues.ResolveAsync(import, seed.Tenant.Id, DateTimeOffset.UtcNow, batchSize: 500, maxBatches: 5, CancellationToken.None));
        }

        await using var check = fixture.CreateContext();
        var issues = await IssuesAsync(check, seed.Tenant.Id);
        Assert.Equal(IssueStatus.Resolved, issues[oldKey].Status);
        Assert.Equal(IssueStatus.Resolved, issues[oldPttKey].Status);
        Assert.Equal(IssueStatus.Open, issues[newKey].Status);
        Assert.Equal(IssueStatus.Open, issues[newPttKey].Status);
    }
}
