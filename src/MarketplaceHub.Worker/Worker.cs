using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Infrastructure.Adapters.Hepsiburada;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Adapters.Shopify;

namespace MarketplaceHub.Worker;

public sealed class Worker(IServiceScopeFactory scopeFactory, ILogger<Worker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var job = await LeaseNextAsync(stoppingToken);
                if (job is not null) await ExecuteLeasedJobAsync(job, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception exception) { logger.LogError(exception, "Worker loop failed"); }

            if (!stoppingToken.IsCancellationRequested)
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
    }

    private async Task<LeasedJob?> LeaseNextAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var jobs = scope.ServiceProvider.GetRequiredService<IJobLeaseService>();
        var reaped = await jobs.ReapExpiredAsync(cancellationToken);
        if (reaped > 0) logger.LogWarning("Reaped {Count} expired job leases", reaped);
        return await jobs.TryLeaseAsync(JobRetryPolicy.DefaultLeaseDuration, cancellationToken);
    }

    private async Task ExecuteLeasedJobAsync(LeasedJob job, CancellationToken stoppingToken)
    {
        using var execution = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        using var heartbeatStop = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var heartbeat = MaintainLeaseAsync(job, execution, heartbeatStop.Token);
        JobResult? result = null;

        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            result = await DispatchAsync(scope.ServiceProvider, job, execution.Token);
        }
        catch (OperationCanceledException) when (execution.IsCancellationRequested) { }
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
        var completed = await jobs.CompleteAsync(job.Id, job.LeaseToken, result.Succeeded, result.ErrorCode, stoppingToken);
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
                    continue;

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

    private static async Task<JobResult> DispatchAsync(IServiceProvider services, LeasedJob job, CancellationToken cancellationToken)
    {
        if (job.JobType is "IMPORT_PREVIEW" or "IMPORT_APPLY")
        {
            var payload = JsonSerializer.Deserialize<ImportJobPayload>(job.PayloadJson);
            var processor = services.GetRequiredService<IImportJobProcessor>();
            var succeeded = payload is not null && await processor.ProcessAsync(job.TenantId, payload.SessionId, payload.Operation, cancellationToken);
            return new JobResult(succeeded, succeeded ? null : "IMPORT_JOB_REJECTED");
        }

        if (job.JobType is F3JobTypes.ConnectionTest or F3JobTypes.OrderSync or F3JobTypes.ShipmentAction or F3JobTypes.ReturnSync or F3JobTypes.ReturnAction or F3JobTypes.WebhookIngest or ShopifyContract.ConnectionTestJob or ShopifyContract.OrderSyncJob or ShopifyContract.WebhookIngestJob or HepsiburadaContract.ConnectionTestJob)
        {
            var processor = services.GetRequiredService<IF3JobProcessor>();
            var succeeded = await processor.ProcessAsync(job.TenantId, job.ConnectionId, job.JobType, job.PayloadJson, job.CorrelationId, cancellationToken);
            return new JobResult(succeeded, succeeded ? null : "F3_JOB_REJECTED");
        }

        if (job.JobType is F4JobTypes.ConnectionTest or F4JobTypes.InvoiceSubmit or F4JobTypes.InvoiceReconcile or F4JobTypes.InvoiceDocumentFetch or F4JobTypes.MarketplaceDelivery or F4JobTypes.InvoiceCancellation or F4JobTypes.InvoiceDueScan)
        {
            var processor = services.GetRequiredService<IF4JobProcessor>();
            var succeeded = await processor.ProcessAsync(job.TenantId, job.ConnectionId, job.JobType, job.PayloadJson, job.CorrelationId, cancellationToken);
            return new JobResult(succeeded, succeeded ? null : "F4_JOB_REJECTED");
        }

        return new JobResult(false, "UNSUPPORTED_JOB_TYPE");
    }

    private sealed record ImportJobPayload(Guid SessionId, string Operation);
    private sealed record JobResult(bool Succeeded, string? ErrorCode);
}
