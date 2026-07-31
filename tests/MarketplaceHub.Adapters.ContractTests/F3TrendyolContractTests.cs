using MarketplaceHub.Infrastructure.Adapters.Trendyol.Contracts;
using MarketplaceHub.Infrastructure.Adapters.Trendyol.Mapping;

namespace MarketplaceHub.Adapters.ContractTests;

public sealed class F3TrendyolContractTests
{
    [Fact]
    public void Anonymous_order_fixture_maps_cursor_package_and_line_without_PII()
    {
        var json = Fixture("order-success.json"); var page = TrendyolJsonMapper.Orders(json); var order = Assert.Single(page.Items); var package = Assert.Single(order.Packages); var line = Assert.Single(order.Lines);
        Assert.False(page.HasMore); Assert.Equal("ORDER-ANON-001", order.ExternalOrderId); Assert.Equal("900001", package.ExternalPackageId); Assert.Equal("SKU-ANON-001", line.Sku);
        Assert.DoesNotContain("@", json, StringComparison.Ordinal); Assert.DoesNotContain("+90", json, StringComparison.Ordinal); Assert.True(TrendyolContractGuard.HasContentArray(json));
    }

    [Fact]
    public void Partial_batch_preserves_each_line_result()
    {
        var json = Fixture("batch-partial.json"); var batch = TrendyolJsonMapper.Batch(json, "fallback"); Assert.Equal("BATCH-ANON-001", batch.ExternalOperationId); Assert.Equal(2, batch.Lines.Count); Assert.Single(batch.Lines, x => x.Succeeded); Assert.Single(batch.Lines, x => !x.Succeeded); Assert.True(TrendyolContractGuard.HasBatchRequestId(json));
    }

    [Fact]
    public void Product_and_return_fixtures_map_only_documented_identifiers()
    {
        Assert.Single(TrendyolJsonMapper.Products(Fixture("product-approved.json")).Items); var claim = Assert.Single(TrendyolJsonMapper.Returns(Fixture("return-success.json")).Items); Assert.Equal("CLAIM-ANON-001", claim.ExternalClaimId); Assert.Equal("Created", claim.RawStatus); Assert.Single(claim.Lines);
    }

    private static string Fixture(string name) => File.ReadAllText(Path.Combine(FindRoot(), "src", "MarketplaceHub.Infrastructure", "Adapters", "Trendyol", "Fixtures", name));
    private static string FindRoot() { var path = AppContext.BaseDirectory; while (!File.Exists(Path.Combine(path, "MarketplaceHub.sln"))) path = Directory.GetParent(path)?.FullName ?? throw new InvalidOperationException("Root not found"); return path; }
}
