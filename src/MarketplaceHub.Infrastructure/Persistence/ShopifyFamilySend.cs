using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Adapters.Shopify;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

// A Shopify model family is one product under one handle, and every colour card's publication queues a job carrying
// that whole product (ShopifyProductPublicationComposer). The family is written and published once: a card whose
// family a sibling's job of the same round already sent follows that send, whose result lists every SKU of the family,
// and records its own SKUs from it. Live 2026-10-28 (MZ054YNC, eight cards): each job looked the product up, read the
// locations and the sales channels and published again; the per-connection lock kept them one at a time, but eight
// rounds of the same calls were made for one product.
internal static class ShopifyFamilySend
{
    // A Shopify send is answered at once (shopify-completed:...); it is followed within the PTT AVM tracking window.
    internal static readonly TimeSpan TrackingWindow = PttAvmFamilySend.TrackingWindow;

    internal static bool IsPublishOnlyFailure(string? code) =>
        code is ShopifyHttpClient.PublishRetryCode or ShopifyHttpClient.PublishFailedCode;

    // The send of another job that wrote exactly this job's product: the same payload hash (the hash covers the handle,
    // every variant, price, stock and image of the family), sent within the window and answered by Shopify. The latest
    // such send wins; null when there is none and this job sends the family itself.
    internal static ProductPublicationJobPayload? Covering(ProductPublicationJobPayload own, IEnumerable<ProductPublicationJobPayload> sends, DateTimeOffset now) => sends
        .Where(send => string.Equals(send.Phase, "POLL", StringComparison.OrdinalIgnoreCase)
            && send.ExternalOperationId is { Length: > 0 } operation && operation.StartsWith("shopify-completed:", StringComparison.Ordinal)
            && send.SubmittedAt is { } submitted && submitted <= now && now - submitted <= TrackingWindow
            && string.Equals(send.PayloadHash, own.PayloadHash, StringComparison.OrdinalIgnoreCase))
        .OrderByDescending(send => send.SubmittedAt)
        .FirstOrDefault();

    // The sends of this connection's other product jobs of the same round (a resend is asked for because an earlier
    // round did not do what it should, so it is never answered by that round) that are waiting or done.
    internal static async Task<ProductPublicationJobPayload?> CoveringSendAsync(AppDbContext db, Guid tenantId, Guid connectionId, Guid jobId, DateTimeOffset jobCreatedAt, ProductPublicationJobPayload own, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var since = PttAvmFamilySend.RoundStart(jobCreatedAt);
        var payloads = await db.IntegrationJobs.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.JobType == MarketplaceJobTypes.ProductCreate && x.Id != jobId
                && x.CreatedAt >= since
                && (x.Status == JobStatus.Pending || x.Status == JobStatus.Leased || x.Status == JobStatus.RetryScheduled || x.Status == JobStatus.Succeeded))
            .Select(x => x.PayloadJson)
            .ToListAsync(cancellationToken);
        return Covering(own, payloads.Select(Read).OfType<ProductPublicationJobPayload>(), now);
    }

    private static ProductPublicationJobPayload? Read(string payloadJson)
    {
        try { return JsonSerializer.Deserialize<ProductPublicationJobPayload>(payloadJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }); }
        catch (JsonException) { return null; }
    }
}
