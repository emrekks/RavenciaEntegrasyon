using MarketplaceHub.Infrastructure.Jobs;
using MarketplaceHub.Worker;
using Xunit;
using Xunit.Abstractions;

namespace MarketplaceHub.Application.Tests;

// Queue phase 2 (27 Nov 2026): the dispatcher's lanes, its decision (DispatcherCore.Decide) and every interleaving of a small
// dispatcher with a model of the lease query and the census (the source of truth for which job: lock groups and the store cap).
public sealed class DispatcherCoreTests(ITestOutputHelper output)
{
    private static readonly DateTimeOffset Start = new(2026, 11, 27, 9, 0, 0, TimeSpan.Zero);

    // The default lanes, in lane order: kullanici, webhook, yazma, acil, siparis-okuma, durum-taramasi, iade, toplu, katalog,
    // arka-plan, ilk-cekim.
    private static DispatcherCore.LaneConfig[] DefaultLanes() =>
    [
        new("kullanici", 2, 0), new("webhook", 1, 0), new("yazma", 1, 1), new("acil", 0, 0), new("siparis-okuma", 3, 0), new("durum-taramasi", 2, 0),
        new("iade", 0, 2), new("toplu", 0, 2, Bulk: true), new("katalog", 0, 2, Bulk: true), new("arka-plan", 0, 2, Bulk: true), new("ilk-cekim", 0, 2, Bulk: true)
    ];

    [Fact]
    public void TheDefaultLanesAddUpToTwentySlotsWithElevenShared()
    {
        var settings = DispatcherSettings.Default;
        Assert.Equal(DefaultLanes().Select(lane => (lane.Name, lane.Reserved, lane.Share)),
            DispatcherLanes.Names.Select(lane => (lane, settings.ReservedOf(lane), settings.ShareOf(lane))));
        var core = new DispatcherCore(DefaultLanes());
        Assert.Equal(20, core.Total);
        Assert.Equal(20, settings.Total);
        Assert.Equal(11, core.SharedSlots);
    }

