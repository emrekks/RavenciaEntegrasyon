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

    private static Func<CancellationToken, Task<ShopifyTokenCache.IssuedToken?>> Issued(string token, TimeSpan lifetime) =>
        _ => Task.FromResult<ShopifyTokenCache.IssuedToken?>(new(token, lifetime));

    // ADAPTOR-DIGER-2: five jobs starting together on a client-credentials Shopify connection asked for five tokens.
    [Fact]
    public async Task ShopifyConcurrentCallersShareOneTokenRequest()
    {
        var cache = new ShopifyTokenCache(new Clock(Start));
        var key = (Guid.NewGuid(), Guid.NewGuid());
        var calls = 0;
        var release = new TaskCompletionSource();
        async Task<ShopifyTokenCache.IssuedToken?> Request(CancellationToken _)
        {
            Interlocked.Increment(ref calls);
            await release.Task;
            return new("shpat", TimeSpan.FromHours(24));
        }

        var callers = Enumerable.Range(0, 5).Select(_ => cache.GetAsync(key, Request, CancellationToken.None)).ToArray();
        release.SetResult();
        var tokens = await Task.WhenAll(callers);

        Assert.Equal(1, calls);
        Assert.All(tokens, token => Assert.Equal("shpat", token!.Token));
        cache.Forget(key.Item1);
        Assert.Equal("next", (await cache.GetAsync(key, Issued("next", TimeSpan.FromHours(24)), CancellationToken.None))!.Token);
    }

    // ADAPTOR-DIGER-2 step 4: the expiry is counted on the injected clock from the answer's arrival, and the token is
    // renewed from a minute before it.
    [Fact]
    public async Task ShopifyKeepsATokenUntilAMinuteBeforeItExpiresOnTheInjectedClock()
    {
        var clock = new Clock(Start);
        var cache = new ShopifyTokenCache(clock);
        var key = (Guid.NewGuid(), Guid.NewGuid());

        var first = await cache.GetAsync(key, Issued("first", TimeSpan.FromHours(1)), CancellationToken.None);
        Assert.Equal(Start.AddHours(1), first!.ExpiresAt);

        clock.Now = Start.AddMinutes(58).AddSeconds(59);
        Assert.Equal("first", (await cache.GetAsync(key, Issued("early", TimeSpan.FromHours(1)), CancellationToken.None))!.Token);

        clock.Now = Start.AddMinutes(59);
        var renewed = await cache.GetAsync(key, Issued("second", TimeSpan.FromHours(1)), CancellationToken.None);
        Assert.Equal("second", renewed!.Token);
        Assert.Equal(Start.AddMinutes(119), renewed.ExpiresAt);

        // A token whose lifetime is within the renewal minute is never fresh, and a failed request is not kept.
        var otherKey = (Guid.NewGuid(), Guid.NewGuid());
        await cache.GetAsync(otherKey, Issued("short", TimeSpan.FromSeconds(50)), CancellationToken.None);
        Assert.Null(await cache.GetAsync(otherKey, _ => Task.FromResult<ShopifyTokenCache.IssuedToken?>(null), CancellationToken.None));
        Assert.Equal("long", (await cache.GetAsync(otherKey, Issued("long", TimeSpan.FromHours(1)), CancellationToken.None))!.Token);
    }
}

