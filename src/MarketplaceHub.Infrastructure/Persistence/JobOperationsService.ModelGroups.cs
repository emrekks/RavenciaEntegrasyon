using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

// İşlem Takibi's grouped page (JobListQuery.GroupByModel, JobModelGroupPolicy): the product jobs of one model's colour
// cards from one send come as one row. Limit counts rows; a group is one row and comes whole on the page where its
// newest matching job falls, so it is never split: a later page leaves out a group an earlier page showed (one of its
// matching jobs lies before the cursor). The filters choose the jobs; a group shows when any of its jobs matches and
// then carries all of them, so its summary hides no failure.
//
// SQL per scanned chunk (Limit + 1 jobs, at most ModelGroupScanChunks chunks): the page query as before, on the
// (TenantId, CreatedAt, Id) index; one read of the cards the product jobs name (products by id and their variants by
// ProductId); one sibling read per chunk that has product jobs, an index range per send on the same index:
//   SELECT j.* FROM integration.jobs j
//   JOIN unnest(@correlations, @froms, @tos) AS w(c, f, t) ON j."CorrelationId" = w.c AND j."CreatedAt" BETWEEN w.f AND w.t
//   WHERE j."TenantId" = @tenant AND j."JobType" = ANY(@types) AND j."ConnectionId" = ANY(@connections)
// and, from the second page on, the siblings that an earlier page held (primary-key lookup with the page's filters).
// A page without product jobs costs what it cost before.
public sealed partial class JobOperationsService
{
    private const int ModelGroupScanChunks = 8;

    private sealed record ModelGroupCard(Guid ProductId, string Title, string FamilyKey, string? ModelCode, string? OptionSignature, IReadOnlyList<(string Sku, string? Barcode)> Variants);

    private sealed class ModelGroupScan
    {
        public Dictionary<Guid, ModelGroupCard?> Cards { get; } = [];
        public Dictionary<Guid, Guid?> ProductOf { get; } = [];
        public Dictionary<Guid, string?> KeyOf { get; } = [];
        public Dictionary<string, List<IntegrationJob>> Members { get; } = new(StringComparer.Ordinal);
        public HashSet<string> ShownEarlier { get; } = new(StringComparer.Ordinal);
    }

    private async Task<PageResult<JobSummaryView>> ModelGroupedPageAsync(Guid tenantId, IQueryable<IntegrationJob> filtered, (DateTimeOffset CreatedAt, Guid Id)? cursor, int limit, CancellationToken cancellationToken)
    {
        var scan = new ModelGroupScan();
        var rows = new List<(IntegrationJob Anchor, string? Key)>();
        var onPage = new HashSet<string>(StringComparer.Ordinal);
        var position = cursor;
        var hasMore = false;
        for (var chunk = 0; ; chunk++)
        {
            // The previous chunk was full, so more jobs follow: the page ends shorter and the next one goes on from here.
            if (chunk == ModelGroupScanChunks) { hasMore = true; break; }
            var query = position is { } at ? AfterPosition(filtered, at.CreatedAt, at.Id) : filtered;
            var jobs = await query.OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id).Take(limit + 1).ToListAsync(cancellationToken);
            await ResolveModelGroupsAsync(tenantId, filtered, cursor, jobs, scan, cancellationToken);
            var full = false;
            foreach (var job in jobs)
            {
                var key = scan.KeyOf[job.Id];
                // A job of a group shown here or on an earlier page is in that group's row already.
                if (key is not null && (scan.ShownEarlier.Contains(key) || onPage.Contains(key))) { position = (job.CreatedAt, job.Id); continue; }
                if (rows.Count == limit) { full = true; break; }
                rows.Add((job, key));
                if (key is not null) onPage.Add(key);
                position = (job.CreatedAt, job.Id);
            }
            if (full) { hasMore = true; break; }
            if (jobs.Count <= limit) break;
        }

