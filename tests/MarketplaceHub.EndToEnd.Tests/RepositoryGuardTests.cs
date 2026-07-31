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
    private static string FindRoot() { var path = AppContext.BaseDirectory; while (!File.Exists(Path.Combine(path, "MarketplaceHub.sln"))) path = Directory.GetParent(path)?.FullName ?? throw new InvalidOperationException("Root not found"); return path; }
}
