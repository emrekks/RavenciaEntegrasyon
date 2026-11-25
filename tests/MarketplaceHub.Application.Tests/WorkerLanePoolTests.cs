using MarketplaceHub.Infrastructure.Jobs;
using MarketplaceHub.Worker;
using Microsoft.Extensions.Configuration;
using Xunit;
using WorkerService = MarketplaceHub.Worker.Worker;

namespace MarketplaceHub.Application.Tests;

// Worker lanes step 3 (19 Nov 2026): flexible lanes (LaneSlotPool). Pure counting, no clock and no database.
public sealed class WorkerLanePoolTests
{
    // Three lanes, six slots: hot 2 (never lends), catalog 2, background 2.
    private static LaneSlotPool Pool() => new(new Dictionary<string, int> { ["hot"] = 2, ["product-catalog"] = 2, ["background"] = 2 }, ["hot"]);

    [Fact]
    public void ALaneBorrowsOnlyWhenItsOwnSlotsAreBusyAndNeverAProtectedLanesIdleSlot()
    {
        var pool = Pool();
        Assert.False(pool.TryClaim("product-catalog", ownSlot: false));
        Assert.True(pool.TryClaim("product-catalog", ownSlot: true));
        Assert.True(pool.TryClaim("product-catalog", ownSlot: true));
        // Own slots busy: the background lane's two idle slots may be borrowed, the hot lane's two may not.
        Assert.True(pool.TryClaim("product-catalog", ownSlot: false));
        Assert.True(pool.TryClaim("product-catalog", ownSlot: false));
        Assert.False(pool.TryClaim("product-catalog", ownSlot: false));
        Assert.Equal(2, pool.Borrowed("product-catalog"));
        Assert.Equal(4, pool.Running);
        // The hot lane's own slots are there at once.
        Assert.True(pool.TryClaim("hot", ownSlot: true));
        Assert.True(pool.TryClaim("hot", ownSlot: true));
        Assert.Equal(6, pool.Running);
    }

    [Fact]
    public void ALenderGetsItsSlotBackAtTheNextReleaseAndBorrowersTakeNothingNewMeanwhile()
    {
        var pool = Pool();
        IReadOnlyList<string>? freedFor = null;
        pool.SlotFreed += lanes => freedFor = lanes;
        Assert.True(pool.TryClaim("product-catalog", true));
        Assert.True(pool.TryClaim("product-catalog", true));
        Assert.True(pool.TryClaim("product-catalog", false));
        Assert.True(pool.TryClaim("product-catalog", false));
        Assert.True(pool.TryClaim("hot", true));
        Assert.True(pool.TryClaim("hot", true));
        // Background work comes: every slot is busy (two of its own lent out). Its slot waits; nothing running is cut off.
        pool.NoteWork("background");
        Assert.False(pool.TryClaim("background", true));
        Assert.True(pool.Wants("background"));
        // A catalog job ends: the background lane is told first, and the borrower may not take the freed slot.
        pool.Release("product-catalog", false);
        Assert.Equal(["background"], freedFor);
        Assert.False(pool.TryClaim("product-catalog", false));
        Assert.True(pool.TryClaim("background", true));
        Assert.False(pool.Wants("background"));
        Assert.Equal(1, pool.Borrowed("product-catalog"));
    }

    [Fact]
    public void NeverMoreJobsThanTheLanesSlotsAndReleasingTwiceChangesNothing()
    {
        var pool = new LaneSlotPool(new Dictionary<string, int> { ["user"] = 1, ["background"] = 1 }, []);
        Assert.True(pool.TryClaim("background", true));
        Assert.True(pool.TryClaim("background", false));
        Assert.False(pool.TryClaim("user", true));
        Assert.Equal(2, pool.Running);
        pool.Release("background", false);
        pool.Release("background", false);
        Assert.Equal(1, pool.Running);
        Assert.False(pool.TryClaim("unknown-lane", true));
        Assert.Equal(2, pool.Total);
    }

    [Fact]
    public async Task ASlotGivesItsRoomBackWhenItFindsNothingFailsOrItsJobEnds()
    {
        var pool = Pool();
        Assert.Null(await WorkerService.LeaseThroughPoolAsync(pool, "background", true, () => Task.FromResult<LeasedJob?>(null), _ => Task.CompletedTask));
        Assert.Equal(0, pool.Running);
        await Assert.ThrowsAsync<InvalidOperationException>(() => WorkerService.LeaseThroughPoolAsync(pool, "background", true, () => throw new InvalidOperationException(), _ => Task.CompletedTask));
        Assert.Equal(0, pool.Running);
        var ran = false;
        var run = await WorkerService.LeaseThroughPoolAsync(pool, "background", true, () => Task.FromResult<LeasedJob?>(Job()), _ => { ran = true; Assert.Equal(1, pool.Running); return Task.CompletedTask; });
        Assert.NotNull(run);
        Assert.Equal(1, pool.Running);
        await run();
        Assert.True(ran);
        Assert.Equal(0, pool.Running);
        // No room: the queue is not even asked.
        var asked = false;
        Assert.Null(await WorkerService.LeaseThroughPoolAsync(pool, "background", false, () => { asked = true; return Task.FromResult<LeasedJob?>(Job()); }, _ => Task.CompletedTask));
        Assert.False(asked);
        // Without a pool (legacy model) every slot leases as before.
        Assert.NotNull(await WorkerService.LeaseThroughPoolAsync(null, "background", false, () => Task.FromResult<LeasedJob?>(Job()), _ => Task.CompletedTask));
    }

