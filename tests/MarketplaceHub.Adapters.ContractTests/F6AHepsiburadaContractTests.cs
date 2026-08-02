using MarketplaceHub.Application;
using MarketplaceHub.Domain;
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
    public async Task Connection_capability_and_every_read_are_fail_closed_without_partner_evidence()
    {
        var adapter = new HepsiburadaAdapter();
        var returns = (IReturnPort)adapter;
        var connection = await adapter.TestAsync(Context, TestContext.Current.CancellationToken);
        var capabilities = await adapter.DiscoverCapabilitiesAsync(Context, TestContext.Current.CancellationToken);
        var references = await adapter.ReadAsync(Context, new("UNVERIFIED", null), new(null, 1), TestContext.Current.CancellationToken);
        var products = await adapter.ListAsync(Context, new(null, 1), new(null), TestContext.Current.CancellationToken);
        var operation = await adapter.GetOperationAsync(Context, "unverified", TestContext.Current.CancellationToken);
        var orders = await adapter.PollAsync(Context, new(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow), new(null, 1), TestContext.Current.CancellationToken);
        var order = await adapter.GetAsync(Context, "unverified", TestContext.Current.CancellationToken);
        var claims = await returns.PollAsync(Context, new(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow), new(null, 1), TestContext.Current.CancellationToken);
        var claim = await returns.GetAsync(Context, "unverified", TestContext.Current.CancellationToken);

        Assert.All(
            new[] { connection.Error, capabilities.Error, references.Error, products.Error, operation.Error, orders.Error, order.Error, claims.Error, claim.Error },
            error => Assert.Equal("HEPSIBURADA_CAPABILITY_UNVERIFIED", error?.Code));
    }

    [Fact]
    public async Task Every_external_write_is_disabled()
    {
        var adapter = new HepsiburadaAdapter();
        var product = await adapter.UpsertAsync(Context, new(Guid.NewGuid(), "hash", "{}"), TestContext.Current.CancellationToken);
        var archive = await adapter.ArchiveAsync(Context, new("unverified", null), TestContext.Current.CancellationToken);
        var stock = await adapter.PushStockAsync(Context, [], TestContext.Current.CancellationToken);
        var price = await adapter.PushPricesAsync(Context, [], TestContext.Current.CancellationToken);
        var package = await adapter.ExecutePackageActionAsync(Context, new("external", "action", "{}"), TestContext.Current.CancellationToken);
        var claim = await adapter.ExecuteAsync(Context, new("external", "action", null, null, []), TestContext.Current.CancellationToken);
        Assert.All(new[] { product.Error?.Code, archive.Error?.Code, stock.Error?.Code, price.Error?.Code, package.Error?.Code, claim.Error?.Code }, code => Assert.Equal("EXTERNAL_WRITE_DISABLED", code));
    }

    [Theory]
    [InlineData(401, AdapterErrorClass.Authentication)]
    [InlineData(403, AdapterErrorClass.Authentication)]
    [InlineData(404, AdapterErrorClass.NotFound)]
    [InlineData(409, AdapterErrorClass.BusinessConflict)]
    [InlineData(429, AdapterErrorClass.RateLimit)]
    [InlineData(500, AdapterErrorClass.Remote5xx)]
    [InlineData(422, AdapterErrorClass.Validation)]
    public void Http_error_classes_remain_distinct(int status, AdapterErrorClass expected) => Assert.Equal(expected, HepsiburadaErrorClassifier.FromHttpStatus(status).Class);

    [Fact]
    public void Timeout_remains_a_retryable_transient_network_error()
    {
        var error = HepsiburadaErrorClassifier.Timeout();

        Assert.Equal(AdapterErrorClass.TransientNetwork, error.Class);
        Assert.Equal("HEPSIBURADA_TIMEOUT", error.Code);
        Assert.NotNull(error.RetryAfter);
    }

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

    [Fact]
    public void Package_safety_accepts_valid_split_cancel_ship_deliver_return_chain()
    {
        var remote = new RemotePackageAllocation("line-1", 8, 2, 6, 5, 1);

        Assert.True(PackageIngestionSafety.TryNormalize(10, remote, ShipmentPackageStatus.Processing, out var safe));
        Assert.Equal(8, safe.ActiveAllocatedQuantity);
        Assert.Equal(2, safe.CancelledQuantity);
        Assert.Equal(6, safe.ShippedQuantity);
        Assert.Equal(5, safe.DeliveredQuantity);
        Assert.Equal(1, safe.ReturnedQuantity);
    }

    [Theory]
    [InlineData(9, 2, 6, 5, 1)]
    [InlineData(8, 2, 9, 5, 1)]
    [InlineData(8, 2, 6, 7, 1)]
    [InlineData(8, 2, 6, 5, 6)]
    [InlineData(-1, 11, 0, 0, 0)]
    public void Package_safety_rejects_quantity_invariant_violations(decimal active, decimal cancelled, decimal shipped, decimal delivered, decimal returned)
    {
        var remote = new RemotePackageAllocation("line-1", active, cancelled, shipped, delivered, returned);

        Assert.False(PackageIngestionSafety.TryNormalize(10, remote, ShipmentPackageStatus.Processing, out _));
    }

    [Theory]
    [InlineData(ShipmentPackageStatus.Cancelled, 0, 10, 0, 0, 0)]
    [InlineData(ShipmentPackageStatus.Shipped, 10, 0, 10, 0, 0)]
    [InlineData(ShipmentPackageStatus.Delivered, 10, 0, 10, 10, 0)]
    [InlineData(ShipmentPackageStatus.Returned, 10, 0, 10, 10, 10)]
    public void Package_safety_normalizes_canonical_terminal_progress(
        ShipmentPackageStatus status,
        decimal active,
        decimal cancelled,
        decimal shipped,
        decimal delivered,
        decimal returned)
    {
        Assert.True(PackageIngestionSafety.TryNormalize(10, new("line-1", 10, 0, 0, 0, 0), status, out var safe));
        Assert.Equal(new NormalizedPackageAllocation(active, cancelled, shipped, delivered, returned), safe);
    }

    [Fact]
    public void Package_event_identity_is_deterministic_and_old_or_regressive_state_is_rejected()
    {
        var currentAt = DateTimeOffset.Parse("2026-08-02T10:00:00Z");
        var olderAt = currentAt.AddSeconds(-1);

        Assert.Equal(PackageIngestionSafety.EventId("package-1", currentAt), PackageIngestionSafety.EventId("package-1", currentAt));
        var package = new RemotePackage("package-1", null, "UNVERIFIED", currentAt, null, null, []);
        Assert.True(PackageIngestionSafety.AllEventsRecorded([package], new HashSet<string>(StringComparer.Ordinal) { PackageIngestionSafety.EventId("package-1", currentAt) }));
        Assert.False(PackageIngestionSafety.AllEventsRecorded([package], new HashSet<string>(StringComparer.Ordinal)));
        Assert.False(PackageIngestionSafety.ShouldAccept(ShipmentPackageStatus.Shipped, currentAt, ShipmentPackageStatus.Delivered, olderAt));
        Assert.False(PackageIngestionSafety.ShouldAccept(ShipmentPackageStatus.Delivered, currentAt, ShipmentPackageStatus.New, currentAt.AddSeconds(1)));
        Assert.True(PackageIngestionSafety.ShouldAccept(ShipmentPackageStatus.Shipped, currentAt, ShipmentPackageStatus.Delivered, currentAt.AddSeconds(1)));
    }

    [Fact]
    public void Package_event_is_rejected_as_a_whole_for_duplicate_or_unknown_line_allocation()
    {
        var ordered = new Dictionary<string, decimal>(StringComparer.Ordinal) { ["line-1"] = 2 };
        var allocation = new RemotePackageAllocation("line-1", 2, 0, 0, 0, 0);

        Assert.False(PackageIngestionSafety.TryNormalizeAll(ordered, [allocation, allocation], ShipmentPackageStatus.Processing, out _));
        Assert.False(PackageIngestionSafety.TryNormalizeAll(ordered, [new("unknown", 1, 0, 0, 0, 0)], ShipmentPackageStatus.Processing, out _));
        Assert.True(PackageIngestionSafety.TryNormalizeAll(ordered, [allocation], ShipmentPackageStatus.Processing, out var safe));
        Assert.Single(safe);
    }

    [Fact]
    public void Order_event_is_rejected_before_persistence_for_duplicate_or_negative_lines()
    {
        var valid = new RemoteOrderLine("line-1", "sku", null, "title", 2, 10, 20, "UNVERIFIED");
        var negative = valid with { ExternalLineId = "line-2", Quantity = -1 };

        Assert.False(PackageIngestionSafety.TryGetOrderedQuantities([valid, valid], out _));
        Assert.False(PackageIngestionSafety.TryGetOrderedQuantities([valid, negative], out _));
        Assert.True(PackageIngestionSafety.TryGetOrderedQuantities([valid], out var quantities));
        Assert.Equal(2, quantities["line-1"]);
    }

    private static string FindRoot() { var path = AppContext.BaseDirectory; while (!File.Exists(Path.Combine(path, "MarketplaceHub.sln"))) path = Directory.GetParent(path)?.FullName ?? throw new InvalidOperationException("Root not found"); return path; }
}
