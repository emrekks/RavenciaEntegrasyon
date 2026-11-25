using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

// İşlem Takibi's grouped page (JobListQuery.GroupByModel, JobModelGroupPolicy): the product jobs of one model's colour
// cards from one send come as one row. Every job of a key (tenant, connection, job type, correlation id, model) is read,
// whatever its age, and cut into groups (JobModelGroupPolicy.Clusters), so a job is in exactly one group and the groups
// are the same from every page. Limit counts rows, a group being one. A group shows whole where its newest job that
// matches the filters falls, and its row shows that job's time and attempts (its state sums up the whole group). A job
// is left out of the scan only when its id is among the jobs of a group already shown: on this page, or on an earlier
// one (a group with a matching job at or before the cursor), so no job is ever lost and none shown twice. A group's job
// created after the first page was read is shown with its group on the refreshed first page.
//
// SQL per scanned chunk (Limit + 1 jobs, at most ModelGroupScanChunks chunks): the page query as before, on the
// (TenantId, CreatedAt, Id) index; for a chunk with new product jobs, the cards they name (products by id, their
// variants by ProductId), every job of their keys (TenantId = @tenant AND JobType = ANY(@types) AND CorrelationId =
// ANY(@ids) AND ConnectionId = ANY(@connections): an index range on IX_jobs_TenantId_JobType_JobDedupKey over the
// tenant's product jobs, a small share of its jobs) and, from the second page on, which of their ids an earlier page
// held (primary-key lookup with the page's filters). A page without product jobs costs what it cost before.
public sealed partial class JobOperationsService
{
    private const int ModelGroupScanChunks = 8;

    private sealed record ModelGroupCard(Guid ProductId, string Title, string FamilyKey, string? ModelCode, string? OptionSignature, IReadOnlyList<(string Sku, string? Barcode)> Variants);

    private sealed class ModelGroupScan
    {
        public Dictionary<Guid, ModelGroupCard?> Cards { get; } = [];
        public Dictionary<Guid, Guid?> ProductOf { get; } = [];
        public Dictionary<Guid, string?> KeyOf { get; } = [];
        public HashSet<string> LoadedKeys { get; } = new(StringComparer.Ordinal);
        // Each grouped job's group (its jobs oldest first), shared by the group's jobs.
        public Dictionary<Guid, IReadOnlyList<IntegrationJob>> GroupOf { get; } = [];
        // The ids of the jobs of every group shown so far, on this page or an earlier one.
        public HashSet<Guid> Shown { get; } = [];
    }

