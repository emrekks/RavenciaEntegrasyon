using System.Net;
using System.Net.Http.Headers;
using MarketplaceHub.Infrastructure.Adapters.Hepsiburada;
using MarketplaceHub.Infrastructure.Adapters.Http;
using MarketplaceHub.Infrastructure.Adapters.Trendyol;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class RetryAfterHeaderTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void DelayReadsSecondsAndFutureDatesOnly()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), RetryAfterHeader.Delay(new RetryConditionHeaderValue(TimeSpan.FromSeconds(30)), Now));
        Assert.Equal(TimeSpan.FromSeconds(90), RetryAfterHeader.Delay(new RetryConditionHeaderValue(Now.AddSeconds(90)), Now));
        Assert.Null(RetryAfterHeader.Delay(new RetryConditionHeaderValue(Now.AddSeconds(-5)), Now));
        Assert.Null(RetryAfterHeader.Delay(new RetryConditionHeaderValue(TimeSpan.Zero), Now));
        Assert.Null(RetryAfterHeader.Delay(null, Now));
        Assert.Equal(RetryAfterHeader.Delay(new RetryConditionHeaderValue(Now.AddSeconds(90)), Now), TrendyolHttpClient.RetryAfterDelay(new RetryConditionHeaderValue(Now.AddSeconds(90)), Now));
    }

    // The reading n11, Pazarama, PTT AVM and Shopify keep (a drift, see RetryAfterHeader.Unchecked).
    [Fact]
    public void UncheckedKeepsZeroAndPastDates()
    {
        Assert.Equal(TimeSpan.Zero, RetryAfterHeader.Unchecked(new RetryConditionHeaderValue(TimeSpan.Zero), Now));
        Assert.Equal(TimeSpan.FromSeconds(-5), RetryAfterHeader.Unchecked(new RetryConditionHeaderValue(Now.AddSeconds(-5)), Now));
        Assert.Equal(TimeSpan.FromSeconds(30), RetryAfterHeader.Unchecked(new RetryConditionHeaderValue(TimeSpan.FromSeconds(30)), Now));
        Assert.Null(RetryAfterHeader.Unchecked(null, Now));
    }

    // ADAPTOR-TY-HB-7: a Retry-After date in the past gave Hepsiburada a negative wait.
    [Fact]
    public void HepsiburadaReadsAPastRetryAfterDateAsNoWait()
    {
        var adapter = new HepsiburadaHttpClient(null!, null!, Options.Create(new HepsiburadaOptions()), new FixedClock(Now), new ConfigurationBuilder().Build(), NullLogger<HepsiburadaHttpClient>.Instance);
        using var past = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        past.Headers.RetryAfter = new RetryConditionHeaderValue(Now.AddSeconds(-20));
        using var future = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        future.Headers.RetryAfter = new RetryConditionHeaderValue(Now.AddSeconds(20));

        Assert.Null(adapter.RetryAfter(past));
        Assert.Equal(TimeSpan.FromSeconds(20), adapter.RetryAfter(future));
        Assert.Equal(TimeSpan.FromSeconds(5), HepsiburadaHttpClient.Error(HttpStatusCode.TooManyRequests, adapter.RetryAfter(past) ?? adapter.RateLimit(past)?.RetryAfter, null).RetryAfter);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