        var shown = new List<IntegrationJob>();
        var seen = new HashSet<Guid>();
        foreach (var (anchor, key) in rows)
            foreach (var job in Grouped(key) ?? [anchor])
                if (seen.Add(job.Id)) shown.Add(job);
        var summaries = (await SummariesAsync(tenantId, shown, cancellationToken)).ToDictionary(summary => summary.Id);
        var items = rows.Select(row => Grouped(row.Key) is { } group ? GroupRow(row.Key!, group) : summaries[row.Anchor.Id]).ToList();
        var nextCursor = hasMore && position is { } last ? JobListCursor.Encode(last.CreatedAt, last.Id) : null;
        return new(items, nextCursor, nextCursor is not null);

        List<IntegrationJob>? Grouped(string? key) => key is not null && scan.Members.TryGetValue(key, out var group) && group.Count > 1 ? group : null;

        JobSummaryView GroupRow(string key, List<IntegrationJob> group)
        {
            // Oldest first, as the send queued the cards; the row itself is the newest job's.
            var ordered = group.OrderBy(job => job.CreatedAt).ThenBy(job => job.Id).ToList();
            var cards = ordered.Select(job =>
            {
                var card = scan.Cards[scan.ProductOf[job.Id]!.Value]!;
                return new JobModelGroupCardView(summaries[job.Id], card.ProductId, card.Title, card.OptionSignature, JobModelGroupPolicy.CardVariantCount(job.JobType, job.PayloadJson, card.Variants));
            }).ToList();
            var newest = ordered[^1];
            var newestCard = scan.Cards[scan.ProductOf[newest.Id]!.Value]!;
            // A card with two jobs in one send counts once, with its newest job's count.
            var variantCount = JobModelGroupPolicy.GroupVariantCount(cards.GroupBy(card => card.ProductId).Select(card => card.Last().VariantCount));
            var view = new JobModelGroupView(key, newestCard.ModelCode, newestCard.Title, cards.Select(card => card.ProductId).Distinct().Count(), variantCount, cards);
            return summaries[newest.Id] with { ModelGroup = view };
        }
    }

    // Gives every new job of the chunk its group key and reads each new group's jobs whole (whatever the filters), or
    // marks it as shown on an earlier page.
    private async Task ResolveModelGroupsAsync(Guid tenantId, IQueryable<IntegrationJob> filtered, (DateTimeOffset CreatedAt, Guid Id)? cursor, List<IntegrationJob> jobs, ModelGroupScan scan, CancellationToken cancellationToken)
    {
        var fresh = jobs.Where(job => !scan.KeyOf.ContainsKey(job.Id)).ToList();
        await LoadModelGroupCardsAsync(tenantId, fresh, scan, cancellationToken);
        foreach (var job in fresh) scan.KeyOf[job.Id] = ModelGroupKey(job, scan);
        var anchors = fresh.Where(job => scan.KeyOf[job.Id] is { } key && !scan.Members.ContainsKey(key) && !scan.ShownEarlier.Contains(key)).ToList();
        if (anchors.Count == 0) return;
        var wanted = anchors.Select(job => scan.KeyOf[job.Id]!).ToHashSet(StringComparer.Ordinal);
        var windows = anchors.GroupBy(job => job.CorrelationId, StringComparer.Ordinal)
            .Select(send => (Correlation: send.Key, From: send.Min(job => job.CreatedAt) - JobModelGroupPolicy.SiblingWindow, To: send.Max(job => job.CreatedAt) + JobModelGroupPolicy.SiblingWindow))
            .ToList();
        var correlations = windows.Select(window => window.Correlation).ToArray();
        var froms = windows.Select(window => window.From.ToUniversalTime()).ToArray();
        var tos = windows.Select(window => window.To.ToUniversalTime()).ToArray();
        var types = anchors.Select(job => job.JobType).Distinct(StringComparer.Ordinal).ToArray();
        var connections = anchors.Select(job => job.ConnectionId!.Value).Distinct().ToArray();
        var siblings = await db.IntegrationJobs.FromSqlInterpolated($"""
            SELECT j.* FROM integration.jobs j
            JOIN unnest({correlations}::text[], {froms}::timestamptz[], {tos}::timestamptz[]) AS w(c, f, t)
              ON j."CorrelationId" = w.c AND j."CreatedAt" BETWEEN w.f AND w.t
            WHERE j."TenantId" = {tenantId} AND j."JobType" = ANY({types}) AND j."ConnectionId" = ANY({connections})
            """).AsNoTracking().ToListAsync(cancellationToken);
        var unknown = siblings.Where(job => !scan.KeyOf.ContainsKey(job.Id)).ToList();
        await LoadModelGroupCardsAsync(tenantId, unknown, scan, cancellationToken);
        foreach (var job in unknown) scan.KeyOf[job.Id] = ModelGroupKey(job, scan);
        foreach (var job in siblings)
            if (scan.KeyOf[job.Id] is { } key && wanted.Contains(key))
                (scan.Members.TryGetValue(key, out var list) ? list : scan.Members[key] = []).Add(job);
        if (cursor is not { } at) return;
        var siblingIds = siblings.Select(job => job.Id).ToArray();
        var earlier = await UpToPosition(filtered, at.CreatedAt, at.Id).Where(job => siblingIds.Contains(job.Id)).Select(job => job.Id).ToListAsync(cancellationToken);
        foreach (var id in earlier)
            if (scan.KeyOf[id] is { } key && wanted.Contains(key) && scan.Members.Remove(key)) scan.ShownEarlier.Add(key);
    }

    private static string? ModelGroupKey(IntegrationJob job, ModelGroupScan scan) =>
        job.ConnectionId is { } connectionId && scan.ProductOf.GetValueOrDefault(job.Id) is { } productId && scan.Cards.GetValueOrDefault(productId) is { } card
            ? JobModelGroupPolicy.Key(connectionId, job.JobType, job.CorrelationId, card.FamilyKey)
            : null;

    // The cards the product jobs name: title, model (brand + the first coded variant's model code, as the catalog's
    // family rule reads it) and variants. A card the catalog no longer holds leaves its job on its own row.
    private async Task LoadModelGroupCardsAsync(Guid tenantId, IReadOnlyList<IntegrationJob> jobs, ModelGroupScan scan, CancellationToken cancellationToken)
    {
        foreach (var job in jobs)
            if (!scan.ProductOf.ContainsKey(job.Id))
                scan.ProductOf[job.Id] = JobModelGroupPolicy.IsGrouped(job.JobType) && job.ConnectionId is not null ? JobModelGroupPolicy.ProductId(job.PayloadJson) : null;
        var productIds = jobs.Select(job => scan.ProductOf[job.Id]).OfType<Guid>().Where(id => !scan.Cards.ContainsKey(id)).Distinct().ToArray();
        if (productIds.Length == 0) return;
        var products = await EffectiveCatalog.Products(db).Where(x => x.TenantId == tenantId && productIds.Contains(x.Id))
            .Select(x => new { x.Id, x.Title, x.BrandId }).ToListAsync(cancellationToken);
        var variants = await db.ProductVariants.AsNoTracking().Where(x => x.TenantId == tenantId && productIds.Contains(x.ProductId))
            .OrderBy(x => x.SortOrder).ThenBy(x => x.Id)
            .Select(x => new { x.ProductId, x.ModelCode, x.OptionSignature, x.Sku, x.Barcode }).ToListAsync(cancellationToken);
        foreach (var id in productIds) scan.Cards[id] = null;
        foreach (var product in products)
        {
            var cardVariants = variants.Where(x => x.ProductId == product.Id).ToList();
            var modelCode = cardVariants.Select(x => x.ModelCode?.Trim()).FirstOrDefault(code => !string.IsNullOrEmpty(code));
            scan.Cards[product.Id] = new(product.Id, product.Title, ProductFamilyKeyPolicy.FamilyKey(product.Id, product.BrandId, modelCode), modelCode,
                cardVariants.Select(x => x.OptionSignature).FirstOrDefault(), [.. cardVariants.Select(x => (x.Sku, x.Barcode))]);
        }
    }
}
