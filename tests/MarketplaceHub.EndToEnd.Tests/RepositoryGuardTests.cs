namespace MarketplaceHub.EndToEnd.Tests;

public sealed class RepositoryGuardTests
{
    [Fact]
    public void Domain_has_no_project_or_package_dependencies()
    {
        var root = FindRoot(); var project = File.ReadAllText(Path.Combine(root, "src", "MarketplaceHub.Domain", "MarketplaceHub.Domain.csproj"));
        Assert.DoesNotContain("PackageReference", project, StringComparison.Ordinal);
        Assert.DoesNotContain("ProjectReference", project, StringComparison.Ordinal);
    }

    [Fact]
    public void F2_web_surface_has_only_approved_routes_and_no_later_phase_menu()
    {
        var root = FindRoot(); var source = File.ReadAllText(Path.Combine(root, "src", "MarketplaceHub.Web", "src", "App.tsx"));
        foreach (var required in new[] { "/products", "/products/new", "/products/:id", "/catalog/categories", "/catalog/brands", "/catalog/attributes", "/imports", "/imports/:id", "/inventory" }) Assert.Contains(required, source, StringComparison.Ordinal);
        foreach (var forbidden in new[] { "/orders", "/shipments", "/returns", "/invoices", "/integrations", "/operations", "/tenants", "/users" }) Assert.DoesNotContain(forbidden, source, StringComparison.Ordinal);
    }
    private static string FindRoot() { var path = AppContext.BaseDirectory; while (!File.Exists(Path.Combine(path, "MarketplaceHub.sln"))) path = Directory.GetParent(path)?.FullName ?? throw new InvalidOperationException("Root not found"); return path; }
}
