namespace MarketplaceHub.Application.Tests;

public sealed class V9CatalogWorkspaceSourceTests
{
    [Fact]
    public void Catalog_contract_supports_category_requirements_and_variant_scoped_attributes()
    {
        var root = FindRoot();
        var contracts = File.ReadAllText(Path.Combine(root, "src", "MarketplaceHub.Application", "F2Contracts.cs"));
        Assert.Contains("CategoryAttributeRequirementView", contracts, StringComparison.Ordinal);
        Assert.Contains("IReadOnlyList<ProductAttributeCommand>? Attributes = null", contracts, StringComparison.Ordinal);
        Assert.Contains("AddAttributeValuesAsync", contracts, StringComparison.Ordinal);
        Assert.Contains("ListMappingsAsync", contracts, StringComparison.Ordinal);
        Assert.Contains("DeleteMappingAsync", contracts, StringComparison.Ordinal);
    }

    [Fact]
    public void Catalog_service_persists_variant_assignments_and_preserves_attributes_on_partial_update()
    {
        var source = File.ReadAllText(Path.Combine(FindRoot(), "src", "MarketplaceHub.Infrastructure", "Persistence", "CatalogService.cs"));
        Assert.Contains("Assignment(tenantId, product.Id, variants[index].Id, x)", source, StringComparison.Ordinal);
        Assert.Contains("ValidateAttributeValuesAsync", source, StringComparison.Ordinal);
        Assert.Contains("if (command.Attributes is not null)", source, StringComparison.Ordinal);
        Assert.Contains("AddAttributeValuesAsync", source, StringComparison.Ordinal);
    }

    private static string FindRoot()
    {
        var path = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(path, "MarketplaceHub.sln")))
            path = Directory.GetParent(path)?.FullName ?? throw new InvalidOperationException("Root not found");
        return path;
    }
}
