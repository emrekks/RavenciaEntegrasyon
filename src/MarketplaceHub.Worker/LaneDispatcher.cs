using MarketplaceHub.Application;
using MarketplaceHub.Infrastructure.Jobs;

namespace MarketplaceHub.Worker;

// Queue phase 2 (27 Nov 2026): the one loop that hands free slots to lanes (Worker:LaneModel=dispatcher). It replaces the
// lanes' own and borrowing loops (LaneSlotPool, 220 loops) with one decision per event (DispatcherCore):
//   - it learns about work from the job notifications (Route: "job:<type>:<priority>" makes that lane Ready, "freed" retries
//     the blocked lanes), from jobs that became due (NoteDue, the due-time wake), from its own jobs' ends, from the end of a
//     quota hold (Unblock), and from the census: one query of which lanes have runnable jobs, read after leases that found
//     nothing (to tell blocked from empty) and every safety tick (a lost notification costs one tick at most);
//   - it leases one job at a time (a lease is one query of a few milliseconds), so its own leases never race each other on
//     the per-store cap; the lease query decides which job, the dispatcher only which lane;
//   - idle, it waits for a signal or the next tick: no lease is tried for a lane without a hint, and a failing lease backs
//     its lane off (1, 2, 4 ... 15 s) instead of spinning;
//   - each job runs on its own task; the shutdown stops the loop and waits for the running jobs (a writing job finishes, as
//     with the lane loops: Worker.ExecuteLeasedJobAsync).
public sealed class LaneDispatcher
{
    public sealed record Lane(WorkerLane Definition, int Reserved, int Share);

    private readonly object gate = new();
    private readonly Lane[] lanes;
    private readonly DispatcherCore core;
    private readonly TimeProvider clock;
    private readonly Func<WorkerLane, bool, CancellationToken, Task<LeasedJob?>> lease;
    private readonly Func<CancellationToken, Task<IReadOnlyCollection<(string JobType, int Priority)>>> census;
    private readonly Func<LeasedJob, string, Task> run;
    private readonly Action<string, Exception> onFailure;
    private readonly Func<Task, TimeSpan, CancellationToken, Task<bool>> wait;
    private readonly TimeSpan safetyTick;
    private readonly HashSet<Task> runs = [];
    private TaskCompletionSource kick = NewKick();
    private DateTimeOffset nextSafetyCensus;
    private DateTimeOffset censusRetryAt;
    private int censusFaults;

    // lease(lane, outsideShare, token): lease one job of the lane (null: none); outsideShare goes on its attempt row (Borrowed).
    // census(token): the type and priority of every runnable queued job (distinct). run(job, lane): run a leased job to its end.
    // wait(woken, delay, token): true when woken (tests pass a simulated clock's).
    public LaneDispatcher(IReadOnlyList<Lane> lanes, TimeProvider clock,
        Func<WorkerLane, bool, CancellationToken, Task<LeasedJob?>> lease,
        Func<CancellationToken, Task<IReadOnlyCollection<(string JobType, int Priority)>>> census,
        Func<LeasedJob, string, Task> run,
        Action<string, Exception> onFailure,
        TimeSpan? safetyTick = null,
        Func<Task, TimeSpan, CancellationToken, Task<bool>>? wait = null)
    {
        this.lanes = [.. lanes];
        this.clock = clock;
        this.lease = lease;
        this.census = census;
        this.run = run;
        this.onFailure = onFailure;
        this.wait = wait ?? JobWakeSignal.WaitAsync;
        this.safetyTick = safetyTick ?? DispatcherSettings.DefaultSafetyTick;
        var now = clock.GetUtcNow();
        core = new DispatcherCore([.. this.lanes.Select(lane => new DispatcherCore.LaneConfig(lane.Definition.Name, lane.Reserved, lane.Share))], now);
        nextSafetyCensus = now + this.safetyTick;
    }

    public int Total => core.Total;

    // Counters (tests, the replay, the startup log).
    public long Leases { get; private set; }
    public long EmptyLeases { get; private set; }
    public long Censuses { get; private set; }

    public static LaneDispatcher.Lane[] LanesFrom(IReadOnlyList<WorkerLane> definitions, DispatcherSettings settings) =>
        [.. definitions.Select(lane => new Lane(lane, settings.ReservedOf(lane.Name), settings.ShareOf(lane.Name)))];

    private int LaneIndex(string jobType, int priority) => Array.FindIndex(lanes, lane => lane.Definition.Takes(jobType, priority));

    // A job notification (JobWakeRouter's payloads): a named job makes its lane Ready; "freed" (a leased job ended, here or in
    // another process) retries the blocked lanes; anything else (an old trigger's empty payload) reads the census at once.
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

