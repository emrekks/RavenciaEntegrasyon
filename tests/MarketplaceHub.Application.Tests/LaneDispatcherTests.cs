using MarketplaceHub.Application.Tests.QueueReplay;
using MarketplaceHub.Infrastructure.Jobs;
using MarketplaceHub.Worker;
using Xunit;
using Xunit.Abstractions;

namespace MarketplaceHub.Application.Tests;

// Queue phase 2 (27 Nov 2026): the dispatcher's loop (LaneDispatcher) on a simulated clock and one thread (ReplayPump), so every
// run gives the same result and nothing depends on the machine's speed. Includes the review's probes P1-P3 as permanent tests:
// a lane held by a store costs nothing on other stores' job ends, a hung lease is cancelled after its timeout, a silent unblock or
// a silent due job starts within one safety tick; and a turn that throws does not stop the loop.
public sealed class LaneDispatcherTests(ITestOutputHelper output)
{
    private static readonly WorkerLane Fast = new("fast", 1, null, null, ["FAST_JOB"], []);
    private static readonly WorkerLane Held = new("held", 1, null, null, ["HELD_JOB"], []);
    private static readonly WorkerLane HeldB = new("held-b", 1, null, null, ["HELD_B_JOB"], []);
    private static readonly WorkerLane HeldC = new("held-c", 1, null, null, ["HELD_C_JOB"], []);
    private static readonly Guid StoreA = new("00000000-0000-0000-0000-0000000000aa");
    private static readonly Guid StoreB = new("00000000-0000-0000-0000-0000000000bb");

    private sealed class Probe
    {
        public int Leases;
        public int EmptyLeases;
        public int Censuses;
        public int Runs;
        public int Ended;
        public Func<WorkerLane, Task<LeasedJob?>>? Lease;
        public Func<IReadOnlyCollection<LaneDispatcher.CensusRow>> Rows = () => [];
        public Func<LeasedJob, Task>? Run;
        public Func<Task, TimeSpan, CancellationToken, Task<bool>>? Wait;
        public List<string> Failures { get; } = [];
    }

    // Runs body on one thread with the pump and a simulated clock.
    private static void Pumped(Action<ReplayPump, ReplayClock> body)
    {
        var pump = new ReplayPump();
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(pump);
        try { body(pump, new ReplayClock(ReplayHarness.Origin, pump)); }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }

    private static void RunFor(ReplayPump pump, ReplayClock clock, TimeSpan length)
    {
        pump.Drain();
        var until = clock.UtcNow + length;
        while (clock.UtcNow < until && clock.AdvanceToNext()) pump.Drain();
    }

    private static LaneDispatcher Create(ReplayClock clock, Probe probe, params (WorkerLane Lane, int Reserved, int Share)[] lanes) =>
        new([.. lanes.Select(lane => new LaneDispatcher.Lane(lane.Lane, lane.Reserved, lane.Share))], clock,
            async (lane, _, _) =>
            {
                probe.Leases++;
                var job = probe.Lease is null ? null : await probe.Lease(lane.Definition);
                if (probe.Lease is null) await clock.Delay(TimeSpan.FromMilliseconds(10));
                if (job is null) probe.EmptyLeases++;
                return job;
            },
            async _ =>
            {
                probe.Censuses++;
                await clock.Delay(TimeSpan.FromMilliseconds(10));
                return probe.Rows();
            },
            async (job, _) =>
            {
                probe.Runs++;
                try { await (probe.Run?.Invoke(job) ?? Task.CompletedTask); }
                finally { probe.Ended++; }
            },
            (what, _) => probe.Failures.Add(what),
            new LaneDispatcher.Options(TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(10), [], TimeSpan.FromSeconds(5)),
            (wake, delay, token) => probe.Wait?.Invoke(wake, delay, token) ?? clock.WaitAsync(wake, delay, token));

    private static LeasedJob Job(string type, Guid? store = null) => new(Guid.NewGuid(), Guid.NewGuid(), store ?? Guid.NewGuid(), type, "{}", "test", "token");

