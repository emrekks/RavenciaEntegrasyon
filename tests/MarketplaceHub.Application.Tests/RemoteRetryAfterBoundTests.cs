using MarketplaceHub.Infrastructure.Adapters.Hepsiburada;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class RemoteRetryAfterBoundTests
{
    [Theory]
    [InlineData(AdapterErrorClass.RateLimit)]
    [InlineData(AdapterErrorClass.TransientNetwork)]
    [InlineData(AdapterErrorClass.Remote5xx)]
    [InlineData(AdapterErrorClass.Authentication)]
    public void AFarAwayRemoteWaitIsHeldToAQuarterHour(AdapterErrorClass errorClass)
    {
        var result = JobExecutionResult.FromAdapterError(new(errorClass, "REMOTE", "bekle", 429, TimeSpan.FromDays(365 * 55), null));

        Assert.Equal(TimeSpan.FromMinutes(15), result.RetryAfter);
    }

    [Fact]
    public void AShortRemoteWaitIsKeptAsIs()
    {
        var result = JobExecutionResult.FromAdapterError(new(AdapterErrorClass.RateLimit, "REMOTE", "bekle", 429, TimeSpan.FromSeconds(40), null));

        Assert.Equal(TimeSpan.FromSeconds(40), result.RetryAfter);
        Assert.True(result.BackOffConnection);
    }

    [Fact]
    public void ANegativeRemoteWaitFallsBackToTheDefault()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), JobExecutionResult.FromAdapterError(new(AdapterErrorClass.RateLimit, "REMOTE", "bekle", 429, TimeSpan.FromSeconds(-5), null)).RetryAfter);
        Assert.Null(JobExecutionResult.FromAdapterError(new(AdapterErrorClass.Remote5xx, "REMOTE", "hata", 503, TimeSpan.FromSeconds(-5), null)).RetryAfter);
    }

    [Fact]
    public void HepsiburadaResetPastOneBillionIsReadAsUnixTime()
    {
        var now = new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

        Assert.Equal(TimeSpan.FromSeconds(42), HepsiburadaHttpClient.ResetWait(now.AddSeconds(42).ToUnixTimeSeconds(), now));
        Assert.Equal(TimeSpan.Zero, HepsiburadaHttpClient.ResetWait(now.AddSeconds(-10).ToUnixTimeSeconds(), now));
        Assert.Equal(TimeSpan.FromSeconds(12), HepsiburadaHttpClient.ResetWait(12, now));
        Assert.Equal(TimeSpan.Zero, HepsiburadaHttpClient.ResetWait(-3, now));
    }

    [Fact]
    public void HepsiburadaRateLimitHeaderWithAnEpochResetWaitsUntilThatInstant()
    {
        var time = new FixedClock(new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero));
        var adapter = new HepsiburadaHttpClient(null!, null!, Options.Create(new HepsiburadaOptions()), time, new ConfigurationBuilder().Build(), NullLogger<HepsiburadaHttpClient>.Instance);
        using var response = new HttpResponseMessage(System.Net.HttpStatusCode.TooManyRequests);
        response.Headers.TryAddWithoutValidation("X-RateLimit-Reset", time.GetUtcNow().AddSeconds(30).ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture));

        var rate = adapter.RateLimit(response);

        Assert.Equal(TimeSpan.FromSeconds(30), rate!.RetryAfter);
        Assert.Equal(time.GetUtcNow().AddSeconds(30), rate.ResetAt);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
