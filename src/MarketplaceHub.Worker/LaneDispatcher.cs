using MarketplaceHub.Application;
using MarketplaceHub.Infrastructure.Jobs;

namespace MarketplaceHub.Worker;

// Queue phase 2 (27 Nov 2026): the one loop that hands free slots to lanes (Worker:LaneModel=dispatcher). It replaces the
// lanes' own and borrowing loops (LaneSlotPool, 220 loops) with one decision per event (DispatcherCore):
//   - it learns about work from the job notifications (Route: "job:<type>:<priority>" makes that lane Ready, "freed" retries
//     the lanes held for an unnamed reason), from jobs that became due (NoteDue, the due-time wake), from its own jobs' ends
//     (the lanes waiting for that store), from the end of a quota hold (Unblock), and from the census: one query of the
//     runnable jobs per lane and store and whether only their own store holds them, read after leases that found nothing and
//     every safety tick (a lost notification costs one tick at most; 5 s while a kullanici, webhook or yazma job waits);
//   - it leases one job at a time (a lease is one query of a few milliseconds), so its own leases never race each other on the
//     per-store cap; the lease query decides which job, the dispatcher only which lane. A lease or census that takes longer
//     than LeaseTimeout is cancelled and its lane backs off, so one slow query never holds the other lanes for minutes;
//   - idle, it waits for a signal or the next tick; a failing lease backs its lane off (1, 2, 4 ... 15 s) instead of spinning;
//   - a turn that throws is logged and the loop goes on after a second (it never ends silently); LastTurnAt tells the
//     worker's watchdog that it still turns;
//   - each job runs on its own task; the shutdown stops the loop and waits for the running jobs (a writing job finishes, as
//     with the lane loops: Worker.ExecuteLeasedJobAsync).
public sealed class LaneDispatcher
{
    // One lane. Filters: the lease filters it passes, tried in turn (toplu has two, DispatcherLanes.LeaseFilters).
    public sealed record Lane(WorkerLane Definition, int Reserved, int Share)
    {
        public IReadOnlyList<WorkerLane> Filters { get; init; } = [Definition];
        // A lane of bulk work: beyond its share it gets a shared slot only after every other lane (DispatcherCore).
        public bool Bulk { get; init; }
        public bool Takes(string jobType, int priority) => Filters.Any(filter => filter.Takes(jobType, priority));
    }

    // A runnable queued job kind as the census sees it, with HeldBy the store whose running or queued job alone keeps it waiting
    // (a busy lock group, the store cap, an order read queued ahead in its lock group, a full bulkhead class of cap 1); null: free.
    public readonly record struct CensusRow(string JobType, int Priority, Guid? ConnectionId, Guid? HeldBy);

    public sealed record Options(TimeSpan SafetyTick, TimeSpan LeaseTimeout, IReadOnlyCollection<string> ShortTickLanes, TimeSpan ShortSafetyTick)
    {
        public static Options Default { get; } = new(DispatcherSettings.DefaultSafetyTick, DispatcherSettings.DefaultLeaseTimeout, DispatcherSettings.ShortTickLanes, DispatcherSettings.ShortSafetyTick);
    }

    public static readonly TimeSpan TurnFailureDelay = TimeSpan.FromSeconds(1);

    private readonly object gate = new();
    private readonly Lane[] lanes;
    private readonly DispatcherCore core;
    private readonly TimeProvider clock;
    private readonly Func<Lane, bool, CancellationToken, Task<LeasedJob?>> lease;
    private readonly Func<CancellationToken, Task<IReadOnlyCollection<CensusRow>>> census;
    private readonly Func<LeasedJob, string, Task> run;
    private readonly Action<string, Exception> onFailure;
    private readonly Func<Task, TimeSpan, CancellationToken, Task<bool>> wait;
    private readonly Options options;
    private readonly int[] shortTickLanes;
    private readonly HashSet<Task> runs = [];
    private TaskCompletionSource kick = NewKick();
    private DateTimeOffset nextSafetyCensus;
    private DateTimeOffset lastSafetyCensus;
    private DateTimeOffset censusRetryAt;
    private int censusFaults;
    private long lastTurnTicks;

    // lease(lane, outsideShare, token): lease one job of the lane (null: none); outsideShare goes on its attempt row (Borrowed).
    // census(token): the runnable queued job kinds (CensusRow). run(job, lane): run a leased job to its end.
    // wait(woken, delay, token): true when woken (tests pass a simulated clock's).
    public LaneDispatcher(IReadOnlyList<Lane> lanes, TimeProvider clock,
        Func<Lane, bool, CancellationToken, Task<LeasedJob?>> lease,
        Func<CancellationToken, Task<IReadOnlyCollection<CensusRow>>> census,
        Func<LeasedJob, string, Task> run,
        Action<string, Exception> onFailure,
        Options? options = null,
        Func<Task, TimeSpan, CancellationToken, Task<bool>>? wait = null)
    {
        this.lanes = [.. lanes];
        this.clock = clock;
        this.lease = lease;
        this.census = census;
        this.run = run;
        this.onFailure = onFailure;
        this.wait = wait ?? JobWakeSignal.WaitAsync;
        this.options = options ?? Options.Default;
        shortTickLanes = [.. this.lanes.Select((lane, index) => (lane, index)).Where(item => this.options.ShortTickLanes.Contains(item.lane.Definition.Name)).Select(item => item.index)];
        var now = clock.GetUtcNow();
        core = new DispatcherCore([.. this.lanes.Select(lane => new DispatcherCore.LaneConfig(lane.Definition.Name, lane.Reserved, lane.Share, lane.Bulk))]);
        lastSafetyCensus = now;
        nextSafetyCensus = now + this.options.SafetyTick;
        lastTurnTicks = now.UtcTicks;
    }

