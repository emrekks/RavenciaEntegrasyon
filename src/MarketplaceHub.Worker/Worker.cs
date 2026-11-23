using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace MarketplaceHub.Worker;

public sealed class Worker(IServiceScopeFactory scopeFactory, ILogger<Worker> logger, IConfiguration configuration, IHostApplicationLifetime applicationLifetime) : BackgroundService
{
    // The lanes (and the job types each takes) are defined in WorkerLanes, shared with the queue metrics.
    internal static readonly string[] OrderStatusLaneJobTypes = [.. MarketplaceHub.Infrastructure.Jobs.WorkerLanes.OrderStatusJobTypes];
    private readonly string healthFile = configuration["Worker:HealthFile"] ?? "/tmp/marketplacehub-worker-heartbeat";
    private readonly TimeSpan schedulerScanInterval = TimeSpan.FromSeconds(Math.Clamp(configuration.GetValue("Worker:SchedulerScanSeconds", 10), 1, 30));
    private readonly int hotPriorityCeiling = Math.Clamp(configuration.GetValue("Worker:HotPriorityCeiling", 2), 0, 5);
    private readonly TimeSpan healthStaleAfter = TimeSpan.FromSeconds(Math.Clamp(configuration.GetValue("Worker:HealthStaleAfterSeconds", 120), 30, 900));
    private readonly bool schedulerEnabled = configuration.GetValue("Worker:EnableScheduler", true);
    private readonly string? singleJobIdText = string.IsNullOrWhiteSpace(configuration["Worker:SingleJobId"]) ? null : configuration["Worker:SingleJobId"]!.Trim();
    private readonly Guid? singleJobId = Guid.TryParse(configuration["Worker:SingleJobId"], out var parsedJobId) ? parsedJobId : null;
    private readonly string? singleJobType = string.IsNullOrWhiteSpace(configuration["Worker:SingleJobType"]) ? null : configuration["Worker:SingleJobType"]!.Trim();
    private long lastDatabaseContactUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    // Pulsed on every job notification: the due-time wake reads the next due moment again.
    private readonly JobWakeSignal wakeSignal = new();
    // KUYRUK-6: wakes the idle slots of the lanes a notification concerns (set up with the lanes in ExecuteAsync).
    private JobWakeRouter wakeRouter = new([]);
    // Asks the due-time wake to read the next due moment again (a scheduler pass enqueued jobs).
    private readonly JobWakeSignal dueRecheck = new();
    private static readonly TimeSpan DueRecheckDebounce = TimeSpan.FromSeconds(1);
    // Worker lanes step 1: quota-aware leasing (QuotaPressure, JobQuotaHold). Worker:QuotaAwareLeasing:Enabled=false restores
    // the earlier behaviour (a job waits for its quota inside its slot).
    private readonly QuotaAwareLeasingSettings quotaAwareLeasing = QuotaAwareLeasingSettings.From(configuration);
    // Worker lanes step 2: connection fairness and per-marketplace bulkheads (Worker:Fairness:*, Worker:Bulkhead:*).
    private readonly FairnessSettings fairnessSettings = FairnessSettings.From(configuration);
    // Worker lanes step 3: legacy (own slots only) or flexible (LaneSlotPool) lanes (Worker:LaneModel). With flexible lanes the
    // bulkhead classes take their isolation caps by default (what each dedicated lane's single slot gave).
    private readonly LaneModelSettings laneModel = LaneModelSettings.From(configuration);
    private readonly BulkheadSettings bulkheads = BulkheadSettings.From(configuration, LaneModelSettings.From(configuration).Flexible);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (singleJobIdText is not null && singleJobId is null)
            throw new InvalidOperationException("Worker:SingleJobId must be a valid job identifier.");
        if (singleJobId.HasValue)
        {
            if (schedulerEnabled || singleJobType != MarketplaceJobTypes.ProductApprovalReconcile)
                throw new InvalidOperationException("Single-job mode requires the scheduler disabled and the product approval reconciliation job type.");
            await ExecuteSingleJobAsync(stoppingToken);
            return;
        }
        if (singleJobType is not null)
            throw new InvalidOperationException("Worker:SingleJobType can only be used together with Worker:SingleJobId.");

