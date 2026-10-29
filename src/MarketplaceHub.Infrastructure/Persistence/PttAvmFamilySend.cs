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

    // The jobs of one publication round (the colour cards of one Yayınla or Yeniden Gönder) are queued within seconds
    // of each other. A job queued more than this before this one belongs to an earlier round: a resend is asked for
    // because that earlier send did not do what it should (live 2026-10-28: MZ059YNC went out with a variant XML PTT
    // AVM ignored), so it must not answer for the resend. PTT AVM refuses an identical repeat within five minutes anyway.
    internal static readonly TimeSpan RoundWindow = TimeSpan.FromMinutes(5);

    // The earliest queue time of a sibling job in this job's round.
    internal static DateTimeOffset RoundStart(DateTimeOffset ownCreatedAt) => ownCreatedAt - RoundWindow;

    // The family sends of this connection's other product jobs of the same round that may still be followed: waiting
    // for their tracking result or done. A refused, blocked or failed send, or one of an earlier round, is not
    // followed: this job then sends the family itself.
    internal static async Task<ProductPublicationJobPayload?> CoveringSendAsync(AppDbContext db, Guid tenantId, Guid connectionId, Guid jobId, DateTimeOffset jobCreatedAt, string ownPayloadJson, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var since = RoundStart(jobCreatedAt);
        var payloads = await db.IntegrationJobs.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.JobType == MarketplaceJobTypes.ProductCreate && x.Id != jobId
                && x.CreatedAt >= since
                && (x.Status == JobStatus.Pending || x.Status == JobStatus.Leased || x.Status == JobStatus.RetryScheduled || x.Status == JobStatus.Succeeded))
            .Select(x => x.PayloadJson)
            .ToListAsync(cancellationToken);
        return Covering(ownPayloadJson, payloads.Select(Read).OfType<ProductPublicationJobPayload>(), now);
    }

    // A colour card the family's send carried while the card had no publication of its own (live 2026-10-29: a bulk
    // publication of MZ053TCD queued one card of eight, its request carried all 24 variants, the other seven cards kept
    // no state). jobs are the family's product jobs on the connection. The family's latest send (the newest job that
    // reached PTT AVM with a tracking id) answers, only when it finished and carried every one of the card's variant
    // barcodes (case-insensitive); otherwise null, and the card is left as it is.
    internal static ProductPublicationJobPayload? Carrying(IReadOnlyCollection<string> cardBarcodes, IEnumerable<(string PayloadJson, bool Succeeded, DateTimeOffset CreatedAt)> jobs)
    {
        if (cardBarcodes.Count == 0 || cardBarcodes.Any(string.IsNullOrWhiteSpace)) return null;
        var latest = jobs
            .Select(job => (Payload: Read(job.PayloadJson), job.Succeeded, job.CreatedAt))
            .Where(job => job.Payload is { } payload && string.Equals(payload.Phase, "POLL", StringComparison.OrdinalIgnoreCase)
                && payload.ExternalOperationId is { Length: > 0 } operation && operation.StartsWith("t:", StringComparison.Ordinal)
                && payload.SubmittedAt is not null && PttAvmProductPayloadRules.IsVariantPayload(payload.PayloadJson))
            .OrderByDescending(job => job.Payload!.SubmittedAt)
            .ThenByDescending(job => job.CreatedAt)
            .FirstOrDefault();
        if (latest.Payload is null || !latest.Succeeded) return null;
        var sent = PttAvmProductPayloadRules.Read(latest.Payload.PayloadJson, out _)?.Variants?.Select(x => x.Barcode.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return sent is not null && cardBarcodes.All(barcode => sent.Contains(barcode.Trim())) ? latest.Payload : null;
    }

    internal static ProductPublicationJobPayload? Read(string payloadJson)
    {
        try { return JsonSerializer.Deserialize<ProductPublicationJobPayload>(payloadJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }); }
        catch (JsonException) { return null; }
    }
}
