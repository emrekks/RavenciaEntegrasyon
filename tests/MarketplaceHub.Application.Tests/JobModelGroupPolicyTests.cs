using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class JobModelGroupPolicyTests
{
    [Fact]
    public void ProductId_ReadsThePayloadsCardAndNothingElse()
    {
        var id = Guid.NewGuid();
        Assert.Equal(id, JobModelGroupPolicy.ProductId($$"""{"JobId":"{{Guid.NewGuid()}}","ProductId":"{{id}}"}"""));
        Assert.Null(JobModelGroupPolicy.ProductId("""{"ProductId":null}"""));
        Assert.Null(JobModelGroupPolicy.ProductId("not json"));
        Assert.Null(JobModelGroupPolicy.ProductId("[]"));
    }

    [Fact]
    public void Key_SeparatesConnectionTypeSendAndModel()
    {
        var connection = Guid.NewGuid();
        var key = JobModelGroupPolicy.Key(connection, "PRODUCT_CREATE", "run-1", "model:b:MZ1");
        Assert.NotEqual(key, JobModelGroupPolicy.Key(connection, "PRODUCT_CREATE", "run-2", "model:b:MZ1"));
        Assert.NotEqual(key, JobModelGroupPolicy.Key(connection, "PRODUCT_CREATE", "run-1", "model:b:MZ2"));
        Assert.NotEqual(key, JobModelGroupPolicy.Key(connection, "PRODUCT_UPDATE", "run-1", "model:b:MZ1"));
        Assert.NotEqual(key, JobModelGroupPolicy.Key(Guid.NewGuid(), "PRODUCT_CREATE", "run-1", "model:b:MZ1"));
    }

    [Fact]
    public void Clusters_CutAKeysJobsFromTheirFirstOneAndPutEveryJobInExactlyOneGroup()
    {
        var start = new DateTimeOffset(2026, 11, 25, 10, 0, 0, TimeSpan.Zero);
        var jobs = new[] { 0, 5, 59, 60, 61, 180 }.Select(minutes => (Id: Guid.NewGuid(), At: start.AddMinutes(minutes))).Reverse().ToList();
        var groups = JobModelGroupPolicy.Clusters(jobs, job => job.At, job => job.Id);
        Assert.Equal([[0, 5, 59], [60, 61], [180]], groups.Select(group => group.Select(job => (int)(job.At - start).TotalMinutes).ToArray()).ToArray());
        Assert.False(JobModelGroupPolicy.NamesASend("publication-status:0a1b"));
        Assert.True(JobModelGroupPolicy.NamesASend("5c2a9e17"));
    }

    [Fact]
    public void CardVariantCount_CountsOnlyTheCardsOwnVariantsAndKeepsUnknownUnknown()
    {
        // A family request (PTT AVM, Shopify) carries every colour: the card counts its own variants only.
        var payload = """{"ProductId":"00000000-0000-0000-0000-000000000001","PayloadJson":"{\"items\":[{\"barcode\":\"A1\"},{\"barcode\":\"A2\"},{\"barcode\":\"B1\"}]}"}""";
        Assert.Equal(2, JobModelGroupPolicy.CardVariantCount("PRODUCT_CREATE", payload, [("SKU-A1", "A1"), ("SKU-A2", "a2"), ("SKU-A3", null)]));
        // An approval check carries no items: unknown, not zero.
        Assert.Null(JobModelGroupPolicy.CardVariantCount("PRODUCT_APPROVAL_RECONCILE", """{"ProductId":"00000000-0000-0000-0000-000000000001"}""", [("SKU-A1", "A1")]));
        Assert.Null(JobModelGroupPolicy.CardVariantCount("PRODUCT_CREATE", payload, [("SKU-C1", "C1")]));
        Assert.Equal(5, JobModelGroupPolicy.GroupVariantCount([2, 3]));
        Assert.Null(JobModelGroupPolicy.GroupVariantCount([2, null]));
    }
}
