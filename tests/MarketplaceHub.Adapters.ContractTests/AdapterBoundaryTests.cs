namespace MarketplaceHub.Adapters.ContractTests;

public sealed class AdapterBoundaryTests
{
    [Fact]
    public void F3_has_only_the_Trendyol_adapter()
    {
        var root = FindRoot();
        var adapterRoot = Path.Combine(root, "src", "MarketplaceHub.Infrastructure", "Adapters");
        Assert.True(Directory.Exists(Path.Combine(adapterRoot, "Trendyol")));
        Assert.DoesNotContain(Directory.GetDirectories(adapterRoot), path => !string.Equals(Path.GetFileName(path), "Trendyol", StringComparison.Ordinal));
    }
    private static string FindRoot() { var path = AppContext.BaseDirectory; while (!File.Exists(Path.Combine(path, "MarketplaceHub.sln"))) path = Directory.GetParent(path)?.FullName ?? throw new InvalidOperationException("Root not found"); return path; }
}
