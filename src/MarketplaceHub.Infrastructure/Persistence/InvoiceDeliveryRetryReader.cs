using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

// Reads, for issued sale invoices, whether the document fetch or the marketplace delivery still has to be retried
// (InvoiceDeliveryAutoRetryPolicy) and the state the invoice screens show. The scheduler, the manual "Tekrar Dene", the
// invoice detail and the invoice workspace all read it here, so they never disagree.
internal static class InvoiceDeliveryRetryReader
{
    // Need: what the scheduler does (None when only the owner may send it again); OwnerNeed: what the owner's "Tekrar
    // Gönder" does. An entry exists when either is not None.
    internal sealed record Entry(Guid InvoiceId, InvoiceDeliveryRetryNeed Need, bool HasActiveJob, InvoiceDeliveryRetryView View, InvoiceDeliveryRetryNeed OwnerNeed = InvoiceDeliveryRetryNeed.None)
    {
        public InvoiceDeliveryRetryNeed EffectiveOwnerNeed => OwnerNeed != InvoiceDeliveryRetryNeed.None ? OwnerNeed : Need;
    }

    public static async Task<IReadOnlyDictionary<Guid, Entry>> LoadAsync(AppDbContext db, Guid tenantId, IReadOnlyCollection<Guid> invoiceIds, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (invoiceIds.Count == 0) return new Dictionary<Guid, Entry>();
        var ids = invoiceIds.Distinct().ToArray();
        var invoices = await db.Invoices.AsNoTracking()
            .Where(invoice => invoice.TenantId == tenantId && ids.Contains(invoice.Id)
                && invoice.SequencePurpose == "SALE" && invoice.OriginalInvoiceId == null
                && invoice.ExternalReference != null
                && (invoice.Status == InvoiceStatus.Accepted || invoice.Status == InvoiceStatus.MarketplaceFailed || invoice.Status == InvoiceStatus.MarketplacePending))
            .Select(invoice => new { invoice.Id, invoice.Status, invoice.LastErrorCode, invoice.IssuedAt, invoice.CreatedAt, invoice.UpdatedAt, invoice.DeliveryRetryAttempts, invoice.DeliveryRetryNextAt })
            .ToListAsync(cancellationToken);
        if (invoices.Count == 0) return new Dictionary<Guid, Entry>();
        var candidateIds = invoices.Select(invoice => invoice.Id).ToArray();
        var documented = (await db.InvoiceDocuments.AsNoTracking()
                .Where(document => document.TenantId == tenantId && candidateIds.Contains(document.InvoiceId) && document.PermanentUrl != null)
                .Select(document => document.InvoiceId).Distinct().ToListAsync(cancellationToken))
            .ToHashSet();
        var states = (await db.MarketplaceDeliveryStates.AsNoTracking()
                .Where(state => state.TenantId == tenantId && candidateIds.Contains(state.InvoiceId))
                .Select(state => new { state.InvoiceId, state.Status }).ToListAsync(cancellationToken))
            .ToLookup(state => state.InvoiceId, state => state.Status);
        var entries = new Dictionary<Guid, Entry>();
        foreach (var invoice in invoices)
        {
            // Every invoice job is keyed "{type}:{invoiceId}:..." (InvoicingBillingService.AddJob, EnqueueAutomaticJob,
            // the delivery recovery), so the key prefix finds this invoice's fetch and delivery jobs.
            var fetchPrefix = $"{InvoicingJobTypes.InvoiceDocumentFetch}:{invoice.Id}:";
            var deliveryPrefix = $"{InvoicingJobTypes.MarketplaceDelivery}:{invoice.Id}:";
            var reconcilePrefix = $"{InvoicingJobTypes.InvoiceReconcile}:{invoice.Id}:";
            var jobs = await db.IntegrationJobs.AsNoTracking()
                .Where(job => job.TenantId == tenantId
                    && (job.JobDedupKey.StartsWith(fetchPrefix) || job.JobDedupKey.StartsWith(deliveryPrefix) || job.JobDedupKey.StartsWith(reconcilePrefix)))
                .Select(job => new { job.JobType, job.Status, job.CreatedAt })
                .ToListAsync(cancellationToken);
            var hasActiveJob = jobs.Any(job => job.Status is JobStatus.Pending or JobStatus.Leased or JobStatus.RetryScheduled);
            // An Accepted or MarketplaceFailed invoice is retried only after a delivery that failed retryably: the latest
            // delivery job used up its retries (Dead). One that was refused (Blocked: a missing package, a refused
            // precondition) or stopped for review stays with the owner, as the never-queued recovery leaves it. A
            // MarketplacePending one that sent nothing is retried once its latest delivery job ended, however it ended.
            var latestDeliveryJob = jobs.Where(job => job.JobType == InvoicingJobTypes.MarketplaceDelivery)
                .OrderByDescending(job => job.CreatedAt).Select(job => (JobStatus?)job.Status).FirstOrDefault();
            var latestEnded = latestDeliveryJob is { } latest && latest is not (JobStatus.Pending or JobStatus.Leased or JobStatus.RetryScheduled);
            var deliveryStates = states[invoice.Id].ToArray();
            var need = InvoiceDeliveryAutoRetryPolicy.Need(invoice.Status, true, documented.Contains(invoice.Id),
                deliveryStates, latestEnded, invoice.LastErrorCode, latestDeliveryJobDead: latestDeliveryJob == JobStatus.Dead);
            // The owner's "Tekrar Gönder" stands for an issued invoice that did not reach the marketplace: a failed one
            // (also one the marketplace refused), and a pending one that sent nothing once its delivery job ended. An
            // Accepted one keeps the scheduler's path (its first delivery may still be on its way).
            var ownerNeed = invoice.Status == InvoiceStatus.MarketplaceFailed || invoice.Status == InvoiceStatus.MarketplacePending && latestEnded
                ? InvoiceDeliveryAutoRetryPolicy.Need(invoice.Status, true, documented.Contains(invoice.Id), deliveryStates, latestEnded, invoice.LastErrorCode, owner: true)
                : InvoiceDeliveryRetryNeed.None;
            if (need == InvoiceDeliveryRetryNeed.None && ownerNeed == InvoiceDeliveryRetryNeed.None) continue;
            var view = InvoiceDeliveryAutoRetryPolicy.Describe(invoice.DeliveryRetryAttempts, invoice.DeliveryRetryNextAt,
                invoice.IssuedAt ?? invoice.CreatedAt, invoice.UpdatedAt, hasActiveJob, need != InvoiceDeliveryRetryNeed.None ? need : ownerNeed,
                invoice.LastErrorCode, now, ScheduledJobProducer.InvoiceDueScanIntervalSeconds, automatic: need != InvoiceDeliveryRetryNeed.None);
            entries[invoice.Id] = new(invoice.Id, need, hasActiveJob, view, ownerNeed);
        }
        return entries;
    }