    [Fact]
    public void AnIdleDispatcherReadsOnlyTheCensusOncePerSafetyTick() => Pumped((pump, clock) =>
    {
        var probe = new Probe();
        var dispatcher = Create(clock, probe, (Fast, 1, 0), (Held, 0, 1));
        using var stopping = new CancellationTokenSource();
        _ = dispatcher.RunAsync(stopping.Token);
        RunFor(pump, clock, TimeSpan.FromMinutes(5));
        // At start each lane is read once; then one census every 15 s: 20 in five minutes, and no lease.
        Assert.Equal(2, probe.Leases);
        Assert.InRange(probe.Censuses, 20, 22);
        stopping.Cancel();
        pump.Drain();
    });

    [Fact]
    public void AFailingLeaseBacksOffInsteadOfSpinning() => Pumped((pump, clock) =>
    {
        var probe = new Probe();
        var failing = true;
        var given = false;
        probe.Lease = async lane =>
        {
            await clock.Delay(TimeSpan.FromMilliseconds(10));
            if (failing) throw new TimeoutException("database down");
            if (lane == Fast && !given) { given = true; return Job("FAST_JOB"); }
            return null;
        };
        probe.Rows = () => given ? [] : [new LaneDispatcher.CensusRow("FAST_JOB", 3, null, null)];
        var dispatcher = Create(clock, probe, (Fast, 1, 0), (Held, 0, 1));
        using var stopping = new CancellationTokenSource();
        _ = dispatcher.RunAsync(stopping.Token);
        dispatcher.Notify("FAST_JOB", 3);
        RunFor(pump, clock, TimeSpan.FromMinutes(1));
        // Waits of 1, 2, 4, 8, 15, 15 ... s per lane: at most a dozen tries a minute for each of the two lanes.
        Assert.InRange(probe.Leases, 2, 24);
        Assert.NotEmpty(probe.Failures);
        failing = false;
        RunFor(pump, clock, TimeSpan.FromSeconds(20));
        Assert.Equal(1, probe.Runs);
        stopping.Cancel();
        pump.Drain();
    });

    [Fact]
    public void AJobThatThrowsFreesItsSlot() => Pumped((pump, clock) =>
    {
        var probe = new Probe();
        var jobs = new Queue<LeasedJob>([Job("FAST_JOB"), Job("FAST_JOB")]);
        probe.Lease = async lane => { await clock.Delay(TimeSpan.FromMilliseconds(10)); return lane == Fast && jobs.Count > 0 ? jobs.Dequeue() : null; };
        probe.Run = _ => throw new InvalidOperationException("broken job");
        var dispatcher = Create(clock, probe, (Fast, 1, 0), (Held, 0, 1));
        using var stopping = new CancellationTokenSource();
        _ = dispatcher.RunAsync(stopping.Token);
        RunFor(pump, clock, TimeSpan.FromSeconds(5));
        Assert.Equal(2, probe.Runs);
        Assert.Equal(0, dispatcher.Read(core => core.RunningTotal));
        Assert.Equal(2, probe.Failures.Count(what => what.StartsWith("job ", StringComparison.Ordinal)));
        stopping.Cancel();
        pump.Drain();
    });

    [Fact]
    public void TheShutdownWaitsForARunningJob() => Pumped((pump, clock) =>
    {
        var probe = new Probe();
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var given = false;
        probe.Lease = async lane => { await clock.Delay(TimeSpan.FromMilliseconds(10)); if (lane == Fast && !given) { given = true; return Job("FAST_JOB"); } return null; };
        probe.Run = _ => finish.Task;
        var dispatcher = Create(clock, probe, (Fast, 1, 0), (Held, 0, 1));
        using var stopping = new CancellationTokenSource();
        var loop = dispatcher.RunAsync(stopping.Token);
        RunFor(pump, clock, TimeSpan.FromSeconds(1));
        Assert.Equal(1, probe.Runs);
        stopping.Cancel();
        RunFor(pump, clock, TimeSpan.FromSeconds(30));
        Assert.False(loop.IsCompleted, "the dispatcher ended while its job still ran");
        finish.SetResult();
        pump.Drain();
        Assert.True(loop.IsCompleted);
        Assert.Equal(1, probe.Ended);
    });

