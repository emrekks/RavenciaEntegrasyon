using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// Owner decision 12 Nov 2026: a product import maps the listing's brand like its category. The mapping on the connection
// wins; else the panel brand of the same name is reused and mapped; else a new panel brand is opened and mapped
// (VERIFIED, Source IMPORT). The focused price/stock path writes no brand. No marketplace call is made for brands.
public sealed partial class PostgreSqlTenantIsolationTests
{
    private sealed record ImportBrandSeed(Tenant Tenant, PlatformConnection Connection, Guid BrandSnapshotId);

    private static RemoteCatalogProduct ImportBrandListing(string id, string? brandId, string brandName, string sku) => new(
        id, $"{sku}-M", $"Liste {sku}", "", brandId, brandName, null, null, [],
        [new RemoteCatalogVariant($"ty-{sku}", sku, sku, $"{sku}-M", new Dictionary<string, string>(), Archived: false, SalePrice: 100m, ListPrice: 120m, VatRate: 10m, StockQuantity: 2m, Currency: "TRY", RawJson: "{}")],
        "{}");

    private async Task<ImportBrandSeed> SeedImportBrandTenantAsync(string code, Action<AppDbContext, Tenant, PlatformConnection, Guid>? seed = null)
    {
        var tenant = NewTenant(code);
        var connection = NewQuestionConnection(tenant);
        var brandSnapshotId = Guid.CreateVersion7();
        await using var db = fixture.CreateContext();
        db.Tenants.Add(tenant);
        db.PlatformConnections.Add(connection);
        db.ReferenceSnapshots.AddRange(
            new ReferenceSnapshot { Id = brandSnapshotId, TenantId = tenant.Id, ConnectionId = connection.Id, ResourceType = "BRANDS", SourceVersion = "test", ContentHash = "test", FetchedAt = fixture.Now, IsCurrent = true },
            new ReferenceSnapshot { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, ResourceType = "CATEGORIES", SourceVersion = "test", ContentHash = "test", FetchedAt = fixture.Now, IsCurrent = true });
        seed?.Invoke(db, tenant, connection, brandSnapshotId);
        await db.SaveChangesAsync();
        return new(tenant, connection, brandSnapshotId);
    }

    private async Task<ReadOnlyCatalogProductTestPort> RunImportBrandSyncAsync(ImportBrandSeed seed, string payloadJson, params RemoteCatalogProduct[] listings)
    {
        await using var processorDb = fixture.CreateContext();
        var port = new ReadOnlyCatalogProductTestPort(listings);
        var processor = new MarketplaceJobProcessor(processorDb, null!, null!, port, null!, null!, null!, null!, null!, null!, null!, new ConfigurationBuilder().Build(), fixture.TimeProvider);
        var result = await processor.ProcessAsync(seed.Tenant.Id, seed.Connection.Id, MarketplaceJobTypes.ProductSync, payloadJson, "import-brand", CancellationToken.None);
        Assert.True(result.Succeeded, result.ErrorSummary);
        Assert.Equal(0, port.WriteAttempts);
        return port;
    }

    private async Task CleanImportBrandTenantAsync(Guid tenantId)
    {
        await using (var db = fixture.CreateContext())
        {
            await db.ChannelPriceHistory.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
            await db.StockLedgerEntries.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
            await db.IntegrationJobs.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
            await db.ProductChangeEntries.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
            await db.ReferenceSnapshots.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        }
        await DeleteCatalogOptionRepairTestTenantAsync(tenantId);
    }

    [PostgreSqlFact]
    public async Task ImportBrand_NewBrand_OpensAPanelBrandAndItsMapping()
    {
        var seed = await SeedImportBrandTenantAsync("import-brand-new");
        try
        {
            await RunImportBrandSyncAsync(seed, "{\"full\":true,\"updateExistingProducts\":true}", ImportBrandListing("listing-new", "300", "Yeni Marka", "NEW-1"));

            await using var db = fixture.CreateContext();
            var brand = await db.Brands.AsNoTracking().SingleAsync(row => row.TenantId == seed.Tenant.Id);
            Assert.Equal(("Yeni Marka", CatalogKeyNormalizer.Key("Yeni Marka"), true), (brand.Name, brand.NormalizedName, brand.IsActive));
            var mapping = await db.BrandMappings.AsNoTracking().SingleAsync(row => row.TenantId == seed.Tenant.Id);
            Assert.Equal((seed.Connection.Id, brand.Id, "300", "", "VERIFIED", MappingSources.Import, seed.BrandSnapshotId), (mapping.ConnectionId, mapping.LocalId, mapping.ExternalId, mapping.ScopeExternalId, mapping.Status, mapping.Source, mapping.SnapshotId));
            Assert.NotNull(mapping.VerifiedAt);
            var product = await db.Products.AsNoTracking().SingleAsync(row => row.TenantId == seed.Tenant.Id);
            Assert.Equal(brand.Id, product.BrandId);
            Assert.False(await db.OperationalIssues.AnyAsync(row => row.TenantId == seed.Tenant.Id && row.Code == ImportedBrandRule.IssueCode && row.Status != IssueStatus.Resolved));
        }
        finally { await CleanImportBrandTenantAsync(seed.Tenant.Id); }
    }

