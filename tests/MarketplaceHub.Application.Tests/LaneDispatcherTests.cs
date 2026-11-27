using MarketplaceHub.Application.Tests.QueueReplay;
using MarketplaceHub.Infrastructure.Jobs;
using MarketplaceHub.Worker;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// Queue phase 2 (27 Nov 2026): the dispatcher's loop (LaneDispatcher) on a simulated clock: a failing lease does not spin,
// a job's failure frees its slot, the shutdown waits for a running job, and an idle dispatcher reads only the census.
public sealed class LaneDispatcherTests
{
    private static readonly WorkerLane Test = new("test", 1, null, null, ["TEST_JOB"], []);
    private static readonly WorkerLane Other = new("other", 1, null, null, ["OTHER_JOB"], []);

    private sealed class Probe
    {
        public int Leases;
        public int Censuses;
        public int Runs;
        public int Ended;
        public Func<WorkerLane, LeasedJob?> Answer = _ => null;
        public Exception? LeaseFailure;
        public Func<LeasedJob, Task> Run = _ => Task.CompletedTask;
        public Func<IReadOnlyCollection<(string JobType, int Priority)>> Rows = () => [];
        public List<string> Failures { get; } = [];
    }

    private static (LaneDispatcher Dispatcher, ReplayClock Clock, Probe Probe) Create()
    {
        var clock = new ReplayClock(ReplayHarness.Origin);
        var probe = new Probe();
        var dispatcher = new LaneDispatcher([new LaneDispatcher.Lane(Test, 1, 0), new LaneDispatcher.Lane(Other, 0, 1)], clock,
            async (lane, _, _) =>
            {
                Interlocked.Increment(ref probe.Leases);
                await clock.Delay(TimeSpan.FromMilliseconds(3));
                if (probe.LeaseFailure is { } failure) throw failure;
                return probe.Answer(lane);
            },
            async _ =>
            {
                Interlocked.Increment(ref probe.Censuses);
                await clock.Delay(TimeSpan.FromMilliseconds(3));
                return probe.Rows();
            },
            async (job, _) =>
            {
                Interlocked.Increment(ref probe.Runs);
                try { await probe.Run(job); }
                finally { Interlocked.Increment(ref probe.Ended); }
            },
            (what, _) => { lock (probe.Failures) probe.Failures.Add(what); },
            TimeSpan.FromSeconds(15),
            clock.WaitAsync);
        return (dispatcher, clock, probe);
    }

    private static async Task RunForAsync(ReplayClock clock, Func<int> actors, TimeSpan length)
    {
        await clock.SettleAsync(actors);
        var until = clock.UtcNow + length;
        while (clock.UtcNow < until)
        {
            if (!clock.AdvanceToNext()) break;
            await clock.SettleAsync(actors);
        }
    }