    // A fetch of the stored invoice's document, keyed per attempt so an earlier fetch (the one after acceptance, a
    // previous retry) never blocks it. The key travels in the payload: the delivery that follows the document is keyed
    // by it too (InvoicingJobProcessor.FetchDocument).
    public static IntegrationJob NewDocumentFetchJob(Invoice invoice, string retryKey, int priority, string correlationId, DateTimeOffset now, string triggerType = "system")
    {
        var payload = JsonSerializer.Serialize(new { invoiceId = invoice.Id, deliveryRetryKey = retryKey });
        var dedup = $"{InvoicingJobTypes.InvoiceDocumentFetch}:{invoice.Id}:{retryKey}";
        return new IntegrationJob
        {
            Id = Guid.CreateVersion7(),
            TenantId = invoice.TenantId,
            ConnectionId = invoice.ProviderConnectionId,
            JobType = InvoicingJobTypes.InvoiceDocumentFetch,
            PayloadJson = payload,
            PayloadVersion = 1,
            PayloadHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))),
            JobDedupKey = dedup,
            EffectIdempotencyKey = dedup,
            Priority = priority,
            TriggerType = triggerType,
            AvailableAt = now,
            CorrelationId = correlationId,
            Version = 1
        };
    }

    // Records an automatic attempt: the count goes up and the next one is scheduled, or none after the last.
    public static void RecordAutomaticAttempt(Invoice invoice, DateTimeOffset now)
    {
        invoice.DeliveryRetryAttempts++;
        invoice.DeliveryRetryNextAt = InvoiceDeliveryAutoRetryPolicy.DelayAfter(invoice.DeliveryRetryAttempts) is { } delay ? now + delay : null;
    }

    // A manual retry starts the count again; the automatic attempts follow it after the first wait.
    public static void RecordManualAttempt(Invoice invoice, DateTimeOffset now)
    {
        invoice.DeliveryRetryAttempts = 0;
        invoice.DeliveryRetryNextAt = now + InvoiceDeliveryAutoRetryPolicy.DelayAfter(1)!.Value;
    }

    internal static string ReadRetryKey(string payloadJson)
    {
        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("deliveryRetryKey", out var value)
                && value.ValueKind == JsonValueKind.String
                    ? value.GetString() ?? string.Empty
                    : string.Empty;
        }
        catch (JsonException) { return string.Empty; }
    }
}
