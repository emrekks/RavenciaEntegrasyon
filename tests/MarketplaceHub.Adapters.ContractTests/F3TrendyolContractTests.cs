using MarketplaceHub.Infrastructure.Adapters.Trendyol.Contracts;
using MarketplaceHub.Infrastructure.Adapters.Trendyol.Mapping;

namespace MarketplaceHub.Adapters.ContractTests;

public sealed class F3TrendyolContractTests
{
    [Fact]
    public void Anonymous_order_fixture_maps_cursor_package_and_line_without_PII()
    {
        var json = Fixture("order-success.json"); var page = TrendyolJsonMapper.Orders(json); var order = Assert.Single(page.Items); var package = Assert.Single(order.Packages); var line = Assert.Single(order.Lines);
        Assert.False(page.HasMore); Assert.Equal("ORDER-ANON-001", order.ExternalOrderId); Assert.Equal("900001", package.ExternalPackageId); Assert.Equal(125.50m, package.NetAmount); Assert.Equal("SKU-ANON-001", line.Sku);
        Assert.Equal(125.50m, line.UnitPrice); Assert.Equal(20m, line.VatRate);
        Assert.DoesNotContain("@", json, StringComparison.Ordinal); Assert.DoesNotContain("+90", json, StringComparison.Ordinal); Assert.True(TrendyolContractGuard.HasContentArray(json));
    }

    [Fact]
    public void Legacy_order_fields_remain_readable_during_Trendyol_transition()
    {
        const string json = """
            {"content":[{"shipmentPackageId":1,"orderNumber":"O-1","currencyCode":"TRY","packageTotalPrice":120,
            "lines":[{"id":2,"merchantSku":"OLD-SKU","productName":"Legacy","quantity":1,"price":120,"vatBaseAmount":20}]}]}
            """;
        var line = Assert.Single(Assert.Single(TrendyolJsonMapper.Orders(json).Items).Lines);
        Assert.Equal("2", line.ExternalLineId); Assert.Equal("OLD-SKU", line.Sku); Assert.Equal(120m, line.UnitPrice); Assert.Equal(20m, line.VatRate);
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

    [Fact]
    public void Documented_brand_response_maps_to_canonical_BRANDS_resource()
    {
        const string json = "{\"brands\":[{\"id\":1584892,\"name\":\"brand-1\"}]}";
        var brand = Assert.Single(TrendyolJsonMapper.References("BRANDS", json, null));
        Assert.Equal("BRANDS", brand.ResourceType);
        Assert.Equal("1584892", brand.ExternalId);
        Assert.Equal("brand-1", brand.Name);
        Assert.True(brand.IsActive);
    }

    [Fact]
    public void Capability_discovery_probes_documented_reads_and_contains_no_write_request()
    {
        var source = File.ReadAllText(Path.Combine(FindRoot(), "src", "MarketplaceHub.Infrastructure", "Adapters", "Trendyol", "TrendyolHttpClient.cs"));
        var discovery = source[source.IndexOf("DiscoverCapabilitiesAsync", StringComparison.Ordinal)..source.IndexOf("public async Task<AdapterResult<AdapterPageResult<RemoteReferenceItem>>>", StringComparison.Ordinal)];

        Assert.Contains("F3Capabilities.ProductRead", discovery, StringComparison.Ordinal);
        Assert.Contains("F3Capabilities.OrderRead", discovery, StringComparison.Ordinal);
        Assert.Contains("F3Capabilities.ReturnRead", discovery, StringComparison.Ordinal);
        Assert.Contains("F3Capabilities.ReferenceRead", discovery, StringComparison.Ordinal);
        Assert.Contains("new(\"CATEGORIES\", null)", discovery, StringComparison.Ordinal);
        Assert.Contains("new(null, 1)", discovery, StringComparison.Ordinal);
        Assert.DoesNotContain("HttpMethod.Post", discovery, StringComparison.Ordinal);
        Assert.DoesNotContain("HttpMethod.Put", discovery, StringComparison.Ordinal);
        Assert.DoesNotContain("HttpMethod.Delete", discovery, StringComparison.Ordinal);
    }

    private static string Fixture(string name) => File.ReadAllText(Path.Combine(FindRoot(), "src", "MarketplaceHub.Infrastructure", "Adapters", "Trendyol", "Fixtures", name));
    private static string FindRoot() { var path = AppContext.BaseDirectory; while (!File.Exists(Path.Combine(path, "MarketplaceHub.sln"))) path = Directory.GetParent(path)?.FullName ?? throw new InvalidOperationException("Root not found"); return path; }
}
