using MarketplaceHub.Infrastructure.Jobs;
using MarketplaceHub.Infrastructure.Persistence;
using MarketplaceHub.Worker;
using Microsoft.Extensions.Configuration;

namespace MarketplaceHub.Application.Tests.QueueReplay;

// Queue phase 2 (27 Nov 2026), the replay harness: a recorded or synthetic job stream (arrival, type, priority, store, lock
// group, run time) is fed to a lane model on a simulated clock over a model of the lease query, and each job's start delay is
// measured. The lane models are the worker's own code, wired as Worker.ExecuteAsync wires them:
//   flexible   (today's default) the lanes' own and borrowing slots (Worker.RunSlotAsync, Worker.LeaseThroughPoolAsync,
//              LaneSlotPool, JobWakeRouter, LaneWakeSignal), every lease loop a task;
//   legacy     the same loops without the pool (each lane on its own slots);
//   dispatcher LaneDispatcher and DispatcherCore over DispatcherLanes.
// The lease model (ReplayQueue) applies what JobLeaseService does: the lane's priority range and job types, a connection's
// busy lock group, the bulkhead classes (isolation caps 1 with the pooled models, as on live), the dispatcher's per-store cap,
// a quota hold (background jobs only, at most 15 minutes), and the order of the candidates (priority, a job runnable for 15
// minutes first, then the tenant and the connection with fewer running jobs, the connection served longest ago, arrival).
// Notifications are delivered as the worker's listener delivers them ("job:<type>:<priority>" when a job is queued runnable,
// "freed" when a run ends), jobs queued for later (retries, deferrals) through the due-time wake; any of them can be dropped.
// Time moves only when every loop waits, so the delays are those of the code's decisions, not of the machine running it.
internal enum ReplayModel
{
    Legacy,
    Flexible,
    Dispatcher
}

internal enum ReplaySignal
{
    Notify,
    Due
}

internal sealed record ReplayJob(int Id, TimeSpan Arrival, string JobType, int Priority, string Tenant, string? Connection, string Platform, string? LockGroup, TimeSpan Duration, ReplaySignal Signal);

internal sealed record ReplayQuotaHold(string Connection, string JobType, TimeSpan From, TimeSpan Until);

internal sealed record ReplayStream(string Name, IReadOnlyList<ReplayJob> Jobs, IReadOnlyList<ReplayQuotaHold>? Holds = null, TimeSpan? IdleTail = null, bool StartBacklog = false);

internal sealed record ReplayOptions(bool Signals = true, TimeSpan? LeaseLatency = null, TimeSpan? DrainLimit = null);

internal sealed record ReplayResult(ReplayModel Model, string Stream, IReadOnlyDictionary<int, TimeSpan> StartDelays, IReadOnlyDictionary<int, string> WorkTypes, long Leases, long Censuses,
    int MaxRunning, int MaxStoreCounted, int OrderMinimumViolations, TimeSpan SimulatedTime, int NotStarted, double IdleQueriesPerSecond, IReadOnlyDictionary<int, TimeSpan> SlotWaits, IReadOnlyList<string>? Notes = null);

internal static class ReplayHarness
{
    public static readonly DateTimeOffset Origin = new(2026, 11, 27, 0, 0, 0, TimeSpan.Zero);

    // The work type a job is reported under: the dispatcher's lane, webhooks on their own.
    public static string WorkType(string jobType, int priority) =>
        jobType is MarketplaceJobTypes.WebhookIngest or MarketplaceJobTypes.ShopifyWebhookIngest or MarketplaceJobTypes.HepsiburadaWebhookIngest && priority >= 0
            ? "webhook"
            : DispatcherLanes.LaneFor(jobType, priority);

