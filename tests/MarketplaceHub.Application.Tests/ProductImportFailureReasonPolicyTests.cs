using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class ProductImportFailureReasonPolicyTests
{
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
