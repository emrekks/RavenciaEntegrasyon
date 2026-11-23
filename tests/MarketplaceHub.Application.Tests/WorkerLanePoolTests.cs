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
    public void TheLaneModelIsLegacyUnlessSetToFlexible()
    {
        Assert.False(LaneModelSettings.From(new ConfigurationBuilder().Build()).Flexible);
        Assert.False(LaneModelSettings.From(Config(("Worker:LaneModel", "legacy"))).Flexible);
        var flexible = LaneModelSettings.From(Config(("Worker:LaneModel", " Flexible ")));
        Assert.True(flexible.Flexible);
        Assert.Equal(["user", "hot"], flexible.ProtectedLanes);
        Assert.Equal(["user"], LaneModelSettings.From(Config(("Worker:LaneModel", "flexible"), ("Worker:FlexibleLanes:ProtectedLanes", " user , "))).ProtectedLanes);
        // The pool is as big as the lanes' slots add up to (today 20).
        Assert.Equal(WorkerLanes.All().Sum(lane => lane.DefaultSlots), new LaneSlotPool(WorkerLanes.All().ToDictionary(lane => lane.Name, lane => lane.DefaultSlots), flexible.ProtectedLanes).Total);
    }

    private static LeasedJob Job() => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "TEST", "{}", "test", "lease-token");

    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(item => item.Key, item => (string?)item.Value)).Build();
}
