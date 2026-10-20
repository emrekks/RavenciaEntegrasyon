using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace MarketplaceHub.Worker;

public sealed class Worker(IServiceScopeFactory scopeFactory, ILogger<Worker> logger, IConfiguration configuration, IHostApplicationLifetime applicationLifetime) : BackgroundService
{
    // The lanes (and the job types each takes) are defined in WorkerLanes, shared with the queue metrics.
    internal static readonly string[] OrderStatusLaneJobTypes = [.. MarketplaceHub.Infrastructure.Jobs.WorkerLanes.OrderStatusJobTypes];
    internal static readonly string[] HotLaneExcludedJobTypes = [.. MarketplaceHub.Infrastructure.Jobs.WorkerLanes.HotLaneExcludedJobTypes];
    private readonly string healthFile = configuration["Worker:HealthFile"] ?? "/tmp/marketplacehub-worker-heartbeat";
    private readonly TimeSpan schedulerScanInterval = TimeSpan.FromSeconds(Math.Clamp(configuration.GetValue("Worker:SchedulerScanSeconds", 5), 1, 30));
    private readonly int hotPriorityCeiling = Math.Clamp(configuration.GetValue("Worker:HotPriorityCeiling", 2), 0, 5);
    private readonly TimeSpan healthStaleAfter = TimeSpan.FromSeconds(Math.Clamp(configuration.GetValue("Worker:HealthStaleAfterSeconds", 120), 30, 900));
    private readonly bool schedulerEnabled = configuration.GetValue("Worker:EnableScheduler", true);
    private readonly string? singleJobIdText = string.IsNullOrWhiteSpace(configuration["Worker:SingleJobId"]) ? null : configuration["Worker:SingleJobId"]!.Trim();
    private readonly Guid? singleJobId = Guid.TryParse(configuration["Worker:SingleJobId"], out var parsedJobId) ? parsedJobId : null;
    private readonly string? singleJobType = string.IsNullOrWhiteSpace(configuration["Worker:SingleJobType"]) ? null : configuration["Worker:SingleJobType"]!.Trim();
    private long lastDatabaseContactUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    private readonly JobWakeSignal wakeSignal = new();

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
        // Each lane runs Worker:LaneSlots:<lane> concurrent loops. Slots of one lane lease
        // independently (FOR UPDATE SKIP LOCKED); jobs that collide on the same connection
        // lock group are deferred without consuming an attempt.
        var loops = new List<Task>();
        var totalSlots = 0;
        foreach (var lane in MarketplaceHub.Infrastructure.Jobs.WorkerLanes.All(hotPriorityCeiling))
        {
            var slots = LaneSlots(configuration, lane.Name, lane.DefaultSlots);
            totalSlots += slots;
            loops.AddRange(Lane(lane.Name, slots, slot => RunLeaseLaneAsync(slot, lane.MaximumPriority, lane.MinimumPriority, stoppingToken,
                includedJobTypes: [.. lane.IncludedJobTypes], excludedJobTypes: [.. lane.ExcludedJobTypes])));
        }
        await WarnWhenPoolIsTooSmallAsync(totalSlots, stoppingToken);
        loops.Add(RunHealthWatchdogAsync(stoppingToken));
        loops.Add(RunLeaseReaperAsync(stoppingToken));
        loops.Add(RunJobNotificationListenerAsync(stoppingToken));
        if (schedulerEnabled) loops.Add(RunSchedulerAsync(stoppingToken));
        if (schedulerEnabled) loops.Add(RunPolicyMaintenanceAsync(stoppingToken));
        if (schedulerEnabled && configuration.GetValue("Worker:JobRetention:Enabled", true)) loops.Add(RunJobRetentionAsync(stoppingToken));
        if (schedulerEnabled && configuration.GetValue("Worker:IssueRetention:Enabled", true)) loops.Add(RunIssueRetentionAsync(stoppingToken));
        if (schedulerEnabled) loops.Add(RunWebhookUpkeepAsync(stoppingToken));
        if (schedulerEnabled) loops.Add(RunExternalEffectSweepAsync(stoppingToken));
        await Task.WhenAll(loops);
    }

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

    // The policy passes (default upgrades, switching off product and write automation) do not need to run
    // every scheduler tick: they run every five minutes, the default upgrades once an hour. The startup
    // recovery runs them all once before the first scheduling pass.
    private async Task RunPolicyMaintenanceAsync(CancellationToken stoppingToken)
    {
        var lastDefaultsPass = DateTimeOffset.UtcNow;
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    var includeDefaults = DateTimeOffset.UtcNow - lastDefaultsPass >= TimeSpan.FromHours(1);
                    await using var scope = scopeFactory.CreateAsyncScope();
                    if (scope.ServiceProvider.GetRequiredService<IScheduledJobProducer>() is ScheduledJobProducer producer)
                        await producer.MaintainPoliciesAsync(includeDefaults, stoppingToken);
                    if (includeDefaults) lastDefaultsPass = DateTimeOffset.UtcNow;
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    logger.LogError(exception, "Sync policy maintenance pass failed");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    private async Task RunLeaseLaneAsync(string lane, int? maximumPriority, int? minimumPriority, CancellationToken stoppingToken, string? onlyJobType = null, bool reapExpiredLeases = false, string[]? includedJobTypes = null, string[]? excludedJobTypes = null)
    {
        var idleDelay = TimeSpan.FromSeconds(1);
        while (!stoppingToken.IsCancellationRequested)
        {
            var wake = wakeSignal.Current;
            try
            {
                var job = await LeaseNextAsync(maximumPriority, minimumPriority, stoppingToken, onlyJobType, reapExpiredLeases, includedJobTypes, excludedJobTypes);
                // Health is refreshed only after a successful lease database cycle.
                // A live process that cannot reach the database must not remain healthy.
                TouchHealthFile();
                if (job is not null)
                {
                    RecordQueueWait(job, lane);
                    await ExecuteLeasedJobAsync(job, stoppingToken);
                    idleDelay = TimeSpan.FromSeconds(1);
                    continue;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception exception)
            {
                logger.LogError(exception, "Worker {Lane} lease loop failed", lane);
                idleDelay = TimeSpan.FromSeconds(1);
            }

            if (!stoppingToken.IsCancellationRequested)
            {
                // Polling stays as the fallback; a notification for a runnable job ends the wait early.
                var woken = await JobWakeSignal.WaitAsync(wake, idleDelay, stoppingToken);
                idleDelay = woken ? TimeSpan.FromSeconds(1) : TimeSpan.FromMilliseconds(Math.Min(3_000, idleDelay.TotalMilliseconds * 2));
            }
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
    private static string LaneName(string slot) => slot.Split('#')[0];

    private async Task ProduceScheduledJobsAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var producer = scope.ServiceProvider.GetRequiredService<IScheduledJobProducer>();
        var count = await producer.EnqueueDueAsync(cancellationToken);
        if (count > 0) logger.LogInformation("Enqueued {Count} scheduled integration jobs", count);
    }

    private async Task<LeasedJob?> LeaseNextAsync(int? maximumPriority, int? minimumPriority, CancellationToken cancellationToken, string? onlyJobType = null, bool reapExpiredLeases = false, string[]? includedJobTypes = null, string[]? excludedJobTypes = null)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var jobs = scope.ServiceProvider.GetRequiredService<IJobLeaseService>();
        var reaped = reapExpiredLeases && !singleJobId.HasValue ? await jobs.ReapExpiredAsync(cancellationToken) : 0;
        if (reaped > 0) logger.LogWarning("Reaped {Count} expired job leases", reaped);
        return await jobs.TryLeaseAsync(JobRetryPolicy.DefaultLeaseDuration, maximumPriority, minimumPriority, cancellationToken, onlyJobType ?? singleJobType, singleJobId, includedJobTypes, excludedJobTypes);
    }

    private async Task ExecuteLeasedJobAsync(LeasedJob job, CancellationToken stoppingToken)
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
        using var execution = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        // How long the job type may run (and the setting that changes it) is kept in the job type registry.
        execution.CancelAfter(MarketplaceHub.Infrastructure.Jobs.JobTypeRegistry.TimeoutFor(job.JobType, configuration));
        using var heartbeatStop = StopLeaseHeartbeatOnExecutionTimeout(job.JobType)
            ? CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, execution.Token)
            : CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var heartbeat = MaintainLeaseAsync(job, execution, heartbeatStop.Token);
        JobExecutionResult? result = null;

        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            result = await DispatchAsync(scope.ServiceProvider, job, execution.Token);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
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
            if (leaseHeld && stoppingToken.IsCancellationRequested && (result is null || result.Kind == JobCompletionKind.Retry))
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
    private async Task RunLeaseReaperAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                try
                {
                    await using var scope = scopeFactory.CreateAsyncScope();
                    var reaped = await scope.ServiceProvider.GetRequiredService<IJobLeaseService>().ReapExpiredAsync(cancellationToken);
                    if (reaped > 0) logger.LogWarning("Lease reaper recovered {Count} expired job leases", reaped);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    logger.LogError(exception, "Lease reaper pass failed");
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    // Hourly purge of old finished jobs and processed webhook inbox rows so the queue tables stop growing forever.
    private async Task RunJobRetentionAsync(CancellationToken stoppingToken)
    {
        var succeededRetention = TimeSpan.FromDays(Math.Clamp(configuration.GetValue("Worker:JobRetention:SucceededDays", 14), 1, 365));
        var failedRetention = TimeSpan.FromDays(Math.Clamp(configuration.GetValue("Worker:JobRetention:FailedDays", 30), 1, 365));
        var inboxRetention = TimeSpan.FromDays(Math.Clamp(configuration.GetValue("Worker:JobRetention:InboxDays", 30), 1, 365));
        var inboxReceivedRetention = TimeSpan.FromDays(Math.Clamp(configuration.GetValue("Worker:JobRetention:InboxReceivedDays", 90), 30, 365));
        var guardRetention = TimeSpan.FromDays(Math.Clamp(configuration.GetValue("Worker:JobRetention:GuardDays", 90), 30, 3650));
        var effectRecordRetention = TimeSpan.FromDays(Math.Clamp(configuration.GetValue("Worker:JobRetention:EffectRecordDays", 90), 30, 3650));
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(10), stoppingToken);
            using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
            do
            {
                try
                {
                    await using var scope = scopeFactory.CreateAsyncScope();
                    var retention = scope.ServiceProvider.GetRequiredService<JobRetentionService>();
                    var deleted = await retention.PurgeAsync(succeededRetention, failedRetention, guardRetention, maximumBatches: 25, stoppingToken);
                    if (deleted > 0) logger.LogInformation("Job retention removed {Count} finished jobs", deleted);
                    var inbox = await retention.PurgeInboxAsync(inboxRetention, inboxReceivedRetention, maximumBatches: 25, stoppingToken);
                    if (inbox > 0) logger.LogInformation("Job retention removed {Count} webhook inbox rows", inbox);
                    var usage = await retention.PurgeRateLimitUsageAsync(maximumBatches: 25, stoppingToken);
                    if (usage > 0) logger.LogInformation("Job retention removed {Count} old quota usage rows", usage);
                    var effects = await retention.PurgeCompletedEffectRecordsAsync(effectRecordRetention, maximumBatches: 25, stoppingToken);
                    if (effects > 0) logger.LogInformation("Job retention removed {Count} settled marketplace action records", effects);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    logger.LogError(exception, "Job retention pass failed");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    // An open operational issue not seen for a month is closed daily, so the operations page shows what is
    // happening now; a recurrence reopens the same record. A resolved issue is deleted a month after it was
    // resolved and last seen (many issue keys carry event ids or timestamps, so they would otherwise pile up
    // forever); an issue resolved in this pass is not deleted in the same pass.
    private async Task RunIssueRetentionAsync(CancellationToken stoppingToken)
    {
        var staleAfter = TimeSpan.FromDays(Math.Clamp(configuration.GetValue("Worker:IssueRetention:StaleDays", 30), 7, 365));
        var resolvedRetention = TimeSpan.FromDays(Math.Clamp(configuration.GetValue("Worker:IssueRetention:ResolvedDays", 30), 7, 365));
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(15), stoppingToken);
            using var timer = new PeriodicTimer(TimeSpan.FromHours(24));
            do
            {
                try
                {
                    await using var scope = scopeFactory.CreateAsyncScope();
                    var issues = scope.ServiceProvider.GetRequiredService<OperationalIssueRetentionService>();
                    var resolved = await issues.ResolveStaleAsync(staleAfter, stoppingToken);
                    if (resolved > 0) logger.LogInformation("Issue retention resolved {Count} operational issue(s) not seen for {Days} days", resolved, staleAfter.TotalDays);
                    var deleted = await issues.DeleteResolvedAsync(resolvedRetention, stoppingToken);
                    if (deleted > 0) logger.LogInformation("Issue retention deleted {Count} resolved operational issue(s) not seen for {Days} days", deleted, resolvedRetention.TotalDays);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    logger.LogError(exception, "Issue retention pass failed");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    // Trendyol passivates a webhook whose deliveries keep failing (for example while the server is
    // off at night). Shortly after start and then hourly, registered webhooks found passive are
    // reactivated; the startup recovery's incremental reads cover what was missed meanwhile.
    private async Task RunWebhookUpkeepAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
            using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
            do
            {
                try
                {
                    await using var scope = scopeFactory.CreateAsyncScope();
                    var reactivated = await scope.ServiceProvider.GetRequiredService<IInstantNotificationService>().ReactivatePassiveAsync(stoppingToken);
                    if (reactivated > 0) logger.LogInformation("Reactivated {Count} passive marketplace webhook(s)", reactivated);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    logger.LogError(exception, "Webhook upkeep pass failed");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    // Every two minutes, marketplace actions whose outcome is still open get a verification job.
    private async Task RunExternalEffectSweepAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(2));
            do
            {
                try
                {
                    await using var scope = scopeFactory.CreateAsyncScope();
                    var changed = await scope.ServiceProvider.GetRequiredService<ExternalEffectSweep>().SweepAsync(stoppingToken);
                    if (changed > 0) logger.LogInformation("External effect sweep queued or closed {Count} open marketplace action(s)", changed);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    logger.LogError(exception, "External effect sweep failed");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

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
                connection.Notification += (_, _) => wakeSignal.Pulse();
                await using (var listen = new NpgsqlCommand($"LISTEN {IntegrationJobNotifications.Channel}", connection))
                    await listen.ExecuteNonQueryAsync(stoppingToken);
                logger.LogInformation("Listening for runnable job notifications on {Channel}", IntegrationJobNotifications.Channel);
                // Jobs queued while the listener was down are picked up right away.
                wakeSignal.Pulse();
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
        // A duplicate insert usually lost a race with another job (a webhook and a poll inserting the same
        // row): the next run finds that row. It is retried a couple of times before it goes to review.
        _ when DataConstraintViolation(exception) is { SqlState: PostgresErrorCodes.UniqueViolation } duplicate =>
            JobExecutionResult.Retry(MarketplaceHub.Infrastructure.Jobs.JobConflictPolicy.DuplicateInsert, $"Aynı kayıt başka bir işlem tarafından eklendi; işlem kısa süre içinde yeniden denenecek. ({duplicate.ConstraintName})", TimeSpan.FromSeconds(Random.Shared.Next(5, 16))),
        // A violated check or a value too long is a data fault that six retries cannot fix.
        _ when DataConstraintViolation(exception) is { } violation =>
            JobExecutionResult.ManualReview("DATABASE_CONSTRAINT_VIOLATION", $"Kayıt bir veritabanı kuralına takıldı ve incelenmeli. ({violation.SqlState} {violation.ConstraintName}: {violation.MessageText.ReplaceLineEndings(" ")})"),
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
            if (current is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation or PostgresErrorCodes.CheckViolation or PostgresErrorCodes.StringDataRightTruncation } postgres)
                return postgres;
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