    [Fact]
    public void ANotificationLeasesTheNamedLaneAtOnce() => Pumped((pump, clock) =>
    {
        var probe = new Probe();
        var given = false;
        DateTimeOffset? started = null;
        probe.Lease = async lane => { await clock.Delay(TimeSpan.FromMilliseconds(3)); if (lane == Held && !given && clock.UtcNow > ReplayHarness.Origin + TimeSpan.FromSeconds(1)) { given = true; return Job("HELD_JOB"); } return null; };
        probe.Run = _ => { started ??= clock.UtcNow; return Task.CompletedTask; };
        var dispatcher = Create(clock, probe, (Fast, 1, 0), (Held, 0, 1));
        using var stopping = new CancellationTokenSource();
        _ = dispatcher.RunAsync(stopping.Token);
        RunFor(pump, clock, TimeSpan.FromSeconds(2));
        var before = clock.UtcNow;
        dispatcher.Route(JobWakeRouter.Payload("HELD_JOB", 3));
        RunFor(pump, clock, TimeSpan.FromSeconds(1));
        Assert.NotNull(started);
        Assert.True(started - before < TimeSpan.FromMilliseconds(50), $"{started - before}");
        stopping.Cancel();
        pump.Drain();
    });

    // P1 (C1 of the review): three lanes whose jobs are held by store B (its lock groups busy, its cap) next to a lane whose short
    // jobs of store A end ten times a second. Before the fix every end made the held lanes try again (546 ends: 549 empty leases,
    // 551 censuses in 60 s). Now they wait for store B: the empty leases per job end stay below a small constant.
    [Fact]
    public void LanesHeldByOneStoreCostNothingOnAnotherStoresJobEnds() => Pumped((pump, clock) =>
    {
        var probe = new Probe();
        var fastRunning = false;
        var heldLeases = 0;
        probe.Lease = async lane =>
        {
            await clock.Delay(TimeSpan.FromMilliseconds(10));
            if (lane != Fast) { heldLeases++; return null; }
            if (fastRunning) return null;
            fastRunning = true;
            return Job("FAST_JOB", StoreA);
        };
        probe.Run = async _ => { await clock.Delay(TimeSpan.FromMilliseconds(90)); fastRunning = false; };
        probe.Rows = () => [new("HELD_JOB", 4, StoreB, StoreB), new("HELD_B_JOB", 4, StoreB, StoreB), new("HELD_C_JOB", 4, StoreB, StoreB), new("FAST_JOB", 4, StoreA, null)];
        var dispatcher = Create(clock, probe, (Fast, 1, 0), (Held, 0, 3), (HeldB, 0, 3), (HeldC, 0, 3));
        using var stopping = new CancellationTokenSource();
        _ = dispatcher.RunAsync(stopping.Token);
        // Keep the fast lane busy: a notification after every end (the trigger's "job:" for its next job).
        RunFor(pump, clock, TimeSpan.FromSeconds(1));
        var endsBefore = probe.Ended;
        var heldBefore = heldLeases;
        var until = clock.UtcNow + TimeSpan.FromSeconds(60);
        while (clock.UtcNow < until && clock.AdvanceToNext())
        {
            pump.Drain();
            if (!fastRunning) dispatcher.Notify("FAST_JOB", 4);
        }
        var ends = probe.Ended - endsBefore;
        var held = heldLeases - heldBefore;
        output.WriteLine($"60 s: {ends} fast job ends, {held} leases of the three held lanes");
        Assert.True(ends >= 100, $"only {ends} job ends");
        // Independent of the job ends: at most each held lane once per safety census (four in 60 s) and once more.
        Assert.True(held <= 3 * 5, $"{held} leases of the held lanes for {ends} job ends of another store");
        stopping.Cancel();
        pump.Drain();
    });

