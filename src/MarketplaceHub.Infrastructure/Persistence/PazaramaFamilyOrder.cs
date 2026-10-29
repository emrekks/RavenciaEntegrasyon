using System.Text.Json;
using System.Text.Json.Nodes;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

// Pazarama merges the colour cards of a model family (one groupCode) into one listing, and the listing's colours follow
// the order in which Pazarama created them. Every colour card is its own product create job, so the cards went out in
// whatever order their jobs happened to run (live 2026-10-29, MZ035YNC: eight cards queued in two seconds, created
// between 17:29 and 17:44 in an order the panel never shows). The cards of one publication round now go out one by
// one in the panel's colour order (ProductFamilyColorOrder): a card's create is sent only after every earlier card of
// the round has its answer (its job is no longer waiting, sending or polling). An earlier card that was refused,
// stopped or failed does not hold the next one back.
//
// One family request (as PTT AVM and Shopify send) is not used for Pazarama: Pazarama has no service to edit a listing,
// so a family request would create again the colours a resend leaves alone; each card's mappings, refusals, retries
// and stop button stay its own; and the order of products within one Pazarama batch is not documented, while a create
// sent after the previous one finished is created after it.
internal static class PazaramaFamilyOrder
{
    internal const string WaitCode = "PRODUCT_FAMILY_ORDER_WAIT";

    // The jobs of one Yayınla are queued within seconds; a sibling queued this long before or after belongs to another
    // round and does not hold this card.
    internal static readonly TimeSpan RoundWindow = PttAvmFamilySend.RoundWindow;

    // A card's job may start before the editor or a bulk publication has queued its siblings; a young card of a family
    // waits this long so it can see the whole round.
    internal static readonly TimeSpan SettleWindow = TimeSpan.FromSeconds(10);

    internal static readonly TimeSpan WaitDelay = TimeSpan.FromSeconds(15);

    internal sealed record RoundJob(Guid JobId, DateTimeOffset CreatedAt, ProductFamilyColorOrder.Card Card);

    // The groupCode a Pazarama create body carries (every product of a card shares it); null when there is none.
    internal static string? GroupCode(string payloadJson)
    {
        try
        {
            if (JsonNode.Parse(payloadJson) is not JsonObject root || root["products"] is not JsonArray products) return null;
            foreach (var product in products)
                if (product is JsonObject item && item["groupCode"] is JsonValue value && value.TryGetValue<string>(out var code) && !string.IsNullOrWhiteSpace(code))
                    return code.Trim();
            return null;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException) { return null; }
    }

    // The nearest earlier card of the round this job must wait for, or null when it may send now. Cards come in the
    // panel's colour order; two jobs of the same card keep their queue order.
    internal static RoundJob? Blocking(RoundJob own, IEnumerable<RoundJob> unfinishedSiblings) => unfinishedSiblings
        .Where(sibling => sibling.JobId != own.JobId && Precedes(sibling, own))
        .OrderByDescending(sibling => sibling, Comparer<RoundJob>.Create(CompareJobs))
        .FirstOrDefault();

    private static bool Precedes(RoundJob left, RoundJob right) => CompareJobs(left, right) < 0;

    private static int CompareJobs(RoundJob left, RoundJob right)
    {
        var byCard = left.Card.ProductId == right.Card.ProductId ? 0 : ProductFamilyColorOrder.Compare(left.Card, right.Card);
        if (byCard != 0) return byCard;
        var byQueue = left.CreatedAt.CompareTo(right.CreatedAt);
        return byQueue != 0 ? byQueue : left.JobId.CompareTo(right.JobId);
    }

    // Deferred (the attempt is given back) while an earlier card of the round is unanswered or the round may still be
    // queueing; null when this card may send its create now.
    internal static async Task<JobExecutionResult?> WaitAsync(AppDbContext db, Guid tenantId, Guid connectionId, IntegrationJob job, ProductPublicationJobPayload payload, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (GroupCode(payload.PayloadJson) is not { } groupCode) return null;
        var since = job.CreatedAt - RoundWindow;
        var until = job.CreatedAt + RoundWindow;
        var rows = await db.IntegrationJobs.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.JobType == MarketplaceJobTypes.ProductCreate && x.Id != job.Id
                && x.CreatedAt >= since && x.CreatedAt <= until
                && (x.Status == JobStatus.Pending || x.Status == JobStatus.Leased || x.Status == JobStatus.RetryScheduled))
            .Select(x => new { x.Id, x.CreatedAt, x.PayloadJson })
            .ToListAsync(cancellationToken);
        var siblings = rows
            .Select(row => (row.Id, row.CreatedAt, Payload: Read(row.PayloadJson)))
            .Where(row => row.Payload is { ProductId: var productId } sibling && productId != Guid.Empty && string.Equals(GroupCode(sibling.PayloadJson), groupCode, StringComparison.Ordinal))
            .ToList();

        var age = now - job.CreatedAt;
        if (age < SettleWindow && (siblings.Count > 0 || await HasFamilySiblingsAsync(db, tenantId, payload.ProductId, cancellationToken)))
            return JobExecutionResult.Deferred(WaitCode, "Model ailesinin diğer renk kartlarının kuyruğa alınması bekleniyor; renkler Pazarama'ya ürünler sayfasındaki sırayla gönderilecek.", SettleWindow - age);
        if (siblings.Count == 0) return null;

        var productIds = siblings.Select(x => x.Payload!.ProductId).Append(payload.ProductId).Distinct().ToArray();
        var variants = await db.ProductVariants.AsNoTracking()
            .Where(x => x.TenantId == tenantId && productIds.Contains(x.ProductId))
            .Select(x => new { x.ProductId, x.OptionSignature, x.SortOrder })
            .ToListAsync(cancellationToken);
        var cards = productIds.ToDictionary(id => id, id =>
            ProductFamilyColorOrder.ForCard(id, variants.Where(x => x.ProductId == id).Select(x => new ProductFamilyColorOrder.VariantRow(x.OptionSignature, x.SortOrder))));
        var own = new RoundJob(job.Id, job.CreatedAt, cards[payload.ProductId]);
        if (Blocking(own, siblings.Select(x => new RoundJob(x.Id, x.CreatedAt, cards[x.Payload!.ProductId]))) is not { } earlier) return null;
        var colour = earlier.Card.Color is { } color ? $"{color} rengi" : "önceki renk kartı";
        return JobExecutionResult.Deferred(WaitCode, $"Renkler Pazarama'ya ürünler sayfasındaki sırayla gönderiliyor; önce {colour} için Pazarama'nın ürün açma sonucu bekleniyor.", WaitDelay);
    }

    // Another card that shares this card's model code (only asked while the job is young).
    private static async Task<bool> HasFamilySiblingsAsync(AppDbContext db, Guid tenantId, Guid productId, CancellationToken cancellationToken)
    {
        var modelCode = await db.ProductVariants.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ProductId == productId && x.ModelCode != null)
            .OrderBy(x => x.SortOrder).ThenBy(x => x.Id)
            .Select(x => x.ModelCode)
            .FirstOrDefaultAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(modelCode)) return false;
        return await db.ProductVariants.AsNoTracking()
            .AnyAsync(x => x.TenantId == tenantId && x.ProductId != productId && x.ModelCode == modelCode, cancellationToken);
    }

    private static ProductPublicationJobPayload? Read(string payloadJson)
    {
        try { return JsonSerializer.Deserialize<ProductPublicationJobPayload>(payloadJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }); }
        catch (JsonException) { return null; }
    }
}
