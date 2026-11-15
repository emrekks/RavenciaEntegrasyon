using MarketplaceHub.Infrastructure.Adapters;
using MarketplaceHub.Infrastructure.Adapters.Shopify;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class SingleFlightTokenCacheTests
{
    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed record Entry(string Value, DateTimeOffset ExpiresAt);

    private static readonly DateTimeOffset Start = new(2026, 11, 15, 10, 0, 0, TimeSpan.Zero);

    private static SingleFlightTokenCache<Guid, Entry> Cache(Clock clock) => new(clock, (entry, now) => entry.ExpiresAt - TimeSpan.FromMinutes(5) > now);

    private static Task<string?> Get(SingleFlightTokenCache<Guid, Entry> cache, Guid key, Func<CancellationToken, Task<string?>> issue, Clock clock) =>
        cache.GetAsync(key, issue, (value, now) => value is null ? null : new Entry(value, now.AddHours(1)), entry => entry.Value, CancellationToken.None);

    [Fact]
    public async Task ConcurrentCallersShareOneRequest()
    {
        var clock = new Clock(Start);
        var cache = Cache(clock);
        var key = Guid.NewGuid();
        var calls = 0;
        var release = new TaskCompletionSource();
        async Task<string?> Issue(CancellationToken _) { Interlocked.Increment(ref calls); await release.Task; return "token"; }

        var callers = Enumerable.Range(0, 10).Select(_ => Get(cache, key, Issue, clock)).ToArray();
        release.SetResult();
        var results = await Task.WhenAll(callers);

        Assert.Equal(1, calls);
        Assert.All(results, value => Assert.Equal("token", value));
    }

    [Fact]
    public async Task AFailureIsNotKeptAndForgetAsksAgain()
    {
        var clock = new Clock(Start);
        var cache = Cache(clock);
        var key = Guid.NewGuid();
        var calls = 0;
        Assert.Null(await Get(cache, key, _ => { calls++; return Task.FromResult<string?>(null); }, clock));
        Assert.Equal("a", await Get(cache, key, _ => { calls++; return Task.FromResult<string?>("a"); }, clock));
        Assert.Equal("a", await Get(cache, key, _ => { calls++; return Task.FromResult<string?>("b"); }, clock));
        Assert.Equal(2, calls);

        cache.Forget(key);
        Assert.Equal("c", await Get(cache, key, _ => { calls++; return Task.FromResult<string?>("c"); }, clock));
        cache.ForgetWhere(candidate => candidate == key);
        Assert.Equal("d", await Get(cache, key, _ => { calls++; return Task.FromResult<string?>("d"); }, clock));
        Assert.Equal(4, calls);
    }

    [Fact]
    public async Task ATokenIsRenewedInsideItsMargin()
    {
        var clock = new Clock(Start);
        var cache = Cache(clock);
        var key = Guid.NewGuid();
        Assert.Equal("a", await Get(cache, key, _ => Task.FromResult<string?>("a"), clock));
        clock.Now = Start.AddMinutes(54);
        Assert.Equal("a", await Get(cache, key, _ => Task.FromResult<string?>("b"), clock));
        clock.Now = Start.AddMinutes(55);
        Assert.Equal("c", await Get(cache, key, _ => Task.FromResult<string?>("c"), clock));
    }

    // ADAPTOR-DIGER-2: five jobs starting together on a client-credentials Shopify connection asked for five tokens.
    [Fact]
    public async Task ShopifyConcurrentCallersShareOneTokenRequest()
    {
        var key = (Guid.NewGuid(), Guid.NewGuid());
        var calls = 0;
        var release = new TaskCompletionSource();
        async Task<ShopifyAuthenticationHandler.CachedToken?> Request(CancellationToken _)
        {
            Interlocked.Increment(ref calls);
            await release.Task;
            return new("shpat", DateTimeOffset.UtcNow.AddHours(24));
        }

        var callers = Enumerable.Range(0, 5).Select(_ => ShopifyAuthenticationHandler.CachedAsync(key, Request, CancellationToken.None)).ToArray();
        release.SetResult();
        var tokens = await Task.WhenAll(callers);

        Assert.Equal(1, calls);
        Assert.All(tokens, token => Assert.Equal("shpat", token!.Token));
        ShopifyAuthenticationHandler.Forget(key.Item1);
        Assert.Equal("next", (await ShopifyAuthenticationHandler.CachedAsync(key, _ => Task.FromResult<ShopifyAuthenticationHandler.CachedToken?>(new("next", DateTimeOffset.UtcNow.AddHours(24))), CancellationToken.None))!.Token);
    }

    [Fact]
    public async Task ShopifyKeepsATokenUntilAMinuteBeforeItExpires()
    {
        var key = (Guid.NewGuid(), Guid.NewGuid());
        await ShopifyAuthenticationHandler.CachedAsync(key, _ => Task.FromResult<ShopifyAuthenticationHandler.CachedToken?>(new("short", DateTimeOffset.UtcNow.AddSeconds(50))), CancellationToken.None);
        var next = await ShopifyAuthenticationHandler.CachedAsync(key, _ => Task.FromResult<ShopifyAuthenticationHandler.CachedToken?>(new("long", DateTimeOffset.UtcNow.AddHours(1))), CancellationToken.None);
        Assert.Equal("long", next!.Token);
        var kept = await ShopifyAuthenticationHandler.CachedAsync(key, _ => Task.FromResult<ShopifyAuthenticationHandler.CachedToken?>(new("other", DateTimeOffset.UtcNow.AddHours(1))), CancellationToken.None);
        Assert.Equal("long", kept!.Token);
    }
}