    public static async Task<ReplayResult> RunAsync(ReplayModel model, ReplayStream stream, ReplayOptions? options = null)
    {
        options ??= new ReplayOptions();
        var clock = new ReplayClock(Origin);
        var queue = new ReplayQueue(clock);
        using var stopping = new CancellationTokenSource();
        var runner = model == ReplayModel.Dispatcher
            ? (IReplayRunner)new DispatcherRunner(clock, queue, options, stopping.Token)
            : new LaneLoopRunner(clock, queue, options, model == ReplayModel.Flexible, stopping.Token);
        if (stream.StartBacklog)
            foreach (var job in stream.Jobs.Where(job => job.Arrival <= TimeSpan.Zero)) queue.Add(job);
        foreach (var job in stream.Jobs.Where(job => !(stream.StartBacklog && job.Arrival <= TimeSpan.Zero)))
            clock.Schedule(Origin + job.Arrival, () => runner.Arrive(job));
        foreach (var hold in stream.Holds ?? [])
        {
            clock.Schedule(Origin + hold.From, () => queue.Hold(hold.Connection, hold.JobType, Origin + hold.Until));
            clock.Schedule(Origin + hold.Until, () =>
            {
                queue.Release(hold.Connection, hold.JobType);
                runner.QuotaReleased();
            });
        }
        var last = stream.Jobs.Count == 0 ? TimeSpan.Zero : stream.Jobs.Max(job => job.Arrival);
        var idleTail = stream.IdleTail ?? TimeSpan.Zero;
        var drainLimit = options.DrainLimit ?? TimeSpan.FromHours(6);
        runner.Start();
        try
        {
            await clock.SettleAsync(() => runner.Actors, ReplayFailures.Check);
            var idleFrom = (DateTimeOffset?)null;
            long idleQueriesAt = 0;
            while (true)
            {
                var finished = clock.UtcNow >= Origin + last && queue.Drained(stream.Jobs.Count);
                if (finished && idleFrom is null)
                {
                    idleFrom = clock.UtcNow;
                    idleQueriesAt = queue.Leases + queue.Censuses;
                }
                if (finished && clock.UtcNow >= idleFrom + idleTail) break;
                if (clock.UtcNow > Origin + last + drainLimit) break;
                if (!clock.AdvanceToNext()) break;
                await clock.SettleAsync(() => runner.Actors, ReplayFailures.Check);
            }
            var idleSeconds = idleFrom is { } from ? (clock.UtcNow - from).TotalSeconds : 0;
            var idleRate = idleSeconds > 0 ? (queue.Leases + queue.Censuses - idleQueriesAt) / idleSeconds : double.NaN;
            return new ReplayResult(model, stream.Name, queue.StartDelays(), stream.Jobs.ToDictionary(job => job.Id, job => WorkType(job.JobType, job.Priority)),
                queue.Leases, queue.Censuses, queue.MaxRunning, queue.MaxStoreCounted, queue.OrderMinimumViolations, clock.UtcNow - Origin,
                stream.Jobs.Count - queue.StartDelays().Count, idleRate, queue.SlotWaits(), [.. queue.ViolationNotes]);
        }
        finally
        {
            stopping.Cancel();
            clock.ReleaseAll();
            await runner.StopAsync();
        }
    }

    private interface IReplayRunner
    {
        int Actors { get; }
        void Start();
        void Arrive(ReplayJob job);
        void QuotaReleased();
        Task StopAsync();
    }

    // Today's lane loops (flexible: own and borrowing slots over LaneSlotPool; legacy: own slots only), wired as
    // Worker.ExecuteAsync wires them.
    private sealed class LaneLoopRunner : IReplayRunner
    {
        private readonly ReplayClock clock;
        private readonly ReplayQueue queue;
        private readonly ReplayOptions options;
        private readonly CancellationToken stopping;
        private readonly List<Task> loops = [];
        private readonly JobWakeRouter router;
        private readonly LaneSlotPool? pool;
        private readonly IReadOnlyList<WorkerLane> lanes = WorkerLanes.All();
        private readonly BulkheadSettings bulkheads;

        public LaneLoopRunner(ReplayClock clock, ReplayQueue queue, ReplayOptions options, bool flexible, CancellationToken stopping)
        {
            this.clock = clock;
            this.queue = queue;
            this.options = options;
            this.stopping = stopping;
            router = new JobWakeRouter(lanes);
            var slots = lanes.ToDictionary(lane => lane.Name, lane => lane.DefaultSlots);
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Worker:LaneModel"] = flexible ? "flexible" : "legacy" }).Build();
            var model = LaneModelSettings.From(configuration);
            bulkheads = BulkheadSettings.From(configuration, model.Flexible);
            pool = flexible ? new LaneSlotPool(slots, model.ProtectedLanes, model.BorrowLimits, clock) : null;
            if (pool is not null)
            {
                router.LaneNotified += pool.NoteWork;
                router.JobEnded += pool.Unquiet;
                pool.SlotFreed += freedFor =>
                {
                    foreach (var lane in freedFor)
                    {
                        router.SignalFor(lane).WakeOne();
                        router.BorrowSignalFor(lane).WakeOne();
                    }
                };
            }
            Actors = slots.Values.Sum() + (pool is null ? 0 : lanes.Sum(lane => slots.Values.Sum() - slots[lane.Name]));
            Slots = slots;
        }

