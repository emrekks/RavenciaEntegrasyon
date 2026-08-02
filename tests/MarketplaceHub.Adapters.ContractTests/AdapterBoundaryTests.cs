namespace MarketplaceHub.Adapters.ContractTests;

public sealed class AdapterBoundaryTests
{
    [Fact]
    public void F5_has_only_the_approved_adapters()
    {
        var root = FindRoot();
        var adapterRoot = Path.Combine(root, "src", "MarketplaceHub.Infrastructure", "Adapters");
        Assert.True(Directory.Exists(Path.Combine(adapterRoot, "Trendyol")));
        Assert.True(Directory.Exists(Path.Combine(adapterRoot, "TrendyolEFaturam")));
        Assert.True(Directory.Exists(Path.Combine(adapterRoot, "Shopify")));
        Assert.True(Directory.Exists(Path.Combine(adapterRoot, "Hepsiburada")));
        Assert.DoesNotContain(Directory.GetDirectories(adapterRoot), path => Path.GetFileName(path) is not ("Trendyol" or "TrendyolEFaturam" or "Shopify" or "Hepsiburada"));
    }
    private static string FindRoot() { var path = AppContext.BaseDirectory; while (!File.Exists(Path.Combine(path, "MarketplaceHub.sln"))) path = Directory.GetParent(path)?.FullName ?? throw new InvalidOperationException("Root not found"); return path; }
}