    [Fact]
    public void TheLaneModelIsFlexibleUnlessSetToLegacy()
    {
        // Flexible by default since 23 Nov 2026 (owner's go); legacy is the way back, for the worker and the api alike.
        Assert.True(LaneModelSettings.From(new ConfigurationBuilder().Build()).Flexible);
        Assert.True(WorkerLaneModel.IsFlexible(null));
        Assert.False(WorkerLaneModel.IsFlexible(Config(("Worker:LaneModel", " Legacy "))));
        Assert.True(BulkheadSettings.From(new ConfigurationBuilder().Build(), LaneModelSettings.From(new ConfigurationBuilder().Build()).Flexible).For("pazarama-orders", 2)!.ClassCaps.All(cap => cap == 1));
        Assert.False(LaneModelSettings.From(Config(("Worker:LaneModel", "legacy"))).Flexible);
        var flexible = LaneModelSettings.From(Config(("Worker:LaneModel", " Flexible ")));
        Assert.True(flexible.Flexible);
        Assert.Equal(["user", "hot", "hepsiburada-orders", "pazarama-orders", "pttavm-orders", "hepsiburada-status", "order-status"], flexible.ProtectedLanes);
        Assert.Empty(flexible.BorrowLimits);
        Assert.Equal(4, LaneModelSettings.From(Config(("Worker:LaneModel", "flexible"), ("Worker:FlexibleLanes:BorrowLimits:product-catalog", "4"), ("Worker:FlexibleLanes:BorrowLimits:background", "x"))).BorrowLimits.Single().Value);
        Assert.Equal(["user"], LaneModelSettings.From(Config(("Worker:LaneModel", "flexible"), ("Worker:FlexibleLanes:ProtectedLanes", " user , "))).ProtectedLanes);
        // With flexible lanes a lane may run more than its own slot count, so the marketplace classes take the isolation of one
        // dedicated slot by default (per tenant and marketplace, priority 0..hot ceiling); the legacy model leaves them off.
        var flexibleBulkhead = BulkheadSettings.From(new ConfigurationBuilder().Build(), flexibleLanes: true).For("pazarama-orders", 2)!;
        Assert.Equal(WorkerBulkheads.DefaultClasses.Select(item => item.Name), flexibleBulkhead.ClassNames);
        Assert.All(flexibleBulkhead.ClassCaps, cap => Assert.Equal(1, cap));
        Assert.True(BulkheadSettings.From(new ConfigurationBuilder().Build()).For("pazarama-orders", 2)!.IsEmpty);
        Assert.True(BulkheadSettings.From(Config(("Worker:Bulkhead:Classes:pazarama-orders", "0")), flexibleLanes: true).For("hot", 2)!.ClassNames.All(name => name != "pazarama-orders"));
        // The pool is as big as the lanes' slots add up to (today 20).
        Assert.Equal(WorkerLanes.All().Sum(lane => lane.DefaultSlots), new LaneSlotPool(WorkerLanes.All().ToDictionary(lane => lane.Name, lane => lane.DefaultSlots), flexible.ProtectedLanes).Total);
    }

    [Fact]
    public void ALaneThatOnlyPollsIdlyDoesNotHoldTheNextFreeSlotUp()
    {
        var clock = new ManualClock();
        var pool = new LaneSlotPool(new Dictionary<string, int> { ["product-catalog"] = 1, ["background"] = 1, ["returns-read"] = 1 }, [], clock: clock);
        Assert.True(pool.TryClaim("product-catalog", true));
        Assert.True(pool.TryClaim("product-catalog", false));
        Assert.True(pool.TryClaim("product-catalog", false));
        // The returns lane polls without known work: not "wanting", so the catalog may take the freed slot again.
        Assert.False(pool.TryClaim("returns-read", true));
        Assert.False(pool.Wants("returns-read"));
        pool.Release("product-catalog", false);
        Assert.True(pool.TryClaim("product-catalog", false));
        // A notification named a job the background lane can lease: now it wants its slot, for a minute.
        pool.NoteWork("background");
        Assert.False(pool.TryClaim("background", true));
        Assert.True(pool.Wants("background"));
        pool.Release("product-catalog", false);
        Assert.False(pool.TryClaim("product-catalog", false));
        Assert.True(pool.TryClaim("background", true));
        // A notice older than the minute, or a poll that found nothing, no longer counts.
        pool.Release("background", true);
        Assert.True(pool.TryClaim("product-catalog", false));
        pool.NoteWork("background");
        clock.Advance(LaneSlotPool.WorkNoticeTtl + TimeSpan.FromSeconds(1));
        Assert.False(pool.TryClaim("background", true));
        Assert.False(pool.Wants("background"));
        pool.NoteWork("returns-read");
        pool.NoteNoWork("returns-read");
        Assert.False(pool.TryClaim("returns-read", true));
        Assert.False(pool.Wants("returns-read"));
    }

    [Fact]
    public void AnOrderLaneNeverLendsAndItsOwnerNeverWaitsBehindALongBorrowedJob()
    {
        var protectedLanes = LaneModelSettings.From(Config(("Worker:LaneModel", "flexible"))).ProtectedLanes;
        var pool = new LaneSlotPool(new Dictionary<string, int> { ["pazarama-orders"] = 1, ["product-catalog"] = 1, ["background"] = 1 }, protectedLanes);
        Assert.True(pool.TryClaim("product-catalog", true));
        // Hours of catalog reads borrow every slot they may: the background slot, never the Pazarama order slot.
        Assert.True(pool.TryClaim("product-catalog", false));
        Assert.False(pool.TryClaim("product-catalog", false));
        // A new Pazarama order read takes its slot at once.
        Assert.True(pool.TryClaim("pazarama-orders", true));
        // The background lane lent its slot to a long read: its job waits for the next job to end, then gets the slot first.
        pool.NoteWork("background");
        Assert.False(pool.TryClaim("background", true));
        // The Pazarama slot frees up: it is not the background lane's to take (order lanes never lend), it waits on.
        pool.Release("pazarama-orders", true);
        Assert.False(pool.TryClaim("background", true));
        // The long catalog read on its slot ends: the slot goes back to the background lane, not to another catalog read.
        pool.Release("product-catalog", false);
        Assert.False(pool.TryClaim("product-catalog", false));
        Assert.True(pool.TryClaim("background", true));
    }