    // Every job type at every priority belongs to exactly one dispatcher lane, the one LaneFor names, and the lease query's cap
    // rule (JobConnectionCap.Caps) holds exactly the jobs of the capped lanes. Webhooks, writes and file imports land where the
    // review asked: their own lanes, never acil.
    [Fact]
    public void EveryJobHasExactlyOneLaneAndTheCapFollowsTheLanes()
    {
        var lanes = DispatcherLanes.All();
        var settings = DispatcherSettings.Default;
        var cap = DispatcherLanes.ConnectionCap(settings);
        string[] types = [.. MarketplaceJobTypes.ProcessorJobTypes, InvoicingJobTypes.InvoiceSubmit, InvoicingJobTypes.InvoiceReconcile, InvoicingJobTypes.InvoiceDocumentFetch,
            InvoicingJobTypes.MarketplaceDelivery, PanelJobTypes.FactoryReset, "IMPORT_PREVIEW", "IMPORT_APPLY", "UNKNOWN_TYPE"];
        foreach (var type in types)
            for (var priority = -4; priority <= 7; priority++)
            {
                var taking = lanes.Where(lane => DispatcherLanes.Takes(lane, type, priority)).Select(lane => lane.Name).ToList();
                Assert.True(taking.Count == 1, $"{type}:{priority} -> {string.Join(",", taking)}");
                var name = DispatcherLanes.LaneFor(type, priority);
                Assert.Equal(taking[0], name);
                Assert.Equal(settings.IsCapped(name), cap.Caps(type, priority));
            }
        Assert.Equal(["katalog", "arka-plan", "ilk-cekim"], settings.CappedLanes);
        Assert.Equal(DispatcherLanes.Webhooks, DispatcherLanes.LaneFor(MarketplaceJobTypes.WebhookIngest, 0));
        Assert.Equal(DispatcherLanes.Writes, DispatcherLanes.LaneFor(MarketplaceJobTypes.PriceInventorySync, 1));
        Assert.Equal(DispatcherLanes.Writes, DispatcherLanes.LaneFor(MarketplaceJobTypes.ProductUpdate, 2));
        Assert.Equal(DispatcherLanes.Bulk, DispatcherLanes.LaneFor("IMPORT_APPLY", 0));
        Assert.Equal(DispatcherLanes.Bulk, DispatcherLanes.LaneFor(InvoicingJobTypes.InvoiceSubmit, JobPriorityPolicy.BulkUserAction));
        Assert.Equal(DispatcherLanes.User, DispatcherLanes.LaneFor(InvoicingJobTypes.InvoiceSubmit, JobPriorityPolicy.UserAction));
        // Second correctness review: -3 work no user waits on as a single click leaves kullanici (priority unchanged).
        Assert.Equal(DispatcherLanes.User, DispatcherLanes.LaneFor(MarketplaceJobTypes.ShipmentAction, JobPriorityPolicy.UserAction));
        Assert.Equal(DispatcherLanes.Writes, DispatcherLanes.LaneFor(MarketplaceJobTypes.PriceInventorySync, JobPriorityPolicy.UserAction));
        Assert.Equal(DispatcherLanes.Writes, DispatcherLanes.LaneFor(InvoicingJobTypes.InvoiceReconcile, JobPriorityPolicy.UserAction));
        Assert.Equal(DispatcherLanes.Writes, DispatcherLanes.LaneFor(MarketplaceJobTypes.HepsiburadaOrderInvoiceReconciliation, JobPriorityPolicy.UserAction));
        Assert.Equal(DispatcherLanes.Writes, DispatcherLanes.LaneFor(MarketplaceJobTypes.ExternalEffectReconcile, JobPriorityPolicy.UserAction));
        Assert.Equal(DispatcherLanes.Bulk, DispatcherLanes.LaneFor(PanelJobTypes.FactoryReset, JobPriorityPolicy.UserAction));
        Assert.Equal(DispatcherLanes.Bulk, DispatcherLanes.LaneFor(MarketplaceJobTypes.ConnectionFullReset, JobPriorityPolicy.UserAction));
        // Under the earlier lane models nothing moves: the flexible user lane still takes them all.
        var flexibleUser = WorkerLanes.All().First(lane => lane.Name == "user");
        Assert.True(flexibleUser.Takes(MarketplaceJobTypes.PriceInventorySync, JobPriorityPolicy.UserAction));
        Assert.True(flexibleUser.Takes(PanelJobTypes.FactoryReset, JobPriorityPolicy.UserAction));
    }

