using System.Text;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// The editor's required fields held by the server (owner, 10–11 Nov 2026): a card on sale or closed is never saved,
// opened by a bulk change or left by a file without one; an incomplete card is kept only as a draft. And the Excel
// import's brand column (owner decision 12 Nov 2026).
[Collection("postgres-integration")]
public sealed class ProductRequiredFieldsPostgreSqlTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
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

    private static CreateProductCommand Complete(Seed seed, string sku, string status = "ACTIVE", int? warranty = 0, decimal desi = 1m) =>
        new("Bluz", "Açıklama", seed.Brand.Id, seed.Category.Id, [new CreateVariantCommand(sku, $"{sku}B", $"{sku}-M", Desi: desi, DefaultListPrice: 120m, DefaultSalePrice: 100m)],
            Status: status, DefaultListPrice: 120m, DefaultSalePrice: 100m, WarrantyMonths: warranty, VatRate: 10m, VatInclusion: "INCLUDED", DeliveryDays: 2);

    [PostgreSqlFact]
    public async Task ACardOnSale_IsCreatedOnlyComplete_AndADraftMayMissAnything()
    {
        var seed = await SeedAsync("required-create");
        await using var db = fixture.CreateContext();
        var service = Catalog(db);
        var sku = $"RQ-{Guid.NewGuid():N}"[..14].ToUpperInvariant();

        // Garanti Süresi empty (not sent) is missing; 0 is "no warranty" and is fine.
        var noWarranty = await service.CreateProductAsync(seed.Tenant.Id, Complete(seed, sku, warranty: null), CancellationToken.None);
        Assert.Equal((422, ProductRequiredFields.Code, "Zorunlu alanları doldurun: Garanti Süresi."), (noWarranty.Error!.Status, noWarranty.Error.Code, noWarranty.Error.Message));
        Assert.Equal("Garanti Süresi zorunludur.", noWarranty.Error.FieldErrors!["warrantyMonths"][0]);
        // A closed card is held to the same fields; a price of 0 is named as not above 0.
        var closed = await service.CreateProductAsync(seed.Tenant.Id, Complete(seed, sku, status: "ARCHIVED") with { BrandId = null, DefaultSalePrice = 0m, Variants = [new CreateVariantCommand(sku, null, $"{sku}-M", Desi: 1m)] }, CancellationToken.None);
        Assert.Equal("Zorunlu alanları doldurun: Marka, Barkod, Satış Fiyatı.", closed.Error!.Message);
        Assert.Equal("Satış Fiyatı 0'dan büyük olmalı.", closed.Error.FieldErrors!["salePrice"][0]);
        // One desi limit: above 9999 is refused before anything else.
        var heavy = await service.CreateProductAsync(seed.Tenant.Id, Complete(seed, sku, desi: 10000m), CancellationToken.None);
        Assert.Equal(ProductDesi.TooLargeMessage, heavy.Error!.Message);
        Assert.False(await db.ProductVariants.AnyAsync(x => x.TenantId == seed.Tenant.Id));

        var draft = await service.CreateProductAsync(seed.Tenant.Id, new CreateProductCommand("Taslak", "", null, null, [new CreateVariantCommand($"{sku}-D", null, null)], Status: "DRAFT"), CancellationToken.None);
        Assert.True(draft.Succeeded, draft.Error?.Message);
        var created = await service.CreateProductAsync(seed.Tenant.Id, Complete(seed, sku), CancellationToken.None);
        Assert.True(created.Succeeded, created.Error?.Message);
        Assert.Equal("ACTIVE", created.Value!.Status);
    }

    [PostgreSqlFact]
    public async Task ASaveOfACardOnSale_ThatEmptiesARequiredField_IsRefused_NotTurnedIntoADraft()
    {
        var seed = await SeedAsync("required-update");
        var sku = $"RU-{Guid.NewGuid():N}"[..14].ToUpperInvariant();
        await using var db = fixture.CreateContext();
        var service = Catalog(db);
        var created = await service.CreateProductAsync(seed.Tenant.Id, Complete(seed, sku), CancellationToken.None);
        Assert.True(created.Succeeded, created.Error?.Message);
        var variant = created.Value!.Variants.Single();
        db.ChangeTracker.Clear();

        var noBrand = await service.UpdateProductAsync(seed.Tenant.Id, created.Value.Id, created.Value.Version, new UpdateProductCommand("Bluz", "Açıklama", null, seed.Category.Id), CancellationToken.None);
        Assert.Equal((422, "Zorunlu alanları doldurun: Marka."), (noBrand.Error!.Status, noBrand.Error.Message));
        db.ChangeTracker.Clear();
        var noCodes = await service.UpdateProductAsync(seed.Tenant.Id, created.Value.Id, created.Value.Version, new UpdateProductCommand("Bluz", "Açıklama", seed.Brand.Id, seed.Category.Id,
            VariantUpdates: [new UpdateVariantCommand(variant.Id, variant.Sku, null, " ", DefaultListPrice: 0m)]), CancellationToken.None);
        Assert.Equal("Zorunlu alanları doldurun: Model Kodu, Barkod, Liste Fiyatı.", noCodes.Error!.Message);
        db.ChangeTracker.Clear();
        var stored = await db.Products.AsNoTracking().SingleAsync(x => x.Id == created.Value.Id);
        Assert.Equal((ProductStatus.Active, seed.Brand.Id, created.Value.Version), (stored.Status, stored.BrandId, stored.Version));

        // A save that leaves the prices, desi and warranty out keeps the stored ones and goes through.
        var renamed = await service.UpdateProductAsync(seed.Tenant.Id, created.Value.Id, created.Value.Version, new UpdateProductCommand("Yeni Bluz", "Açıklama", seed.Brand.Id, seed.Category.Id), CancellationToken.None);
        Assert.True(renamed.Succeeded, renamed.Error?.Message);
    }

    [PostgreSqlFact]
    public async Task ABulkOpening_LeavesEveryCardThatMissesARequiredField()
    {
        var seed = await SeedAsync("required-bulk");
        var sku = $"RB-{Guid.NewGuid():N}"[..14].ToUpperInvariant();
        await using var db = fixture.CreateContext();
        var service = Catalog(db);
        var complete = await service.CreateProductAsync(seed.Tenant.Id, Complete(seed, sku, status: "ARCHIVED"), CancellationToken.None);
        Assert.True(complete.Succeeded, complete.Error?.Message);
        // A closed card from before the rule, without its desi and brand.
        var now = fixture.Now;
        var incomplete = new Product { Id = Guid.CreateVersion7(), TenantId = seed.Tenant.Id, Title = "Eski Kapalı", Description = "", CategoryId = seed.Category.Id, DefaultListPrice = 10m, DefaultSalePrice = 10m, VatRate = 10m, VatInclusion = "INCLUDED", Status = ProductStatus.Archived, CreatedAt = now, UpdatedAt = now };
        db.Products.Add(incomplete);
        db.ProductVariants.Add(new ProductVariant { Id = Guid.CreateVersion7(), TenantId = seed.Tenant.Id, ProductId = incomplete.Id, Sku = $"{sku}-X", SkuNormalized = $"{sku}-X", Barcode = $"{sku}X", BarcodeNormalized = $"{sku}X", ModelCode = $"{sku}-XM", OptionSignature = "", Status = ProductStatus.Archived, CreatedAt = now, UpdatedAt = now });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        // Toplu Satışa Aç (owner, 13 Nov 2026: "Eksik olanı atlasın, bildirsin"): the complete card is opened, the
        // incomplete one skipped and returned with its model code and what it misses.
        var opened = await service.BulkSetStatusAsync(seed.Tenant.Id, new BulkProductStatusCommand([complete.Value!.Id, incomplete.Id], "ACTIVE"), CancellationToken.None);
        Assert.True(opened.Succeeded, opened.Error?.Message);
        Assert.Equal(1, opened.Value!.UpdatedCount);
        var skipped = Assert.Single(opened.Value.Skipped);
        Assert.Equal((incomplete.Id, "Eski Kapalı", $"{sku}-XM"), (skipped.ProductId, skipped.Title, skipped.ModelCode));
        Assert.Equal(["Marka", "Desi"], skipped.MissingFields);
        db.ChangeTracker.Clear();
        var opening = await db.Products.AsNoTracking().Where(x => x.TenantId == seed.Tenant.Id).ToDictionaryAsync(x => x.Id, x => x.Status);
        Assert.Equal((ProductStatus.Active, ProductStatus.Archived), (opening[complete.Value.Id], opening[incomplete.Id]));
        // Closing never asks for the fields.
        var closed = await service.BulkSetStatusAsync(seed.Tenant.Id, new BulkProductStatusCommand([complete.Value.Id, incomplete.Id], "ARCHIVED"), CancellationToken.None);
        Assert.True(closed.Succeeded);
        Assert.Empty(closed.Value!.Skipped);

        // Toplu Değer Ata › Satış Durumu: the complete card is opened, the incomplete one left and returned in Skipped.
        db.ChangeTracker.Clear();
        var field = await service.BulkSetFieldAsync(seed.Tenant.Id, new BulkProductFieldCommand([complete.Value.Id, incomplete.Id], BulkProductFields.Status, "ACTIVE"), CancellationToken.None);
        Assert.True(field.Succeeded, field.Error?.Message);
        Assert.Equal(1, field.Value!.UpdatedCount);
        Assert.Empty(field.Value.Failures);
        var fieldSkipped = Assert.Single(field.Value.Skipped!);
        Assert.Equal(incomplete.Id, fieldSkipped.ProductId);
        Assert.Equal(["Marka", "Desi"], fieldSkipped.MissingFields);
        var statuses = await db.Products.AsNoTracking().Where(x => x.TenantId == seed.Tenant.Id).ToDictionaryAsync(x => x.Id, x => x.Status);
        Assert.Equal((ProductStatus.Active, ProductStatus.Archived), (statuses[complete.Value.Id], statuses[incomplete.Id]));
    }

    // Scan, 13 Nov 2026: a closed card without its KDV or its Teslimat Süresi is not opened by a bulk change either (the
    // editor refuses both).
    [PostgreSqlFact]
    public async Task ABulkOpening_LeavesACardWithoutItsVatOrDeliveryDays()
    {
        var seed = await SeedAsync("required-bulk-vat");
        var sku = $"RV-{Guid.NewGuid():N}"[..14].ToUpperInvariant();
        var now = fixture.Now;
        await using var db = fixture.CreateContext();
        var service = Catalog(db);
        var card = new Product { Id = Guid.CreateVersion7(), TenantId = seed.Tenant.Id, Title = "KDV'siz", Description = "", BrandId = seed.Brand.Id, CategoryId = seed.Category.Id, DefaultListPrice = 10m, DefaultSalePrice = 10m, DeliveryDays = null, Status = ProductStatus.Archived, CreatedAt = now, UpdatedAt = now };
        db.Products.Add(card);
        db.ProductVariants.Add(new ProductVariant { Id = Guid.CreateVersion7(), TenantId = seed.Tenant.Id, ProductId = card.Id, Sku = sku, SkuNormalized = sku, Barcode = $"{sku}B", BarcodeNormalized = $"{sku}B", ModelCode = $"{sku}-M", Desi = 1m, OptionSignature = "", Status = ProductStatus.Archived, CreatedAt = now, UpdatedAt = now });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var opened = await service.BulkSetStatusAsync(seed.Tenant.Id, new BulkProductStatusCommand([card.Id], "ACTIVE"), CancellationToken.None);
        Assert.True(opened.Succeeded, opened.Error?.Message);
        Assert.Equal(0, opened.Value!.UpdatedCount);
        Assert.Equal(["Teslimat Süresi", "KDV Oranı"], Assert.Single(opened.Value.Skipped).MissingFields);
        db.ChangeTracker.Clear();
        Assert.Equal(ProductStatus.Archived, (await db.Products.AsNoTracking().SingleAsync(x => x.Id == card.Id)).Status);
    }

    [PostgreSqlFact]
    public async Task TheExcelBrand_GoesToEveryColourCardOfTheModel_ByName_AndMarksTheirLinks()
    {
        var seed = await SeedAsync("transfer-brand");
        var now = fixture.Now;
        var model = $"TB{Guid.NewGuid():N}"[..10].ToUpperInvariant();
        var connection = new PlatformConnection { Id = Guid.CreateVersion7(), TenantId = seed.Tenant.Id, PublicId = Guid.CreateVersion7(), PlatformCode = "TRENDYOL", Environment = "PRODUCTION", DisplayName = "Trendyol", ExternalStoreId = $"store-{Guid.NewGuid():N}", Status = "ACTIVE", ApiVersion = "V2", SettingsJson = "{}" };
        Product Card(string colour) => new() { Id = Guid.CreateVersion7(), TenantId = seed.Tenant.Id, Title = $"Bluz {colour}", Description = "", BrandId = seed.Brand.Id, CategoryId = seed.Category.Id, DefaultListPrice = 10m, DefaultSalePrice = 10m, Status = ProductStatus.Active, CreatedAt = now, UpdatedAt = now };
        var siyah = Card("Siyah");
        var bordo = Card("Bordo");
        ProductVariant Variant(Product card, string sku) => new() { Id = Guid.CreateVersion7(), TenantId = seed.Tenant.Id, ProductId = card.Id, Sku = sku, SkuNormalized = sku, Barcode = sku, BarcodeNormalized = sku, ModelCode = model, Desi = 1m, OptionSignature = "", Status = ProductStatus.Active, CreatedAt = now, UpdatedAt = now };
        var siyahLink = new MarketplaceProductLink { Id = Guid.CreateVersion7(), TenantId = seed.Tenant.Id, ConnectionId = connection.Id, ProductId = siyah.Id, ExternalId = $"r-{Guid.NewGuid():N}", SyncStatus = "SYNCED", Version = 1 };
        var bordoLink = new MarketplaceProductLink { Id = Guid.CreateVersion7(), TenantId = seed.Tenant.Id, ConnectionId = connection.Id, ProductId = bordo.Id, ExternalId = $"r-{Guid.NewGuid():N}", SyncStatus = "SYNCED", Version = 1 };
        await using (var setup = fixture.CreateContext())
        {
            setup.PlatformConnections.Add(connection);
            setup.Products.AddRange(siyah, bordo);
            setup.ProductVariants.AddRange(Variant(siyah, $"{model}-S"), Variant(bordo, $"{model}-B"));
            setup.MarketplaceProductLinks.AddRange(siyahLink, bordoLink);
            await setup.SaveChangesAsync();
        }

        await using var db = fixture.CreateContext();
        var service = new ProductTransferService(db, fixture.TimeProvider);
        // Only the Siyah row names the new brand; an empty Marka cell on the Bordo row changes nothing by itself.
        var csv = $"Stok Kodu;Marka\r\n{model}-S;Moda Evi\r\n{model}-B;\r\n";
        var preview = await service.PreviewAsync(seed.Tenant.Id, "urunler.csv", new MemoryStream(Encoding.UTF8.GetBytes(csv)), [ProductTransferFields.Brand], CancellationToken.None);
        Assert.True(preview.Succeeded, preview.Error?.Message);
        Assert.Equal(1, preview.Value!.BrandUpdates);
        var row = preview.Value.Rows.Single(item => item.Sku == $"{model}-S");
        Assert.Equal(("Ravencia", "Moda Evi", "SET"), (row.OldBrand, row.NewBrand, row.BrandChange));

        var applied = await service.ApplyAsync(seed.Tenant.Id, Guid.CreateVersion7(), new ApplyProductTransferCommand(preview.Value.Changes, preview.Value.ProductChanges), "transfer-brand", CancellationToken.None);
        Assert.True(applied.Succeeded, applied.Error?.Message);
        Assert.Equal((2, 1), (applied.Value!.BrandsUpdated, applied.Value.BrandsCreated));

        await using var check = fixture.CreateContext();
        var newBrand = await check.Brands.AsNoTracking().SingleAsync(x => x.TenantId == seed.Tenant.Id && x.Name == "Moda Evi");
        Assert.Equal(PanelBrandByName.Key("Moda Evi"), newBrand.NormalizedName);
        Assert.All(await check.Products.AsNoTracking().Where(x => x.TenantId == seed.Tenant.Id).ToListAsync(), card => Assert.Equal(newBrand.Id, card.BrandId));
        Assert.All(await check.MarketplaceProductLinks.AsNoTracking().Where(x => x.TenantId == seed.Tenant.Id).ToListAsync(), link => Assert.Equal("LOCAL_CHANGES_PENDING", link.SyncStatus));

        // A switched-off brand is not revived: the row is an error and nothing is written.
        check.Brands.Add(new Brand { Id = Guid.CreateVersion7(), TenantId = seed.Tenant.Id, Name = "Kapalı", NormalizedName = "KAPALI", IsActive = false, CreatedAt = now, UpdatedAt = now });
        await check.SaveChangesAsync();
        var closed = await new ProductTransferService(check, fixture.TimeProvider).PreviewAsync(seed.Tenant.Id, "urunler.csv", new MemoryStream(Encoding.UTF8.GetBytes($"Stok Kodu;Marka\r\n{model}-S;kapalı\r\n")), [ProductTransferFields.Brand], CancellationToken.None);
        Assert.Equal((1, 0), (closed.Value!.Errors, closed.Value.ProductChanges!.Count));
    }

    // Klasik içe aktarma (owner, 13 Nov 2026): the file's Garanti Süresi (Ay) and Teslimat Süresi (Gün) go to the new card
    // as the editor stores them; a row without them keeps today's no warranty and the default days.
    [PostgreSqlFact]
    public async Task TheClassicImport_WritesTheFilesWarrantyAndDeliveryDays_AndKeepsTheDefaultsWithoutThem()
    {
        var seed = await SeedAsync("import-terms");
        var now = fixture.Now;
        var code = $"IT{Guid.NewGuid():N}"[..10].ToUpperInvariant();
        var session = new ImportSession { Id = Guid.CreateVersion7(), TenantId = seed.Tenant.Id, SourceType = ImportSourceType.Csv, Status = ImportSessionStatus.ReadyToApply, CreatedAt = now, UpdatedAt = now };
        Dictionary<string, string> Row(string suffix, string? warranty, string? days)
        {
            var values = new Dictionary<string, string> { ["title"] = $"Bluz {suffix}", ["sku"] = $"{code}-{suffix}", ["barcode"] = $"{code}{suffix}", ["modelCode"] = $"{code}{suffix}M", ["brand"] = "Ravencia", ["category"] = "Bluz", ["desi"] = "1", ["listPrice"] = "120", ["salePrice"] = "100", ["vatRate"] = "10" };
            if (warranty is not null) values["warrantyMonths"] = warranty;
            if (days is not null) values["deliveryDays"] = days;
            return values;
        }
        await using (var setup = fixture.CreateContext())
        {
            setup.ImportSessions.Add(session);
            var rowNumber = 1;
            foreach (var values in new[] { Row("A", "24", "3"), Row("B", null, null), Row("C", "0", "") })
            {
                var json = System.Text.Json.JsonSerializer.Serialize(values);
                var staging = new ImportStagingRecord { Id = Guid.CreateVersion7(), TenantId = seed.Tenant.Id, SessionId = session.Id, RowNumber = ++rowNumber, RawJson = json, SafeValuesJson = json, ValidationErrorsJson = "[]", RowHash = $"hash-{code}-{rowNumber}", ReviewStatus = "PENDING" };
                var candidate = new ImportMatchCandidate { Id = Guid.CreateVersion7(), TenantId = seed.Tenant.Id, SessionId = session.Id, StagingRecordId = staging.Id, MatchRule = "NEW", Status = "DECIDED", SafeSummary = "{}" };
                setup.ImportStagingRecords.Add(staging);
                setup.ImportMatchCandidates.Add(candidate);
                setup.ImportDecisions.Add(new ImportDecision { Id = Guid.CreateVersion7(), TenantId = seed.Tenant.Id, CandidateId = candidate.Id, Decision = ImportDecisionKind.Create, ActorUserId = Guid.CreateVersion7(), DecidedAt = now });
            }
            await setup.SaveChangesAsync();
        }

        await using (var db = fixture.CreateContext())
            Assert.True(await new MarketplaceHub.Infrastructure.Imports.ImportJobProcessor(db, null!, fixture.TimeProvider, new ConfigurationBuilder().Build()).ProcessAsync(seed.Tenant.Id, session.Id, "APPLY", CancellationToken.None));

        await using var check = fixture.CreateContext();
        var cards = await check.Products.AsNoTracking().Where(x => x.TenantId == seed.Tenant.Id).ToDictionaryAsync(x => x.Title);
        Assert.Equal(((int?)24, (int?)3, ProductStatus.Active), (cards["Bluz A"].WarrantyMonths, cards["Bluz A"].DeliveryDays, cards["Bluz A"].Status));
        Assert.Equal(((int?)null, (int?)1, ProductStatus.Active), (cards["Bluz B"].WarrantyMonths, cards["Bluz B"].DeliveryDays, cards["Bluz B"].Status));
        // 0 is "no warranty" (kept empty, as the editor keeps it); an empty cell is not given.
        Assert.Equal(((int?)null, (int?)1), (cards["Bluz C"].WarrantyMonths, cards["Bluz C"].DeliveryDays));
    }
}
