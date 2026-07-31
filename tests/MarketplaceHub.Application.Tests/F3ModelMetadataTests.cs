using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace MarketplaceHub.Application.Tests;

public sealed class F3ModelMetadataTests
{
    private static AppDbContext Context() => new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql("Host=localhost;Database=metadata;Username=none;Password=none").Options);

    [Fact]
    public void F3_sales_and_integration_entities_use_the_existing_database_chain()
    {
        using var db = Context(); Assert.Equal("sales", db.Model.FindEntityType(typeof(Order))!.GetSchema()); Assert.Equal("sales", db.Model.FindEntityType(typeof(ReturnClaim))!.GetSchema()); Assert.Equal("integration", db.Model.FindEntityType(typeof(PlatformCapability))!.GetSchema());
    }

    [Fact]
    public void Remote_identity_and_idempotency_indexes_are_tenant_scoped()
    {
        using var db = Context(); AssertUnique(db, typeof(Order), "TenantId", "ConnectionId", "ExternalOrderId"); AssertUnique(db, typeof(ShipmentPackage), "TenantId", "ConnectionId", "ExternalPackageId"); AssertUnique(db, typeof(ReturnClaim), "TenantId", "ConnectionId", "ExternalClaimId"); AssertUnique(db, typeof(ReturnDecision), "TenantId", "IdempotencyKey");
    }
    private static void AssertUnique(AppDbContext db, Type type, params string[] names) => Assert.Contains(db.Model.FindEntityType(type)!.GetIndexes(), index => index.IsUnique && Names(index).SequenceEqual(names));
    private static IEnumerable<string> Names(IReadOnlyIndex index) => index.Properties.Select(property => property.Name);
}
