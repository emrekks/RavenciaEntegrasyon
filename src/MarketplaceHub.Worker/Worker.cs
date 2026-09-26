using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Microsoft.Extensions.Hosting;

namespace MarketplaceHub.Worker;

public sealed class Worker(IServiceScopeFactory scopeFactory, ILogger<Worker> logger, IConfiguration configuration, IHostApplicationLifetime applicationLifetime) : BackgroundService
{
    private readonly string healthFile = configuration["Worker:HealthFile"] ?? "/tmp/marketplacehub-worker-heartbeat";
    private readonly TimeSpan schedulerScanInterval = TimeSpan.FromSeconds(Math.Clamp(configuration.GetValue("Worker:SchedulerScanSeconds", 5), 1, 30));
    private readonly int hotPriorityCeiling = Math.Clamp(configuration.GetValue("Worker:HotPriorityCeiling", 2), 0, 5);
    private readonly TimeSpan healthStaleAfter = TimeSpan.FromSeconds(Math.Clamp(configuration.GetValue("Worker:HealthStaleAfterSeconds", 120), 30, 900));
    private readonly bool schedulerEnabled = configuration.GetValue("Worker:EnableScheduler", true);
    private readonly string? singleJobIdText = string.IsNullOrWhiteSpace(configuration["Worker:SingleJobId"]) ? null : configuration["Worker:SingleJobId"]!.Trim();
    private readonly Guid? singleJobId = Guid.TryParse(configuration["Worker:SingleJobId"], out var parsedJobId) ? parsedJobId : null;
    private readonly string? singleJobType = string.IsNullOrWhiteSpace(configuration["Worker:SingleJobType"]) ? null : configuration["Worker:SingleJobType"]!.Trim();
    private long lastDatabaseContactUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

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
        var loops = new List<Task>
        {
            RunLeaseLaneAsync("hot", hotPriorityCeiling, null, stoppingToken),
            RunLeaseLaneAsync("background", null, hotPriorityCeiling + 1, stoppingToken),
            RunHealthWatchdogAsync(stoppingToken)
        };
        if (schedulerEnabled) loops.Add(RunSchedulerAsync(stoppingToken));
        await Task.WhenAll(loops);
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

    private async Task RunLeaseLaneAsync(string lane, int? maximumPriority, int? minimumPriority, CancellationToken stoppingToken)
    {
        var idleDelay = TimeSpan.FromSeconds(1);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var job = await LeaseNextAsync(maximumPriority, minimumPriority, stoppingToken);
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
                await Task.Delay(idleDelay, stoppingToken);
                idleDelay = TimeSpan.FromMilliseconds(Math.Min(3_000, idleDelay.TotalMilliseconds * 2));
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

    private async Task<LeasedJob?> LeaseNextAsync(int? maximumPriority, int? minimumPriority, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var jobs = scope.ServiceProvider.GetRequiredService<IJobLeaseService>();
        var reaped = minimumPriority is null && !singleJobId.HasValue ? await jobs.ReapExpiredAsync(cancellationToken) : 0;
        if (reaped > 0) logger.LogWarning("Reaped {Count} expired job leases", reaped);
        return await jobs.TryLeaseAsync(JobRetryPolicy.DefaultLeaseDuration, maximumPriority, minimumPriority, cancellationToken, singleJobType, singleJobId);
    }

    private async Task ExecuteLeasedJobAsync(LeasedJob job, CancellationToken stoppingToken)
    {
        using var execution = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        if (job.JobType is MarketplaceJobTypes.ProductSync or MarketplaceJobTypes.ShopifyProductSync)
        {
            var configuredMinutes = configuration.GetValue<double?>("Worker:ProductSyncTimeoutMinutes") ?? 60;
            execution.CancelAfter(TimeSpan.FromMinutes(Math.Clamp(configuredMinutes, 1, 180)));
        }
        using var heartbeatStop = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
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
            result = JobExecutionResult.Retry("JOB_EXECUTION_CANCELLED", "İşlem geçici olarak iptal edildi ve yeniden denenecek.");
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
        if (!leaseHeld || result is null || stoppingToken.IsCancellationRequested)
        {
            logger.LogWarning("Job {JobId} stopped without completion because its lease could not be confirmed", job.Id);
            return;
        }

        await using var completionScope = scopeFactory.CreateAsyncScope();
        var jobs = completionScope.ServiceProvider.GetRequiredService<IJobLeaseService>();
        var completed = await jobs.CompleteAsync(job.Id, job.LeaseToken, result, stoppingToken);
        if (!completed)
            logger.LogError("Job {JobId} completion was fenced because the lease owner or expiry no longer matched", job.Id);
        else
            logger.LogInformation("Job {JobId} ({JobType}) completed with {CompletionKind}; error {ErrorCode}", job.Id, job.JobType, result.Kind, result.ErrorCode ?? "none");
    }

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
            throw new InvalidOperationException("Worker database heartbeat became stale.");
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

        if (job.JobType is MarketplaceJobTypes.ConnectionTest or MarketplaceJobTypes.ShopifyConnectionTest or MarketplaceJobTypes.ReferenceSync or MarketplaceJobTypes.ProductSync or MarketplaceJobTypes.ShopifyProductSync or MarketplaceJobTypes.ProductCreate or MarketplaceJobTypes.ProductApprovalReconcile or MarketplaceJobTypes.ProductUpdate or MarketplaceJobTypes.ProductArchive or MarketplaceJobTypes.PriceInventorySync or MarketplaceJobTypes.StockProjectionDispatch or MarketplaceJobTypes.OrderSync or MarketplaceJobTypes.ShopifyOrderSync or MarketplaceJobTypes.OrderRecoverySync or MarketplaceJobTypes.ShopifyOrderRecoverySync or MarketplaceJobTypes.OrderStatusSync or MarketplaceJobTypes.ShopifyOrderStatusSync or MarketplaceJobTypes.OrderReconciliation or MarketplaceJobTypes.ShopifyOrderReconciliation or MarketplaceJobTypes.OrderInvoiceReconciliation or MarketplaceJobTypes.ShopifyOrderInvoiceReconciliation or MarketplaceJobTypes.ShipmentAction or MarketplaceJobTypes.CommonLabel or MarketplaceJobTypes.CapabilityProbe or MarketplaceJobTypes.StageTestOrder or MarketplaceJobTypes.ReturnSync or MarketplaceJobTypes.ReturnStatusSync or MarketplaceJobTypes.ReturnReconciliation or MarketplaceJobTypes.ReturnAction or MarketplaceJobTypes.StockReconciliation or MarketplaceJobTypes.WebhookIngest or MarketplaceJobTypes.ShopifyWebhookIngest)
        {
            var processor = services.GetRequiredService<IMarketplaceJobProcessor>();
            return await processor.ProcessAsync(job.TenantId, job.ConnectionId, job.JobType, job.PayloadJson, job.CorrelationId, cancellationToken, job.Id);
        }

        if (job.JobType is InvoicingJobTypes.ConnectionTest or InvoicingJobTypes.InvoiceSubmit or InvoicingJobTypes.InvoiceReconcile or InvoicingJobTypes.InvoiceDocumentFetch or InvoicingJobTypes.MarketplaceDelivery or InvoicingJobTypes.InvoiceCancellation or InvoicingJobTypes.InvoiceDueScan or InvoicingJobTypes.StageCapabilityProbe)
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
        JsonException or ArgumentException or InvalidOperationException =>
            JobExecutionResult.Blocked("INVALID_JOB_STATE", "İşlem verisi veya mevcut durum doğrulaması başarısız oldu."),
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
