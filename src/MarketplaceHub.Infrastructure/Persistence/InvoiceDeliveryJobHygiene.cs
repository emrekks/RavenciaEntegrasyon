using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

// Owner decision 7 Nov 2026 (live: five INVOICE_MARKETPLACE_DELIVERY jobs of one invoice made on 13 Oct by the delivery
// recovery and the reconcile redelivery, each under its own time-bucketed key, each stopping for review, all sitting until
// 7 Nov). An invoice has at most one delivery job per package that is waiting, running or stopped for the owner's review:
// an automatic trigger adds none next to it, whatever its key. And a delivery job that is still waiting is closed at once
// when the invoice goes to review, is cancelled, or its delivery is confirmed (also by the owner's "İletildi Say"),
// instead of waiting for the next run or an operator. A leased job keeps its lease (the worker owns it; the delivery
// itself refuses an invoice in review or cancelled). Jobs outside the "{type}:{invoiceId}:" key series (the Hepsiburada
// exchange resend, the owner's one-time stage delivery) are never touched here. No invoice is issued or sent here.
internal static class InvoiceDeliveryJobHygiene
{
    public const string ManualReviewClosedCode = "INVOICE_DELIVERY_CLOSED_FOR_MANUAL_REVIEW";
    public const string OwnerConfirmedCode = "INVOICE_DELIVERY_CONFIRMED_BY_OWNER";

    public static string KeyPrefix(Guid invoiceId) => $"{InvoicingJobTypes.MarketplaceDelivery}:{invoiceId}:";

    public static bool IsWaitingOrRunning(JobStatus status) => status is JobStatus.Pending or JobStatus.Leased or JobStatus.RetryScheduled;

    // Whether an existing delivery job of the invoice holds back a new automatic one for this package (null: the whole
    // invoice): one that is waiting or running, or one stopped for the owner's review when the review counts.
    public static bool Holds(JobStatus status, string payloadJson, Guid? packageId, bool reviewHolds) =>
        (IsWaitingOrRunning(status) || reviewHolds && status == JobStatus.ManualReview)
        && (packageId is not { } package || InvoicingJobProcessor.DeliveryJobCoversPackage(payloadJson, package));

    // Why a waiting delivery job of the invoice has nothing left to do, or null while it still has.
    public static (string Code, string Summary)? CloseReason(InvoiceStatus invoiceStatus, bool targetConfirmed, bool ownerConfirmed = false)
    {
        if (ownerConfirmed)
            return (OwnerConfirmedCode, "Fatura sizin “İletildi Say” onayınızla pazaryerinde iletildi sayıldı; bekleyen bu iletim işi kapatıldı.");
        if (targetConfirmed)
            return (InvoicingJobProcessor.DeliverySettledElsewhereCode, "Fatura pazaryerine başka bir işle iletildi ve doğrulandı; bu iş otomatik kapatıldı.");
        if (InvoiceDeliveryGuardPolicy.InvoiceBlocksDelivery(invoiceStatus))
            return (InvoiceDeliveryGuardPolicy.CancelledJobCode, "Fatura iptal edildi veya iptal ya da düzeltme sürecinde; bekleyen pazaryeri iletimi kapatıldı.");
        if (invoiceStatus == InvoiceStatus.ManualReview)
            return (ManualReviewClosedCode, "Fatura incelemeye alındı; bekleyen otomatik pazaryeri iletimi kapatıldı. İletim yalnız sizin işleminizle (Tekrar Gönder) yeniden başlar.");
        return null;
    }

    // Whether an automatic trigger must not add a delivery job for this invoice and package: the invoice is in review,
    // or a job of its key series that covers the package is waiting, running or stopped for review. Jobs added to this
    // context and not yet saved count too.
    public static async Task<bool> AutomaticDeliveryHeldAsync(AppDbContext db, Guid tenantId, Guid invoiceId, Guid? packageId, bool reviewHolds, CancellationToken cancellationToken)
    {
        var invoiceStatus = db.Invoices.Local.FirstOrDefault(invoice => invoice.TenantId == tenantId && invoice.Id == invoiceId)?.Status
            ?? await db.Invoices.AsNoTracking().Where(invoice => invoice.TenantId == tenantId && invoice.Id == invoiceId)
                .Select(invoice => (InvoiceStatus?)invoice.Status).SingleOrDefaultAsync(cancellationToken);
        if (invoiceStatus == InvoiceStatus.ManualReview) return true;
        var prefix = KeyPrefix(invoiceId);
        if (db.IntegrationJobs.Local.Any(job => job.TenantId == tenantId && job.JobType == InvoicingJobTypes.MarketplaceDelivery
                && job.JobDedupKey.StartsWith(prefix, StringComparison.Ordinal) && Holds(job.Status, job.PayloadJson, packageId, reviewHolds)))
            return true;
        var jobs = await db.IntegrationJobs.AsNoTracking()
            .Where(job => job.TenantId == tenantId && job.JobType == InvoicingJobTypes.MarketplaceDelivery && job.JobDedupKey.StartsWith(prefix)
                && (job.Status == JobStatus.Pending || job.Status == JobStatus.Leased || job.Status == JobStatus.RetryScheduled || job.Status == JobStatus.ManualReview))
            .Select(job => new { job.Status, job.PayloadJson })
            .ToListAsync(cancellationToken);
        return jobs.Any(job => Holds(job.Status, job.PayloadJson, packageId, reviewHolds));
    }