    [Fact]
    public void ABorrowLimitCapsTheLanesBorrowedSlots()
    {
        var pool = new LaneSlotPool(new Dictionary<string, int> { ["product-catalog"] = 1, ["background"] = 3 }, [], new Dictionary<string, int> { ["product-catalog"] = 1 });
        Assert.True(pool.TryClaim("product-catalog", true));
        Assert.True(pool.TryClaim("product-catalog", false));
        Assert.False(pool.TryClaim("product-catalog", false));
        Assert.Equal(1, pool.Borrowed("product-catalog"));
    }

    [Fact]
    public async Task ManyThreadsClaimingAndReleasingNeverRunMoreThanThePoolNorTakeTheProtectedSlots()
    {
        var pool = new LaneSlotPool(new Dictionary<string, int> { ["hot"] = 2, ["user"] = 1, ["product-catalog"] = 2, ["background"] = 2, ["returns-read"] = 1 }, ["hot", "user"]);
        string[] lanes = ["hot", "user", "product-catalog", "background", "returns-read"];
        var active = 0;
        var peak = 0;
        var claims = 0;
        var breaches = 0;
        await Task.WhenAll(Enumerable.Range(0, 8).Select(thread => Task.Run(() =>
        {
            var random = new Random(thread);
            var held = new List<(string Lane, bool Own)>();
            for (var step = 0; step < 20_000; step++)
            {
                if (held.Count > 0 && random.Next(2) == 0)
                {
                    var index = random.Next(held.Count);
                    var (heldLane, heldOwn) = held[index];
                    held.RemoveAt(index);
                    Interlocked.Decrement(ref active);
                    pool.Release(heldLane, heldOwn);
                    continue;
                }
                var lane = lanes[random.Next(lanes.Length)];
                var ownSlot = random.Next(2) == 0;
                if (random.Next(3) == 0) pool.NoteWork(lane);
                if (!pool.TryClaim(lane, ownSlot)) continue;
                held.Add((lane, ownSlot));
                var now = Interlocked.Increment(ref active);
                int seen;
                while ((seen = Volatile.Read(ref peak)) < now && Interlocked.CompareExchange(ref peak, now, seen) != seen) { }
                Interlocked.Increment(ref claims);
                // The unprotected lanes never run more than the pool less the protected lanes' slots.
                if (pool.UnprotectedRunning > pool.UnprotectedCapacity) Interlocked.Increment(ref breaches);
            }
            foreach (var (heldLane, heldOwn) in held)
            {
                Interlocked.Decrement(ref active);
                pool.Release(heldLane, heldOwn);
            }
        })));
        Assert.True(claims > 0);
        Assert.True(peak <= pool.Total, $"peak {peak} > {pool.Total}");
        Assert.Equal(0, breaches);
        Assert.Equal(0, pool.Running);
        Assert.All(lanes, lane => Assert.Equal(0, pool.Borrowed(lane)));
    }

    [Fact]
    public void AProtectedLaneAlwaysGetsItsOwnSlotsAtOnceWhateverTheOtherLanesDid()
    {
        var slots = new Dictionary<string, int> { ["hot"] = 2, ["user"] = 1, ["product-catalog"] = 2, ["background"] = 2, ["returns-read"] = 1 };
        var protectedLanes = new HashSet<string> { "hot", "user" };
        var pool = new LaneSlotPool(slots, protectedLanes);
        var random = new Random(7);
        var held = new List<(string Lane, bool Own)>();
        var ownHeld = slots.Keys.ToDictionary(lane => lane, _ => 0);
        for (var step = 0; step < 50_000; step++)
        {
            if (held.Count > 0 && random.Next(3) == 0)
            {
                var index = random.Next(held.Count);
                var (lane, own) = held[index];
                held.RemoveAt(index);
                if (own) ownHeld[lane]--;
                pool.Release(lane, own);
                continue;
            }
            var claimLane = slots.Keys.ElementAt(random.Next(slots.Count));
            var ownSlot = random.Next(2) == 0;
            if (random.Next(2) == 0) pool.NoteWork(claimLane);
            var claimed = pool.TryClaim(claimLane, ownSlot);
            if (ownSlot && protectedLanes.Contains(claimLane) && ownHeld[claimLane] < slots[claimLane])
                Assert.True(claimed, $"protected {claimLane} refused its own slot at step {step}");
            if (!claimed) continue;
            held.Add((claimLane, ownSlot));
            if (ownSlot) ownHeld[claimLane]++;
            Assert.True(pool.UnprotectedRunning <= pool.UnprotectedCapacity);
        }
    }

    [Fact]
    public void ALenderTakingItsSlotBackNeverTakesTheIdleSlotsOfTheOrderLanes()
    {
        // Live lanes and slots (20), order lanes protected (10). "Pull every store's products": seven catalog reads.
        var pool = new LaneSlotPool(WorkerLanes.All().ToDictionary(lane => lane.Name, lane => lane.DefaultSlots),
            LaneModelSettings.From(new ConfigurationBuilder().Build()).ProtectedLanes);
        for (var own = 0; own < 3; own++) Assert.True(pool.TryClaim("product-catalog", true));
        var borrowed = 0;
        while (pool.TryClaim("product-catalog", false)) borrowed++;
        Assert.Equal(7, borrowed);
        Assert.Equal(10, pool.Running);
        // Hot and the user lane are idle. Background, returns and first imports now want their own slots back: none may take
        // the order lanes' idle slots, they wait for a catalog read to end.
        foreach (var lane in new[] { "background", "returns-read", "initial-data-sync" })
        {
            pool.NoteWork(lane);
            Assert.False(pool.TryClaim(lane, true));
            Assert.True(pool.Wants(lane));
        }
        Assert.Equal(10, pool.Running);
        // Every order lane, the user lane and hot get all their slots at once.
        foreach (var (lane, slots) in new[] { ("user", 2), ("hot", 3), ("hepsiburada-orders", 1), ("pazarama-orders", 1), ("pttavm-orders", 1), ("hepsiburada-status", 1), ("order-status", 1) })
            for (var slot = 0; slot < slots; slot++) Assert.True(pool.TryClaim(lane, true), lane);
        Assert.Equal(20, pool.Running);
        // A catalog read ends: the freed slot goes to a lane that wants it back, not to another catalog read.
        pool.Release("product-catalog", false);
        Assert.False(pool.TryClaim("product-catalog", false));
        Assert.True(pool.TryClaim("background", true));
    }

