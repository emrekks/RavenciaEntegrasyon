namespace MarketplaceHub.Api.IntegrationTests;

public sealed class ApiSurfaceTests
{
    [Fact]
    public void Api_source_contains_the_exact_F2_route_families_without_F3_surfaces()
    {
        var root = FindRoot(); var f2 = File.ReadAllText(Path.Combine(root, "src", "MarketplaceHub.Api", "F2", "F2Endpoints.cs"));
        foreach (var required in new[] { "/catalog/categories", "/catalog/brands", "/catalog/attributes", "/products", "/imports", "/inventory", "/channel-offers", "/reference-data", "/mappings" }) Assert.Contains(required, f2, StringComparison.Ordinal);
        foreach (var forbidden in new[] { "/orders", "/shipments", "/returns", "/invoices", "/integrations", "/webhooks", "/tenants", "/users" }) Assert.DoesNotContain(forbidden, f2, StringComparison.Ordinal);
    }
    private static string FindRoot() { var path = AppContext.BaseDirectory; while (!File.Exists(Path.Combine(path, "MarketplaceHub.sln"))) path = Directory.GetParent(path)?.FullName ?? throw new InvalidOperationException("Root not found"); return path; }
}
