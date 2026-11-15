using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// VERITABANI-DAGITIM-10: with distinct nulls the unique indexes let a card-level attribute value or a card-level picture
// place be stored twice; NULLS NOT DISTINCT makes them unique.
public sealed class CatalogNullsNotDistinctModelTests
{
    [Fact]
    public void CatalogUniquesTreatNullsAsEqual()
    {
        using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Database=schema-model-only;Username=none;Password=none").Options);
        var assignments = Assert.Single(db.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(ProductAttributeAssignment))!.GetIndexes(), index => index.IsUnique
            && index.Properties.Select(property => property.Name).SequenceEqual(new[]
            {
                nameof(ProductAttributeAssignment.TenantId), nameof(ProductAttributeAssignment.ProductId), nameof(ProductAttributeAssignment.VariantId),
                nameof(ProductAttributeAssignment.AttributeId), nameof(ProductAttributeAssignment.ValueId),
            }));
        var media = Assert.Single(db.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(ProductMedia))!.GetIndexes(), index => index.IsUnique
            && index.Properties.Select(property => property.Name).SequenceEqual(new[]
            {
                nameof(ProductMedia.TenantId), nameof(ProductMedia.ProductId), nameof(ProductMedia.VariantId), nameof(ProductMedia.SortOrder),
            }));

        Assert.False(assignments.GetAreNullsDistinct() ?? true);
        Assert.False(media.GetAreNullsDistinct() ?? true);
    }
}
