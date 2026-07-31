namespace MarketplaceHub.Api.IntegrationTests;

public sealed class ApiSurfaceTests
{
    [Fact]
    public void Api_source_keeps_F2_and_adds_only_approved_F3_route_families()
    {
        var root = FindRoot(); var f2 = File.ReadAllText(Path.Combine(root, "src", "MarketplaceHub.Api", "F2", "F2Endpoints.cs"));
        foreach (var required in new[] { "/catalog/categories", "/catalog/brands", "/catalog/attributes", "/products", "/imports", "/inventory", "/channel-offers", "/reference-data", "/mappings" }) Assert.Contains(required, f2, StringComparison.Ordinal);
        foreach (var forbidden in new[] { "/orders", "/shipments", "/returns", "/invoices", "/integrations", "/webhooks", "/tenants", "/users" }) Assert.DoesNotContain(forbidden, f2, StringComparison.Ordinal);
        var f3 = File.ReadAllText(Path.Combine(root, "src", "MarketplaceHub.Api", "F3", "F3Endpoints.cs"));
        foreach (var required in new[] { "/connections", "/orders", "/shipments", "/returns", "/hooks/{connectionPublicId:guid}/{routeToken}" }) Assert.Contains(required, f3, StringComparison.Ordinal);
        foreach (var forbidden in new[] { "/invoices", "/billing", "/reports", "/tenants", "/users", "Shopify", "Hepsiburada", "N11", "Pazarama" }) Assert.DoesNotContain(forbidden, f3, StringComparison.OrdinalIgnoreCase);
    }
    private static string FindRoot() { var path = AppContext.BaseDirectory; while (!File.Exists(Path.Combine(path, "MarketplaceHub.sln"))) path = Directory.GetParent(path)?.FullName ?? throw new InvalidOperationException("Root not found"); return path; }
}