        private IReadOnlyDictionary<string, int> Slots { get; }

        public int Actors { get; }

        public void Start()
        {
            var total = Slots.Values.Sum();
            foreach (var lane in lanes)
            {
                var bulkhead = bulkheads.For(lane.Name, WorkerLanes.DefaultHotPriorityCeiling);
                var own = router.SignalFor(lane.Name);
                var borrow = router.BorrowSignalFor(lane.Name);
                for (var slot = 0; slot < Slots[lane.Name]; slot++)
                    loops.Add(Loop(lane, own, true, bulkhead, pool is null ? null : borrow.WakeOne));
                if (pool is not null)
                    for (var slot = 0; slot < total - Slots[lane.Name]; slot++)
                        loops.Add(Loop(lane, borrow, false, bulkhead, null));
            }
            // The listener's first connection wakes every slot (Worker.RunJobNotificationListenerAsync).
            router.WakeAll();
        }

        private Task Loop(WorkerLane lane, LaneWakeSignal signal, bool ownSlot, Application.JobBulkhead? bulkhead, Action? leased) =>
            Task.Run(() => MarketplaceHub.Worker.Worker.RunSlotAsync(signal, token => MarketplaceHub.Worker.Worker.LeaseThroughPoolAsync(pool, lane.Name, ownSlot, async () =>
            {
                await clock.Delay(options.LeaseLatency ?? ReplayClock.DefaultLeaseLatency);
                return queue.Lease(lane.Name, lane.MaximumPriority, lane.MinimumPriority, lane.IncludedJobTypes, lane.ExcludedJobTypes, bulkhead, null);
            }, async job =>
            {
                await clock.Delay(queue.DurationOf(job));
                queue.Complete(job);
                if (options.Signals) router.Route(JobWakeRouter.Freed);
            }, leased), exception => ReplayFailures.Add("lease loop", exception), stopping, wait: clock.WaitAsync));

        public void Arrive(ReplayJob job)
        {
            queue.Add(job);
            if (!options.Signals) return;
            if (job.Signal == ReplaySignal.Notify) router.Route(JobWakeRouter.Payload(job.JobType, job.Priority));
            else
            {
                // Worker.RunDueJobWakeAsync: the lanes are told (flexible) and every slot woken.
                if (pool is not null) router.NoteDue([(job.JobType, job.Priority)]);
                router.WakeAll();
            }
        }

        public void QuotaReleased()
        {
            pool?.QuotaReleased();
            router.WakeAll();
        }

        public Task StopAsync() => Task.WhenAll(loops.Select(loop => loop.ContinueWith(_ => { }, TaskScheduler.Default)));
    }

    private sealed class DispatcherRunner : IReplayRunner
    {
        private readonly ReplayClock clock;
        private readonly ReplayQueue queue;
        private readonly ReplayOptions options;
        private readonly CancellationToken stopping;
        private readonly LaneDispatcher dispatcher;
        private Task? loop;

        public DispatcherRunner(ReplayClock clock, ReplayQueue queue, ReplayOptions options, CancellationToken stopping)
        {
            this.clock = clock;
            this.queue = queue;
            this.options = options;
            this.stopping = stopping;
            var settings = DispatcherSettings.Default;
            var lanes = DispatcherLanes.All(WorkerLanes.DefaultHotPriorityCeiling, settings);
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Worker:LaneModel"] = "dispatcher" }).Build();
            var bulkheads = BulkheadSettings.From(configuration, true);
            var cap = DispatcherLanes.ConnectionCap(settings);
            dispatcher = new LaneDispatcher(LaneDispatcher.LanesFrom(lanes, settings), clock,
                async (lane, _, _) =>
                {
                    // Checked when the dispatcher decides (a job that becomes leasable during the lease is seen at the next one).
                    if (options.Signals) queue.CheckOrderMinimum(lane.Name, () => dispatcher!.Read(core => $"orders lane {core.Hint(2)} running {core.Running(2)} epoch {core.Epoch(2)}"));
                    await clock.Delay(options.LeaseLatency ?? ReplayClock.DefaultLeaseLatency);
                    return queue.Lease(lane.Name, lane.MaximumPriority, lane.MinimumPriority, lane.IncludedJobTypes, lane.ExcludedJobTypes,
                        bulkheads.For(lane.Name, WorkerLanes.DefaultHotPriorityCeiling), settings.IsCapped(lane.Name) ? cap : null);
                },
                async _ =>
                {
                    await clock.Delay(options.LeaseLatency ?? ReplayClock.DefaultLeaseLatency);
                    queue.CountCensus();
                    return queue.Census();
                },
                async (job, _) =>
                {
                    await clock.Delay(queue.DurationOf(job));
                    queue.Complete(job);
                    if (options.Signals) dispatcher!.Route(JobWakeRouter.Freed);
                },
                (what, exception) => ReplayFailures.Add(what, exception),
                settings.SafetyTick,
                clock.WaitAsync);
        }

