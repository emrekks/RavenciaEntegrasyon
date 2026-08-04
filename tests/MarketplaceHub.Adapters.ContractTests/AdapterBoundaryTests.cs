namespace MarketplaceHub.Adapters.ContractTests;

public sealed class AdapterBoundaryTests
{
    [Fact]
    public void Active_scope_has_only_Trendyol_and_Trendyol_EFaturam_adapters()
    {
        var root = FindRoot(); var adapterRoot = Path.Combine(root, "src", "MarketplaceHub.Infrastructure", "Adapters");
        Assert.True(Directory.Exists(Path.Combine(adapterRoot, "Trendyol")));
        Assert.True(Directory.Exists(Path.Combine(adapterRoot, "TrendyolEFaturam")));
        Assert.DoesNotContain(Directory.GetDirectories(adapterRoot), path => Path.GetFileName(path) is not ("Trendyol" or "TrendyolEFaturam"));
    }
    private static string FindRoot() { var path = AppContext.BaseDirectory; while (!File.Exists(Path.Combine(path, "MarketplaceHub.sln"))) path = Directory.GetParent(path)?.FullName ?? throw new InvalidOperationException("Root not found"); return path; }
}
