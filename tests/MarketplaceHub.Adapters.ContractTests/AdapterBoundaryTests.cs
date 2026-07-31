namespace MarketplaceHub.Adapters.ContractTests;

public sealed class AdapterBoundaryTests
{
    [Fact]
    public void No_real_platform_adapter_exists_in_F1()
    {
        var root = FindRoot();
        var files = Directory.GetFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories);
        Assert.DoesNotContain(files, path => path.Contains("Trendyol", StringComparison.OrdinalIgnoreCase) || path.Contains("Hepsiburada", StringComparison.OrdinalIgnoreCase));
    }
    private static string FindRoot() { var path = AppContext.BaseDirectory; while (!File.Exists(Path.Combine(path, "MarketplaceHub.sln"))) path = Directory.GetParent(path)?.FullName ?? throw new InvalidOperationException("Root not found"); return path; }
}
