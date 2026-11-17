using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class ProductImportFailureReasonPolicyTests
{
    // A version conflict in the product import names its rows (table, key, state, the version the save expected), so
    // the next MZ002K-like failure shows what it collided with.
    [Fact]
    public void ConcurrencyConflictRows_NamesTableKeyStateAndExpectedVersion()
    {
        using var db = new AppDbContext(new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<AppDbContext>().UseNpgsql("Host=localhost;Database=unused").Options, TimeProvider.System);
        var id = Guid.Parse("01a1dc03-0000-7000-8000-000000000001");
        var product = new Product { Id = id, TenantId = Guid.NewGuid(), Title = "t", Description = "d", Version = 3 };
        var entry = db.Attach(product);
        entry.State = Microsoft.EntityFrameworkCore.EntityState.Modified;
        var conflict = new Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException("conflict", [((Microsoft.EntityFrameworkCore.Infrastructure.IInfrastructure<Microsoft.EntityFrameworkCore.ChangeTracking.Internal.InternalEntityEntry>)entry).Instance]);

        Assert.Equal($"catalog.products Id={id} Modified Version=3", CatalogImportService.ConcurrencyConflictRows(conflict));
        Assert.Equal("kayıt bilgisi yok", CatalogImportService.ConcurrencyConflictRows(new Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException("conflict", Array.Empty<Microsoft.EntityFrameworkCore.Update.IUpdateEntry>())));
    }

    [Fact]
    public void Build_GroupsRepeatedNestedTransactionFailures_AndExplainsCause()
    {
        var issues = new[]
        {
            Issue("100", "exception=InvalidOperationException; detail=The connection is already in a transaction and cannot participate in another transaction."),
            Issue("101", "exception=InvalidOperationException; detail=The connection is already in a transaction and cannot participate in another transaction."),
            Issue("102", "exception=InvalidOperationException; detail=Sequence contains more than one matching element")
        };

        var reasons = ProductImportFailureReasonPolicy.Build(issues);

        Assert.Equal(2, reasons.Count);
        Assert.Equal(2, reasons[0].AffectedRecords);
        Assert.Equal("database-transaction-already-open", reasons[0].Key);
        Assert.Contains("ikinci işlemi başlatmaya çalıştı", reasons[0].Description);
        Assert.Equal(new[] { "100", "101" }, reasons[0].SampleProductIds);
        Assert.Equal("multiple-mapped-attributes", reasons[1].Key);
        Assert.Contains("tekilleştiriliyor", reasons[1].Description);
    }

    private static OperationalIssue Issue(string productId, string technicalDetail) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        DedupeKey = $"product-sync-import:connection:{productId}",
        Code = "PRODUCT_IMPORT_FAILED",
        Summary = $"Pazar yeri ürünü '{productId}' aktarılmadı. Veritabanı ayrıntısı: {technicalDetail}",
        Status = IssueStatus.Open,
        FirstSeenAt = DateTimeOffset.UtcNow,
        LastSeenAt = DateTimeOffset.UtcNow,
        OccurrenceCount = 1
    };
}