    private async Task<PageResult<JobSummaryView>> ModelGroupedPageAsync(Guid tenantId, IQueryable<IntegrationJob> filtered, (DateTimeOffset CreatedAt, Guid Id)? cursor, int limit, CancellationToken cancellationToken)
    {
        var scan = new ModelGroupScan();
        var rows = new List<(IntegrationJob Anchor, IReadOnlyList<IntegrationJob>? Group)>();
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
                // Only a job already shown inside a group's row is passed over.
                if (scan.Shown.Contains(job.Id)) { position = (job.CreatedAt, job.Id); continue; }
                if (rows.Count == limit) { full = true; break; }
                var group = scan.GroupOf.GetValueOrDefault(job.Id) is { Count: > 1 } members ? members : null;
                rows.Add((job, group));
                foreach (var member in group ?? [job]) scan.Shown.Add(member.Id);
                position = (job.CreatedAt, job.Id);
            }
            if (full) { hasMore = true; break; }
            if (jobs.Count <= limit) break;
        }

        var shown = new List<IntegrationJob>();
        var seen = new HashSet<Guid>();
        foreach (var (anchor, group) in rows)
            foreach (var job in group ?? [anchor])
                if (seen.Add(job.Id)) shown.Add(job);
        var summaries = (await SummariesAsync(tenantId, shown, cancellationToken)).ToDictionary(summary => summary.Id);
        var items = rows.Select(row => row.Group is { } group ? GroupRow(row.Anchor, group) : summaries[row.Anchor.Id]).ToList();
        var nextCursor = hasMore && position is { } last ? JobListCursor.Encode(last.CreatedAt, last.Id) : null;
        return new(items, nextCursor, nextCursor is not null);

        // The row is the job it is sorted by (the newest that matches the filters): its time and attempts; the cards come
        // oldest first, as the send queued them.
        JobSummaryView GroupRow(IntegrationJob anchor, IReadOnlyList<IntegrationJob> group)
        {
            var cards = group.Select(job =>
            {
                var card = scan.Cards[scan.ProductOf[job.Id]!.Value]!;
                return new JobModelGroupCardView(summaries[job.Id], card.ProductId, card.Title, card.OptionSignature, JobModelGroupPolicy.CardVariantCount(job.JobType, job.PayloadJson, card.Variants));
            }).ToList();
            var newestCard = scan.Cards[scan.ProductOf[group[^1].Id]!.Value]!;
            // A card with two jobs in one send counts once, with its newest job's count.
            var variantCount = JobModelGroupPolicy.GroupVariantCount(cards.GroupBy(card => card.ProductId).Select(card => card.Last().VariantCount));
            var key = $"{scan.KeyOf[anchor.Id]}@{group[0].Id:N}";
            var view = new JobModelGroupView(key, newestCard.ModelCode, newestCard.Title, cards.Select(card => card.ProductId).Distinct().Count(), variantCount, cards);
            return summaries[anchor.Id] with { ModelGroup = view };
        }
    }

    // Gives every new job of the chunk its key, reads every job of each new key and cuts them into groups, and marks as
    // shown the jobs of the groups an earlier page showed.
    private async Task ResolveModelGroupsAsync(Guid tenantId, IQueryable<IntegrationJob> filtered, (DateTimeOffset CreatedAt, Guid Id)? cursor, List<IntegrationJob> jobs, ModelGroupScan scan, CancellationToken cancellationToken)
    {
        var fresh = jobs.Where(job => !scan.KeyOf.ContainsKey(job.Id)).ToList();
        await LoadModelGroupCardsAsync(tenantId, fresh, scan, cancellationToken);
        foreach (var job in fresh) scan.KeyOf[job.Id] = ModelGroupKey(job, scan);
        // A job whose key another chunk's read already met is keyed but maybe not grouped: every key not cut into groups yet is read.
        var newKeys = jobs.Where(job => scan.KeyOf[job.Id] is { } key && !scan.LoadedKeys.Contains(key)).ToList();
        if (newKeys.Count == 0) return;
        var correlations = newKeys.Select(job => job.CorrelationId).Distinct(StringComparer.Ordinal).ToArray();
        var types = newKeys.Select(job => job.JobType).Distinct(StringComparer.Ordinal).ToArray();
        var connections = newKeys.Select(job => job.ConnectionId!.Value).Distinct().ToArray();
        var related = await db.IntegrationJobs.AsNoTracking()
            .Where(x => x.TenantId == tenantId && types.Contains(x.JobType) && correlations.Contains(x.CorrelationId) && x.ConnectionId != null && connections.Contains(x.ConnectionId.Value))
            .ToListAsync(cancellationToken);
        var unknown = related.Where(job => !scan.KeyOf.ContainsKey(job.Id)).ToList();
        await LoadModelGroupCardsAsync(tenantId, unknown, scan, cancellationToken);
        foreach (var job in unknown) scan.KeyOf[job.Id] = ModelGroupKey(job, scan);
        // Every job of each key met in this read was read (its type, connection and correlation id are among those asked
        // for), so each of them is cut into groups now, whole; a key already cut stays as it was.
        var groups = new List<IReadOnlyList<IntegrationJob>>();
        foreach (var key in related.Where(job => scan.KeyOf[job.Id] is { } key && !scan.LoadedKeys.Contains(key)).GroupBy(job => scan.KeyOf[job.Id]!, StringComparer.Ordinal))
        {
            scan.LoadedKeys.Add(key.Key);
            foreach (var group in JobModelGroupPolicy.Clusters(key, job => job.CreatedAt, job => job.Id))
            {
                groups.Add(group);
                foreach (var job in group) scan.GroupOf[job.Id] = group;
            }
        }
        if (cursor is not { } at) return;
        var ids = groups.Where(group => group.Count > 1).SelectMany(group => group).Select(job => job.Id).ToArray();
        if (ids.Length == 0) return;
        var earlier = await UpToPosition(filtered, at.CreatedAt, at.Id).Where(job => ids.Contains(job.Id)).Select(job => job.Id).ToListAsync(cancellationToken);
        foreach (var id in earlier)
            foreach (var job in scan.GroupOf[id]) scan.Shown.Add(job.Id);
    }

    private static string? ModelGroupKey(IntegrationJob job, ModelGroupScan scan) =>
        job.ConnectionId is { } connectionId && JobModelGroupPolicy.NamesASend(job.CorrelationId)
        && scan.ProductOf.GetValueOrDefault(job.Id) is { } productId && scan.Cards.GetValueOrDefault(productId) is { } card
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