    // The invoice's waiting delivery job (Pending, Leased or RetryScheduled) of its key series, if any.
    public static Task<Guid?> OpenJobAsync(AppDbContext db, Guid tenantId, Guid invoiceId, CancellationToken cancellationToken)
    {
        var prefix = KeyPrefix(invoiceId);
        return db.IntegrationJobs.AsNoTracking()
            .Where(job => job.TenantId == tenantId && job.JobType == InvoicingJobTypes.MarketplaceDelivery && job.JobDedupKey.StartsWith(prefix)
                && (job.Status == JobStatus.Pending || job.Status == JobStatus.Leased || job.Status == JobStatus.RetryScheduled))
            .OrderByDescending(job => job.CreatedAt)
            .Select(job => (Guid?)job.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }

    // Closes the invoice's waiting (Pending or RetryScheduled) delivery jobs that have nothing left to do, reading the
    // saved invoice and delivery states. After the owner's "İletildi Say" (ownerConfirmed) the jobs stopped for review,
    // blocked or dead are closed as well: the owner settled what they were waiting for. Each close is a guarded update, so a job a worker leased meanwhile is left alone
    // and nothing else pending in the context is saved. Returns how many were closed.
    public static async Task<int> CloseWaitingJobsAsync(AppDbContext db, Guid tenantId, Guid invoiceId, DateTimeOffset now, CancellationToken cancellationToken, bool ownerConfirmed = false)
    {
        var prefix = KeyPrefix(invoiceId);
        var jobs = await db.IntegrationJobs.AsNoTracking()
            .Where(job => job.TenantId == tenantId && job.JobType == InvoicingJobTypes.MarketplaceDelivery && job.JobDedupKey.StartsWith(prefix)
                && (job.Status == JobStatus.Pending || job.Status == JobStatus.RetryScheduled
                    || ownerConfirmed && (job.Status == JobStatus.ManualReview || job.Status == JobStatus.Blocked || job.Status == JobStatus.Dead)))
            .Select(job => new { job.Id, job.PayloadJson, job.Status })
            .ToListAsync(cancellationToken);
        if (jobs.Count == 0) return 0;
        var invoiceStatus = await db.Invoices.AsNoTracking().Where(invoice => invoice.TenantId == tenantId && invoice.Id == invoiceId)
            .Select(invoice => (InvoiceStatus?)invoice.Status).SingleOrDefaultAsync(cancellationToken);
        if (invoiceStatus is null) return 0;
        var states = await db.MarketplaceDeliveryStates.AsNoTracking()
            .Where(state => state.TenantId == tenantId && state.InvoiceId == invoiceId)
            .Select(state => new { state.PackageId, state.Status }).ToListAsync(cancellationToken);
        var closed = 0;
        foreach (var job in jobs)
        {
            var target = InvoicingJobProcessor.DeliveryJobTarget(job.PayloadJson);
            var targetConfirmed = target?.PackageId is { } packageId
                ? states.Any(state => state.PackageId == packageId && state.Status == "CONFIRMED")
                : states.Count > 0 && states.All(state => state.Status == "CONFIRMED");
            if (CloseReason(invoiceStatus.Value, targetConfirmed, ownerConfirmed) is not { } reason) continue;
            closed += await db.IntegrationJobs
                .Where(item => item.Id == job.Id && item.TenantId == tenantId && item.Status == job.Status)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.Status, JobStatus.Cancelled)
                    .SetProperty(item => item.LastErrorCode, reason.Code)
                    .SetProperty(item => item.LastErrorSummary, reason.Summary)
                    .SetProperty(item => item.CompletedAt, now)
                    .SetProperty(item => item.Version, item => item.Version + 1), cancellationToken);
        }
        return closed;
    }
}