    public int Total => core.Total;

    // Counters (tests, the replay, the startup log).
    public long Leases { get; private set; }
    public long EmptyLeases { get; private set; }
    public long Censuses { get; private set; }

    // When the loop last began a turn (the worker's watchdog: a loop that stopped turning makes the worker unhealthy).
    public DateTimeOffset LastTurnAt => new(Interlocked.Read(ref lastTurnTicks), TimeSpan.Zero);

    public static Lane[] LanesFrom(IReadOnlyList<WorkerLane> definitions, DispatcherSettings settings) =>
        [.. definitions.Select(lane => new Lane(lane, settings.ReservedOf(lane.Name), settings.ShareOf(lane.Name)) { Filters = DispatcherLanes.LeaseFilters(lane), Bulk = DispatcherLanes.IsBulk(lane.Name) })];

    private int LaneIndex(string jobType, int priority) => Array.FindIndex(lanes, lane => lane.Takes(jobType, priority));

    // A job notification (JobWakeRouter's payloads): a named job makes its lane Ready; "freed" (a leased job ended, here or in
    // another process) retries the lanes held for an unnamed reason; anything else (an old trigger's empty payload) reads the
    // census at once.
    public void Route(string? payload)
    {
        if (string.Equals(payload, JobWakeRouter.Freed, StringComparison.Ordinal))
        {
            Unblock();
            return;
        }
        if (JobWakeRouter.Parse(payload) is { } job) Notify(job.JobType, job.Priority);
        else CensusSoon();
    }

    public void Notify(string jobType, int priority)
    {
        lock (gate)
        {
            var lane = LaneIndex(jobType, priority);
            if (lane < 0) return;
            core.Notify(lane);
            Kick();
        }
    }

    // Jobs that became leasable because time passed (their AvailableAt, the end of a quota backoff): no notification came.
    public void NoteDue(IReadOnlyCollection<(string JobType, int Priority)> jobs)
    {
        lock (gate)
        {
            foreach (var lane in jobs.Select(job => LaneIndex(job.JobType, job.Priority)).Where(lane => lane >= 0).Distinct())
                core.Notify(lane);
            Kick();
        }
    }

    // A job ended elsewhere, or a quota hold ended: the lanes held for an unnamed reason try again.
    public void Unblock()
    {
        lock (gate)
        {
            core.RetryBlocked(clock.GetUtcNow());
            Kick();
        }
    }

    // The notification listener (re)connected, or a payload was not understood: read which lanes have work now.
    public void CensusSoon()
    {
        lock (gate)
        {
            nextSafetyCensus = clock.GetUtcNow();
            censusRetryAt = default;
            Kick();
        }
    }

    // Called under the lock.
    private void Kick() => kick.TrySetResult();

