using System.Text.Json;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using MarketplaceHub.Infrastructure.Persistence.Migrations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// Garanti Süresi (owner, 15 Nov 2026: "0 ile boş farklı; boşsa doldurmasını istesin ama default 0 zaten"): 0 is "no
// warranty" and is stored as 0; empty (null) is "not filled", which only a draft may keep. Every write path, the data
// migration that turns the old "0 stored as null" into 0, the repair pass that runs after the deploy's container swap,
// and what the marketplaces receive (byte for byte the same for a card whose 0 was stored as null).
[Collection("postgres-integration")]
public sealed class WarrantyZeroIsAValuePostgreSqlTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    private sealed record Seed(Tenant Tenant, Brand Brand, Category Category);

    private async Task<Seed> SeedAsync(string code)
    {
        var now = fixture.Now;
        var tenant = new Tenant { Id = Guid.CreateVersion7(), Code = $"{code}-{Guid.NewGuid():N}", DisplayName = code, Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = now, UpdatedAt = now, Version = 1 };
        var brand = new Brand { Id = Guid.CreateVersion7(), TenantId = tenant.Id, Name = "Ravencia", NormalizedName = "RAVENCIA", CreatedAt = now, UpdatedAt = now };
        var category = new Category { Id = Guid.CreateVersion7(), TenantId = tenant.Id, Name = "Bluz", NormalizedName = "BLUZ", Path = "Bluz", CreatedAt = now, UpdatedAt = now };
        await using var db = fixture.CreateContext();
        db.Tenants.Add(tenant);
        db.Brands.Add(brand);
        db.Categories.Add(category);
        await db.SaveChangesAsync();
        return new(tenant, brand, category);
    }

    private CatalogService Catalog(AppDbContext db) => new(db, null!, new ConfigurationBuilder().Build(), fixture.TimeProvider, new MemoryCache(new MemoryCacheOptions()));

    private static CreateProductCommand Complete(Seed seed, string sku, string status = "ACTIVE", int? warranty = 0) =>
        new("Bluz", "Açıklama", seed.Brand.Id, seed.Category.Id, [new CreateVariantCommand(sku, $"{sku}B", $"{sku}-M", Desi: 1m, DefaultListPrice: 120m, DefaultSalePrice: 100m)],
            Status: status, DefaultListPrice: 120m, DefaultSalePrice: 100m, WarrantyMonths: warranty, VatRate: 10m, VatInclusion: "INCLUDED", DeliveryDays: 2);

    private static UpdateProductCommand Save(Seed seed, string? status = null, int? warranty = null, bool clear = false) =>
        new("Bluz", "Açıklama", seed.Brand.Id, seed.Category.Id, Status: status, WarrantyMonths: warranty, ClearWarrantyMonths: clear);

    // One of the migration's own statements, narrowed to a test's tenant so other tests' rows stay as they are.
    private static Task<int> RunForTenantAsync(AppDbContext db, string statement, Guid tenantId) =>
        db.Database.ExecuteSqlAsync(System.Runtime.CompilerServices.FormattableStringFactory.Create(statement + " AND \"TenantId\" = {0}", tenantId));

    private async Task<int?> WarrantyAsync(Guid productId)
    {
        await using var db = fixture.CreateContext();
        // KATALOG-14 D5 step B2b: the card's column while the copies are kept, else the value every reader sees (the model's).
        return (await (ModelFieldCopiesTestPosition.CopiesWritten ? db.Products.AsNoTracking() : EffectiveCatalog.Products(db)).SingleAsync(x => x.Id == productId)).WarrantyMonths;
    }

    [PostgreSqlFact]
    public async Task TheEditor_StoresZeroAsZero_AndEmptyOnlyOnADraft()
    {
        var seed = await SeedAsync("warranty-editor");
        var sku = $"WE-{Guid.NewGuid():N}"[..14].ToUpperInvariant();
        await using var db = fixture.CreateContext();
        var service = Catalog(db);

        // A new card on sale with 0 keeps 0 (it was stored as null before 15 Nov 2026).
        var active = await service.CreateProductAsync(seed.Tenant.Id, Complete(seed, sku), CancellationToken.None);
        Assert.True(active.Succeeded, active.Error?.Message);
        Assert.Equal(0, active.Value!.WarrantyMonths);
        Assert.Equal(0, await WarrantyAsync(active.Value.Id));
        // A draft created with the field empty stays empty.
        var draft = await service.CreateProductAsync(seed.Tenant.Id, Complete(seed, $"{sku}D", status: "DRAFT", warranty: null), CancellationToken.None);
        Assert.True(draft.Succeeded, draft.Error?.Message);
        Assert.Null(await WarrantyAsync(draft.Value!.Id));
        db.ChangeTracker.Clear();

        // Edits of the card on sale: 24, then 0 again (kept as 0), then a save that leaves it out keeps it.
        var version = active.Value.Version;
        foreach (var (warranty, expected) in new (int?, int?)[] { (24, 24), (0, 0), (null, 0) })
        {
            var saved = await service.UpdateProductAsync(seed.Tenant.Id, active.Value.Id, version, Save(seed, warranty: warranty), CancellationToken.None);
            Assert.True(saved.Succeeded, saved.Error?.Message);
            Assert.Equal(expected, saved.Value!.WarrantyMonths);
            Assert.Equal(expected, await WarrantyAsync(active.Value.Id));
            version = saved.Value.Version;
            db.ChangeTracker.Clear();
        }
        // Emptying it on a card on sale is a missing required field, refused with the editor's message.
        var emptied = await service.UpdateProductAsync(seed.Tenant.Id, active.Value.Id, version, Save(seed, clear: true), CancellationToken.None);
        Assert.Equal((422, ProductRequiredFields.Code, "Zorunlu alanları doldurun: Garanti Süresi."), (emptied.Error!.Status, emptied.Error.Code, emptied.Error.Message));
        db.ChangeTracker.Clear();
        Assert.Equal(0, await WarrantyAsync(active.Value.Id));
        // A value and a clear in the same save contradict each other.
        var both = await service.UpdateProductAsync(seed.Tenant.Id, active.Value.Id, version, Save(seed, warranty: 6, clear: true), CancellationToken.None);
        Assert.Equal(CatalogService.WarrantyClearConflictMessage, both.Error!.Message);
        db.ChangeTracker.Clear();

        // The draft: saved empty on purpose (Taslak Olarak Kaydet with the field emptied), a save that leaves it out
        // keeps it empty, and it cannot open for sale until the warranty is given; 0 is enough.
        var draftVersion = draft.Value.Version;
        var draftSaved = await service.UpdateProductAsync(seed.Tenant.Id, draft.Value.Id, draftVersion, Save(seed, status: "DRAFT", clear: true), CancellationToken.None);
        Assert.True(draftSaved.Succeeded, draftSaved.Error?.Message);
        Assert.Null(draftSaved.Value!.WarrantyMonths);
        db.ChangeTracker.Clear();
        var opening = await service.UpdateProductAsync(seed.Tenant.Id, draft.Value.Id, draftSaved.Value.Version, Save(seed, status: "ACTIVE"), CancellationToken.None);
        Assert.Equal("Zorunlu alanları doldurun: Garanti Süresi.", opening.Error!.Message);
        Assert.Equal("Garanti Süresi zorunludur.", opening.Error.FieldErrors!["warrantyMonths"][0]);
        db.ChangeTracker.Clear();
        Assert.Null(await WarrantyAsync(draft.Value.Id));
        var opened = await service.UpdateProductAsync(seed.Tenant.Id, draft.Value.Id, draftSaved.Value.Version, Save(seed, status: "ACTIVE", warranty: 0), CancellationToken.None);
        Assert.True(opened.Succeeded, opened.Error?.Message);
        Assert.Equal(("ACTIVE", (int?)0), (opened.Value!.Status, opened.Value.WarrantyMonths));
    }

    [PostgreSqlFact]
    public async Task TheBulkValue_WritesZero_AndABulkOpening_LeavesACardWithAnEmptyWarranty()
    {
        var seed = await SeedAsync("warranty-bulk");
        var sku = $"WB-{Guid.NewGuid():N}"[..14].ToUpperInvariant();
        await using var db = fixture.CreateContext();
        var service = Catalog(db);
        var card = await service.CreateProductAsync(seed.Tenant.Id, Complete(seed, sku, warranty: 24), CancellationToken.None);
        Assert.True(card.Succeeded, card.Error?.Message);
        db.ChangeTracker.Clear();

        // Toplu Değer Ata › Garanti Süresi 0: stored as 0, not emptied.
        var bulk = await service.BulkSetFieldAsync(seed.Tenant.Id, new BulkProductFieldCommand([card.Value!.Id], BulkProductFields.WarrantyMonths, "0"), CancellationToken.None);
        Assert.True(bulk.Succeeded, bulk.Error?.Message);
        Assert.Equal(1, bulk.Value!.UpdatedCount);
        Assert.Equal(0, await WarrantyAsync(card.Value.Id));
        db.ChangeTracker.Clear();

        // A closed card whose warranty is empty is not opened by Toplu Satışa Aç; one with 0 is.
        var empty = await service.CreateProductAsync(seed.Tenant.Id, Complete(seed, $"{sku}E", status: "DRAFT", warranty: null), CancellationToken.None);
        Assert.True(empty.Succeeded, empty.Error?.Message);
        await db.Products.Where(x => x.Id == empty.Value!.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, ProductStatus.Archived));
        await db.Products.Where(x => x.Id == card.Value.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, ProductStatus.Archived));
        db.ChangeTracker.Clear();
        var opened = await service.BulkSetStatusAsync(seed.Tenant.Id, new BulkProductStatusCommand([card.Value.Id, empty.Value!.Id], "ACTIVE"), CancellationToken.None);
        Assert.True(opened.Succeeded, opened.Error?.Message);
        Assert.Equal(1, opened.Value!.UpdatedCount);
        var skipped = Assert.Single(opened.Value.Skipped);
        Assert.Equal(empty.Value.Id, skipped.ProductId);
        Assert.Equal(["Garanti Süresi"], skipped.MissingFields);
    }

    // A card with one variant and its own model code, linked to its model record by the save (ProductModelLinks).
    private async Task<(Guid CardId, Guid ModelId)> LinkedCardAsync(Seed seed, string title, ProductStatus status)
    {
        var now = fixture.Now;
        var code = $"WZ{Guid.NewGuid():N}"[..12].ToUpperInvariant();
        var card = new Product { Id = Guid.CreateVersion7(), TenantId = seed.Tenant.Id, Title = title, Description = "", BrandId = seed.Brand.Id, Status = status, CreatedAt = now, UpdatedAt = now };
        await using (var db = fixture.CreateContext())
        {
            db.Products.Add(card);
            db.ProductVariants.Add(new ProductVariant { Id = Guid.CreateVersion7(), TenantId = seed.Tenant.Id, ProductId = card.Id, Sku = code, SkuNormalized = code, ModelCode = code, OptionSignature = "", Status = status, CreatedAt = now, UpdatedAt = now });
            await db.SaveChangesAsync();
        }
        await using var check = fixture.CreateContext();
        return (card.Id, (await check.Products.AsNoTracking().SingleAsync(x => x.Id == card.Id)).FamilyId!.Value);
    }

    // Writes warranties the way the previous code left them, without a save (no hook, no Version).
    private async Task SetWarrantiesAsync(IEnumerable<(Guid Id, int? Warranty)> cards, IEnumerable<(Guid Id, int? Warranty)> models)
    {
        await using var db = fixture.CreateContext();
        foreach (var (id, warranty) in cards) await db.Products.Where(x => x.Id == id).ExecuteUpdateAsync(s => s.SetProperty(x => x.WarrantyMonths, warranty));
        foreach (var (id, warranty) in models) await db.ProductModels.Where(x => x.Id == id).ExecuteUpdateAsync(s => s.SetProperty(x => x.WarrantyMonths, warranty));
    }

    // The data migration on rows as the previous code left them: every null is a 0 the user chose; 24 stays; a second
    // run changes nothing; Down is deliberately empty (null -> 0 cannot be undone without losing what was saved since).
    [PostgreSqlFact]
    public async Task TheMigration_TurnsEveryStoredNullIntoZero_AndKeepsTheValues()
    {
        var seed = await SeedAsync("warranty-migration");
        var empty = await LinkedCardAsync(seed, "Boş", ProductStatus.Active);
        var emptyDraft = await LinkedCardAsync(seed, "Boş Taslak", ProductStatus.Draft);
        var zero = await LinkedCardAsync(seed, "Sıfır", ProductStatus.Active);
        var twentyFour = await LinkedCardAsync(seed, "Yirmi Dört", ProductStatus.Active);
        await SetWarrantiesAsync([(empty.CardId, null), (emptyDraft.CardId, null), (zero.CardId, 0), (twentyFour.CardId, 24)], [(empty.ModelId, null), (twentyFour.ModelId, 24)]);
        var versions = await VersionsAsync();

        var migration = new WarrantyZeroIsAValue();
        Assert.Empty(migration.DownOperations);
        for (var run = 0; run < 2; run++)
        {
            await using var db = fixture.CreateContext();
            // The migration's own statements, narrowed to this test's tenant so other tests' rows stay as they are.
            foreach (var operation in migration.UpOperations.OfType<SqlOperation>())
                await RunForTenantAsync(db, operation.Sql, seed.Tenant.Id);
        }

        await using var check = fixture.CreateContext();
        var cards = await check.Products.AsNoTracking().Where(x => x.TenantId == seed.Tenant.Id).ToDictionaryAsync(x => x.Id, x => x.WarrantyMonths);
        Assert.Equal(((int?)0, (int?)0, (int?)0, (int?)24), (cards[empty.CardId], cards[emptyDraft.CardId], cards[zero.CardId], cards[twentyFour.CardId]));
        var models = await check.ProductModels.AsNoTracking().Where(x => x.TenantId == seed.Tenant.Id).ToDictionaryAsync(x => x.Id, x => x.WarrantyMonths);
        Assert.Equal(((int?)0, (int?)24), (models[empty.ModelId], models[twentyFour.ModelId]));
        Assert.DoesNotContain(null, models.Values);
        // No version, no change time: an editor holding the card loaded before the deploy still saves it.
        Assert.Equal(versions, await VersionsAsync());

        async Task<List<(Guid, long, DateTimeOffset)>> VersionsAsync()
        {
            await using var db = fixture.CreateContext();
            return [.. (await db.Products.AsNoTracking().Where(x => x.TenantId == seed.Tenant.Id).OrderBy(x => x.Id).Select(x => new { x.Id, x.Version, x.UpdatedAt }).ToListAsync()).Select(x => (x.Id, x.Version, x.UpdatedAt))];
        }
    }

    // The previous code still serves while the deploy migrates and stores a 0 it saves as null: the new Worker's first
    // pass after it starts gives such a card (not a draft) its 0. A draft keeps its empty warranty, and so does the model
    // record of an empty draft. Later passes write nothing: they only count a card on sale left empty, for a warning.
    [PostgreSqlFact]
    public async Task TheRepairAfterTheSwap_WritesOnlyOnItsFirstPass_GivingZeroToCardsOnSale_AndLeavesEmptyDrafts()
    {
        var seed = await SeedAsync("warranty-repair");
        var active = await LinkedCardAsync(seed, "Satışta", ProductStatus.Active);
        var archived = await LinkedCardAsync(seed, "Kapalı", ProductStatus.Archived);
        var draft = await LinkedCardAsync(seed, "Taslak", ProductStatus.Draft);
        await SetWarrantiesAsync([(active.CardId, null), (archived.CardId, null), (draft.CardId, null)], [(active.ModelId, null), (archived.ModelId, null), (draft.ModelId, null)]);

        // A later pass (not the first after a start): counts the cards on sale or closed left empty, writes nothing.
        await using (var db = fixture.CreateContext())
        {
            var later = await ProductWarranty.RunPassAsync(db, firstPassAfterStart: false, CancellationToken.None);
            Assert.True(later.EmptyCardsNotDraft >= 2, $"{later.EmptyCardsNotDraft} cards");
            Assert.Equal((0, 0), (later.CardsWritten, later.ModelsWritten));
        }
        Assert.Equal(((int?)null, (int?)null, (int?)null), (await WarrantyAsync(active.CardId), await WarrantyAsync(archived.CardId), await WarrantyAsync(draft.CardId)));

        // KATALOG-14 D5 step B2b: with the copies off (Catalog:MirrorModelFields false) the repair is retired: the first pass writes
        // nothing either and only counts by the model. The repair below is the switch-on behaviour.
        if (!ModelFieldCopiesTestPosition.CopiesWritten)
        {
            await using var off = fixture.CreateContext();
            var none = await ProductWarranty.RunPassAsync(off, firstPassAfterStart: true, CancellationToken.None);
            Assert.Equal((0, 0), (none.CardsWritten, none.ModelsWritten));
            Assert.True(none.EmptyCardsNotDraft >= 2, $"{none.EmptyCardsNotDraft} cards");
            return;
        }
        // The first pass after a start writes 0; a second first pass (a restart) finds nothing left.
        await using (var db = fixture.CreateContext())
        {
            var first = await ProductWarranty.RunPassAsync(db, firstPassAfterStart: true, CancellationToken.None);
            Assert.True(first.CardsWritten >= 2 && first.ModelsWritten >= 2, $"{first.CardsWritten} cards, {first.ModelsWritten} models");
            var again = await ProductWarranty.RunPassAsync(db, firstPassAfterStart: true, CancellationToken.None);
            Assert.Equal((0, 0, 0), (again.CardsWritten, again.ModelsWritten, again.EmptyCardsNotDraft));
        }
        Assert.Equal(((int?)0, (int?)0, (int?)null), (await WarrantyAsync(active.CardId), await WarrantyAsync(archived.CardId), await WarrantyAsync(draft.CardId)));
        await using var check = fixture.CreateContext();
        var records = await check.ProductModels.AsNoTracking().Where(x => x.TenantId == seed.Tenant.Id).ToDictionaryAsync(x => x.Id, x => x.WarrantyMonths);
        Assert.Equal(((int?)0, (int?)0, (int?)null), (records[active.ModelId], records[archived.ModelId], records[draft.ModelId]));
    }

    // Review, 16 Nov 2026: a draft colour saved with its warranty emptied (Taslak Olarak Kaydet) shares the model's fields
    // with its sibling colours, but an empty warranty never reaches a colour that is not a draft (it keeps its own) nor the
    // model record of such a colour; a draft sibling takes it.
    [PostgreSqlFact]
    public async Task ADraftColourSavedEmpty_LeavesTheWarrantyOfASiblingOnSaleAndOfTheModel()
    {
        var seed = await SeedAsync("warranty-family");
        var now = fixture.Now;
        var code = $"WF{Guid.NewGuid():N}"[..12].ToUpperInvariant();
        Product Card(string colour, ProductStatus status, int minute) => new() { Id = Guid.CreateVersion7(), TenantId = seed.Tenant.Id, Title = $"Bluz {colour}", Description = "Açıklama", BrandId = seed.Brand.Id, CategoryId = seed.Category.Id, VatRate = 10m, VatInclusion = "INCLUDED", WarrantyMonths = 24, DeliveryDays = 2, DefaultListPrice = 120m, DefaultSalePrice = 100m, Status = status, CreatedAt = now.AddMinutes(minute), UpdatedAt = now };
        var siyah = Card("Siyah", ProductStatus.Active, 0);
        var kirmizi = Card("Kırmızı", ProductStatus.Draft, 1);
        var mavi = Card("Mavi", ProductStatus.Draft, 2);
        Product[] cards = [siyah, kirmizi, mavi];
        await using (var setup = fixture.CreateContext())
        {
            setup.Products.AddRange(cards);
            foreach (var (card, index) in cards.Select((card, index) => (card, index)))
                setup.ProductVariants.Add(new ProductVariant { Id = Guid.CreateVersion7(), TenantId = seed.Tenant.Id, ProductId = card.Id, Sku = $"{code}-{index}", SkuNormalized = $"{code}-{index}", Barcode = $"{code}{index}", BarcodeNormalized = $"{code}{index}", ModelCode = code, Desi = 1m, OptionSignature = $"Renk: {card.Title[5..]}", Status = card.Status, CreatedAt = now, UpdatedAt = now });
            await setup.SaveChangesAsync();
        }

        await using (var db = fixture.CreateContext())
        {
            var service = Catalog(db);
            var shown = (await service.GetProductAsync(seed.Tenant.Id, kirmizi.Id, CancellationToken.None)).Value!;
            var saved = await service.UpdateProductAsync(seed.Tenant.Id, kirmizi.Id, shown.Version, new UpdateProductCommand(shown.Title, shown.Description, shown.BrandId, shown.CategoryId,
                Status: "DRAFT", ClearWarrantyMonths: true, VatRate: 10m, VatInclusion: "INCLUDED", DeliveryDays: 2, FamilySharedFields: ProductFamilySharedFields.All), CancellationToken.None);
            Assert.True(saved.Succeeded, saved.Error?.Message);
        }

        // KATALOG-14 D5 step B2b: with the copies off (Catalog:MirrorModelFields false) the drafts' emptied warranty is not kept on
        // their copies (the model keeps 24), so every colour reads 24; with them on, the drafts' copies hold it empty (unread).
        Assert.Equal(((int?)24, ModelFieldCopiesTestPosition.CopiesWritten ? null : (int?)24, ModelFieldCopiesTestPosition.CopiesWritten ? null : (int?)24), (await WarrantyAsync(siyah.Id), await WarrantyAsync(kirmizi.Id), await WarrantyAsync(mavi.Id)));
        await using var check = fixture.CreateContext();
        var modelId = (await check.Products.AsNoTracking().SingleAsync(x => x.Id == siyah.Id)).FamilyId!.Value;
        Assert.Equal(24, (await check.ProductModels.AsNoTracking().SingleAsync(x => x.Id == modelId)).WarrantyMonths);
        // And the daily pass has nothing to say: no card on sale was left empty.
        Assert.Equal(0, await EffectiveCatalog.Products(check).CountAsync(x => x.TenantId == seed.Tenant.Id && x.WarrantyMonths == null && x.Status != ProductStatus.Draft));
    }


    // What the marketplaces receive: every publication, update and price/stock composer of every marketplace builds a card
    // whose 0 the previous code stored as null exactly as it builds it after the migration wrote 0, except that the new
    // code refuses to send an empty warranty where the card's own would go (Hepsiburada's GarantiSuresi, PttAVM's
    // warranty) instead of sending it as 0. The previous code sent `WarrantyMonths ?? 0` there, so the migrated card's
    // payload carries the same 0. A card with 24 months is untouched throughout.
    [PostgreSqlFact]
    public async Task EveryComposer_SendsAMigratedZeroAsTheOldCodeSentItsNull_AndTwentyFourAsBefore()
    {
        var now = DateTimeOffset.UtcNow;
        var tenant = new Tenant { Id = Guid.CreateVersion7(), Code = $"warranty-eq-{Guid.NewGuid():N}", DisplayName = "warranty-eq", Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = now, UpdatedAt = now, Version = 1 };
        var categoryId = Guid.CreateVersion7();
        var brandId = Guid.CreateVersion7();
        string[] platforms = ["TRENDYOL", "HEPSIBURADA", "N11", "PAZARAMA", "PTTAVM", "SHOPIFY"];
        Product Card(string code, int? warranty) => new() { Id = Guid.CreateVersion7(), TenantId = tenant.Id, Title = $"Kadın Siyah Triko Bluz {code}", Description = "Açıklama", CategoryId = categoryId, BrandId = brandId, VatRate = 10m, VatInclusion = "INCLUDED", WarrantyMonths = warranty, DeliveryDays = 2, DefaultListPrice = 900m, DefaultSalePrice = 630m, Status = ProductStatus.Active, CreatedAt = now, UpdatedAt = now, Version = 1 };
        var zero = Card("MZ410", null);
        var twentyFour = Card("MZ411", 24);
        Product[] cards = [zero, twentyFour];
        var variants = cards.SelectMany((card, cardIndex) => new[] { "M", "L" }.Select((size, index) => new ProductVariant
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenant.Id,
            ProductId = card.Id,
            SortOrder = index,
            Sku = $"MZ41{cardIndex}-{size}",
            SkuNormalized = $"MZ41{cardIndex}-{size}",
            Barcode = $"86941000{cardIndex}{index}11",
            BarcodeNormalized = $"86941000{cardIndex}{index}11",
            ModelCode = $"MZ41{cardIndex}",
            OptionSignature = $"Renk: Siyah | Beden: {size}",
            Desi = 1m,
            CostPrice = 100m,
            DefaultListPrice = 900m,
            DefaultSalePrice = 630m,
            Status = ProductStatus.Active,
            CreatedAt = now,
            UpdatedAt = now,
            Version = 1,
        })).ToList();
        var connections = platforms.Select(code => new PlatformConnection { Id = Guid.CreateVersion7(), TenantId = tenant.Id, PublicId = Guid.CreateVersion7(), PlatformCode = code, Environment = "PRODUCTION", DisplayName = code, ExternalStoreId = $"store-{Guid.NewGuid():N}", Status = "ACTIVE", ApiVersion = "V1", SettingsJson = "{}", Version = 1 }).ToList();

        await using (var seed = fixture.CreateContext())
        {
            seed.Tenants.Add(tenant);
            seed.Categories.Add(new Category { Id = categoryId, TenantId = tenant.Id, Name = "Bluz", NormalizedName = "BLUZ", Path = "Bluz", IsLeaf = true, IsActive = true, CreatedAt = now, UpdatedAt = now, Version = 1 });
            seed.Brands.Add(new Brand { Id = brandId, TenantId = tenant.Id, Name = "Ravencia", NormalizedName = "RAVENCIA", IsActive = true, CreatedAt = now, UpdatedAt = now, Version = 1 });
            seed.PlatformConnections.AddRange(connections);
            seed.Products.AddRange(cards);
            seed.ProductVariants.AddRange(variants);
            foreach (var (card, index) in cards.Select((card, index) => (card, index)))
            {
                var asset = new FileAsset { Id = Guid.CreateVersion7(), TenantId = tenant.Id, Classification = "PRODUCT_MEDIA_URL", RelativePath = $"https://cdn.example.com/mz41/{index}.jpg", MimeType = "image/jpeg", Sha256 = Guid.NewGuid().ToString("N"), Status = "ACTIVE", CreatedAt = now };
                seed.FileAssets.Add(asset);
                seed.ProductMedia.Add(new ProductMedia { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ProductId = card.Id, FileAssetId = asset.Id, MediaRole = "ORDERED_PRIMARY", SortOrder = 0, Status = "ACTIVE" });
            }
            foreach (var connection in connections)
            {
                ReferenceSnapshot Snapshot(string type, string scope) => new() { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, ResourceType = type, ScopeExternalId = scope, SourceVersion = type, ContentHash = $"hash-{type}", FetchedAt = now, IsCurrent = true, ItemCount = 1 };
                ReferenceItem Item(Guid snapshotId, string type, string externalId, string name) => new() { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, SnapshotId = snapshotId, ResourceType = type, ExternalId = externalId, Name = name, NormalizedName = name.ToUpperInvariant(), Path = name, Depth = 0, IsLeaf = true, IsActive = true, PayloadHash = $"payload-{type}-{externalId}" };
                var categories = Snapshot("CATEGORIES", "");
                var brands = Snapshot("BRANDS", "");
                seed.ReferenceSnapshots.AddRange(categories, brands, Snapshot("CATEGORY_ATTRIBUTES", "123"));
                seed.ReferenceItems.AddRange(Item(categories.Id, "CATEGORIES", "123", "Bluz"), Item(brands.Id, "BRANDS", "456", "Ravencia"));
                seed.CategoryMappings.Add(new CategoryMapping { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, SnapshotId = categories.Id, LocalId = categoryId, ExternalId = "123", Status = "VERIFIED", VerifiedAt = now, Version = 1 });
                seed.BrandMappings.Add(new BrandMapping { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, SnapshotId = brands.Id, LocalId = brandId, ExternalId = "456", Status = "VERIFIED", VerifiedAt = now, Version = 1 });
                foreach (var card in cards)
                    seed.ChannelListingProfiles.Add(new ChannelListingProfile { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, ProductId = card.Id, Enabled = true, DesiredStatus = "ACTIVE", ActualStatus = "DRAFT", Version = 1 });
                foreach (var variant in variants)
                    seed.ChannelOffers.Add(new ChannelOffer { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, VariantId = variant.Id, ListPrice = 900m, SalePrice = 630m, Currency = "TRY", VatRate = 10m, VatInclusion = "INCLUDED", RoundingMode = "HALF_EVEN", Status = "ACTIVE", PriceVersion = 1, Version = 1 });
            }
            foreach (var variant in variants)
                seed.InventoryItems.Add(new InventoryItem { Id = Guid.CreateVersion7(), TenantId = tenant.Id, VariantId = variant.Id, LocationCode = "MAIN", OnHand = 5m, Available = 5m, ProjectionVersion = 1, Version = 1 });
            await seed.SaveChangesAsync();
        }

        async Task<Dictionary<string, string>> PayloadsAsync()
        {
            await using var db = fixture.CreateContext();
            var configuration = new ConfigurationBuilder().Build();
            var payloads = new Dictionary<string, string>(StringComparer.Ordinal);
            void Add(string what, object result) => payloads.Add(what, JsonSerializer.Serialize(result));
            foreach (var connection in connections)
            {
                foreach (var card in cards)
                {
                    var key = $"{connection.PlatformCode} {(card.Id == zero.Id ? "zero" : "24")}";
                    switch (connection.PlatformCode)
                    {
                        case "TRENDYOL":
                            Add($"{key} publication", await new ProductPublicationComposer(db, configuration).BuildAsync(tenant.Id, card.Id, connection.Id, CancellationToken.None));
                            Add($"{key} update", await new ProductUpdateComposer(db, configuration).BuildAsync(tenant.Id, card.Id, connection.Id, CancellationToken.None));
                            Add($"{key} price-stock", await new PriceInventoryComposer(db).BuildAsync(tenant.Id, connection.Id, CancellationToken.None, productId: card.Id));
                            break;
                        case "HEPSIBURADA":
                            Add($"{key} publication", await new HepsiburadaProductPublicationComposer(db, configuration).BuildAsync(tenant.Id, card.Id, connection.Id, CancellationToken.None));
                            Add($"{key} update", await new HepsiburadaProductUpdateComposer(db, configuration).BuildAsync(tenant.Id, card.Id, connection.Id, CancellationToken.None));
                            break;
                        case "N11":
                            Add($"{key} publication", await new N11ProductPublicationComposer(db, configuration).BuildAsync(tenant.Id, card.Id, connection.Id, CancellationToken.None));
                            Add($"{key} update", await new N11ProductUpdateComposer(db, configuration).BuildAsync(tenant.Id, card.Id, connection.Id, null, CancellationToken.None));
                            break;
                        case "PAZARAMA":
                            Add($"{key} publication", await new PazaramaProductPublicationComposer(db, configuration).BuildAsync(tenant.Id, card.Id, connection.Id, CancellationToken.None));
                            Add($"{key} update", await new PazaramaProductUpdateComposer(db).BuildAsync(tenant.Id, card.Id, connection.Id, CancellationToken.None));
                            break;
                        case "PTTAVM":
                            Add($"{key} publication", await new PttAvmProductPublicationComposer(db, configuration).BuildAsync(tenant.Id, card.Id, connection.Id, CancellationToken.None));
                            Add($"{key} update", await new PttAvmProductUpdateComposer(db, configuration).BuildAsync(tenant.Id, card.Id, connection.Id, [.. ProductUpdateFields.Known.Order(StringComparer.Ordinal)], CancellationToken.None));
                            break;
                        case "SHOPIFY":
                            Add($"{key} publication", await new ShopifyProductPublicationComposer(db, configuration).BuildAsync(tenant.Id, card.Id, connection.Id, CancellationToken.None));
                            break;
                    }
                }
            }
            return payloads;
        }

        // The card exactly as the previous code stored a user's 0: null. Where its own warranty would go, the new code
        // refuses it as missing; everything else builds as for any card.
        var stored = await PayloadsAsync();
        string[] cardWarrantyReaders = ["HEPSIBURADA zero publication", "HEPSIBURADA zero update"];
        foreach (var key in cardWarrantyReaders)
            Assert.Contains($"\"Code\":\"{ProductWarranty.RequiredCode}\"", stored[key], StringComparison.Ordinal);

        // The migration writes 0 (its own statement, narrowed to this tenant).
        await using (var db = fixture.CreateContext())
            foreach (var operation in new WarrantyZeroIsAValue().UpOperations.OfType<SqlOperation>())
                await RunForTenantAsync(db, operation.Sql, tenant.Id);
        var migrated = await PayloadsAsync();

        // Every other payload of both cards, Trendyol, n11, Pazarama, PttAVM and Shopify included, byte for byte the same;
        // the 24-month card's Hepsiburada payloads too.
        Assert.Equal(stored.Keys.Order(StringComparer.Ordinal), migrated.Keys.Order(StringComparer.Ordinal));
        foreach (var (key, payload) in stored.Where(pair => !cardWarrantyReaders.Contains(pair.Key)))
            Assert.Equal(payload, migrated[key]);
        Assert.Equal(2, migrated.Count(pair => pair.Key.StartsWith("TRENDYOL", StringComparison.Ordinal) && pair.Key.EndsWith("publication", StringComparison.Ordinal) && pair.Value.Contains("\"Succeeded\":true", StringComparison.Ordinal)));
        // The migrated card's Hepsiburada publication is built and carries GarantiSuresi 0, what the previous code's
        // `product.WarrantyMonths ?? 0` sent for its null: the payload is the 24-month card's with that card's own codes
        // and 0 in place of 24, nothing else differs. Its update now stops where the 24-month card's does (no HB code).
        Assert.Equal(HepsiburadaPayload(migrated["HEPSIBURADA 24 publication"], twentyFour, 24), HepsiburadaPayload(migrated["HEPSIBURADA zero publication"], zero, 0));
        Assert.Contains("\"GarantiSuresi\":0", PayloadJson(migrated["HEPSIBURADA zero publication"]), StringComparison.Ordinal);
        Assert.Equal(migrated["HEPSIBURADA 24 update"].Replace(twentyFour.Id.ToString(), zero.Id.ToString(), StringComparison.Ordinal), migrated["HEPSIBURADA zero update"]);

        static string PayloadJson(string result)
        {
            using var document = JsonDocument.Parse(result);
            Assert.True(document.RootElement.GetProperty("Succeeded").GetBoolean(), result);
            return document.RootElement.GetProperty("Value").GetProperty("PayloadJson").GetString()!;
        }

        string HepsiburadaPayload(string result, Product card, int warranty)
        {
            var index = card.Id == zero.Id ? 0 : 1;
            var normalized = PayloadJson(result)
                .Replace(card.Title, "TITLE", StringComparison.Ordinal)
                .Replace($"MZ41{index}", "MODEL", StringComparison.Ordinal)
                .Replace($"86941000{index}", "BARCODE", StringComparison.Ordinal)
                .Replace($"mz41/{index}.jpg", "IMAGE", StringComparison.Ordinal)
                .Replace($"\"GarantiSuresi\":{warranty},", "\"GarantiSuresi\":-1,", StringComparison.Ordinal);
            // One item per variant; the two sizes' order is the variants' id order, which differs between the cards.
            using var items = JsonDocument.Parse(normalized);
            return string.Join("\n", items.RootElement.EnumerateArray().Select(item => item.GetRawText()).Order(StringComparer.Ordinal));
        }
    }
}
