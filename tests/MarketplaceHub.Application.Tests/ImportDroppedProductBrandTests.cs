using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// A product an import does not open after all (every variant is another panel product's) takes back the brand it opened
// for that product alone, with the mapping written for it: no panel brand appears for a product that never opened. A
// brand another unsaved product of the same model group uses stays.
public sealed class ImportDroppedProductBrandTests
{
    private static AppDbContext Context() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql("Host=localhost;Database=unused").Options);

    private static (Brand Brand, Product Product, BrandMapping Mapping) AddProductWithNewBrand(AppDbContext db, Guid tenantId)
    {
        var brand = new Brand { Id = Guid.CreateVersion7(), TenantId = tenantId, Name = "Yetim Marka", NormalizedName = "YETIM MARKA", IsActive = true, Version = 1 };
        var product = new Product { Id = Guid.CreateVersion7(), TenantId = tenantId, BrandId = brand.Id, Title = "Ürün", Description = "" };
        var mapping = new BrandMapping { Id = Guid.CreateVersion7(), TenantId = tenantId, ConnectionId = Guid.CreateVersion7(), LocalId = brand.Id, ScopeExternalId = "", ExternalId = "77", Status = "VERIFIED", Version = 1 };
        db.Brands.Add(brand);
        db.Products.Add(product);
        db.BrandMappings.Add(mapping);
        return (brand, product, mapping);
    }

    [Fact]
    public void ADroppedProductTakesBackTheBrandItOpenedAlone()
    {
        using var db = Context();
        var (brand, product, mapping) = AddProductWithNewBrand(db, Guid.CreateVersion7());

        MarketplaceJobProcessor.DropNewCatalogProduct(db, product);

        Assert.Equal(EntityState.Detached, db.Entry(product).State);
        Assert.Equal(EntityState.Detached, db.Entry(brand).State);
        Assert.Equal(EntityState.Detached, db.Entry(mapping).State);
    }

    [Fact]
    public void ABrandAnotherNewProductUsesStays()
    {
        using var db = Context();
        var tenantId = Guid.CreateVersion7();
        var (brand, product, mapping) = AddProductWithNewBrand(db, tenantId);
        var sibling = new Product { Id = Guid.CreateVersion7(), TenantId = tenantId, BrandId = brand.Id, Title = "Ürün", Description = "" };
        db.Products.Add(sibling);

        MarketplaceJobProcessor.DropNewCatalogProduct(db, product);

        Assert.Equal(EntityState.Detached, db.Entry(product).State);
        Assert.Equal(EntityState.Added, db.Entry(brand).State);
        Assert.Equal(EntityState.Added, db.Entry(mapping).State);
        Assert.Equal(EntityState.Added, db.Entry(sibling).State);
    }
}
