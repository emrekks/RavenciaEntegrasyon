using MarketplaceHub.Worker;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class JobWakeSignalTests
{
    [Fact]
    public async Task Pulse_WakesEveryLoopThatTookTheSignalBeforeIt()
    {
        var signal = new JobWakeSignal();
        var first = signal.Current;
        var second = signal.Current;

        signal.Pulse();

        Assert.True(await JobWakeSignal.WaitAsync(first, TimeSpan.FromSeconds(5), CancellationToken.None));
        Assert.True(await JobWakeSignal.WaitAsync(second, TimeSpan.FromSeconds(5), CancellationToken.None));
    }

    [Fact]
    public async Task WaitAsync_FallsBackToTheDelayWithoutAPulse()
    {
        var signal = new JobWakeSignal();
        signal.Pulse();
        // A signal taken after the pulse is a fresh one and must wait for the next pulse.
        var afterPulse = signal.Current;

        Assert.False(await JobWakeSignal.WaitAsync(afterPulse, TimeSpan.FromMilliseconds(20), CancellationToken.None));
    }

    [Fact]
    public async Task WaitAsync_StopsOnShutdown()
    {
        using var shutdown = new CancellationTokenSource();
        var wait = JobWakeSignal.WaitAsync(new JobWakeSignal().Current, TimeSpan.FromMinutes(1), shutdown.Token);

        shutdown.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
    }
}
