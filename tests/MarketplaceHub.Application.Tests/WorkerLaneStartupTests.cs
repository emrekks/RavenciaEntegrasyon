using MarketplaceHub.Infrastructure.Jobs;
using MarketplaceHub.Worker;
using Microsoft.Extensions.Configuration;
using Xunit;
using Xunit.Abstractions;
using WorkerService = MarketplaceHub.Worker.Worker;

namespace MarketplaceHub.Application.Tests;

// 23 Nov 2026, after the release of the protected slots: on an idle worker a Trendyol return read queued at startup waited
// 113 s. The live lanes and slots are wired as Worker.ExecuteAsync wires them (own and borrowing slots, wake signals, the
// pool's SlotFreed), with no notification at all: only the slots' own polls can find the job. Time is simulated: the idle
// waits and the running job end only when the test moves the clock, and the clock moves only once every slot waits (24 Nov:
// the first version measured wall-clock seconds and polls a second, which a busy CI runner could break).
public sealed class WorkerLaneStartupTests(ITestOutputHelper output)
{
    [Fact]
    public async Task AJobQueuedAtStartupIsLeasedAtOnceAndAnIdleWorkerPollsOnlyOnItsIdleTimers()
    {
        var lanes = WorkerLanes.All();
        var slots = lanes.ToDictionary(lane => lane.Name, lane => lane.DefaultSlots);
        var total = slots.Values.Sum();
        var sim = new SimulatedTime();
        var router = new JobWakeRouter(lanes);
        var pool = new LaneSlotPool(slots, LaneModelSettings.From(new ConfigurationBuilder().Build()).ProtectedLanes, clock: sim);
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
        TimeSpan? returnLeasedAt = null;
        var polls = 0;
        var borrowPolls = 0;
        var wakes = 0;
        using var stopping = new CancellationTokenSource();
        Task Slot(string lane, bool own) => WorkerService.RunSlotAsync(own ? router.SignalFor(lane) : router.BorrowSignalFor(lane),
            token =>
            {
                Interlocked.Increment(ref wakes);
                return WorkerService.LeaseThroughPoolAsync(pool, lane, own, async () =>
                {
                    Interlocked.Increment(ref polls);
                    if (!own) Interlocked.Increment(ref borrowPolls);
                    await Task.Yield();
                    var found = queued.TryGetValue(lane, out var count) && count > 0 && queued.TryUpdate(lane, count - 1, count);
                    return found ? new LeasedJob(Guid.NewGuid(), Guid.NewGuid(), null, "TEST", "{}", "test", "lease-token") : null;
                }, _ =>
                {
                    if (lane == "returns-read") returnLeasedAt ??= sim.Elapsed;
                    return lane == "hepsiburada-status" ? sim.Delay(TimeSpan.FromSeconds(3)) : Task.CompletedTask;
                });
            }, _ => { }, stopping.Token, wait: sim.WaitAsync);
        var loops = new List<Task>();
        foreach (var lane in lanes)
        {
            loops.AddRange(Enumerable.Range(0, slots[lane.Name]).Select(_ => Slot(lane.Name, true)));
            loops.AddRange(Enumerable.Range(0, total - slots[lane.Name]).Select(_ => Slot(lane.Name, false)));
        }
        try
        {
            // Settled: every loop waits (idle wait or running job) and nothing it waits on has happened yet. A storm never
            // settles: each empty poll wakes the next slots without the clock moving.
            async Task SettleAsync()
            {
                var pollsBefore = Volatile.Read(ref polls);
                while (!sim.Settled(loops.Count))
                {
                    Assert.True(Volatile.Read(ref polls) - pollsBefore < 2_000, $"the slots keep polling without the clock moving ({Volatile.Read(ref polls) - pollsBefore} polls at {sim.Elapsed})");
                    Assert.DoesNotContain(loops, loop => loop.IsFaulted);
                    await Task.Delay(1);
                }
            }

            await SettleAsync();
            while (returnLeasedAt is null && sim.Elapsed < TimeSpan.FromSeconds(30))
            {
                sim.AdvanceToNext();
                await SettleAsync();
            }
            // Before the fix: not within a minute (the borrowing slots kept the unprotected part full).
            Assert.NotNull(returnLeasedAt);
            Assert.True(returnLeasedAt <= TimeSpan.FromSeconds(5), $"leased after {returnLeasedAt}");

            // Idle: one simulated minute after the first one. Only the own slots poll the queue, each at most every 15 s once
            // its idle wait has grown (20 slots: 80 polls a minute); a borrowing slot of a lane whose poll found nothing wakes
            // on its idle timer and asks the pool, not the database. Its timer ends at the moment the lane's quiet ends and the
            // own slot polls again (25 Nov: which of them ran first decided whether it read the queue too, 81 to 105 polls a
            // minute by thread timing), so no borrowing poll at all is the rule, whatever the order.
            while (sim.Elapsed < TimeSpan.FromMinutes(1))
            {
                sim.AdvanceToNext();
                await SettleAsync();
            }
            var pollMark = Volatile.Read(ref polls);
            var borrowPollMark = Volatile.Read(ref borrowPolls);
            var wakeMark = Volatile.Read(ref wakes);
            while (sim.Elapsed < TimeSpan.FromMinutes(2))
            {
                sim.AdvanceToNext();
                await SettleAsync();
            }
            var idlePolls = Volatile.Read(ref polls) - pollMark;
            var idleBorrowPolls = Volatile.Read(ref borrowPolls) - borrowPollMark;
            var idleWakes = Volatile.Read(ref wakes) - wakeMark;
            output.WriteLine($"return read leased at {returnLeasedAt}; idle minute: {idlePolls} queue polls ({idleBorrowPolls} by borrowing slots), {idleWakes} slot wakes ({loops.Count} loops)");
            Assert.True(idleBorrowPolls == 0, $"{idleBorrowPolls} queue polls of borrowing slots in an idle minute");
            Assert.True(idlePolls <= total * 4, $"{idlePolls} queue polls in an idle minute");
            Assert.True(idleWakes <= loops.Count * 4 + loops.Count, $"{idleWakes} wakes in an idle minute ({loops.Count} loops)");
        }
        finally
        {
            stopping.Cancel();
            await Task.WhenAll(loops.Select(loop => loop.ContinueWith(_ => { }, TaskScheduler.Default)));
        }
    }

