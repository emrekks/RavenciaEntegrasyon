namespace MarketplaceHub.Application;

// A retry whose code says the marketplace (or the invoice provider) is still working on a request that was accepted:
// a batch task still running, a product waiting for approval, a label still being prepared. Such a run did its job:
// it asked and was told to come back later. It is a waiting check, not a failure: it does not use up the job's
// attempts while the job is young, it does not count as a failed attempt or an error time, and the job screens
// show it as "Kontrol Edildi" instead of "Başarısız".
public static class JobWaitingPolicy
{
    // Codes a processor returns with JobCompletionKind.Retry only while the remote side is still processing.
    private static readonly HashSet<string> WaitingCodes = new(StringComparer.Ordinal)
    {
        "PRODUCT_BATCH_PENDING",
        "PRODUCT_UPDATE_BATCH_PENDING",
        "PRODUCT_UPDATE_NEXT_PHASE",
        "PRODUCT_ARCHIVE_BATCH_PENDING",
        "PRODUCT_ARCHIVE_RECONCILE_PENDING",
        "PRICE_INVENTORY_BATCH_PENDING",
        "PRODUCT_WRITE_IN_PROGRESS",
        "PRODUCT_APPROVAL_CHECK_IN_PROGRESS",
        ApprovalPendingCode,
        "HEPSIBURADA_PRODUCT_APPROVAL_PENDING",
        "HEPSIBURADA_PRODUCT_IMPORT_PENDING",
        "COMMON_LABEL_PENDING",
        "COMMON_LABEL_READ_PENDING",
        "INVOICE_REMOTE_PENDING",
        "INVOICE_CANCELLATION_REMOTE_PENDING",
        "DELIVERY_REMOTE_PENDING"
    };

    // The approval codes: the marketplace still reviews the product.
    private static readonly HashSet<string> ApprovalCodes = new(StringComparer.Ordinal)
    {
        ApprovalPendingCode,
        "HEPSIBURADA_PRODUCT_APPROVAL_PENDING"
    };

    public const string ApprovalPendingCode = "PRODUCT_APPROVAL_PENDING";

    public static IReadOnlyCollection<string> Codes => WaitingCodes;

    public static bool IsWaitingCode(string? code) => code is not null && WaitingCodes.Contains(code);

    public static bool IsApprovalCode(string? code) => code is not null && ApprovalCodes.Contains(code);

    public static bool IsWaiting(JobExecutionResult result) =>
        result.Kind == JobCompletionKind.Retry && !result.RefundAttempt && IsWaitingCode(result.ErrorCode);

    // How long a job may keep checking without using up its attempts. An approval check has its own seven-day
    // deadline (ProductApprovalReconciliationPolicy); every other wait is bounded by its processor's window (four
    // hours for a batch) and, as a backstop for a remote side that never answers, by a day.
    public static TimeSpan FreeWaitingWindow(string jobType) =>
        jobType == MarketplaceJobTypes.ProductApprovalReconcile ? TimeSpan.FromDays(8) : TimeSpan.FromDays(1);

    // A waiting check runs again after the delay its processor asked for. The first few checks keep that pace; a
    // remote task that takes longer is asked less often (30 s, 1, 2, then 5 minutes at most), so a long batch does
    // not poll a marketplace every 15 seconds for hours. A longer delay the processor asked for is kept as it is.
    public static TimeSpan Delay(TimeSpan? requested, int waitingChecks)
    {
        var asked = requested is { } value && value > TimeSpan.Zero ? value : TimeSpan.FromSeconds(30);
        var floor = waitingChecks switch
        {
            <= 4 => TimeSpan.Zero,
            5 => TimeSpan.FromSeconds(30),
            6 => TimeSpan.FromMinutes(1),
            7 => TimeSpan.FromMinutes(2),
            _ => TimeSpan.FromMinutes(5)
        };
        return asked > floor ? asked : floor;
    }

    // The outcome of one stored attempt as the job screens show it. A waiting code on the last attempt of a job that
    // ended (the limit was reached, or it was stopped) is that ending, so it stays a failure.
    public static string AttemptOutcome(bool completed, bool succeeded, string? errorCode, bool lastAttemptOfEndedJob)
    {
        if (!completed) return "RUNNING";
        if (succeeded) return "SUCCEEDED";
        return IsWaitingCode(errorCode) && !lastAttemptOfEndedJob ? "WAITING" : "FAILED";
    }
}
