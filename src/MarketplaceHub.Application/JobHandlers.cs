namespace MarketplaceHub.Application;

// A job type handled by its own class instead of a branch of the marketplace job processor. The processor
// still takes the connection's execution lock and records the sync telemetry around the handler; the
// handler does the job and reports what it counted. Job types without a handler keep the processor's
// own path, so job types move over one at a time.
// Reads: where a read job notes each record it pulled (the job detail's "Çekilen Kayıtlar"); null when the job keeps none.
public sealed record JobRequest(Guid TenantId, Guid ConnectionId, string JobType, string PayloadJson, string CorrelationId, Guid? JobId, JobReadRecorder? Reads = null);

public sealed record JobTelemetry(int Requests = 0, int Received = 0, int Inserted = 0, int Updated = 0, int Changed = 0, int Failed = 0);

public sealed record JobOutcome(JobExecutionResult Result, JobTelemetry Telemetry);

public interface IJobHandler
{
    bool Handles(string jobType);
    Task<JobOutcome> HandleAsync(JobRequest request, CancellationToken cancellationToken);
}
