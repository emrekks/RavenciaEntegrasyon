using MarketplaceHub.Application;
using MarketplaceHub.Infrastructure.Adapters.Hepsiburada;
using MarketplaceHub.Infrastructure.Persistence;
using System.Text.RegularExpressions;

namespace MarketplaceHub.Adapters.ContractTests;

public sealed class F6AHepsiburadaContractTests
{
    private static readonly AdapterContext Context = new(Guid.NewGuid(), Guid.NewGuid(), "test", "test", DateTimeOffset.UtcNow.AddMinutes(1));

    [Fact]
    public void Adapter_reuses_all_generic_F6A_ports()
    {
        var adapter = new HepsiburadaAdapter();
        Assert.IsAssignableFrom<IConnectionPort>(adapter);
        Assert.IsAssignableFrom<IReferenceDataPort>(adapter);
        Assert.IsAssignableFrom<IProductPort>(adapter);
        Assert.IsAssignableFrom<IInventoryPricePort>(adapter);
        Assert.IsAssignableFrom<IOrderPort>(adapter);
        Assert.IsAssignableFrom<IReturnPort>(adapter);
    }

    [Fact]
    public async Task Connection_and_read_are_fail_closed_without_partner_evidence()
    {
        var adapter = new HepsiburadaAdapter();
        var connection = await adapter.TestAsync(Context, TestContext.Current.CancellationToken);
        var products = await adapter.ListAsync(Context, new(null, 1), new(null), TestContext.Current.CancellationToken);
        Assert.Equal("HEPSIBURADA_CAPABILITY_UNVERIFIED", connection.Error?.Code);
        Assert.Equal("HEPSIBURADA_CAPABILITY_UNVERIFIED", products.Error?.Code);
    }

    [Fact]
    public async Task Every_external_write_is_disabled()
    {
        var adapter = new HepsiburadaAdapter();
        var product = await adapter.UpsertAsync(Context, new(Guid.NewGuid(), "hash", "{}"), TestContext.Current.CancellationToken);
        var stock = await adapter.PushStockAsync(Context, [], TestContext.Current.CancellationToken);
        var price = await adapter.PushPricesAsync(Context, [], TestContext.Current.CancellationToken);
        var package = await adapter.ExecutePackageActionAsync(Context, new("external", "action", "{}"), TestContext.Current.CancellationToken);
        var claim = await adapter.ExecuteAsync(Context, new("external", "action", null, null, []), TestContext.Current.CancellationToken);
        Assert.All(new[] { product.Error?.Code, stock.Error?.Code, price.Error?.Code, package.Error?.Code, claim.Error?.Code }, code => Assert.Equal("EXTERNAL_WRITE_DISABLED", code));
    }

    [Theory]
    [InlineData(401, AdapterErrorClass.Authentication)]
    [InlineData(409, AdapterErrorClass.BusinessConflict)]
    [InlineData(429, AdapterErrorClass.RateLimit)]
    [InlineData(500, AdapterErrorClass.Remote5xx)]
    [InlineData(422, AdapterErrorClass.Validation)]
    public void Http_error_classes_remain_distinct(int status, AdapterErrorClass expected) => Assert.Equal(expected, HepsiburadaErrorClassifier.FromHttpStatus(status).Class);

    [Fact]
    public void N11_and_Pazarama_adapters_are_not_created_in_F6A()
    {
        var root = FindRoot(); var adapters = Path.Combine(root, "src", "MarketplaceHub.Infrastructure", "Adapters");
        Assert.False(Directory.Exists(Path.Combine(adapters, "N11")));
        Assert.False(Directory.Exists(Path.Combine(adapters, "Pazarama")));
    }

    [Theory]
    [InlineData("TRENDYOL")]
    [InlineData("SHOPIFY")]
    [InlineData("HEPSIBURADA")]
    public void Local_dry_reconciliation_accepts_only_released_platforms(string platformCode)
    {
        Assert.True(LocalReconciliationPolicy.Supports(platformCode));
        Assert.False(LocalReconciliationPolicy.Supports("N11"));
        Assert.False(LocalReconciliationPolicy.Supports("PAZARAMA"));
    }

    [Fact]
    public void Hepsiburada_adapter_has_no_auth_network_or_secret_implementation()
    {
        var root = FindRoot();
        var adapterRoot = Path.Combine(root, "src", "MarketplaceHub.Infrastructure", "Adapters", "Hepsiburada");
        var sourceFiles = Directory.GetFiles(adapterRoot, "*.cs", SearchOption.AllDirectories);
        var source = string.Join(Environment.NewLine, sourceFiles.Select(File.ReadAllText));

        Assert.NotEmpty(sourceFiles);
        foreach (var forbidden in new[] { "HttpClient", "Authorization", "AuthenticationHeaderValue", "IDataProtector", "ProtectedPayload", "ApiKey", "ApiSecret", "ClientSecret", "AccessToken", "Password" })
            Assert.DoesNotContain(forbidden, source, StringComparison.Ordinal);
        Assert.All(Directory.GetFiles(adapterRoot, "*", SearchOption.AllDirectories), path => Assert.Contains(Path.GetExtension(path), new[] { ".cs", ".md" }));
    }

    [Fact]
    public void Hepsiburada_credential_and_connection_test_gates_remain_closed()
    {
        var root = FindRoot();
        var service = File.ReadAllText(Path.Combine(root, "src", "MarketplaceHub.Infrastructure", "Persistence", "F3ConnectionService.cs"));
        var page = File.ReadAllText(Path.Combine(root, "src", "MarketplaceHub.Web", "src", "F3Pages.tsx"));

        Assert.Equal(2, Regex.Matches(service, "HEPSIBURADA_AUTH_MODEL_UNVERIFIED", RegexOptions.CultureInvariant).Count);
        Assert.Contains("item.platformCode === 'HEPSIBURADA'", page, StringComparison.Ordinal);
        Assert.Contains("disabled={item.platformCode === 'HEPSIBURADA'}", page, StringComparison.Ordinal);
    }

    [Fact]
    public void Production_sources_and_docs_contain_no_high_confidence_secret_signatures()
    {
        var root = FindRoot();
        var patterns = new[]
        {
            new Regex("-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----", RegexOptions.CultureInvariant),
            new Regex("AKIA[0-9A-Z]{16}", RegexOptions.CultureInvariant),
            new Regex("gh[pousr]_[A-Za-z0-9]{20,}", RegexOptions.CultureInvariant),
            new Regex("xox[baprs]-[A-Za-z0-9-]{10,}", RegexOptions.CultureInvariant),
            new Regex("sk_(?:live|test)_[A-Za-z0-9]{16,}", RegexOptions.CultureInvariant)
        };
        var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".cs", ".json", ".md", ".ts", ".tsx", ".yml", ".yaml" };
        var files = new[] { Path.Combine(root, "src"), Path.Combine(root, "docs") }
            .SelectMany(path => Directory.GetFiles(path, "*", SearchOption.AllDirectories))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}node_modules{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(path => extensions.Contains(Path.GetExtension(path)))
            .Append(Path.Combine(root, ".env.example"));

        foreach (var file in files)
        {
            var content = File.ReadAllText(file);
            Assert.All(patterns, pattern => Assert.False(pattern.IsMatch(content), $"Potential secret signature in {Path.GetRelativePath(root, file)}"));
        }
    }

    private static string FindRoot() { var path = AppContext.BaseDirectory; while (!File.Exists(Path.Combine(path, "MarketplaceHub.sln"))) path = Directory.GetParent(path)?.FullName ?? throw new InvalidOperationException("Root not found"); return path; }
}
