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

    // ADAPTOR-TY-HB-7 (owner, 16 Nov 2026): n11, Pazarama, PTT AVM and Shopify read the header with Delay too. A
    // Retry-After of 0 or a past date used to give their 429 a zero or negative wait (the job then waited 30 s); now it
    // names no wait and the adapter's own 429 default applies. A future wait is kept as before.
    [Theory]
    [InlineData("N11", 60)]
    [InlineData("PAZARAMA", 60)]
    [InlineData("PTTAVM", 60)]
    [InlineData("SHOPIFY", 5)]
    public void TheFourAdaptersReadAZeroOrPastRetryAfterAsTheirDefaultWait(string adapter, int defaultSeconds)
    {
        AdapterError Error(TimeSpan? wait) => adapter switch
        {
            "N11" => MarketplaceHub.Infrastructure.Adapters.N11.N11HttpClient.Error(HttpStatusCode.TooManyRequests, "", wait),
            "PAZARAMA" => MarketplaceHub.Infrastructure.Adapters.Pazarama.PazaramaHttpClient.Error(HttpStatusCode.TooManyRequests, "", wait),
            "PTTAVM" => MarketplaceHub.Infrastructure.Adapters.PttAvm.PttAvmHttpClient.SoapResponse(HttpStatusCode.TooManyRequests, "", "Test", wait).Error!,
            _ => MarketplaceHub.Infrastructure.Adapters.Shopify.ShopifyHttpClient.MapHttpError(HttpStatusCode.TooManyRequests, "", wait)
        };

        Assert.Equal(TimeSpan.FromSeconds(defaultSeconds), Error(RetryAfterHeader.Delay(new RetryConditionHeaderValue(TimeSpan.Zero), Now)).RetryAfter);
        Assert.Equal(TimeSpan.FromSeconds(defaultSeconds), Error(RetryAfterHeader.Delay(new RetryConditionHeaderValue(Now.AddSeconds(-5)), Now)).RetryAfter);
        Assert.Equal(TimeSpan.FromSeconds(30), Error(RetryAfterHeader.Delay(new RetryConditionHeaderValue(TimeSpan.FromSeconds(30)), Now)).RetryAfter);
        // Downstream the job waits that long (the quota class keeps its attempt), never a tight loop.
        var job = JobExecutionResult.FromAdapterError(Error(RetryAfterHeader.Delay(new RetryConditionHeaderValue(TimeSpan.Zero), Now)));
        Assert.Equal(TimeSpan.FromSeconds(defaultSeconds), job.RetryAfter);
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
