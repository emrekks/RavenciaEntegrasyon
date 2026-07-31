namespace MarketplaceHub.Api.IntegrationTests;

public sealed class ApiSurfaceTests
{
    [Fact]
    public void Api_source_contains_only_F1_route_family()
    {
        var root = FindRoot(); var source = File.ReadAllText(Path.Combine(root, "src", "MarketplaceHub.Api", "Security", "AuthEndpoints.cs"));
        Assert.Contains("/api/v1/auth", source, StringComparison.Ordinal);
        foreach (var forbidden in new[] { "/products", "/orders", "/integrations", "/tenants", "/users" }) Assert.DoesNotContain(forbidden, source, StringComparison.Ordinal);
    }
    private static string FindRoot() { var path = AppContext.BaseDirectory; while (!File.Exists(Path.Combine(path, "MarketplaceHub.sln"))) path = Directory.GetParent(path)?.FullName ?? throw new InvalidOperationException("Root not found"); return path; }
}
