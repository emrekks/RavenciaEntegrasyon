using MarketplaceHub.Infrastructure.Jobs;
using MarketplaceHub.Infrastructure.Persistence;
using MarketplaceHub.Worker;
using Microsoft.Extensions.Configuration;

namespace MarketplaceHub.Application.Tests.QueueReplay;

// Queue phase 2 (27 Nov 2026, rebuilt after review the same day), the replay harness: a recorded or synthetic job stream is fed
// to a lane model on a simulated clock over a model of the lease query, and every attempt's start delay is measured. The lane
// models are the worker's own code, wired as Worker.ExecuteAsync wires them:
//   flexible   (today's default) the lanes' own and borrowing slots (Worker.RunSlotAsync, Worker.LeaseThroughPoolAsync,
//              LaneSlotPool, JobWakeRouter, LaneWakeSignal);
//   legacy     the same loops without the pool;
//   dispatcher LaneDispatcher and DispatcherCore over DispatcherLanes.
// Determinism: everything runs on one thread (ReplayPump, a single-threaded SynchronizationContext and TaskScheduler); the
// simulated clock's waits complete through that queue, never through the thread pool, so the same stream gives the same result
// on every run (the first harness raced the thread pool within a simulated moment and varied 2-4x between runs).
// The lease model (ReplayQueue) applies what JobLeaseService does: the lane's priority range and job types, a connection's busy
// lock group, connection_backoffs, the bulkhead classes (isolation caps 1 with the pooled models, as on live), the dispatcher's
// per-store cap and status-scan deferral, a quota hold (background jobs only, at most 15 minutes), and the candidates' order
// (priority, a job runnable for 15 minutes first, then the tenant and the connection with fewer running jobs, the connection
// served longest ago, arrival). Failed attempts reschedule themselves with the real retry table (5 s, 15 s, 1 min, 2 min, 5 min,
// jitter 0-20 %, six attempts); a job can set its store's lock group into a backoff and enqueue follow-up jobs; workers restart.
// Notifications are delivered as the worker's listener delivers them ("job:<type>:<priority>" when a job is queued runnable,
// "freed" when a run ends), jobs queued for later through the due-time wake, with a delay and a loss rate.
// Still simplified: lease latency is a constant; the candidate limit (20 per owner) and the "first job of each lock group" rule
// are not modelled (with the busy check they change no pick unless an owner has more than 20 leasable jobs); quota waits inside a
// run are part of its recorded duration; a quota hold is set by the stream, not learned; one worker process.
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

// One job. Attempts: each attempt's run time and whether it succeeded (a failure retries by the table; past the last attempt
// listed, the last one repeats). BackoffOnFailure: a failed attempt puts its connection's lock group into a backoff that long.
// FollowUps: jobs enqueued when it succeeds, each that long after.
internal sealed record ReplayJob(int Id, TimeSpan Arrival, string JobType, int Priority, string Tenant, string? Connection, string Platform, string? LockGroup, TimeSpan Duration, ReplaySignal Signal)
{
    public IReadOnlyList<(TimeSpan Duration, bool Succeeded)>? Attempts { get; init; }
    public TimeSpan? BackoffOnFailure { get; init; }
    public IReadOnlyList<(TimeSpan After, ReplayJob Job)>? FollowUps { get; init; }
    public int MaxAttempts { get; init; } = 6;

    public (TimeSpan Duration, bool Succeeded) Attempt(int number) =>
        Attempts is { Count: > 0 } list ? list[Math.Min(number - 1, list.Count - 1)] : (Duration, true);
}

internal sealed record ReplayQuotaHold(string Connection, string JobType, TimeSpan From, TimeSpan Until);

internal sealed record ReplayStream(string Name, IReadOnlyList<ReplayJob> Jobs, IReadOnlyList<ReplayQuotaHold>? Holds = null, TimeSpan? IdleTail = null, bool StartBacklog = false)
{
    // Worker restarts: at each moment the running jobs go back to the queue and the worker is down for RestartDowntime.
    public IReadOnlyList<TimeSpan> Restarts { get; init; } = [];
    public static readonly TimeSpan RestartDowntime = TimeSpan.FromSeconds(20);
}

internal sealed record ReplayOptions(bool Signals = true, TimeSpan? LeaseLatency = null, TimeSpan? DrainLimit = null)
{
    public TimeSpan NotifyDelay { get; init; } = TimeSpan.FromMilliseconds(5);
    // The share of notifications (job and "freed") lost, by a fixed seed.
    public double NotifyLoss { get; init; }
    public DispatcherSettings Dispatcher { get; init; } = DispatcherSettings.Default;
}

// One attempt's measurement. StartDelay: eligible to started, less the time a job of the same type held the same store's lock
// group (the only part of a lock wait no lane model causes). LockFreeWait: from the later of eligible and the last moment the
// job's lock group, quota hold or backoff opened, to started (the wait a model's slot decision causes).
internal sealed record ReplayAttempt(int JobId, int Attempt, string WorkType, string? Connection, TimeSpan StartDelay, TimeSpan LockFreeWait)
{
    public DateTimeOffset Started { get; init; }
    public TimeSpan Duration { get; init; }
    public string JobType { get; init; } = "";
}

internal sealed record ReplayResult(ReplayModel Model, string Stream, IReadOnlyList<ReplayAttempt> Attempts, long Leases, long Censuses,
    int MaxRunning, int MaxStoreCounted, int OrderMinimumViolations, TimeSpan SimulatedTime, int NotFinished, double IdleQueriesPerSecond, IReadOnlyList<string> Notes);

