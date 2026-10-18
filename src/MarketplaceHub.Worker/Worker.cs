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
    private static readonly string[] HepsiburadaOrderJobTypes =
        [MarketplaceJobTypes.HepsiburadaOrderSync, MarketplaceJobTypes.HepsiburadaOrderRecoverySync];
    private static readonly string[] OrderReadSyncJobTypes =
    [
        MarketplaceJobTypes.OrderSync, MarketplaceJobTypes.OrderRecoverySync, MarketplaceJobTypes.OrderStatusSync,
        MarketplaceJobTypes.OrderReconciliation, MarketplaceJobTypes.OrderInvoiceReconciliation, MarketplaceJobTypes.TrendyolOrderCargoInfoReconciliation,
        MarketplaceJobTypes.ShopifyOrderSync, MarketplaceJobTypes.ShopifyOrderRecoverySync, MarketplaceJobTypes.ShopifyOrderStatusSync,
        MarketplaceJobTypes.ShopifyOrderReconciliation, MarketplaceJobTypes.ShopifyOrderInvoiceReconciliation,
        MarketplaceJobTypes.HepsiburadaOrderSync, MarketplaceJobTypes.HepsiburadaOrderRecoverySync,
        MarketplaceJobTypes.HepsiburadaOrderStatusSync, MarketplaceJobTypes.HepsiburadaOrderInvoiceReconciliation,
        MarketplaceJobTypes.N11OrderSync, MarketplaceJobTypes.N11OrderRecoverySync
    ];
    // Pazarama has no change filter on orders, so its status scan re-reads open orders one by one;
    // the three order flows run in their own lane so they never occupy the hot lane's slots.
    private static readonly string[] PazaramaOrderJobTypes =
        [MarketplaceJobTypes.PazaramaOrderSync, MarketplaceJobTypes.PazaramaOrderRecoverySync, MarketplaceJobTypes.PazaramaOrderStatusSync];
    private static readonly string[] ReturnReadJobTypes =
        [MarketplaceJobTypes.ReturnSync, MarketplaceJobTypes.HepsiburadaReturnSync, MarketplaceJobTypes.ReturnStatusSync];
    private static readonly string[] ProductCatalogSyncJobTypes =
        [MarketplaceJobTypes.ProductSync, MarketplaceJobTypes.ShopifyProductSync, MarketplaceJobTypes.HepsiburadaProductSync, MarketplaceJobTypes.CatalogStructureImport];
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
        // Return reads and product-catalog syncs have dedicated lanes and are kept out of the
        // shared hot/background lanes so long reads cannot occupy their slots.
        var loops = new List<Task>();
        // Jobs a user waits for (JobPriorityPolicy.UserAction) never queue behind syncs.
        loops.AddRange(Lane("user", 2, slot => RunLeaseLaneAsync(slot, JobPriorityPolicy.UserAction, null, stoppingToken)));
        loops.AddRange(Lane("hot", 3, slot => RunLeaseLaneAsync(slot, hotPriorityCeiling, 0, stoppingToken, excludedJobTypes: [.. HepsiburadaOrderJobTypes, .. PazaramaOrderJobTypes, .. ReturnReadJobTypes])));
        loops.AddRange(Lane("returns-read", 1, slot => RunLeaseLaneAsync(slot, null, null, stoppingToken, includedJobTypes: ReturnReadJobTypes)));
        loops.AddRange(Lane("product-catalog", 1, slot => RunLeaseLaneAsync(slot, null, null, stoppingToken, includedJobTypes: ProductCatalogSyncJobTypes)));
        loops.AddRange(Lane("hepsiburada-status", 1, slot => RunLeaseLaneAsync(slot, hotPriorityCeiling, 0, stoppingToken, MarketplaceJobTypes.HepsiburadaOrderStatusSync)));
        loops.AddRange(Lane("hepsiburada-orders", 1, slot => RunLeaseLaneAsync(slot, hotPriorityCeiling, 0, stoppingToken, includedJobTypes: HepsiburadaOrderJobTypes)));
        loops.AddRange(Lane("pazarama-orders", 1, slot => RunLeaseLaneAsync(slot, hotPriorityCeiling, 0, stoppingToken, includedJobTypes: PazaramaOrderJobTypes)));
        loops.AddRange(Lane("initial-data-sync", 1, slot => RunLeaseLaneAsync(slot, -1, -1, stoppingToken)));
        loops.AddRange(Lane("background", 2, slot => RunLeaseLaneAsync(slot, null, hotPriorityCeiling + 1, stoppingToken, excludedJobTypes: [.. ProductCatalogSyncJobTypes, .. ReturnReadJobTypes])));
        loops.Add(RunHealthWatchdogAsync(stoppingToken));
        loops.Add(RunLeaseReaperAsync(stoppingToken));
        loops.Add(RunJobNotificationListenerAsync(stoppingToken));
        if (schedulerEnabled) loops.Add(RunSchedulerAsync(stoppingToken));
        if (schedulerEnabled && configuration.GetValue("Worker:JobRetention:Enabled", true)) loops.Add(RunJobRetentionAsync(stoppingToken));
        if (schedulerEnabled) loops.Add(RunWebhookUpkeepAsync(stoppingToken));
        await Task.WhenAll(loops);
    }

    private IEnumerable<Task> Lane(string lane, int defaultSlots, Func<string, Task> run)
    {
        var slots = LaneSlots(configuration, lane, defaultSlots);
        logger.LogInformation("Worker lane {Lane} starting with {Slots} slot(s)", lane, slots);
        return Enumerable.Range(1, slots).Select(slot => run(slots == 1 ? lane : $"{lane}#{slot}")).ToArray();
    }

    internal static int LaneSlots(IConfiguration configuration, string lane, int defaultSlots) =>
        Math.Clamp(configuration.GetValue($"Worker:LaneSlots:{lane}", defaultSlots), 1, 8);

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
                    logger.LogInformation("Job {JobId} leased by {Lane} lane", job.Id, lane);
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
        using var execution = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        if (job.JobType is MarketplaceJobTypes.ProductSync or MarketplaceJobTypes.ShopifyProductSync or MarketplaceJobTypes.HepsiburadaProductSync or MarketplaceJobTypes.CatalogStructureImport)
        {
            var configuredMinutes = configuration.GetValue<double?>("Worker:ProductSyncTimeoutMinutes") ?? 60;
            execution.CancelAfter(TimeSpan.FromMinutes(Math.Clamp(configuredMinutes, 1, 180)));
        }
        else if (IsOrderInvoiceReconciliationJob(job.JobType))
        {
            var configuredMinutes = configuration.GetValue<double?>("Worker:OrderInvoiceReconciliationTimeoutMinutes") ?? 15;
            execution.CancelAfter(TimeSpan.FromMinutes(Math.Clamp(configuredMinutes, 1, 30)));
        }
        else if (job.JobType == MarketplaceJobTypes.TrendyolOrderCargoInfoReconciliation)
        {
            var configuredMinutes = configuration.GetValue<double?>("Worker:TrendyolCargoInfoTimeoutMinutes") ?? 30;
            execution.CancelAfter(TimeSpan.FromMinutes(Math.Clamp(configuredMinutes, 1, 60)));
        }
        else if (job.JobType == MarketplaceJobTypes.OrderStatusSync)
        {
            var configuredMinutes = configuration.GetValue<double?>("Worker:TrendyolOrderStatusTimeoutMinutes") ?? 20;
            execution.CancelAfter(TimeSpan.FromMinutes(Math.Clamp(configuredMinutes, 1, 60)));
        }
        else if (IsOrderReadSyncJob(job.JobType))
        {
            var configuredMinutes = configuration.GetValue<double?>("Worker:OrderReadSyncTimeoutMinutes")
                ?? configuration.GetValue<double?>("Worker:OrderStatusSyncTimeoutMinutes")
                ?? 10;
            execution.CancelAfter(TimeSpan.FromMinutes(Math.Clamp(configuredMinutes, 1, 60)));
        }
        else
        {
            // Every other job type gets a generous ceiling so a hung call cannot hold a lane forever.
            var configuredMinutes = configuration.GetValue<double?>("Worker:DefaultJobTimeoutMinutes") ?? 60;
            execution.CancelAfter(TimeSpan.FromMinutes(Math.Clamp(configuredMinutes, 5, 180)));
        }
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
        finally
        {
            heartbeatStop.Cancel();
        }

        var leaseHeld = await heartbeat;
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
        using var completionTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var completionScope = scopeFactory.CreateAsyncScope();
        var jobs = completionScope.ServiceProvider.GetRequiredService<IJobLeaseService>();
        var completed = await jobs.CompleteAsync(job.Id, job.LeaseToken, result, completionTimeout.Token);
        if (!completed)
            logger.LogError("Job {JobId} completion was fenced because the lease owner or expiry no longer matched", job.Id);
        else
            logger.LogInformation("Job {JobId} ({JobType}) completed with {CompletionKind}; error {ErrorCode}", job.Id, job.JobType, result.Kind, result.ErrorCode ?? "none");
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

    internal static bool IsOrderReadSyncJob(string jobType) => Array.IndexOf(OrderReadSyncJobTypes, jobType) >= 0;
    internal static bool IsProductCatalogSyncJob(string jobType) => Array.IndexOf(ProductCatalogSyncJobTypes, jobType) >= 0;
    internal static TimeSpan? RetryDelayAfterOrderReadTimeout(string jobType) =>
        IsOrderReadSyncJob(jobType) ? TimeSpan.FromMinutes(3) : null;
    internal static bool IsOrderInvoiceReconciliationJob(string jobType) => jobType is
        MarketplaceJobTypes.OrderInvoiceReconciliation
        or MarketplaceJobTypes.ShopifyOrderInvoiceReconciliation
        or MarketplaceJobTypes.HepsiburadaOrderInvoiceReconciliation;
    internal static bool StopLeaseHeartbeatOnExecutionTimeout(string jobType) => IsOrderInvoiceReconciliationJob(jobType);

    private async Task<bool> MaintainLeaseAsync(LeasedJob job, CancellationTokenSource execution, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(JobRetryPolicy.HeartbeatInterval(JobRetryPolicy.DefaultLeaseDuration));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var jobs = scope.ServiceProvider.GetRequiredService<IJobLeaseService>();
                if (await jobs.HeartbeatAsync(job.Id, job.LeaseToken, JobRetryPolicy.DefaultLeaseDuration, cancellationToken))
                {
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

    // Hourly purge of old finished scheduled jobs so the queue tables stop growing forever.
    private async Task RunJobRetentionAsync(CancellationToken stoppingToken)
    {
        var succeededRetention = TimeSpan.FromDays(Math.Clamp(configuration.GetValue("Worker:JobRetention:SucceededDays", 14), 1, 365));
        var failedRetention = TimeSpan.FromDays(Math.Clamp(configuration.GetValue("Worker:JobRetention:FailedDays", 30), 1, 365));
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(10), stoppingToken);
            using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
            do
            {
                try
                {
                    await using var scope = scopeFactory.CreateAsyncScope();
                    var deleted = await scope.ServiceProvider.GetRequiredService<JobRetentionService>()
                        .PurgeAsync(succeededRetention, failedRetention, maximumBatches: 25, stoppingToken);
                    if (deleted > 0) logger.LogInformation("Job retention removed {Count} finished scheduled jobs", deleted);
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

    private static JobExecutionResult ExceptionResult(Exception exception) => exception switch
    {
        TimeoutException or HttpRequestException or IOException or System.Data.Common.DbException =>
            JobExecutionResult.Retry("TRANSIENT_EXECUTION_FAILURE", "Geçici altyapı hatası nedeniyle işlem yeniden denenecek."),
        // InvalidOperationException is also thrown by runtime/EF faults, not only by bad job data,
        // so it is surfaced for manual review instead of being silently blocked.
        InvalidOperationException =>
            JobExecutionResult.ManualReview("UNEXPECTED_JOB_STATE", $"İşlem beklenmeyen bir durumla karşılaştı ve incelenmeli. ({exception.GetType().Name}: {exception.Message.ReplaceLineEndings(" ")})"),
        JsonException or ArgumentException =>
            JobExecutionResult.Blocked("INVALID_JOB_STATE", $"İşlem verisi veya mevcut durum doğrulaması başarısız oldu. ({exception.GetType().Name}: {exception.Message.ReplaceLineEndings(" ")})"),
        _ => JobExecutionResult.Retry("UNHANDLED_EXECUTION_FAILURE", "Beklenmeyen işlem hatası otomatik deneme sınırı içinde yeniden denenecek.")
    };

    private void TouchHealthFile()
    {
        try
        {
            var directory = Path.GetDirectoryName(healthFile);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(healthFile, DateTimeOffset.UtcNow.ToString("O"));
            Interlocked.Exchange(ref lastDatabaseContactUnixMilliseconds, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Worker health heartbeat file could not be updated");
        }
    }

    private sealed record ImportJobPayload(Guid SessionId, string Operation);
}
