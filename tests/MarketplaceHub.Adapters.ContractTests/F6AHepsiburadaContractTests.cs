using MarketplaceHub.Application;
using MarketplaceHub.Infrastructure.Adapters.Hepsiburada;

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

    private static string FindRoot() { var path = AppContext.BaseDirectory; while (!File.Exists(Path.Combine(path, "MarketplaceHub.sln"))) path = Directory.GetParent(path)?.FullName ?? throw new InvalidOperationException("Root not found"); return path; }
}