    // Every interleaving of a small pool (hot 2 and user 1 protected, catalog 2 and background 1 not; 6 slots), explored
    // breadth first over every reachable state: any own or borrowing slot claiming or ending, a lane told it has work or
    // finding none. Each slot is bounded as on live (a lane's own slots, and pool less own borrowing slots).
    //   - A protected lane with an idle own slot gets it at once, whatever ran before (Y1, 23 Nov scan: lenders taking
    //     their slots back used to fill the order lanes' idle slots, so a new order read waited for the catalog reads).
    //   - The unprotected lanes never run more than the pool less the protected slots.
    //   - Mirror: an unprotected lane that wants its slot back is never overtaken by a borrower, and gets its slot at the
    //     first end of an unprotected or borrowed job (no starvation behind borrowers while the protected slots stay idle).
    [Fact]
    public void EveryInterleavingKeepsTheProtectedSlotsAndNeverStarvesALenderBehindBorrowers()
    {
        string[] lanes = ["hot", "user", "product-catalog", "background"];
        int[] slots = [2, 1, 2, 1];
        bool[] isProtected = [true, true, false, false];
        var total = slots.Sum();
        var start = new PoolState(new int[4], new int[4], new bool[4], new bool[4], new bool[4]);
        var seen = new HashSet<string> { start.Key };
        var queue = new Queue<(PoolState State, List<PoolStep> Path)>();
        queue.Enqueue((start, []));
        var explored = 0;
        while (queue.Count > 0)
        {
            var (state, path) = queue.Dequeue();
            explored++;
            foreach (var step in Steps(state))
            {
                var (pool, own, borrowed, noticed) = Replay(path);
                var result = Apply(pool, own, borrowed, noticed, step);
                var where = $"{string.Join(", ", path)} then {step} = {result}";
                if (step.Kind == StepKind.ClaimOwn && isProtected[step.Lane])
                    Assert.True(result, $"protected {lanes[step.Lane]} refused its idle own slot: {where}");
                if (step.Kind == StepKind.ClaimBorrow && state.Quiet[step.Lane])
                    Assert.False(result, $"{lanes[step.Lane]} borrowed although its last poll found nothing: {where}");
                if (step.Kind == StepKind.ClaimBorrow && result)
                    for (var lane = 0; lane < 4; lane++)
                        if (lane != step.Lane && pool.Wants(lanes[lane]) && own[lane] < slots[lane])
                            Assert.True(pool.Running + (slots[lane] - own[lane]) <= total, $"a borrower took the slot {lanes[lane]} wants back: {where}");
                Assert.True(pool.Running <= total, where);
                Assert.True(pool.UnprotectedRunning <= pool.UnprotectedCapacity, $"unprotected lanes ran into the protected slots: {where}");
                // Mirror: a lender that wants its slot back gets it when an unprotected or borrowed job ends.
                if (step.Kind is StepKind.ReleaseBorrow || (step.Kind == StepKind.ReleaseOwn && !isProtected[step.Lane]))
                    for (var lane = 0; lane < 4; lane++)
                        if (!isProtected[lane] && own[lane] < slots[lane] && state.Wanting[lane])
                        {
                            var (probe, probeOwn, probeBorrowed, probeNoticed) = Replay([.. path, step]);
                            Assert.True(Apply(probe, probeOwn, probeBorrowed, probeNoticed, new PoolStep(StepKind.ClaimOwn, lane)),
                                $"{lanes[lane]} wanted its slot back and did not get it after {where}");
                        }
                var next = new PoolState(own, borrowed, noticed, [.. lanes.Select(pool.Wants)], [.. lanes.Select(pool.IsQuiet)]);
                if (seen.Add(next.Key)) queue.Enqueue((next, [.. path, step]));
            }
        }
        Assert.True(explored > 1000, $"only {explored} states explored");

        IEnumerable<PoolStep> Steps(PoolState state)
        {
            for (var lane = 0; lane < 4; lane++)
            {
                if (state.Own[lane] < slots[lane]) yield return new PoolStep(StepKind.ClaimOwn, lane);
                if (state.Borrowed[lane] < total - slots[lane]) yield return new PoolStep(StepKind.ClaimBorrow, lane);
                if (state.Own[lane] > 0) yield return new PoolStep(StepKind.ReleaseOwn, lane);
                if (state.Borrowed[lane] > 0) yield return new PoolStep(StepKind.ReleaseBorrow, lane);
                // A slot finding a job or a notification (NoteWork), a poll finding nothing (NoteNoWork): known work makes an
                // unprotected lane want its slot, a poll that found nothing stops the lane borrowing.
                if (!state.Noticed[lane] || state.Quiet[lane]) yield return new PoolStep(StepKind.NoteWork, lane);
                if (state.Noticed[lane] || state.Wanting[lane] || !state.Quiet[lane]) yield return new PoolStep(StepKind.NoteNoWork, lane);
            }
        }

        (LaneSlotPool Pool, int[] Own, int[] Borrowed, bool[] Noticed) Replay(List<PoolStep> path)
        {
            var pool = new LaneSlotPool(lanes.Select((lane, index) => (lane, index)).ToDictionary(item => item.lane, item => slots[item.index]),
                [.. lanes.Where((_, index) => isProtected[index])], clock: new ManualClock());
            var own = new int[4];
            var borrowed = new int[4];
            var noticed = new bool[4];
            foreach (var step in path) Apply(pool, own, borrowed, noticed, step);
            return (pool, own, borrowed, noticed);
        }

        bool Apply(LaneSlotPool pool, int[] own, int[] borrowed, bool[] noticed, PoolStep step)
        {
            var lane = lanes[step.Lane];
            switch (step.Kind)
            {
                case StepKind.ClaimOwn:
                    if (!pool.TryClaim(lane, true)) return false;
                    own[step.Lane]++;
                    return true;
                case StepKind.ClaimBorrow:
                    if (!pool.TryClaim(lane, false)) return false;
                    borrowed[step.Lane]++;
                    return true;
                case StepKind.ReleaseOwn:
                    pool.Release(lane, true);
                    own[step.Lane]--;
                    return true;
                case StepKind.ReleaseBorrow:
                    pool.Release(lane, false);
                    borrowed[step.Lane]--;
                    return true;
                case StepKind.NoteWork:
                    pool.NoteWork(lane);
                    noticed[step.Lane] = true;
                    return true;
                default:
                    pool.NoteNoWork(lane);
                    noticed[step.Lane] = false;
                    return true;
            }
        }
    }

