using MarketplaceHub.Application;

namespace MarketplaceHub.EndToEnd.Tests;

public sealed class FakeAdapterScenarioTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-08-02T12:00:00Z");
    private static readonly AdapterContext Context = new(Guid.Parse("11111111-1111-1111-1111-111111111111"), Guid.Parse("22222222-2222-2222-2222-222222222222"), "fake-correlation", "same-effect", Now.AddMinutes(1));

    [Fact]
    public void Fake_adapter_reuses_every_generic_port_without_a_production_dependency()
    {
        var adapter = Adapter(FakeScenario.Success);

        Assert.IsAssignableFrom<IConnectionPort>(adapter);
        Assert.IsAssignableFrom<IReferenceDataPort>(adapter);
        Assert.IsAssignableFrom<IProductPort>(adapter);
        Assert.IsAssignableFrom<IInventoryPricePort>(adapter);
        Assert.IsAssignableFrom<IOrderPort>(adapter);
        Assert.IsAssignableFrom<IReturnPort>(adapter);
    }

    [Fact]
    public void Fake_adapter_has_no_network_auth_or_secret_dependency()
    {
        var root = FindRoot();
        var source = File.ReadAllText(Path.Combine(root, "tests", "MarketplaceHub.EndToEnd.Tests", "DeterministicFakeAdapter.cs"));

        foreach (var forbidden in new[] { "HttpClient", "System.Net", "Authorization", "AuthenticationHeaderValue", "ApiKey", "ApiSecret", "AccessToken", "ClientSecret", "Password" })
            Assert.DoesNotContain(forbidden, source, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Success_and_empty_reads_are_deterministic_and_anonymous()
    {
        var success = Adapter(FakeScenario.Success);
        var first = await success.ListAsync(Context, new(null, 50), new(null), TestContext.Current.CancellationToken);
        var second = await success.ListAsync(Context, new(null, 50), new(null), TestContext.Current.CancellationToken);
        var empty = await Adapter(FakeScenario.Empty).ListAsync(Context, new(null, 50), new(null), TestContext.Current.CancellationToken);

        Assert.True(first.IsSuccess);
        Assert.True(second.IsSuccess);
        Assert.Equal(Assert.Single(first.Value!.Items), Assert.Single(second.Value!.Items));
        Assert.Equal("synthetic-product", Assert.Single(first.Value.Items).ExternalProductId);
        Assert.Empty(empty.Value!.Items);
    }

    [Fact]
    public async Task Writes_are_fail_closed_until_explicitly_enabled_and_replay_has_one_effect()
    {
        var disabled = Adapter(FakeScenario.Success);
        Assert.Equal("FAKE_WRITE_DISABLED", (await disabled.ArchiveAsync(Context, new("synthetic", null), TestContext.Current.CancellationToken)).Error?.Code);

        var enabled = Adapter(FakeScenario.Success, true);
        var first = await enabled.CreateAsync(Context, new(Guid.Empty, "synthetic-hash", "{\"items\":[{\"barcode\":\"0000000000000\"}]}"), TestContext.Current.CancellationToken);
        var replay = await enabled.CreateAsync(Context, new(Guid.Empty, "synthetic-hash", "{\"items\":[{\"barcode\":\"0000000000000\"}]}"), TestContext.Current.CancellationToken);
        Assert.Equal(first.Value, replay.Value);
        Assert.Equal(1, enabled.ExternalEffectCount);
    }

    [Fact]
    public async Task Partial_batch_is_visible_and_is_not_reported_as_full_success()
    {
        var adapter = Adapter(FakeScenario.Partial, true);
        var result = await adapter.PushStockAsync(Context,
        [
            new(Guid.Parse("33333333-3333-3333-3333-333333333333"), "0000000000000", 1, 1),
            new(Guid.Parse("44444444-4444-4444-4444-444444444444"), "0000000000001", 1, 1)
        ], TestContext.Current.CancellationToken);

        Assert.True(result.Value!.IsPartial);
        Assert.Contains(result.Value.Lines, x => x.Succeeded);
        Assert.Contains(result.Value.Lines, x => !x.Succeeded);
    }

    [Theory]
    [InlineData(FakeScenario.Authentication, AdapterErrorClass.Authentication)]
    [InlineData(FakeScenario.RateLimit, AdapterErrorClass.RateLimit)]
    [InlineData(FakeScenario.RemoteError, AdapterErrorClass.Remote5xx)]
    [InlineData(FakeScenario.Timeout, AdapterErrorClass.TransientNetwork)]
    [InlineData(FakeScenario.Validation, AdapterErrorClass.Validation)]
    [InlineData(FakeScenario.ContractViolation, AdapterErrorClass.ContractViolation)]
    public async Task Failure_modes_remain_distinct(FakeScenario scenario, AdapterErrorClass expected)
    {
        var result = await Adapter(scenario).TestAsync(Context, TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal(expected, result.Error?.Class);
    }

    private static DeterministicFakeAdapter Adapter(FakeScenario scenario, bool writes = false) => new(scenario, new FixedTimeProvider(Now), writes);

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }

    private static string FindRoot()
    {
        var path = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(path, "MarketplaceHub.sln")))
            path = Directory.GetParent(path)?.FullName ?? throw new InvalidOperationException("Root not found");
        return path;
    }
}
