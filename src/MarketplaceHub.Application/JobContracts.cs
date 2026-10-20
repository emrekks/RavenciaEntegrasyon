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
    string? RemoteRequestId = null,
    bool RefundAttempt = false,
    // The marketplace rejected the call for quota: other jobs of the same connection and lock group
    // are not leased until RetryAfter has passed.
    bool BackOffConnection = false)
{
    public bool Succeeded => Kind == JobCompletionKind.Succeeded;

    public static JobExecutionResult Success() => new(JobCompletionKind.Succeeded);
    /// <summary>The job could not start yet (e.g. its connection lock is busy); retry later without consuming an attempt.</summary>
    public static JobExecutionResult Deferred(string code, string? summary = null, TimeSpan? retryAfter = null) =>
        new(JobCompletionKind.Retry, code, Safe(summary), retryAfter, null, RefundAttempt: true);
    public const string ContinueCode = "SLICE_CONTINUES";
    /// <summary>A long job finished one bounded slice and saved its position; it runs again shortly without consuming an attempt.</summary>
    public static JobExecutionResult Continue(string summary, TimeSpan? retryAfter = null) =>
        new(JobCompletionKind.Retry, ContinueCode, Safe(summary), retryAfter ?? TimeSpan.FromSeconds(2), null, RefundAttempt: true);
    public bool Continues => Kind == JobCompletionKind.Retry && ErrorCode == ContinueCode;
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
        // A quota rejection is not the job's fault: it keeps its attempt and the connection backs off.
        AdapterErrorClass.RateLimit =>
            new(JobCompletionKind.Retry, error.Code, Safe(error.SafeMessage), BoundedRemoteWait(error.RetryAfter) ?? TimeSpan.FromSeconds(30), error.RemoteRequestId, RefundAttempt: true, BackOffConnection: true),
        AdapterErrorClass.TransientNetwork or AdapterErrorClass.Remote5xx =>
            Retry(error.Code, error.SafeMessage, BoundedRemoteWait(error.RetryAfter), error.RemoteRequestId),
        AdapterErrorClass.InternalBug or AdapterErrorClass.ContractViolation => ManualReview(error.Code, error.SafeMessage, error.RemoteRequestId),
        // Refused credentials or access do not change from one minute to the next: the connection's other jobs of
        // the same group wait (five minutes unless the marketplace said how long) instead of each hitting the same wall.
        AdapterErrorClass.Authentication =>
            new(JobCompletionKind.Blocked, error.Code, Safe(error.SafeMessage), BoundedRemoteWait(error.RetryAfter) ?? TimeSpan.FromMinutes(5), error.RemoteRequestId, BackOffConnection: true),
        _ => Blocked(error.Code, error.SafeMessage, error.RemoteRequestId)
    };

    // A wait a marketplace asked for is never trusted beyond a quarter hour: a misread header (an epoch taken
    // as seconds, a far-future date) must not park the job and its connection for years.
    public static readonly TimeSpan MaximumRemoteWait = TimeSpan.FromMinutes(15);

    private static TimeSpan? BoundedRemoteWait(TimeSpan? wait) => wait is { } value && value > TimeSpan.Zero
        ? value > MaximumRemoteWait ? MaximumRemoteWait : value
        : null;

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
    string? ProgressLabel = null,
    int ProgressReceived = 0,
    int ProgressProcessed = 0,
    int ProgressSkipped = 0,
    int ProgressFailed = 0,
    DateTimeOffset? LastAttemptStartedAt = null,
    DateTimeOffset? HeartbeatAt = null);

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

public sealed record JobInvoiceContextView(
    Guid InvoiceId,
    string OrderNumber,
    string Status,
    string InvoiceType,
    string Currency,
    decimal PayableTotal,
    string? InvoiceNumber,
    string? ExternalPackageId);

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

public sealed record JobFailureReasonView(
    string Key,
    string Title,
    string Description,
    string TechnicalDetail,
    int AffectedRecords,
    IReadOnlyList<string> SampleProductIds);