    private static LeasedJob Job(string type) => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), type, "{}", "test", "token");

    [Fact]
    public async Task AnIdleDispatcherReadsOnlyTheCensusOncePerSafetyTick()
    {
        var (dispatcher, clock, probe) = Create();
        using var stopping = new CancellationTokenSource();
        var loop = Task.Run(() => dispatcher.RunAsync(stopping.Token));
        try
        {
            await RunForAsync(clock, () => 1, TimeSpan.FromMinutes(5));
            // At start each lane is read once; then one census every 15 s: 20 in five minutes, and no lease.
            Assert.Equal(2, probe.Leases);
            Assert.InRange(probe.Censuses, 20, 22);
        }
        finally
        {
            stopping.Cancel();
            clock.ReleaseAll();
            await loop;
        }
    }

    [Fact]
    public async Task AFailingLeaseBacksOffInsteadOfSpinning()
    {
        var (dispatcher, clock, probe) = Create();
        probe.LeaseFailure = new TimeoutException("database down");
        using var stopping = new CancellationTokenSource();
        var loop = Task.Run(() => dispatcher.RunAsync(stopping.Token));
        try
        {
            dispatcher.Notify("TEST_JOB", 3);
            await RunForAsync(clock, () => 1, TimeSpan.FromMinutes(1));
            // Waits of 1, 2, 4, 8, 15, 15 ... s per lane: at most a dozen tries a minute for each of the two lanes.
            Assert.InRange(probe.Leases, 2, 24);
            Assert.NotEmpty(probe.Failures);
            probe.LeaseFailure = null;
            var job = Job("TEST_JOB");
            var given = 0;
            probe.Answer = lane => lane == Test && Interlocked.Exchange(ref given, 1) == 0 ? job : null;
            probe.Rows = () => Volatile.Read(ref given) == 0 ? [("TEST_JOB", 3)] : [];
            await RunForAsync(clock, () => 1 + dispatcher.RunningTasks, TimeSpan.FromSeconds(20));
            Assert.Equal(1, probe.Runs);
        }
        finally
        {
            stopping.Cancel();
            clock.ReleaseAll();
            await loop;
        }
    }

    [Fact]
    public async Task AJobThatThrowsFreesItsSlot()
    {
        var (dispatcher, clock, probe) = Create();
        var jobs = new Queue<LeasedJob>([Job("TEST_JOB"), Job("TEST_JOB")]);
        probe.Answer = lane => lane == Test && jobs.Count > 0 ? jobs.Dequeue() : null;
        probe.Run = _ => throw new InvalidOperationException("broken job");
        using var stopping = new CancellationTokenSource();
        var loop = Task.Run(() => dispatcher.RunAsync(stopping.Token));
        try
        {
            await RunForAsync(clock, () => 1 + dispatcher.RunningTasks, TimeSpan.FromSeconds(5));
            Assert.Equal(2, probe.Runs);
            Assert.Equal(0, dispatcher.Read(core => core.RunningTotal));
            Assert.Equal(2, probe.Failures.Count(what => what.StartsWith("job ", StringComparison.Ordinal)));
        }
        finally
        {
            stopping.Cancel();
            clock.ReleaseAll();
            await loop;
        }
    }

    [Fact]
    public async Task TheShutdownWaitsForARunningJob()
    {
        var (dispatcher, clock, probe) = Create();
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var given = 0;
        probe.Answer = lane => lane == Test && Interlocked.Exchange(ref given, 1) == 0 ? Job("TEST_JOB") : null;
        probe.Run = _ => finish.Task;
        using var stopping = new CancellationTokenSource();
        var loop = Task.Run(() => dispatcher.RunAsync(stopping.Token));
        var until = DateTime.UtcNow.AddSeconds(10);
        while (probe.Runs == 0 && DateTime.UtcNow < until)
        {
            clock.AdvanceToNext();
            await Task.Delay(5);
        }
        Assert.Equal(1, probe.Runs);
        stopping.Cancel();
        clock.ReleaseAll();
        await Task.Delay(100);
        Assert.False(loop.IsCompleted, "the dispatcher ended while its job still ran");
        finish.SetResult();
        await loop.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, probe.Ended);
    }

    [Fact]
    public async Task ANotificationLeasesTheNamedLaneAtOnce()
    {
        var (dispatcher, clock, probe) = Create();
        using var stopping = new CancellationTokenSource();
        var loop = Task.Run(() => dispatcher.RunAsync(stopping.Token));
        try
        {
            await RunForAsync(clock, () => 1, TimeSpan.FromSeconds(1));
            var job = Job("OTHER_JOB");
            var given = 0;
            probe.Answer = lane => lane == Other && Interlocked.Exchange(ref given, 1) == 0 ? job : null;
            var before = clock.UtcNow;
            dispatcher.Route(JobWakeRouter.Payload("OTHER_JOB", 3));
            await clock.SettleAsync(() => 1 + dispatcher.RunningTasks);
            while (probe.Runs == 0 && clock.AdvanceToNext()) await clock.SettleAsync(() => 1 + dispatcher.RunningTasks);
            Assert.Equal(1, probe.Runs);
            // One lease of 3 ms: the job starts at once, not at the next tick.
            Assert.True(clock.UtcNow - before < TimeSpan.FromMilliseconds(50), $"{clock.UtcNow - before}");
        }
        finally
        {
            stopping.Cancel();
            clock.ReleaseAll();
            await loop;
        }
    }
}
