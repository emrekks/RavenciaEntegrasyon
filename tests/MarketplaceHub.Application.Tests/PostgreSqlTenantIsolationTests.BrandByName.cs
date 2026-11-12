using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// One name, one brand (owner decision 12 Nov 2026): Platformdan Ürün Aktar (by name, no brand id) and the file imports
// (/imports and the products Excel, which read names through PanelBrandByName in one batch) land every spelling of a
// name on the same panel brand, never revive a switched-off one and open the same new one.
public sealed partial class PostgreSqlTenantIsolationTests
{
    [PostgreSqlFact]
    public async Task BrandByName_TheMarketplaceImportAndTheFileImports_LandEveryNameOnTheSameBrand()
    {
        var active = Guid.CreateVersion7();
        var legacy = Guid.CreateVersion7();
        var switchedOff = Guid.CreateVersion7();
        var seed = await SeedImportBrandTenantAsync("brand-by-name", (db, tenant, _, _) => db.Brands.AddRange(
            new Brand { Id = active, TenantId = tenant.Id, Name = "Gümüş Moda", NormalizedName = CatalogKeyNormalizer.Key("Gümüş Moda"), IsActive = true, CreatedAt = fixture.Now, UpdatedAt = fixture.Now, Version = 1 },
            // Stored before the shared key, in the old panel form.
            new Brand { Id = legacy, TenantId = tenant.Id, Name = "Kırmızı Ev", NormalizedName = CatalogKeyNormalizer.Legacy("Kırmızı Ev"), IsActive = true, CreatedAt = fixture.Now, UpdatedAt = fixture.Now, Version = 1 },
            new Brand { Id = switchedOff, TenantId = tenant.Id, Name = "Kapalı Marka", NormalizedName = CatalogKeyNormalizer.Key("Kapalı Marka"), IsActive = false, CreatedAt = fixture.Now, UpdatedAt = fixture.Now, Version = 1 }));
        string[] names = ["gumus-moda", "kırmızı ev", "kapali marka", "Yepyeni Marka"];
        try
        {
            // The marketplace path: listings without a brand id are matched by name.
            await RunImportBrandSyncAsync(seed, "{\"full\":true,\"updateExistingProducts\":true}",
                [.. names.Select((name, index) => ImportBrandListing($"by-name-{index}", null, name, $"BYN-{index}"))]);

            await using var db = fixture.CreateContext();
            var byListing = await db.ProductVariants.AsNoTracking().Where(row => row.TenantId == seed.Tenant.Id)
                .Join(db.Products.AsNoTracking(), variant => variant.ProductId, product => product.Id, (variant, product) => new { variant.Sku, product.BrandId })
                .ToDictionaryAsync(row => row.Sku, row => row.BrandId);
            var opened = await db.Brands.AsNoTracking().SingleAsync(row => row.TenantId == seed.Tenant.Id && row.Name == "Yepyeni Marka");
            Guid?[] expected = [active, legacy, null, opened.Id];
            Assert.Equal(expected, names.Select((_, index) => byListing[$"BYN-{index}"]));

            // The file-import path (one batch, as /imports and the Excel read a file) gives the same answer for every name,
            // and opens nothing new.
            var lookup = new PanelBrandByName(db, seed.Tenant.Id);
            await lookup.PreloadAsync([.. names, "Gümüş Moda", " gümüş   moda "], CancellationToken.None);
            var matches = new List<PanelBrandByName.Match>();
            foreach (var name in names) matches.Add(await lookup.FindOrCreateAsync(name, fixture.Now, CancellationToken.None));
            Assert.Equal(expected, matches.Select(match => match.Id));
            Assert.Equal(switchedOff, matches[2].SwitchedOff!.Id);
            Assert.All(matches, match => Assert.False(match.Created));
            Assert.Equal(active, (await lookup.FindAsync(" gümüş   moda ", CancellationToken.None)).Id);
            Assert.Equal(4, await db.Brands.CountAsync(row => row.TenantId == seed.Tenant.Id));
        }
        finally { await CleanImportBrandTenantAsync(seed.Tenant.Id); }
    }
}