    [PostgreSqlFact]
    public async Task ImportBrand_SameNamedPanelBrand_IsReusedAndMapped()
    {
        var panelBrandId = Guid.CreateVersion7();
        var seed = await SeedImportBrandTenantAsync("import-brand-name", (db, tenant, _, _) =>
            db.Brands.Add(new Brand { Id = panelBrandId, TenantId = tenant.Id, Name = "Eşlenmemiş Marka", NormalizedName = CatalogKeyNormalizer.Key("Eşlenmemiş Marka"), IsActive = true, CreatedAt = fixture.Now, UpdatedAt = fixture.Now, Version = 1 }));
        try
        {
            // Spelled differently (case, dotless i) from the panel brand: the shared catalog key matches them.
            await RunImportBrandSyncAsync(seed, "{\"full\":true,\"updateExistingProducts\":true}", ImportBrandListing("listing-name", "200", "EŞLENMEMİŞ marka", "NAM-1"));

            await using var db = fixture.CreateContext();
            Assert.Equal(panelBrandId, (await db.Brands.AsNoTracking().SingleAsync(row => row.TenantId == seed.Tenant.Id)).Id);
            var mapping = await db.BrandMappings.AsNoTracking().SingleAsync(row => row.TenantId == seed.Tenant.Id);
            Assert.Equal((panelBrandId, "200", "VERIFIED", MappingSources.Import), (mapping.LocalId, mapping.ExternalId, mapping.Status, mapping.Source));
            Assert.Equal(panelBrandId, (await db.Products.AsNoTracking().SingleAsync(row => row.TenantId == seed.Tenant.Id)).BrandId);
        }
        finally { await CleanImportBrandTenantAsync(seed.Tenant.Id); }
    }

    // The user's mapping wins over a panel brand spelled like the listing's brand; the existing product takes it and keeps
    // its own (archived) status; nothing new is opened or mapped.
    [PostgreSqlFact]
    public async Task ImportBrand_ExistingMapping_IsUsed_AndThePanelStatusStays()
    {
        var mappedBrandId = Guid.CreateVersion7();
        var sameNameBrandId = Guid.CreateVersion7();
        var productId = Guid.CreateVersion7();
        var variantId = Guid.CreateVersion7();
        var seed = await SeedImportBrandTenantAsync("import-brand-mapped", (db, tenant, connection, brandSnapshotId) =>
        {
            db.Brands.AddRange(
                new Brand { Id = mappedBrandId, TenantId = tenant.Id, Name = "Panel Markası", NormalizedName = CatalogKeyNormalizer.Key("Panel Markası"), IsActive = true, CreatedAt = fixture.Now, UpdatedAt = fixture.Now, Version = 1 },
                new Brand { Id = sameNameBrandId, TenantId = tenant.Id, Name = "Uzak Ad", NormalizedName = CatalogKeyNormalizer.Key("Uzak Ad"), IsActive = true, CreatedAt = fixture.Now, UpdatedAt = fixture.Now, Version = 1 });
            db.BrandMappings.Add(new BrandMapping { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, SnapshotId = brandSnapshotId, LocalId = mappedBrandId, ScopeExternalId = "", ExternalId = "100", Status = "VERIFIED", VerifiedAt = fixture.Now, Version = 1 });
            db.Products.Add(new Product { Id = productId, TenantId = tenant.Id, Title = "Panel ürünü", Description = "", Status = ProductStatus.Archived, ArchivedAt = fixture.Now.AddDays(-1), CreatedAt = fixture.Now.AddDays(-2), UpdatedAt = fixture.Now.AddDays(-1), Version = 4 });
            db.ProductVariants.Add(new ProductVariant { Id = variantId, TenantId = tenant.Id, ProductId = productId, SortOrder = 0, Sku = "BRD-1", SkuNormalized = "BRD-1", Barcode = "BRD-1", BarcodeNormalized = "BRD-1", ModelCode = "BRD-1-M", OptionSignature = "-", Status = ProductStatus.Archived, CreatedAt = fixture.Now.AddDays(-2), UpdatedAt = fixture.Now.AddDays(-1), Version = 2 });
            db.MarketplaceProductLinks.Add(new MarketplaceProductLink { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, ProductId = productId, ExternalId = "listing-mapped", LastImportedProductVersion = 4, SyncStatus = "SYNCED", Version = 1 });
            db.MarketplaceVariantLinks.Add(new MarketplaceVariantLink { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, VariantId = variantId, ExternalId = "ty-BRD-1", Version = 1 });
        });
        try
        {
            await RunImportBrandSyncAsync(seed, "{\"full\":true,\"includeArchived\":true,\"updateExistingProducts\":true}", ImportBrandListing("listing-mapped", "100", "Uzak Ad", "BRD-1"));

            await using var db = fixture.CreateContext();
            var product = await db.Products.AsNoTracking().SingleAsync(row => row.Id == productId);
            Assert.Equal(mappedBrandId, product.BrandId);
            Assert.Equal(ProductStatus.Archived, product.Status);
            Assert.Equal(ProductStatus.Archived, (await db.ProductVariants.AsNoTracking().SingleAsync(row => row.Id == variantId)).Status);
            Assert.Equal(2, await db.Brands.CountAsync(row => row.TenantId == seed.Tenant.Id));
            var mapping = await db.BrandMappings.AsNoTracking().SingleAsync(row => row.TenantId == seed.Tenant.Id);
            Assert.Equal(("100", mappedBrandId, 1L, MappingSources.Manual), (mapping.ExternalId, mapping.LocalId, mapping.Version, mapping.Source));
        }
        finally { await CleanImportBrandTenantAsync(seed.Tenant.Id); }
    }