        // The dispatcher's loop and every job it runs (counted from its lease to its completion, ReplayQueue.Running).
        public int Actors => 1 + Math.Max(queue.RunningCount, dispatcher.RunningTasks);

        public void Start()
        {
            loop = Task.Run(() => dispatcher.RunAsync(stopping));
            dispatcher.CensusSoon();
        }

        public void Arrive(ReplayJob job)
        {
            queue.Add(job);
            if (!options.Signals) return;
            if (job.Signal == ReplaySignal.Notify) dispatcher.Route(JobWakeRouter.Payload(job.JobType, job.Priority));
            else dispatcher.NoteDue([(job.JobType, job.Priority)]);
        }

        public void QuotaReleased() => dispatcher.Unblock();

        public Task StopAsync() => loop?.ContinueWith(_ => { }, TaskScheduler.Default) ?? Task.CompletedTask;
    }
}

// A failure inside a replayed loop (the worker would log it) fails the replay at its next settle.
internal static class ReplayFailures
{
    private static readonly System.Collections.Concurrent.ConcurrentQueue<string> Failures = new();

    public static void Add(string what, Exception exception) => Failures.Enqueue($"{what}: {exception}");

    public static void Check()
    {
        if (Failures.TryDequeue(out var failure)) throw new InvalidOperationException($"A replayed loop failed: {failure}");
    }
}

// The simulated clock: the loops' idle waits, the leases' and runs' durations and the stream's events are due at moments;
// time moves to the next one only when every loop waits (SettleAsync).
internal sealed class ReplayClock(DateTimeOffset start) : TimeProvider
{
    public static readonly TimeSpan DefaultLeaseLatency = TimeSpan.FromMilliseconds(3);

    private readonly object gate = new();
    private readonly List<Entry> pending = [];
    private readonly PriorityQueue<Action, (DateTimeOffset At, long Sequence)> events = new();
    private long sequence;
    private DateTimeOffset now = start;

    private sealed record Entry(Task? Wake, DateTimeOffset Due, TaskCompletionSource<bool> Done);

    public DateTimeOffset UtcNow
    {
        get { lock (gate) return now; }
    }

    public override DateTimeOffset GetUtcNow() => UtcNow;

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

