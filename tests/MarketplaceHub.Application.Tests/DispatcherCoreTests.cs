using MarketplaceHub.Worker;
using Xunit;
using Xunit.Abstractions;

namespace MarketplaceHub.Application.Tests;

// Queue phase 2 (27 Nov 2026): the dispatcher's decision (DispatcherCore.Decide) and every interleaving of a small dispatcher
// with a model of the lease query (the source of truth for which job: lock groups and the per-store cap).
public sealed class DispatcherCoreTests(ITestOutputHelper output)
{
    private static readonly DateTimeOffset Start = new(2026, 11, 27, 9, 0, 0, TimeSpan.Zero);

    private static DispatcherCore.LaneConfig[] DefaultLanes() =>
    [
        new("kullanici", 2, 0), new("acil", 1, 0), new("siparis-okuma", 3, 0), new("durum-taramasi", 3, 0),
        new("iade", 0, 3), new("katalog", 0, 3), new("arka-plan", 0, 2), new("ilk-cekim", 0, 3)
    ];

    [Fact]
    public void TheDefaultLanesAddUpToTwentySlotsWithElevenShared()
    {
        var settings = Infrastructure.Jobs.DispatcherSettings.Default;
        Assert.Equal(DefaultLanes().Select(lane => (lane.Name, lane.Reserved, lane.Share)),
            Infrastructure.Jobs.DispatcherLanes.Names.Select(lane => (lane, settings.ReservedOf(lane), settings.ShareOf(lane))));
        var core = new DispatcherCore(DefaultLanes(), Start);
        Assert.Equal(20, core.Total);
        Assert.Equal(20, settings.Total);
        Assert.Equal(11, core.SharedSlots);
    }

    [Fact]
    public void ALaneUsesItsReservationFirstAndAnotherLaneNeverTakesIt()
    {
        var lanes = DefaultLanes();
        // Catalogue and background flood the shared slots: eleven jobs of katalog and arka-plan run.
        var running = new int[8];
        running[5] = 7;
        running[6] = 4;
        var all = Enumerable.Repeat(true, 8).ToArray();
        // No shared slot is free: only lanes with a free reservation may start, in lane order.
        Assert.Equal(0, DispatcherCore.Decide(lanes, running, all, leaseInFlight: false));
        var ordersOnly = new bool[8];
        ordersOnly[2] = true;
        Assert.Equal(2, DispatcherCore.Decide(lanes, running, ordersOnly, false));
        var catalogOnly = new bool[8];
        catalogOnly[5] = true;
        Assert.Equal(-1, DispatcherCore.Decide(lanes, running, catalogOnly, false));
        // The minimum of siparis-okuma (3) is never lent: with its three jobs running it takes a shared slot only when one is free.
        running[2] = 3;
        Assert.Equal(-1, DispatcherCore.Decide(lanes, running, ordersOnly, false));
        running[6] = 3;
        Assert.Equal(2, DispatcherCore.Decide(lanes, running, ordersOnly, false));
        // One lease at a time.
        Assert.Equal(-1, DispatcherCore.Decide(lanes, running, all, leaseInFlight: true));
    }

    [Fact]
    public void AFreeSharedSlotGoesToALaneBelowItsShareBeforeAMoreUrgentLaneBeyondIt()
    {
        var lanes = DefaultLanes();
        var running = new int[8];
        running[1] = 1 + 4; // acil: its 1 reserved and 4 shared
        running[5] = 3;     // katalog: its share of 3
        var wants = new bool[8];
        wants[1] = wants[4] = wants[5] = true;
        // iade (share 3, none running) is below its share: it goes before acil and katalog, which are beyond theirs.
        Assert.Equal(4, DispatcherCore.Decide(lanes, running, wants, false));
        wants[4] = false;
        // Beyond their own slots both: the lane running fewer jobs beyond them (katalog 0, acil 4) goes first.
        Assert.Equal(5, DispatcherCore.Decide(lanes, running, wants, false));
        running[1] = 1 + 1;
        running[5] = 3 + 1;
        // Equally far beyond: the earlier lane (acil).
        Assert.Equal(1, DispatcherCore.Decide(lanes, running, wants, false));
    }