internal static class ReplayHarness
{
    public static readonly DateTimeOffset Origin = new(2026, 11, 27, 0, 0, 0, TimeSpan.Zero);

    // The work type a job is reported under: the dispatcher's lane, webhooks on their own lane already.
    public static string WorkType(string jobType, int priority) => DispatcherLanes.LaneFor(jobType, priority);

    public static Task<ReplayResult> RunAsync(ReplayModel model, ReplayStream stream, ReplayOptions? options = null) =>
        Task.Factory.StartNew(() => Run(model, stream, options ?? new ReplayOptions()), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    private static ReplayResult Run(ReplayModel model, ReplayStream stream, ReplayOptions options)
    {
        var pump = new ReplayPump();
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(pump);
        try
        {
            var clock = new ReplayClock(Origin, pump);
            var queue = new ReplayQueue(clock, options.Dispatcher) { NotifyGrace = options.NotifyDelay + TimeSpan.FromMilliseconds(15) };
            var bus = new SignalBus(clock, options);
            Func<CancellationToken, IReplayRunner> create = token => model == ReplayModel.Dispatcher
                ? new DispatcherRunner(clock, queue, options, bus, token)
                : new LaneLoopRunner(clock, queue, options, bus, model == ReplayModel.Flexible, token);
            var stopping = new CancellationTokenSource();
            var runner = create(stopping.Token);
            queue.Arrived = job => bus.Arrived(job);
            queue.BackoffEnded = (connection, group) => bus.Due(queue.KindsOf(connection, group));
            if (stream.StartBacklog)
                foreach (var job in stream.Jobs.Where(job => job.Arrival <= TimeSpan.Zero)) queue.Add(job, signal: false);
            foreach (var job in stream.Jobs.Where(job => !(stream.StartBacklog && job.Arrival <= TimeSpan.Zero)))
                clock.Schedule(Origin + job.Arrival, () => queue.Add(job, signal: true));
            foreach (var hold in stream.Holds ?? [])
            {
                clock.Schedule(Origin + hold.From, () => queue.Hold(hold.Connection, hold.JobType, Origin + hold.Until));
                clock.Schedule(Origin + hold.Until, () =>
                {
                    queue.Release(hold.Connection, hold.JobType);
                    bus.QuotaReleased();
                });
            }
            bus.Runner = runner;
            foreach (var restart in stream.Restarts)
                clock.Schedule(Origin + restart, () =>
                {
                    // The shutdown: the loops stop, the running jobs go back to the queue (attempt refunded), nothing runs while
                    // the worker is down; the new process starts with fresh in-memory state.
                    stopping.Cancel();
                    bus.Runner = null;
                    queue.ReleaseRunning();
                    clock.CancelRuns();
                    pump.Drain();
                    clock.Schedule(clock.UtcNow + ReplayStream.RestartDowntime, () =>
                    {
                        stopping = new CancellationTokenSource();
                        runner = create(stopping.Token);
                        bus.Runner = runner;
                        runner.Start();
                    });
                });
            runner.Start();
            pump.Drain();
            var last = stream.Jobs.Count == 0 ? TimeSpan.Zero : stream.Jobs.Max(job => job.Arrival);
            var drainLimit = options.DrainLimit ?? TimeSpan.FromHours(6);
            DateTimeOffset? idleFrom = null;
            long idleQueriesAt = 0;
            while (true)
            {
                var finished = clock.UtcNow >= Origin + last && queue.Drained;
                if (finished && idleFrom is null)
                {
                    idleFrom = clock.UtcNow;
                    idleQueriesAt = queue.Leases + queue.Censuses;
                }
                if (finished && clock.UtcNow >= idleFrom + (stream.IdleTail ?? TimeSpan.Zero)) break;
                if (clock.UtcNow > Origin + last + drainLimit) break;
                if (!clock.AdvanceToNext()) break;
                pump.Drain();
                ReplayFailures.Check();
            }
            var idleSeconds = idleFrom is { } from ? (clock.UtcNow - from).TotalSeconds : 0;
            var idleRate = idleSeconds > 0 ? (queue.Leases + queue.Censuses - idleQueriesAt) / idleSeconds : double.NaN;
            var result = new ReplayResult(model, stream.Name, queue.Measured, queue.Leases, queue.Censuses, queue.MaxRunning, queue.MaxStoreCounted, queue.OrderMinimumViolations,
                clock.UtcNow - Origin, queue.Unfinished, idleRate, queue.ViolationNotes);
            stopping.Cancel();
            clock.CancelRuns();
            pump.Drain();
            return result;
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    internal interface IReplayRunner
    {
        void Start();
        void Notify(string jobType, int priority);
        void Due(IReadOnlyCollection<(string JobType, int Priority)> kinds);
        void Freed();
        void QuotaReleased();
    }

    // Today's lane loops (flexible: own and borrowing slots over LaneSlotPool; legacy: own slots only), wired as Worker.ExecuteAsync.
    private sealed class LaneLoopRunner : IReplayRunner
    {
        private readonly ReplayClock clock;
        private readonly ReplayQueue queue;
        private readonly ReplayOptions options;
        private readonly SignalBus bus;
        private readonly CancellationToken stopping;
        private readonly JobWakeRouter router;
        private readonly LaneSlotPool? pool;
        private readonly IReadOnlyList<WorkerLane> lanes = WorkerLanes.All();
        private readonly BulkheadSettings bulkheads;
        private readonly IReadOnlyDictionary<string, int> slots;

        public LaneLoopRunner(ReplayClock clock, ReplayQueue queue, ReplayOptions options, SignalBus bus, bool flexible, CancellationToken stopping)
        {
            this.clock = clock;
            this.queue = queue;
            this.options = options;
            this.bus = bus;
            this.stopping = stopping;
            router = new JobWakeRouter(lanes);
            slots = lanes.ToDictionary(lane => lane.Name, lane => lane.DefaultSlots);
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Worker:LaneModel"] = flexible ? "flexible" : "legacy" }).Build();
            var model = LaneModelSettings.From(configuration);
            bulkheads = BulkheadSettings.From(configuration, model.Flexible);
            pool = flexible ? new LaneSlotPool(slots, model.ProtectedLanes, model.BorrowLimits, clock) : null;
            if (pool is null) return;
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

        public void Start()
        {
            var total = slots.Values.Sum();
            foreach (var lane in lanes)
            {
                var bulkhead = bulkheads.For(lane.Name, WorkerLanes.DefaultHotPriorityCeiling);
                var own = router.SignalFor(lane.Name);
                var borrow = router.BorrowSignalFor(lane.Name);
                for (var slot = 0; slot < slots[lane.Name]; slot++) Loop(lane, own, true, bulkhead, pool is null ? null : borrow.WakeOne);
                if (pool is not null)
                    for (var slot = 0; slot < total - slots[lane.Name]; slot++) Loop(lane, borrow, false, bulkhead, null);
            }
            // The listener's first connection wakes every slot (Worker.RunJobNotificationListenerAsync).
            router.WakeAll();
        }

        private void Loop(WorkerLane lane, LaneWakeSignal signal, bool ownSlot, Application.JobBulkhead? bulkhead, Action? leased) =>
            _ = MarketplaceHub.Worker.Worker.RunSlotAsync(signal, token => MarketplaceHub.Worker.Worker.LeaseThroughPoolAsync(pool, lane.Name, ownSlot, async () =>
            {
                await clock.Delay(options.LeaseLatency ?? ReplayClock.DefaultLeaseLatency);
                if (stopping.IsCancellationRequested) return null;
                return queue.Lease(lane.MaximumPriority, lane.MinimumPriority, lane.IncludedJobTypes, lane.ExcludedJobTypes, bulkhead, null, null);
            }, job => RunJobAsync(clock, queue, bus, job), leased), exception => ReplayFailures.Add("lease loop", exception), stopping, wait: clock.WaitAsync);

        public void Notify(string jobType, int priority) => router.Route(JobWakeRouter.Payload(jobType, priority));

        public void Due(IReadOnlyCollection<(string JobType, int Priority)> kinds)
        {
            // Worker.RunDueJobWakeAsync: the lanes are told (flexible) and every slot woken.
            if (pool is not null) router.NoteDue(kinds);
            router.WakeAll();
        }

        public void Freed() => router.Route(JobWakeRouter.Freed);

        public void QuotaReleased()
        {
            pool?.QuotaReleased();
            router.WakeAll();
        }
    }

    // A leased job runs for its attempt's time on the simulated clock; a run the restart cancels leaves the queue as it is (the
    // job went back to it).
    private static async Task RunJobAsync(ReplayClock clock, ReplayQueue queue, SignalBus bus, Application.LeasedJob job)
    {
        if (!await clock.RunFor(queue.DurationOf(job))) return;
        queue.Complete(job);
        bus.Freed();
    }

    private sealed class DispatcherRunner : IReplayRunner
    {
        private readonly LaneDispatcher dispatcher;
        private readonly CancellationToken stopping;

        public DispatcherRunner(ReplayClock clock, ReplayQueue queue, ReplayOptions options, SignalBus bus, CancellationToken stopping)
        {
            this.stopping = stopping;
            var settings = options.Dispatcher;
            var lanes = DispatcherLanes.All(WorkerLanes.DefaultHotPriorityCeiling, settings);
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Worker:LaneModel"] = "dispatcher" }).Build();
            var bulkheads = BulkheadSettings.From(configuration, true);
            var cap = DispatcherLanes.ConnectionCap(settings);
            LaneDispatcher? created = null;
            created = new LaneDispatcher(LaneDispatcher.LanesFrom(lanes, settings), clock,
                async (lane, _, _) =>
                {
                    // Checked when the dispatcher decides (a job that becomes leasable during the lease is seen at the next one).
                    if (options.Signals)
                    {
                        var orders = lanes.ToList().FindIndex(item => item.Name == DispatcherLanes.OrderReads);
                        queue.CheckOrderMinimum(lane.Definition.Name, () => created!.Read(core => $"orders lane {core.Hint(orders)}"), created!.Read(core => core.Spacing(orders, clock.UtcNow)));
                    }
                    await clock.Delay(options.LeaseLatency ?? ReplayClock.DefaultLeaseLatency);
                    if (stopping.IsCancellationRequested) return null;
                    foreach (var filter in lane.Filters)
                        if (queue.Lease(filter.MaximumPriority, filter.MinimumPriority, filter.IncludedJobTypes, filter.ExcludedJobTypes,
                            bulkheads.For(lane.Definition.Name, WorkerLanes.DefaultHotPriorityCeiling), cap.Cap > 0 ? cap : null,
                            DispatcherLanes.DeferralFor(lane.Definition.Name, settings)) is { } job) return job;
                    return null;
                },
                async _ =>
                {
                    await clock.Delay(options.LeaseLatency ?? ReplayClock.DefaultLeaseLatency);
                    return queue.Census(cap, DispatcherLanes.DeferredRules(settings), DispatcherLanes.DeferralFor(DispatcherLanes.StatusScans, settings), bulkheads.For(DispatcherLanes.OrderReads, WorkerLanes.DefaultHotPriorityCeiling));
                },
                (job, _) => RunJobAsync(clock, queue, bus, job),
                (what, exception) => ReplayFailures.Add(what, exception),
                new LaneDispatcher.Options(settings.SafetyTick, settings.LeaseTimeout, DispatcherSettings.ShortTickLanes, DispatcherSettings.ShortSafetyTick),
                clock.WaitAsync);
            dispatcher = created;
            queue.CountQueries = () => (dispatcher.Leases, dispatcher.Censuses);
        }

        public void Start()
        {
            _ = dispatcher.RunAsync(stopping);
            dispatcher.CensusSoon();
        }

        public void Notify(string jobType, int priority) => dispatcher.Route(JobWakeRouter.Payload(jobType, priority));
        public void Due(IReadOnlyCollection<(string JobType, int Priority)> kinds) => dispatcher.NoteDue(kinds);
        public void Freed() => dispatcher.Route(JobWakeRouter.Freed);
        public void QuotaReleased() => dispatcher.Unblock();
    }

    // The notification path: the trigger's NOTIFY after a short delay, some lost; the due-time wake (never lost).
    internal sealed class SignalBus(ReplayClock clock, ReplayOptions options)
    {
        private readonly Random loss = new(4242);
        public IReplayRunner? Runner { get; set; }

        private void Send(Action<IReplayRunner> deliver)
        {
            if (!options.Signals || (options.NotifyLoss > 0 && loss.NextDouble() < options.NotifyLoss)) return;
            clock.Schedule(clock.UtcNow + options.NotifyDelay, () => { if (Runner is { } runner) deliver(runner); });
        }

        public void Arrived(ReplayJob job)
        {
            if (job.Signal == ReplaySignal.Notify) Send(runner => runner.Notify(job.JobType, job.Priority));
            else Due([(job.JobType, job.Priority)]);
        }

        public void Due(IReadOnlyCollection<(string JobType, int Priority)> kinds)
        {
            if (!options.Signals || kinds.Count == 0) return;
            Runner?.Due(kinds);
        }

        public void Freed() => Send(runner => runner.Freed());

        public void QuotaReleased() => Runner?.QuotaReleased();
    }
}

// A failure inside a replayed loop (the worker would log it) fails the replay.
internal static class ReplayFailures
{
    private static readonly System.Collections.Concurrent.ConcurrentQueue<string> Failures = new();

    public static void Add(string what, Exception exception) => Failures.Enqueue($"{what}: {exception}");

    public static void Check()
    {
        if (Failures.TryDequeue(out var failure)) throw new InvalidOperationException($"A replayed loop failed: {failure}");
    }
}

// One thread runs everything: posted continuations and scheduled tasks go to one queue, run in order. Drain runs them until
// none is left; then every loop waits on the simulated clock.
internal sealed class ReplayPump : SynchronizationContext
{
    private readonly Queue<(SendOrPostCallback Callback, object? State)> queue = new();
    private readonly object gate = new();
    private readonly PumpScheduler scheduler;

    public ReplayPump() => scheduler = new PumpScheduler(this);

    public TaskScheduler Scheduler => scheduler;

    public override void Post(SendOrPostCallback d, object? state)
    {
        lock (gate) queue.Enqueue((d, state));
    }

    public override void Send(SendOrPostCallback d, object? state) => d(state);

    public override SynchronizationContext CreateCopy() => this;

    public void Drain()
    {
        var steps = 0;
        while (true)
        {
            (SendOrPostCallback Callback, object? State) item;
            lock (gate)
            {
                if (queue.Count == 0) return;
                item = queue.Dequeue();
            }
            item.Callback(item.State);
            if (++steps > 50_000_000) throw new InvalidOperationException("The replay keeps running without the clock moving (a busy loop)");
        }
    }

    private sealed class PumpScheduler(ReplayPump pump) : TaskScheduler
    {
        protected override void QueueTask(Task task) => pump.Post(_ => TryExecuteTask(task), null);
        protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) => false;
        protected override IEnumerable<Task>? GetScheduledTasks() => null;
    }
}

// The simulated clock: waits, run times, timers and the stream's events are due at moments; AdvanceToNext moves to the next one.
// Completions go through the pump, so their order is the order they were due (and, within a moment, the order they were made).
internal sealed class ReplayClock(DateTimeOffset start, ReplayPump? pump = null) : TimeProvider
{
    public static readonly TimeSpan DefaultLeaseLatency = TimeSpan.FromMilliseconds(3);

    private readonly object gate = new();
    private readonly PriorityQueue<Action, (DateTimeOffset At, long Sequence)> due = new();
    private readonly HashSet<TaskCompletionSource<bool>> runs = [];
    private long sequence;
    private DateTimeOffset now = start;

    public DateTimeOffset UtcNow
    {
        get { lock (gate) return now; }
    }

    public override DateTimeOffset GetUtcNow() => UtcNow;

    public void Schedule(DateTimeOffset at, Action action)
    {
        lock (gate) due.Enqueue(action, (at < now ? now : at, sequence++));
    }

    // RunSlotAsync's and the dispatcher's idle wait: true when woken, false when the delay passed.
    public Task<bool> WaitAsync(Task wake, TimeSpan delay, CancellationToken cancellationToken)
    {
        var result = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (cancellationToken.IsCancellationRequested) { result.TrySetCanceled(cancellationToken); return result.Task; }
        Schedule(UtcNow + (delay < TimeSpan.Zero ? TimeSpan.Zero : delay), () => result.TrySetResult(false));
        wake.ContinueWith(_ => result.TrySetResult(true), CancellationToken.None, TaskContinuationOptions.None, pump?.Scheduler ?? TaskScheduler.Default);
        cancellationToken.Register(() => result.TrySetCanceled(cancellationToken));
        return result.Task;
    }

    public Task Delay(TimeSpan delay)
    {
        var result = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Schedule(UtcNow + (delay < TimeSpan.Zero ? TimeSpan.Zero : delay), () => result.TrySetResult());
        return result.Task;
    }

    // A job's run: true when it ran its time, false when a restart cancelled it.
    public Task<bool> RunFor(TimeSpan duration)
    {
        var result = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (gate) runs.Add(result);
        Schedule(UtcNow + duration, () =>
        {
            lock (gate) runs.Remove(result);
            result.TrySetResult(true);
        });
        return result.Task;
    }

    public void CancelRuns()
    {
        TaskCompletionSource<bool>[] all;
        lock (gate)
        {
            all = [.. runs];
            runs.Clear();
        }
        foreach (var run in all) run.TrySetResult(false);
    }

    // Timers on the simulated clock (the dispatcher's lease timeout).
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) => new SimulatedTimer(this, callback, state, dueTime);

    private sealed class SimulatedTimer : ITimer
    {
        private readonly ReplayClock clock;
        private readonly TimerCallback callback;
        private readonly object? state;
        private long generation;

        public SimulatedTimer(ReplayClock clock, TimerCallback callback, object? state, TimeSpan dueTime)
        {
            this.clock = clock;
            this.callback = callback;
            this.state = state;
            Change(dueTime, Timeout.InfiniteTimeSpan);
        }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            var mine = Interlocked.Increment(ref generation);
            if (dueTime != Timeout.InfiniteTimeSpan)
                clock.Schedule(clock.UtcNow + dueTime, () => { if (Interlocked.Read(ref generation) == mine) callback(state); });
            return true;
        }

        public void Dispose() => Interlocked.Increment(ref generation);
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }

    // Moves to the next due moment and runs everything due then, in order. False: nothing left.
    public bool AdvanceToNext()
    {
        List<Action> actions = [];
        lock (gate)
        {
            if (!due.TryPeek(out _, out var head)) return false;
            if (head.At > now) now = head.At;
            while (due.TryPeek(out _, out var next) && next.At <= now) actions.Add(due.Dequeue());
        }
        foreach (var action in actions) action();
        return true;
    }
}

// The lease query's model (JobLeaseService.TryLeaseAsync and the census QueueHealthService.RunnableJobKindsAsync), the job
// lifecycle (retries, backoffs, follow-ups, restarts) and what the replay measures.
internal sealed class ReplayQueue(ReplayClock clock, DispatcherSettings settings)
{
    private static readonly TimeSpan Aging = TimeSpan.FromMinutes(15);
    // A job queued less than this ago may not be announced yet (the notification's delay): not counted as a wait behind others.
    public TimeSpan NotifyGrace { get; set; } = TimeSpan.FromMilliseconds(20);
    private static readonly TimeSpan MaximumHold = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan[] RetryDelays = [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5)];
    private static readonly HashSet<string> ProcessorJobTypes = [.. MarketplaceJobTypes.ProcessorJobTypes];