    // A job ended elsewhere, or a quota hold ended: the blocked lanes try once more.
    public void Unblock()
    {
        lock (gate)
        {
            core.Unblock();
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
                Task woken;
                var now = clock.GetUtcNow();
                int lane;
                var outsideShare = false;
                long epoch = 0;
                var censusNow = false;
                var safety = false;
                TimeSpan delay;
                lock (gate)
                {
                    // Taken before the state is read: a signal that changes the state after this point completes it.
                    if (kick.Task.IsCompleted) kick = NewKick();
                    woken = kick.Task;
                    safety = now >= nextSafetyCensus;
                    var (kind, planned) = core.Plan(now, safety, now >= censusRetryAt);
                    lane = kind == DispatcherCore.StepKind.Lease ? planned : -1;
                    censusNow = kind == DispatcherCore.StepKind.Census;
                    if (lane >= 0) (epoch, outsideShare) = core.BeginLease(lane);
                    // The next census: the safety tick, or now when a lease found nothing, never before a failed census's retry.
                    var until = core.NeedsCensus && nextSafetyCensus > now ? now : nextSafetyCensus;
                    if (until < censusRetryAt) until = censusRetryAt;
                    if (core.NextRetry(now) is { } retry && retry < until) until = retry;
                    delay = until - now;
                }
                if (lane >= 0)
                {
                    await LeaseAsync(lane, epoch, outsideShare, stoppingToken);
                    continue;
                }
                if (censusNow)
                {
                    await CensusAsync(safety, stoppingToken);
                    continue;
                }
                if (delay < JobIdleWait.MinimumDueWait) delay = JobIdleWait.MinimumDueWait;
                await wait(woken, delay, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            // The running jobs end on their own (the shutdown cancels a reading job, a writing job finishes).
            Task[] running;
            lock (gate) running = [.. runs];
            await Task.WhenAll(running.Select(task => task.ContinueWith(_ => { }, TaskScheduler.Default)));
        }
    }

    private async Task LeaseAsync(int lane, long epoch, bool outsideShare, CancellationToken stoppingToken)
    {
        LeasedJob? job;
        try
        {
            job = await lease(lanes[lane].Definition, outsideShare, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            lock (gate) core.LeaseAbandoned(lane);
            return;
        }
        catch (Exception exception)
        {
            lock (gate) core.LeaseFailed(lane, clock.GetUtcNow());
            onFailure($"lease for lane {lanes[lane].Definition.Name}", exception);
            return;
        }
        lock (gate)
        {
            Leases++;
            if (job is null)
            {
                EmptyLeases++;
                core.LeaseFoundNothing(lane, epoch);
                return;
            }
            core.Leased(lane, clock.GetUtcNow());
            // A job leased just as the shutdown began still runs: Worker.ExecuteLeasedJobAsync hands it back to the queue.
            var task = Task.Run(() => RunJobAsync(job, lane), CancellationToken.None);
            runs.Add(task);
            _ = task.ContinueWith(done =>
            {
                lock (gate) runs.Remove(done);
            }, TaskScheduler.Default);
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
                core.Ended(lane, clock.GetUtcNow());
                Kick();
            }
        }
    }

    private async Task CensusAsync(bool safety, CancellationToken stoppingToken)
    {
        long[] epochs;
        lock (gate) epochs = core.Epochs();
        IReadOnlyCollection<(string JobType, int Priority)> rows;
        try
        {
            rows = await census(stoppingToken);
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
        var withRows = new bool[lanes.Length];
        foreach (var (jobType, priority) in rows)
            if (LaneIndex(jobType, priority) is var index and >= 0) withRows[index] = true;
        lock (gate)
        {
            Censuses++;
            censusFaults = 0;
            censusRetryAt = default;
            core.ApplyCensus(withRows, epochs, safety);
            if (safety) nextSafetyCensus = clock.GetUtcNow() + safetyTick;
        }
    }

    // Queue phase 0 recording: the spells in which the job's lane had no free slot (the dispatcher's lanes are disjoint: one).
    public IReadOnlyList<(DateTimeOffset From, DateTimeOffset To)> NoRoomSpells(IReadOnlyList<string> laneNames, DateTimeOffset from, DateTimeOffset to)
    {
        lock (gate)
        {
            var index = laneNames.Count == 1 ? Array.FindIndex(lanes, lane => lane.Definition.Name == laneNames[0]) : -1;
            return index < 0 ? [] : core.NoRoomSpellsOf(index, from, to, clock.GetUtcNow());
        }
    }

    // The job tasks not yet ended (their end told to the core): the replay counts them as busy until then.
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