    // The same three lanes held for a reason the census cannot name (a quota hold, a bulkhead): each is tried again on a job end
    // at most once a second.
    [Fact]
    public void LanesHeldForAnUnnamedReasonAreRetriedAtMostOnceASecond() => Pumped((pump, clock) =>
    {
        var probe = new Probe();
        var fastRunning = false;
        var heldLeases = 0;
        probe.Lease = async lane =>
        {
            await clock.Delay(TimeSpan.FromMilliseconds(10));
            if (lane != Fast) { heldLeases++; return null; }
            if (fastRunning) return null;
            fastRunning = true;
            return Job("FAST_JOB", StoreA);
        };
        probe.Run = async _ => { await clock.Delay(TimeSpan.FromMilliseconds(90)); fastRunning = false; };
        probe.Rows = () => [new("HELD_JOB", 4, StoreB, null), new("HELD_B_JOB", 4, StoreB, null), new("HELD_C_JOB", 4, StoreB, null)];
        var dispatcher = Create(clock, probe, (Fast, 1, 0), (Held, 0, 3), (HeldB, 0, 3), (HeldC, 0, 3));
        using var stopping = new CancellationTokenSource();
        _ = dispatcher.RunAsync(stopping.Token);
        RunFor(pump, clock, TimeSpan.FromSeconds(1));
        var heldBefore = heldLeases;
        var until = clock.UtcNow + TimeSpan.FromSeconds(60);
        while (clock.UtcNow < until && clock.AdvanceToNext())
        {
            pump.Drain();
            if (!fastRunning) dispatcher.Notify("FAST_JOB", 4);
        }
        var held = heldLeases - heldBefore;
        output.WriteLine($"60 s: {held} empty leases of the three held lanes");
        Assert.True(held <= 3 * 61, $"{held} empty leases of three held lanes in 60 s");
        stopping.Cancel();
        pump.Drain();
    });

    // P2 (C2 of the review): one lease hangs (a slow database). Before the fix the next lane's notified job waited 30 s behind it;
    // now the lease is cancelled after its timeout (10 s) and its lane backs off.
    [Fact]
    public void AHungLeaseIsCancelledAfterItsTimeout() => Pumped((pump, clock) =>
    {
        var probe = new Probe();
        var hang = true;
        var fastGiven = false;
        DateTimeOffset? fastStarted = null;
        var dispatcher = new LaneDispatcher([new LaneDispatcher.Lane(Fast, 1, 0), new LaneDispatcher.Lane(Held, 0, 3)], clock,
            async (lane, _, token) =>
            {
                if (lane.Definition == Held && hang)
                {
                    hang = false;
                    var never = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    using (token.Register(() => never.TrySetCanceled(token))) await never.Task;
                }
                await clock.Delay(TimeSpan.FromMilliseconds(10));
                if (lane.Definition == Fast && fastGiven && fastStarted is null) return Job("FAST_JOB");
                return null;
            },
            async _ => { await clock.Delay(TimeSpan.FromMilliseconds(10)); return []; },
            (_, _) => { fastStarted ??= clock.UtcNow; return Task.CompletedTask; },
            (what, _) => probe.Failures.Add(what),
            new LaneDispatcher.Options(TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(10), [], TimeSpan.FromSeconds(5)),
            clock.WaitAsync);
        using var stopping = new CancellationTokenSource();
        _ = dispatcher.RunAsync(stopping.Token);
        pump.Drain();
        clock.AdvanceToNext();
        pump.Drain();
        var notifiedAt = clock.UtcNow;
        fastGiven = true;
        dispatcher.Notify("FAST_JOB", 0);
        RunFor(pump, clock, TimeSpan.FromSeconds(30));
        output.WriteLine($"fast job started {(fastStarted - notifiedAt)?.TotalSeconds:0.000} s after its notification");
        Assert.NotNull(fastStarted);
        Assert.True(fastStarted - notifiedAt <= TimeSpan.FromSeconds(10.1), $"{fastStarted - notifiedAt}");
        Assert.Contains(probe.Failures, what => what.Contains("over 10 s", StringComparison.Ordinal));
        stopping.Cancel();
        pump.Drain();
    });

