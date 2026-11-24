using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// Y5/O7 (scan 23 Nov 2026, rewritten after the review of 23 Nov): the owner deleted every product and imported again under
// new ids; about 1,350 issues keyed by the old variants stayed open beside the real work. An issue is resolved only when
// its subject (a variant, a connection) is gone, or when the writer's own guard no longer holds; one rule per issue code,
// every other code untouched. The review found real warnings closed by the first version: a code clash over a listing's
// whole code set, a listing linked to the wrong panel variant, a failed update of a linked product, PttAVM's warning on a
// variant that keeps its own link. Each has a test below in which its subject and its condition still hold, and the pass
// leaves it open. The prediction query (OrphanedOperationalIssues.PredictSql) is checked against the pass on one fixture.
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

    private static ChannelOffer Offer(Seed seed, PlatformConnection connection, ProductVariant variant, decimal salePrice, bool? followsPanelPrice = null, string currency = "TRY") => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = seed.Tenant.Id,
        ConnectionId = connection.Id,
        VariantId = variant.Id,
        ListPrice = salePrice,
        SalePrice = salePrice,
        Currency = currency,
        VatRate = 10m,
        VatInclusion = "INCLUDED",
        RoundingMode = "HALF_EVEN",
        Status = "ACTIVE",
        FollowsPanelPrice = followsPanelPrice,
        PriceVersion = 1,
        Version = 1
    };

    private static OperationalIssue Issue(Seed seed, string key, string code, string summary = "test") => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = seed.Tenant.Id,
        DedupeKey = key,
        Code = code,
        Summary = summary,
        Status = IssueStatus.Open,
        FirstSeenAt = DateTimeOffset.UtcNow.AddDays(-6),
        LastSeenAt = DateTimeOffset.UtcNow.AddDays(-6),
        OccurrenceCount = 1
    };

    // The code each writer gives its key.
    private static string CodeOf(string key) =>
        key.StartsWith("product-sync-import:", StringComparison.Ordinal) ? "PRODUCT_IMPORT_FAILED"
        : key.StartsWith("listing-verify-observe:", StringComparison.Ordinal) ? "LISTING_VERIFY_OBSERVE"
        : key.StartsWith("product-sync-barcode-conflict:", StringComparison.Ordinal) ? "PRODUCT_BARCODE_CONFLICT"
        : key.StartsWith("product-mapping-unmatched:", StringComparison.Ordinal) ? "PRODUCT_MAPPING_UNMATCHED"
        : key.StartsWith("pttavm-product-link:", StringComparison.Ordinal) ? "PRODUCT_MAPPING_UNMATCHED"
        : key.StartsWith("pttavm-product-code:", StringComparison.Ordinal) ? "PTTAVM_PRODUCT_CODE_NOT_MODEL"
        : key.StartsWith("product-web-color-value:", StringComparison.Ordinal) ? "PRODUCT_WEBCOLOR_VALUE_UNMAPPED"
        : key.StartsWith("product-web-color", StringComparison.Ordinal) ? "PRODUCT_WEBCOLOR_MAPPING_REQUIRED"
        : key.StartsWith("product-attribute-value:", StringComparison.Ordinal) ? "PRODUCT_ATTRIBUTE_VALUE_UNMAPPED"
        : key.StartsWith("stock-projection-offer-invalid:", StringComparison.Ordinal) ? "CHANNEL_OFFER_INVALID"
        : key.StartsWith("product-sync-model-code-conflict:", StringComparison.Ordinal) ? ModelCodeUniquenessPolicy.ChangeConflictCode
        : key.StartsWith("order-line-variant-unmatched:", StringComparison.Ordinal) ? "ORDER_LINE_VARIANT_UNMATCHED"
        : key.StartsWith("stock-order-consumption:", StringComparison.Ordinal) ? "STOCK_ORDER_CONSUMPTION_BLOCKED" : "OTHER";

    // PttAVM's own summary for a variant whose colour or size it does not list (MapPttAvmProduct).
    private const string PttColourMissing = "PttAVM'de bu renk/beden yok: MURDUM (X-M); ürün X · Kart.";

    private static string WebColorKey(Product card, ProductVariant variant) => $"product-web-color-mapping:{card.Id}:{variant.Id}:{Guid.CreateVersion7()}";

    private CatalogService Service(AppDbContext db) =>
        new(db, new CursorCodec(new EphemeralDataProtectionProvider(), TimeProvider.System), new ConfigurationBuilder().Build(), fixture.TimeProvider, new MemoryCache(new MemoryCacheOptions()));

    private static Task<IReadOnlyDictionary<(string Code, string Reason), int>> PassAsync(AppDbContext db, Guid tenantId, int batchSize = 500, int maxBatches = 5) =>
        OrphanedOperationalIssues.ResolveAsync(db, tenantId, DateTimeOffset.UtcNow, batchSize, maxBatches, OrphanedOperationalIssues.MaintenanceActor, CancellationToken.None);

    // The prediction query, narrowed to one tenant: what it says the pass resolves, by code and reason.
    private static async Task<Dictionary<(string Code, string Reason), int>> PredictAsync(AppDbContext db, Guid tenantId)
    {
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open) await connection.OpenAsync();
        await using var command = new NpgsqlCommand(OrphanedOperationalIssues.PredictSql(500 * 5, $"""i."TenantId" = '{tenantId}'"""), connection);
        await using var reader = await command.ExecuteReaderAsync();
        var predicted = new Dictionary<(string Code, string Reason), int>();
        while (await reader.ReadAsync())
        {
            if (reader.GetString(0) != "resolves") continue;
            var key = (reader.GetString(1), reader.GetString(2));
            predicted[key] = predicted.GetValueOrDefault(key) + (int)reader.GetInt64(4);
        }
        return predicted;
    }

    private static async Task<Dictionary<string, OperationalIssue>> IssuesAsync(AppDbContext db, Guid tenantId) =>
        await db.OperationalIssues.AsNoTracking().Where(x => x.TenantId == tenantId).ToDictionaryAsync(x => x.DedupeKey);

    // Seeds the issues, runs the pass and says the given ones stayed open, untouched.
    private async Task AssertThePassLeavesOpenAsync(Seed seed, params (string Key, string Code, string Summary)[] issues)
    {
        await using (var setup = fixture.CreateContext())
        {
            setup.OperationalIssues.AddRange(issues.Select(issue => Issue(seed, issue.Key, issue.Code, issue.Summary)));
            await setup.SaveChangesAsync();
        }
        await using (var db = fixture.CreateContext())
        {
            Assert.Empty(await PredictAsync(db, seed.Tenant.Id));
            Assert.Empty(await PassAsync(db, seed.Tenant.Id));
        }
        await using var check = fixture.CreateContext();
        var stored = await IssuesAsync(check, seed.Tenant.Id);
        Assert.All(issues, issue => Assert.True(stored[issue.Key].Status == IssueStatus.Open && stored[issue.Key].Summary == issue.Summary && stored[issue.Key].ResolvedAt is null, issue.Key));
    }

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
                $"stock-projection-offer-invalid:{seed.Trendyol.Id:N}:{gone.L.Id:N}"
            ];
            liveKeys = [WebColorKey(kept.Card, kept.M), $"product-mapping-unmatched:{seed.Ptt.Id}:{kept.L.Id:N}", $"stock-projection-offer-invalid:{seed.Trendyol.Id:N}:{kept.M.Id:N}"];
            setup.ChannelOffers.Add(Offer(seed, seed.Trendyol, kept.M, salePrice: 0m, followsPanelPrice: false));
            await setup.SaveChangesAsync();
            setup.OperationalIssues.AddRange(goneKeys.Concat(liveKeys).Select(key => Issue(seed, key, CodeOf(key), key.Contains(seed.Ptt.Id.ToString(), StringComparison.Ordinal) ? PttColourMissing : "test")));
            var pttGone = $"product-mapping-unmatched:{seed.Ptt.Id}:{gone.M.Id:N}";
            setup.OperationalIssues.Add(Issue(seed, pttGone, "PRODUCT_MAPPING_UNMATCHED", PttColourMissing));
            goneKeys = [.. goneKeys, pttGone];
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
            Assert.EndsWith($"({OrphanedOperationalIssues.DeleteActor}): konu artık yok — kaydın panel varyantı silinmiş.", issues[key].Summary, StringComparison.Ordinal);
        });
        Assert.All(liveKeys, key => Assert.Equal(IssueStatus.Open, issues[key].Status));
    }

    [PostgreSqlFact]
    public async Task ThePassResolvesOnlyWhatItsRulesProve_ThePredictionCountsTheSame_AndASecondRunFindsNothing()
    {
        Seed seed;
        var closes = new List<string>();
        var stays = new List<string>();
        await using (var setup = fixture.CreateContext())
        {
            seed = await SeedAsync(setup);
            var live = AddCard(setup, seed, "LIVE1");
            var other = AddCard(setup, seed, "LIVE2");
            // The offer its Trendyol issue names: still at 0 with its own price kept (Özel Fiyat), so the send still refuses it.
            setup.ChannelOffers.Add(Offer(seed, seed.Trendyol, other.M, salePrice: 0m, followsPanelPrice: false));
            var deletedConnection = Connection(seed.Tenant, "PAZARAMA");
            deletedConnection.Status = "DELETED";
            var pazarama = Connection(seed.Tenant, "PAZARAMA");
            setup.PlatformConnections.AddRange(deletedConnection, pazarama);
            // An N11 model product and a Pazarama group linked since their "several cards" warnings.
            setup.MarketplaceProductLinks.Add(new MarketplaceProductLink { Id = Guid.CreateVersion7(), TenantId = seed.Tenant.Id, ConnectionId = seed.N11.Id, ProductId = live.Card.Id, ExternalId = "main:MODEL-1", Version = 1 });
            setup.MarketplaceProductLinks.Add(new MarketplaceProductLink { Id = Guid.CreateVersion7(), TenantId = seed.Tenant.Id, ConnectionId = pazarama.Id, ProductId = other.Card.Id, ExternalId = "group:G-1", Version = 1 });
            // The listing check of N11: three runs completed (observe-only is two); of PttAVM: two (its notice still describes the last one);
            // of Trendyol: five, but Trendyol has no observe period (a notice there is not this rule's).
            setup.SyncCursors.Add(new SyncCursor { Id = Guid.CreateVersion7(), TenantId = seed.Tenant.Id, ConnectionId = seed.N11.Id, ResourceType = ListingVerificationPolicy.StateCursorResource, OpaqueCursor = """{"runJobId":null,"completedRuns":3}""", Version = 1 });
            setup.SyncCursors.Add(new SyncCursor { Id = Guid.CreateVersion7(), TenantId = seed.Tenant.Id, ConnectionId = seed.Ptt.Id, ResourceType = ListingVerificationPolicy.StateCursorResource, OpaqueCursor = """{"runJobId":null,"completedRuns":2}""", Version = 1 });
            setup.SyncCursors.Add(new SyncCursor { Id = Guid.CreateVersion7(), TenantId = seed.Tenant.Id, ConnectionId = seed.Trendyol.Id, ResourceType = ListingVerificationPolicy.StateCursorResource, OpaqueCursor = """{"runJobId":null,"completedRuns":5}""", Version = 1 });
            await setup.SaveChangesAsync();

            var goneVariant = Guid.CreateVersion7();
            var goneProduct = Guid.CreateVersion7();
            var neverStored = Guid.CreateVersion7();
            closes.AddRange(
            [
                $"product-web-color-mapping:{goneProduct}:{goneVariant}:{Guid.CreateVersion7()}",
                $"product-web-color-value:{goneProduct}:{goneVariant}:KIRMIZI",
                $"product-attribute-value:{goneProduct}:{goneVariant}:{Guid.CreateVersion7()}:PAMUK",
                $"product-mapping-unmatched:{seed.Ptt.Id}:{goneVariant:N}",
                $"stock-projection-offer-invalid:{seed.Trendyol.Id:N}:{goneVariant:N}",
                $"product-sync-model-code-conflict:{seed.Trendyol.Id}:{goneVariant:N}",
                $"product-mapping-unmatched:{seed.N11.Id}:main:MODEL-1",
                $"product-mapping-unmatched:{pazarama.Id}:group:G-1",
                $"listing-verify-observe:{seed.N11.Id}",
                $"product-mapping-unmatched:{deletedConnection.Id}:PZ-1",
                $"pttavm-product-link:{deletedConnection.Id}:PZ-3",
                $"product-sync-barcode-conflict:{deletedConnection.Id}:PZ-2",
                $"product-sync-import:{deletedConnection.Id}:PZ-4",
                $"listing-verify-observe:{deletedConnection.Id}",
                $"pttavm-product-code:{deletedConnection.Id}:PZ-5",
                // A connection id no connection row holds any more.
                $"product-sync-import:{neverStored}:PZ-6"
            ]);
            stays.AddRange(
            [
                WebColorKey(live.Card, live.M),
                $"product-mapping-unmatched:{seed.Ptt.Id}:{live.M.Id:N}",
                $"stock-projection-offer-invalid:{seed.Trendyol.Id:N}:{other.M.Id:N}",
                $"product-mapping-unmatched:{seed.N11.Id}:N11-NOT-LINKED",
                $"product-mapping-unmatched:{seed.N11.Id}:main:MODEL-NOT-LINKED",
                $"product-mapping-unmatched:{seed.Ptt.Id}:ptt:PTT-NOT-LINKED",
                $"pttavm-product-link:{seed.Ptt.Id}:PTT-1",
                $"pttavm-product-code:{seed.Ptt.Id}:PTT-2",
                // A failed import is judged by the import alone (it resolves it when it writes the product again).
                $"product-sync-import:{seed.Trendyol.Id}:999999999",
                $"listing-verify-observe:{seed.Ptt.Id}",
                $"listing-verify-observe:{seed.Trendyol.Id}",
                // Unreadable subjects: no uuid where the writer puts one; a 32-hex id on a connection that is not PttAVM.
                "product-web-color-mapping:not-a-guid:also-not:x",
                $"product-mapping-unmatched:{seed.N11.Id}:{goneVariant:N}",
                // Order and stock issues are not catalog subjects.
                $"order-line-variant-unmatched:{seed.Trendyol.Id:N}:LIVE9",
                $"stock-order-consumption:{Guid.CreateVersion7():N}"
            ]);
            string Summary(string key) => key.StartsWith("product-mapping-unmatched:", StringComparison.Ordinal) && key.Contains(seed.Ptt.Id.ToString(), StringComparison.Ordinal) && !key.Contains(":ptt:", StringComparison.Ordinal) ? PttColourMissing : "test";
            setup.OperationalIssues.AddRange(closes.Concat(stays).Select(key => Issue(seed, key, CodeOf(key), Summary(key))));
            // Only the code each rule names: issues of other codes, or of a rule's code under another rule's key, stay open.
            foreach (var (key, code, summary) in new[]
            {
                ($"stock-projection-offer-invalid:{seed.Trendyol.Id:N}:{Guid.CreateVersion7():N}", "STOCK_ORDER_CONSUMPTION_BLOCKED", "test"),
                ($"product-web-color-mapping:{goneProduct}:{Guid.CreateVersion7()}:x", "ORDER_LINE_VARIANT_UNMATCHED", "test"),
                ($"product-web-color-value:{goneProduct}:{Guid.CreateVersion7()}:x", "PRODUCT_WEBCOLOR_MAPPING_REQUIRED", "test"),
                ($"product-mapping-unmatched:{seed.Ptt.Id}:{Guid.CreateVersion7():N}", "INVOICE_ADJUSTMENT_REQUIRED", PttColourMissing),
                // A 32-hex PttAVM key whose summary is not one of PttAVM's variant warnings.
                ($"product-mapping-unmatched:{seed.Ptt.Id}:{Guid.CreateVersion7():N}", "PRODUCT_MAPPING_UNMATCHED", "test"),
                ($"product-mapping-unmatched:{deletedConnection.Id}:PZ-9", "HEPSIBURADA_RETURN_ORDER_OUTSIDE_HISTORY", "test"),
                ($"listing-verify-observe:{deletedConnection.Id}:x", "SOME_NEW_CODE", "test"),
                ($"product-sync-link-conflict:{deletedConnection.Id}:PZ-8", "PRODUCT_LINK_CONFLICT", "test")
            })
            {
                stays.Add(key);
                setup.OperationalIssues.Add(Issue(seed, key, code, summary));
            }
            await setup.SaveChangesAsync();
        }

        await using (var db = fixture.CreateContext())
        {
            var predicted = await PredictAsync(db, seed.Tenant.Id);
            var counts = await PassAsync(db, seed.Tenant.Id);
            // Written by hand from the issues above, not from the rules (review 24 Nov 2026): what must close, by code and
            // reason. The pass and the prediction query are each held to it.
            const string Gone = OrphanedOperationalIssues.SubjectGone, Cleared = OrphanedOperationalIssues.ConditionCleared;
            var expected = new Dictionary<(string Code, string Reason), int>
            {
                [("PRODUCT_WEBCOLOR_MAPPING_REQUIRED", Gone)] = 1,
                [("PRODUCT_WEBCOLOR_VALUE_UNMAPPED", Gone)] = 1,
                [("PRODUCT_ATTRIBUTE_VALUE_UNMAPPED", Gone)] = 1,
                [("CHANNEL_OFFER_INVALID", Gone)] = 1,
                [("MODEL_CODE_CHANGE_IN_USE", Gone)] = 1,
                [("PRODUCT_MAPPING_UNMATCHED", Gone)] = 3,
                [("PRODUCT_MAPPING_UNMATCHED", Cleared)] = 2,
                [("LISTING_VERIFY_OBSERVE", Cleared)] = 1,
                [("LISTING_VERIFY_OBSERVE", Gone)] = 1,
                [("PRODUCT_BARCODE_CONFLICT", Gone)] = 1,
                [("PRODUCT_IMPORT_FAILED", Gone)] = 2,
                [("PTTAVM_PRODUCT_CODE_NOT_MODEL", Gone)] = 1
            };
            Assert.Equal(closes.Count, expected.Values.Sum());
            Assert.Equal(expected.OrderBy(x => x.Key).ToList(), counts.OrderBy(x => x.Key).ToList());
            Assert.Equal(expected.OrderBy(x => x.Key).ToList(), predicted.OrderBy(x => x.Key).ToList());
        }

        await using (var check = fixture.CreateContext())
        {
            var issues = await IssuesAsync(check, seed.Tenant.Id);
            Assert.All(closes, key => Assert.True(issues[key].Status == IssueStatus.Resolved && issues[key].ResolvedAt is not null
                && issues[key].Summary.Contains($"Otomatik kapatıldı ({OrphanedOperationalIssues.MaintenanceActor}): ", StringComparison.Ordinal), key));
            Assert.All(stays, key => Assert.True(issues[key].Status == IssueStatus.Open && issues[key].ResolvedAt is null && !issues[key].Summary.Contains("Otomatik kapatıldı", StringComparison.Ordinal), key));
        }

        // A second run finds nothing, and the prediction then finds nothing either. The check query finds every resolved one
        // under a rule of its own code, none under NO RULE MATCHES NOW.
        await using (var again = fixture.CreateContext())
        {
            Assert.Empty(await PassAsync(again, seed.Tenant.Id));
            Assert.Empty(await PredictAsync(again, seed.Tenant.Id));
            var connection = (NpgsqlConnection)again.Database.GetDbConnection();
            await using var command = new NpgsqlCommand(OrphanedOperationalIssues.VerifySql($"""i."TenantId" = '{seed.Tenant.Id}'"""), connection);
            await using var reader = await command.ExecuteReaderAsync();
            var resolvedRows = 0L;
            while (await reader.ReadAsync())
            {
                if (reader.GetString(0) != "resolved by the pass") continue;
                Assert.Equal(nameof(IssueStatus.Resolved), reader.GetString(1));
                Assert.NotEqual("NO RULE MATCHES NOW", reader.GetString(3));
                resolvedRows += reader.GetInt64(5);
            }
            Assert.Equal(closes.Count, resolvedRows);
        }
    }

    // Review finding 1: a code clash is judged over the listing's whole code set. The key holds the first code alone, which
    // one card holds while the listing's second code is on another card: the clash holds and the pass leaves it open.
    [PostgreSqlFact]
    public async Task CodeClashOverAMultiCodeListing_WhileItsCodesSitOnTwoCards_StaysOpen()
    {
        Seed seed;
        await using (var setup = fixture.CreateContext())
        {
            seed = await SeedAsync(setup);
            var first = AddCard(setup, seed, "CLA");
            var second = AddCard(setup, seed, "CLB");
            first.M.Sku = first.M.SkuNormalized = "CLASH-1";
            second.M.Sku = second.M.SkuNormalized = "CLASH-2";
            await setup.SaveChangesAsync();
        }
        await AssertThePassLeavesOpenAsync(seed,
            ($"product-sync-barcode-conflict:{seed.N11.Id}:CLASH-1", "PRODUCT_BARCODE_CONFLICT", "N11 ürününün stok kodu veya barkodu birden fazla yerel üründe bulundu; otomatik eşleştirme yapılmadı."));
    }

    // Review finding 2: an N11 listing linked to another panel variant than the one its codes name now. The listing and both
    // variants exist and the wrong link stays: the warning holds.
    [PostgreSqlFact]
    public async Task ListingLinkedToTheWrongPanelVariant_StaysOpen()
    {
        Seed seed;
        await using (var setup = fixture.CreateContext())
        {
            seed = await SeedAsync(setup);
            var card = AddCard(setup, seed, "WRONG");
            setup.MarketplaceVariantLinks.Add(new MarketplaceVariantLink { Id = Guid.CreateVersion7(), TenantId = seed.Tenant.Id, ConnectionId = seed.N11.Id, VariantId = card.M.Id, ExternalId = "WRONG-L", Version = 1 });
            setup.MarketplaceProductLinks.Add(new MarketplaceProductLink { Id = Guid.CreateVersion7(), TenantId = seed.Tenant.Id, ConnectionId = seed.N11.Id, ProductId = card.Card.Id, ExternalId = "WRONG-L", Version = 1 });
            await setup.SaveChangesAsync();
        }
        await AssertThePassLeavesOpenAsync(seed,
            ($"product-mapping-unmatched:{seed.N11.Id}:WRONG-L", "PRODUCT_MAPPING_UNMATCHED", "Pazar yeri varyantı WRONG-L daha önce başka bir panel varyantına bağlanmış; şimdiki eşleşme WRONG-L. Bağlantı değiştirilmedi · Kart."));
    }

    // Review finding 3: the update import of a Trendyol product that is linked failed. The product, its link (imported before
    // and also after the failure) and the connection exist: whether the failure still holds is the import's to say.
    [PostgreSqlFact]
    public async Task FailedUpdateImportOfALinkedProduct_StaysOpen()
    {
        Seed seed;
        await using (var setup = fixture.CreateContext())
        {
            seed = await SeedAsync(setup);
            var card = AddCard(setup, seed, "UPD");
            setup.MarketplaceProductLinks.Add(new MarketplaceProductLink { Id = Guid.CreateVersion7(), TenantId = seed.Tenant.Id, ConnectionId = seed.Trendyol.Id, ProductId = card.Card.Id, ExternalId = "861700001", LastImportedAt = DateTimeOffset.UtcNow.AddDays(-10), Version = 1 });
            var later = AddCard(setup, seed, "UPD2");
            setup.MarketplaceProductLinks.Add(new MarketplaceProductLink { Id = Guid.CreateVersion7(), TenantId = seed.Tenant.Id, ConnectionId = seed.Trendyol.Id, ProductId = later.Card.Id, ExternalId = "861700002", LastImportedAt = DateTimeOffset.UtcNow, Version = 1 });
            await setup.SaveChangesAsync();
        }
        await AssertThePassLeavesOpenAsync(seed,
            ($"product-sync-import:{seed.Trendyol.Id}:861700001", "PRODUCT_IMPORT_FAILED", "Pazar yeri ürünü '861700001' panele alınamadı; sonraki ürünle devam edildi."),
            ($"product-sync-import:{seed.Trendyol.Id}:861700002", "PRODUCT_IMPORT_FAILED", "Pazar yeri ürünü '861700002' panele alınamadı; sonraki ürünle devam edildi."));
    }

    // Review finding 4: PttAVM does not list a colour of a panel variant that still exists and keeps its own link on the
    // connection. The variant and its link exist: the warning holds.
    [PostgreSqlFact]
    public async Task PttAvmWarningOnAVariantThatExistsAndKeepsItsOwnLink_StaysOpen()
    {
        Seed seed;
        ProductVariant variant;
        await using (var setup = fixture.CreateContext())
        {
            seed = await SeedAsync(setup);
            var card = AddCard(setup, seed, "OWN");
            variant = card.L;
            setup.MarketplaceVariantLinks.Add(new MarketplaceVariantLink { Id = Guid.CreateVersion7(), TenantId = seed.Tenant.Id, ConnectionId = seed.Ptt.Id, VariantId = card.L.Id, ExternalId = "OWN-L", Version = 1 });
            await setup.SaveChangesAsync();
        }
        await AssertThePassLeavesOpenAsync(seed,
            ($"product-mapping-unmatched:{seed.Ptt.Id}:{variant.Id:N}", "PRODUCT_MAPPING_UNMATCHED", PttColourMissing));
    }

    // A clash is resolved by the import once the listing's codes land on one card, never by the pass.
    [PostgreSqlFact]
    public async Task ACodeClashIsResolvedByTheImportOnceTheListingsCodesLandOnOneCard_NotByThePass()
    {
        Seed seed;
        ProductVariant onSecondCard;
        await using (var setup = fixture.CreateContext())
        {
            seed = await SeedAsync(setup);
            var first = AddCard(setup, seed, "PRB-A");
            var second = AddCard(setup, seed, "PRB-B");
            first.M.Sku = first.M.SkuNormalized = "PRB-A1";
            second.M.Sku = second.M.SkuNormalized = "PRB-A2";
            onSecondCard = second.M;
            await setup.SaveChangesAsync();
        }
        var snapshot = new RemoteCatalogProduct("N11-LISTING-1", null, "Liste", "", null, null, null, null, [],
            [
                new RemoteCatalogVariant("N11-V1", "PRB-A1", null, null, new Dictionary<string, string>(), false, 500m, null, 10m, 1m, "TRY", "{}"),
                new RemoteCatalogVariant("N11-V2", "PRB-A2", null, null, new Dictionary<string, string>(), false, 500m, null, 10m, 1m, "TRY", "{}")
            ], "{}");
        // Another listing of the store whose only code is the first listing's first code (one card holds it: no clash).
        var sharing = new RemoteCatalogProduct("N11-LISTING-2", null, "Liste 2", "", null, null, null, null, [],
            [new RemoteCatalogVariant("N11-V9", "PRB-A1", null, null, new Dictionary<string, string>(), false, 500m, null, 10m, 1m, "TRY", "{}")], "{}");
        async Task ImportAsync(RemoteCatalogProduct listing)
        {
            await using var db = fixture.CreateContext();
            var processor = new MarketplaceJobProcessor(db, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, new ConfigurationBuilder().Build(), fixture.TimeProvider);
            var upsert = typeof(CatalogImportService).GetMethod("UpsertCatalogProduct", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            await (Task<bool>)upsert.Invoke(processor.CatalogImport,
                [seed.Tenant.Id, seed.N11.Id, listing, null, null, CancellationToken.None, true, false, true, false, false, true, false, false, false, false, true, "N11", null, false, null, false])!;
            await db.SaveChangesAsync();
        }
        var key = $"product-sync-barcode-conflict:{seed.N11.Id}:PRB-A1";
        async Task<IssueStatus> StatusAsync()
        {
            await using var db = fixture.CreateContext();
            return (await db.OperationalIssues.AsNoTracking().SingleAsync(x => x.TenantId == seed.Tenant.Id && x.DedupeKey == key)).Status;
        }

        await ImportAsync(snapshot);
        Assert.Equal(IssueStatus.Open, await StatusAsync());
        // Review 24 Nov 2026: the other listing shares the key but not the clash; it leaves the first listing's warning open.
        await ImportAsync(sharing);
        Assert.Equal(IssueStatus.Open, await StatusAsync());
        await using (var db = fixture.CreateContext())
        {
            Assert.Empty(await PassAsync(db, seed.Tenant.Id));
            Assert.Equal(IssueStatus.Open, (await db.OperationalIssues.AsNoTracking().SingleAsync(x => x.TenantId == seed.Tenant.Id && x.DedupeKey == key)).Status);
            // The owner moves the second code off the other card.
            await db.ProductVariants.Where(x => x.Id == onSecondCard.Id).ExecuteUpdateAsync(setters => setters.SetProperty(x => x.Sku, "PRB-Z2").SetProperty(x => x.SkuNormalized, "PRB-Z2"));
        }

        await ImportAsync(snapshot);
        await using var check = fixture.CreateContext();
        var issue = await check.OperationalIssues.AsNoTracking().SingleAsync(x => x.TenantId == seed.Tenant.Id && x.DedupeKey == key);
        Assert.Equal(IssueStatus.Resolved, issue.Status);
        Assert.NotNull(issue.ResolvedAt);
    }

    // The "several cards" warning of an N11 model product is resolved by the mapping itself once the product's variants land
    // on one panel card; while they sit on two it stays.
    [PostgreSqlFact]
    public async Task TheSeveralCardsWarningIsResolvedByTheMappingOnceTheVariantsLandOnOneCard()
    {
        Seed seed;
        ProductVariant onFirst, onSecond;
        await using (var setup = fixture.CreateContext())
        {
            seed = await SeedAsync(setup);
            var first = AddCard(setup, seed, "SCA");
            var second = AddCard(setup, seed, "SCB");
            first.M.Sku = first.M.SkuNormalized = "SC-1";
            second.M.Sku = second.M.SkuNormalized = "SC-2";
            onFirst = first.L;
            onSecond = second.M;
            await setup.SaveChangesAsync();
        }
        var snapshot = new RemoteCatalogProduct("main:SC", "SC", "Model", "", null, null, null, null, [],
            [
                new RemoteCatalogVariant("SC-1", "SC-1", null, "SC", new Dictionary<string, string>(), false, 500m, null, 10m, 1m, "TRY", "{}"),
                new RemoteCatalogVariant("SC-2", "SC-2", null, "SC", new Dictionary<string, string>(), false, 500m, null, 10m, 1m, "TRY", "{}")
            ], "{}");
        async Task MapAsync()
        {
            await using var db = fixture.CreateContext();
            var processor = new MarketplaceJobProcessor(db, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, new ConfigurationBuilder().Build(), fixture.TimeProvider);
            var map = typeof(CatalogImportService).GetMethod("MapExistingCatalogProduct", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            await (Task<bool>)map.Invoke(processor.CatalogImport, [seed.Tenant.Id, seed.N11.Id, snapshot, false, CancellationToken.None, true, true, false, null, "N11", false])!;
            await db.SaveChangesAsync();
        }
        var key = $"product-mapping-unmatched:{seed.N11.Id}:main:SC";

        await MapAsync();
        await using (var db = fixture.CreateContext())
        {
            Assert.Equal(IssueStatus.Open, (await db.OperationalIssues.AsNoTracking().SingleAsync(x => x.TenantId == seed.Tenant.Id && x.DedupeKey == key)).Status);
            Assert.Empty(await PassAsync(db, seed.Tenant.Id));
            // The owner moves the second code onto the first card.
            await db.ProductVariants.Where(x => x.Id == onSecond.Id).ExecuteUpdateAsync(setters => setters.SetProperty(x => x.Sku, "SC-X").SetProperty(x => x.SkuNormalized, "SC-X"));
            await db.ProductVariants.Where(x => x.Id == onFirst.Id).ExecuteUpdateAsync(setters => setters.SetProperty(x => x.Sku, "SC-2").SetProperty(x => x.SkuNormalized, "SC-2"));
        }

        await MapAsync();
        await using var check = fixture.CreateContext();
        var issue = await check.OperationalIssues.AsNoTracking().SingleAsync(x => x.TenantId == seed.Tenant.Id && x.DedupeKey == key);
        Assert.Equal(IssueStatus.Resolved, issue.Status);
        Assert.NotNull(issue.ResolvedAt);
    }

    // An issue the pass resolved by mistake is not stuck: its own writer opens the same record again under the same key,
    // with its own summary, and the next pass leaves it open while the guard holds again.
    [PostgreSqlFact]
    public async Task AnIssueThePassResolvedOpensAgainUnderItsOwnKeyWhenItsWriterSeesItAgain()
    {
        Seed seed;
        Guid linkId;
        var key = "";
        await using (var setup = fixture.CreateContext())
        {
            seed = await SeedAsync(setup);
            var card = AddCard(setup, seed, "REOPEN");
            linkId = Guid.CreateVersion7();
            setup.MarketplaceProductLinks.Add(new MarketplaceProductLink { Id = linkId, TenantId = seed.Tenant.Id, ConnectionId = seed.N11.Id, ProductId = card.Card.Id, ExternalId = "main:REOPEN", Version = 1 });
            key = $"product-mapping-unmatched:{seed.N11.Id}:main:REOPEN";
            setup.OperationalIssues.Add(Issue(seed, key, "PRODUCT_MAPPING_UNMATCHED"));
            await setup.SaveChangesAsync();
        }
        await using (var db = fixture.CreateContext())
        {
            Assert.Equal(1, (await PassAsync(db, seed.Tenant.Id))[("PRODUCT_MAPPING_UNMATCHED", OrphanedOperationalIssues.ConditionCleared)]);
            // The link goes again and the import sees the listing on several cards once more.
            await db.MarketplaceProductLinks.Where(x => x.Id == linkId).ExecuteDeleteAsync();
            await new IssueRecorder(db).RecordAsync(seed.Tenant.Id, key, "PRODUCT_MAPPING_UNMATCHED", "yeniden", DateTimeOffset.UtcNow, CancellationToken.None);
            await db.SaveChangesAsync();
            Assert.Empty(await PassAsync(db, seed.Tenant.Id));
        }
        await using var check = fixture.CreateContext();
        var issue = await check.OperationalIssues.AsNoTracking().SingleAsync(x => x.TenantId == seed.Tenant.Id && x.DedupeKey == key);
        Assert.Equal(IssueStatus.Open, issue.Status);
        Assert.Equal("yeniden", issue.Summary);
        Assert.Equal(2, issue.OccurrenceCount);
    }

    // 24 Nov 2026: an offer issue of the automatic send closes when its offer is gone, or when the send's own check
    // (PriceInventoryComposer.StandardOfferRefused) passes the stored offer now; an offer still refused, another platform's
    // offer and another code stay open. A delete's pass (subject gone only) does not judge the check.
    [PostgreSqlFact]
    public async Task AnOfferIssueClosesWhenItsOfferIsGoneOrPassesTheSendsOwnCheck_AndStaysWhileRefused()
    {
        Seed seed;
        string passesAtPanelPrice, passesAtOwnPrice, offerGone;
        string[] stays;
        await using (var setup = fixture.CreateContext())
        {
            seed = await SeedAsync(setup);
            var a = AddCard(setup, seed, "OFA");
            var b = AddCard(setup, seed, "OFB");
            var c = AddCard(setup, seed, "OFC");
            setup.ChannelOffers.AddRange(
                // Stored at 0, follows the panel price (600 on the variant): the send sends the panel price.
                Offer(seed, seed.Trendyol, a.M, salePrice: 0m),
                // Its own price (Özel Fiyat) is above 0 now.
                Offer(seed, seed.Trendyol, a.L, salePrice: 450m, followsPanelPrice: false),
                // Still refused: its own price is 0, or the currency is not TRY.
                Offer(seed, seed.Trendyol, b.M, salePrice: 0m, followsPanelPrice: false),
                Offer(seed, seed.Trendyol, b.L, salePrice: 450m, currency: "USD"),
                // N11's own checks are not judged here; another code under the same key shape is not this rule's.
                Offer(seed, seed.N11, c.M, salePrice: 450m),
                Offer(seed, seed.Trendyol, c.M, salePrice: 450m));
            await setup.SaveChangesAsync();
            string Key(PlatformConnection connection, ProductVariant variant) => $"stock-projection-offer-invalid:{connection.Id:N}:{variant.Id:N}";
            passesAtPanelPrice = Key(seed.Trendyol, a.M);
            passesAtOwnPrice = Key(seed.Trendyol, a.L);
            offerGone = Key(seed.Trendyol, c.L);
            stays = [Key(seed.Trendyol, b.M), Key(seed.Trendyol, b.L), Key(seed.N11, c.M)];
            setup.OperationalIssues.AddRange(new[] { passesAtPanelPrice, passesAtOwnPrice, offerGone }.Concat(stays).Select(key => Issue(seed, key, "CHANNEL_OFFER_INVALID")));
            setup.OperationalIssues.Add(Issue(seed, Key(seed.Trendyol, c.M), "HEPSIBURADA_LISTING_LINK_REQUIRED"));
            stays = [.. stays, Key(seed.Trendyol, c.M)];
            await setup.SaveChangesAsync();
        }

        await using (var db = fixture.CreateContext())
        {
            var afterDelete = await OrphanedOperationalIssues.ResolveAsync(db, seed.Tenant.Id, DateTimeOffset.UtcNow, 500, 5, OrphanedOperationalIssues.DeleteActor, CancellationToken.None, subjectGoneOnly: true);
            Assert.Equal([new KeyValuePair<(string, string), int>(("CHANNEL_OFFER_INVALID", OrphanedOperationalIssues.SubjectGone), 1)], afterDelete.ToList());
            var counts = await PassAsync(db, seed.Tenant.Id, batchSize: 1, maxBatches: 10);
            Assert.Equal([new KeyValuePair<(string, string), int>(("CHANNEL_OFFER_INVALID", OrphanedOperationalIssues.ConditionCleared), 2)], counts.ToList());
            Assert.Empty(await PassAsync(db, seed.Tenant.Id));
        }

        await using var check = fixture.CreateContext();
        var issues = await IssuesAsync(check, seed.Tenant.Id);
        Assert.All(new[] { passesAtPanelPrice, passesAtOwnPrice }, key => Assert.True(issues[key].Status == IssueStatus.Resolved && issues[key].ResolvedAt is not null
            && issues[key].Summary.EndsWith($"({OrphanedOperationalIssues.MaintenanceActor}): koşul artık geçerli değil — {OrphanedOperationalIssues.OfferPassesDetail}", StringComparison.Ordinal), key));
        Assert.EndsWith("konu artık yok — kaydın bu bağlantıdaki teklifi artık yok.", issues[offerGone].Summary, StringComparison.Ordinal);
        Assert.All(stays, key => Assert.True(issues[key].Status == IssueStatus.Open && issues[key].Summary == "test", key));
        var connection = (NpgsqlConnection)check.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open) await connection.OpenAsync();
        await using var command = new NpgsqlCommand(OrphanedOperationalIssues.VerifySql($"""i."TenantId" = '{seed.Tenant.Id}'"""), connection);
        await using var reader = await command.ExecuteReaderAsync();
        var rules = new List<string>();
        while (await reader.ReadAsync())
            if (reader.GetString(0) == "resolved by the pass") rules.Add($"{reader.GetString(3)} {reader.GetString(4)[3..]} {reader.GetInt64(5)}");
        Assert.Equal(["CONDITION_CLEARED offer-passes-send-check 2", "SUBJECT_GONE offer-gone 1"], rules.Order().ToList());
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

        var key = ("PRODUCT_WEBCOLOR_MAPPING_REQUIRED", OrphanedOperationalIssues.SubjectGone);
        await using (var db = fixture.CreateContext())
        {
            Assert.Equal(4, (await PassAsync(db, seed.Tenant.Id, batchSize: 2, maxBatches: 2))[key]);
            Assert.Equal(1, (await PassAsync(db, seed.Tenant.Id, batchSize: 2, maxBatches: 2))[key]);
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
            setup.OperationalIssues.AddRange(Issue(seed, oldKey, "PRODUCT_WEBCOLOR_MAPPING_REQUIRED"), Issue(seed, oldPttKey, "PRODUCT_MAPPING_UNMATCHED", PttColourMissing));
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
            await recorder.RecordAsync(seed.Tenant.Id, newPttKey, "PRODUCT_MAPPING_UNMATCHED", PttColourMissing, DateTimeOffset.UtcNow, CancellationToken.None);
            await import.SaveChangesAsync();
            Assert.Empty(await PassAsync(import, seed.Tenant.Id));
        }

        await using var check = fixture.CreateContext();
        var issues = await IssuesAsync(check, seed.Tenant.Id);
        Assert.Equal(IssueStatus.Resolved, issues[oldKey].Status);
        Assert.Equal(IssueStatus.Resolved, issues[oldPttKey].Status);
        Assert.Equal(IssueStatus.Open, issues[newKey].Status);
        Assert.Equal(IssueStatus.Open, issues[newPttKey].Status);
    }
}
