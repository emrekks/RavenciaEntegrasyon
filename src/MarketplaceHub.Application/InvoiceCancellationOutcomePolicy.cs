using MarketplaceHub.Domain;

namespace MarketplaceHub.Application;

/// <summary>What a failed e-Archive cancellation request leaves the invoice in, and when it may be requested again.</summary>
public static class InvoiceCancellationOutcomePolicy
{
    // Only the provider's refusal of the request itself (a 400/422 answer, or the write switch being off) is a refusal.
    // A refused session or access, a quota, "not found" or a conflict says nothing about the invoice: the cancellation
    // stays pending and is tried again, and every run reads the provider first, so one cancelled meanwhile is not sent twice.
    public static InvoiceStatus StatusAfterFailure(AdapterErrorClass errorClass) => errorClass switch
    {
        AdapterErrorClass.TransientNetwork or AdapterErrorClass.RateLimit or AdapterErrorClass.Remote5xx
            or AdapterErrorClass.Authentication or AdapterErrorClass.NotFound or AdapterErrorClass.BusinessConflict => InvoiceStatus.CancellationPending,
        AdapterErrorClass.ContractViolation or AdapterErrorClass.InternalBug => InvoiceStatus.ManualReview,
        _ => InvoiceStatus.CancellationRejected
    };

    // A session, access, "not found" or conflict answer stops other jobs of the kind, but a pending cancellation is
    // tried again after a pause rather than left without a job.
    public static bool RetriesLater(AdapterErrorClass errorClass) =>
        errorClass is AdapterErrorClass.Authentication or AdapterErrorClass.NotFound or AdapterErrorClass.BusinessConflict;

    public static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(5);

    // An issued invoice, a refused cancellation, or a pending one whose job has ended (its retries ran out) can be
    // cancelled from the panel; a pending one with a job still running waits for that job.
    public static bool CanRequest(InvoiceStatus status, bool jobRunning) =>
        status is InvoiceStatus.Accepted or InvoiceStatus.Completed or InvoiceStatus.AdjustmentRequired or InvoiceStatus.CancellationRejected
        || status == InvoiceStatus.CancellationPending && !jobRunning;
}