    // P3: (a) a lane whose job opens with no signal (its blocker ended elsewhere, no "freed"); (b) an idle lane whose job becomes
    // due with no due-time wake. Both start within one safety tick.
    [Fact]
    public void ASilentUnblockAndASilentDueJobStartWithinOneSafetyTick() => Pumped((pump, clock) =>
    {
        var probe = new Probe();
        var openAt = ReplayHarness.Origin + TimeSpan.FromSeconds(7.3);
        var dueAt = ReplayHarness.Origin + TimeSpan.FromSeconds(21.1);
        DateTimeOffset? heldStarted = null, fastStarted = null;
        var heldGiven = false;
        var fastGiven = false;
        probe.Lease = async lane =>
        {
            await clock.Delay(TimeSpan.FromMilliseconds(10));
            if (lane == Held) { if (clock.UtcNow >= openAt && !heldGiven) { heldGiven = true; return Job("HELD_JOB"); } return null; }
            if (clock.UtcNow >= dueAt && !fastGiven) { fastGiven = true; return Job("FAST_JOB"); }
            return null;
        };
        probe.Rows = () =>
        {
            var rows = new List<LaneDispatcher.CensusRow>();
            if (!heldGiven) rows.Add(new("HELD_JOB", 4, StoreB, clock.UtcNow < openAt ? StoreB : null));
            if (clock.UtcNow >= dueAt && !fastGiven) rows.Add(new("FAST_JOB", 4, null, null));
            return rows;
        };
        probe.Run = job => { if (job.JobType == "HELD_JOB") heldStarted ??= clock.UtcNow; else fastStarted ??= clock.UtcNow; return clock.Delay(TimeSpan.FromMinutes(5)); };
        var dispatcher = Create(clock, probe, (Fast, 1, 0), (Held, 0, 3));
        using var stopping = new CancellationTokenSource();
        _ = dispatcher.RunAsync(stopping.Token);
        RunFor(pump, clock, TimeSpan.FromSeconds(60));
        output.WriteLine($"opened silently at +7.3 s, started +{(heldStarted - ReplayHarness.Origin)?.TotalSeconds:0.000} s; due silently at +21.1 s, started +{(fastStarted - ReplayHarness.Origin)?.TotalSeconds:0.000} s");
        Assert.NotNull(heldStarted);
        Assert.NotNull(fastStarted);
        Assert.True(heldStarted - openAt <= TimeSpan.FromSeconds(15.1));
        Assert.True(fastStarted - dueAt <= TimeSpan.FromSeconds(15.1));
        stopping.Cancel();
        pump.Drain();
    });

    // C2: a turn that throws (here the wait itself) is logged and the loop goes on: the next notified job still starts.
    [Fact]
    public void ATurnThatThrowsDoesNotStopTheLoop() => Pumped((pump, clock) =>
    {
        var probe = new Probe();
        var throwOnce = true;
        var given = false;
        probe.Wait = (wake, delay, token) =>
        {
            if (throwOnce) { throwOnce = false; throw new InvalidOperationException("broken turn"); }
            return clock.WaitAsync(wake, delay, token);
        };
        probe.Lease = async lane => { await clock.Delay(TimeSpan.FromMilliseconds(10)); if (lane == Fast && !given && clock.UtcNow > ReplayHarness.Origin + TimeSpan.FromSeconds(3)) { given = true; return Job("FAST_JOB"); } return null; };
        var dispatcher = Create(clock, probe, (Fast, 1, 0), (Held, 0, 1));
        using var stopping = new CancellationTokenSource();
        var loop = dispatcher.RunAsync(stopping.Token);
        RunFor(pump, clock, TimeSpan.FromSeconds(5));
        dispatcher.Notify("FAST_JOB", 0);
        RunFor(pump, clock, TimeSpan.FromSeconds(2));
        Assert.Contains("dispatcher turn", probe.Failures);
        Assert.Equal(1, probe.Runs);
        Assert.False(loop.IsCompleted);
        Assert.True(clock.UtcNow - dispatcher.LastTurnAt <= TimeSpan.FromSeconds(16), "the loop stopped turning");
        stopping.Cancel();
        pump.Drain();
    });
}