    [Fact]
    public void AFailingLeaseBacksItsLaneOffAndOtherLanesGoOn()
    {
        var core = new DispatcherCore(DefaultLanes(), Start);
        var (_, _) = core.BeginLease(2);
        core.LeaseFailed(2, Start);
        Assert.Equal(DispatcherCore.LaneHint.Ready, core.Hint(2));
        Assert.Equal(Start + TimeSpan.FromSeconds(1), core.NextRetry(Start));
        // Lanes 0 and 1 are leased first anyway (lane order); after they found nothing lane 2 still waits for its retry.
        for (var lane = 0; lane < 8; lane++)
            if (lane != 2)
            {
                var (epoch, _) = core.BeginLease(lane);
                core.LeaseFoundNothing(lane, epoch);
            }
        Assert.Equal(-1, core.Next(Start));
        Assert.Equal(2, core.Next(Start + TimeSpan.FromSeconds(1)));
        core.BeginLease(2);
        core.LeaseFailed(2, Start + TimeSpan.FromSeconds(1));
        Assert.Equal(Start + TimeSpan.FromSeconds(3), core.NextRetry(Start + TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void ASignalDuringALeaseIsNotUndoneByItsEmptyAnswer()
    {
        var core = new DispatcherCore(DefaultLanes(), Start);
        var (epoch, _) = core.BeginLease(4);
        core.Notify(4);
        core.LeaseFoundNothing(4, epoch);
        Assert.Equal(DispatcherCore.LaneHint.Ready, core.Hint(4));
        // A census that started before a signal does not mark the lane idle either.
        var (again, _) = core.BeginLease(4);
        core.LeaseFoundNothing(4, again);
        Assert.Equal(DispatcherCore.LaneHint.Unclassified, core.Hint(4));
        var epochs = core.Epochs();
        core.Notify(4);
        core.ApplyCensus(new bool[8], epochs, safetyTick: false);
        Assert.Equal(DispatcherCore.LaneHint.Ready, core.Hint(4));
    }

    [Fact]
    public void AJobsEndRetriesTheBlockedLanesOnce()
    {
        var core = new DispatcherCore(DefaultLanes(), Start);
        for (var lane = 0; lane < 8; lane++)
        {
            var (epoch, _) = core.BeginLease(lane);
            if (lane == 2) core.Leased(lane, Start);
            else core.LeaseFoundNothing(lane, epoch);
        }
        // siparis-okuma stays Ready after its lease found a job; its next lease finds nothing.
        var (orders, _) = core.BeginLease(2);
        core.LeaseFoundNothing(2, orders);
        var withRows = new bool[8];
        withRows[4] = true; // iade has jobs the lease did not take (its lock group is busy)
        core.ApplyCensus(withRows, core.Epochs(), safetyTick: false);
        Assert.Equal(DispatcherCore.LaneHint.Blocked, core.Hint(4));
        Assert.Equal(DispatcherCore.LaneHint.Idle, core.Hint(5));
        Assert.Equal(-1, core.Next(Start));
        core.Ended(2, Start);
        Assert.Equal(DispatcherCore.LaneHint.Ready, core.Hint(4));
        Assert.Equal(DispatcherCore.LaneHint.Idle, core.Hint(5));
        Assert.Equal(4, core.Next(Start));
    }

    // ---- Every interleaving of a small dispatcher ----
    //
    // Lanes: U (user: 1 reserved, exempt from the cap), O (orders: 1 reserved), C (catalog: share 1), G (background: share 1):
    // 4 slots, 2 shared. Jobs belong to a lane and one of two stores; a store's jobs of one lane share a lock group; the
    // per-store cap is 1 outside U. The lease model takes the first queued job of the lane that is neither locked nor capped
    // (what the lease query does), at the moment the lease starts or ends (both explored); the census sees every queued job
    // of a lane (also at the start or the end of the query). Events, in any order and between any lease's start and end:
    // a job arrives with its notification or without one (a lost signal), a running job ends (success or failure) or ends and
    // goes back to the queue without a signal (a retry), a "freed" notification, a lease that fails, the safety tick, time
    // passing a backoff. Checked in every reachable state:
    //   - never more running than the slots; the shared slots never overused; every lane's reservation free for it;
    //   - the per-store cap never exceeded;
    //   - the dispatcher alone reaches a wait within a bounded number of steps (no busy loop: leases per event bounded);
    //   - the decision always picks a lane of the lowest tier among those that may lease;
    //   - every queued job runs in the end with no signal at all (jobs ending and the safety tick only).
    private const int Cap = 1;
    private static readonly DispatcherCore.LaneConfig[] SmallLanes = [new("U", 1, 0), new("O", 1, 0), new("C", 0, 1), new("G", 0, 1)];
    private static bool Exempt(int lane) => lane == 0;

    private sealed record Job(int Lane, char Store, int Id);

    private sealed class State
    {
        public required DispatcherCore Core;
        public List<Job> Queued = [];
        public List<Job> Running = [];
        public int NextId;
        public int ArrivalsLeft = 2;
        public int RequeuesLeft = 1;
        public int FailuresLeft = 1;
        public int FreedLeft = 1;
        public int TimeSteps;
        public bool SafetyDue;
        // The lease in flight: lane, epoch, whether the model already took its result (at its start), and that result.
        public int LeaseLane = -1;
        public long LeaseEpoch;
        public bool LeaseTaken;
        public Job? LeaseResult;
        // The census in flight: epochs at its start, its rows (null: read at its end), safety.
        public long[]? CensusEpochs;
        public bool[]? CensusRows;
        public bool CensusSafety;

        public DateTimeOffset Now => Start + TimeSpan.FromSeconds(20 * TimeSteps);

        public State Copy() => new()
        {
            Core = Core.Clone(),
            Queued = [.. Queued],
            Running = [.. Running],
            NextId = NextId,
            ArrivalsLeft = ArrivalsLeft,
            RequeuesLeft = RequeuesLeft,
            FailuresLeft = FailuresLeft,
            FreedLeft = FreedLeft,
            TimeSteps = TimeSteps,
            SafetyDue = SafetyDue,
            LeaseLane = LeaseLane,
            LeaseEpoch = LeaseEpoch,
            LeaseTaken = LeaseTaken,
            LeaseResult = LeaseResult,
            CensusEpochs = CensusEpochs is null ? null : [.. CensusEpochs],
            CensusRows = CensusRows is null ? null : [.. CensusRows],
            CensusSafety = CensusSafety
        };

        public string Key()
        {
            var lanes = string.Join(",", Enumerable.Range(0, SmallLanes.Length).Select(lane => $"{Core.Running(lane)}{Core.Hint(lane).ToString()[0]}{(Core.BackingOff(lane, Now) ? "b" : "")}"));
            var queued = string.Join(",", Queued.Select(job => $"{job.Lane}{job.Store}"));
            var running = string.Join(",", Running.Select(job => $"{job.Lane}{job.Store}").Order(StringComparer.Ordinal));
            var lease = LeaseLane < 0 ? "-" : $"{LeaseLane}{(Core.Epoch(LeaseLane) == LeaseEpoch ? "=" : "!")}{(LeaseTaken ? LeaseResult is null ? "n" : $"{LeaseResult.Lane}{LeaseResult.Store}" : "?")}";
            var census = CensusEpochs is null ? "-" : string.Join("", Enumerable.Range(0, SmallLanes.Length).Select(lane => Core.Epoch(lane) == CensusEpochs[lane] ? "=" : "!"))
                + (CensusRows is null ? "?" : string.Join("", CensusRows.Select(row => row ? "1" : "0"))) + (CensusSafety ? "s" : "");
            return $"{lanes}|{queued}|{running}|{lease}|{census}|{ArrivalsLeft}{RequeuesLeft}{FailuresLeft}{FreedLeft}{(SafetyDue ? "S" : "")}";
        }
    }

    // The lease query's answer for the lane now (and the job moves to running, as a committed lease does).
    private static Job? LeaseModel(State state, int lane)
    {
        foreach (var job in state.Queued)
        {
            if (job.Lane != lane) continue;
            if (state.Running.Any(running => running.Store == job.Store && running.Lane == job.Lane)) continue;
            if (!Exempt(lane) && state.Running.Count(running => running.Store == job.Store && !Exempt(running.Lane)) >= Cap) continue;
            state.Queued.Remove(job);
            state.Running.Add(job);
            return job;
        }
        return null;
    }

    private static bool[] CensusModel(State state) => [.. Enumerable.Range(0, SmallLanes.Length).Select(lane => state.Queued.Any(job => job.Lane == lane))];

    private static IEnumerable<(string Name, State Next)> Successors(State state)
    {
        // External events.
        if (state.ArrivalsLeft > 0)
            for (var lane = 0; lane < SmallLanes.Length; lane++)
                foreach (var store in "ab")
                {
                    foreach (var signalled in new[] { true, false })
                    {
                        var next = state.Copy();
                        next.ArrivalsLeft--;
                        next.Queued.Add(new Job(lane, store, next.NextId++));
                        if (signalled) next.Core.Notify(lane);
                        yield return ($"arrive({lane}{store}{(signalled ? "" : ",lost")})", next);
                    }
                }
        // A running job ends; only jobs the core counts (not one a lease took that has not answered yet).
        foreach (var job in state.Running.Where(job => !(state.LeaseTaken && state.LeaseResult == job)))
        {
            var next = state.Copy();
            next.Running.Remove(job);
            next.Core.Ended(job.Lane, next.Now);
            yield return ($"end({job.Lane}{job.Store})", next);
            if (state.RequeuesLeft > 0)
            {
                var retry = state.Copy();
                retry.RequeuesLeft--;
                retry.Running.Remove(job);
                retry.Queued.Add(job);
                retry.Core.Ended(job.Lane, retry.Now);
                yield return ($"fail-and-requeue({job.Lane}{job.Store})", retry);
            }
        }
        if (state.FreedLeft > 0)
        {
            var next = state.Copy();
            next.FreedLeft--;
            next.Core.Unblock();
            yield return ("freed", next);
        }
        if (!state.SafetyDue)
        {
            var next = state.Copy();
            next.SafetyDue = true;
            yield return ("tick", next);
        }
        if (Enumerable.Range(0, SmallLanes.Length).Any(lane => state.Core.BackingOff(lane, state.Now)))
        {
            var next = state.Copy();
            next.TimeSteps++;
            yield return ("time", next);
        }
        // The dispatcher.
        foreach (var step in DispatcherSteps(state)) yield return step;
    }

    private static IEnumerable<(string Name, State Next)> DispatcherSteps(State state)
    {
        if (state.LeaseLane >= 0)
        {
            var next = state.Copy();
            var job = next.LeaseTaken ? next.LeaseResult : LeaseModel(next, next.LeaseLane);
            if (job is null) next.Core.LeaseFoundNothing(next.LeaseLane, next.LeaseEpoch);
            else next.Core.Leased(next.LeaseLane, next.Now);
            next.LeaseLane = -1;
            next.LeaseTaken = false;
            next.LeaseResult = null;
            yield return ("lease-end", next);
            if (!state.LeaseTaken && state.FailuresLeft > 0)
            {
                var failed = state.Copy();
                failed.FailuresLeft--;
                failed.Core.LeaseFailed(failed.LeaseLane, failed.Now);
                failed.LeaseLane = -1;
                yield return ("lease-fails", failed);
            }
            yield break;
        }
        if (state.CensusEpochs is not null)
        {
            var next = state.Copy();
            next.Core.ApplyCensus(next.CensusRows ?? CensusModel(next), next.CensusEpochs!, next.CensusSafety);
            if (next.CensusSafety) next.SafetyDue = false;
            next.CensusEpochs = null;
            next.CensusRows = null;
            next.CensusSafety = false;
            yield return ("census-end", next);
            yield break;
        }
        var (kind, lane) = state.Core.Plan(state.Now, state.SafetyDue, censusAllowed: true);
        if (kind == DispatcherCore.StepKind.Lease)
        {
            foreach (var atStart in new[] { true, false })
            {
                var next = state.Copy();
                (next.LeaseEpoch, _) = next.Core.BeginLease(lane);
                next.LeaseLane = lane;
                if (atStart)
                {
                    next.LeaseTaken = true;
                    next.LeaseResult = LeaseModel(next, lane);
                }
                yield return ($"lease-start({lane}{(atStart ? ",now" : "")})", next);
            }
        }
        else if (kind == DispatcherCore.StepKind.Census)
        {
            foreach (var atStart in new[] { true, false })
            {
                var next = state.Copy();
                next.CensusEpochs = next.Core.Epochs();
                next.CensusRows = atStart ? CensusModel(next) : null;
                next.CensusSafety = state.SafetyDue;
                yield return ($"census-start{(atStart ? "(now)" : "")}", next);
            }
        }
    }

    private static void CheckInvariants(State state, string where)
    {
        var core = state.Core;
        Assert.True(core.Violation() is null, $"{core.Violation()}: {where}");
        Assert.True(core.RunningTotal <= core.Total, where);
        var reservedIdle = Enumerable.Range(0, SmallLanes.Length).Sum(lane => Math.Max(0, SmallLanes[lane].Reserved - core.Running(lane)));
        Assert.True(core.Total - core.RunningTotal >= reservedIdle, $"a reservation was lent: {where}");
        foreach (var store in "ab")
            Assert.True(state.Running.Count(job => job.Store == store && !Exempt(job.Lane)) <= Cap, $"store {store} over the cap: {where}");
        // The core counts what the model runs (a lease that took a job but has not answered is not counted yet).
        for (var lane = 0; lane < SmallLanes.Length; lane++)
        {
            var modelled = state.Running.Count(job => job.Lane == lane) - (state.LeaseTaken && state.LeaseResult?.Lane == lane ? 1 : 0);
            Assert.True(modelled == core.Running(lane), $"lane {lane}: core counts {core.Running(lane)}, model runs {modelled}: {where}");
        }
        // The decision picks a lane of the lowest tier among those that may lease.
        if (state.LeaseLane < 0 && core.Next(state.Now) is var chosen and >= 0)
        {
            int Tier(int lane) => core.Running(lane) < SmallLanes[lane].Reserved ? 0 : core.Running(lane) - SmallLanes[lane].Reserved < SmallLanes[lane].Share ? 1 : 2;
            for (var lane = 0; lane < SmallLanes.Length; lane++)
                if (core.Hint(lane) == DispatcherCore.LaneHint.Ready && !core.BackingOff(lane, state.Now) && core.HasRoom(lane))
                    Assert.True(Tier(chosen) <= Tier(lane), $"lane {chosen} chosen over lane {lane} of a lower tier: {where}");
        }
    }

    // The dispatcher alone (no other event) reaches a wait: each of its steps either leases, reads the census or waits, and a
    // bounded number of them may follow each other.
    private static void CheckNoBusyLoop(State state, string where)
    {
        var probe = state.Copy();
        var bound = (3 * SmallLanes.Length) + probe.Queued.Count + 4;
        for (var steps = 0; ; steps++)
        {
            Assert.True(steps <= bound, $"the dispatcher took {steps} steps without waiting: {where}");
            var step = DispatcherSteps(probe).FirstOrDefault();
            if (step.Next is null) return;
            probe = step.Next;
        }
    }

    // With no signal at all, the queue drains: jobs end, the safety tick reads the census, backoffs pass.
    private static void CheckEveryJobRuns(State state, string where)
    {
        var probe = state.Copy();
        probe.ArrivalsLeft = 0;
        probe.FreedLeft = 0;
        probe.RequeuesLeft = 0;
        probe.FailuresLeft = 0;
        for (var round = 0; round < 200; round++)
        {
            var step = DispatcherSteps(probe).FirstOrDefault();
            if (step.Next is not null)
            {
                probe = step.Next;
                continue;
            }
            if (probe.Running.Count > 0)
            {
                var job = probe.Running[0];
                probe.Running.RemoveAt(0);
                probe.Core.Ended(job.Lane, probe.Now);
                continue;
            }
            if (probe.Queued.Count == 0) return;
            if (Enumerable.Range(0, SmallLanes.Length).Any(lane => probe.Core.BackingOff(lane, probe.Now))) probe.TimeSteps++;
            else probe.SafetyDue = true;
        }
        Assert.Fail($"jobs {string.Join(",", probe.Queued.Select(job => $"{job.Lane}{job.Store}"))} never ran: {where}");
    }

    [Fact]
    public void EveryInterleavingKeepsTheSlotsTheReservationsAndTheCapAndRunsEveryJob()
    {
        var start = new State { Core = new DispatcherCore(SmallLanes, Start) };
        var seen = new HashSet<string> { start.Key() };
        var queue = new Queue<(State State, string Path)>();
        queue.Enqueue((start, ""));
        var explored = 0;
        var transitions = 0;
        while (queue.Count > 0)
        {
            var (state, path) = queue.Dequeue();
            explored++;
            CheckInvariants(state, path);
            CheckNoBusyLoop(state, path);
            CheckEveryJobRuns(state, path);
            foreach (var (name, next) in Successors(state))
            {
                transitions++;
                var where = path.Length < 400 ? $"{path} {name}" : $"...{name}";
                CheckInvariants(next, where);
                if (seen.Add(next.Key())) queue.Enqueue((next, where));
            }
        }
        output.WriteLine($"{explored} states, {transitions} transitions");
        Assert.True(explored > 10_000, $"only {explored} states explored");
    }
}