    private enum StepKind { ClaimOwn, ClaimBorrow, ReleaseOwn, ReleaseBorrow, NoteWork, NoteNoWork }

    private sealed record PoolStep(StepKind Kind, int Lane)
    {
        public override string ToString() => $"{Kind}({Lane})";
    }

    private sealed record PoolState(int[] Own, int[] Borrowed, bool[] Noticed, bool[] Wanting, bool[] Quiet)
    {
        public string Key => $"{string.Join(',', Own)}|{string.Join(',', Borrowed)}|{string.Join(',', Noticed)}|{string.Join(',', Wanting)}|{string.Join(',', Quiet)}";
    }

    [Fact]
    public void ANotificationWakesTheLanesBorrowingSlotsToo()
    {
        var router = new JobWakeRouter(WorkerLanes.All());
        var borrow = router.BorrowSignalFor("hot").Register();
        var own = router.SignalFor("hot").Register();
        router.Route(JobWakeRouter.Payload(MarketplaceHub.Application.MarketplaceJobTypes.WebhookIngest, 0));
        Assert.True(borrow.Woken.IsCompleted);
        Assert.True(own.Woken.IsCompleted);
        var other = router.BorrowSignalFor("background").Register();
        router.WakeAll();
        Assert.True(other.Woken.IsCompleted);
    }

    [Fact]
    public void ABorrowingSlotsNameReportsItsLane()
    {
        Assert.Equal("hot", WorkerService.LaneName("hot+12"));
        Assert.Equal("hot", WorkerService.LaneName("hot#2"));
        Assert.Equal("product-catalog", WorkerService.LaneName("product-catalog"));
    }

    [Fact]
    public void ANotificationTellsThePoolWhichLanesHaveWork()
    {
        var router = new JobWakeRouter(WorkerLanes.All());
        var noted = new List<string>();
        router.LaneNotified += noted.Add;
        router.Route(JobWakeRouter.Payload(MarketplaceHub.Application.MarketplaceJobTypes.PazaramaOrderSync, 0));
        Assert.Equal(["pazarama-orders"], noted);
        router.Route(JobWakeRouter.Freed);
        router.WakeAll();
        Assert.Equal(["pazarama-orders"], noted);
    }

    [Fact]
    public void AJobThatBecameDueMakesItsLaneWantItsLentSlotBack()
    {
        // Live lanes; seven catalog reads fill the unprotected part (three own, seven borrowed). A background retry's time
        // comes: no notification names it, the due-time wake tells its lane (NoteDue) before it wakes every slot.
        var router = new JobWakeRouter(WorkerLanes.All());
        var pool = new LaneSlotPool(WorkerLanes.All().ToDictionary(lane => lane.Name, lane => lane.DefaultSlots),
            LaneModelSettings.From(new ConfigurationBuilder().Build()).ProtectedLanes);
        var noted = new List<string>();
        router.LaneNotified += noted.Add;
        router.LaneNotified += pool.NoteWork;
        for (var own = 0; own < 3; own++) Assert.True(pool.TryClaim("product-catalog", true));
        while (pool.TryClaim("product-catalog", false)) { }
        Assert.Equal(7, pool.Borrowed("product-catalog"));

        router.NoteDue([("TEST_DUE_RETRY", MarketplaceHub.Application.JobPriorityPolicy.Reconciliation)]);
        Assert.Equal(["background"], noted);
        Assert.False(pool.TryClaim("background", true));
        Assert.True(pool.Wants("background"));
        // The next catalog read to end hands its slot to the background lane, not to another catalog read.
        pool.Release("product-catalog", false);
        Assert.False(pool.TryClaim("product-catalog", false));
        Assert.True(pool.TryClaim("background", true));
        // Nothing due tells no one; jobs of several lanes tell each lane once.
        noted.Clear();
        router.NoteDue([]);
        Assert.Empty(noted);
        router.NoteDue([("TEST_DUE_RETRY", 4), ("TEST_DUE_OTHER", 3), (MarketplaceHub.Application.MarketplaceJobTypes.PazaramaOrderSync, 0)]);
        Assert.Equal(["pazarama-orders", "background"], noted);
    }

    [Fact]
    public void ALaneWhoseLastPollFoundNothingBorrowsNothingUntilItHasWorkAgain()
    {
        var pool = new LaneSlotPool(new Dictionary<string, int> { ["pazarama-orders"] = 1, ["returns-read"] = 2 }, ["pazarama-orders"]);
        // The Pazarama slot is polling (its own slot counts as busy): its poll found nothing, so its borrowing slots, woken by
        // every freed slot, take no slot to poll the same empty queue.
        Assert.True(pool.TryClaim("pazarama-orders", true));
        pool.NoteNoWork("pazarama-orders");
        Assert.True(pool.IsQuiet("pazarama-orders"));
        Assert.False(pool.TryClaim("pazarama-orders", false));
        // A notification names a Pazarama job (or a slot finds one): it may borrow again.
        pool.NoteWork("pazarama-orders");
        Assert.True(pool.TryClaim("pazarama-orders", false));
        Assert.Equal(1, pool.Borrowed("pazarama-orders"));
    }

