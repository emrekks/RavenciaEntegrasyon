using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using MarketplaceHub.Infrastructure.Telemetry;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// KUYRUK-5: every maintenance pass is timed and a failed one counted, under the loop's name.
public sealed class MaintenancePassMetricsTests
{
    [Fact]
    public async Task APassIsTimedAndAFailedPassCountedUnderItsLoop()
    {
        var loop = $"test-loop-{Guid.NewGuid():N}";
        var durations = new ConcurrentQueue<string>();
        var failures = new ConcurrentQueue<long>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            // By name: the instruments may be published while the telemetry class is still initializing.
            if (instrument.Meter.Name == MarketplaceHubTelemetry.Name && instrument.Name is "marketplacehub.maintenance.pass.duration" or "marketplacehub.maintenance.pass.failures")
                meterListener.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<double>((instrument, _, tags, _) =>
        {
            var values = tags.ToArray().ToDictionary(tag => tag.Key, tag => tag.Value?.ToString());
            if (values.GetValueOrDefault("loop") == loop) durations.Enqueue(values["outcome"]!);
        });
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            if (tags.ToArray().Any(tag => tag.Key == "loop" && Equals(tag.Value, loop))) failures.Enqueue(value);
        });
        listener.Start();

        using var stopping = new CancellationTokenSource();
        var passes = 0;
        var run = MarketplaceHub.Worker.Worker.RunPeriodicAsync(TimeSpan.Zero, TimeSpan.FromMilliseconds(20), _ =>
        {
            if (Interlocked.Increment(ref passes) == 1) throw new TaskCanceledException("a library timeout");
            if (passes >= 2) stopping.Cancel();
            return Task.CompletedTask;
        }, _ => { }, stopping.Token, loop);
        await run.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(["failed", "succeeded"], durations.Take(2));
        Assert.Equal([1L], failures);
    }
}
