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
