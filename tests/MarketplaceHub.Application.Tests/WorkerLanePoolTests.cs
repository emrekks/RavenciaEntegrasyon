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
        pool.Release("pazarama-orders", true);
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
    public async Task ManyThreadsClaimingAndReleasingNeverRunMoreThanThePool()
    {
        var pool = new LaneSlotPool(new Dictionary<string, int> { ["hot"] = 2, ["product-catalog"] = 2, ["background"] = 2, ["returns-read"] = 1 }, ["hot"]);
        string[] lanes = ["hot", "product-catalog", "background", "returns-read"];
        var active = 0;
        var peak = 0;
        var claims = 0;
        await Task.WhenAll(Enumerable.Range(0, 8).Select(thread => Task.Run(() =>
        {
            var random = new Random(thread);
            for (var step = 0; step < 20_000; step++)
            {
                var lane = lanes[random.Next(lanes.Length)];
                var ownSlot = random.Next(2) == 0;
                if (random.Next(3) == 0) pool.NoteWork(lane);
                if (!pool.TryClaim(lane, ownSlot)) continue;
                var now = Interlocked.Increment(ref active);
                int seen;
                while ((seen = Volatile.Read(ref peak)) < now && Interlocked.CompareExchange(ref peak, now, seen) != seen) { }
                Interlocked.Increment(ref claims);
                Interlocked.Decrement(ref active);
                pool.Release(lane, ownSlot);
            }
        })));
        Assert.True(claims > 0);
        Assert.True(peak <= pool.Total, $"peak {peak} > {pool.Total}");
        Assert.Equal(0, pool.Running);
        Assert.All(lanes, lane => Assert.Equal(0, pool.Borrowed(lane)));
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