    [Fact]
    public void ALaneThatFoundNothingForTwoMinutesTakesTheNextFreeSlotForOnePoll()
    {
        // A job no notification announced (missed at startup or on a listener reconnect) while catalog reads fill the
        // unprotected part: the return lane polls idly, so it does not want its slot at once, but two minutes after its last
        // empty poll it does, and the next freed slot is its own for one poll.
        var clock = new ManualClock();
        var pool = new LaneSlotPool(new Dictionary<string, int> { ["product-catalog"] = 1, ["returns-read"] = 1 }, [], clock: clock);
        Assert.True(pool.TryClaim("product-catalog", true));
        Assert.True(pool.TryClaim("product-catalog", false));
        pool.NoteNoWork("returns-read");
        clock.Advance(TimeSpan.FromSeconds(30));
        Assert.False(pool.TryClaim("returns-read", true));
        Assert.False(pool.Wants("returns-read"));
        clock.Advance(LaneSlotPool.IdleProbeAfter);
        Assert.False(pool.TryClaim("returns-read", true));
        Assert.True(pool.Wants("returns-read"));
        pool.Release("product-catalog", false);
        Assert.False(pool.TryClaim("product-catalog", false));
        Assert.True(pool.TryClaim("returns-read", true));
        // It found nothing: the next two minutes it holds nothing up again.
        pool.NoteNoWork("returns-read");
        pool.Release("returns-read", true);
        pool.NoteWork("product-catalog");
        Assert.True(pool.TryClaim("product-catalog", false));
        Assert.False(pool.TryClaim("returns-read", true));
        Assert.False(pool.Wants("returns-read"));
    }

    [Fact]
    public async Task AnEmptyPollWakesNoOneAndOnlyAJobThatRanFreesASlotForTheBusyLanes()
    {
        // 24 Nov: every empty poll's release woke the lanes whose own slots were all busy, and those are mostly busy because
        // they poll too: each empty poll woke the next ones, and every idle wait was cut back to one second.
        var pool = new LaneSlotPool(new Dictionary<string, int> { ["hot"] = 1, ["returns-read"] = 1, ["background"] = 1 }, ["hot"]);
        var freed = new List<IReadOnlyList<string>>();
        pool.SlotFreed += lanes => freed.Add(lanes);
        Assert.True(pool.TryClaim("hot", true));
        Assert.Null(await WorkerService.LeaseThroughPoolAsync(pool, "returns-read", true, () => Task.FromResult<LeasedJob?>(null), _ => Task.CompletedTask));
        await Assert.ThrowsAsync<InvalidOperationException>(() => WorkerService.LeaseThroughPoolAsync(pool, "background", true, () => throw new InvalidOperationException(), _ => Task.CompletedTask));
        Assert.Empty(freed);
        Assert.Equal(1, pool.Running);
        // A job that ran ends: the busy hot lane is told, as before.
        var run = await WorkerService.LeaseThroughPoolAsync(pool, "background", true, () => Task.FromResult<LeasedJob?>(Job()), _ => Task.CompletedTask);
        await run!();
        Assert.Equal(["hot"], Assert.Single(freed));
        // A lane with known work that found no room is woken by the next release, an empty poll's too (and only once).
        var small = new LaneSlotPool(new Dictionary<string, int> { ["hot"] = 1, ["product-catalog"] = 1, ["background"] = 1 }, []);
        var smallFreed = new List<IReadOnlyList<string>>();
        small.SlotFreed += lanes => smallFreed.Add(lanes);
        Assert.True(small.TryClaim("hot", true));
        Assert.True(small.TryClaim("product-catalog", true));
        Assert.True(small.TryClaim("background", true));
        small.NoteWork("hot");
        Assert.False(small.TryClaim("hot", false));
        small.Release("background", true, ranJob: false);
        Assert.Equal(["hot"], Assert.Single(smallFreed));
        Assert.True(small.TryClaim("background", true));
        small.Release("background", true, ranJob: false);
        Assert.Single(smallFreed);
    }

    [Fact]
    public async Task ANotificationDuringAnEmptyPollIsNotLostWhateverTheOrder()
    {
        // The poll read the queue before the job's row was committed, the notification came while it ran: its stale "nothing"
        // must not mark the lane quiet, or the lane's borrowing slot (woken by the notification) takes no slot and the job waits
        // for an own slot (hot 3/3 running hour-long reads). Two threads, the order forced: poll starts, notification, poll ends.
        var pool = new LaneSlotPool(new Dictionary<string, int> { ["hot"] = 1, ["background"] = 2 }, []);
        Assert.True(pool.TryClaim("hot", true));
        var polled = new SemaphoreSlim(0);
        var notified = new SemaphoreSlim(0);
        var borrowPoll = Task.Run(() => WorkerService.LeaseThroughPoolAsync(pool, "hot", false, async () =>
        {
            polled.Release();
            await notified.WaitAsync();
            return null;
        }, _ => Task.CompletedTask));
        var notification = Task.Run(async () =>
        {
            await polled.WaitAsync();
            pool.NoteWork("hot");
            notified.Release();
        });
        await Task.WhenAll(borrowPoll, notification);
        Assert.Null(await borrowPoll);
        Assert.False(pool.IsQuiet("hot"));
        Assert.True(pool.TryClaim("hot", false));
        // The other order (the notification before the poll began): the poll's "nothing" stands, the lane is quiet.
        var later = new LaneSlotPool(new Dictionary<string, int> { ["hot"] = 1, ["background"] = 2 }, []);
        Assert.True(later.TryClaim("hot", true));
        later.NoteWork("hot");
        Assert.Null(await WorkerService.LeaseThroughPoolAsync(later, "hot", false, () => Task.FromResult<LeasedJob?>(null), _ => Task.CompletedTask));
        Assert.True(later.IsQuiet("hot"));
        Assert.False(later.TryClaim("hot", false));
        // A stale answer changes nothing; a current one does.
        var epoch = later.Epoch("hot");
        later.NoteWork("hot");
        Assert.False(later.NoteNoWork("hot", epoch));
        Assert.True(later.NoteNoWork("hot", later.Epoch("hot")));
    }

