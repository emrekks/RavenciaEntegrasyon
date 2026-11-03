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

    [Fact]
    public void Idle_wait_doubles_up_to_fifteen_seconds_and_a_wake_starts_again_at_one()
    {
        var waits = new List<TimeSpan>();
        var delay = JobIdleWait.First;
        for (var i = 0; i < 6; i++)
        {
            waits.Add(delay);
            delay = JobIdleWait.Next(delay, woken: false);
        }

        Assert.Equal(new double[] { 1, 2, 4, 8, 15, 15 }, waits.Select(x => x.TotalSeconds));
        Assert.Equal(JobIdleWait.First, JobIdleWait.Next(JobIdleWait.Maximum, woken: true));
    }

    // A retry or deferred job whose time comes is woken for at its AvailableAt, not after the lanes' idle wait.
    [Theory]
    [InlineData(null, 15_000)]
    [InlineData(-3_000, 50)]
    [InlineData(10, 50)]
    [InlineData(2_500, 2_500)]
    [InlineData(14_000, 14_000)]
    [InlineData(600_000, 15_000)]
    public void Due_wake_sleeps_until_the_next_due_job_within_the_idle_bound(int? dueInMilliseconds, int expectedMilliseconds)
    {
        var now = new DateTimeOffset(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);
        DateTimeOffset? due = dueInMilliseconds is { } ms ? now.AddMilliseconds(ms) : null;

        Assert.Equal(TimeSpan.FromMilliseconds(expectedMilliseconds), JobIdleWait.UntilDue(due, now));
    }
}