    private static TaskCompletionSource NewKick() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task RunAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await TurnAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
                catch (Exception exception)
                {
                    // Never silently: logged, the state left consistent, and the loop goes on after a second.
                    lock (gate)
                        if (core.LeasingLane >= 0) core.LeaseAbandoned(core.LeasingLane);
                    onFailure("dispatcher turn", exception);
                    try { await wait(new TaskCompletionSource().Task, TurnFailureDelay, stoppingToken); }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
                }
            }
        }
        finally
        {
            // The running jobs end on their own (the shutdown cancels a reading job, a writing job finishes).
            Task[] running;
            lock (gate) running = [.. runs];
            await Task.WhenAll(running.Select(task => task.ContinueWith(_ => { }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default)));
        }
    }

    private async Task TurnAsync(CancellationToken stoppingToken)
    {
        Task woken;
        var now = clock.GetUtcNow();
        Interlocked.Exchange(ref lastTurnTicks, now.UtcTicks);
        int lane;
        var outsideShare = false;
        long epoch = 0;
        bool censusNow, safety;
        TimeSpan delay;
        lock (gate)
        {
            // Taken before the state is read: a signal that changes the state after this point completes it.
            if (kick.Task.IsCompleted) kick = NewKick();
            woken = kick.Task;
            // The short tick while a kullanici, webhook or yazma job waits for a blocker: their targets are 5 s.
            var dueAt = core.AnyWaiting(shortTickLanes) && lastSafetyCensus + options.ShortSafetyTick < nextSafetyCensus ? lastSafetyCensus + options.ShortSafetyTick : nextSafetyCensus;
            safety = now >= dueAt;
            var (kind, planned) = core.Plan(now, safety, now >= censusRetryAt);
            lane = kind == DispatcherCore.StepKind.Lease ? planned : -1;
            censusNow = kind == DispatcherCore.StepKind.Census;
            if (lane >= 0) (epoch, outsideShare) = core.BeginLease(lane);
            // The next census: the tick, or now when a lease found nothing, never before a failed census's retry.
            var until = core.NeedsCensus && dueAt > now ? now : dueAt;
            if (until < censusRetryAt) until = censusRetryAt;
            if (core.NextRetry(now) is { } retry && retry < until) until = retry;
            delay = until - now;
        }
        if (lane >= 0)
        {
            await LeaseAsync(lane, epoch, outsideShare, stoppingToken);
            return;
        }
        if (censusNow)
        {
            await CensusAsync(safety, stoppingToken);
            return;
        }
        if (delay < JobIdleWait.MinimumDueWait) delay = JobIdleWait.MinimumDueWait;
        await wait(woken, delay, stoppingToken);
    }

    private async Task LeaseAsync(int lane, long epoch, bool outsideShare, CancellationToken stoppingToken)
    {
        LeasedJob? job;
        using var timeout = new CancellationTokenSource(options.LeaseTimeout, clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, timeout.Token);
        try
        {
            job = await lease(lanes[lane], outsideShare, linked.Token);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            lock (gate) core.LeaseAbandoned(lane);
            return;
        }
        catch (Exception exception)
        {
            lock (gate) core.LeaseFailed(lane, clock.GetUtcNow());
            onFailure(timeout.IsCancellationRequested ? $"lease for lane {lanes[lane].Definition.Name} (over {options.LeaseTimeout.TotalSeconds:0} s)" : $"lease for lane {lanes[lane].Definition.Name}", exception);
            return;
        }
        lock (gate)
        {
            Leases++;
            if (job is null)
            {
                EmptyLeases++;
                core.LeaseFoundNothing(lane, epoch, clock.GetUtcNow());
                return;
            }
            core.Leased(lane);
            // A job leased just as the shutdown began still runs: Worker.ExecuteLeasedJobAsync hands it back to the queue. It runs on
            // the scheduler of the caller's context when there is one (the replay's single thread), else on the thread pool.
            var scheduler = SynchronizationContext.Current is null ? TaskScheduler.Default : TaskScheduler.FromCurrentSynchronizationContext();
            var task = Task.Factory.StartNew(() => RunJobAsync(job, lane), CancellationToken.None, TaskCreationOptions.DenyChildAttach, scheduler).Unwrap();
            runs.Add(task);
            _ = task.ContinueWith(done =>
            {
                lock (gate) runs.Remove(done);
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    private async Task RunJobAsync(LeasedJob job, int lane)
    {
        try
        {
            await run(job, lanes[lane].Definition.Name);
        }
        catch (Exception exception)
        {
            onFailure($"job {job.Id} of lane {lanes[lane].Definition.Name}", exception);
        }
        finally
        {
            lock (gate)
            {
                core.Ended(lane, job.ConnectionId, clock.GetUtcNow());
                Kick();
            }
        }
    }

    private async Task CensusAsync(bool safety, CancellationToken stoppingToken)
    {
        (long[] Epochs, long EndedSequence) start;
        lock (gate) start = core.CensusStart();
        IReadOnlyCollection<CensusRow> rows;
        using var timeout = new CancellationTokenSource(options.LeaseTimeout, clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, timeout.Token);
        try
        {
            rows = await census(linked.Token);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception)
        {
            lock (gate)
            {
                censusFaults = Math.Min(censusFaults + 1, 16);
                censusRetryAt = clock.GetUtcNow() + TimeSpan.FromTicks(Math.Min(DispatcherCore.MaximumFaultDelay.Ticks, DispatcherCore.FirstFaultDelay.Ticks << (censusFaults - 1)));
            }
            onFailure("census", exception);
            return;
        }
        var free = new bool[lanes.Length];
        var held = lanes.Select(_ => new HashSet<Guid>()).ToArray();
        foreach (var row in rows)
        {
            if (LaneIndex(row.JobType, row.Priority) is not (var index and >= 0)) continue;
            if (row.HeldBy is { } store) held[index].Add(store);
            else free[index] = true;
        }
        lock (gate)
        {
            Censuses++;
            censusFaults = 0;
            censusRetryAt = default;
            var now = clock.GetUtcNow();
            core.ApplyCensus([.. Enumerable.Range(0, lanes.Length).Select(index => new DispatcherCore.LaneCensus(free[index], held[index]))], start, safety, now);
            if (safety)
            {
                lastSafetyCensus = now;
                nextSafetyCensus = now + options.SafetyTick;
            }
        }
    }

    // The job tasks not yet ended (their end told to the core).
    internal int RunningTasks
    {
        get { lock (gate) return runs.Count; }
    }

    // State for tests and the replay (under the lock).
    internal T Read<T>(Func<DispatcherCore, T> read)
    {
        lock (gate) return read(core);
    }
}