    // The worker runs the model its settings name and the api shows that model's lanes: a switch changes both files (the
    // production compose passes no override). This release ships the flexible lanes, named explicitly. The files are copied
    // next to the test (ShippedSettings), so the test does not depend on where the build puts its output.
    [Fact]
    public void TheWorkerAndTheApiShipTheSameLaneModel()
    {
        static string? Model(string file)
        {
            using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "ShippedSettings", file)));
            return document.RootElement.GetProperty("Worker").GetProperty("LaneModel").GetString();
        }
        Assert.Equal("flexible", Model("worker.appsettings.json"));
        Assert.Equal(Model("worker.appsettings.json"), Model("api.appsettings.json"));
    }

    [Fact]
    public void ALaneUsesItsReservationFirstAndAnotherLaneNeverTakesIt()
    {
        var lanes = DefaultLanes();
        // Catalogue and background flood the shared slots: eleven jobs of katalog and arka-plan run.
        var running = new int[11];
        running[8] = 7;
        running[9] = 4;
        var all = Enumerable.Repeat(true, 11).ToArray();
        // No shared slot is free: only lanes with a free reservation may start, in lane order.
        Assert.Equal(0, DispatcherCore.Decide(lanes, running, all, leaseInFlight: false));
        var ordersOnly = new bool[11];
        ordersOnly[4] = true;
        Assert.Equal(4, DispatcherCore.Decide(lanes, running, ordersOnly, false));
        var catalogOnly = new bool[11];
        catalogOnly[8] = true;
        Assert.Equal(-1, DispatcherCore.Decide(lanes, running, catalogOnly, false));
        // The minimum of siparis-okuma (3) is never lent: with its three jobs running it takes a shared slot only when one is free.
        running[4] = 3;
        Assert.Equal(-1, DispatcherCore.Decide(lanes, running, ordersOnly, false));
        running[9] = 3;
        Assert.Equal(4, DispatcherCore.Decide(lanes, running, ordersOnly, false));
        // One lease at a time.
        Assert.Equal(-1, DispatcherCore.Decide(lanes, running, all, leaseInFlight: true));
    }

    // Second correctness review: a safety tick not below the watchdog's stale limit would restart an idle worker; it is lowered.
    [Fact]
    public void ASafetyTickNotBelowTheWatchdogsLimitIsLowered()
    {
        var logger = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        Assert.Equal(TimeSpan.FromSeconds(15), MarketplaceHub.Worker.Worker.DispatcherSettingsChecked(DispatcherSettings.Default, TimeSpan.FromSeconds(120), logger).SafetyTick);
        Assert.Equal(TimeSpan.FromSeconds(10), MarketplaceHub.Worker.Worker.DispatcherSettingsChecked(DispatcherSettings.Default with { SafetyTick = TimeSpan.FromSeconds(30) }, TimeSpan.FromSeconds(30), logger).SafetyTick);
    }

    // Second correctness review (M2): 19 of the 20 slots busy, one of them a single user action: the next single user action
    // takes kullanici's second reserved slot, whatever else wants a slot.
    [Fact]
    public void ASingleUserActionGetsKullanicisSecondReservedSlotWithNineteenBusy()
    {
        var lanes = DefaultLanes();
        var running = new int[11];
        running[0] = 1;                  // kullanici: one single action
        running[1] = 1; running[2] = 1;  // webhook, yazma: their reserved slots
        running[4] = 3; running[5] = 2;  // siparis-okuma, durum-taramasi: their minimums
        running[2] += 1; running[6] = 2; running[7] = 2; running[8] = 2; running[9] = 2; running[10] = 2; // the 11 shared slots
        Assert.Equal(19, running.Sum());
        var all = Enumerable.Repeat(true, 11).ToArray();
        Assert.Equal(0, DispatcherCore.Decide(lanes, running, all, false));
    }

    [Fact]
    public void AFreeSharedSlotGoesToALaneBelowItsShareBeforeAMoreUrgentLaneBeyondIt()
    {
        var lanes = DefaultLanes();
        var running = new int[11];
        running[1] = 1 + 4; // webhook: its 1 reserved and 4 shared
        running[8] = 2;     // katalog: its share of 2
        var wants = new bool[11];
        wants[1] = wants[6] = wants[8] = true;
        // iade (share 2, none running) is below its share: it goes before webhook and katalog, which are beyond theirs.
        Assert.Equal(6, DispatcherCore.Decide(lanes, running, wants, false));
        wants[6] = false;
        // Beyond their own slots both: a lane of short work (webhook, 4 beyond) goes before a bulk lane (katalog, 0 beyond).
        Assert.Equal(1, DispatcherCore.Decide(lanes, running, wants, false));
        // Two lanes of short work beyond their slots: the one running fewer jobs beyond them (yazma 1, webhook 4) goes first.
        wants[2] = true;
        running[2] = 1 + 1 + 1;
        Assert.Equal(2, DispatcherCore.Decide(lanes, running, wants, false));
        // Equally far beyond: the earlier lane (webhook).
        running[1] = 1 + 1;
        Assert.Equal(1, DispatcherCore.Decide(lanes, running, wants, false));
    }

    [Fact]
    public void AFailingLeaseBacksItsLaneOffAndOtherLanesGoOn()
    {
        var core = new DispatcherCore(DefaultLanes());
        core.BeginLease(4);
        core.LeaseFailed(4, Start);
        Assert.Equal(DispatcherCore.LaneHint.Ready, core.Hint(4));
        Assert.Equal(Start + TimeSpan.FromSeconds(1), core.NextRetry(Start));
        for (var lane = 0; lane < 11; lane++)
            if (lane != 4)
            {
                var (epoch, _) = core.BeginLease(lane);
                core.LeaseFoundNothing(lane, epoch, Start);
            }
        Assert.Equal(-1, core.Next(Start));
        Assert.Equal(4, core.Next(Start + TimeSpan.FromSeconds(1)));
        core.BeginLease(4);
        core.LeaseFailed(4, Start + TimeSpan.FromSeconds(1));
        Assert.Equal(Start + TimeSpan.FromSeconds(3), core.NextRetry(Start + TimeSpan.FromSeconds(1)));
    }

    private static DispatcherCore.LaneCensus[] Census(int lanes, params (int Lane, DispatcherCore.LaneCensus Census)[] rows)
    {
        var result = Enumerable.Repeat(DispatcherCore.LaneCensus.Empty, lanes).ToArray();
        foreach (var (lane, census) in rows) result[lane] = census;
        return result;
    }

    [Fact]
    public void ASignalDuringALeaseOrCensusIsNotUndoneByItsOlderAnswer()
    {
        var core = new DispatcherCore(DefaultLanes());
        var (epoch, _) = core.BeginLease(6);
        core.Notify(6);
        core.LeaseFoundNothing(6, epoch, Start);
        Assert.Equal(DispatcherCore.LaneHint.Ready, core.Hint(6));
        var (again, _) = core.BeginLease(6);
        core.LeaseFoundNothing(6, again, Start);
        Assert.Equal(DispatcherCore.LaneHint.Unclassified, core.Hint(6));
        var start = core.CensusStart();
        core.Notify(6);
        core.ApplyCensus(Census(11), start, safetyTick: false, Start);
        Assert.Equal(DispatcherCore.LaneHint.Ready, core.Hint(6));
    }

    // C1 of the review: a lane held by one store waits for that store's job end; other stores' job ends cost nothing.
    [Fact]
    public void ALaneHeldByAStoreWaitsForThatStoresJobEndOnly()
    {
        var storeA = Guid.NewGuid();
        var storeB = Guid.NewGuid();
        var core = new DispatcherCore(DefaultLanes());
        for (var lane = 0; lane < 11; lane++)
        {
            var (epoch, _) = core.BeginLease(lane);
            if (lane == 4) core.Leased(lane);
            else core.LeaseFoundNothing(lane, epoch, Start);
        }
        // iade: its only job waits for store A (A's returns lock group busy); katalog: a free job the lease did not take.
        core.ApplyCensus(Census(11, (6, new DispatcherCore.LaneCensus(false, [storeA])), (8, new DispatcherCore.LaneCensus(true, []))), core.CensusStart(), false, Start);
        Assert.Equal(DispatcherCore.LaneHint.BlockedOn, core.Hint(6));
        Assert.Equal(DispatcherCore.LaneHint.Blocked, core.Hint(8));
        Assert.Equal(DispatcherCore.LaneHint.Idle, core.Hint(9));
        // A job of store B ends: iade stays held; katalog is retried, but not before a second after its empty lease.
        core.Ended(4, storeB, Start);
        Assert.Equal(DispatcherCore.LaneHint.BlockedOn, core.Hint(6));
        Assert.Equal(DispatcherCore.LaneHint.Ready, core.Hint(8));
        Assert.Equal(-1, core.Next(Start));
        Assert.Equal(8, core.Next(Start + DispatcherCore.RetrySpacing));
        // A store A job ends (any lane): iade tries again.
        core.BeginLease(4);
        core.Leased(4);
        core.Ended(4, storeA, Start);
        Assert.Equal(DispatcherCore.LaneHint.Ready, core.Hint(6));
    }

    // A store whose job ended while the census ran is not taken as still holding the lane.
    [Fact]
    public void ACensusDoesNotHoldALaneOnAStoreThatEndedDuringIt()
    {
        var storeA = Guid.NewGuid();
        var core = new DispatcherCore(DefaultLanes());
        core.BeginLease(4);
        core.Leased(4);
        var (epoch, _) = core.BeginLease(6);
        core.LeaseFoundNothing(6, epoch, Start);
        var start = core.CensusStart();
        core.Ended(4, storeA, Start + TimeSpan.FromSeconds(2));
        core.ApplyCensus(Census(11, (6, new DispatcherCore.LaneCensus(false, [storeA]))), start, false, Start + TimeSpan.FromSeconds(2));
        Assert.Equal(DispatcherCore.LaneHint.Ready, core.Hint(6));
    }

    // ---- Every interleaving of a small dispatcher ----
    //
    // Lanes: U (user: 1 reserved, exempt from the cap), O (orders: 1 reserved), C (catalog: share 1), G (background: share 1):
    // 4 slots, 2 shared. Jobs belong to a lane and one of two stores; a store's jobs of one lane share a lock group; the per-store
    // cap is 1 outside U. The lease model takes the first queued job of the lane that is neither locked nor capped (what the
    // lease query does), at the moment the lease starts or ends (both explored); the census reports per lane whether a job could
    // be leased and which stores hold the others (also read at the start or the end of the query). Events, in any order and
    // between any lease's start and end: a job arrives with its notification or without one (a lost signal), a running job ends
    // (success or failure) or ends and goes back to the queue without a signal (a retry), a "freed" notification, a lease that
    // fails, the safety tick, time passing a backoff. Checked in every reachable state:
    //   - never more running than the slots; the shared slots never overused; every lane's reservation free for it;
    //   - the per-store cap never exceeded;
    //   - the dispatcher alone reaches a wait within a bounded number of steps (no busy loop: leases per event bounded);
    //   - the decision always picks a lane of the lowest tier among those that may lease;
    //   - every queued job runs in the end with no signal at all (jobs ending and the safety tick only).
    // Second correctness review (28 Nov): lane G is a bulk lane, so Decide's four tiers (below the reservation, below the share,
    // beyond it for short work, beyond it for bulk work) are all reachable and checked. The deferral needs no model of its own:
    // to the core a deferred job is a job held by its own store (the census names that store, HeldBy), the case the store-held
    // census below explores; its predicate itself is the lease's and the census's SQL, checked against PostgreSQL in
    // DispatcherPostgreSqlTests.
    private const int Cap = 1;
    private static readonly DispatcherCore.LaneConfig[] SmallLanes = [new("U", 1, 0), new("O", 1, 0), new("C", 0, 1), new("G", 0, 1, Bulk: true)];
    private static readonly Dictionary<char, Guid> Stores = new() { ['a'] = new Guid("00000000-0000-0000-0000-00000000000a"), ['b'] = new Guid("00000000-0000-0000-0000-00000000000b") };
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
        public int LeaseLane = -1;
        public long LeaseEpoch;
        public bool LeaseTaken;
        public Job? LeaseResult;
        public (long[] Epochs, long EndedSequence)? CensusStarted;
        public DispatcherCore.LaneCensus[]? CensusRows;
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
            CensusStarted = CensusStarted is { } started ? ([.. started.Epochs], started.EndedSequence) : null,
            CensusRows = CensusRows is null ? null : [.. CensusRows],
            CensusSafety = CensusSafety
        };

        public string Key()
        {
            string Stores(IEnumerable<Guid> stores) => string.Concat(stores.Select(store => store == DispatcherCoreTests.Stores['a'] ? "a" : "b").Order(StringComparer.Ordinal));
            var lanes = string.Join(",", Enumerable.Range(0, SmallLanes.Length).Select(lane => $"{Core.Running(lane)}{Core.Hint(lane)}{Stores(Core.BlockedOnStores(lane))}{(Core.BackingOff(lane, Now) ? "b" : "")}"));
            var queued = string.Join(",", Queued.Select(job => $"{job.Lane}{job.Store}"));
            var running = string.Join(",", Running.Select(job => $"{job.Lane}{job.Store}").Order(StringComparer.Ordinal));
            var lease = LeaseLane < 0 ? "-" : $"{LeaseLane}{(Core.Epoch(LeaseLane) == LeaseEpoch ? "=" : "!")}{(LeaseTaken ? LeaseResult is null ? "n" : $"{LeaseResult.Lane}{LeaseResult.Store}" : "?")}";
            var census = CensusStarted is not { } started ? "-" : string.Join("", Enumerable.Range(0, SmallLanes.Length).Select(lane => Core.Epoch(lane) == started.Epochs[lane] ? "=" : "!"))
                + (CensusRows is null ? "?" : string.Join("", CensusRows.Select(row => $"{(row.HasFree ? "f" : "")}{Stores(row.BlockedOn)};"))) + (CensusSafety ? "s" : "");
            return $"{lanes}|{queued}|{running}|{lease}|{census}|{ArrivalsLeft}{RequeuesLeft}{FailuresLeft}{FreedLeft}{(SafetyDue ? "S" : "")}";
        }
    }

    private static bool Locked(State state, Job job) => state.Running.Any(running => running.Store == job.Store && running.Lane == job.Lane);
    private static bool CappedNow(State state, Job job) => !Exempt(job.Lane) && state.Running.Count(running => running.Store == job.Store && !Exempt(running.Lane)) >= Cap;

    // The lease query's answer for the lane now (and the job moves to running, as a committed lease does).
    private static Job? LeaseModel(State state, int lane)
    {
        foreach (var job in state.Queued)
        {
            if (job.Lane != lane || Locked(state, job) || CappedNow(state, job)) continue;
            state.Queued.Remove(job);
            state.Running.Add(job);
            return job;
        }
        return null;
    }

    private static DispatcherCore.LaneCensus[] CensusModel(State state) =>
        [.. Enumerable.Range(0, SmallLanes.Length).Select(lane =>
        {
            var jobs = state.Queued.Where(job => job.Lane == lane).ToList();
            return new DispatcherCore.LaneCensus(jobs.Any(job => !Locked(state, job) && !CappedNow(state, job)),
                [.. jobs.Where(job => Locked(state, job) || CappedNow(state, job)).Select(job => Stores[job.Store]).Distinct()]);
        })];

    private static IEnumerable<(string Name, State Next)> Successors(State state)
    {
        if (state.ArrivalsLeft > 0)
            for (var lane = 0; lane < SmallLanes.Length; lane++)
                foreach (var store in "ab")
                    foreach (var signalled in new[] { true, false })
                    {
                        var next = state.Copy();
                        next.ArrivalsLeft--;
                        next.Queued.Add(new Job(lane, store, next.NextId++));
                        if (signalled) next.Core.Notify(lane);
                        yield return ($"arrive({lane}{store}{(signalled ? "" : ",lost")})", next);
                    }
        foreach (var job in state.Running.Where(job => !(state.LeaseTaken && state.LeaseResult == job)))
        {
            var next = state.Copy();
            next.Running.Remove(job);
            next.Core.Ended(job.Lane, Stores[job.Store], next.Now);
            yield return ($"end({job.Lane}{job.Store})", next);
            if (state.RequeuesLeft > 0)
            {
                var retry = state.Copy();
                retry.RequeuesLeft--;
                retry.Running.Remove(job);
                retry.Queued.Add(job);
                retry.Core.Ended(job.Lane, Stores[job.Store], retry.Now);
                yield return ($"fail-and-requeue({job.Lane}{job.Store})", retry);
            }
        }
        if (state.FreedLeft > 0)
        {
            var next = state.Copy();
            next.FreedLeft--;
            next.Core.RetryBlocked(next.Now);
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
        foreach (var step in DispatcherSteps(state)) yield return step;
    }

    private static IEnumerable<(string Name, State Next)> DispatcherSteps(State state)
    {
        if (state.LeaseLane >= 0)
        {
            var next = state.Copy();
            var job = next.LeaseTaken ? next.LeaseResult : LeaseModel(next, next.LeaseLane);
            if (job is null) next.Core.LeaseFoundNothing(next.LeaseLane, next.LeaseEpoch, next.Now);
            else next.Core.Leased(next.LeaseLane);
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
        if (state.CensusStarted is { } started)
        {
            var next = state.Copy();
            next.Core.ApplyCensus(next.CensusRows ?? CensusModel(next), started, next.CensusSafety, next.Now);
            if (next.CensusSafety) next.SafetyDue = false;
            next.CensusStarted = null;
            next.CensusRows = null;
            next.CensusSafety = false;
            yield return ("census-end", next);
            yield break;
        }
        var (kind, lane) = state.Core.Plan(state.Now, state.SafetyDue, censusAllowed: true);
        if (kind == DispatcherCore.StepKind.Lease)
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
        else if (kind == DispatcherCore.StepKind.Census)
            foreach (var atStart in new[] { true, false })
            {
                var next = state.Copy();
                next.CensusStarted = next.Core.CensusStart();
                next.CensusRows = atStart ? CensusModel(next) : null;
                next.CensusSafety = state.SafetyDue;
                yield return ($"census-start{(atStart ? "(now)" : "")}", next);
            }
    }

    private static void CheckInvariants(State state, string where)
    {
        var core = state.Core;
        Assert.True(core.Violation() is null, $"{core.Violation()}: {where}");
        var reservedIdle = Enumerable.Range(0, SmallLanes.Length).Sum(lane => Math.Max(0, SmallLanes[lane].Reserved - core.Running(lane)));
        Assert.True(core.Total - core.RunningTotal >= reservedIdle, $"a reservation was lent: {where}");
        foreach (var store in "ab")
            Assert.True(state.Running.Count(job => job.Store == store && !Exempt(job.Lane)) <= Cap, $"store {store} over the cap: {where}");
        for (var lane = 0; lane < SmallLanes.Length; lane++)
        {
            var modelled = state.Running.Count(job => job.Lane == lane) - (state.LeaseTaken && state.LeaseResult?.Lane == lane ? 1 : 0);
            Assert.True(modelled == core.Running(lane), $"lane {lane}: core counts {core.Running(lane)}, model runs {modelled}: {where}");
        }
        if (state.LeaseLane < 0 && core.Next(state.Now) is var chosen and >= 0)
        {
            int Tier(int lane) => core.Running(lane) < SmallLanes[lane].Reserved ? 0 : core.Running(lane) - SmallLanes[lane].Reserved < SmallLanes[lane].Share ? 1 : SmallLanes[lane].Bulk ? 3 : 2;
            for (var lane = 0; lane < SmallLanes.Length; lane++)
                if (core.Hint(lane) == DispatcherCore.LaneHint.Ready && !core.BackingOff(lane, state.Now) && core.HasRoom(lane))
                    Assert.True(Tier(chosen) <= Tier(lane), $"lane {chosen} chosen over lane {lane} of a lower tier: {where}");
        }
    }

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

    private static void CheckEveryJobRuns(State state, string where)
    {
        var probe = state.Copy();
        probe.ArrivalsLeft = 0;
        probe.FreedLeft = 0;
        probe.RequeuesLeft = 0;
        probe.FailuresLeft = 0;
        for (var round = 0; round < 300; round++)
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
                probe.Core.Ended(job.Lane, Stores[job.Store], probe.Now);
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
        var start = new State { Core = new DispatcherCore(SmallLanes) };
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