    private readonly List<QueuedJob> queued = [];
    private readonly Dictionary<Guid, QueuedJob> running = [];
    private readonly Dictionary<string, DateTimeOffset> lastLeased = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Connection, string JobType), DateTimeOffset> held = [];
    private readonly Dictionary<(string Connection, string JobType), DateTimeOffset> holdEnded = [];
    private readonly Dictionary<(string Connection, string Group), DateTimeOffset> backoffs = [];
    private readonly Dictionary<(string Connection, string Group), DateTimeOffset> lastOpened = [];
    private readonly Dictionary<(string Connection, string Group), List<(DateTimeOffset From, DateTimeOffset? To, string JobType)>> runs = [];
    private readonly List<ReplayAttempt> measured = [];
    private int outstanding;
    private int followUpIds;
    private DateTimeOffset lastCompletedAt;
    private long leases;
    private long censuses;

    private sealed record QueuedJob(ReplayJob Job, int Attempt, DateTimeOffset RunnableAt, string? LockGroup, string Lane);

    public Action<ReplayJob>? Arrived { get; set; }
    public Action<string, string>? BackoffEnded { get; set; }
    public Func<(long Leases, long Censuses)>? CountQueries { get; set; }

    public long Leases => CountQueries is { } count ? count().Leases : leases;
    public long Censuses => CountQueries is { } count ? count().Censuses : censuses;
    public int MaxRunning { get; private set; }
    public int MaxStoreCounted { get; private set; }
    public int OrderMinimumViolations { get; private set; }
    public int OrderSpacingWaits { get; private set; }
    public List<string> ViolationNotes { get; } = [];
    public IReadOnlyList<ReplayAttempt> Measured => measured;
    public bool Drained => outstanding == 0;
    public int Unfinished => outstanding;

    private static string? LockGroupOf(ReplayJob job) =>
        string.IsNullOrEmpty(job.LockGroup) ? (job.Connection is not null && ProcessorJobTypes.Contains(job.JobType) ? MarketplaceSyncExecutionLock.GroupFor(job.JobType) : null) : job.LockGroup;

    // A new job arrives (signal: its notification goes out; false for the backlog a worker starts with).
    public void Add(ReplayJob job, bool signal)
    {
        // A follow-up job (made with Id 0) gets an id of its own.
        if (job.Id == 0) job = job with { Id = -(++followUpIds) };
        outstanding++;
        Enqueue(job, 1, clock.UtcNow);
        if (signal) Arrived?.Invoke(job);
    }

    private void Enqueue(ReplayJob job, int attempt, DateTimeOffset runnableAt) =>
        queued.Add(new QueuedJob(job, attempt, runnableAt, LockGroupOf(job), DispatcherLanes.LaneFor(job.JobType, job.Priority)));

    public void Hold(string connection, string jobType, DateTimeOffset until) => held[(connection, jobType)] = until;

    public void Release(string connection, string jobType)
    {
        held.Remove((connection, jobType));
        holdEnded[(connection, jobType)] = clock.UtcNow;
    }

    // The kinds a backoff's end makes leasable (the due-time wake's BecameDueAsync).
    public IReadOnlyCollection<(string JobType, int Priority)> KindsOf(string connection, string group) =>
        [.. queued.Where(job => job.Job.Connection == connection && job.LockGroup == group).Select(job => (job.Job.JobType, job.Job.Priority)).Distinct()];

    public TimeSpan DurationOf(Application.LeasedJob job) => running[job.Id].Job.Attempt(running[job.Id].Attempt).Duration;

    private bool BackedOff(QueuedJob job, DateTimeOffset now) =>
        job.Job.Connection is { } connection && job.LockGroup is { } group && backoffs.TryGetValue((connection, group), out var until) && until > now;

    private bool Busy(QueuedJob job) =>
        job.Job.Connection is { } connection && job.LockGroup is { } group && running.Values.Any(other => other.Job.Connection == connection && other.LockGroup == group);

    private bool Capped(QueuedJob job, Application.JobConnectionCap? cap) =>
        cap is { Cap: > 0 } && job.Job.Connection is { } connection && cap.Caps(job.Job.JobType, job.Job.Priority)
        && running.Values.Count(other => other.Job.Connection == connection && cap.Caps(other.Job.JobType, other.Job.Priority)) >= cap.Cap;

    // JobDeferral's predicate: a job of the store queued ahead in the same lock group that the lease could take now (an ahead
    // type in the priority range, runnable, not backed off, not held by the store cap; the quota hold never holds the range).
    private bool Deferred(QueuedJob job, Application.JobDeferral? deferral, Application.JobConnectionCap? cap, DateTimeOffset now) =>
        deferral is not null && job.Job.Connection is { } connection
        && queued.Any(other => other != job && other.Job.Connection == connection && other.LockGroup == job.LockGroup
            && deferral.AheadJobTypes.Contains(other.Job.JobType) && other.Job.Priority >= deferral.MinimumAheadPriority && other.Job.Priority <= deferral.MaximumAheadPriority
            && other.RunnableAt <= now && !BackedOff(other, now) && !Capped(other, cap));

    private bool Leasable(QueuedJob candidate, int? maximumPriority, int? minimumPriority, IReadOnlyList<string> included, IReadOnlyList<string> excluded,
        Application.JobBulkhead? bulkhead, Application.JobConnectionCap? cap, Application.JobDeferral? deferral, DateTimeOffset now)
    {
        var job = candidate.Job;
        if (maximumPriority is { } maximum && job.Priority > maximum) return false;
        if (minimumPriority is { } minimum && job.Priority < minimum) return false;
        if (included.Count > 0 && !included.Contains(job.JobType)) return false;
        if (excluded.Contains(job.JobType)) return false;
        if (job.Connection is not { } connection) return true;
        if (Busy(candidate) || BackedOff(candidate, now)) return false;
        if (job.Priority > Application.JobQuotaHold.DefaultHeldAbovePriority && held.TryGetValue((connection, job.JobType), out var until) && until > now
            && candidate.RunnableAt > now - MaximumHold) return false;
        return !BulkheadFull(candidate, bulkhead) && !Capped(candidate, cap) && !Deferred(candidate, deferral, cap, now);
    }

    // A bulkhead class at its cap for the job's tenant and marketplace (JobBulkhead).
    private bool BulkheadFull(QueuedJob candidate, Application.JobBulkhead? bulkhead)
    {
        var job = candidate.Job;
        if (bulkhead is not { IsEmpty: false } || job.Connection is null || job.Priority <= Application.JobPriorityPolicy.BulkUserAction
            || job.Priority < bulkhead.ClassMinimumPriority || job.Priority > bulkhead.ClassMaximumPriority) return false;
        var types = bulkhead.JobTypes.ToList();
        var index = types.IndexOf(job.JobType);
        if (index < 0) return false;
        var className = bulkhead.Classes[index];
        var classCap = bulkhead.ClassCaps[bulkhead.ClassNames.ToList().IndexOf(className)];
        var classRunning = running.Values.Count(other => other.Job.Tenant == job.Tenant && other.Job.Platform == job.Platform
            && other.Job.Priority >= bulkhead.ClassMinimumPriority && other.Job.Priority <= bulkhead.ClassMaximumPriority
            && types.IndexOf(other.Job.JobType) is var otherIndex and >= 0 && bulkhead.Classes[otherIndex] == className);
        return classRunning >= classCap;
    }

    public Application.LeasedJob? Lease(int? maximumPriority, int? minimumPriority, IReadOnlyList<string> included, IReadOnlyList<string> excluded,
        Application.JobBulkhead? bulkhead, Application.JobConnectionCap? cap, Application.JobDeferral? deferral)
    {
        leases++;
        var now = clock.UtcNow;
        var pick = queued.Where(candidate => candidate.RunnableAt <= now && Leasable(candidate, maximumPriority, minimumPriority, included, excluded, bulkhead, cap, deferral, now))
            .OrderBy(candidate => candidate.Job.Priority)
            .ThenByDescending(candidate => Aged(candidate, now))
            .ThenBy(candidate => Aged(candidate, now) ? candidate.RunnableAt : DateTimeOffset.MinValue)
            .ThenBy(candidate => running.Values.Count(other => other.Job.Tenant == candidate.Job.Tenant))
            .ThenBy(candidate => candidate.Job.Connection is null || candidate.Job.Priority <= Application.JobPriorityPolicy.BulkUserAction ? 0 : running.Values.Count(other => other.Job.Connection == candidate.Job.Connection))
            .ThenBy(candidate => candidate.Job.Connection is { } connection && candidate.Job.Priority > Application.JobPriorityPolicy.BulkUserAction && lastLeased.TryGetValue(connection, out var at) ? at : DateTimeOffset.MinValue)
            .ThenBy(candidate => candidate.RunnableAt)
            .ThenBy(candidate => candidate.Job.Id)
            .FirstOrDefault();
        if (pick is null) return null;
        queued.Remove(pick);
        var id = Guid.NewGuid();
        running[id] = pick;
        if (pick.Job.Connection is { } leasedConnection) lastLeased[leasedConnection] = now;
        Measure(pick, now);
        if (pick.Job.Connection is { } store && pick.LockGroup is { } group)
        {
            if (!runs.TryGetValue((store, group), out var list)) runs[(store, group)] = list = [];
            list.Add((now, null, pick.Job.JobType));
        }
        MaxRunning = Math.Max(MaxRunning, running.Count);
        var capRule = DispatcherLanes.ConnectionCap(settings);
        if (pick.Job.Connection is { } capStore)
            MaxStoreCounted = Math.Max(MaxStoreCounted, running.Values.Count(other => other.Job.Connection == capStore && capRule.Caps(other.Job.JobType, other.Job.Priority)));
        return new Application.LeasedJob(id, Guid.Empty, pick.Job.Connection is { } connectionText ? StoreGuid(connectionText) : null, pick.Job.JobType, "{}", "replay", "token", now - pick.RunnableAt, pick.Job.Priority);
    }

    private static bool Aged(QueuedJob candidate, DateTimeOffset now) =>
        candidate.Job.Connection is not null && candidate.Job.Priority > Application.JobPriorityPolicy.BulkUserAction && candidate.RunnableAt <= now - Aging;

    // A stable id for a store (the dispatcher keys the lanes waiting for a store by it).
    public static Guid StoreGuid(string connection)
    {
        Span<byte> bytes = stackalloc byte[16];
        var hash = System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(connection));
        hash.CopyTo(bytes);
        return new Guid(bytes);
    }

    private void Measure(QueuedJob pick, DateTimeOffset now)
    {
        var sameType = TimeSpan.Zero;
        var free = pick.RunnableAt;
        if (pick.Job.Connection is { } store && pick.LockGroup is { } group)
        {
            if (runs.TryGetValue((store, group), out var list))
                foreach (var (from, to, type) in list)
                {
                    if (type != pick.Job.JobType) continue;
                    var start = from > pick.RunnableAt ? from : pick.RunnableAt;
                    var end = to ?? now;
                    if (end > now) end = now;
                    if (end > start) sameType += end - start;
                }
            if (lastOpened.TryGetValue((store, group), out var opened) && opened > free) free = opened;
            if (holdEnded.TryGetValue((store, pick.Job.JobType), out var ended) && ended > free) free = ended;
        }
        var delay = now - pick.RunnableAt - sameType;
        measured.Add(new ReplayAttempt(pick.Job.Id, pick.Attempt, ReplayHarness.WorkType(pick.Job.JobType, pick.Job.Priority), pick.Job.Connection,
            delay < TimeSpan.Zero ? TimeSpan.Zero : delay, now - free < TimeSpan.Zero ? TimeSpan.Zero : now - free)
        { Started = now, Duration = pick.Job.Attempt(pick.Attempt).Duration, JobType = pick.Job.JobType });
    }

    // The census: each kind per connection with the store that alone holds it now (its busy lock group, the cap, its order read
    // queued ahead, or the store running a full bulkhead class of cap 1), as QueueHealthService.RunnableJobKindsAsync.
    public IReadOnlyCollection<LaneDispatcher.CensusRow> Census(Application.JobConnectionCap? cap, IReadOnlyList<Application.JobConnectionCap.Rule> deferredRules, Application.JobDeferral? deferral, Application.JobBulkhead? bulkhead)
    {
        censuses++;
        var now = clock.UtcNow;
        var deferredKinds = new Application.JobConnectionCap(1, deferredRules);
        return [.. queued.Where(job => job.RunnableAt <= now && !BackedOff(job, now))
            .GroupBy(job => (job.Job.JobType, job.Job.Priority, job.Job.Connection, HeldBy: HeldBy(job, cap, deferredKinds, deferral, bulkhead, now)))
            .Select(group => new LaneDispatcher.CensusRow(group.Key.JobType, group.Key.Priority, group.Key.Connection is { } connection ? StoreGuid(connection) : null, group.Key.HeldBy))];
    }

    private Guid? HeldBy(QueuedJob job, Application.JobConnectionCap? cap, Application.JobConnectionCap deferredKinds, Application.JobDeferral? deferral, Application.JobBulkhead? bulkhead, DateTimeOffset now)
    {
        if (job.Job.Connection is not { } connection) return null;
        if (Busy(job) || Capped(job, cap) || (deferredKinds.Caps(job.Job.JobType, job.Job.Priority) && Deferred(job, deferral, cap, now))) return StoreGuid(connection);
        if (bulkhead is null || !BulkheadFull(job, bulkhead)) return null;
        var types = bulkhead.JobTypes.ToList();
        var className = bulkhead.Classes[types.IndexOf(job.Job.JobType)];
        if (bulkhead.ClassCaps[bulkhead.ClassNames.ToList().IndexOf(className)] != 1) return null;
        return running.Values.Where(other => other.Job.Tenant == job.Job.Tenant && other.Job.Platform == job.Job.Platform && other.Job.Connection is not null
                && other.Job.Priority >= bulkhead.ClassMinimumPriority && other.Job.Priority <= bulkhead.ClassMaximumPriority
                && types.IndexOf(other.Job.JobType) is var index and >= 0 && bulkhead.Classes[index] == className)
            .Select(other => StoreGuid(other.Job.Connection!)).OrderBy(id => id.ToString()).Cast<Guid?>().FirstOrDefault();
    }

    // A slot goes to work outside siparis-okuma while an order read could be leased and siparis-okuma runs fewer than its
    // minimum (Y1, 23 Nov). Checked when the dispatcher decides to lease for a catalogue, background, return or first-import lane.
    // An order lane in its retry spacing is not a violation: its last lease found nothing less than a second ago and the store
    // or class holding it freed before the census could name it; it tries again within DispatcherCore.RetrySpacing (its
    // reserved slots stay free meanwhile). Those moments are counted in OrderSpacingWaits.
    public void CheckOrderMinimum(string leasingLane, Func<string>? state = null, bool ordersSpacing = false)
    {
        if (leasingLane is not (DispatcherLanes.Returns or DispatcherLanes.Catalog or DispatcherLanes.Background or DispatcherLanes.InitialSync)) return;
        var now = clock.UtcNow;
        if (lastCompletedAt == now) return;
        if (ordersSpacing)
        {
            OrderSpacingWaits++;
            return;
        }
        if (running.Values.Count(job => job.Lane == DispatcherLanes.OrderReads) >= settings.ReservedOf(DispatcherLanes.OrderReads)) return;
        var lane = DispatcherLanes.All(WorkerLanes.DefaultHotPriorityCeiling, settings).First(item => item.Name == DispatcherLanes.OrderReads);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Worker:LaneModel"] = "dispatcher" }).Build();
        var bulkhead = BulkheadSettings.From(configuration, true).For(lane.Name, WorkerLanes.DefaultHotPriorityCeiling);
        // A job queued at this very moment may be announced a step after another one of the same moment: not a wait.
        var waiting = queued.Where(candidate => candidate.Lane == DispatcherLanes.OrderReads && candidate.RunnableAt < now - NotifyGrace
            && Leasable(candidate, lane.MaximumPriority, lane.MinimumPriority, lane.IncludedJobTypes, lane.ExcludedJobTypes, bulkhead, null, null, now)).ToList();
        if (waiting.Count == 0) return;
        OrderMinimumViolations++;
        if (ViolationNotes.Count < 20) ViolationNotes.Add($"{now:HH:mm:ss.fff} {leasingLane}: {state?.Invoke()}; leasable {string.Join(",", waiting.Select(item => $"{item.Job.JobType}@{item.Job.Connection}"))}");
    }

    public void Complete(Application.LeasedJob leased)
    {
        var entry = running[leased.Id];
        running.Remove(leased.Id);
        var now = clock.UtcNow;
        lastCompletedAt = now;
        CloseRun(entry, now);
        var (_, succeeded) = entry.Job.Attempt(entry.Attempt);
        if (succeeded || entry.Attempt >= entry.Job.MaxAttempts)
        {
            outstanding--;
            if (succeeded)
                foreach (var (after, followUp) in entry.Job.FollowUps ?? [])
                    clock.Schedule(now + after, () => Add(followUp, signal: true));
            return;
        }
        // A failed attempt: retried after the table's delay (jitter 0-20 % by the job), its notification is the due-time wake.
        var delay = RetryDelays[Math.Min(entry.Attempt - 1, RetryDelays.Length - 1)];
        var jitter = (entry.Job.Id * 7919 % 100) / 500.0;
        var at = now + TimeSpan.FromTicks((long)(delay.Ticks * (1 + jitter)));
        if (entry.Job.BackoffOnFailure is { } backoff && entry.Job.Connection is { } connection && entry.LockGroup is { } group)
        {
            backoffs[(connection, group)] = now + backoff;
            clock.Schedule(now + backoff, () =>
            {
                if (backoffs.TryGetValue((connection, group), out var until) && until <= clock.UtcNow)
                {
                    backoffs.Remove((connection, group));
                    lastOpened[(connection, group)] = clock.UtcNow;
                    BackoffEnded?.Invoke(connection, group);
                }
            });
        }
        clock.Schedule(at, () =>
        {
            Enqueue(entry.Job, entry.Attempt + 1, clock.UtcNow);
            Arrived?.Invoke(entry.Job with { Signal = ReplaySignal.Due });
        });
    }

    private void CloseRun(QueuedJob entry, DateTimeOffset now)
    {
        if (entry.Job.Connection is not { } store || entry.LockGroup is not { } group) return;
        lastOpened[(store, group)] = now;
        if (!runs.TryGetValue((store, group), out var list)) return;
        var index = list.FindLastIndex(item => item.To is null && item.JobType == entry.Job.JobType);
        if (index >= 0) list[index] = (list[index].From, now, list[index].JobType);
        if (list.Count > 64) list.RemoveRange(0, list.Count - 64);
    }

    // A restart: the running jobs go back to the queue as runnable when the worker is up again (the attempt refunded).
    public void ReleaseRunning()
    {
        var now = clock.UtcNow;
        foreach (var entry in running.Values.ToList())
        {
            CloseRun(entry, now);
            Enqueue(entry.Job, entry.Attempt, now + ReplayStream.RestartDowntime);
        }
        running.Clear();
    }
}
