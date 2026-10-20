using System.Text.Json;
using System.Text.RegularExpressions;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace MarketplaceHub.Infrastructure.Persistence;

// Reads the products already listed on a marketplace and brings their category,
// option and attribute structure into the panel. It never writes to the
// marketplace: the only remote calls are product and reference reads.
public sealed partial class MarketplaceJobProcessor
{
    private static readonly JsonSerializerOptions StructureReportJson = new(JsonSerializerDefaults.Web);
    private static readonly Regex PlatformSuffix = new(@"\s\([^()]*\)$", RegexOptions.Compiled);

    private async Task<bool> ImportCatalogStructure(Guid tenantId, Guid connectionId, string payloadJson, string correlationId, Guid? jobId, CancellationToken cancellationToken)
    {
        var connection = await db.PlatformConnections.AsNoTracking()
            .SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == connectionId && (x.PlatformCode == "TRENDYOL" || x.PlatformCode == "HEPSIBURADA"), cancellationToken);
        if (connection is null || connection.Environment is not ("STAGE" or "PRODUCTION") || connection.Status is not ("ACTIVE" or "VERIFIED")) return false;
        if (!Guid.TryParse(ReadText(payloadJson, "runId"), out var runId)) return false;
        var run = await db.CatalogStructureRuns.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == runId && x.ConnectionId == connectionId, cancellationToken);
        if (run is null) return false;
        if (run.Status == "COMPLETED") return true;

        var apply = string.Equals(run.Mode, "APPLY", StringComparison.Ordinal);
        run.Status = "RUNNING";
        run.JobId = jobId;
        run.ErrorSummary = null;
        run.Version++;
        await db.SaveChangesAsync(cancellationToken);

        try
        {
            var report = await RunCatalogStructure(tenantId, connection, apply, correlationId, jobId, cancellationToken);
            var finished = await db.CatalogStructureRuns.SingleAsync(x => x.TenantId == tenantId && x.Id == runId, cancellationToken);
            finished.Status = "COMPLETED";
            finished.ReportJson = JsonSerializer.Serialize(report, StructureReportJson);
            finished.CompletedAt = timeProvider.GetUtcNow();
            finished.Version++;
            await db.SaveChangesAsync(cancellationToken);
            if (jobId is { } doneJob)
                await UpdateProductSyncProgressAsync(tenantId, doneJob, report.Totals.Products, report.Totals.Products, 100, apply ? "Yapı panele aktarıldı" : "Önizleme hazır", cancellationToken);
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            db.ChangeTracker.Clear();
            var failed = await db.CatalogStructureRuns.SingleAsync(x => x.TenantId == tenantId && x.Id == runId, CancellationToken.None);
            failed.Status = "FAILED";
            failed.ErrorSummary = Short(exception.GetBaseException().Message, 1000);
            failed.Version++;
            await db.SaveChangesAsync(CancellationToken.None);
            throw;
        }
    }

    private async Task<StructureReport> RunCatalogStructure(Guid tenantId, PlatformConnection connection, bool apply, string correlationId, Guid? jobId, CancellationToken cancellationToken)
    {
        var connectionId = connection.Id;
        var isHepsiburada = connection.PlatformCode == "HEPSIBURADA";
        var platformLabel = isHepsiburada ? "Hepsiburada" : "Trendyol";
        var report = new StructureReport { Mode = apply ? "APPLY" : "PREVIEW", Platform = connection.PlatformCode, GeneratedAt = timeProvider.GetUtcNow() };

        // 1. Read the listed products and aggregate what each category uses.
        var usage = new Dictionary<string, StructureUsage>(StringComparer.Ordinal);
        string? cursor = null;
        var pageNumber = 0;
        var received = 0;
        string[]? sampleKeys = null;
        while (true)
        {
            pageNumber++;
            TrackRequest();
            var result = await products.ListCatalogAsync(
                Context(tenantId, connectionId, correlationId, $"structure-scan:{cursor ?? "0"}"),
                new(cursor, isHepsiburada ? 500 : 100),
                new ProductReadFilter(null, CatalogStructure: true),
                cancellationToken);
            if (!result.IsSuccess) { TrackResultFailure(result.Error); throw JobProcessingException.FromAdapter(result.Error!); }
            foreach (var item in result.Value!.Items)
            {
                received++;
                if (sampleKeys is null) sampleKeys = TopLevelKeys(item.RawJson);
                if (item.IsDraft || item.IsPendingApproval) { report.Totals.ProductsSkipped++; continue; }
                var variants = item.Variants.Where(variant => !variant.Archived).ToList();
                if (variants.Count == 0) { report.Totals.ProductsSkipped++; continue; }
                report.Totals.Products++;
                var categoryId = Short(item.CategoryExternalId, 256);
                if (string.IsNullOrWhiteSpace(categoryId)) { report.Totals.ProductsWithoutCategory++; continue; }
                if (!usage.TryGetValue(categoryId, out var entry)) usage[categoryId] = entry = new StructureUsage(categoryId, item.CategoryName);
                entry.ProductCount++;
                foreach (var option in variants.SelectMany(variant => variant.Options))
                {
                    if (string.IsNullOrWhiteSpace(option.Key) || string.IsNullOrWhiteSpace(option.Value)) continue;
                    if (!entry.Options.TryGetValue(option.Key.Trim(), out var values)) entry.Options[option.Key.Trim()] = values = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    values.Add(option.Value.Trim());
                }
            }
            if (jobId is { } scanJob)
                await UpdateProductSyncProgressAsync(tenantId, scanJob, received, result.Value.TotalCount, null, $"Ürünler okunuyor · {received:N0}", cancellationToken);
            if (!result.Value.HasMore || string.IsNullOrWhiteSpace(result.Value.NextCursor) || cancellationToken.IsCancellationRequested) break;
            cursor = result.Value.NextCursor;
        }
        cancellationToken.ThrowIfCancellationRequested();
        report.ResponseFields = (sampleKeys ?? []).ToList();
        if (report.Totals.Products > 0 && usage.Count == 0)
            report.Warnings.Add($"{platformLabel} ürün yanıtında kategori bilgisi bulunamadı. Yanıttaki alanlar: {string.Join(", ", sampleKeys ?? [])}");

        // 2. Warm the marketplace reference cache outside the transaction, so a
        // preview that rolls back does not throw the fetched references away.
        var categorySnapshot = await EnsureReferenceSnapshot(tenantId, connectionId, "CATEGORIES", null, correlationId, cancellationToken)
            ?? throw new InvalidOperationException("Pazar yeri kategori referansı alınamadı.");
        var categoryItems = await db.ReferenceItems.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.SnapshotId == categorySnapshot.Id && x.ResourceType == "CATEGORIES" && x.IsActive)
            .ToListAsync(cancellationToken);
        var ordered = usage.Values.OrderByDescending(x => x.ProductCount).ThenBy(x => x.CategoryId, StringComparer.Ordinal).ToList();
        for (var index = 0; index < ordered.Count; index++)
        {
            var entry = ordered[index];
            if (jobId is { } prepareJob)
                await UpdateProductSyncProgressAsync(tenantId, prepareJob, index, ordered.Count, Math.Clamp(index * 50 / Math.Max(1, ordered.Count), 0, 49), $"Referanslar hazırlanıyor · {index + 1}/{ordered.Count} kategori", cancellationToken);
            var categoryItem = categoryItems.FirstOrDefault(x => string.Equals(x.ExternalId, entry.CategoryId, StringComparison.Ordinal));
            if (categoryItem is null) continue;
            var attributeSnapshot = await EnsureReferenceSnapshot(tenantId, connectionId, "CATEGORY_ATTRIBUTES", entry.CategoryId, correlationId, cancellationToken);
            if (attributeSnapshot is null) continue;
            var remoteAttributes = await LoadReferenceItems(tenantId, attributeSnapshot, "CATEGORY_ATTRIBUTES", cancellationToken);
            foreach (var candidate in StructureCandidateAttributes(remoteAttributes, entry))
                await EnsureReferenceSnapshot(tenantId, connectionId, "ATTRIBUTE_VALUES", $"{entry.CategoryId}/{candidate.ExternalId}", correlationId, cancellationToken);
        }

        // 3. Plan or apply inside one transaction: a preview rolls it back, an
        // apply commits it, so a failed apply leaves the panel untouched.
        var changes = new List<StructureChange>();
        void CollectChanges(object? sender, SavingChangesEventArgs args)
        {
            foreach (var entry in db.ChangeTracker.Entries().Where(x => x.State == EntityState.Added))
                changes.Add(new(entry.Entity switch
                {
                    Category => "category",
                    AttributeDefinition => "attribute",
                    AttributeValue => "value",
                    CategoryAttributeRequirement => "requirement",
                    CategoryMapping => "categoryMapping",
                    AttributeMapping => "attributeMapping",
                    AttributeValueMapping => "valueMapping",
                    _ => ""
                }, entry.Entity is Category category ? category.Name : entry.Entity is AttributeDefinition definition ? definition.Name : entry.Entity is AttributeValue value ? value.Value : "", entry.Entity is AttributeValue attributeValue ? attributeValue.AttributeId : null));
        }

        // Progress writes enlist in the transaction and would be rolled back with a
        // preview, so the label is set once before it starts.
        if (jobId is { } workJob)
            await UpdateProductSyncProgressAsync(tenantId, workJob, ordered.Count, ordered.Count, 50, apply ? "Yapı panele uygulanıyor" : "Önizleme hazırlanıyor", cancellationToken);
        db.ChangeTracker.Clear();
        db.SavingChanges += CollectChanges;
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var importedAttributeLibrary = (await db.AttributeDefinitions
                    .Where(x => x.TenantId == tenantId && x.IsActive)
                    .OrderBy(x => x.CreatedAt).ThenBy(x => x.Id)
                    .ToListAsync(cancellationToken))
                .Where(x => !string.IsNullOrWhiteSpace(NormalizeCatalogKey(x.Name, 160)))
                .GroupBy(x => NormalizeCatalogKey(x.Name, 160), StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
            var cache = new Dictionary<string, CategoryAttributeContext>(StringComparer.Ordinal);

            for (var index = 0; index < ordered.Count; index++)
            {
                var entry = ordered[index];
                var categoryReport = new StructureCategoryReport { ExternalId = entry.CategoryId, RemoteName = entry.Name ?? "", ProductCount = entry.ProductCount };
                report.Categories.Add(categoryReport);
                var categoryItem = categoryItems.FirstOrDefault(x => string.Equals(x.ExternalId, entry.CategoryId, StringComparison.Ordinal));
                if (categoryItem is null)
                {
                    categoryReport.Decision = "NeedsReview";
                    categoryReport.Reason = "Kategori pazar yeri referansında bulunamadı.";
                    report.Review.Add(new("category", categoryReport.RemoteName, categoryReport.Reason));
                    continue;
                }
                categoryReport.RemotePath = categoryItem.Path ?? "";
                var attributeSnapshot = await EnsureReferenceSnapshot(tenantId, connectionId, "CATEGORY_ATTRIBUTES", entry.CategoryId, correlationId, cancellationToken);
                if (attributeSnapshot is null)
                {
                    categoryReport.Decision = "NeedsReview";
                    categoryReport.Reason = "Kategori özellikleri pazar yerinden alınamadı.";
                    report.Review.Add(new("category", categoryReport.RemoteName, categoryReport.Reason));
                    continue;
                }

                var mark = changes.Count;
                var outcome = await EnsureStructureCategory(tenantId, connection, platformLabel, categorySnapshot, categoryItem, attributeSnapshot, entry, importedAttributeLibrary, cache, correlationId, cancellationToken);
                await db.SaveChangesAsync(cancellationToken);
                var made = changes.Skip(mark).ToList();

                categoryReport.Decision = outcome.AlreadyMapped ? "AlreadyMapped" : outcome.Decision.Decision.ToString();
                categoryReport.PanelName = outcome.Context?.LocalCategory.Name ?? outcome.Decision.PanelName;
                categoryReport.Reason = outcome.AlreadyMapped ? "Kategori daha önce eşlenmiş." : outcome.Decision.Reason;
                categoryReport.CreatedCategory = made.Any(x => x.Kind == "category");
                foreach (var attribute in outcome.Attributes)
                    categoryReport.Attributes.Add(new()
                    {
                        RemoteName = attribute.Remote.Name,
                        PanelName = attribute.Definition.Name,
                        Role = attribute.Role,
                        CreatedDefinition = made.Any(x => x.Kind == "attribute" && x.Name == attribute.Definition.Name),
                        UsedValueCount = attribute.Values.Count,
                        NewValueCount = made.Where(x => x.Kind == "value" && x.AttributeId == attribute.Definition.Id).Select(x => x.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                        NewValues = made.Where(x => x.Kind == "value" && x.AttributeId == attribute.Definition.Id).Select(x => x.Name).Distinct(StringComparer.OrdinalIgnoreCase).Take(200).ToList()
                    });
                categoryReport.NewValues = made.Count(x => x.Kind == "value");
                categoryReport.UnmatchedFields = outcome.UnmatchedFields.ToList();
                categoryReport.Difference = outcome.Difference;
                if (outcome.Decision.Decision == CategoryReuseDecision.NeedsReview && !outcome.AlreadyMapped)
                    report.Review.Add(new("category", categoryReport.RemoteName, outcome.Decision.Reason));

                report.Totals.AttributesCreated += made.Count(x => x.Kind == "attribute");
                report.Totals.ValuesCreated += made.Count(x => x.Kind == "value");
                report.Totals.MappingsCreated += made.Count(x => x.Kind is "categoryMapping" or "attributeMapping" or "valueMapping");
                if (categoryReport.CreatedCategory) report.Totals.CategoriesCreated++;
                else if (categoryReport.Decision == "ReuseExisting") report.Totals.CategoriesReused++;
                if (categoryReport.Decision == "CreateSeparate") report.Totals.CategoriesSeparated++;
                if (categoryReport.Decision == "NeedsReview") report.Totals.CategoriesNeedReview++;
                if (categoryReport.Decision == "AlreadyMapped") report.Totals.CategoriesAlreadyMapped++;
            }
            report.Totals.Categories = report.Categories.Count;

            if (apply) await transaction.CommitAsync(cancellationToken);
            else await transaction.RollbackAsync(cancellationToken);
        }
        finally
        {
            db.SavingChanges -= CollectChanges;
            db.ChangeTracker.Clear();
        }
        return report;
    }

    private async Task<StructureCategoryOutcome> EnsureStructureCategory(
        Guid tenantId,
        PlatformConnection connection,
        string platformLabel,
        ReferenceSnapshot categorySnapshot,
        ReferenceItem categoryItem,
        ReferenceSnapshot attributeSnapshot,
        StructureUsage usage,
        IDictionary<string, AttributeDefinition> importedAttributeLibrary,
        IDictionary<string, CategoryAttributeContext> cache,
        string correlationId,
        CancellationToken cancellationToken)
    {
        var connectionId = connection.Id;
        var remoteAttributes = await LoadReferenceItems(tenantId, attributeSnapshot, "CATEGORY_ATTRIBUTES", cancellationToken);
        var snapshot = StructureSnapshot(usage, remoteAttributes);
        var candidates = StructureCandidateAttributes(remoteAttributes, usage);
        var remoteKeys = remoteAttributes.Select(x => NormalizeCatalogKey(CanonicalAttributeName(x.Name), 160)).Where(x => x.Length > 0).ToHashSet(StringComparer.Ordinal);

        var remoteNamesByKey = remoteAttributes
            .GroupBy(x => NormalizeCatalogKey(CanonicalAttributeName(x.Name), 160), StringComparer.Ordinal)
            .Where(group => group.Key.Length > 0)
            .ToDictionary(group => group.Key, group => CanonicalAttributeName(group.First().Name), StringComparer.Ordinal);
        StructureCategoryDifference? difference = null;
        var (category, decision, alreadyMapped) = await EnsureMappedCategoryForStructure(tenantId, connection, platformLabel, categorySnapshot, categoryItem, remoteKeys, remoteNamesByKey, found => difference = found, cancellationToken);
        var unmatched = UnmatchedProductFields(usage, remoteAttributes);
        if (category is null) return new(null, decision, alreadyMapped, [], unmatched, difference);

        var valuesByAttribute = new Dictionary<string, IReadOnlyList<ReferenceItem>>(StringComparer.Ordinal);
        foreach (var remoteAttribute in candidates)
        {
            var valueSnapshot = await EnsureReferenceSnapshot(tenantId, connectionId, "ATTRIBUTE_VALUES", $"{categoryItem.ExternalId}/{remoteAttribute.ExternalId}", correlationId, cancellationToken);
            if (valueSnapshot is null) continue;
            var values = await LoadReferenceItems(tenantId, valueSnapshot, "ATTRIBUTE_VALUES", cancellationToken);
            // Only the values products really use are imported; marketplace value lists can hold thousands.
            var usedValues = ObservedAttributeValueKeys(snapshot, remoteAttribute);
            values = usedValues is null ? [] : values.Where(value => usedValues.Contains(NormalizeCatalogKey(value.Name, 320))).ToList();
            if (values.Count > 0 || IsRealColorOptionKey(remoteAttribute.Name))
                valuesByAttribute[remoteAttribute.ExternalId] = values;
        }

        var attributes = new Dictionary<string, LocalCategoryAttribute>(StringComparer.Ordinal);
        var codePrefix = connection.PlatformCode == "HEPSIBURADA" ? "HB" : "TRD";
        foreach (var remoteAttribute in candidates.Where(x => valuesByAttribute.ContainsKey(x.ExternalId)))
        {
            var localAttribute = await EnsureMappedAttribute(tenantId, connectionId, category, categoryItem, attributeSnapshot, remoteAttribute, valuesByAttribute[remoteAttribute.ExternalId], importedAttributeLibrary, cancellationToken, codePrefix);
            attributes[NormalizeCatalogKey(remoteAttribute.Name, 320)] = localAttribute;
            attributes.TryAdd(NormalizeCatalogKey(localAttribute.Definition.Name, 320), localAttribute);
        }

        var context = new CategoryAttributeContext(category, categoryItem.ExternalId, attributes);
        // Products may carry values the marketplace list does not contain. Only the colour/size
        // options, where sellers define their own values, take them; every other attribute keeps
        // to the marketplace's own value list (already filtered to the values products use).
        var optionAttributes = attributes
            .Where(pair => VariantOptionAxis(pair.Value.Remote.Name) is not null)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        await EnsureObservedVariantAttributeValues(tenantId, new CategoryAttributeContext(category, categoryItem.ExternalId, optionAttributes), snapshot, cancellationToken);
        await EnsureExactWebColorValueMappings(tenantId, connectionId, context, cancellationToken);
        cache[categoryItem.ExternalId] = context;
        return new(context, decision, alreadyMapped, attributes.Values.GroupBy(x => x.Definition.Id).Select(group => group.First()).ToList(), unmatched, difference);
    }

    private async Task<(Category? Category, CategoryReuseResult Decision, bool AlreadyMapped)> EnsureMappedCategoryForStructure(
        Guid tenantId,
        PlatformConnection connection,
        string platformLabel,
        ReferenceSnapshot categorySnapshot,
        ReferenceItem remoteCategory,
        IReadOnlySet<string> remoteAttributeKeys,
        IReadOnlyDictionary<string, string> remoteNamesByKey,
        Action<StructureCategoryDifference> onDifference,
        CancellationToken cancellationToken)
    {
        var connectionId = connection.Id;
        var now = timeProvider.GetUtcNow();
        var mapping = await db.CategoryMappings.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ScopeExternalId == categorySnapshot.ScopeExternalId && x.ExternalId == remoteCategory.ExternalId, cancellationToken);
        var existing = mapping is null ? null : await db.Categories.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == mapping.LocalId, cancellationToken);
        if (existing is not null && mapping is not null)
        {
            mapping.SnapshotId = categorySnapshot.Id;
            mapping.Status = "VERIFIED";
            mapping.VerifiedAt = now;
            mapping.Version++;
            return (existing, new(CategoryReuseDecision.ReuseExisting, existing.Id, existing.Name, "Kategori daha önce eşlenmiş."), true);
        }

        var leaf = StructureLeafName(connection.PlatformCode, remoteCategory);
        var leafKey = NormalizeCatalogKey(leaf, 160);
        var pathKey = NormalizeCatalogKey(remoteCategory.Path, 160);
        if (string.IsNullOrWhiteSpace(pathKey)) pathKey = leafKey;

        var roots = await db.Categories.Where(x => x.TenantId == tenantId && x.IsActive && x.ParentId == null).ToListAsync(cancellationToken);
        // The same marketplace path already created a panel category in an earlier import.
        var samePath = roots.FirstOrDefault(x => x.NormalizedName == pathKey) ?? roots.FirstOrDefault(x => CatalogKeyNormalizer.SameKey(x.NormalizedName, pathKey));
        CategoryReuseResult decision;
        Category? category = null;
        if (samePath is not null)
        {
            decision = new(CategoryReuseDecision.ReuseExisting, samePath.Id, samePath.Name, "Aynı pazar yeri yoluyla oluşturulmuş panel kategorisi var.");
            category = samePath;
        }
        else
        {
            var sameLeaf = roots.Where(x => NormalizeCatalogKey(PlatformSuffix.Replace(x.Name, ""), 160) == leafKey).ToList();
            var candidates = new List<CategoryReuseCandidate>();
            var known = new List<(Category Panel, KnownAttributeSet Set)>();
            foreach (var panelCategory in sameLeaf)
                foreach (var set in await KnownAttributeSets(tenantId, panelCategory.Id, cancellationToken))
                {
                    candidates.Add(new(panelCategory.Id, panelCategory.Name, set.NamesByKey.Keys.ToHashSet(StringComparer.Ordinal)));
                    known.Add((panelCategory, set));
                }
            decision = CategoryReusePolicy.Decide(leaf, platformLabel, remoteAttributeKeys, candidates);
            if (decision.Decision == CategoryReuseDecision.ReuseExisting)
                category = sameLeaf.First(x => x.Id == decision.CategoryId);
            else if (decision.Decision is CategoryReuseDecision.CreateNew or CategoryReuseDecision.CreateSeparate)
            {
                if (decision.Decision == CategoryReuseDecision.CreateSeparate)
                {
                    decision = decision with { PanelName = SeparateCategoryName(roots, leaf, platformLabel, remoteCategory) };
                    if (known.Count > 0) onDifference(DescribeDifference(known, remoteAttributeKeys, remoteNamesByKey));
                }
                category = NewStructureCategory(tenantId, decision.PanelName, pathKey, now);
            }
        }

        // A panel category links to a single marketplace category per connection. When
        // this connection already points it at another remote category, open a separate one.
        if (category is not null && decision.Decision == CategoryReuseDecision.ReuseExisting
            && await db.CategoryMappings.AnyAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ScopeExternalId == categorySnapshot.ScopeExternalId && x.LocalId == category.Id, cancellationToken))
        {
            var separateName = SeparateCategoryName(roots, leaf, platformLabel, remoteCategory);
            decision = new(CategoryReuseDecision.CreateSeparate, null, separateName, "Bu bağlantıda aynı panel kategorisine başka bir pazar yeri kategorisi zaten eşli; ayrı kategori açılacak.");
            category = NewStructureCategory(tenantId, separateName, pathKey, now);
        }
        if (category is null) return (null, decision, false);

        db.CategoryMappings.Add(new CategoryMapping { Id = Guid.CreateVersion7(), TenantId = tenantId, ConnectionId = connectionId, SnapshotId = categorySnapshot.Id, LocalId = category.Id, ScopeExternalId = categorySnapshot.ScopeExternalId, ExternalId = remoteCategory.ExternalId, Status = "VERIFIED", VerifiedAt = now, Version = 1 });
        return (category, decision, false);
    }

    // Product fields that match no attribute of the category (by name or id). They show
    // up in the report so a mismatch between product fields and attribute names is visible.
    private static List<string> UnmatchedProductFields(StructureUsage usage, IReadOnlyList<ReferenceItem> remoteAttributes)
    {
        var known = remoteAttributes
            .SelectMany(x => new[] { NormalizeCatalogKey(x.Name, 320), NormalizeCatalogKey(x.ExternalId, 320) })
            .Where(x => x.Length > 0)
            .ToHashSet(StringComparer.Ordinal);
        return usage.Options.Keys
            .Where(key => !known.Contains(NormalizeCatalogKey(key, 320)))
            .OrderBy(key => key, StringComparer.OrdinalIgnoreCase)
            .Take(40)
            .ToList();
    }

    private Category NewStructureCategory(Guid tenantId, string name, string pathKey, DateTimeOffset now)
    {
        var category = new Category { Id = Guid.CreateVersion7(), TenantId = tenantId, Name = Short(name, 160), NormalizedName = pathKey, Path = Short(name, 160), Depth = 0, IsLeaf = true, IsActive = true, CreatedAt = now, UpdatedAt = now, Version = 1 };
        db.Categories.Add(category);
        return category;
    }

    // "Bluz (Hepsiburada)"; when that name is taken, add the parent category, then the id.
    private static string SeparateCategoryName(IReadOnlyList<Category> roots, string leaf, string platformLabel, ReferenceItem remoteCategory)
    {
        bool Taken(string name) => roots.Any(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
        var name = $"{leaf} ({platformLabel})";
        if (!Taken(name)) return name;
        var segments = (remoteCategory.Path ?? "").Split(['>', '/'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length > 1)
        {
            name = $"{leaf} ({platformLabel} · {segments[^2]})";
            if (!Taken(name)) return name;
        }
        return $"{leaf} ({platformLabel} · {remoteCategory.ExternalId})";
    }

    private sealed record KnownAttributeSet(string Source, IReadOnlyDictionary<string, string> NamesByKey);

    private static Dictionary<string, string> NamesByKey(IEnumerable<string> names) => names
        .Select(name => (Key: NormalizeCatalogKey(CanonicalAttributeName(name), 160), Name: CanonicalAttributeName(name)))
        .Where(pair => pair.Key.Length > 0)
        .GroupBy(pair => pair.Key, StringComparer.Ordinal)
        .ToDictionary(group => group.Key, group => group.First().Name, StringComparer.Ordinal);

    private async Task<string> PlatformLabelAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken)
    {
        var code = await db.PlatformConnections.AsNoTracking().Where(x => x.TenantId == tenantId && x.Id == connectionId).Select(x => x.PlatformCode).SingleOrDefaultAsync(cancellationToken);
        return code switch { "TRENDYOL" => "Trendyol", "HEPSIBURADA" => "Hepsiburada", "SHOPIFY" => "Shopify", _ => code ?? "Pazar yeri" };
    }

    // Explains a "separate category" decision against the most similar panel category.
    private static StructureCategoryDifference DescribeDifference(
        IReadOnlyList<(Category Panel, KnownAttributeSet Set)> known,
        IReadOnlySet<string> remoteKeys,
        IReadOnlyDictionary<string, string> remoteNamesByKey)
    {
        var best = known.OrderByDescending(item => item.Set.NamesByKey.Keys.Count(remoteKeys.Contains)).First();
        var onlyMarketplace = remoteKeys.Where(key => !best.Set.NamesByKey.ContainsKey(key)).Select(key => remoteNamesByKey.GetValueOrDefault(key, key)).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        var onlyPanel = best.Set.NamesByKey.Where(pair => !remoteKeys.Contains(pair.Key)).Select(pair => pair.Value).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        return new(best.Panel.Name, best.Set.Source, best.Set.NamesByKey.Keys.Count(remoteKeys.Contains), onlyMarketplace, onlyPanel);
    }

    // Every attribute set a panel category is known by: its own requirements, plus
    // the full attribute list of each marketplace category already mapped to it.
    private async Task<List<KnownAttributeSet>> KnownAttributeSets(Guid tenantId, Guid categoryId, CancellationToken cancellationToken)
    {
        var sets = new List<KnownAttributeSet>();
        var own = await (from requirement in db.CategoryAttributeRequirements.AsNoTracking()
                         join definition in db.AttributeDefinitions.AsNoTracking() on new { requirement.TenantId, Id = requirement.AttributeId } equals new { definition.TenantId, definition.Id }
                         where requirement.TenantId == tenantId && requirement.CategoryId == categoryId && definition.IsActive
                         select definition.Name).ToListAsync(cancellationToken);
        var ownNames = NamesByKey(own);
        if (ownNames.Count > 0) sets.Add(new("Panel", ownNames));

        var mappings = await db.CategoryMappings.AsNoTracking().Where(x => x.TenantId == tenantId && x.LocalId == categoryId).ToListAsync(cancellationToken);
        foreach (var mapping in mappings)
        {
            var snapshot = await db.ReferenceSnapshots.AsNoTracking()
                .Where(x => x.TenantId == tenantId && x.ConnectionId == mapping.ConnectionId && x.ResourceType == "CATEGORY_ATTRIBUTES" && x.ScopeExternalId == mapping.ExternalId && x.IsCurrent)
                .OrderByDescending(x => x.FetchedAt)
                .FirstOrDefaultAsync(cancellationToken);
            if (snapshot is null) continue;
            var items = await LoadReferenceItems(tenantId, snapshot, "CATEGORY_ATTRIBUTES", cancellationToken);
            var names = NamesByKey(items.Select(x => x.Name));
            if (names.Count > 0) sets.Add(new(await PlatformLabelAsync(tenantId, mapping.ConnectionId, cancellationToken), names));
        }
        return sets;
    }

    private Task<List<ReferenceItem>> LoadReferenceItems(Guid tenantId, ReferenceSnapshot snapshot, string resourceType, CancellationToken cancellationToken) =>
        db.ReferenceItems.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.SnapshotId == snapshot.Id && x.ResourceType == resourceType && x.IsActive)
            .OrderBy(x => x.SortOrder).ThenBy(x => x.Name)
            .ToListAsync(cancellationToken);

    // Attributes worth importing: only the ones products actually use (by name, by id, or as the
    // same colour/size axis). Mandatory or option attributes no product fills are left out; their
    // marketplace value lists can hold thousands of entries.
    private static List<ReferenceItem> StructureCandidateAttributes(IReadOnlyList<ReferenceItem> remoteAttributes, StructureUsage usage)
    {
        var observedNames = usage.Options.Keys.Select(key => NormalizeCatalogKey(key, 320)).Where(x => x.Length > 0).ToHashSet(StringComparer.Ordinal);
        var observedAxes = usage.Options.Keys.Select(VariantOptionAxis).Where(axis => axis is not null).ToHashSet(StringComparer.Ordinal);
        return remoteAttributes
            .Where(x => observedNames.Contains(NormalizeCatalogKey(x.Name, 320))
                || observedNames.Contains(NormalizeCatalogKey(x.ExternalId, 320))
                || VariantOptionAxis(x.Name) is { } axis && observedAxes.Contains(axis))
            .ToList();
    }

    // Hepsiburada can key product attributes by attribute id; rename them to the
    // reference name so they line up with the category's attribute list.
    private static RemoteCatalogProduct StructureSnapshot(StructureUsage usage, IReadOnlyList<ReferenceItem> remoteAttributes)
    {
        var namesById = remoteAttributes
            .Where(x => !string.IsNullOrWhiteSpace(x.ExternalId))
            .GroupBy(x => NormalizeCatalogKey(x.ExternalId, 320), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Name, StringComparer.Ordinal);
        var variants = new List<RemoteCatalogVariant>();
        var counter = 0;
        foreach (var option in usage.Options)
        {
            var name = namesById.TryGetValue(NormalizeCatalogKey(option.Key, 320), out var mapped) ? mapped : option.Key;
            foreach (var value in option.Value)
            {
                counter++;
                variants.Add(new RemoteCatalogVariant($"structure:{usage.CategoryId}:{counter}", $"structure-{counter}", null, null, new Dictionary<string, string> { [name] = value }, false, null, null, null, null, null, "{}"));
            }
        }
        return new RemoteCatalogProduct($"structure:{usage.CategoryId}", null, usage.Name ?? "", "", null, null, usage.CategoryId, usage.Name, [], variants, "{}");
    }

    private static HashSet<string>? ObservedAttributeValueKeys(RemoteCatalogProduct snapshot, ReferenceItem remoteAttribute)
    {
        if (ObservedVariantValueAxis(remoteAttribute.Name) is { } axis && ObservedOptionValueKeys(snapshot).TryGetValue(axis, out var axisValues)) return axisValues;
        var key = NormalizeCatalogKey(remoteAttribute.Name, 320);
        var values = snapshot.Variants
            .SelectMany(x => x.Options)
            .Where(pair => NormalizeCatalogKey(pair.Key, 320) == key)
            .Select(pair => NormalizeCatalogKey(pair.Value, 320))
            .Where(value => value.Length > 0)
            .ToHashSet(StringComparer.Ordinal);
        return values.Count > 0 ? values : null;
    }

    private static Dictionary<string, HashSet<string>> ObservedOptionValueKeys(RemoteCatalogProduct snapshot) => snapshot.Variants
        .SelectMany(x => x.Options)
        .Select(pair => (Axis: ObservedVariantValueAxis(pair.Key), Value: NormalizeCatalogKey(pair.Value, 320)))
        .Where(item => item.Axis is not null && !string.IsNullOrWhiteSpace(item.Value))
        .GroupBy(item => item.Axis!, StringComparer.Ordinal)
        .ToDictionary(group => group.Key, group => group.Select(item => item.Value).ToHashSet(StringComparer.Ordinal), StringComparer.Ordinal);

    private static string CanonicalAttributeName(string name) => IsWebColorOptionKey(name) ? "Renk" : VariantOptionAxis(name) switch
    {
        "COLOR" => "Renk",
        "SIZE" => "Beden",
        _ => name
    };

    // Trendyol paths are "A / B / C"; Hepsiburada paths are "A > B > C" and its
    // category name is already the leaf.
    private static string StructureLeafName(string platformCode, ReferenceItem remoteCategory) =>
        platformCode == "HEPSIBURADA" && !string.IsNullOrWhiteSpace(remoteCategory.Name)
            ? Short(remoteCategory.Name, 160)
            : CategoryLeafName(remoteCategory.Name, remoteCategory.Path);

    // Field names only (never values): top-level keys plus the keys one level inside
    // objects and arrays of objects, e.g. "variants[]: sku, attributes". Shown in the report
    // so a mismatch between a marketplace response and the mapper is visible.
    private static string[] TopLevelKeys(string rawJson)
    {
        try
        {
            using var document = JsonDocument.Parse(rawJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return [];
            var fields = new List<string>();
            foreach (var property in document.RootElement.EnumerateObject().Take(60))
            {
                var inner = property.Value.ValueKind == JsonValueKind.Object ? property.Value
                    : property.Value.ValueKind == JsonValueKind.Array && property.Value.GetArrayLength() > 0 && property.Value[0].ValueKind == JsonValueKind.Object ? property.Value[0]
                    : default;
                var suffix = property.Value.ValueKind == JsonValueKind.Array ? "[]" : property.Value.ValueKind == JsonValueKind.Object ? "{}" : "";
                fields.Add(inner.ValueKind == JsonValueKind.Object
                    ? $"{property.Name}{suffix}: {string.Join(", ", inner.EnumerateObject().Select(x => x.Name).Take(20))}"
                    : property.Name);
            }
            return fields.ToArray();
        }
        catch (JsonException) { return []; }
    }


    private sealed class StructureUsage(string categoryId, string? name)
    {
        public string CategoryId { get; } = categoryId;
        public string? Name { get; set; } = name;
        public int ProductCount { get; set; }
        public Dictionary<string, HashSet<string>> Options { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    public sealed record StructureCategoryDifference(string ComparedWith, string ComparedSource, int SharedCount, List<string> OnlyInMarketplace, List<string> OnlyInPanel);

    private sealed record StructureChange(string Kind, string Name, Guid? AttributeId = null);

    private sealed record StructureCategoryOutcome(CategoryAttributeContext? Context, CategoryReuseResult Decision, bool AlreadyMapped, IReadOnlyList<LocalCategoryAttribute> Attributes, IReadOnlyList<string> UnmatchedFields, StructureCategoryDifference? Difference);
}

public sealed class StructureReport
{
    public string Mode { get; set; } = "";
    public string Platform { get; set; } = "";
    public DateTimeOffset GeneratedAt { get; set; }
    public StructureTotals Totals { get; set; } = new();
    public List<StructureCategoryReport> Categories { get; set; } = [];
    public List<StructureReviewItem> Review { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
    public List<string> ResponseFields { get; set; } = [];
}

public sealed class StructureTotals
{
    public int Products { get; set; }
    public int ProductsSkipped { get; set; }
    public int ProductsWithoutCategory { get; set; }
    public int Categories { get; set; }
    public int CategoriesCreated { get; set; }
    public int CategoriesReused { get; set; }
    public int CategoriesSeparated { get; set; }
    public int CategoriesAlreadyMapped { get; set; }
    public int CategoriesNeedReview { get; set; }
    public int AttributesCreated { get; set; }
    public int ValuesCreated { get; set; }
    public int MappingsCreated { get; set; }
}

public sealed class StructureCategoryReport
{
    public string ExternalId { get; set; } = "";
    public string RemoteName { get; set; } = "";
    public string RemotePath { get; set; } = "";
    public int ProductCount { get; set; }
    public string Decision { get; set; } = "";
    public string PanelName { get; set; } = "";
    public string Reason { get; set; } = "";
    public bool CreatedCategory { get; set; }
    public int NewValues { get; set; }
    public List<string> UnmatchedFields { get; set; } = [];
    public MarketplaceJobProcessor.StructureCategoryDifference? Difference { get; set; }
    public List<StructureAttributeReport> Attributes { get; set; } = [];
}

public sealed class StructureAttributeReport
{
    public string RemoteName { get; set; } = "";
    public string PanelName { get; set; } = "";
    public string Role { get; set; } = "";
    public bool CreatedDefinition { get; set; }
    public int UsedValueCount { get; set; }
    public int NewValueCount { get; set; }
    public List<string> NewValues { get; set; } = [];
}

public sealed record StructureReviewItem(string Kind, string Subject, string Reason);
