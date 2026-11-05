using MarketplaceHub.Domain;

namespace MarketplaceHub.Application;

// What an issued sale invoice still needs before it reaches the marketplace.
public enum InvoiceDeliveryRetryNeed
{
    None,
    // The provider holds the invoice but its document was never stored: fetch it from E-Faturam, then deliver.
    FetchDocument,
    // The document is stored and nothing that could have reached the marketplace is in flight: deliver again.
    Deliver
}

/// <summary>
/// The retry of issued sale invoices that are not on the marketplace yet: Accepted or MarketplaceFailed, and
/// MarketplacePending with no delivery state at all (the delivery stopped before anything was sent, e.g. its read of
/// the marketplace failed). Each attempt is a new job with its own dedupe key; the delivery job keeps its own state
/// machine, so a package whose delivery STARTED, was SUBMITTED or CONFIRMED is never written twice. The scheduler's
/// attempts back off (15 min, 1 h, 6 h, 24 h) and stop after <see cref="MaxAttempts"/>; the invoice then stays
/// retryable by hand ("Tekrar Gönder"), which runs the same path and starts the count again. A delivery the marketplace
/// refused (a FAILED state) is never retried by the scheduler, only by the owner.
/// No new invoice is ever issued here: only the stored invoice's document and its link are sent.
/// </summary>
public static class InvoiceDeliveryAutoRetryPolicy
{
    public const int MaxAttempts = 5;
    public const string Scheduled = "SCHEDULED";
    public const string InProgress = "IN_PROGRESS";
    public const string Exhausted = "EXHAUSTED";
    // Only the owner's "Tekrar Gönder" sends it again (a refused delivery): no automatic attempt is planned.
    public const string ManualOnly = "MANUAL_ONLY";
    public const string NotApplicableCode = "INVOICE_DELIVERY_RETRY_NOT_APPLICABLE";
    public const string AlreadyRunningCode = "INVOICE_DELIVERY_RETRY_RUNNING";
    // A delivery state the owner's retry reopened (FAILED → RETRYABLE_FAILURE): the job reads the package first again.
    public const string OwnerRetryCode = "OWNER_REQUESTED_DELIVERY_RETRY";

    // An invoice touched this recently may still have its own chain (reconcile, fetch, delivery) in flight.
    public static readonly TimeSpan SettleDelay = TimeSpan.FromMinutes(5);

    // The same window as the never-queued delivery recovery: an old invoice is never sent by surprise.
    public static TimeSpan Window => InvoiceMarketplaceAutoDeliveryPolicy.NeverQueuedRecoveryWindow;

    private static readonly TimeSpan[] Delays = [TimeSpan.FromMinutes(15), TimeSpan.FromHours(1), TimeSpan.FromHours(6), TimeSpan.FromHours(24)];

    // The wait after the given (1-based) attempt; null after the last one.
    public static TimeSpan? DelayAfter(int attempt) =>
        attempt >= 1 && attempt < MaxAttempts ? Delays[Math.Min(attempt, Delays.Length) - 1] : null;

    // Report item 11. n11 and Pazarama keep one link per order and a new link replaces the earlier one (n11's
    // SaveLinkSellerInvoice page; Pazarama's "Fatura Linki Güncelleme"), and neither can read a link back: a delivery
    // whose answer was lost (UNKNOWN) is settled by sending the link again, never by waiting for a read that cannot come.
    public static bool ResendReplacesLink(string? platformCode) =>
        platformCode?.Trim().ToUpperInvariant() is "N11" or "PAZARAMA";

    // The marketplaces whose uncertain delivery goes to the automatic retry schedule: n11 and Pazarama (a resend replaces
    // the link) and PTT AVM (its order read tells per line whether the invoice is there, IsInvoice; a resend follows only
    // when the read says it is not).
    public static bool RetriesUncertainDelivery(string? platformCode) =>
        ResendReplacesLink(platformCode) || platformCode?.Trim().ToUpperInvariant() is "PTTAVM";

    public const string UncertainResendCode = "UNCERTAIN_DELIVERY_RESEND";
    public const string UncertainResendScheduledCode = "UNCERTAIN_DELIVERY_RESEND_SCHEDULED";
    public const string UncertainResendExhaustedCode = "UNCERTAIN_DELIVERY_RESEND_EXHAUSTED";

    public enum UncertainResend { Now, Wait, Exhausted }

    // When an uncertain delivery (on a RetriesUncertainDelivery marketplace) is sent again: the same backoff as the other
    // automatic retries, counted from the moment the delivery became uncertain (attempts: the resends made so far; the
    // first waits 15 minutes), at most MaxAttempts resends and only inside the window. After that only the owner's
    // "Tekrar Gönder" sends it.
    public static UncertainResend UncertainResendDue(int attempts, DateTimeOffset uncertainSince, DateTimeOffset issuedAt, DateTimeOffset now)
    {
        if (attempts >= MaxAttempts || !InWindow(issuedAt, now)) return UncertainResend.Exhausted;
        var wait = DelayAfter(Math.Max(1, attempts)) ?? TimeSpan.Zero;
        return now >= uncertainSince + wait ? UncertainResend.Now : UncertainResend.Wait;
    }