    private Entry Add(Task? wake, TimeSpan delay)
    {
        lock (gate)
        {
            var entry = new Entry(wake, now + (delay < TimeSpan.Zero ? TimeSpan.Zero : delay), new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously));
            pending.Add(entry);
            return entry;
        }
    }

    public void Schedule(DateTimeOffset at, Action action)
    {
        lock (gate) events.Enqueue(action, (at, sequence++));
    }

    private bool Settled(int actors)
    {
        lock (gate)
            return pending.Count >= actors && pending.All(entry => entry.Wake is not { IsCompleted: true } && !entry.Done.Task.IsCompleted);
    }

    // Waits (real time) until every actor waits on the simulated clock. A storm of polls without time moving never settles.
    // actors: how many loops and runs must wait (read again at every look: a run that ends leaves, a lease adds one).
    public async Task SettleAsync(Func<int> actors, Action? check = null)
    {
        var spin = new SpinWait();
        var started = global::System.Diagnostics.Stopwatch.StartNew();
        var quiet = 0;
        while (true)
        {
            check?.Invoke();
            if (Settled(actors()))
            {
                // Settled twice in a row with a yield between: a continuation queued just now would show in between.
                if (++quiet >= 2) return;
                await Task.Yield();
                continue;
            }
            quiet = 0;
            if (started.Elapsed > TimeSpan.FromSeconds(60)) throw new TimeoutException($"The replay did not settle at {UtcNow:O} ({actors()} actors, {pending.Count} waiting)");
            if (spin.NextSpinWillYield) await Task.Yield();
            spin.SpinOnce(sleep1Threshold: -1);
        }
    }

    // Moves to the next due moment: ends the waits due then and runs the stream's events of that moment. False: nothing left.
    public bool AdvanceToNext()
    {
        List<TaskCompletionSource<bool>> due;
        List<Action> actions = [];
        lock (gate)
        {
            DateTimeOffset? next = pending.Count == 0 ? null : pending.Min(entry => entry.Due);
            if (events.TryPeek(out _, out var head) && (next is null || head.At < next)) next = head.At;
            if (next is null) return false;
            if (next > now) now = next.Value;
            // The stream's events of the moment first (arrivals, holds), the waits due then at the next call: the loops see a
            // job that arrives at the moment a run ends in the same order every time.
            while (events.TryPeek(out _, out var at) && at.At <= now) actions.Add(events.Dequeue());
            due = actions.Count > 0 ? [] : [.. pending.Where(entry => entry.Due <= now).Select(entry => entry.Done)];
        }
        foreach (var action in actions) action();
        foreach (var done in due) done.TrySetResult(true);
        return true;
    }

    public void ReleaseAll()
    {
        List<TaskCompletionSource<bool>> all;
        lock (gate) all = [.. pending.Select(entry => entry.Done)];
        foreach (var done in all) done.TrySetResult(true);
    }
}

// The lease query's model (JobLeaseService.TryLeaseAsync), and what the replay measures.
internal sealed class ReplayQueue(ReplayClock clock)
{
    private static readonly TimeSpan Aging = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan MaximumHold = TimeSpan.FromMinutes(15);
    private static readonly HashSet<string> ProcessorJobTypes = [.. MarketplaceJobTypes.ProcessorJobTypes];