    // Two listings of one brand in one run open one panel brand and one mapping; a listing without a brand id is matched
    // by name in the panel alone and writes no mapping.
    [PostgreSqlFact]
    public async Task ImportBrand_TwoListingsOfOneBrandInOneRun_OpenOneBrand()
    {
        var seed = await SeedImportBrandTenantAsync("import-brand-twice");
        try
        {
            await RunImportBrandSyncAsync(seed, "{\"full\":true,\"updateExistingProducts\":true}",
                ImportBrandListing("listing-a", "400", "Ortak Marka", "TWO-1"),
                ImportBrandListing("listing-b", "400", "Ortak Marka", "TWO-2"),
                ImportBrandListing("listing-c", null, "ortak marka", "TWO-3"));

            await using var db = fixture.CreateContext();
            var brand = await db.Brands.AsNoTracking().SingleAsync(row => row.TenantId == seed.Tenant.Id);
            var mapping = await db.BrandMappings.AsNoTracking().SingleAsync(row => row.TenantId == seed.Tenant.Id);
            Assert.Equal((brand.Id, "400"), (mapping.LocalId, mapping.ExternalId));
            var brands = await db.Products.AsNoTracking().Where(row => row.TenantId == seed.Tenant.Id).Select(row => row.BrandId).ToListAsync();
            Assert.Equal(3, brands.Count);
            Assert.All(brands, id => Assert.Equal(brand.Id, id));
        }
        finally { await CleanImportBrandTenantAsync(seed.Tenant.Id); }
    }

    // "Mevcut Ürünleri Güncelle" with only price/stock (the focused path) never opens or maps a brand, nor touches the
    // product's own.
    [PostgreSqlFact]
    public async Task ImportBrand_FocusedPriceStockPath_WritesNoBrand()
    {
        var productId = Guid.CreateVersion7();
        var variantId = Guid.CreateVersion7();
        var seed = await SeedImportBrandTenantAsync("import-brand-focused", (db, tenant, connection, _) =>
        {
            db.Products.Add(new Product { Id = productId, TenantId = tenant.Id, Title = "Panel ürünü", Description = "", Status = ProductStatus.Active, CreatedAt = fixture.Now.AddDays(-2), UpdatedAt = fixture.Now.AddDays(-1), Version = 1 });
            db.ProductVariants.Add(new ProductVariant { Id = variantId, TenantId = tenant.Id, ProductId = productId, SortOrder = 0, Sku = "FOC-1", SkuNormalized = "FOC-1", Barcode = "FOC-1", BarcodeNormalized = "FOC-1", ModelCode = "FOC-1-M", OptionSignature = "-", CreatedAt = fixture.Now.AddDays(-2), UpdatedAt = fixture.Now.AddDays(-1), Version = 1 });
            db.MarketplaceProductLinks.Add(new MarketplaceProductLink { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, ProductId = productId, ExternalId = "listing-focused", LastImportedProductVersion = 1, SyncStatus = "SYNCED", Version = 1 });
            db.MarketplaceVariantLinks.Add(new MarketplaceVariantLink { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, VariantId = variantId, ExternalId = "ty-FOC-1", Version = 1 });
        });
        try
        {
            await RunImportBrandSyncAsync(seed, "{\"existingOnly\":true,\"updateExistingProducts\":false,\"updatePrices\":true,\"updateStock\":true}", ImportBrandListing("listing-focused", "500", "Odak Marka", "FOC-1"));

            await using var db = fixture.CreateContext();
            Assert.False(await db.Brands.AnyAsync(row => row.TenantId == seed.Tenant.Id));
            Assert.False(await db.BrandMappings.AnyAsync(row => row.TenantId == seed.Tenant.Id));
            Assert.Null((await db.Products.AsNoTracking().SingleAsync(row => row.Id == productId)).BrandId);
        }
        finally { await CleanImportBrandTenantAsync(seed.Tenant.Id); }
    }
}