    [Fact]
    public void ALaneIsQuietForFifteenSecondsAtMostAndNotAfterAJobEndsOrAHoldEnds()
    {
        // 24 Nov review: quiet lasted until a notification named the lane's job, but a job becomes leasable in other ways too.
        // The hot lane's three own slots run jobs, a webhook is notified, the borrowing slot's poll skips it (its connection's
        // lock group runs in another lane) and the lane went quiet for good: the webhook waited for one of hot's own jobs.
        var clock = new ManualClock();
        var pool = new LaneSlotPool(new Dictionary<string, int> { ["hot"] = 3, ["product-catalog"] = 1, ["background"] = 4 }, ["hot"], clock: clock);
        for (var slot = 0; slot < 3; slot++) Assert.True(pool.TryClaim("hot", true));
        Assert.True(pool.TryClaim("product-catalog", true));
        pool.NoteWork("hot");
        Assert.True(pool.TryClaim("hot", false));
        Assert.True(pool.NoteNoWork("hot", pool.Epoch("hot")));
        pool.Release("hot", false, ranJob: false);
        Assert.False(pool.TryClaim("hot", false));
        // The catalog job that held the lock group ends: the webhook is leased on the woken borrowing slot at once.
        pool.Release("product-catalog", true, ranJob: true);
        Assert.True(pool.TryClaim("hot", false));
        pool.Release("hot", false, ranJob: true);
        // A bulkhead or lock group freed by a job of another worker or the api: the "freed" notification ends the quiet.
        var router = new JobWakeRouter(WorkerLanes.All());
        var live = new LaneSlotPool(WorkerLanes.All().ToDictionary(lane => lane.Name, lane => lane.DefaultSlots), [], clock: clock);
        router.JobEnded += live.Unquiet;
        for (var slot = 0; slot < WorkerLanes.All().Single(lane => lane.Name == "hot").DefaultSlots; slot++) Assert.True(live.TryClaim("hot", true));
        live.NoteNoWork("hot");
        Assert.False(live.TryClaim("hot", false));
        router.Route(JobWakeRouter.Freed);
        Assert.True(live.TryClaim("hot", false));
        // Nothing tells the lane (a missed notification on a listener reconnect, a hold that ended by time): 15 s at most.
        Assert.True(pool.TryClaim("hot", false));
        pool.NoteNoWork("hot");
        pool.Release("hot", false, ranJob: false);
        clock.Advance(LaneSlotPool.QuietFor - TimeSpan.FromSeconds(1));
        Assert.False(pool.TryClaim("hot", false));
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(pool.TryClaim("hot", false));
        // The end of a quota hold ends it at once.
        pool.NoteNoWork("hot");
        pool.Release("hot", false, ranJob: false);
        Assert.False(pool.TryClaim("hot", false));
        pool.QuotaReleased();
        Assert.True(pool.TryClaim("hot", false));
    }

    [Fact]
    public async Task ABorrowingSlotDoesNotReadTheQueueWhileTheLanesOwnSlotPollsItWithNoKnownWork()
    {
        // 25 Nov: the quiet ends when the own slot's 15 s idle wait ends, so a borrowing slot whose idle wait ended at the same
        // moment found the lane not quiet and its own slot busy with that poll, and read the same empty queue.
        var clock = new ManualClock();
        var pool = new LaneSlotPool(new Dictionary<string, int> { ["hot"] = 1, ["background"] = 2 }, [], clock: clock);
        Assert.Null(await WorkerService.LeaseThroughPoolAsync(pool, "hot", true, () => Task.FromResult<LeasedJob?>(null), _ => Task.CompletedTask));
        clock.Advance(LaneSlotPool.QuietFor);
        Assert.False(pool.IsQuiet("hot"));
        bool? borrowed = null;
        Assert.Null(await WorkerService.LeaseThroughPoolAsync(pool, "hot", true, () =>
        {
            borrowed = pool.TryClaim("hot", false);
            return Task.FromResult<LeasedJob?>(null);
        }, _ => Task.CompletedTask));
        Assert.False(borrowed);
        Assert.True(pool.IsQuiet("hot"));
        Assert.Null(pool.Violation());

        // Known work: a notification during the own slot's poll lets the lane borrow at once, as before.
        clock.Advance(LaneSlotPool.QuietFor);
        var run = await WorkerService.LeaseThroughPoolAsync(pool, "hot", true, () =>
        {
            pool.NoteWork("hot");
            borrowed = pool.TryClaim("hot", false);
            return Task.FromResult<LeasedJob?>(new LeasedJob(Guid.NewGuid(), Guid.NewGuid(), null, "TEST", "{}", "test", "lease-token"));
        }, _ => Task.CompletedTask);
        Assert.True(borrowed);
        pool.Release("hot", false, ranJob: false);
        // The own slot's poll found a job: its borrowing slots may lease while it runs.
        Assert.True(pool.TryClaim("hot", false));
        pool.Release("hot", false, ranJob: false);
        await run!();
        Assert.Equal(0, pool.Running);
        Assert.Null(pool.Violation());
    }

    [Fact]
    public async Task ABorrowingSlotWokenForAJobItsLanesOwnSlotFoundMayBorrowAtOnce()
    {
        // The own slot found a job no notification named, on a quiet lane: it wakes a borrowing slot (leased) only once the pool
        // knows the work and its poll ended, else the woken slot was refused and the wake lost.
        var pool = new LaneSlotPool(new Dictionary<string, int> { ["hot"] = 1, ["background"] = 2 }, []);
        Assert.Null(await WorkerService.LeaseThroughPoolAsync(pool, "hot", true, () => Task.FromResult<LeasedJob?>(null), _ => Task.CompletedTask));
        Assert.True(pool.IsQuiet("hot"));
        bool? borrowed = null;
        var run = await WorkerService.LeaseThroughPoolAsync(pool, "hot", true,
            () => Task.FromResult<LeasedJob?>(new LeasedJob(Guid.NewGuid(), Guid.NewGuid(), null, "TEST", "{}", "test", "lease-token")),
            _ => Task.CompletedTask, leased: () => borrowed = pool.TryClaim("hot", false));
        Assert.True(borrowed);
        pool.Release("hot", false, ranJob: false);
        await run!();
        Assert.Equal(0, pool.Running);
        Assert.Null(pool.Violation());
    }

