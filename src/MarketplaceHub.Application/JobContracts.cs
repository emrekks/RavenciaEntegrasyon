namespace MarketplaceHub.Application;

public enum JobCompletionKind
{
    Succeeded,
    Retry,
    Blocked,
    Dead,
    ManualReview
}

public sealed record JobExecutionResult(
    JobCompletionKind Kind,
    string? ErrorCode = null,
    string? ErrorSummary = null,
    TimeSpan? RetryAfter = null,
    string? RemoteRequestId = null)
{
    public bool Succeeded => Kind == JobCompletionKind.Succeeded;

    public static JobExecutionResult Success() => new(JobCompletionKind.Succeeded);
    public static JobExecutionResult Retry(string code, string? summary = null, TimeSpan? retryAfter = null, string? remoteRequestId = null) =>
        new(JobCompletionKind.Retry, code, Safe(summary), retryAfter, remoteRequestId);
    public static JobExecutionResult Blocked(string code, string? summary = null, string? remoteRequestId = null) =>
        new(JobCompletionKind.Blocked, code, Safe(summary), null, remoteRequestId);
    public static JobExecutionResult Dead(string code, string? summary = null, string? remoteRequestId = null) =>
        new(JobCompletionKind.Dead, code, Safe(summary), null, remoteRequestId);
    public static JobExecutionResult ManualReview(string code, string? summary = null, string? remoteRequestId = null) =>
        new(JobCompletionKind.ManualReview, code, Safe(summary), null, remoteRequestId);

    public static JobExecutionResult FromAdapterError(AdapterError error) => error.Class switch
    {
        AdapterErrorClass.TransientNetwork or AdapterErrorClass.RateLimit or AdapterErrorClass.Remote5xx =>
            Retry(error.Code, error.SafeMessage, error.RetryAfter, error.RemoteRequestId),
        AdapterErrorClass.InternalBug or AdapterErrorClass.ContractViolation => ManualReview(error.Code, error.SafeMessage, error.RemoteRequestId),
        _ => Blocked(error.Code, error.SafeMessage, error.RemoteRequestId)
    };

    private static string? Safe(string? value) => string.IsNullOrWhiteSpace(value)
        ? null
        : value.Trim()[..Math.Min(value.Trim().Length, 512)];
}

public sealed record JobAttemptDetailView(
    int AttemptNumber,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    bool Succeeded,
    string? ErrorCode,
    string? ErrorSummary);

public sealed record JobSummaryView(
    Guid Id,
    Guid? ConnectionId,
    string JobType,
    string Status,
    int AttemptCount,
    int MaxAttempts,
    DateTimeOffset AvailableAt,
    string? LastErrorCode,
    string? LastErrorSummary,
    string CorrelationId,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    string Marketplace = "Trendyol",
    string? ExternalId = null,
    DateTimeOffset? FirstFailedAt = null,
    DateTimeOffset? LastFailedAt = null,
    DateTimeOffset? NextRetryAt = null,
    int BatchCount = 1,
    int ProgressCurrent = 0,
    int? ProgressTotal = null,
    int? ProgressPercent = null,
    string? ProgressLabel = null);

public sealed record JobOrderContextView(
    Guid OrderId,
    string OrderNumber,
    string ExternalOrderId,
    string Status,
    string Currency,
    decimal NetAmount,
    DateTimeOffset OrderedAt,
    string? ExternalPackageId = null,
    string? CargoProvider = null,
    string? CargoTrackingNumber = null,
    string? CustomerName = null,
    int LineCount = 0);

public sealed record JobChangeView(string Label, string Value, string? Detail = null);

public sealed record JobScanView(
    string Mode,
    string Label,
    string Detail,
    string? Window = null,
    int? PlannedIntervalSeconds = null,
    string? PlannedIntervalLabel = null,
    string? PreviousScheduledAt = null,
    string? ActualIntervalLabel = null);

public sealed record JobDetailView(
    JobSummaryView Job,
    IReadOnlyList<JobAttemptDetailView> Attempts,
    JobOrderContextView? Order = null,
    JobChangeView? Change = null,
    IReadOnlyList<JobOrderContextView>? RelatedOrders = null,
    JobScanView? Scan = null);

public interface IJobOperationsService
{
    Task<IReadOnlyList<JobSummaryView>> ListAsync(Guid tenantId, string? status, CancellationToken cancellationToken);
    Task<ServiceResult<JobDetailView>> GetAsync(Guid tenantId, Guid jobId, CancellationToken cancellationToken);
    Task<ServiceResult<JobDetailView>> RetryAsync(Guid tenantId, Guid jobId, CancellationToken cancellationToken);
    Task<ServiceResult<JobDetailView>> CancelAsync(Guid tenantId, Guid jobId, CancellationToken cancellationToken);
}

public interface IScheduledJobProducer
{
    Task<int> EnqueueDueAsync(CancellationToken cancellationToken);
}
