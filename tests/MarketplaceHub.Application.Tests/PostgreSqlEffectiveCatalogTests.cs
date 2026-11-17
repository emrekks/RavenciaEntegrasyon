using MarketplaceHub.Application.Tests.Contracts;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// KATALOG-14 phase D5b (owner, 17 Nov 2026): the name, description, VAT, delivery days, warranty, brand, category and desi
// are the model's; no colour or variant holds its own. A value given to one colour goes to the model and every colour
// (ModelMirror), a marketplace import never moves a model (the difference is warned about), and the Worker's startup
// pass keeps a value the previous release wrote to a card during a deploy (UpdatedAt after its model's).
[Collection("postgres-integration")]
public sealed class PostgreSqlEffectiveCatalogTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    private static Task<ProductModel> ModelOfAsync(AppDbContext db, Guid cardId) =>
        db.ProductModels.AsNoTracking().SingleAsync(x => x.Id == db.Products.Where(p => p.Id == cardId).Select(p => p.FamilyId).Single());

    [PostgreSqlFact]
    public async Task AValueGivenToOneColour_GoesToTheModelAndEveryColour()
    {
        var seeded = await ModelComposerGoldenPostgreSqlTests.SeedAsync(fixture, "same");
        var card = seeded.Cards[1];
        await using (var db = fixture.CreateContext())
        {
            // One colour's VAT, delivery days, description and name (an Excel row of one card, the API).
            var stored = await db.Products.SingleAsync(x => x.Id == card.Id);
            stored.VatRate = 20m;
            stored.DeliveryDays = 5;
            stored.Description = "Yeni ortak açıklama";
            stored.Title = "Kadın Bordo Yeni Bluz";
            (await db.ProductVariants.Where(x => x.ProductId == card.Id).OrderBy(x => x.SortOrder).FirstAsync()).Desi = 3m;
            await db.SaveChangesAsync();
            var applied = Assert.Single(db.ModelMirrorReport);
            Assert.Equal(3, applied.Colours);
            Assert.False(applied.Refused);
        }

        await using (var db = fixture.CreateContext())
        {
            var model = await ModelOfAsync(db, card.Id);
            Assert.Equal((20m, (int?)5, "Yeni ortak açıklama", "Kadın Bordo Yeni Bluz", "Bordo", 3m), (model.VatRate, model.DeliveryDays, model.Description, model.Title, model.TitleColor, model.Desi));
            var cards = await db.Products.AsNoTracking().Where(x => x.TenantId == seeded.Tenant.Id).ToListAsync();
            Assert.All(cards, x => Assert.Equal((20m, (int?)5, "Yeni ortak açıklama"), (x.VatRate, x.DeliveryDays, x.Description)));
            Assert.Equal(["Kadın Bordo Yeni Bluz", "Kadın Gri Yeni Bluz", "Kadın Siyah Yeni Bluz"], cards.Select(x => x.Title).Order(StringComparer.Ordinal));
            Assert.All(await db.ProductVariants.AsNoTracking().Where(x => x.TenantId == seeded.Tenant.Id).ToListAsync(), x => Assert.Equal(3m, x.Desi));
            // The overrides are neither read nor written.
            Assert.All(cards, x => Assert.Equal((null, null), (x.TitleOverride, x.DescriptionOverride)));
            var read = await EffectiveCatalog.Products(db).Where(x => x.TenantId == seeded.Tenant.Id).ToListAsync();
            Assert.All(read, x => Assert.Equal((20m, (int?)5, "Yeni ortak açıklama"), (x.VatRate, x.DeliveryDays, x.Description)));

            // The invoice: every colour is invoiced at the model's rate.
            var trendyol = seeded.Connections.Single(x => x.PlatformCode == "TRENDYOL");
            foreach (var colour in seeded.Cards)
            {
                var variant = await db.ProductVariants.AsNoTracking().Where(x => x.ProductId == colour.Id).OrderBy(x => x.SortOrder).FirstAsync();
                var line = new OrderLine { Id = Guid.CreateVersion7(), TenantId = seeded.Tenant.Id, ExternalLineId = $"l-{colour.Id:N}", Sku = variant.Sku, TitleSnapshot = "Bluz", RawStatus = "Created", VariantId = variant.Id };
                Assert.Equal(20m, (await InvoiceLineVatRates.ResolveAsync(db, seeded.Tenant.Id, "TRENDYOL", trendyol.Id, [line], CancellationToken.None))[line.Id]);
            }
        }
    }

    [PostgreSqlFact]
    public async Task AMarketplaceImport_NeverMovesTheModel_TheColourTakesItBack_AndTheDifferenceIsWarned()
    {
        var seeded = await ModelComposerGoldenPostgreSqlTests.SeedAsync(fixture, "same");
        var card = seeded.Cards[1];
        await using (var db = fixture.CreateContext())
        {
            db.MarketplaceCatalogImport = true;
            (await db.Products.SingleAsync(x => x.Id == card.Id)).VatRate = 20m;
            await db.SaveChangesAsync();
            Assert.True(Assert.Single(db.ModelMirrorReport).Refused);
        }
        await using (var db = fixture.CreateContext())
        {
            Assert.Equal(10m, (await ModelOfAsync(db, card.Id)).VatRate);
            Assert.Equal(10m, (await db.Products.AsNoTracking().SingleAsync(x => x.Id == card.Id)).VatRate);
            var issue = await db.OperationalIssues.AsNoTracking().SingleAsync(x => x.TenantId == seeded.Tenant.Id && x.Code == ProductModelValues.DifferCode && x.DedupeKey.StartsWith(ModelMirror.DifferDedupePrefix));
            Assert.Equal(IssueStatus.Open, issue.Status);
            Assert.Contains("MZ300 modelinin pazaryerinden gelen", issue.Summary);
            Assert.Contains("KDV (%20 dahil; modelin: %10 dahil)", issue.Summary);

            // The owner then gives the model a value (the editor, Excel): the warning is settled.
            (await db.Products.SingleAsync(x => x.Id == card.Id)).VatRate = 20m;
            await db.SaveChangesAsync();
            Assert.Equal(20m, (await ModelOfAsync(db, card.Id)).VatRate);
            Assert.Equal(IssueStatus.Resolved, (await db.OperationalIssues.AsNoTracking().SingleAsync(x => x.Id == issue.Id)).Status);
        }
    }

    // KATALOG-14 phase D5b review 4: a marketplace import opens a new colour of an existing model with no warranty of its own
    // (the listing carries none: 0), no delivery days, no VAT, no category and no description, and brings an existing
    // colour without a category or description. Unknown is no difference: the colours take the model's values and no
    // PRODUCT_MODEL_VALUES_DIFFER warning opens.
    [PostgreSqlFact]
    public async Task AnImportsUnknownValues_AreNoDifference_TheColourTakesTheModels_AndNothingIsWarned()
    {
        var seeded = await ModelComposerGoldenPostgreSqlTests.SeedAsync(fixture, "same");
        var now = DateTimeOffset.UtcNow;
        Guid added;
        ProductModel model;
        await using (var db = fixture.CreateContext())
        {
            model = await ModelOfAsync(db, seeded.Cards[0].Id);
            db.MarketplaceCatalogImport = true;
            var card = new Product { Id = Guid.CreateVersion7(), TenantId = seeded.Tenant.Id, Title = ProductFamilySharedFields.TitleFor(model.Title, model.TitleColor, "Mavi"), Description = "", BrandId = model.BrandId, WarrantyMonths = 0, Status = ProductStatus.Active, CreatedAt = now, UpdatedAt = now, Version = 1 };
            db.Products.Add(card);
            db.ProductVariants.Add(new ProductVariant { Id = Guid.CreateVersion7(), TenantId = seeded.Tenant.Id, ProductId = card.Id, Sku = $"MAVI-{Guid.NewGuid():N}"[..20], SkuNormalized = "MAVI", ModelCode = model.ModelCode, OptionSignature = "Renk: Mavi | Beden: M", CreatedAt = now, UpdatedAt = now });
            var existing = await db.Products.SingleAsync(x => x.Id == seeded.Cards[1].Id);
            existing.CategoryId = null;
            existing.Description = "";
            await db.SaveChangesAsync();
            added = card.Id;
            Assert.DoesNotContain(db.ModelMirrorReport, x => x.Refused);
        }
        await using (var db = fixture.CreateContext())
        {
            Assert.False(await db.OperationalIssues.AnyAsync(x => x.TenantId == seeded.Tenant.Id && x.Code == ProductModelValues.DifferCode && x.Status == IssueStatus.Open));
            var after = await ModelOfAsync(db, seeded.Cards[0].Id);
            Assert.Equal((model.VatRate, model.CategoryId, model.WarrantyMonths, model.DeliveryDays, model.Description), (after.VatRate, after.CategoryId, after.WarrantyMonths, after.DeliveryDays, after.Description));
            var newColour = await db.Products.AsNoTracking().SingleAsync(x => x.Id == added);
            Assert.Equal((after.Id, model.VatRate, model.CategoryId, model.DeliveryDays, model.Description), (newColour.FamilyId!.Value, newColour.VatRate, newColour.CategoryId, newColour.DeliveryDays, newColour.Description));
            var existing = await db.Products.AsNoTracking().SingleAsync(x => x.Id == seeded.Cards[1].Id);
            Assert.Equal((model.CategoryId, model.Description), (existing.CategoryId, existing.Description));
        }
    }

    // What the automatic paths may not write: every column of the eight model fields of the tenant's models, cards and
    // variants, their Version and UpdatedAt, and the marketplace links' sync marks.
    private static async Task<string> ModelFieldsSnapshotAsync(AppDbContext db, Guid tenantId) =>
        System.Text.Json.JsonSerializer.Serialize(new
        {
            Models = await db.ProductModels.AsNoTracking().Where(x => x.TenantId == tenantId).OrderBy(x => x.Id)
                .Select(x => new { x.Id, x.Title, x.TitleColor, x.Description, x.VatRate, x.VatInclusion, x.CategoryId, x.WarrantyMonths, x.DeliveryDays, x.Desi, x.BrandId, x.Version, x.UpdatedAt }).ToListAsync(),
            Cards = await db.Products.AsNoTracking().Where(x => x.TenantId == tenantId).OrderBy(x => x.Id)
                .Select(x => new { x.Id, x.FamilyId, x.Title, x.Description, x.VatRate, x.VatInclusion, x.CategoryId, x.WarrantyMonths, x.DeliveryDays, x.BrandId, x.Version, x.UpdatedAt }).ToListAsync(),
            Variants = await db.ProductVariants.AsNoTracking().Where(x => x.TenantId == tenantId).OrderBy(x => x.Id).Select(x => new { x.Id, x.Desi, x.Version, x.UpdatedAt }).ToListAsync(),
            Links = await db.MarketplaceProductLinks.AsNoTracking().Where(x => x.TenantId == tenantId).OrderBy(x => x.Id).Select(x => new { x.Id, x.SyncStatus, x.DirtyFieldsJson, x.Version }).ToListAsync(),
        });

    // The deploy: the previous release (phase A) wrote a card alone, after the model's last change. KATALOG-14 phase D5b
    // review (17 Nov 2026, "yeni kart kazanır" withdrawn): the Worker's startup pass, the nightly scan and the deploy refresh
    // write none of the eight model fields and mark no link; they open PRODUCT_MODEL_VALUES_DIFFER naming the model, the
    // colour, the field and both values. The invoice takes the model's VAT meanwhile.
    [PostgreSqlFact]
    public async Task ACardThePreviousReleaseChangedDuringADeploy_IsOnlyWarnedAbout_NothingIsWritten()
    {
        var seeded = await ModelComposerGoldenPostgreSqlTests.SeedAsync(fixture, "same");
        var changed = seeded.Cards[1];
        string before;
        await using (var db = fixture.CreateContext())
        {
            var model = await ModelOfAsync(db, changed.Id);
            await db.Database.ExecuteSqlInterpolatedAsync($"""UPDATE catalog.products SET "VatRate" = 20, "UpdatedAt" = {model.UpdatedAt.AddMinutes(5)} WHERE "Id" = {changed.Id}""");
            before = await ModelFieldsSnapshotAsync(db, seeded.Tenant.Id);
        }
        await using (var db = fixture.CreateContext())
        {
            var differing = await ProductModelValues.AlignStaleAsync(db, DateTimeOffset.UtcNow, CancellationToken.None);
            Assert.Contains(differing, x => x.StartsWith("MZ300", StringComparison.Ordinal));
            await ProductModelValues.RefreshTenantAsync(db, seeded.Tenant.Id, DateTimeOffset.UtcNow, CancellationToken.None);
            await ProductModelValues.RefreshAllAsync(db, DateTimeOffset.UtcNow, CancellationToken.None);
            await ProductModelScan.RunTenantAsync(db, seeded.Tenant.Id, DateTimeOffset.UtcNow, CancellationToken.None);
            Assert.Equal(before, await ModelFieldsSnapshotAsync(db, seeded.Tenant.Id));
            Assert.Equal(10m, (await ModelOfAsync(db, changed.Id)).VatRate);

            var issue = await db.OperationalIssues.AsNoTracking().SingleAsync(x => x.TenantId == seeded.Tenant.Id && x.DedupeKey.StartsWith(ModelMirror.GapDedupePrefix));
            Assert.Equal((ProductModelValues.DifferCode, IssueStatus.Open, 1), (issue.Code, issue.Status, issue.OccurrenceCount));
            Assert.Contains("MZ300 modelinin", issue.Summary);
            Assert.Contains("Bordo rengi: KDV %20 dahil (modelin: %10 dahil)", issue.Summary);

            // The invoice: the colour is invoiced at the model's rate.
            var variant = await db.ProductVariants.AsNoTracking().Where(x => x.ProductId == changed.Id).OrderBy(x => x.SortOrder).FirstAsync();
            var line = new OrderLine { Id = Guid.CreateVersion7(), TenantId = seeded.Tenant.Id, ExternalLineId = "l-gap", Sku = variant.Sku, TitleSnapshot = "Bluz", RawStatus = "Created", VariantId = variant.Id };
            var trendyol = seeded.Connections.Single(x => x.PlatformCode == "TRENDYOL");
            Assert.Equal(10m, (await InvoiceLineVatRates.ResolveAsync(db, seeded.Tenant.Id, "TRENDYOL", trendyol.Id, [line], CancellationToken.None))[line.Id]);

            // The owner's own save closes the difference (every colour takes the value) and the next scan settles the warning.
            (await db.Products.SingleAsync(x => x.Id == seeded.Cards[0].Id)).VatRate = 20m;
            await db.SaveChangesAsync();
            Assert.All(await db.Products.AsNoTracking().Where(x => x.TenantId == seeded.Tenant.Id).ToListAsync(), x => Assert.Equal(20m, x.VatRate));
            await ProductModelValues.RefreshTenantAsync(db, seeded.Tenant.Id, DateTimeOffset.UtcNow, CancellationToken.None);
            Assert.Equal(IssueStatus.Resolved, (await db.OperationalIssues.AsNoTracking().SingleAsync(x => x.Id == issue.Id)).Status);
        }
    }

    // KATALOG-14 phase D5b review 2 (17 Nov 2026): no automatic path touches a model's brand. A coded colour without a brand
    // (newer than its model) neither erases the model's brand nor counts as another brand; one the previous release left on
    // a model of its own is linked to its code's model, whose brand stays; an own model whose card's brand the previous
    // release changed keeps its brand and the difference is only warned about.
    [PostgreSqlFact]
    public async Task NoAutomaticPath_TouchesAModelsBrand_AndABlankBrandIsNoOtherBrand()
    {
        var seeded = await ModelComposerGoldenPostgreSqlTests.SeedAsync(fixture, "same");
        var now = DateTimeOffset.UtcNow;
        Guid brand, blankCard, ownCard, ownModel;
        await using (var db = fixture.CreateContext())
        {
            brand = (await ModelOfAsync(db, seeded.Cards[0].Id)).BrandId!.Value;
            // A colour of MZ300 without a brand, changed after its model.
            var model = await ModelOfAsync(db, seeded.Cards[1].Id);
            await db.Database.ExecuteSqlInterpolatedAsync($"""UPDATE catalog.products SET "BrandId" = NULL, "UpdatedAt" = {model.UpdatedAt.AddMinutes(5)} WHERE "Id" = {seeded.Cards[1].Id}""");

            // A new MZ300 colour without a brand, then left by the previous release on a model of its own.
            var card = new Product { Id = Guid.CreateVersion7(), TenantId = seeded.Tenant.Id, Title = "Kadın Mavi Bluz", Description = model.Description, VatRate = 10m, VatInclusion = "INCLUDED", Status = ProductStatus.Active, CreatedAt = now, UpdatedAt = now };
            db.Products.Add(card);
            db.ProductVariants.Add(new ProductVariant { Id = Guid.CreateVersion7(), TenantId = seeded.Tenant.Id, ProductId = card.Id, Sku = $"MAVI-{Guid.NewGuid():N}"[..20], SkuNormalized = "MAVI", ModelCode = "MZ300", OptionSignature = "Renk: Mavi | Beden: M", CreatedAt = now, UpdatedAt = now });
            // A codeless card on a model of its own.
            var codeless = new Product { Id = Guid.CreateVersion7(), TenantId = seeded.Tenant.Id, Title = "Kodsuz Şal", Description = "Kodsuz", BrandId = brand, VatRate = 10m, VatInclusion = "INCLUDED", Status = ProductStatus.Active, CreatedAt = now, UpdatedAt = now };
            db.Products.Add(codeless);
            db.ProductVariants.Add(new ProductVariant { Id = Guid.CreateVersion7(), TenantId = seeded.Tenant.Id, ProductId = codeless.Id, Sku = $"SAL-{Guid.NewGuid():N}"[..20], SkuNormalized = "SAL", OptionSignature = "SAL", CreatedAt = now, UpdatedAt = now });
            await db.SaveChangesAsync();
            Assert.Equal(model.Id, (await db.Products.AsNoTracking().SingleAsync(x => x.Id == card.Id)).FamilyId);
            blankCard = card.Id;
            ownCard = codeless.Id;
            ownModel = (await db.Products.AsNoTracking().SingleAsync(x => x.Id == codeless.Id)).FamilyId!.Value;
            var stray = Guid.CreateVersion7();
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO catalog.product_models ("Id", "TenantId", "BrandId", "ModelCode", "ModelCodeKey", "Title", "Description", "FieldsSavedAt", "CreatedAt", "UpdatedAt", "Version")
                VALUES ({stray}, {seeded.Tenant.Id}, NULL, '', {$"#{card.Id:N}"}, 'Kadın Mavi Bluz', '', NULL, {now}, {now}, 1)
                """);
            await db.Database.ExecuteSqlInterpolatedAsync($"""UPDATE catalog.products SET "FamilyId" = {stray}, "BrandId" = NULL WHERE "Id" = {card.Id}""");
            // The previous release gave the codeless card another brand without its model.
            var other = new Brand { Id = Guid.CreateVersion7(), TenantId = seeded.Tenant.Id, Name = "Lavian", NormalizedName = "LAVIAN", IsActive = true, CreatedAt = now, UpdatedAt = now, Version = 1 };
            db.Brands.Add(other);
            await db.SaveChangesAsync();
            await db.Database.ExecuteSqlInterpolatedAsync($"""UPDATE catalog.products SET "BrandId" = {other.Id}, "UpdatedAt" = {now.AddMinutes(10)} WHERE "Id" = {codeless.Id}""");
        }

        await using (var db = fixture.CreateContext())
        {
            await ProductModelValues.AlignStaleAsync(db, DateTimeOffset.UtcNow, CancellationToken.None);
            await ProductModelScan.RunTenantAsync(db, seeded.Tenant.Id, DateTimeOffset.UtcNow, CancellationToken.None);
            await ProductModelValues.RefreshAllAsync(db, DateTimeOffset.UtcNow, CancellationToken.None);

            var mz300 = await ModelOfAsync(db, seeded.Cards[0].Id);
            Assert.Equal(brand, mz300.BrandId);
            Assert.Equal(mz300.Id, (await db.Products.AsNoTracking().SingleAsync(x => x.Id == blankCard)).FamilyId);
            Assert.Equal(((Guid?)null, (Guid?)null), ((await db.Products.AsNoTracking().SingleAsync(x => x.Id == blankCard)).BrandId, (await db.Products.AsNoTracking().SingleAsync(x => x.Id == seeded.Cards[1].Id)).BrandId));
            Assert.All(await EffectiveCatalog.Products(db).Where(x => x.TenantId == seeded.Tenant.Id && x.Id != ownCard).ToListAsync(), x => Assert.Equal(brand, x.BrandId));
            // The own model keeps its brand; the card's other brand is only warned about.
            Assert.Equal(brand, (await db.ProductModels.AsNoTracking().SingleAsync(x => x.Id == ownModel)).BrandId);
            var gaps = await db.OperationalIssues.AsNoTracking().Where(x => x.TenantId == seeded.Tenant.Id && x.DedupeKey.StartsWith(ModelMirror.GapDedupePrefix)).ToListAsync();
            var gap = Assert.Single(gaps);
            Assert.Contains("marka \"Lavian\"", gap.Summary);
            Assert.False(await db.OperationalIssues.AsNoTracking().AnyAsync(x => x.TenantId == seeded.Tenant.Id && x.Code == ProductModelLinks.AmbiguousCode && x.Status == IssueStatus.Open));
        }
    }

    // A card without a model code holds a model of its own, whose brand follows the card's.
    [PostgreSqlFact]
    public async Task ACodelessCard_HoldsItsOwnModel_WhoseBrandFollowsTheCard()
    {
        var seeded = await ModelComposerGoldenPostgreSqlTests.SeedAsync(fixture, "same");
        var now = DateTimeOffset.UtcNow;
        await using var db = fixture.CreateContext();
        var brands = await db.Brands.AsNoTracking().Where(x => x.TenantId == seeded.Tenant.Id).Select(x => x.Id).ToListAsync();
        var other = new Brand { Id = Guid.CreateVersion7(), TenantId = seeded.Tenant.Id, Name = "Lavian", NormalizedName = "LAVIAN", IsActive = true, CreatedAt = now, UpdatedAt = now, Version = 1 };
        db.Brands.Add(other);
        var card = new Product { Id = Guid.CreateVersion7(), TenantId = seeded.Tenant.Id, Title = "Kodsuz Şal", Description = "Kodsuz", BrandId = brands[0], VatRate = 10m, VatInclusion = "INCLUDED", Status = ProductStatus.Active, CreatedAt = now, UpdatedAt = now };
        db.Products.Add(card);
        db.ProductVariants.Add(new ProductVariant { Id = Guid.CreateVersion7(), TenantId = seeded.Tenant.Id, ProductId = card.Id, Sku = $"SAL-{Guid.NewGuid():N}"[..20], SkuNormalized = "SAL", OptionSignature = "SAL", CreatedAt = now, UpdatedAt = now });
        await db.SaveChangesAsync();
        var model = await ModelOfAsync(db, card.Id);
        Assert.Equal(($"#{card.Id:N}", brands[0]), (model.ModelCodeKey, model.BrandId));

        card.BrandId = other.Id;
        await db.SaveChangesAsync();
        Assert.Equal(other.Id, (await db.ProductModels.AsNoTracking().SingleAsync(x => x.Id == model.Id)).BrandId);
        Assert.Equal(other.Id, (await EffectiveCatalog.Products(db).SingleAsync(x => x.Id == card.Id)).BrandId);
    }
}