    [Fact]
    public async Task AHoldOrAJobEndingDuringAnEmptyPollLeavesTheLaneNotQuiet()
    {
        // The poll's snapshot may predate what the end made leasable: its "nothing" is stale (the lane epoch moved on).
        foreach (var end in new Action<LaneSlotPool>[] { pool => pool.QuotaReleased(), pool => pool.Release("background", true, ranJob: true), pool => pool.Unquiet() })
        {
            var pool = new LaneSlotPool(new Dictionary<string, int> { ["hot"] = 1, ["background"] = 2 }, []);
            Assert.True(pool.TryClaim("hot", true));
            Assert.True(pool.TryClaim("background", true));
            Assert.Null(await WorkerService.LeaseThroughPoolAsync(pool, "hot", false, () =>
            {
                end(pool);
                return Task.FromResult<LeasedJob?>(null);
            }, _ => Task.CompletedTask));
            Assert.False(pool.IsQuiet("hot"));
            Assert.True(pool.TryClaim("hot", false));
        }
    }

    [Fact]
    public void AStarvedLaneStaysToBeWokenUntilItIsWoken()
    {
        // A borrower with known work refused for room, and a lane that wants its slot back: a job's end wakes only the lane that
        // wants its slot; the starved one is woken by the next release, not forgotten.
        var clock = new ManualClock();
        var pool = new LaneSlotPool(new Dictionary<string, int> { ["hot"] = 1, ["product-catalog"] = 1, ["background"] = 1 }, [], clock: clock);
        var freed = new List<IReadOnlyList<string>>();
        pool.SlotFreed += lanes => freed.Add(lanes);
        Assert.True(pool.TryClaim("hot", true));
        Assert.True(pool.TryClaim("product-catalog", true));
        Assert.True(pool.TryClaim("product-catalog", false));
        pool.NoteWork("hot");
        Assert.False(pool.TryClaim("hot", false));
        pool.NoteWork("background");
        Assert.False(pool.TryClaim("background", true));
        Assert.True(pool.Wants("background"));
        pool.Release("product-catalog", false, ranJob: true);
        Assert.Equal(["background"], freed[^1]);
        Assert.True(pool.TryClaim("background", true));
        pool.Release("background", true, ranJob: false);
        Assert.Equal(["hot"], freed[^1]);
    }

    [Fact]
    public async Task ManyThreadsOnTheLiveLanesKeepEveryInvariantAtEveryStep()
    {
        // The live lanes and slots (20) and protected lanes. Each protected lane's own slots are claimed by one thread only, so
        // that thread knows how many it holds: with fewer than the lane's slots its claim must succeed at once, every time.
        var slots = WorkerLanes.All().ToDictionary(lane => lane.Name, lane => lane.DefaultSlots);
        var protectedLanes = LaneModelSettings.From(new ConfigurationBuilder().Build()).ProtectedLanes;
        var pool = new LaneSlotPool(slots, protectedLanes);
        Assert.Equal(20, pool.Total);
        var lanes = slots.Keys.ToArray();
        var guarded = protectedLanes.ToArray();
        Assert.True(guarded.Length <= 8);
        var violations = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var claims = 0;
        await Task.WhenAll(Enumerable.Range(0, 8).Select(thread => Task.Run(() =>
        {
            var random = new Random(1000 + thread);
            var mine = thread < guarded.Length ? guarded[thread] : null;
            var held = new List<(string Lane, bool Own)>();
            for (var step = 0; step < 20_000; step++)
            {
                var action = random.Next(10);
                if (held.Count > 0 && action < 4)
                {
                    var index = random.Next(held.Count);
                    var (heldLane, heldOwn) = held[index];
                    held.RemoveAt(index);
                    pool.Release(heldLane, heldOwn, ranJob: random.Next(2) == 0);
                }
                else if (mine is not null && action == 4)
                {
                    var holding = held.Count(item => item.Lane == mine && item.Own);
                    if (holding < slots[mine])
                    {
                        if (pool.TryClaim(mine, true)) held.Add((mine, true));
                        else violations.Enqueue($"protected {mine} refused its idle own slot ({holding} of {slots[mine]} held)");
                    }
                }
                else if (action == 5)
                {
                    var lane = lanes[random.Next(lanes.Length)];
                    var epoch = pool.Epoch(lane);
                    if (random.Next(2) == 0) pool.NoteWork(lane);
                    pool.NoteNoWork(lane, epoch);
                }
                else if (action == 6)
                {
                    if (random.Next(20) == 0) pool.QuotaReleased();
                    else pool.NoteWork(lanes[random.Next(lanes.Length)]);
                }
                else
                {
                    // Own claims only for unprotected lanes (a protected lane's own slots belong to its thread).
                    var lane = lanes[random.Next(lanes.Length)];
                    var ownSlot = !protectedLanes.Contains(lane) && random.Next(2) == 0;
                    if (pool.TryClaim(lane, ownSlot))
                    {
                        held.Add((lane, ownSlot));
                        Interlocked.Increment(ref claims);
                    }
                }
                if (pool.Violation() is { } violation) violations.Enqueue($"thread {thread} step {step}: {violation}");
            }
            foreach (var (heldLane, heldOwn) in held) pool.Release(heldLane, heldOwn, ranJob: true);
        })));
        Assert.Empty(violations);
        Assert.True(claims > 1_000);
        // No slot leaks: every claim was given back.
        Assert.Equal(0, pool.Running);
        Assert.All(lanes, lane => Assert.Equal(0, pool.Borrowed(lane)));
        Assert.Null(pool.Violation());
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset now = new(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan by) => now += by;
    }

    private static LeasedJob Job() => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "TEST", "{}", "test", "lease-token");

    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(item => item.Key, item => (string?)item.Value)).Build();
}
