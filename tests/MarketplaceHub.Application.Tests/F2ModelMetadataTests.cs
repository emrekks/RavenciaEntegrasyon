using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace MarketplaceHub.Application.Tests;

public sealed class F2ModelMetadataTests
{
    private static AppDbContext Context() => new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql("Host=localhost;Database=metadata;Username=none;Password=none").Options);

    [Fact]
    public void F2_entities_are_mapped_to_binding_physical_schemas()
    {
        using var db = Context();
        Assert.Equal("catalog", db.Model.FindEntityType(typeof(Product))!.GetSchema());
        Assert.Equal("catalog", db.Model.FindEntityType(typeof(ImportSession))!.GetSchema());
        Assert.Equal("inventory", db.Model.FindEntityType(typeof(InventoryItem))!.GetSchema());
        Assert.Equal("integration", db.Model.FindEntityType(typeof(ReferenceSnapshot))!.GetSchema());
    }

    [Fact]
    public void Tenant_scoped_uniqueness_guards_sku_ledger_and_offer_identity()
    {
        using var db = Context();
        Assert.Contains(db.Model.FindEntityType(typeof(ProductVariant))!.GetIndexes(), index => index.IsUnique && Names(index).SequenceEqual(new[] { "TenantId", "SkuNormalized" }));
        Assert.Contains(db.Model.FindEntityType(typeof(StockLedgerEntry))!.GetIndexes(), index => index.IsUnique && Names(index).SequenceEqual(new[] { "TenantId", "IdempotencyKey" }));
        Assert.Contains(db.Model.FindEntityType(typeof(ChannelOffer))!.GetIndexes(), index => index.IsUnique && Names(index).SequenceEqual(new[] { "TenantId", "ConnectionId", "VariantId" }));
    }

    private static IEnumerable<string> Names(IReadOnlyIndex index) => index.Properties.Select(property => property.Name);
}