        if (schedulerEnabled) await RecoverAtStartupAsync(stoppingToken);
        else TouchHealthFile();
        await AlignProductModelsAtStartupAsync(stoppingToken);
        // Each lane runs Worker:LaneSlots:<lane> concurrent loops. Slots of one lane lease
        // independently (FOR UPDATE SKIP LOCKED); jobs that collide on the same connection
        // lock group are deferred without consuming an attempt.
        var loops = new List<Task>();
        var lanes = MarketplaceHub.Infrastructure.Jobs.WorkerLanes.All(hotPriorityCeiling);
        wakeRouter = new JobWakeRouter(lanes);
        var laneSlots = lanes.ToDictionary(lane => lane.Name, lane => LaneSlots(configuration, lane.Name, lane.DefaultSlots));
        var totalSlots = laneSlots.Values.Sum();
        // Worker lanes step 3: with the flexible model the lanes' slots form one pool (LaneSlotPool); each lane also runs
        // borrowing slots, which lease only while the pool lends them a free slot.
        var pool = laneModel.Flexible ? new LaneSlotPool(laneSlots, laneModel.ProtectedLanes, laneModel.BorrowLimits) : null;
        if (pool is not null) wakeRouter.LaneNotified += pool.NoteWork;
        // Borrowing slots wait on a signal of their own: a lane's notification goes to its own slots first (a borrowing slot
        // that may not borrow would use the wake up and leave the job to the idle poll). They are woken when a slot is freed
        // and when one of the lane's own slots found work (there may be more), and by the lane's notifications (JobWakeRouter).
        var borrowWakes = lanes.ToDictionary(lane => lane.Name, lane => wakeRouter.BorrowSignalFor(lane.Name));
        if (pool is not null)
            pool.SlotFreed += freedFor =>
            {
                foreach (var lane in freedFor)
                {
                    wakeRouter.SignalFor(lane).WakeOne();
                    borrowWakes[lane].WakeOne();
                }
            };
        foreach (var lane in lanes)
        {
            var slots = laneSlots[lane.Name];
            var laneWake = wakeRouter.SignalFor(lane.Name);
            var borrowWake = borrowWakes[lane.Name];
            var bulkhead = bulkheads.For(lane.Name, hotPriorityCeiling);
            loops.AddRange(Lane(lane.Name, slots, slot => RunLeaseLaneAsync(slot, lane.MaximumPriority, lane.MinimumPriority, laneWake, stoppingToken,
                includedJobTypes: [.. lane.IncludedJobTypes], excludedJobTypes: [.. lane.ExcludedJobTypes], bulkhead: bulkhead, pool: pool, laneName: lane.Name, ownSlot: true,
                leased: pool is null ? null : borrowWake.WakeOne)));
            if (pool is not null)
                loops.AddRange(Enumerable.Range(1, totalSlots - slots).Select(borrow => RunLeaseLaneAsync($"{lane.Name}+{borrow}", lane.MaximumPriority, lane.MinimumPriority, borrowWake, stoppingToken,
                    includedJobTypes: [.. lane.IncludedJobTypes], excludedJobTypes: [.. lane.ExcludedJobTypes], bulkhead: bulkhead, pool: pool, laneName: lane.Name, ownSlot: false)));
        }
        LogLaneModel(pool);
        await WarnWhenPoolIsTooSmallAsync(totalSlots, stoppingToken);
        StartQuotaAwareLeasing();
        LogFairnessAndBulkheads();
        loops.Add(RunHealthWatchdogAsync(stoppingToken));
        loops.Add(RunLeaseReaperAsync(stoppingToken));
        loops.Add(RunJobNotificationListenerAsync(stoppingToken));
        loops.Add(RunDueJobWakeAsync(stoppingToken));
        if (schedulerEnabled) loops.Add(RunSchedulerAsync(stoppingToken));
        if (schedulerEnabled) loops.Add(RunPolicyMaintenanceAsync(stoppingToken));
        if (schedulerEnabled && configuration.GetValue("Worker:JobRetention:Enabled", true)) loops.Add(RunJobRetentionAsync(stoppingToken));
        if (schedulerEnabled && configuration.GetValue("Worker:IssueRetention:Enabled", true)) loops.Add(RunIssueRetentionAsync(stoppingToken));
        if (schedulerEnabled) loops.Add(RunWebhookUpkeepAsync(stoppingToken));
        if (schedulerEnabled) loops.Add(RunLaneWaitAlertsAsync(stoppingToken));
        if (schedulerEnabled) loops.Add(RunExternalEffectSweepAsync(stoppingToken));
        if (schedulerEnabled && configuration.GetValue("Worker:ProductModelScan:Enabled", true)) loops.Add(RunProductModelScanAsync(stoppingToken));
        if (schedulerEnabled && configuration.GetValue("Worker:ReturnCardColumns:Enabled", true)) loops.Add(RunReturnCardColumnsRepairAsync(stoppingToken));
        if (schedulerEnabled) loops.Add(RunProductWarrantyRepairAsync(stoppingToken));
        await Task.WhenAll(loops);
    }

    // The startup log names the mode, so a rollback (Worker__QuotaAwareLeasing__Enabled=false) shows in the logs.
    private void StartQuotaAwareLeasing()
    {
        if (!quotaAwareLeasing.Enabled)
        {
            logger.LogInformation("Quota-aware leasing is off: jobs wait for their marketplace quota inside their slot");
            return;
        }
        var pressure = MarketplaceHub.Infrastructure.Jobs.QuotaPressure.Shared;
        pressure.Saturation = quotaAwareLeasing.Saturation;
        // A full bucket that has emptied its waiters: the jobs held for it are runnable again now, not at the next poll.
        pressure.Released += () => wakeRouter.WakeAll();
        pressure.PairsTrimmed += dropped => logger.LogWarning("Quota-aware leasing: {Dropped} least recently learned connection + job type pairs dropped (limit reached)", dropped);
        logger.LogInformation("Quota-aware leasing is on: a background job (priority above {HotPriorityCeiling}) whose marketplace quota has kept a request waiting for {SaturationSeconds} s is held in the queue for at most {MaximumHoldMinutes} min; user actions, new-order reads, webhooks and writes are never held",
            hotPriorityCeiling, quotaAwareLeasing.Saturation.TotalSeconds, quotaAwareLeasing.MaximumHold.TotalMinutes);
    }

    // The quota hold the lease gets now (null when quota-aware leasing is off): the pairs whose bucket is full, held only
    // above the hot priority ceiling.
    internal JobQuotaHold? QuotaHoldNow(DateTimeOffset now) => quotaAwareLeasing.Enabled
        ? MarketplaceHub.Infrastructure.Jobs.QuotaPressure.Shared.Held(now, quotaAwareLeasing.MaximumHold, hotPriorityCeiling)
        : null;

    private IEnumerable<Task> Lane(string lane, int slots, Func<string, Task> run)
    {
        logger.LogInformation("Worker lane {Lane} starting with {Slots} slot(s)", lane, slots);
        return Enumerable.Range(1, slots).Select(slot => run(slots == 1 ? lane : $"{lane}#{slot}")).ToArray();
    }

    internal static int LaneSlots(IConfiguration configuration, string lane, int defaultSlots) =>
        Math.Clamp(configuration.GetValue($"Worker:LaneSlots:{lane}", defaultSlots), 1, 8);

    // Every running job can hold two pooled connections at once (its context with the advisory lock and a
    // heartbeat or completion scope). A pool smaller than that makes jobs wait for a connection, and a
    // waiting heartbeat can lose its lease; the size is logged at startup with a warning when it is too small.
    private async Task WarnWhenPoolIsTooSmallAsync(int totalSlots, CancellationToken cancellationToken)
    {
        try
        {
            string? connectionString;
            await using (var scope = scopeFactory.CreateAsyncScope())
                connectionString = scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.GetConnectionString();
            if (string.IsNullOrWhiteSpace(connectionString)) return;
            var builder = new NpgsqlConnectionStringBuilder(connectionString);
            var required = MarketplaceHub.Infrastructure.Jobs.WorkerLanes.RequiredPoolSize(totalSlots);
            if (!builder.Pooling || builder.MaxPoolSize >= required)
                logger.LogInformation("Worker runs {Slots} lane slot(s) with a database pool of {PoolSize} (needs at least {Required})", totalSlots, builder.MaxPoolSize, required);
            else
                logger.LogWarning("Worker runs {Slots} lane slot(s) but the database pool allows only {PoolSize} connections; set Maximum Pool Size to at least {Required}", totalSlots, builder.MaxPoolSize, required);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(exception, "Worker could not check the database pool size");
        }
    }

    private async Task ExecuteSingleJobAsync(CancellationToken stoppingToken)
    {
        try
        {
            TouchHealthFile();
            var job = await LeaseNextAsync(null, null, stoppingToken);
            if (job is null)
            {
                logger.LogWarning("Single approval reconciliation job {JobId} was not available to lease", singleJobId);
                return;
            }

            logger.LogInformation("Single approval reconciliation job {JobId} leased", job.Id);
            await ExecuteLeasedJobAsync(job, stoppingToken);
        }
        finally
        {
            applicationLifetime.StopApplication();
        }
    }

    // KATALOG-14 phase D5b: before any job is taken, the cards the previous release linked during the deploy are linked to
    // their code's model and a colour whose copy differs from its model is reported, nothing of the model fields written
    // (ProductModelValues.AlignStaleAsync). A failure is logged and the nightly model scan repeats it.
    private async Task AlignProductModelsAtStartupAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var differing = await ProductModelValues.AlignStaleAsync(db, DateTimeOffset.UtcNow, cancellationToken);
            logger.LogInformation("Product models checked at startup: {Count} models whose colours differ from them (warned, nothing written): {Differing}", differing.Count, string.Join(", ", differing.Take(50)));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            logger.LogError(exception, "Product models were not aligned at startup; the nightly model scan repairs them");
        }
    }

    private async Task RecoverAtStartupAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var jobs = scope.ServiceProvider.GetRequiredService<IJobLeaseService>();
            var reaped = await jobs.ReapExpiredAsync(cancellationToken);
            var producer = scope.ServiceProvider.GetRequiredService<IScheduledJobProducer>();
            if (producer is ScheduledJobProducer maintenance) await maintenance.MaintainPoliciesAsync(includeDefaults: true, cancellationToken);
            var scheduled = await producer.EnqueueDueAsync(cancellationToken);
            TouchHealthFile();
            logger.LogInformation("Worker startup recovery completed; reaped {ReapedJobs} expired jobs and enqueued {ScheduledJobs} due jobs", reaped, scheduled);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            // The normal loops will retry the database recovery. The watchdog
            // keeps this process restartable if the database never becomes
            // reachable instead of leaving a half-alive worker behind.
            logger.LogError(exception, "Worker startup recovery failed; normal recovery loops will retry");
        }
    }

    private async Task RunSchedulerAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProduceScheduledJobsAsync(stoppingToken);
                TouchHealthFile();
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception exception) { logger.LogError(exception, "Worker scheduler loop failed"); }

            if (!stoppingToken.IsCancellationRequested)
                await Task.Delay(schedulerScanInterval, stoppingToken);
        }
    }

    // Worker lanes step 1: every minute each lane's queue wait is compared with its target; a miss that lasts opens the lane's
    // operational issue, ten minutes back within target close it (LaneWaitMonitor, LaneWaitAlarm).
    private Task RunLaneWaitAlertsAsync(CancellationToken stoppingToken)
    {
        var settings = LaneAlertSettings.From(configuration);
        if (!settings.Enabled)
        {
            logger.LogInformation("Lane wait alerts are off (Worker:LaneAlerts:Enabled): open lane alerts are resolved");
            return ResolveLaneWaitAlertsAsync(stoppingToken);
        }
        logger.LogInformation("Lane wait alerts are on: every {EvaluateSeconds} s, p95 of the last {WindowMinutes} min against each lane's target, opened after {SustainMinutes} min, closed after {ClearMinutes} min",
            settings.Evaluate.TotalSeconds, settings.Window.TotalMinutes, settings.Sustain.TotalMinutes, settings.Clear.TotalMinutes);
        // A lane not looked at for two passes starts its count again (LaneWaitAlarm.StaleAfter).
        var alarm = new MarketplaceHub.Infrastructure.Jobs.LaneWaitAlarm(settings.Sustain, settings.Clear, 2 * settings.Evaluate);
        return RunPeriodicAsync(settings.Evaluate, settings.Evaluate, async token =>
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var monitor = ActivatorUtilities.CreateInstance<LaneWaitMonitor>(scope.ServiceProvider);
            var changed = await monitor.EvaluateAsync(alarm, settings.Window, token);
            if (changed > 0) logger.LogInformation("Lane wait alerts: {Changed} lane alert(s) opened or closed", changed);
        }, exception => logger.LogError(exception, "Lane wait alert pass failed"), stoppingToken, "lane-wait-alerts");
    }

    // Lane alerts switched off: the alerts left open would never close, so they are resolved once at startup.
    private async Task ResolveLaneWaitAlertsAsync(CancellationToken stoppingToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var resolved = await ActivatorUtilities.CreateInstance<LaneWaitMonitor>(scope.ServiceProvider).ResolveAllAsync(stoppingToken);
            if (resolved > 0) logger.LogInformation("Lane wait alerts are off: {Resolved} open lane alert(s) resolved", resolved);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Resolving the open lane wait alerts failed");
        }
    }

    // The policy passes (default upgrades, switching off product and write automation) do not need to run
    // every scheduler tick: they run every five minutes, the default upgrades once an hour. The startup
    // recovery runs them all once before the first scheduling pass.
    private Task RunPolicyMaintenanceAsync(CancellationToken stoppingToken)
    {
        var lastDefaultsPass = DateTimeOffset.UtcNow;
        return RunPeriodicAsync(null, TimeSpan.FromMinutes(5), async token =>
        {
            var includeDefaults = DateTimeOffset.UtcNow - lastDefaultsPass >= TimeSpan.FromHours(1);
            await using var scope = scopeFactory.CreateAsyncScope();
            if (scope.ServiceProvider.GetRequiredService<IScheduledJobProducer>() is ScheduledJobProducer producer)
                await producer.MaintainPoliciesAsync(includeDefaults, token);
            if (includeDefaults) lastDefaultsPass = DateTimeOffset.UtcNow;
        }, exception => logger.LogError(exception, "Sync policy maintenance pass failed"), stoppingToken, "policy-maintenance");
    }

    // Runs a maintenance pass every period (first after firstPassAfter when given, else after one period) until
    // shutdown. A failed pass is logged and the loop goes on, also when it failed with a cancellation that did not
    // come from the shutdown (a library timeout surfacing as TaskCanceledException): such a pass used to end the
    // loop for good, silently, until the worker restarted.
    // KUYRUK-5: each pass is timed and each failed pass counted under the loop's name (maintenance.pass.duration /
    // maintenance.pass.failures), so a loop that keeps failing or has stopped running shows in the metrics.
    internal static async Task RunPeriodicAsync(TimeSpan? firstPassAfter, TimeSpan period, Func<CancellationToken, Task> pass, Action<Exception> onFailure, CancellationToken stoppingToken, string loop = "unnamed")
    {
        try
        {
            if (firstPassAfter is { } delay) await Task.Delay(delay, stoppingToken);
            using var timer = new PeriodicTimer(period);
            if (firstPassAfter is not null) await RunPassAsync(loop, pass, onFailure, stoppingToken);
            while (await timer.WaitForNextTickAsync(stoppingToken))
                await RunPassAsync(loop, pass, onFailure, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    private static async Task RunPassAsync(string loop, Func<CancellationToken, Task> pass, Action<Exception> onFailure, CancellationToken stoppingToken)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var outcome = "succeeded";
        try
        {
            await pass(stoppingToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
        {
            outcome = "failed";
            MarketplaceHub.Infrastructure.Telemetry.MarketplaceHubTelemetry.MaintenancePassFailures.Add(1, new System.Diagnostics.TagList { { "loop", loop } });
            onFailure(exception);
        }
        catch (OperationCanceledException)
        {
            outcome = "cancelled";
            throw;
        }
        finally
        {
            MarketplaceHub.Infrastructure.Telemetry.MarketplaceHubTelemetry.MaintenancePassDuration.Record(System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalSeconds,
                new System.Diagnostics.TagList { { "loop", loop }, { "outcome", outcome } });
        }
    }

    private async Task RunLeaseLaneAsync(string lane, int? maximumPriority, int? minimumPriority, LaneWakeSignal laneWake, CancellationToken stoppingToken, string? onlyJobType = null, bool reapExpiredLeases = false, string[]? includedJobTypes = null, string[]? excludedJobTypes = null, JobBulkhead? bulkhead = null,
        LaneSlotPool? pool = null, string? laneName = null, bool ownSlot = true, Action? leased = null)
    {
        await RunSlotAsync(laneWake, token => LeaseThroughPoolAsync(pool, laneName ?? lane, ownSlot, async () =>
        {
            var job = await LeaseNextAsync(maximumPriority, minimumPriority, token, onlyJobType, reapExpiredLeases, includedJobTypes, excludedJobTypes, bulkhead);
            // Health is refreshed only after a successful lease database cycle.
            // A live process that cannot reach the database must not remain healthy.
            TouchHealthFile();
            if (job is not null) leased?.Invoke();
            if (job is null) pool?.NoteNoWork(laneName ?? lane);
            else pool?.NoteWork(laneName ?? lane);
            return job;
        }, async job =>
        {
            RecordQueueWait(job, lane);
            await ExecuteLeasedJobAsync(job, stoppingToken);
        }), exception => logger.LogError(exception, "Worker {Lane} lease loop failed", lane), stoppingToken);
    }

    // A slot leases only with room in the pool (flexible lane model; no pool: always), and gives the room back when it found
    // nothing, failed, or its job ended.
    internal static async Task<Func<Task>?> LeaseThroughPoolAsync(LaneSlotPool? pool, string lane, bool ownSlot, Func<Task<LeasedJob?>> lease, Func<LeasedJob, Task> run)
    {
        if (pool is not null && !pool.TryClaim(lane, ownSlot)) return null;
        LeasedJob? job;
        try
        {
            job = await lease();
        }
        catch
        {
            pool?.Release(lane, ownSlot);
            throw;
        }
        if (job is null)
        {
            pool?.Release(lane, ownSlot);
            return null;
        }
        return async () =>
        {
            try
            {
                await run(job);
            }
            finally
            {
                pool?.Release(lane, ownSlot);
            }
        };
    }

    // The startup log names the lane model, so a switch (Worker__LaneModel) shows in the logs.
    private void LogLaneModel(LaneSlotPool? pool)
    {
        if (pool is null)
            logger.LogInformation("Lane model is legacy: each lane leases on its own slots only (Worker:LaneModel=flexible, the default, lets a busy lane borrow idle slots)");
        else
            logger.LogInformation("Lane model is flexible: {Total} slots in one pool, each lane keeps its own slots and borrows idle ones; lanes that never lend: {Protected}; borrow limits: {Limits}",
                pool.Total, string.Join(", ", pool.ProtectedLanes), pool.BorrowLimits.Count == 0 ? "none" : string.Join(", ", pool.BorrowLimits.Select(item => $"{item.Key}={item.Value}")));
    }

    // One lease slot of a lane: lease and run jobs until none is left, then wait for a wake of its lane (or the idle delay)
    // and poll again. lease returns how to run the job it leased, or null when the queue had nothing for the lane.
    // KUYRUK-6 (review A): PostgreSQL delivers identical notifications of one transaction once, so a scheduler pass that
    // queues the same job type for many connections wakes one slot per lane. A slot whose wait ended by a wake and that
    // then finds a job wakes the next idle slot, so the wake travels on until a slot finds nothing.
    internal static async Task RunSlotAsync(LaneWakeSignal laneWake, Func<CancellationToken, Task<Func<Task>?>> lease, Action<Exception> onFailure, CancellationToken stoppingToken,
        TimeSpan? firstDelay = null, Func<TimeSpan, bool, TimeSpan>? nextDelay = null)
    {
        var first = firstDelay ?? JobIdleWait.First;
        nextDelay ??= JobIdleWait.Next;
        var idleDelay = first;
        var wokenLast = false;
        while (!stoppingToken.IsCancellationRequested)
        {
            // Registered before the poll, so a wake that comes between the poll and the wait is not lost.
            var wake = laneWake.Register();
            var idle = false;
            try
            {
                var run = await lease(stoppingToken);
                if (run is not null)
                {
                    // A wake this slot got while it polled goes to the lane's next idle slot, and so does the wake that
                    // ended its last wait: more jobs of the same notification may be waiting.
                    laneWake.Leave(wake, forward: true);
                    if (wokenLast) laneWake.WakeOne();
                    wokenLast = false;
                    await run();
                    idleDelay = first;
                    continue;
                }
                idle = true;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception exception)
            {
                onFailure(exception);
                idleDelay = first;
            }
            if (!idle) laneWake.Leave(wake, forward: true);
            wokenLast = false;

            if (!stoppingToken.IsCancellationRequested)
            {
                // Polling stays as the fallback; a notification for a job this lane can lease, or the due-time wake when a
                // waiting job's time comes, ends the wait early.
                var woken = await JobWakeSignal.WaitAsync(wake.Woken, idleDelay, stoppingToken);
                if (idle) laneWake.Leave(wake, forward: false);
                idleDelay = nextDelay(idleDelay, woken);
                wokenLast = woken;
            }
            else if (idle) laneWake.Leave(wake, forward: false);
        }
    }

    // How long the job waited to be leased (a free slot, its connection's lock group or a quota backoff). A long
    // wait is logged so a slow webhook ingest can be traced to what held its group.
    private void RecordQueueWait(LeasedJob job, string lane)
    {
        var wait = job.QueueWait ?? TimeSpan.Zero;
        MarketplaceHub.Infrastructure.Telemetry.MarketplaceHubTelemetry.JobQueueWait.Record(wait.TotalSeconds,
            new System.Diagnostics.TagList { { "job.type", job.JobType }, { "lane", LaneName(lane) } });
        if (wait >= LongQueueWait)
            logger.LogInformation("Job {JobId} ({JobType}) leased by {Lane} lane after waiting {WaitSeconds} s in the queue", job.Id, job.JobType, lane, Math.Round(wait.TotalSeconds, 1));
        else
            logger.LogInformation("Job {JobId} leased by {Lane} lane", job.Id, lane);
    }

    private static readonly TimeSpan LongQueueWait = TimeSpan.FromSeconds(10);
    internal static string LaneName(string slot) => slot.Split('#', '+')[0];

    private async Task ProduceScheduledJobsAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var producer = scope.ServiceProvider.GetRequiredService<IScheduledJobProducer>();
        var count = await producer.EnqueueDueAsync(cancellationToken);
        if (count > 0)
        {
            logger.LogInformation("Enqueued {Count} scheduled integration jobs", count);
            dueRecheck.Pulse();
        }
    }

    // A job that becomes runnable only because time passes (a retry's or deferred job's AvailableAt, the end of
    // its connection's quota backoff) changes no row at that moment, so no notification announces it. This loop
    // reads the next such moment and wakes the lanes then. It reads again after every notification (debounced)
    // and scheduler pass that enqueued jobs, and at least every JobIdleWait.Maximum, so the lanes' idle wait can
    // be long without a due job waiting for it.
    private async Task RunDueJobWakeAsync(CancellationToken stoppingToken)
    {
        // Flexible lanes: the jobs that became due since this moment are told to their lanes' pool before the wake.
        var dueSince = DateTimeOffset.UtcNow;
        while (!stoppingToken.IsCancellationRequested)
        {
            var recheck = Task.WhenAny(wakeSignal.Current, dueRecheck.Current);
            DateTimeOffset? nextDue = null;
            var delay = JobIdleWait.Maximum;
            try
            {
                var now = DateTimeOffset.UtcNow;
                nextDue = await NextDueAsync(now, stoppingToken);
                delay = JobIdleWait.UntilDue(nextDue, now);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Next due job time could not be read; lanes keep polling");
            }

            try
            {
                if (await JobWakeSignal.WaitAsync(recheck, delay, stoppingToken))
                    await Task.Delay(DueRecheckDebounce, stoppingToken);
                // Pulsed whether the wait ended by time or by a notification: the moment read above has passed
                // either way, and the next read only looks at later ones.
                if (nextDue is { } due && due <= DateTimeOffset.UtcNow + JobIdleWait.MinimumDueWait)
                {
                    if (laneModel.Flexible) dueSince = await NoteDueWorkAsync(dueSince, stoppingToken);
                    wakeRouter.WakeAll();
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
        }
    }

    // Flexible lanes (23 Nov scan): a job that becomes due raises no notification, so its lane was never told it has work and
    // did not want its lent slot back: borrowers kept taking every freed slot and the job waited for the borrowed work to end.
    // The lanes that can lease a job due since the last look are told now. Returns the moment the next look starts from (the
    // same one when the read failed, so nothing due meanwhile is missed).
    private async Task<DateTimeOffset> NoteDueWorkAsync(DateTimeOffset since, CancellationToken stoppingToken)
    {
        var now = DateTimeOffset.UtcNow;
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var due = await BecameDueAsync(scope.ServiceProvider.GetRequiredService<AppDbContext>(), since, now, stoppingToken);
            if (due.Count > 0) wakeRouter.NoteDue(due);
            return now;
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Jobs that became due could not be read; their lanes are only woken");
            return since;
        }
    }

    // The job types and priorities of the queued jobs that became leasable in (since, now]: their AvailableAt passed, or a
    // quota backoff of their connection ended (any lock group: a lane told of work it cannot lease finds nothing on its next
    // poll and stops wanting).
    internal static async Task<IReadOnlyCollection<(string JobType, int Priority)>> BecameDueAsync(AppDbContext db, DateTimeOffset since, DateTimeOffset now, CancellationToken cancellationToken)
    {
        since = since.ToUniversalTime();
        now = now.ToUniversalTime();
        var rows = await db.Database.SqlQuery<DueJobKind>($"""
            SELECT DISTINCT job."JobType", job."Priority"
            FROM integration.jobs job
            WHERE job."Status" IN ('PENDING', 'RETRY_SCHEDULED')
              AND job."AttemptCount" < job."MaxAttempts"
              AND job."AvailableAt" <= {now}
              AND (job."AvailableAt" > {since}
                OR EXISTS (
                    SELECT 1 FROM integration.connection_backoffs backoff
                    WHERE backoff."ConnectionId" = job."ConnectionId" AND backoff."Until" > {since} AND backoff."Until" <= {now}))
            """).ToListAsync(cancellationToken);
        return [.. rows.Select(row => (row.JobType, row.Priority))];
    }

    internal sealed class DueJobKind
    {
        public string JobType { get; set; } = "";
        public int Priority { get; set; }
    }

    // The earliest future moment a queued job becomes leasable: its AvailableAt, or the end of a quota backoff
    // that may hold jobs of that connection back.
    private async Task<DateTimeOffset?> NextDueAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Database.SqlQuery<DateTimeOffset?>($"""
            SELECT LEAST(
                (SELECT min(job."AvailableAt") FROM integration.jobs job
                 WHERE job."Status" IN ('PENDING', 'RETRY_SCHEDULED') AND job."AvailableAt" > {now}
                   AND job."AttemptCount" < job."MaxAttempts"),
                (SELECT min(backoff."Until") FROM integration.connection_backoffs backoff WHERE backoff."Until" > {now})) AS "Value"
            """).SingleAsync(cancellationToken);
    }

    private async Task<LeasedJob?> LeaseNextAsync(int? maximumPriority, int? minimumPriority, CancellationToken cancellationToken, string? onlyJobType = null, bool reapExpiredLeases = false, string[]? includedJobTypes = null, string[]? excludedJobTypes = null, JobBulkhead? bulkhead = null)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var jobs = scope.ServiceProvider.GetRequiredService<IJobLeaseService>();
        var reaped = reapExpiredLeases && !singleJobId.HasValue ? await jobs.ReapExpiredAsync(cancellationToken) : 0;
        if (reaped > 0) logger.LogWarning("Reaped {Count} expired job leases", reaped);
        var now = DateTimeOffset.UtcNow;
        var hold = singleJobId.HasValue ? null : QuotaHoldNow(now);
        var fairness = singleJobId.HasValue ? null : FairnessNow();
        var job = await jobs.TryLeaseAsync(JobRetryPolicy.DefaultLeaseDuration, maximumPriority, minimumPriority, cancellationToken, onlyJobType ?? singleJobType, singleJobId, includedJobTypes, excludedJobTypes, hold,
            fairness, singleJobId.HasValue ? null : bulkhead);
        if (job?.ConnectionId is { } connectionId) MarketplaceHub.Infrastructure.Jobs.ConnectionLeaseClock.Shared.Record(connectionId, now);
        return job;
    }

    // The fairness order the lease gets now (null when connection fairness is off): when each connection was last served.
    internal JobFairness? FairnessNow() => fairnessSettings.Enabled
        ? MarketplaceHub.Infrastructure.Jobs.ConnectionLeaseClock.Shared.Fairness(fairnessSettings.Aging)
        : null;

    // The startup log names the modes, so a rollback (Worker__Fairness__Enabled=false, Worker__Bulkhead__Enabled=false) shows.
    private void LogFairnessAndBulkheads()
    {
        if (fairnessSettings.Enabled)
            logger.LogInformation("Connection fairness is on: within a priority the connection with fewer running jobs, then the one served longest ago, goes first; a job runnable for {AgingMinutes} min goes ahead; user actions keep their order",
                fairnessSettings.Aging.TotalMinutes);
        else
            logger.LogInformation("Connection fairness is off: jobs are leased by priority, tenant load and age");
        if (bulkheads.Enabled)
            logger.LogInformation("Marketplace bulkheads are on: classes {Classes}; lane caps {LaneCaps}",
                string.Join(", ", bulkheads.ClassCaps.Select(item => $"{item.Key}={item.Value}")),
                bulkheads.LaneCaps.Count == 0 ? "none" : string.Join(", ", bulkheads.LaneCaps.Select(item => $"{item.Key}={item.Value}")));
        else
            logger.LogInformation("Marketplace bulkheads are off");
    }

    internal async Task ExecuteLeasedJobAsync(LeasedJob job, CancellationToken stoppingToken)
    {
        // Every line logged while the job runs (processor, adapters, lease service) carries its context.
        using var logScope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["JobId"] = job.Id,
            ["JobType"] = job.JobType,
            ["TenantId"] = job.TenantId,
            ["ConnectionId"] = job.ConnectionId,
            ["CorrelationId"] = job.CorrelationId
        });
        using var activity = MarketplaceHub.Infrastructure.Telemetry.MarketplaceHubTelemetry.Source.StartActivity($"job {job.JobType}");
        activity?.SetTag("job.id", job.Id).SetTag("job.type", job.JobType).SetTag("connection.id", job.ConnectionId).SetTag("correlation.id", job.CorrelationId);
        var executionStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        // A job that writes to a marketplace or the invoice provider is not cancelled by a shutdown that begins
        // while it runs: cancelled mid-request, the write may still happen while the job can only record an
        // unknown outcome. It finishes (its heartbeat keeps the lease) and records its real outcome while the
        // lane loop, and so the host's shutdown timeout, waits for it. Its timeout and a lost lease still cancel
        // it; a job leased just as shutdown began is still cancelled before it starts and released.
        var finishesOnShutdown = FinishOnShutdown(job.JobType) && !stoppingToken.IsCancellationRequested;
        var shutdown = finishesOnShutdown ? CancellationToken.None : stoppingToken;
        using var shutdownNotice = finishesOnShutdown
            ? stoppingToken.Register(() => logger.LogInformation("Job {JobId} ({JobType}) writes externally; shutdown waits for it to finish", job.Id, job.JobType))
            : default;
        // KUYRUK-1: what the shutdown may still stop in such a job is a marketplace request that has not got its quota token
        // (never sent); that request ends as a local quota wait, and the job is handed back to the queue below.
        using var shutdownSignal = finishesOnShutdown ? MarketplaceHub.Infrastructure.Jobs.JobShutdownSignal.Enter(stoppingToken) : null;
        using var execution = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
        // How long the job type may run (and the setting that changes it) is kept in the job type registry.
        execution.CancelAfter(MarketplaceHub.Infrastructure.Jobs.JobTypeRegistry.TimeoutFor(job.JobType, configuration));
        using var heartbeatStop = StopLeaseHeartbeatOnExecutionTimeout(job.JobType)
            ? CancellationTokenSource.CreateLinkedTokenSource(shutdown, execution.Token)
            : CancellationTokenSource.CreateLinkedTokenSource(shutdown);
        var heartbeat = MaintainLeaseAsync(job, execution, heartbeatStop.Token);
        JobExecutionResult? result = null;

        // The quota buckets this run takes tokens from, learned for quota-aware leasing (QuotaPressure).
        using var quotaScope = MarketplaceHub.Infrastructure.Jobs.JobQuotaScope.Enter();
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            result = await DispatchAsync(scope.ServiceProvider, job, execution.Token);
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
        catch (OperationCanceledException exception)
        {
            logger.LogWarning(exception, "Job {JobId} execution timed out or was cancelled outside shutdown", job.Id);
            result = JobExecutionResult.Retry(
                "JOB_EXECUTION_CANCELLED",
                "İşlem geçici olarak iptal edildi ve yeniden denenecek.",
                RetryDelayAfterOrderReadTimeout(job.JobType));
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Job {JobId} execution failed", job.Id);
            result = ExceptionResult(exception);
        }
        LearnQuotaBuckets(job, quotaScope.Keys);

        try
        {
            // The heartbeat keeps running until the outcome is written: a completion that has to be retried
            // must not let the lease expire, or the reaper would run a finished job (and its marketplace
            // write) again. A heartbeat that already ended with false lost the lease.
            var leaseHeld = !heartbeat.IsCompleted || await heartbeat;
            var outcome = OutcomeTag(leaseHeld, result);
            var jobTags = new System.Diagnostics.TagList { { "job.type", job.JobType }, { "outcome", outcome } };
            MarketplaceHub.Infrastructure.Telemetry.MarketplaceHubTelemetry.JobsCompleted.Add(1, jobTags);
            MarketplaceHub.Infrastructure.Telemetry.MarketplaceHubTelemetry.JobDuration.Record(System.Diagnostics.Stopwatch.GetElapsedTime(executionStarted).TotalSeconds, jobTags);
            activity?.SetTag("job.outcome", outcome).SetTag("job.error_code", result?.ErrorCode);
            // Cancellation during shutdown can also surface as a transient Retry (e.g. a cancelled HTTP/DB call).
            // A job that finished despite the shutdown was not cancelled by it and records its real outcome, unless the
            // shutdown stopped one of its requests before the quota token and it ended to be retried (KUYRUK-1).
            var quotaWaitsStopped = shutdownSignal?.QuotaWaitsStopped ?? 0;
            if (quotaWaitsStopped > 0)
                logger.LogInformation("Job {JobId} ({JobType}): shutdown stopped {Count} marketplace request(s) still waiting for quota; they were not sent", job.Id, job.JobType, quotaWaitsStopped);
            if (ReleasesOnShutdown(leaseHeld, shutdown.IsCancellationRequested, quotaWaitsStopped > 0, result))
            {
                await ReleaseOnShutdownAsync(job);
                return;
            }
            if (!leaseHeld || result is null)
            {
                logger.LogWarning("Job {JobId} stopped without completion because its lease could not be confirmed", job.Id);
                return;
            }

            // A job that finished just as shutdown began still records its real outcome.
            var completed = await CompleteWithRetryAsync(
                async token =>
                {
                    await using var completionScope = scopeFactory.CreateAsyncScope();
                    return await completionScope.ServiceProvider.GetRequiredService<IJobLeaseService>().CompleteAsync(job.Id, job.LeaseToken, result, token);
                },
                () => heartbeat.IsCompleted && !heartbeat.Result,
                (exception, attempt) => logger.LogWarning(exception, "Job {JobId} completion attempt {Attempt} failed; retrying while the lease is held", job.Id, attempt),
                delay => Task.Delay(delay, CancellationToken.None));
            if (completed is null)
                logger.LogError("Job {JobId} completion could not be written; lease expiry will recover it", job.Id);
            else if (!completed.Value)
                logger.LogError("Job {JobId} completion was fenced because the lease owner or expiry no longer matched", job.Id);
            else
                logger.LogInformation("Job {JobId} ({JobType}) completed with {CompletionKind}; error {ErrorCode}", job.Id, job.JobType, result.Kind, result.ErrorCode ?? "none");
        }
        finally
        {
            heartbeatStop.Cancel();
            await heartbeat;
        }
    }

    // A job that writes to a marketplace or the invoice provider (shutdown-protected) is never held for its quota: its path
    // and timing stay exactly as they were. Every other job of a connection teaches the buckets it used.
    internal void LearnQuotaBuckets(LeasedJob job, IReadOnlyCollection<string> keys)
    {
        if (!quotaAwareLeasing.Enabled || job.ConnectionId is not { } connectionId || FinishOnShutdown(job.JobType)) return;
        MarketplaceHub.Infrastructure.Jobs.QuotaPressure.Shared.Learn(connectionId, job.JobType, keys);
    }

    // Whether a run that ended during shutdown goes back to the queue (ReleaseOnShutdownAsync: no attempt spent) instead of
    // recording its outcome. A job the shutdown cancelled goes back when it has no outcome or one to be retried. A write job
    // (not cancelled by the shutdown) goes back only when the shutdown stopped one of its requests before the quota token
    // (KUYRUK-1: nothing of that request was sent) and the job ended to be retried; any other outcome it reached is recorded.
    internal static bool ReleasesOnShutdown(bool leaseHeld, bool cancelledByShutdown, bool quotaWaitStoppedByShutdown, JobExecutionResult? result) =>
        leaseHeld
        && (cancelledByShutdown && (result is null || result.Kind == JobCompletionKind.Retry)
            || quotaWaitStoppedByShutdown && result is { Kind: JobCompletionKind.Retry });

    // Distinct metric outcomes: a deferral (lock busy, conflict) and a finished slice are not failed retries.
    internal static string OutcomeTag(bool leaseHeld, JobExecutionResult? result) =>
        !leaseHeld ? "lease-lost"
        : result is null ? "no-result"
        : result.Continues ? "continued"
        : result.Kind == JobCompletionKind.Retry && result.RefundAttempt ? "deferred"
        : result.Kind switch
        {
            JobCompletionKind.Succeeded => "succeeded",
            JobCompletionKind.Retry => "retry",
            JobCompletionKind.Blocked => "blocked",
            JobCompletionKind.ManualReview => "manual-review",
            _ => "dead"
        };

    // Waits between completion attempts: about one and a half minutes in all, inside the two-minute lease
    // the heartbeat keeps renewing.
    internal static readonly TimeSpan[] CompletionRetryDelays =
        [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15)];

    // Writes the outcome, retrying a failed write (a database blip, a timeout) with backoff while the lease is
    // still held. True: written; false: fenced (the lease belongs to someone else or expired); null: gave up.
    internal static async Task<bool?> CompleteWithRetryAsync(Func<CancellationToken, Task<bool>> complete, Func<bool> leaseLost, Action<Exception, int> onFailure, Func<TimeSpan, Task> wait)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                return await complete(timeout.Token);
            }
            catch (Exception exception)
            {
                onFailure(exception, attempt);
                if (attempt > CompletionRetryDelays.Length || leaseLost()) return null;
                await wait(CompletionRetryDelays[attempt - 1]);
                if (leaseLost()) return null;
            }
        }
    }

    // Shutdown cancels the running job; hand it straight back to the queue instead of waiting
    // for lease expiry, so a deploy neither delays it by minutes nor burns one of its attempts.
    private async Task ReleaseOnShutdownAsync(LeasedJob job)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await using var scope = scopeFactory.CreateAsyncScope();
            var jobs = scope.ServiceProvider.GetRequiredService<IJobLeaseService>();
            if (await jobs.ReleaseAsync(job.Id, job.LeaseToken, timeout.Token))
                logger.LogInformation("Job {JobId} ({JobType}) released back to the queue on shutdown", job.Id, job.JobType);
            else
                logger.LogWarning("Job {JobId} could not be released on shutdown; lease expiry will recover it", job.Id);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Job {JobId} release on shutdown failed; lease expiry will recover it", job.Id);
        }
    }

    internal static bool IsOrderReadSyncJob(string jobType) => MarketplaceHub.Infrastructure.Jobs.JobTypeRegistry.IsOrderReadSync(jobType);
    internal static bool IsProductCatalogSyncJob(string jobType) => MarketplaceHub.Infrastructure.Jobs.JobTypeRegistry.IsProductCatalogSync(jobType);
    internal static TimeSpan? RetryDelayAfterOrderReadTimeout(string jobType) =>
        MarketplaceHub.Infrastructure.Jobs.JobTypeRegistry.For(jobType).RetryAfterTimeout;
    internal static bool IsOrderInvoiceReconciliationJob(string jobType) => MarketplaceHub.Infrastructure.Jobs.JobTypeRegistry.IsOrderInvoiceReconciliation(jobType);
    internal static bool StopLeaseHeartbeatOnExecutionTimeout(string jobType) => MarketplaceHub.Infrastructure.Jobs.JobTypeRegistry.For(jobType).StopHeartbeatOnTimeout;
    internal static bool FinishOnShutdown(string jobType) => MarketplaceHub.Infrastructure.Jobs.JobTypeRegistry.IsShutdownProtected(jobType);

    private async Task<bool> MaintainLeaseAsync(LeasedJob job, CancellationTokenSource execution, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(JobRetryPolicy.HeartbeatInterval(JobRetryPolicy.DefaultLeaseDuration));
        var lastRenewal = DateTimeOffset.UtcNow;
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                bool renewed;
                try
                {
                    await using var scope = scopeFactory.CreateAsyncScope();
                    var jobs = scope.ServiceProvider.GetRequiredService<IJobLeaseService>();
                    renewed = await jobs.HeartbeatAsync(job.Id, job.LeaseToken, JobRetryPolicy.DefaultLeaseDuration, cancellationToken);
                }
                catch (Exception exception) when (!(exception is OperationCanceledException && cancellationToken.IsCancellationRequested)
                    && JobRetryPolicy.KeepRunningAfterHeartbeatFailure(DateTimeOffset.UtcNow - lastRenewal, JobRetryPolicy.DefaultLeaseDuration))
                {
                    logger.LogWarning(exception, "Job {JobId} heartbeat failed; the lease is still valid, retrying at the next tick", job.Id);
                    continue;
                }
                if (renewed)
                {
                    lastRenewal = DateTimeOffset.UtcNow;
                    TouchHealthFile();
                    continue;
                }

                logger.LogError("Job {JobId} lost its lease; cancelling local execution", job.Id);
                execution.Cancel();
                return false;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return true;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Job {JobId} heartbeat failed; cancelling local execution", job.Id);
            execution.Cancel();
            return false;
        }

        return true;
    }

    // The hot lane only reaps between its own jobs; a long hot job would otherwise leave
    // expired leases (e.g. from a crashed run) stuck until it finishes.
    private Task RunLeaseReaperAsync(CancellationToken stoppingToken) =>
        RunPeriodicAsync(null, TimeSpan.FromSeconds(30), async token =>
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var reaped = await scope.ServiceProvider.GetRequiredService<IJobLeaseService>().ReapExpiredAsync(token);
            if (reaped > 0) logger.LogWarning("Lease reaper recovered {Count} expired job leases", reaped);
        }, exception => logger.LogError(exception, "Lease reaper pass failed"), stoppingToken, "lease-reaper");

    // Hourly purge of old finished jobs and processed webhook inbox rows so the queue tables stop growing forever.
    private Task RunJobRetentionAsync(CancellationToken stoppingToken)
    {
        var succeededRetention = TimeSpan.FromDays(Math.Clamp(configuration.GetValue("Worker:JobRetention:SucceededDays", 14), 1, 365));
        var failedRetention = TimeSpan.FromDays(Math.Clamp(configuration.GetValue("Worker:JobRetention:FailedDays", 30), 1, 365));
        var inboxRetention = TimeSpan.FromDays(Math.Clamp(configuration.GetValue("Worker:JobRetention:InboxDays", 30), 1, 365));
        var inboxReceivedRetention = TimeSpan.FromDays(Math.Clamp(configuration.GetValue("Worker:JobRetention:InboxReceivedDays", 90), 30, 365));
        var guardRetention = TimeSpan.FromDays(Math.Clamp(configuration.GetValue("Worker:JobRetention:GuardDays", 90), 30, 3650));
        var effectRecordRetention = TimeSpan.FromDays(Math.Clamp(configuration.GetValue("Worker:JobRetention:EffectRecordDays", 90), 30, 3650));
        // History tables: each table's default period (HistoryRetentionPolicy.DefaultDays) unless the owner sets one; "0" keeps
        // a table forever (docs/SAKLAMA-SURELERI.md). Read as text: a value that is not a whole number of days keeps the
        // table and is logged, it never stops the worker.
        TimeSpan? History(HistoryRetentionPolicy.Table table)
        {
            var setting = HistoryRetentionPolicy.SettingName(table);
            var period = HistoryRetentionPolicy.Period(table, configuration[setting], out var invalid);
            if (invalid) logger.LogError("History retention: {Setting} is not a whole number of days ({Value}); {Table} is kept forever", setting, configuration[setting], table.Name);
            else if (period is not null) logger.LogInformation("History retention: {Table} keeps {Days} days", table.Name, period.Value.TotalDays);
            else logger.LogInformation("History retention: {Table} is kept forever", table.Name);
            return period;
        }
        var auditRetention = History(HistoryRetentionPolicy.AuditLogs);
        var aiUsageRetention = History(HistoryRetentionPolicy.AiUsageLogs);
        var priceHistoryRetention = History(HistoryRetentionPolicy.ChannelPriceHistory);
        var productChangeRetention = History(HistoryRetentionPolicy.ProductChangeEntries);
        var orderStatusRetention = History(HistoryRetentionPolicy.OrderStatusHistory);
        return RunPeriodicAsync(TimeSpan.FromMinutes(10), TimeSpan.FromHours(1), async token =>
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var retention = scope.ServiceProvider.GetRequiredService<JobRetentionService>();
            var deleted = await retention.PurgeAsync(succeededRetention, failedRetention, guardRetention, maximumBatches: 25, token);
            if (deleted > 0) logger.LogInformation("Job retention removed {Count} finished jobs", deleted);
            var inbox = await retention.PurgeInboxAsync(inboxRetention, inboxReceivedRetention, maximumBatches: 25, token);
            if (inbox > 0) logger.LogInformation("Job retention removed {Count} webhook inbox rows", inbox);
            var usage = await retention.PurgeRateLimitUsageAsync(maximumBatches: 25, token);
            if (usage > 0) logger.LogInformation("Job retention removed {Count} old quota usage rows", usage);
            var effects = await retention.PurgeCompletedEffectRecordsAsync(effectRecordRetention, maximumBatches: 25, token);
            if (effects > 0) logger.LogInformation("Job retention removed {Count} settled marketplace action records", effects);
            var sendLog = await retention.PurgePriceStockSendLogAsync(PriceStockSendLogPolicy.Retention, maximumBatches: 25, token);
            if (sendLog > 0) logger.LogInformation("Job retention removed {Count} old price/stock send log rows", sendLog);
            var notifications = await retention.PurgePanelNotificationsAsync(PanelNotificationPolicy.Retention, maximumBatches: 25, token);
            if (notifications > 0) logger.LogInformation("Job retention removed {Count} notification center rows older than a week", notifications);
            var references = await retention.PurgeStaleReferenceSnapshotsAsync(maximumBatches: 50, token);
            if (references > 0) logger.LogInformation("Job retention removed {Count} rows of old marketplace reference lists", references);
            async Task PurgeHistoryAsync(HistoryRetentionPolicy.Table table, TimeSpan? period, Func<TimeSpan, Task<int>> purge)
            {
                if (period is null) return;
                var removed = await purge(period.Value);
                if (removed > 0) logger.LogInformation("History retention removed {Count} rows of {Table} older than {Days} days", removed, table.Name, period.Value.TotalDays);
            }
            await PurgeHistoryAsync(HistoryRetentionPolicy.AuditLogs, auditRetention, period => retention.PurgeAuditLogsAsync(period, maximumBatches: 25, token));
            await PurgeHistoryAsync(HistoryRetentionPolicy.AiUsageLogs, aiUsageRetention, period => retention.PurgeAiUsageLogsAsync(period, maximumBatches: 25, token));
            await PurgeHistoryAsync(HistoryRetentionPolicy.ChannelPriceHistory, priceHistoryRetention, period => retention.PurgeChannelPriceHistoryAsync(period, maximumBatches: 25, token));
            await PurgeHistoryAsync(HistoryRetentionPolicy.ProductChangeEntries, productChangeRetention, period => retention.PurgeProductChangeEntriesAsync(period, maximumBatches: 25, token));
            await PurgeHistoryAsync(HistoryRetentionPolicy.OrderStatusHistory, orderStatusRetention, period => retention.PurgeOrderStatusHistoryAsync(period, maximumBatches: 25, token));
        }, exception => logger.LogError(exception, "Job retention pass failed"), stoppingToken, "job-retention");
    }

    // An open operational issue not seen for a month is closed daily, so the operations page shows what is
    // happening now; a recurrence reopens the same record. A resolved issue is deleted a month after it was
    // resolved and last seen (many issue keys carry event ids or timestamps, so they would otherwise pile up
    // forever); an issue resolved in this pass is not deleted in the same pass.
    private Task RunIssueRetentionAsync(CancellationToken stoppingToken)
    {
        var staleAfter = TimeSpan.FromDays(Math.Clamp(configuration.GetValue("Worker:IssueRetention:StaleDays", 30), 7, 365));
        var resolvedRetention = TimeSpan.FromDays(Math.Clamp(configuration.GetValue("Worker:IssueRetention:ResolvedDays", 30), 7, 365));
        return RunPeriodicAsync(TimeSpan.FromMinutes(15), TimeSpan.FromHours(24), async token =>
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var issues = scope.ServiceProvider.GetRequiredService<OperationalIssueRetentionService>();
            var resolved = await issues.ResolveStaleAsync(staleAfter, token);
            if (resolved > 0) logger.LogInformation("Issue retention resolved {Count} operational issue(s) not seen for {Days} days", resolved, staleAfter.TotalDays);
            // Trendyol quantity refusals of packages superseded by their split children, or no longer listed.
            var superseded = await issues.ResolveSupersededPackageQuantityIssuesAsync(TimeSpan.FromDays(3), token);
            if (superseded > 0) logger.LogInformation("Issue retention resolved {Count} package quantity refusal(s) of superseded or removed Trendyol packages", superseded);
            // Return lines reported as unlinked whose line has since been linked to its order line.
            var linked = await issues.ResolveLinkedReturnLineIssuesAsync(token);
            if (linked > 0) logger.LogInformation("Issue retention resolved {Count} return line notice(s) whose line is linked now", linked);
            var deleted = await issues.DeleteResolvedAsync(resolvedRetention, token);
            if (deleted > 0) logger.LogInformation("Issue retention deleted {Count} resolved operational issue(s) not seen for {Days} days", deleted, resolvedRetention.TotalDays);
        }, exception => logger.LogError(exception, "Issue retention pass failed"), stoppingToken, "issue-retention");
    }

    // KATALOG-6: half an hour after start and then daily, every card is linked to its model again (a link that failed after
    // a save no longer waits for the next save of that model) and every model takes its cards' shared values
    // (ProductModelScan). It writes no card and sends nothing to a marketplace.
    private Task RunProductModelScanAsync(CancellationToken stoppingToken) =>
        RunPeriodicAsync(TimeSpan.FromMinutes(30), TimeSpan.FromHours(24), async token =>
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<MarketplaceHub.Infrastructure.Persistence.AppDbContext>();
            var result = await MarketplaceHub.Infrastructure.Persistence.ProductModelScan.RunAsync(db, DateTimeOffset.UtcNow, token);
            logger.LogInformation("Product model scan: {Tenants} tenant(s), coded unlinked cards {UnlinkedBefore} -> {UnlinkedAfter}, {Written} model(s) written, {Differing} model(s) whose cards disagree, {Spread} model(s) whose colours hold different category attributes (not warned)",
                result.Tenants, result.UnlinkedCodedBefore, result.UnlinkedCodedAfter, result.ModelsWritten, result.ModelsDiffering, result.ModelsAttributesSpread);
        }, exception => logger.LogError(exception, "Product model scan failed"), stoppingToken, "product-model-scan");

    // SIPARIS-STOK-9: a few minutes after start (after a deploy, the claims the previous code wrote while it migrated) and
    // then every six hours, return claims whose Returns page card columns are missing or differ from the rule are written
    // again (ReturnCardColumns.RepairAsync), so a write that bypassed the save hook heals. Until then the page computes
    // that tenant's cards as before. It writes nothing else and sends nothing to a marketplace.
    private Task RunReturnCardColumnsRepairAsync(CancellationToken stoppingToken) =>
        RunPeriodicAsync(TimeSpan.FromMinutes(3), TimeSpan.FromHours(6), async token =>
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<MarketplaceHub.Infrastructure.Persistence.AppDbContext>();
            var (claims, written, lost) = await MarketplaceHub.Infrastructure.Persistence.ReturnCardColumns.RepairAsync(db, token);
            logger.LogInformation("Return card columns repair: {Claims} claim(s) read, {Written} written, {Lost} changed meanwhile and left to their own save", claims, written, lost);
            // A tenant still off the columns has its Returns page computed the slow way until the next pass.
            foreach (var tenantId in await MarketplaceHub.Infrastructure.Persistence.ReturnCardColumns.StaleTenantsAsync(db, token))
                logger.LogWarning("Return card columns are still missing or stale for tenant {TenantId} after the repair pass; its Returns page reads every claim until they are right", tenantId);
        }, exception => logger.LogError(exception, "Return card columns repair failed"), stoppingToken, "return-card-columns");

    // Garanti Süresi (15 Nov 2026): two minutes after start (after a deploy, once the previous API and Worker are gone),
    // a card on sale or closed whose warranty the previous code left empty while the deploy migrated takes 0, which is
    // what it meant (ProductWarranty.RunPassAsync). Only that first successful pass writes; the daily passes after it only
    // count such cards and warn, since one then means some writer empties the warranty of a card that is not a draft.
    private Task RunProductWarrantyRepairAsync(CancellationToken stoppingToken)
    {
        var firstPass = true;
        return RunPeriodicAsync(TimeSpan.FromMinutes(2), TimeSpan.FromHours(24), async token =>
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<MarketplaceHub.Infrastructure.Persistence.AppDbContext>();
            var result = await MarketplaceHub.Infrastructure.Persistence.ProductWarranty.RunPassAsync(db, firstPass, token);
            firstPass = false;
            if (result.CardsWritten > 0 || result.ModelsWritten > 0)
                logger.LogInformation("Product warranty repair: {Cards} card(s) and {Models} model record(s) with an empty warranty took 0", result.CardsWritten, result.ModelsWritten);
            if (result.EmptyCardsNotDraft > 0)
                logger.LogWarning("Product warranty check: {Count} card(s) on sale or closed have an empty Garanti Süresi; they are left as they are (a marketplace send of them stops with WARRANTY_REQUIRED)", result.EmptyCardsNotDraft);
        }, exception => logger.LogError(exception, "Product warranty repair failed"), stoppingToken, "product-warranty");
    }

    // Trendyol passivates a webhook whose deliveries keep failing (for example while the server is
    // off at night). Shortly after start and then hourly, registered webhooks found passive are
    // reactivated; the startup recovery's incremental reads cover what was missed meanwhile.
    private Task RunWebhookUpkeepAsync(CancellationToken stoppingToken) =>
        RunPeriodicAsync(TimeSpan.FromMinutes(1), TimeSpan.FromHours(1), async token =>
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var notifications = scope.ServiceProvider.GetRequiredService<IInstantNotificationService>();
            var reactivated = await notifications.ReactivatePassiveAsync(token);
            if (reactivated > 0) logger.LogInformation("Reactivated {Count} passive marketplace webhook(s)", reactivated);
            var retried = await notifications.RetryConnectionWebhooksAsync(token);
            if (retried > 0) logger.LogInformation("Closed or reopened {Count} webhook(s) of passive or reactivated connections", retried);
        }, exception => logger.LogError(exception, "Webhook upkeep pass failed"), stoppingToken, "webhook-upkeep");

    // Every two minutes, marketplace actions whose outcome is still open get a verification job.
    private Task RunExternalEffectSweepAsync(CancellationToken stoppingToken) =>
        RunPeriodicAsync(TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2), async token =>
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var changed = await scope.ServiceProvider.GetRequiredService<ExternalEffectSweep>().SweepAsync(token);
            if (changed > 0) logger.LogInformation("External effect sweep queued or closed {Count} open marketplace action(s)", changed);
        }, exception => logger.LogError(exception, "External effect sweep failed"), stoppingToken, "external-effect-sweep");

    // Holds one dedicated connection that LISTENs for runnable jobs and wakes the idle lanes.
    // Any failure only costs latency: the lanes keep polling while the listener reconnects.
    private async Task RunJobNotificationListenerAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                string connectionString;
                await using (var scope = scopeFactory.CreateAsyncScope())
                    connectionString = scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.GetConnectionString()
                        ?? throw new InvalidOperationException("The database connection string is not configured.");
                var builder = new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false, KeepAlive = 30 };
                await using var connection = new NpgsqlConnection(builder.ConnectionString);
                await connection.OpenAsync(stoppingToken);
                // KUYRUK-6: the payload names the job, so only the lanes that can lease it wake (one slot each).
                connection.Notification += (_, notification) =>
                {
                    wakeSignal.Pulse();
                    wakeRouter.Route(notification.Payload);
                };
                await using (var listen = new NpgsqlCommand($"LISTEN {IntegrationJobNotifications.Channel}", connection))
                    await listen.ExecuteNonQueryAsync(stoppingToken);
                logger.LogInformation("Listening for runnable job notifications on {Channel}", IntegrationJobNotifications.Channel);
                // Jobs queued while the listener was down are picked up right away.
                wakeSignal.Pulse();
                wakeRouter.WakeAll();
                while (!stoppingToken.IsCancellationRequested)
                    await connection.WaitAsync(TimeSpan.FromSeconds(30), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Job notification listener failed; lanes keep polling while it reconnects");
                try { await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken); }
                catch (OperationCanceledException) { }
            }
        }
    }

    private async Task RunHealthWatchdogAsync(CancellationToken cancellationToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Clamp(healthStaleAfter.TotalSeconds / 3, 10, 30));
        using var timer = new PeriodicTimer(interval);
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            var lastContact = DateTimeOffset.FromUnixTimeMilliseconds(Interlocked.Read(ref lastDatabaseContactUnixMilliseconds));
            var age = DateTimeOffset.UtcNow - lastContact;
            if (age <= healthStaleAfter) continue;

            logger.LogCritical("Worker database heartbeat is stale for {AgeSeconds} seconds; stopping so the container can restart", Math.Round(age.TotalSeconds));
            // Throwing here would not stop anything: ExecuteAsync awaits Task.WhenAll, which keeps
            // waiting for the other endless loops. Stop the host instead so the container restarts.
            Environment.ExitCode = 1;
            applicationLifetime.StopApplication();
            return;
        }
    }

    private static async Task<JobExecutionResult> DispatchAsync(IServiceProvider services, LeasedJob job, CancellationToken cancellationToken)
    {
        if (job.JobType is "IMPORT_PREVIEW" or "IMPORT_APPLY")
        {
            var payload = JsonSerializer.Deserialize<ImportJobPayload>(job.PayloadJson);
            if (payload is null) return JobExecutionResult.Blocked("INVALID_IMPORT_PAYLOAD", "Import job payload could not be parsed.");
            var processor = services.GetRequiredService<IImportJobProcessor>();
            var succeeded = await processor.ProcessAsync(job.TenantId, payload.SessionId, payload.Operation, cancellationToken);
            return succeeded ? JobExecutionResult.Success() : JobExecutionResult.Blocked("IMPORT_JOB_REJECTED", "Import operation was rejected by its current state or validation rules.");
        }

        // Paneli Tamamen Sıfırla: the tenant's own job, not tied to a connection.
        if (job.JobType == PanelJobTypes.FactoryReset)
            return await services.GetRequiredService<IPanelFactoryResetService>().RunAsync(job.TenantId, job.Id, job.PayloadJson, cancellationToken);

        if (MarketplaceJobTypes.IsMarketplaceProcessorJob(job.JobType))
        {
            var processor = services.GetRequiredService<IMarketplaceJobProcessor>();
            return await processor.ProcessAsync(job.TenantId, job.ConnectionId, job.JobType, job.PayloadJson, job.CorrelationId, cancellationToken, job.Id);
        }

        if (job.JobType is InvoicingJobTypes.ConnectionTest or InvoicingJobTypes.InvoiceSubmit or InvoicingJobTypes.InvoiceReconcile or InvoicingJobTypes.InvoiceDocumentFetch or InvoicingJobTypes.MarketplaceDelivery or InvoicingJobTypes.ManualOrderInvoiceDelivery or InvoicingJobTypes.InvoiceCancellation or InvoicingJobTypes.InvoiceDueScan or InvoicingJobTypes.StageCapabilityProbe)
        {
            var processor = services.GetRequiredService<IInvoicingJobProcessor>();
            return await processor.ProcessAsync(job.TenantId, job.ConnectionId, job.JobType, job.PayloadJson, job.CorrelationId, cancellationToken);
        }

        return JobExecutionResult.Dead("UNSUPPORTED_JOB_TYPE", $"Unsupported job type: {job.JobType}");
    }

    internal static JobExecutionResult ExceptionResult(Exception exception) => exception switch
    {
        // Another job of the connection changed the same rows meanwhile. Nothing is wrong with this job: it
        // runs again shortly on fresh data without losing an attempt. The lease service counts consecutive
        // conflicts and lets only the first few go without an attempt (JobConflictPolicy).
        DbUpdateConcurrencyException =>
            JobExecutionResult.Deferred(MarketplaceHub.Infrastructure.Jobs.JobConflictPolicy.ConcurrentUpdate, "Aynı kayıtlar başka bir işlem tarafından güncellendi; işlem kısa süre içinde yeniden çalışacak.", TimeSpan.FromSeconds(Random.Shared.Next(10, 31))),
        // An invoice number or ETTN the provider gave that another invoice already holds (VERITABANI-DAGITIM-11) is no
        // race: a retry meets the same row, so the job goes to review at once and nothing is sent again.
        _ when MarketplaceHub.Infrastructure.Persistence.InvoiceProviderIdentityConflict.Matches(exception) =>
            JobExecutionResult.ManualReview(MarketplaceHub.Infrastructure.Persistence.InvoiceProviderIdentityConflict.Code, MarketplaceHub.Infrastructure.Persistence.InvoiceProviderIdentityConflict.Message),
        // A duplicate insert usually lost a race with another job (a webhook and a poll inserting the same
        // row): the next run finds that row. It is retried a couple of times before it goes to review.
        _ when DataConstraintViolation(exception) is { SqlState: PostgresErrorCodes.UniqueViolation } duplicate =>
            JobExecutionResult.Retry(MarketplaceHub.Infrastructure.Jobs.JobConflictPolicy.DuplicateInsert, $"Aynı kayıt başka bir işlem tarafından eklendi; işlem kısa süre içinde yeniden denenecek. ({duplicate.ConstraintName})", TimeSpan.FromSeconds(Random.Shared.Next(5, 16))),
        // A violated check, foreign key or NOT NULL rule, a value too long or out of range is a data fault that six
        // retries cannot fix.
        _ when DataConstraintViolation(exception) is { } violation =>
            JobExecutionResult.ManualReview("DATABASE_CONSTRAINT_VIOLATION", $"Kayıt bir veritabanı kuralına takıldı ve incelenmeli. ({violation.SqlState} {violation.ConstraintName}: {violation.MessageText.ReplaceLineEndings(" ")})"),
        // Any other error the database reported that is not transient (a serialization failure, deadlock, cancelled
        // query, connection or resource fault is) is still retried, but not under the "transient" code.
        _ when NonTransientDatabaseError(exception) is { } error =>
            JobExecutionResult.Retry("UNHANDLED_EXECUTION_FAILURE", $"Veritabanı beklenmeyen bir hata bildirdi; işlem otomatik deneme sınırı içinde yeniden denenecek. ({error.SqlState}: {error.MessageText.ReplaceLineEndings(" ")})"),
        TimeoutException or HttpRequestException or IOException or System.Data.Common.DbException or DbUpdateException =>
            JobExecutionResult.Retry("TRANSIENT_EXECUTION_FAILURE", $"Geçici altyapı hatası nedeniyle işlem yeniden denenecek. ({Describe(exception)})"),
        // A broken or closed database connection, a disposed service during shutdown or EF's transient-failure
        // wrapper also arrive as InvalidOperationException; they pass, so they are retried.
        InvalidOperationException invalid when IsInfrastructureFault(invalid) =>
            JobExecutionResult.Retry("TRANSIENT_EXECUTION_FAILURE", $"Geçici altyapı hatası nedeniyle işlem yeniden denenecek. ({Describe(exception)})"),
        // Any other InvalidOperationException is surfaced for manual review instead of being silently blocked.
        InvalidOperationException =>
            JobExecutionResult.ManualReview("UNEXPECTED_JOB_STATE", $"İşlem beklenmeyen bir durumla karşılaştı ve incelenmeli. ({Describe(exception)})"),
        JsonException or ArgumentException =>
            JobExecutionResult.Blocked("INVALID_JOB_STATE", $"İşlem verisi veya mevcut durum doğrulaması başarısız oldu. ({Describe(exception)})"),
        _ => JobExecutionResult.Retry("UNHANDLED_EXECUTION_FAILURE", $"Beklenmeyen işlem hatası otomatik deneme sınırı içinde yeniden denenecek. ({Describe(exception)})")
    };

    private static string Describe(Exception exception) => $"{exception.GetType().Name}: {exception.Message.ReplaceLineEndings(" ")}";

    private static readonly string[] ConnectionFaultMessages =
        ["connection is not open", "connection was closed", "connection is broken", "connection has been closed", "transient failure", "while reading from stream", "exception while connecting"];

    // An InvalidOperationException caused by the database connection or by shutdown, not by the job's data.
    internal static bool IsInfrastructureFault(InvalidOperationException exception)
    {
        if (exception is ObjectDisposedException || string.Equals(exception.Source, "Npgsql", StringComparison.Ordinal)) return true;
        for (Exception? inner = exception.InnerException; inner is not null; inner = inner.InnerException)
            if (inner is NpgsqlException or IOException or System.Net.Sockets.SocketException or TimeoutException or ObjectDisposedException) return true;
        return ConnectionFaultMessages.Any(text => exception.Message.Contains(text, StringComparison.OrdinalIgnoreCase));
    }

    private static PostgresException? DataConstraintViolation(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
            if (current is PostgresException
                {
                    SqlState: PostgresErrorCodes.UniqueViolation or PostgresErrorCodes.CheckViolation or PostgresErrorCodes.StringDataRightTruncation
                        or PostgresErrorCodes.ForeignKeyViolation or PostgresErrorCodes.NotNullViolation or PostgresErrorCodes.NumericValueOutOfRange
                        or PostgresErrorCodes.InvalidTextRepresentation or PostgresErrorCodes.DatetimeFieldOverflow
                } postgres)
                return postgres;
        return null;
    }

    // A database error that a retry may well not get past. Npgsql marks the transient ones (serialization failure,
    // deadlock, connection and resource faults); a query cancelled by a statement timeout counts as transient too.
    private static PostgresException? NonTransientDatabaseError(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
            if (current is PostgresException postgres)
                return postgres.IsTransient || postgres.SqlState == PostgresErrorCodes.QueryCanceled ? null : postgres;
        return null;
    }

    private void TouchHealthFile()
    {
        // The database was reached: the in-process watchdog learns it first, so a health file that cannot be
        // written (a full or read-only disk) does not make the watchdog restart a worker that is working.
        Interlocked.Exchange(ref lastDatabaseContactUnixMilliseconds, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        try
        {
            var directory = Path.GetDirectoryName(healthFile);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(healthFile, DateTimeOffset.UtcNow.ToString("O"));
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Worker health heartbeat file could not be updated");
        }
    }

    private sealed record ImportJobPayload(Guid SessionId, string Operation);
}
