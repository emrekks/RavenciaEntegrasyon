using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// Review (18 Nov 2026) of the model rule "a model is its brand and its code; different brands may share a code; nothing is
// merged" (owner, 17 Nov 2026): every path that asks "is this code in use" asks the reconcile's own linking rule
// (ProductModelLinks.LinkBrand), and a save that gives a card a brand and a code at once never joins another model.
[Collection("postgres-integration")]
public sealed class ModelBrandCodeRulePostgreSqlTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    private sealed record Seed(Tenant Tenant, Brand Ravencia, Brand Lavian);

    private async Task<Seed> SeedAsync(string code)
    {
        var now = DateTimeOffset.UtcNow;
        var tenant = new Tenant { Id = Guid.CreateVersion7(), Code = $"{code}-{Guid.NewGuid():N}", DisplayName = code, Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = now, UpdatedAt = now, Version = 1 };
        var ravencia = new Brand { Id = Guid.CreateVersion7(), TenantId = tenant.Id, Name = "Ravencia", NormalizedName = "RAVENCIA", IsActive = true, CreatedAt = now, UpdatedAt = now, Version = 1 };
        var lavian = new Brand { Id = Guid.CreateVersion7(), TenantId = tenant.Id, Name = "Lavian", NormalizedName = "LAVIAN", IsActive = true, CreatedAt = now, UpdatedAt = now, Version = 1 };
        await using var db = fixture.CreateContext();
        db.Tenants.Add(tenant);
        db.Brands.AddRange(ravencia, lavian);
        await db.SaveChangesAsync();
        return new(tenant, ravencia, lavian);
    }

    // A card with one variant of the given model code (null: none), saved through the context (the reconcile links it).
    private static (Product Card, ProductVariant Variant) AddCard(AppDbContext db, Seed seed, Brand? brand, string? modelCode, string title, decimal? vat = 10m, ProductStatus status = ProductStatus.Active)
    {
        var now = DateTimeOffset.UtcNow;
        var card = new Product { Id = Guid.CreateVersion7(), TenantId = seed.Tenant.Id, Title = title, Description = "", BrandId = brand?.Id, VatRate = vat, VatInclusion = vat is null ? null : "INCLUDED", Status = status, CreatedAt = now, UpdatedAt = now };
        var sku = $"SKU-{Guid.NewGuid():N}"[..20];
        var variant = new ProductVariant { Id = Guid.CreateVersion7(), TenantId = seed.Tenant.Id, ProductId = card.Id, Sku = sku, SkuNormalized = sku, ModelCode = modelCode, OptionSignature = $"Renk: {title}", Status = status, CreatedAt = now, UpdatedAt = now };
        db.Products.Add(card);
        db.ProductVariants.Add(variant);
        return (card, variant);
    }

    private CatalogService Catalog(AppDbContext db) =>
        new(db, null!, new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(), fixture.TimeProvider, new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions()));

    private static string Code(string prefix) => $"{prefix}{Guid.NewGuid():N}"[..12].ToUpperInvariant();

    // Finding 2: a card without a brand joins the code's one branded model (LinkBrand), so for it such a code is in use: the
    // holder check (Yeni Ürün's field, the import's code change) names that model's card. Two brands holding the code
    // leave a card without a brand its own model: free. A brand asks only its own model.
    [PostgreSqlFact]
    public async Task ACodeWithOneBrandedModel_IsInUseForACardWithoutBrand_ButFreeForAnotherBrand()
    {
        var seed = await SeedAsync("brandless-holder");
        var code = Code("KX");
        Product ravenciaCard;
        await using (var db = fixture.CreateContext())
        {
            ravenciaCard = AddCard(db, seed, seed.Ravencia, code, "Kadın Siyah Etek").Card;
            await db.SaveChangesAsync();
        }
        await using (var db = fixture.CreateContext())
        {
            Assert.Equal(ravenciaCard.Id, (await ModelCodeOwnership.FindAsync(db, seed.Tenant.Id, null, [code.ToLowerInvariant()], CancellationToken.None))?.ProductId);
            Assert.Equal(ravenciaCard.Id, (await ModelCodeOwnership.FindAsync(db, seed.Tenant.Id, seed.Ravencia.Id, [code], CancellationToken.None))?.ProductId);
            Assert.Null(await ModelCodeOwnership.FindAsync(db, seed.Tenant.Id, seed.Lavian.Id, [code], CancellationToken.None));
            // The import's check (a card the connection does not know) follows the same rule.
            Assert.Equal(ravenciaCard.Id, (await ModelCodeOwnership.FindUnlinkedHolderAsync(db, seed.Tenant.Id, Guid.CreateVersion7(), null, code, Guid.CreateVersion7(), CancellationToken.None))?.ProductId);
            Assert.Null(await ModelCodeOwnership.FindUnlinkedHolderAsync(db, seed.Tenant.Id, Guid.CreateVersion7(), seed.Lavian.Id, code, Guid.CreateVersion7(), CancellationToken.None));
        }
        // Lavian opens its own model of the code: two branded models, a card without a brand joins neither.
        await using (var db = fixture.CreateContext())
        {
            AddCard(db, seed, seed.Lavian, code, "Kadın Mavi Etek");
            await db.SaveChangesAsync();
            Assert.Equal(2, await db.ProductModels.CountAsync(x => x.TenantId == seed.Tenant.Id && x.ModelCodeKey == code));
            Assert.Null(await ModelCodeOwnership.FindAsync(db, seed.Tenant.Id, null, [code], CancellationToken.None));
            Assert.Null(await ModelCodeOwnership.FindUnlinkedHolderAsync(db, seed.Tenant.Id, Guid.CreateVersion7(), null, code, Guid.CreateVersion7(), CancellationToken.None));
        }
    }

    // Finding 2, editor: "Yeni Ürün" without a brand with a code Ravencia alone holds joined Ravencia's model (and took its
    // name and brand); a card without a brand changing its code to it did the same. Both are refused now; with a brand of
    // its own (Lavian) the code is free and opens Lavian's model.
    [PostgreSqlFact]
    public async Task Editor_ABrandlessNewCardOrCodeChange_ToTheCodeOfTheOneBrandedModel_IsRefused()
    {
        var seed = await SeedAsync("brandless-editor");
        var code = Code("KE");
        await using var db = fixture.CreateContext();
        var service = Catalog(db);
        var held = await service.CreateProductAsync(seed.Tenant.Id, new CreateProductCommand("Kadın Kot Etek", "Açıklama", seed.Ravencia.Id, null, [new CreateVariantCommand($"{code}-R1", null, code)], VatRate: 10m, VatInclusion: "INCLUDED"), CancellationToken.None);
        Assert.True(held.Succeeded, held.Error?.Message);

        db.ChangeTracker.Clear();
        var brandless = await service.CreateProductAsync(seed.Tenant.Id, new CreateProductCommand("Markasız Taslak", "", null, null, [new CreateVariantCommand($"{code}-B1", null, code)], Status: "DRAFT"), CancellationToken.None);
        Assert.Equal((409, ModelCodeUniquenessPolicy.ConflictCode), (brandless.Error?.Status, brandless.Error?.Code));

        db.ChangeTracker.Clear();
        var other = await service.CreateProductAsync(seed.Tenant.Id, new CreateProductCommand("Markasız Gömlek", "", null, null, [new CreateVariantCommand($"{code}-B2", null, $"{code}-BASKA")], Status: "DRAFT"), CancellationToken.None);
        Assert.True(other.Succeeded, other.Error?.Message);
        var otherVariant = await db.ProductVariants.AsNoTracking().SingleAsync(x => x.ProductId == other.Value!.Id);
        db.ChangeTracker.Clear();
        var recoded = await service.UpdateProductAsync(seed.Tenant.Id, other.Value!.Id, other.Value.Version, new UpdateProductCommand("Markasız Gömlek", "", null, null,
            VariantUpdates: [new UpdateVariantCommand(otherVariant.Id, otherVariant.Sku, null, code)], Status: "DRAFT"), CancellationToken.None);
        Assert.Equal((409, ModelCodeUniquenessPolicy.ChangeConflictCode), (recoded.Error?.Status, recoded.Error?.Code));

        db.ChangeTracker.Clear();
        var lavian = await service.CreateProductAsync(seed.Tenant.Id, new CreateProductCommand("Kadın Mavi Kot Etek", "Açıklama", seed.Lavian.Id, null, [new CreateVariantCommand($"{code}-L1", null, code)], VatRate: 20m, VatInclusion: "INCLUDED"), CancellationToken.None);
        Assert.True(lavian.Succeeded, lavian.Error?.Message);

        var cards = await db.Products.AsNoTracking().Where(x => x.TenantId == seed.Tenant.Id).ToListAsync();
        Assert.Equal(3, cards.Count);
        var models = await db.ProductModels.AsNoTracking().Where(x => x.TenantId == seed.Tenant.Id && x.ModelCodeKey == code).ToListAsync();
        Assert.Equal(new Guid?[] { seed.Ravencia.Id, seed.Lavian.Id }.Order(), models.Select(x => x.BrandId).Order());
        Assert.Null((await db.Products.AsNoTracking().SingleAsync(x => x.Id == other.Value.Id)).BrandId);
    }

    // Findings 1 and 4, editor: a brand and a code changed in one save are checked under the new brand. Lavian's card moving
    // to Ravencia with a code Ravencia holds is refused; with a code only Lavian holds elsewhere it is free (Ravencia
    // holds no such model), so the old brand is not what is asked.
    [PostgreSqlFact]
    public async Task Editor_BrandAndCodeChangedTogether_AreCheckedUnderTheNewBrand()
    {
        var seed = await SeedAsync("brand-code-editor");
        var held = Code("KR");
        var lavianOnly = Code("KL");
        Guid lavianCardId;
        ProductVariant lavianVariant;
        await using (var setup = fixture.CreateContext())
        {
            AddCard(setup, seed, seed.Ravencia, held, "Ravencia Etek", status: ProductStatus.Draft);
            AddCard(setup, seed, seed.Lavian, lavianOnly, "Lavian Bluz", status: ProductStatus.Draft);
            var lavian = AddCard(setup, seed, seed.Lavian, Code("KY"), "Lavian Etek", status: ProductStatus.Draft);
            await setup.SaveChangesAsync();
            lavianCardId = lavian.Card.Id;
            lavianVariant = lavian.Variant;
        }
        await using var db = fixture.CreateContext();
        var service = Catalog(db);
        var version = (await db.Products.AsNoTracking().SingleAsync(x => x.Id == lavianCardId)).Version;
        var taken = await service.UpdateProductAsync(seed.Tenant.Id, lavianCardId, version, new UpdateProductCommand("Lavian Etek", "", seed.Ravencia.Id, null,
            VariantUpdates: [new UpdateVariantCommand(lavianVariant.Id, lavianVariant.Sku, null, held)], VatRate: 10m, VatInclusion: "INCLUDED", Status: "DRAFT"), CancellationToken.None);
        Assert.Equal((409, ModelCodeUniquenessPolicy.ChangeConflictCode), (taken.Error?.Status, taken.Error?.Code));

        db.ChangeTracker.Clear();
        var free = await service.UpdateProductAsync(seed.Tenant.Id, lavianCardId, version, new UpdateProductCommand("Lavian Etek", "", seed.Ravencia.Id, null,
            VariantUpdates: [new UpdateVariantCommand(lavianVariant.Id, lavianVariant.Sku, null, lavianOnly)], VatRate: 10m, VatInclusion: "INCLUDED", Status: "DRAFT"), CancellationToken.None);
        Assert.True(free.Succeeded, free.Error?.Message);
        var card = await db.Products.AsNoTracking().SingleAsync(x => x.Id == lavianCardId);
        var model = await db.ProductModels.AsNoTracking().SingleAsync(x => x.Id == card.FamilyId);
        Assert.Equal((seed.Ravencia.Id, lavianOnly), (model.BrandId!.Value, model.ModelCodeKey));
        Assert.Equal(2, await db.ProductModels.CountAsync(x => x.TenantId == seed.Tenant.Id && x.ModelCodeKey == lavianOnly));
    }

    // Finding 1, the last guards (ProductModelLinks): a save that gives a card another brand and a code that brand's model
    // holds (no check before it, as a raw API or a future path might) fails in its transaction: nothing is merged and the
    // card keeps its brand, code and model. A coded card is stopped where its model takes the new code (asked under the
    // new brand); a card without a code yet, which has no model of a code to rename, by the reconcile. The same save with a
    // free code moves the card, and a code the old brand holds elsewhere is no reason to refuse it.
    [PostgreSqlFact]
    public async Task ASaveGivingABrandAndACodeTogether_IsRefused_WhenThatBrandsModelHoldsTheCode()
    {
        var seed = await SeedAsync("brand-code-guard");
        var held = Code("GR");
        var own = Code("GY");
        var lavianHeld = Code("GL");
        Guid cardId, variantId, modelId, codelessId, codelessVariantId;
        await using (var setup = fixture.CreateContext())
        {
            AddCard(setup, seed, seed.Ravencia, held, "Ravencia Etek");
            AddCard(setup, seed, seed.Lavian, lavianHeld, "Lavian Bluz");
            var lavian = AddCard(setup, seed, seed.Lavian, own, "Lavian Etek");
            var codeless = AddCard(setup, seed, seed.Lavian, null, "Lavian Kodsuz");
            await setup.SaveChangesAsync();
            (cardId, variantId) = (lavian.Card.Id, lavian.Variant.Id);
            (codelessId, codelessVariantId) = (codeless.Card.Id, codeless.Variant.Id);
            modelId = (await setup.Products.AsNoTracking().SingleAsync(x => x.Id == cardId)).FamilyId!.Value;
        }
        async Task<ModelRuleException> RefusedAsync(Guid card, Guid variant, string expectedCode)
        {
            await using var db = fixture.CreateContext();
            (await db.Products.SingleAsync(x => x.Id == card)).BrandId = seed.Ravencia.Id;
            (await db.ProductVariants.SingleAsync(x => x.Id == variant)).ModelCode = held;
            var refused = await Assert.ThrowsAsync<ModelRuleException>(() => db.SaveChangesAsync());
            Assert.Equal(expectedCode, refused.Code);
            return refused;
        }
        Assert.Contains("Ravencia Etek", (await RefusedAsync(cardId, variantId, ModelCodeUniquenessPolicy.ChangeConflictCode)).Message);
        Assert.Contains("Ravencia", (await RefusedAsync(codelessId, codelessVariantId, ModelCodeUniquenessPolicy.BrandCodeTakenCode)).Message);
        await using (var check = fixture.CreateContext())
        {
            var card = await check.Products.AsNoTracking().SingleAsync(x => x.Id == cardId);
            Assert.Equal((seed.Lavian.Id, modelId), (card.BrandId!.Value, card.FamilyId!.Value));
            Assert.Equal(own, (await check.ProductVariants.AsNoTracking().SingleAsync(x => x.Id == variantId)).ModelCode);
            Assert.Equal(seed.Lavian.Id, (await check.Products.AsNoTracking().SingleAsync(x => x.Id == codelessId)).BrandId);
            Assert.Null((await check.ProductVariants.AsNoTracking().SingleAsync(x => x.Id == codelessVariantId)).ModelCode);
            var heldModel = await check.ProductModels.AsNoTracking().SingleAsync(m => m.TenantId == seed.Tenant.Id && m.ModelCodeKey == held);
            Assert.Equal(1, await check.Products.CountAsync(x => x.TenantId == seed.Tenant.Id && x.FamilyId == heldModel.Id));
        }
        // To a code Ravencia does not hold (Lavian does, elsewhere): the card moves to Ravencia's own model of it.
        await using (var db = fixture.CreateContext())
        {
            (await db.Products.SingleAsync(x => x.Id == cardId)).BrandId = seed.Ravencia.Id;
            (await db.ProductVariants.SingleAsync(x => x.Id == variantId)).ModelCode = lavianHeld;
            await db.SaveChangesAsync();
            var card = await db.Products.AsNoTracking().SingleAsync(x => x.Id == cardId);
            var model = await db.ProductModels.AsNoTracking().SingleAsync(x => x.Id == card.FamilyId);
            Assert.Equal((seed.Ravencia.Id, lavianHeld), (model.BrandId!.Value, model.ModelCodeKey));
            Assert.Equal(2, await db.ProductModels.CountAsync(x => x.TenantId == seed.Tenant.Id && x.ModelCodeKey == lavianHeld));
        }
    }

    // Finding 4 (`given is not null` survived a mutation): a blank brand is a brand too. Taking the brand off a linked colour
    // whose code already has a model without a brand would merge the two; it is refused in the save's own transaction, a
    // serializable one included (the editor's: its model write runs only after the commit, too late to stop it).
    [PostgreSqlFact]
    public async Task TakingTheBrandOff_WhenTheCodeHasAModelWithoutBrand_IsRefused_InASerializableSaveToo()
    {
        var seed = await SeedAsync("brand-off-guard");
        var code = Code("GN");
        Guid cardId, modelId;
        await using (var setup = fixture.CreateContext())
        {
            AddCard(setup, seed, null, code, "Markasız Etek");
            var a = AddCard(setup, seed, seed.Ravencia, code, "Ravencia Etek");
            AddCard(setup, seed, seed.Lavian, code, "Lavian Etek");
            await setup.SaveChangesAsync();
            cardId = a.Card.Id;
            modelId = (await setup.Products.AsNoTracking().SingleAsync(x => x.Id == cardId)).FamilyId!.Value;
            Assert.Equal(3, await setup.ProductModels.CountAsync(x => x.TenantId == seed.Tenant.Id && x.ModelCodeKey == code));
        }
        await using (var db = fixture.CreateContext())
        {
            await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
            (await db.Products.SingleAsync(x => x.Id == cardId)).BrandId = null;
            var refused = await Assert.ThrowsAsync<ModelRuleException>(() => db.SaveChangesAsync());
            Assert.Equal(ModelCodeUniquenessPolicy.BrandCodeTakenCode, refused.Code);
            Assert.Contains("markasız", refused.Message);
        }
        await using var check = fixture.CreateContext();
        Assert.Equal((seed.Ravencia.Id, modelId), ((await check.Products.AsNoTracking().SingleAsync(x => x.Id == cardId)).BrandId!.Value, (await check.Products.AsNoTracking().SingleAsync(x => x.Id == cardId)).FamilyId!.Value));
        Assert.Equal(seed.Ravencia.Id, (await check.ProductModels.AsNoTracking().SingleAsync(x => x.Id == modelId)).BrandId);
    }

    // Finding 1, Excel: a file giving Lavian's card Marka = Ravencia and Model Kodu = a code Ravencia holds was checked under
    // Lavian and the card joined Ravencia's model. It is checked under the brand the row will have: refused before anything
    // is written ("birleştirilmez, değişiklik reddedilir"). A code only another Lavian card holds is free for it (Ravencia
    // holds none), and a brand the panel does not have yet holds no code.
    [PostgreSqlFact]
    public async Task Excel_BrandAndCodeInOneFile_AreCheckedUnderTheBrandTheRowWillHave()
    {
        var seed = await SeedAsync("brand-code-excel");
        var held = Code("XR");
        var lavianOnly = Code("XL");
        Guid cardId, variantId;
        await using (var setup = fixture.CreateContext())
        {
            AddCard(setup, seed, seed.Ravencia, held, "Ravencia Etek");
            AddCard(setup, seed, seed.Lavian, lavianOnly, "Lavian Bluz");
            var lavian = AddCard(setup, seed, seed.Lavian, Code("XY"), "Lavian Etek");
            await setup.SaveChangesAsync();
            (cardId, variantId) = (lavian.Card.Id, lavian.Variant.Id);
        }

        async Task<ServiceResult<ProductTransferApplyResult>> ApplyAsync(string brand, string code)
        {
            await using var db = fixture.CreateContext();
            var variant = await db.ProductVariants.AsNoTracking().SingleAsync(x => x.Id == variantId);
            var card = await db.Products.AsNoTracking().SingleAsync(x => x.Id == cardId);
            return await new ProductTransferService(db, fixture.TimeProvider).ApplyAsync(seed.Tenant.Id, Guid.CreateVersion7(), new ApplyProductTransferCommand(
                [new ProductTransferChange(variantId, variant.Version, false, null, false, null, SetModelCode: true, ModelCode: code)],
                [new ProductTransferProductChange(cardId, card.Version, null, null, Brand: brand)]), $"brand-code-{Guid.NewGuid():N}", CancellationToken.None);
        }

        var refused = await ApplyAsync("Ravencia", held);
        Assert.Equal(("PRODUCT_TRANSFER_MODEL_CODE_TAKEN", 409), (refused.Error?.Code, refused.Error?.Status));
        Assert.Contains("Ravencia Etek", refused.Error!.Message);
        await using (var check = fixture.CreateContext())
            Assert.Equal(seed.Lavian.Id, (await check.Products.AsNoTracking().SingleAsync(x => x.Id == cardId)).BrandId);

        var moved = await ApplyAsync("Ravencia", lavianOnly);
        Assert.True(moved.Succeeded, moved.Error?.Message);
        await using (var check = fixture.CreateContext())
        {
            var card = await check.Products.AsNoTracking().SingleAsync(x => x.Id == cardId);
            Assert.Equal(seed.Ravencia.Id, card.BrandId);
            var model = await check.ProductModels.AsNoTracking().SingleAsync(x => x.Id == card.FamilyId);
            Assert.Equal((seed.Ravencia.Id, lavianOnly), (model.BrandId!.Value, model.ModelCodeKey));
        }

        var newBrand = await ApplyAsync("Yeni Marka", held);
        Assert.True(newBrand.Succeeded, newBrand.Error?.Message);
        await using (var check = fixture.CreateContext())
        {
            var card = await check.Products.AsNoTracking().SingleAsync(x => x.Id == cardId);
            var model = await check.ProductModels.AsNoTracking().SingleAsync(x => x.Id == card.FamilyId);
            Assert.Equal((card.BrandId, held), (model.BrandId, model.ModelCodeKey));
            Assert.NotEqual(seed.Ravencia.Id, card.BrandId);
            Assert.Equal(1, await check.Products.CountAsync(x => x.TenantId == seed.Tenant.Id && x.BrandId == seed.Ravencia.Id && x.FamilyId != null && check.ProductModels.Any(m => m.Id == x.FamilyId && m.ModelCodeKey == held)));
        }
    }

    // Finding 4, the import's VAT fallback: a new colour without a VAT takes its own brand's model of the code, not another
    // brand's; without a brand, the code's one branded model, none when two brands hold it.
    [PostgreSqlFact]
    public async Task ImportVatFallback_TakesTheVatOfTheCardsOwnBrandsModel()
    {
        var seed = await SeedAsync("vat-brand-model");
        var code = Code("VB");
        await using (var setup = fixture.CreateContext())
        {
            AddCard(setup, seed, seed.Ravencia, code, "Ravencia Etek", vat: 10m);
            AddCard(setup, seed, seed.Lavian, code, "Lavian Etek", vat: 20m);
            await setup.SaveChangesAsync();
        }
        await using var db = fixture.CreateContext();
        var processor = new MarketplaceJobProcessor(db, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(), fixture.TimeProvider);
        var connectionId = Guid.CreateVersion7();
        // New colours the import holds in its tracker, not saved yet (their VAT reaches them only at the save).
        var lavianNew = AddCard(db, seed, seed.Lavian, code, "Lavian Yeni", vat: null).Variant;
        var ravenciaNew = AddCard(db, seed, seed.Ravencia, code, "Ravencia Yeni", vat: null).Variant;
        var brandless = AddCard(db, seed, null, code, "Markasız Yeni", vat: null).Variant;
        Assert.Equal(20m, await processor.NewImportedOfferVatRateAsync(seed.Tenant.Id, connectionId, lavianNew.Id, null, CancellationToken.None));
        Assert.Equal(10m, await processor.NewImportedOfferVatRateAsync(seed.Tenant.Id, connectionId, ravenciaNew.Id, null, CancellationToken.None));
        Assert.Equal(0m, await processor.NewImportedOfferVatRateAsync(seed.Tenant.Id, connectionId, brandless.Id, null, CancellationToken.None));
    }

    // Finding 3: "Yeni Ürün" behind a model row another session holds waits ModelLockTimeout and then failed with a 500
    // (55P03 was not a save conflict). It is the editor's 409 SAVE_CONFLICT_RETRY ("tekrar deneyin") now, nothing is
    // saved, and once the holder ends the same create goes through.
    [PostgreSqlFact]
    public async Task NewProduct_BehindAModelRowLock_IsATryAgainConflict_NotAnError()
    {
        var seed = await SeedAsync("create-behind-lock");
        var code = Code("LK");
        await using (var setup = fixture.CreateContext())
        {
            AddCard(setup, seed, seed.Ravencia, code, "Ravencia Etek");
            await setup.SaveChangesAsync();
        }
        await using var holder = fixture.CreateContext();
        await using var held = await holder.Database.BeginTransactionAsync();
        await holder.Database.SqlQuery<int>($"""SELECT 1 AS "Value" FROM catalog.product_models WHERE "TenantId" = {seed.Tenant.Id} FOR UPDATE""").ToListAsync();

        await using var db = fixture.CreateContext();
        db.ModelLockTimeout = "1s";
        var command = new CreateProductCommand("Lavian Etek", "Açıklama", seed.Lavian.Id, null, [new CreateVariantCommand($"{code}-L1", null, code)], VatRate: 10m, VatInclusion: "INCLUDED");
        var blocked = await Catalog(db).CreateProductAsync(seed.Tenant.Id, command, CancellationToken.None);
        Assert.Equal((409, "SAVE_CONFLICT_RETRY"), (blocked.Error?.Status, blocked.Error?.Code));
        Assert.Contains("Tekrar deneyin", blocked.Error!.Message);
        Assert.Equal(1, await db.Products.CountAsync(x => x.TenantId == seed.Tenant.Id));

        await held.RollbackAsync();
        db.ChangeTracker.Clear();
        var created = await Catalog(db).CreateProductAsync(seed.Tenant.Id, command, CancellationToken.None);
        Assert.True(created.Succeeded, created.Error?.Message);
    }

    [Fact]
    public void ALockWaitThatRanOut_IsTheEditorsTryAgainConflict_WhereverItIsWrapped()
    {
        var timeout = new Npgsql.PostgresException("canceling statement due to lock timeout", "ERROR", "ERROR", Npgsql.PostgresErrorCodes.LockNotAvailable);
        foreach (var exception in new Exception[] { timeout, new DbUpdateException("save", timeout), new InvalidOperationException("strategy", new DbUpdateException("save", timeout)) })
        {
            var conflict = CatalogService.SaveConflict<ProductView>(exception);
            Assert.Equal((409, "SAVE_CONFLICT_RETRY"), (conflict?.Error?.Status, conflict?.Error?.Code));
        }
        Assert.Null(CatalogService.SaveConflict<ProductView>(new Npgsql.PostgresException("other", "ERROR", "ERROR", Npgsql.PostgresErrorCodes.UndefinedTable)));
    }

    // The rule the holder check mirrors (LinkBrand), pinned on the reconcile: a card without a brand that reaches a code (an
    // import, which runs no holder check) joins the code's one branded model; with two brands holding the code it opens the
    // model without a brand.
    [PostgreSqlFact]
    public async Task ACardWithoutBrand_JoinsTheCodesOneBrandedModel_ElseTheModelWithoutBrand()
    {
        var seed = await SeedAsync("brandless-link");
        var one = Code("LO");
        var two = Code("LT");
        await using var db = fixture.CreateContext();
        var ravencia = AddCard(db, seed, seed.Ravencia, one, "Ravencia Etek").Card;
        AddCard(db, seed, seed.Ravencia, two, "Ravencia Bluz");
        AddCard(db, seed, seed.Lavian, two, "Lavian Bluz");
        await db.SaveChangesAsync();
        var joining = AddCard(db, seed, null, one, "Markasız Etek").Card;
        var apart = AddCard(db, seed, null, two, "Markasız Bluz").Card;
        await db.SaveChangesAsync();

        var ravenciaModel = (await db.Products.AsNoTracking().SingleAsync(x => x.Id == ravencia.Id)).FamilyId;
        Assert.Equal(ravenciaModel, (await db.Products.AsNoTracking().SingleAsync(x => x.Id == joining.Id)).FamilyId);
        var apartModel = await db.ProductModels.AsNoTracking().SingleAsync(x => x.Id == db.Products.Where(p => p.Id == apart.Id).Select(p => p.FamilyId).Single());
        Assert.Equal(((Guid?)null, two), (apartModel.BrandId, apartModel.ModelCodeKey));
        Assert.Equal(3, await db.ProductModels.CountAsync(x => x.TenantId == seed.Tenant.Id && x.ModelCodeKey == two));
    }
}
