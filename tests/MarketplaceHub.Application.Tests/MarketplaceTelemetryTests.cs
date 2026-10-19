using System.Diagnostics.Metrics;
using System.Net;
using MarketplaceHub.Infrastructure.Telemetry;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class MarketplaceTelemetryTests
{
    [Fact]
    public async Task EachMarketplaceRequestIsCountedWithItsClientAndStatus()
    {
        var seen = new List<(long Value, Dictionary<string, object?> Tags)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == MarketplaceHubTelemetry.Name && instrument.Name == "marketplacehub.marketplace.requests") meterListener.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            var copy = new Dictionary<string, object?>();
            foreach (var tag in tags) copy[tag.Key] = tag.Value;
            lock (seen) seen.Add((value, copy));
        });
        listener.Start();

        using var handler = new MarketplaceRequestMetricsHandler("TestMarket") { InnerHandler = new StatusHandler(HttpStatusCode.TooManyRequests) };
        using var client = new HttpClient(handler);
        using var response = await client.GetAsync("https://unit.test/orders");

        var measurement = Assert.Single(seen, entry => Equals(entry.Tags["client"], "TestMarket"));
        Assert.Equal(1, measurement.Value);
        Assert.Equal("429", measurement.Tags["status"]);
        Assert.Equal("GET", measurement.Tags["method"]);
    }

    private sealed class StatusHandler(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status));
    }
}
