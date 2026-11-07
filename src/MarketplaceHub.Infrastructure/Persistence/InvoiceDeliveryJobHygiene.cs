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
    public const string StoppedJobClosedAuditAction = "INVOICE_DELIVERY_STOPPED_JOB_CLOSED_INVOICE_CANCELLED";

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

    // Owner decision 7 Nov 2026 (live: eleven delivery jobs of invoice DAP2026000000436, stopped for review on 13 Oct with
    // DELIVERY_RETRY_PAYLOAD_CHANGED, still open long after the invoice was cancelled). A delivery job stopped for review,
    // blocked or dead has nothing left to do once its invoice is cancelled for good: cancelled at the provider, a local
    // draft cancelled or replaced, or refused by the provider (all states InvoiceBlocksDelivery holds, none leading back
    // to a deliverable invoice). An invoice whose cancellation is pending or was refused, one waiting for an adjustment,
    // a live one and a missing one leave the stopped job as it is.
    public static bool InvoiceCancelledForGood(InvoiceStatus status) =>
        status is InvoiceStatus.Cancelled or InvoiceStatus.CancelledLocal or InvoiceStatus.Rejected;

    // Whether a stopped delivery job closes because its invoice is cancelled for good: the job is stopped, of its
    // invoice's key series, and the invoice's saved status is known and final.
    public static bool StoppedJobClosesForCancelledInvoice(JobStatus jobStatus, string jobDedupKey, string payloadJson, IReadOnlyDictionary<Guid, InvoiceStatus> invoiceStatuses) =>
        jobStatus is JobStatus.ManualReview or JobStatus.Blocked or JobStatus.Dead
        && InvoicingJobProcessor.DeliveryJobTarget(payloadJson) is { } target
        && jobDedupKey.StartsWith(KeyPrefix(target.InvoiceId), StringComparison.Ordinal)
        && invoiceStatuses.TryGetValue(target.InvoiceId, out var invoiceStatus)
        && InvoiceCancelledForGood(invoiceStatus);

    // Closes the stopped (review, blocked, dead) delivery jobs of the given invoices, or of every invoice (null: the
    // scheduled catch-up), whose invoice is cancelled for good, as Cancelled with the cancelled code. Each close raises
    // the job's "jobs" event and writes an audit entry. Idempotent: a closed job is no longer stopped. The changes are
    // tracked and the caller saves them (a job changed meanwhile fails that save on its version). Returns how many.
    public static async Task<int> CloseStoppedJobsOfCancelledInvoicesAsync(AppDbContext db, Guid tenantId, IReadOnlyCollection<Guid>? invoiceIds, DateTimeOffset now, string correlationId, CancellationToken cancellationToken)
    {
        if (invoiceIds is { Count: 0 }) return 0;
        // Stopped delivery jobs are few (each waits for the owner), so they are read whole and matched here.
        var candidates = (await db.IntegrationJobs.AsNoTracking()
                .Where(job => job.TenantId == tenantId && job.JobType == InvoicingJobTypes.MarketplaceDelivery
                    && (job.Status == JobStatus.ManualReview || job.Status == JobStatus.Blocked || job.Status == JobStatus.Dead))
                .Select(job => new { job.Id, job.Status, job.JobDedupKey, job.PayloadJson })
                .ToListAsync(cancellationToken))
            .Select(job => (job.Id, job.Status, job.JobDedupKey, job.PayloadJson, InvoiceId: InvoicingJobProcessor.DeliveryJobTarget(job.PayloadJson)?.InvoiceId))
            .Where(job => job.InvoiceId is { } invoiceId && (invoiceIds is null || invoiceIds.Contains(invoiceId)))
            .ToList();
        if (candidates.Count == 0) return 0;
        var targetInvoiceIds = candidates.Select(job => job.InvoiceId!.Value).Distinct().ToArray();
        var statuses = await db.Invoices.AsNoTracking()
            .Where(invoice => invoice.TenantId == tenantId && targetInvoiceIds.Contains(invoice.Id))
            .ToDictionaryAsync(invoice => invoice.Id, invoice => invoice.Status, cancellationToken);
        // A status changed in this context and not saved yet counts: a cancel path closes the jobs in its own save.
        foreach (var local in db.Invoices.Local.Where(invoice => invoice.TenantId == tenantId && statuses.ContainsKey(invoice.Id)).ToList())
            statuses[local.Id] = local.Status;
        var closing = candidates.Where(job => StoppedJobClosesForCancelledInvoice(job.Status, job.JobDedupKey, job.PayloadJson, statuses)).Select(job => job.Id).ToArray();
        if (closing.Length == 0) return 0;
        var jobs = await db.IntegrationJobs.Where(job => job.TenantId == tenantId && closing.Contains(job.Id)).ToListAsync(cancellationToken);
        var closed = 0;
        foreach (var job in jobs)
        {
            if (!StoppedJobClosesForCancelledInvoice(job.Status, job.JobDedupKey, job.PayloadJson, statuses)) continue;
            var invoiceId = InvoicingJobProcessor.DeliveryJobTarget(job.PayloadJson)!.Value.InvoiceId;
            var previous = $"{job.Status}:{job.LastErrorCode}";
            var reason = CloseReason(statuses[invoiceId], targetConfirmed: false)!.Value;
            job.Status = JobStatus.Cancelled;
            job.CompletedAt = now;
            job.LastErrorCode = reason.Code;
            job.LastErrorSummary = reason.Summary;
            job.Version++;
            db.IntegrationOutboxEvents.Add(JobLeaseService.NewJobEvent(job, now));
            db.AuditLogs.Add(new AuditLog
            {
                TenantId = tenantId,
                Action = StoppedJobClosedAuditAction,
                TargetType = "IntegrationJob",
                TargetId = job.Id.ToString("D"),
                Reason = $"invoice:{invoiceId:D}:{statuses[invoiceId]}:was:{previous}",
                CorrelationId = correlationId,
                CreatedAt = now
            });
            closed++;
        }
        return closed;
    }

    // After a cancel path saved the invoice as cancelled for good: closes its stopped delivery jobs in a save of their
    // own. Housekeeping only: a job changed meanwhile, or any failure, never undoes the saved cancellation; the
    // scheduled scan closes what is left.
    public static async Task CloseStoppedJobsAfterCancellationAsync(AppDbContext db, Guid tenantId, Guid invoiceId, InvoiceStatus invoiceStatus, DateTimeOffset now, string correlationId, CancellationToken cancellationToken)
    {
        if (!InvoiceCancelledForGood(invoiceStatus)) return;
        try
        {
            if (await CloseStoppedJobsOfCancelledInvoicesAsync(db, tenantId, [invoiceId], now, correlationId, cancellationToken) > 0)
                await db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            foreach (var entry in db.ChangeTracker.Entries().Where(entry => entry.State is EntityState.Added or EntityState.Modified).ToList())
                entry.State = EntityState.Detached;
        }
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
