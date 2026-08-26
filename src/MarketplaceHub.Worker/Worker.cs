using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;

namespace MarketplaceHub.Worker;

public sealed class Worker(IServiceScopeFactory scopeFactory, ILogger<Worker> logger, IConfiguration configuration) : BackgroundService
{
    private readonly string healthFile = configuration["Worker:HealthFile"] ?? "/tmp/marketplacehub-worker-heartbeat";
    private readonly TimeSpan schedulerScanInterval = TimeSpan.FromSeconds(Math.Clamp(configuration.GetValue("Worker:SchedulerScanSeconds", 5), 1, 30));
    private readonly int hotPriorityCeiling = Math.Clamp(configuration.GetValue("Worker:HotPriorityCeiling", 2), 0, 5);
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.WhenAll(
            RunSchedulerAsync(stoppingToken),
            RunLeaseLaneAsync("hot", hotPriorityCeiling, stoppingToken),
            RunLeaseLaneAsync("background", null, stoppingToken));
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

    private async Task RunLeaseLaneAsync(string lane, int? maximumPriority, CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var job = await LeaseNextAsync(maximumPriority, stoppingToken);
                // Health is refreshed only after a successful lease database cycle.
                // A live process that cannot reach the database must not remain healthy.
                TouchHealthFile();
                if (job is not null)
                {
                    logger.LogInformation("Job {JobId} leased by {Lane} lane", job.Id, lane);
                    await ExecuteLeasedJobAsync(job, stoppingToken);
                    continue;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception exception) { logger.LogError(exception, "Worker {Lane} lease loop failed", lane); }

            if (!stoppingToken.IsCancellationRequested)
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
        }
    }

    private async Task ProduceScheduledJobsAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var producer = scope.ServiceProvider.GetRequiredService<IScheduledJobProducer>();
        var count = await producer.EnqueueDueAsync(cancellationToken);
        if (count > 0) logger.LogInformation("Enqueued {Count} scheduled integration jobs", count);
    }

    private async Task<LeasedJob?> LeaseNextAsync(int? maximumPriority, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var jobs = scope.ServiceProvider.GetRequiredService<IJobLeaseService>();
        var reaped = await jobs.ReapExpiredAsync(cancellationToken);
        if (reaped > 0) logger.LogWarning("Reaped {Count} expired job leases", reaped);
        return await jobs.TryLeaseAsync(JobRetryPolicy.DefaultLeaseDuration, maximumPriority, cancellationToken);
    }

    private async Task ExecuteLeasedJobAsync(LeasedJob job, CancellationToken stoppingToken)
    {
        using var execution = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        if (job.JobType == MarketplaceJobTypes.ProductSync)
        {
            var configuredMinutes = configuration.GetValue<double?>("Worker:ProductSyncTimeoutMinutes") ?? 15;
            execution.CancelAfter(TimeSpan.FromMinutes(Math.Clamp(configuredMinutes, 1, 60)));
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

        if (job.JobType is MarketplaceJobTypes.ConnectionTest or MarketplaceJobTypes.ReferenceSync or MarketplaceJobTypes.ProductSync or MarketplaceJobTypes.ProductCreate or MarketplaceJobTypes.ProductApprovalReconcile or MarketplaceJobTypes.ProductUpdate or MarketplaceJobTypes.ProductArchive or MarketplaceJobTypes.PriceInventorySync or MarketplaceJobTypes.StockProjectionDispatch or MarketplaceJobTypes.OrderSync or MarketplaceJobTypes.OrderRecoverySync or MarketplaceJobTypes.OrderStatusSync or MarketplaceJobTypes.OrderReconciliation or MarketplaceJobTypes.ShipmentAction or MarketplaceJobTypes.CommonLabel or MarketplaceJobTypes.CapabilityProbe or MarketplaceJobTypes.StageTestOrder or MarketplaceJobTypes.ReturnSync or MarketplaceJobTypes.ReturnStatusSync or MarketplaceJobTypes.ReturnReconciliation or MarketplaceJobTypes.ReturnAction or MarketplaceJobTypes.StockReconciliation or MarketplaceJobTypes.WebhookIngest)
        {
            var processor = services.GetRequiredService<IMarketplaceJobProcessor>();
            return await processor.ProcessAsync(job.TenantId, job.ConnectionId, job.JobType, job.PayloadJson, job.CorrelationId, cancellationToken);
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
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Worker health heartbeat file could not be updated");
        }
    }

    private sealed record ImportJobPayload(Guid SessionId, string Operation);
}