public sealed record JobDetailView(
    JobSummaryView Job,
    IReadOnlyList<JobAttemptDetailView> Attempts,
    JobOrderContextView? Order = null,
    JobChangeView? Change = null,
    IReadOnlyList<JobOrderContextView>? RelatedOrders = null,
    JobScanView? Scan = null,
    IReadOnlyList<JobFailureReasonView>? FailureReasons = null,
    JobInvoiceContextView? Invoice = null);

// The job list returns at most Limit of the newest matching jobs (default 200, max 1,000)
// instead of the whole history.
public sealed record JobListQuery(string? Status = null, DateTimeOffset? Since = null, IReadOnlyList<string>? JobTypes = null, int? Limit = null)
{
    public const int DefaultLimit = 200;
    public const int MaximumLimit = 1_000;
    public int EffectiveLimit => Math.Clamp(Limit ?? DefaultLimit, 1, MaximumLimit);
}

// Per job type, over the requested window: wait = first attempt start minus the time the job
// became runnable (jobs that were never retried), duration = attempt run time.
public sealed record JobTypeMetricView(
    string JobType,
    int Attempts,
    int Failed,
    double? AverageWaitSeconds,
    double? P95WaitSeconds,
    double? AverageDurationSeconds,
    double? P95DurationSeconds,
    int Queued,
    int Running);

// Job counts by status and type over the requested window. The job list returns only the newest jobs, so the tab and
// status counts come from here to show real totals.
public sealed record JobCountView(string Status, string JobType, int Count);

// Lane is USER (actions a user started), INITIAL (first imports), HOT (new data) or BACKGROUND.
public sealed record LaneMetricView(string Lane, int Attempts, double? AverageWaitSeconds, double? P95WaitSeconds, int Queued, int Running);
// Lane is FOREGROUND (new-order reads, user actions) or BACKGROUND (every other order read).
public sealed record QuotaUsageView(string PlatformCode, string StoreName, string Lane, int LimitPerMinute, int LastHourRequests, int PeakMinuteRequests, double AveragePerMinute);

public interface IQueueHealthService
{
    Task<IReadOnlyList<LaneMetricView>> LanesAsync(Guid tenantId, TimeSpan window, CancellationToken cancellationToken);
    Task<IReadOnlyList<QuotaUsageView>> QuotaAsync(Guid tenantId, CancellationToken cancellationToken);
}

public interface IJobOperationsService
{
    Task<IReadOnlyList<JobCountView>> CountAsync(Guid tenantId, DateTimeOffset? since, CancellationToken cancellationToken);
    Task<IReadOnlyList<JobTypeMetricView>> MetricsAsync(Guid tenantId, TimeSpan window, CancellationToken cancellationToken);
    Task<IReadOnlyList<JobSummaryView>> ListAsync(Guid tenantId, JobListQuery query, CancellationToken cancellationToken);
    Task<ServiceResult<JobDetailView>> GetAsync(Guid tenantId, Guid jobId, CancellationToken cancellationToken);
    Task<ServiceResult<JobDetailView>> RetryAsync(Guid tenantId, Guid jobId, CancellationToken cancellationToken);
    Task<ServiceResult<JobDetailView>> EnqueueOneTimeInvoiceDeliveryAsync(Guid tenantId, Guid jobId, string orderNumber, string idempotencyKey, string correlationId, CancellationToken cancellationToken);
    Task<ServiceResult<JobDetailView>> CancelAsync(Guid tenantId, Guid jobId, CancellationToken cancellationToken);
}

public interface IScheduledJobProducer
{
    Task<int> EnqueueDueAsync(CancellationToken cancellationToken);

    // A user starts a data sync flow now instead of waiting for its next scheduled run.
    Task<ServiceResult<Guid>> RunNowAsync(Guid tenantId, Guid connectionId, string resourceType, string correlationId, CancellationToken cancellationToken);
}
