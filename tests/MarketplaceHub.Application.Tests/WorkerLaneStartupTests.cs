using MarketplaceHub.Infrastructure.Jobs;
using MarketplaceHub.Worker;
using Microsoft.Extensions.Configuration;
using Xunit;
using WorkerService = MarketplaceHub.Worker.Worker;

namespace MarketplaceHub.Application.Tests;

// 23 Nov 2026, after the release of the protected slots: on an idle worker a Trendyol return read queued at startup waited
// 113 s. The live lanes and slots are wired as Worker.ExecuteAsync wires them (own and borrowing slots, wake signals, the
// pool's SlotFreed), with a 40 ms queue poll and no notification at all: only the slots' own polls can find the job.
public sealed class WorkerLaneStartupTests
{
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(40);

    [Fact]
    public async Task AJobQueuedAtStartupIsLeasedAtOnceAndIdleBorrowingSlotsDoNotStormThePool()
    {
        var lanes = WorkerLanes.All();
        var slots = lanes.ToDictionary(lane => lane.Name, lane => lane.DefaultSlots);
        var total = slots.Values.Sum();
        var router = new JobWakeRouter(lanes);
        var pool = new LaneSlotPool(slots, LaneModelSettings.From(new ConfigurationBuilder().Build()).ProtectedLanes);
        router.LaneNotified += pool.NoteWork;
        pool.SlotFreed += freed =>
        {
            foreach (var lane in freed)
            {
                router.SignalFor(lane).WakeOne();
                router.BorrowSignalFor(lane).WakeOne();
            }
        };
        // As on live: the startup recovery queued a Hepsiburada status scan (3 s) and a return read.
        var queued = new System.Collections.Concurrent.ConcurrentDictionary<string, int> { ["returns-read"] = 1, ["hepsiburada-status"] = 1 };
        var returnLeased = new TaskCompletionSource<TimeSpan>(TaskCreationOptions.RunContinuationsAsynchronously);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var polls = 0;
        using var stopping = new CancellationTokenSource();
        Task Slot(string lane, bool own) => WorkerService.RunSlotAsync(own ? router.SignalFor(lane) : router.BorrowSignalFor(lane),
            token => WorkerService.LeaseThroughPoolAsync(pool, lane, own, async () =>
            {
                Interlocked.Increment(ref polls);
                await Task.Delay(Poll, token);
                var found = queued.TryGetValue(lane, out var count) && count > 0 && queued.TryUpdate(lane, count - 1, count);
                if (found) pool.NoteWork(lane);
                else pool.NoteNoWork(lane);
                return found ? new LeasedJob(Guid.NewGuid(), Guid.NewGuid(), null, "TEST", "{}", "test", "lease-token") : null;
            }, _ =>
            {
                if (lane == "returns-read") returnLeased.TrySetResult(clock.Elapsed);
                return lane == "hepsiburada-status" ? Task.Delay(TimeSpan.FromSeconds(3), stopping.Token) : Task.CompletedTask;
            }), _ => { }, stopping.Token);
        var loops = new List<Task>();
        foreach (var lane in lanes)
        {
            loops.AddRange(Enumerable.Range(0, slots[lane.Name]).Select(_ => Slot(lane.Name, true)));
            loops.AddRange(Enumerable.Range(0, total - slots[lane.Name]).Select(_ => Slot(lane.Name, false)));
        }
        try
        {
            // Before the fix: not within a minute (the borrowing slots kept the unprotected part full).
            var leasedAfter = await returnLeased.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(leasedAfter < TimeSpan.FromSeconds(5), $"leased after {leasedAfter}");
            // Idle: only the own slots' timed polls (20 slots, at most every 15 s once settled), not ~180 polls a second.
            await Task.Delay(TimeSpan.FromSeconds(4));
            var mark = Volatile.Read(ref polls);
            await Task.Delay(TimeSpan.FromSeconds(3));
            var perSecond = (Volatile.Read(ref polls) - mark) / 3.0;
            Assert.True(perSecond < 20, $"{perSecond} polls a second on an idle worker");
        }
        finally
        {
            stopping.Cancel();
            await Task.WhenAll(loops.Select(loop => loop.ContinueWith(_ => { }, TaskScheduler.Default)));
        }
    }
}