    // A clock the test moves: the slots' idle waits and the running jobs register what they wait for and when it is due.
    internal sealed class SimulatedTime : TimeProvider
    {
        private readonly object gate = new();
        private readonly List<(Task? Wake, DateTimeOffset Due, TaskCompletionSource<bool> Done)> pending = [];
        private static readonly DateTimeOffset Start = new(2026, 11, 24, 6, 0, 0, TimeSpan.Zero);
        private DateTimeOffset now = Start;

        public TimeSpan Elapsed
        {
            get { lock (gate) return now - Start; }
        }

        public override DateTimeOffset GetUtcNow()
        {
            lock (gate) return now;
        }

        // RunSlotAsync's idle wait: true when woken, false when the delay passed on the simulated clock.
        public async Task<bool> WaitAsync(Task wake, TimeSpan delay, CancellationToken cancellationToken)
        {
            var entry = Add(wake, delay);
            try
            {
                var done = await Task.WhenAny(wake, entry.Done.Task, Task.Delay(Timeout.Infinite, cancellationToken));
                cancellationToken.ThrowIfCancellationRequested();
                return done == wake;
            }
            finally
            {
                lock (gate) pending.Remove(entry);
            }
        }

        public async Task Delay(TimeSpan delay)
        {
            var entry = Add(null, delay);
            try
            {
                await entry.Done.Task;
            }
            finally
            {
                lock (gate) pending.Remove(entry);
            }
        }

        private (Task? Wake, DateTimeOffset Due, TaskCompletionSource<bool> Done) Add(Task? wake, TimeSpan delay)
        {
            var entry = (wake, GetUtcNow() + delay, new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously));
            lock (gate) pending.Add(entry);
            return entry;
        }

        // Every loop waits and nothing it waits on has happened.
        public bool Settled(int loops)
        {
            lock (gate)
                return pending.Count == loops && pending.All(entry => entry.Wake is not { IsCompleted: true } && !entry.Done.Task.IsCompleted);
        }

        // Moves the clock to the next due moment and ends what is due then.
        public void AdvanceToNext()
        {
            List<TaskCompletionSource<bool>> due;
            lock (gate)
            {
                if (pending.Count == 0) return;
                now = pending.Min(entry => entry.Due);
                due = [.. pending.Where(entry => entry.Due <= now).Select(entry => entry.Done)];
            }
            foreach (var done in due) done.TrySetResult(true);
        }
    }
}
