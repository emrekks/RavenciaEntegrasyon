using MarketplaceHub.Domain;

namespace MarketplaceHub.Application;

// What an issued sale invoice still needs before it reaches the marketplace.
public enum InvoiceDeliveryRetryNeed
{
    None,
    // The provider holds the invoice but its document was never stored: fetch it from E-Faturam, then deliver.
    FetchDocument,
    // The document is stored, an earlier delivery job ended without delivering it, and nothing was sent that could
    // have reached the marketplace: deliver again.
    Deliver
}

/// <summary>
/// The scheduler's retry of issued (Accepted or MarketplaceFailed) sale invoices that are not on the marketplace yet.
/// Each attempt is a new job with its own dedupe key; the delivery job keeps its own state machine, so a package whose
/// delivery STARTED, was SUBMITTED or CONFIRMED is never written twice. Attempts back off (1 h, 3 h, 6 h, 12 h) and
/// stop after <see cref="MaxAttempts"/>; a manual "Tekrar Dene" runs the same path and starts the count again.
/// No new invoice is ever issued here: only the stored invoice's document and its delivery are retried.
/// </summary>
public static class InvoiceDeliveryAutoRetryPolicy
{
    public const int MaxAttempts = 5;
    public const string Scheduled = "SCHEDULED";
    public const string InProgress = "IN_PROGRESS";
    public const string Exhausted = "EXHAUSTED";
    public const string NotApplicableCode = "INVOICE_DELIVERY_RETRY_NOT_APPLICABLE";
    public const string AlreadyRunningCode = "INVOICE_DELIVERY_RETRY_RUNNING";

    // An invoice touched this recently may still have its own chain (reconcile, fetch, delivery) in flight.
    public static readonly TimeSpan SettleDelay = TimeSpan.FromMinutes(5);

    // The same window as the never-queued delivery recovery: an old invoice is never sent by surprise.
    public static TimeSpan Window => InvoiceMarketplaceAutoDeliveryPolicy.NeverQueuedRecoveryWindow;

    private static readonly TimeSpan[] Delays = [TimeSpan.FromHours(1), TimeSpan.FromHours(3), TimeSpan.FromHours(6), TimeSpan.FromHours(12)];

    // The wait after the given (1-based) attempt; null after the last one.
    public static TimeSpan? DelayAfter(int attempt) =>
        attempt >= 1 && attempt < MaxAttempts ? Delays[Math.Min(attempt, Delays.Length) - 1] : null;

    // Delivery states a retry may follow: a read proved the marketplace does not hold the invoice (REVOKED), or the
    // marketplace refused the write in a retryable way. Anything else (STARTED, SUBMITTED, CONFIRMED, UNKNOWN, FAILED,
    // a link in removal) is the delivery job's own business and is never written over from here.
    public static bool StateAllowsRetry(string? deliveryStateStatus) =>
        deliveryStateStatus is "REVOKED" or "RETRYABLE_FAILURE";

    public static InvoiceDeliveryRetryNeed Need(
        InvoiceStatus status,
        bool hasProviderReference,
        bool hasDocument,
        IReadOnlyCollection<string> deliveryStateStatuses,
        bool hadEndedDeliveryJob,
        string? lastErrorCode)
    {
        if (status is not (InvoiceStatus.Accepted or InvoiceStatus.MarketplaceFailed) || !hasProviderReference) return InvoiceDeliveryRetryNeed.None;
        if (deliveryStateStatuses.Any(state => !StateAllowsRetry(state))) return InvoiceDeliveryRetryNeed.None;
        if (!hasDocument) return InvoiceDeliveryRetryNeed.FetchDocument;
        // The delivery was skipped on purpose (switch off, platform without an API, missing package): nothing to retry.
        if (InvoiceMarketplaceAutoDeliveryPolicy.IsSkipCode(lastErrorCode)) return InvoiceDeliveryRetryNeed.None;
        // A failed invoice with a REVOKED or retryable state is resent by the five-minute delivery recovery already,
        // and one that never had a delivery job by the never-queued recovery.
        if (status == InvoiceStatus.MarketplaceFailed && deliveryStateStatuses.Count > 0) return InvoiceDeliveryRetryNeed.None;
        return hadEndedDeliveryJob ? InvoiceDeliveryRetryNeed.Deliver : InvoiceDeliveryRetryNeed.None;
    }

    // The scheduler runs the invoice scan every half hour (ScheduledJobProducer.InvoiceDueScanIntervalSeconds); an
    // attempt that is due runs at the next scan.
    public static DateTimeOffset NextScanAtOrAfter(DateTimeOffset due, int scanIntervalSeconds)
    {
        var seconds = due.ToUnixTimeSeconds();
        var next = (seconds + scanIntervalSeconds - 1) / scanIntervalSeconds * scanIntervalSeconds;
        return DateTimeOffset.FromUnixTimeSeconds(next);
    }

    public static bool InWindow(DateTimeOffset issuedAt, DateTimeOffset now) => issuedAt >= now - Window;

    // SCHEDULED while automatic attempts remain inside the window, IN_PROGRESS while a fetch or delivery job of the
    // invoice runs, EXHAUSTED once the attempts are used up or the window has passed.
    public static InvoiceDeliveryRetryView Describe(
        int attempts,
        DateTimeOffset? nextAttemptAt,
        DateTimeOffset issuedAt,
        DateTimeOffset updatedAt,
        bool hasActiveJob,
        InvoiceDeliveryRetryNeed need,
        string? lastErrorCode,
        DateTimeOffset now,
        int scanIntervalSeconds)
    {
        var needCode = need == InvoiceDeliveryRetryNeed.FetchDocument ? "FETCH_DOCUMENT" : "DELIVER";
        if (hasActiveJob) return new(InProgress, attempts, MaxAttempts, null, lastErrorCode, needCode);
        if (attempts >= MaxAttempts || !InWindow(issuedAt, now)) return new(Exhausted, attempts, MaxAttempts, null, lastErrorCode, needCode);
        var due = nextAttemptAt ?? now;
        if (due < updatedAt + SettleDelay) due = updatedAt + SettleDelay;
        if (due < now) due = now;
        return new(Scheduled, attempts, MaxAttempts, NextScanAtOrAfter(due, scanIntervalSeconds), lastErrorCode, needCode);
    }
}

public sealed record InvoiceDeliveryRetryView(string State, int Attempts, int MaxAttempts, DateTimeOffset? NextAttemptAt, string? LastErrorCode, string Need);
