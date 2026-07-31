using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace MarketplaceHub.Application.Tests;

public sealed class F4ModelMetadataTests
{
    private static AppDbContext Context() => new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql("Host=localhost;Database=metadata;Username=none;Password=none").Options);

    [Fact]
    public void F4_entities_use_billing_schema_and_tenant_scoped_identities()
    {
        using var db = Context();
        Assert.Equal("billing", db.Model.FindEntityType(typeof(Invoice))!.GetSchema());
        Assert.Equal("billing", db.Model.FindEntityType(typeof(InvoiceDocument))!.GetSchema());
        AssertUnique(db, typeof(Invoice), "TenantId", "IdempotencyKey");
        AssertUnique(db, typeof(InvoiceLine), "TenantId", "InvoiceId", "LineSequence");
        AssertUnique(db, typeof(InvoiceSubmissionAttempt), "TenantId", "InvoiceId", "AttemptNumber");
    }

    [Fact]
    public void Invoice_documents_reference_private_file_assets_with_tenant_boundary()
    {
        using var db = Context(); var entity = db.Model.FindEntityType(typeof(InvoiceDocument))!;
        Assert.Contains(entity.GetForeignKeys(), key => key.Properties.Select(x => x.Name).SequenceEqual(["TenantId", "FileAssetId"]));
    }

    private static void AssertUnique(AppDbContext db, Type type, params string[] names) => Assert.Contains(db.Model.FindEntityType(type)!.GetIndexes(), index => index.IsUnique && Names(index).SequenceEqual(names));
    private static IEnumerable<string> Names(IReadOnlyIndex index) => index.Properties.Select(property => property.Name);
}