    private readonly object gate = new();
    private readonly List<QueuedJob> queued = [];
    private readonly Dictionary<int, QueuedJob> running = [];
    private readonly Dictionary<Guid, QueuedJob> byLeaseId = [];
    private readonly Dictionary<string, DateTimeOffset> lastLeased = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Connection, string JobType), DateTimeOffset> held = [];
    private readonly Dictionary<int, TimeSpan> startDelays = [];
    private readonly Dictionary<int, TimeSpan> slotWaits = [];
    private readonly Dictionary<(string Connection, string Group), DateTimeOffset> lastFreed = [];
    private readonly Dictionary<(string Connection, string JobType), DateTimeOffset> holdEnded = [];
    private int completed;
    private DateTimeOffset lastCompletedAt;

    private sealed record QueuedJob(ReplayJob Job, DateTimeOffset RunnableAt, string? LockGroup, string Lane);

    public long Leases { get; private set; }
    public long Censuses { get; private set; }
    public int MaxRunning { get; private set; }
    public int MaxStoreCounted { get; private set; }
    public int OrderMinimumViolations { get; private set; }
    public List<string> ViolationNotes { get; } = [];

    public int RunningCount
    {
        get { lock (gate) return running.Count; }
    }

    public void Add(ReplayJob job)
    {
        lock (gate)
            queued.Add(new QueuedJob(job, clock.UtcNow, string.IsNullOrEmpty(job.LockGroup) ? (job.Connection is not null && ProcessorJobTypes.Contains(job.JobType) ? MarketplaceSyncExecutionLock.GroupFor(job.JobType) : null) : job.LockGroup,
                DispatcherLanes.LaneFor(job.JobType, job.Priority)));
    }

    public void Hold(string connection, string jobType, DateTimeOffset until)
    {
        lock (gate) held[(connection, jobType)] = until;
    }

    public void Release(string connection, string jobType)
    {
        lock (gate)
        {
            held.Remove((connection, jobType));
            holdEnded[(connection, jobType)] = clock.UtcNow;
        }
    }

    public IReadOnlyDictionary<int, TimeSpan> SlotWaits()
    {
        lock (gate) return new Dictionary<int, TimeSpan>(slotWaits);
    }

    public void CountCensus()
    {
        lock (gate) Censuses++;
    }

    public bool Drained(int total)
    {
        lock (gate) return completed >= total && running.Count == 0;
    }

    public IReadOnlyDictionary<int, TimeSpan> StartDelays()
    {
        lock (gate) return new Dictionary<int, TimeSpan>(startDelays);
    }

    public TimeSpan DurationOf(Application.LeasedJob job)
    {
        lock (gate) return byLeaseId[job.Id].Job.Duration;
    }

    public IReadOnlyCollection<(string JobType, int Priority)> Census()
    {
        lock (gate) return [.. queued.Select(job => (job.Job.JobType, job.Job.Priority)).Distinct()];
    }

    private static bool Counted(ReplayJob job, Application.JobConnectionCap cap) => cap.Caps(job.JobType, job.Priority);

    private bool Leasable(QueuedJob candidate, int? maximumPriority, int? minimumPriority, IReadOnlyList<string> included, IReadOnlyList<string> excluded, Application.JobBulkhead? bulkhead, Application.JobConnectionCap? cap, DateTimeOffset now)
    {
        var job = candidate.Job;
        if (maximumPriority is { } maximum && job.Priority > maximum) return false;
        if (minimumPriority is { } minimum && job.Priority < minimum) return false;
        if (included.Count > 0 && !included.Contains(job.JobType)) return false;
        if (excluded.Contains(job.JobType)) return false;
        if (job.Connection is { } connection)
        {
            if (candidate.LockGroup is { } group && running.Values.Any(other => other.Job.Connection == connection && other.LockGroup == group)) return false;
            if (job.Priority > Application.JobQuotaHold.DefaultHeldAbovePriority && held.TryGetValue((connection, job.JobType), out var until) && until > now
                && candidate.RunnableAt > now - MaximumHold) return false;
            if (bulkhead is { IsEmpty: false } && job.Priority > Application.JobPriorityPolicy.BulkUserAction && job.Priority >= bulkhead.ClassMinimumPriority && job.Priority <= bulkhead.ClassMaximumPriority)
            {
                var index = bulkhead.JobTypes.ToList().IndexOf(job.JobType);
                if (index >= 0)
                {
                    var className = bulkhead.Classes[index];
                    var classCap = bulkhead.ClassCaps[bulkhead.ClassNames.ToList().IndexOf(className)];
                    var classRunning = running.Values.Count(other => other.Job.Tenant == job.Tenant && other.Job.Platform == job.Platform
                        && other.Job.Priority >= bulkhead.ClassMinimumPriority && other.Job.Priority <= bulkhead.ClassMaximumPriority
                        && bulkhead.JobTypes.ToList().IndexOf(other.Job.JobType) is var otherIndex and >= 0 && bulkhead.Classes[otherIndex] == className);
                    if (classRunning >= classCap) return false;
                }
            }
            if (cap is { Cap: > 0 } && Counted(job, cap) && running.Values.Count(other => other.Job.Connection == connection && Counted(other.Job, cap)) >= cap.Cap) return false;
        }
        return true;
    }

    public Application.LeasedJob? Lease(string lane, int? maximumPriority, int? minimumPriority, IReadOnlyList<string> included, IReadOnlyList<string> excluded, Application.JobBulkhead? bulkhead, Application.JobConnectionCap? cap)
    {
        lock (gate)
        {
            Leases++;
            var now = clock.UtcNow;
            var pick = queued.Where(candidate => Leasable(candidate, maximumPriority, minimumPriority, included, excluded, bulkhead, cap, now))
                .OrderBy(candidate => candidate.Job.Priority)
                .ThenByDescending(candidate => candidate.Job.Connection is not null && candidate.Job.Priority > Application.JobPriorityPolicy.BulkUserAction && candidate.RunnableAt <= now - Aging)
                .ThenBy(candidate => candidate.Job.Connection is not null && candidate.Job.Priority > Application.JobPriorityPolicy.BulkUserAction && candidate.RunnableAt <= now - Aging ? candidate.RunnableAt : DateTimeOffset.MinValue)
                .ThenBy(candidate => running.Values.Count(other => other.Job.Tenant == candidate.Job.Tenant))
                .ThenBy(candidate => candidate.Job.Connection is null || candidate.Job.Priority <= Application.JobPriorityPolicy.BulkUserAction ? 0 : running.Values.Count(other => other.Job.Connection == candidate.Job.Connection))
                .ThenBy(candidate => candidate.Job.Connection is { } connection && candidate.Job.Priority > Application.JobPriorityPolicy.BulkUserAction && lastLeased.TryGetValue(connection, out var at) ? at : DateTimeOffset.MinValue)
                .ThenBy(candidate => candidate.RunnableAt)
                .ThenBy(candidate => candidate.Job.Id)
                .FirstOrDefault();
            if (pick is null) return null;
            queued.Remove(pick);
            running[pick.Job.Id] = pick;
            if (pick.Job.Connection is { } leasedConnection) lastLeased[leasedConnection] = now;
            startDelays[pick.Job.Id] = now - pick.RunnableAt;
            // The wait left once the job's lock group last opened and its quota hold last ended (both the same in every model).
            var free = pick.RunnableAt;
            if (pick.Job.Connection is { } freedConnection)
            {
                if (pick.LockGroup is { } freedGroup && lastFreed.TryGetValue((freedConnection, freedGroup), out var opened) && opened > free) free = opened;
                if (holdEnded.TryGetValue((freedConnection, pick.Job.JobType), out var ended) && ended > free) free = ended;
            }
            slotWaits[pick.Job.Id] = now - free;
            MaxRunning = Math.Max(MaxRunning, running.Count);
            var capRule = DispatcherLanes.ConnectionCap(DispatcherSettings.Default);
            if (pick.Job.Connection is { } store)
                MaxStoreCounted = Math.Max(MaxStoreCounted, running.Values.Count(other => other.Job.Connection == store && Counted(other.Job, capRule)));
            var id = Guid.NewGuid();
            byLeaseId[id] = pick;
            return new Application.LeasedJob(id, Guid.Empty, null, pick.Job.JobType, "{}", "replay", "token", now - pick.RunnableAt, pick.Job.Priority);
        }
    }

    // A slot goes to work outside siparis-okuma while an order read could be leased and siparis-okuma runs fewer than its
    // minimum: the order read waits behind other work although a slot of its minimum is free (Y1, 23 Nov). Checked when the
    // dispatcher decides to lease for a catalogue, background, return or first-import lane.
    public void CheckOrderMinimum(string leasingLane, Func<string>? state = null)
    {
        if (leasingLane is not (DispatcherLanes.Returns or DispatcherLanes.Catalog or DispatcherLanes.Background or DispatcherLanes.InitialSync)) return;
        lock (gate)
        {
            var now = clock.UtcNow;
            var lane = DispatcherLanes.All().First(item => item.Name == DispatcherLanes.OrderReads);
            // A run that ended at this very moment has not told the dispatcher yet (its end comes right after the database's).
            if (lastCompletedAt == now) return;
            if (running.Values.Count(job => job.Lane == DispatcherLanes.OrderReads) >= DispatcherSettings.Default.ReservedOf(DispatcherLanes.OrderReads)) return;
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Worker:LaneModel"] = "dispatcher" }).Build();
            var bulkhead = BulkheadSettings.From(configuration, true).For(lane.Name, WorkerLanes.DefaultHotPriorityCeiling);
            var cap = DispatcherSettings.Default.IsCapped(lane.Name) ? DispatcherLanes.ConnectionCap(DispatcherSettings.Default) : null;
            // A job queued at this very moment may be announced a step after another one of the same moment: not a wait.
            if (queued.Any(candidate => candidate.Lane == DispatcherLanes.OrderReads && candidate.RunnableAt < now
                && Leasable(candidate, lane.MaximumPriority, lane.MinimumPriority, lane.IncludedJobTypes, lane.ExcludedJobTypes, bulkhead, cap, now)))
            {
                OrderMinimumViolations++;
                ViolationNotes.Add($"{now:HH:mm:ss.fff} {leasingLane}: {state?.Invoke()}; leasable {string.Join(",", queued.Where(candidate => candidate.Lane == DispatcherLanes.OrderReads && Leasable(candidate, lane.MaximumPriority, lane.MinimumPriority, lane.IncludedJobTypes, lane.ExcludedJobTypes, bulkhead, cap, now)).Select(candidate => $"{candidate.Job.JobType}@{candidate.Job.Connection} since {candidate.RunnableAt:HH:mm:ss.fff}"))}");
            }
        }
    }

    public void Complete(Application.LeasedJob job)
    {
        lock (gate)
        {
            var entry = byLeaseId[job.Id];
            running.Remove(entry.Job.Id);
            completed++;
            if (entry.Job.Connection is { } connection && entry.LockGroup is { } group) lastFreed[(connection, group)] = clock.UtcNow;
            lastCompletedAt = clock.UtcNow;
        }
    }
}
