using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Adapters.PttAvm;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

// A PTT AVM model family goes out as one product: every colour card's publication queues a job, and each job's
// request carries the whole family under the parent barcode. The family is sent once. A card whose family already
// went out (PTT AVM accepted the request and gave a tracking id) does not send it again; its job follows that send's
// tracking result, which answers for every variant barcode of the family. A repeat would be refused anyway ("Aynı
// isteği ancak 5 dakika sonra tekrardan gönderebilirsiniz"), and the read before the send cannot see the variants of
// a request PTT AVM has not processed yet.
internal static class PttAvmFamilySend
{
    // A product send is tracked for four hours (MarketplaceJobProcessor.CreateProduct's poll window); an older send is
    // not followed.
    internal static readonly TimeSpan TrackingWindow = TimeSpan.FromHours(4);

    // The send of another job of the family that serves this job: the same parent barcode, every variant barcode of
    // this job's request among the ones it sent (case-insensitive), sent within the tracking window and not refused.
    // The latest such send wins; null when there is none and this job sends the family itself.
    internal static ProductPublicationJobPayload? Covering(string ownPayloadJson, IEnumerable<ProductPublicationJobPayload> sends, DateTimeOffset now)
    {
        var own = PttAvmProductPayloadRules.Read(ownPayloadJson, out _);
        if (own?.Variants is not { Count: > 0 } ownVariants) return null;
        return sends
            .Where(send => string.Equals(send.Phase, "POLL", StringComparison.OrdinalIgnoreCase)
                && send.ExternalOperationId is { Length: > 0 } operation && operation.StartsWith("t:", StringComparison.Ordinal)
                && send.SubmittedAt is { } submitted && submitted <= now && now - submitted <= TrackingWindow
                && Covers(send.PayloadJson, own.Barcode, ownVariants))
            .OrderByDescending(send => send.SubmittedAt)
            .FirstOrDefault();
    }

    private static bool Covers(string sentPayloadJson, string parentBarcode, IReadOnlyList<PttAvmProductPayloadRules.VariantDraft> variants)
    {
        var sent = PttAvmProductPayloadRules.Read(sentPayloadJson, out _);
        if (sent?.Variants is not { Count: > 0 } sentVariants || !string.Equals(sent.Barcode, parentBarcode, StringComparison.OrdinalIgnoreCase)) return false;
        var sentBarcodes = sentVariants.Select(x => x.Barcode).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return variants.All(x => sentBarcodes.Contains(x.Barcode));
    }

    // The family sends of this connection's other product jobs that may still be followed: waiting for their tracking
    // result or done. A refused, blocked or failed send is not followed: this job then sends the family itself.
    internal static async Task<ProductPublicationJobPayload?> CoveringSendAsync(AppDbContext db, Guid tenantId, Guid connectionId, Guid jobId, string ownPayloadJson, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var since = now - TrackingWindow - TimeSpan.FromDays(1);
        var payloads = await db.IntegrationJobs.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.JobType == MarketplaceJobTypes.ProductCreate && x.Id != jobId
                && x.CreatedAt >= since
                && (x.Status == JobStatus.Pending || x.Status == JobStatus.Leased || x.Status == JobStatus.RetryScheduled || x.Status == JobStatus.Succeeded))
            .Select(x => x.PayloadJson)
            .ToListAsync(cancellationToken);
        return Covering(ownPayloadJson, payloads.Select(Read).OfType<ProductPublicationJobPayload>(), now);
    }

    private static ProductPublicationJobPayload? Read(string payloadJson)
    {
        try { return JsonSerializer.Deserialize<ProductPublicationJobPayload>(payloadJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }); }
        catch (JsonException) { return null; }
    }
}