    // Delivery states a retry may follow: a read proved the marketplace does not hold the invoice (REVOKED), or the
    // marketplace refused the write in a retryable way; on a RetriesUncertainDelivery marketplace also an uncertain one
    // (UNKNOWN). Anything else (STARTED, SUBMITTED, CONFIRMED, UNKNOWN elsewhere, FAILED, a link in removal) is the
    // delivery job's own business and is never written over from here.
    public static bool StateAllowsRetry(string? deliveryStateStatus, bool uncertainRetried = false) =>
        deliveryStateStatus is "REVOKED" or "RETRYABLE_FAILURE" || uncertainRetried && deliveryStateStatus is "UNKNOWN";

    // The owner may also send again a delivery the marketplace refused (FAILED): the job reads the package first.
    public static bool StateAllowsOwnerRetry(string? deliveryStateStatus, bool uncertainRetried = false) =>
        StateAllowsRetry(deliveryStateStatus, uncertainRetried) || deliveryStateStatus is "FAILED";

    /// <summary>
    /// What the invoice needs. hadEndedDeliveryJob: the latest delivery job ended (any end) without an active one;
    /// latestDeliveryJobDead: it used up its retries on retryable errors. owner: the owner's own "Tekrar Gönder", which
    /// also follows a refused delivery and a delivery that was skipped on purpose (its own gates still apply).
    /// </summary>
    public static InvoiceDeliveryRetryNeed Need(
        InvoiceStatus status,
        bool hasProviderReference,
        bool hasDocument,
        IReadOnlyCollection<string> deliveryStateStatuses,
        bool hadEndedDeliveryJob,
        string? lastErrorCode,
        bool owner = false,
        bool latestDeliveryJobDead = true,
        bool uncertainRetried = false)
    {
        if (!hasProviderReference) return InvoiceDeliveryRetryNeed.None;
        // Issued, delivery started, but nothing at all was sent: no delivery state on any package.
        var pendingWithoutState = status == InvoiceStatus.MarketplacePending && deliveryStateStatuses.Count == 0;
        // n11, Pazarama, PTT AVM: a delivery whose answer was lost waits for its resend (UncertainResendDue), which the
        // delivery job itself makes when it is due, and the owner can send it at once.
        var pendingUncertain = uncertainRetried && status == InvoiceStatus.MarketplacePending && deliveryStateStatuses.Contains("UNKNOWN");
        if (status is not (InvoiceStatus.Accepted or InvoiceStatus.MarketplaceFailed) && !pendingWithoutState && !pendingUncertain) return InvoiceDeliveryRetryNeed.None;
        if (deliveryStateStatuses.Any(state => !(owner ? StateAllowsOwnerRetry(state, uncertainRetried) : StateAllowsRetry(state, uncertainRetried)))) return InvoiceDeliveryRetryNeed.None;
        if (!hasDocument) return InvoiceDeliveryRetryNeed.FetchDocument;
        if (owner) return InvoiceDeliveryRetryNeed.Deliver;
        // The delivery was skipped on purpose (switch off, platform without an API, missing package): nothing to retry.
        if (InvoiceMarketplaceAutoDeliveryPolicy.IsSkipCode(lastErrorCode)) return InvoiceDeliveryRetryNeed.None;
        if (pendingUncertain) return InvoiceDeliveryRetryNeed.Deliver;
        // Nothing was sent and its delivery job ended (refused before writing, or out of retries): deliver again.
        if (pendingWithoutState) return hadEndedDeliveryJob ? InvoiceDeliveryRetryNeed.Deliver : InvoiceDeliveryRetryNeed.None;
        // A failed invoice with a REVOKED or retryable state is resent by the five-minute delivery recovery already,
        // and one that never had a delivery job by the never-queued recovery.
        if (status == InvoiceStatus.MarketplaceFailed && deliveryStateStatuses.Count > 0) return InvoiceDeliveryRetryNeed.None;
        return hadEndedDeliveryJob && latestDeliveryJobDead ? InvoiceDeliveryRetryNeed.Deliver : InvoiceDeliveryRetryNeed.None;
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
    // invoice runs, EXHAUSTED once the attempts are used up or the window has passed, MANUAL_ONLY when only the owner
    // may send it again (automaticNeed None).
    public static InvoiceDeliveryRetryView Describe(
        int attempts,
        DateTimeOffset? nextAttemptAt,
        DateTimeOffset issuedAt,
        DateTimeOffset updatedAt,
        bool hasActiveJob,
        InvoiceDeliveryRetryNeed need,
        string? lastErrorCode,
        DateTimeOffset now,
        int scanIntervalSeconds,
        bool automatic = true)
    {
        var needCode = need == InvoiceDeliveryRetryNeed.FetchDocument ? "FETCH_DOCUMENT" : "DELIVER";
        if (hasActiveJob) return new(InProgress, attempts, MaxAttempts, null, lastErrorCode, needCode);
        if (!automatic) return new(ManualOnly, attempts, MaxAttempts, null, lastErrorCode, needCode);
        if (attempts >= MaxAttempts || !InWindow(issuedAt, now)) return new(Exhausted, attempts, MaxAttempts, null, lastErrorCode, needCode);
        var due = nextAttemptAt ?? now;
        if (due < updatedAt + SettleDelay) due = updatedAt + SettleDelay;
        if (due < now) due = now;
        return new(Scheduled, attempts, MaxAttempts, NextScanAtOrAfter(due, scanIntervalSeconds), lastErrorCode, needCode);
    }
}

public sealed record InvoiceDeliveryRetryView(string State, int Attempts, int MaxAttempts, DateTimeOffset? NextAttemptAt, string? LastErrorCode, string Need);
