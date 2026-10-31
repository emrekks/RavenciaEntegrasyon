using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

public sealed class ReferenceDataService(AppDbContext db, TimeProvider timeProvider) : IReferenceDataService
{
    public async Task<ServiceResult<ReferenceDataView>> ListAsync(Guid tenantId, Guid connectionId, string resourceType, string? parentExternalId, CancellationToken cancellationToken)
    {
        if (!await VisibleConnectionAsync(tenantId, connectionId, cancellationToken)) return NotFound<ReferenceDataView>();
        var scope = string.IsNullOrWhiteSpace(parentExternalId) ? "" : parentExternalId.Trim();
        var snapshot = await db.ReferenceSnapshots.AsNoTracking().Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ResourceType == resourceType && x.ScopeExternalId == scope && x.IsCurrent).OrderByDescending(x => x.FetchedAt).Select(x => new { x.Id, x.ResourceType, x.FetchedAt }).FirstOrDefaultAsync(cancellationToken);
        if (snapshot is null) return ServiceResult<ReferenceDataView>.Fail("REFERENCE_SNAPSHOT_UNAVAILABLE", "Doğrulanmış güncel reference snapshot yok; canlı platform çağrısı yapılmadı.", 422);
        var query = db.ReferenceItems.AsNoTracking().Where(x => x.TenantId == tenantId && x.SnapshotId == snapshot.Id && x.ResourceType == resourceType);
        if (parentExternalId is not null) query = query.Where(x => x.ParentExternalId == parentExternalId);
        var rows = await query.OrderBy(x => x.SortOrder).ThenBy(x => x.Name).Select(x => new ReferenceItemView(x.ExternalId, x.ParentExternalId, x.Name, x.Path, x.Depth, x.IsLeaf, x.IsActive, x.IsRequired, x.AllowsCustomValue, x.AllowsMultipleValues)).ToListAsync(cancellationToken);
        return ServiceResult<ReferenceDataView>.Ok(new(snapshot.Id, snapshot.ResourceType, snapshot.FetchedAt, rows));
    }

    internal const int DefaultBrandSearchLimit = 50;
    internal const int MaximumBrandSearchLimit = 100;
    internal const int MaximumBrandLookupIds = 500;
    // A contains search needs three characters: the trigram index has nothing to narrow a shorter one with.
    internal const int MinimumContainsSearchLength = 3;
    private const string Brands = "BRANDS";

    public async Task<ServiceResult<ReferenceSliceView>> BrandSummaryAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken)
    {
        var snapshot = await CurrentBrandSnapshotAsync(tenantId, connectionId, cancellationToken);
        return snapshot.Succeeded ? ServiceResult<ReferenceSliceView>.Ok(snapshot.Value!.Slice([])) : new ServiceResult<ReferenceSliceView>(null, snapshot.Error);
    }

    // Prefix matches come from the (TenantId, SnapshotId, NormalizedName COLLATE "C") index in name order, contains
    // matches from the trigram index (both partial on BRANDS, migration AddBrandReferenceSearchIndexes), an id from
    // the unique (TenantId, SnapshotId, ResourceType, ExternalId) index. No query reads the whole snapshot.
    public async Task<ServiceResult<ReferenceSliceView>> SearchBrandsAsync(Guid tenantId, Guid connectionId, string? query, int? limit, CancellationToken cancellationToken)
    {
        var take = limit is null ? DefaultBrandSearchLimit : Math.Clamp(limit.Value, 1, MaximumBrandSearchLimit);
        var snapshotResult = await CurrentBrandSnapshotAsync(tenantId, connectionId, cancellationToken);
        if (!snapshotResult.Succeeded) return new ServiceResult<ReferenceSliceView>(null, snapshotResult.Error);
        var snapshot = snapshotResult.Value!;
        var text = (query ?? "").Trim();
        if (text.Length > 160) text = text[..160];
        var items = BrandItems(tenantId, snapshot.Id).Where(x => x.IsActive);
        if (text.Length == 0)
        {
            var first = await items.OrderBy(x => EF.Functions.Collate(x.NormalizedName, "C")).Take(take).Select(BrandRow).ToListAsync(cancellationToken);
            return ServiceResult<ReferenceSliceView>.Ok(snapshot.Slice(first.Select(x => x.View).ToList()));
        }

        var keys = BrandSearchKeys(text);
        var found = new Dictionary<string, BrandSearchRow>(StringComparer.Ordinal);
        void Add(IEnumerable<BrandSearchRow> rows) { foreach (var row in rows) found.TryAdd(row.View.ExternalId, row); }
        Add(await items.Where(x => x.ExternalId == text).Select(BrandRow).ToListAsync(cancellationToken));
        // One ordered index range per spelling; an OR of them would sort every prefix match before the limit.
        foreach (var key in keys)
        {
            var prefix = EscapeLike(key) + "%";
            Add(await items.Where(x => EF.Functions.Like(EF.Functions.Collate(x.NormalizedName, "C"), prefix, LikeEscape))
                .OrderBy(x => EF.Functions.Collate(x.NormalizedName, "C")).Take(take).Select(BrandRow).ToListAsync(cancellationToken));
        }
        if (found.Count < take && text.Length >= MinimumContainsSearchLength)
            foreach (var key in keys)
            {
                var fragment = "%" + EscapeLike(key) + "%";
                // No ORDER BY: the matches are ranked below, and an ordered read would fetch every match first.
                Add(await items.Where(x => EF.Functions.Like(x.NormalizedName, fragment, LikeEscape)).Take(MaximumBrandSearchLimit * 2).Select(BrandRow).ToListAsync(cancellationToken));
            }
        var ranked = found.Values
            .OrderBy(row => BrandSearchRank(row, text, keys))
            .ThenBy(row => row.NormalizedName.Length)
            .ThenBy(row => row.NormalizedName, StringComparer.Ordinal)
            .ThenBy(row => row.View.ExternalId, StringComparer.Ordinal)
            .Take(take)
            .Select(row => row.View)
            .ToList();
        return ServiceResult<ReferenceSliceView>.Ok(snapshot.Slice(ranked));
    }

    public async Task<ServiceResult<ReferenceSliceView>> LookupBrandsAsync(Guid tenantId, Guid connectionId, IReadOnlyCollection<string> externalIds, CancellationToken cancellationToken)
    {
        var ids = externalIds.Select(x => x.Trim()).Where(x => x.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
        if (ids.Length > MaximumBrandLookupIds) return ServiceResult<ReferenceSliceView>.Fail("VALIDATION_FAILED", $"En fazla {MaximumBrandLookupIds} marka kimliği sorgulanabilir.", 422);
        if (ids.Any(x => x.Length > 256)) return ServiceResult<ReferenceSliceView>.Fail("VALIDATION_FAILED", "Marka kimliği en fazla 256 karakterdir.", 422);
        var snapshotResult = await CurrentBrandSnapshotAsync(tenantId, connectionId, cancellationToken);
        if (!snapshotResult.Succeeded) return new ServiceResult<ReferenceSliceView>(null, snapshotResult.Error);
        var snapshot = snapshotResult.Value!;
        if (ids.Length == 0) return ServiceResult<ReferenceSliceView>.Ok(snapshot.Slice([]));
        var rows = await BrandItems(tenantId, snapshot.Id).Where(x => ids.Contains(x.ExternalId)).Select(BrandRow).ToListAsync(cancellationToken);
        return ServiceResult<ReferenceSliceView>.Ok(snapshot.Slice(rows.OrderBy(x => x.NormalizedName, StringComparer.Ordinal).Select(x => x.View).ToList()));
    }

    // The spellings a typed brand can be stored under. Names are stored as Name.Trim().ToUpperInvariant(), which keeps
    // "ı" and "İ" as they are: an "i" may be stored as "I" (written on a Latin layout) or "İ", an "ı" as "ı" or "I".
    internal static IReadOnlyList<string> BrandSearchKeys(string query)
    {
        var turkishCulture = System.Globalization.CultureInfo.GetCultureInfo("tr-TR");
        var text = query.Trim();
        var invariant = text.ToUpperInvariant();
        return new[] { invariant, text.ToUpper(turkishCulture), invariant.Replace('İ', 'I'), text.ToLower(turkishCulture).ToUpperInvariant() }
            .Where(x => x.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
    }

    // EF writes ESCAPE '' (none) unless one is given; the patterns escape with a backslash.
    private const string LikeEscape = "\\";
    internal static string EscapeLike(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal);

    // 0 the id itself, 1 the whole name, 2 the name's start, 3 a word's start, 4 anywhere in the name.
    internal static int BrandSearchRank(BrandSearchRow row, string query, IReadOnlyList<string> keys)
    {
        if (string.Equals(row.View.ExternalId, query, StringComparison.Ordinal)) return 0;
        if (keys.Any(key => string.Equals(row.NormalizedName, key, StringComparison.Ordinal))) return 1;
        if (keys.Any(key => row.NormalizedName.StartsWith(key, StringComparison.Ordinal))) return 2;
        if (keys.Any(key => row.NormalizedName.Contains(" " + key, StringComparison.Ordinal))) return 3;
        return 4;
    }

    internal sealed record BrandSearchRow(string NormalizedName, ReferenceItemView View);

    internal sealed record BrandSnapshot(Guid Id, DateTimeOffset FetchedAt, int ItemCount)
    {
        public ReferenceSliceView Slice(IReadOnlyList<ReferenceItemView> items) => new(Id, Brands, FetchedAt, ItemCount, items);
    }

    private static readonly System.Linq.Expressions.Expression<Func<ReferenceItem, BrandSearchRow>> BrandRow = x =>
        new BrandSearchRow(x.NormalizedName, new ReferenceItemView(x.ExternalId, x.ParentExternalId, x.Name, x.Path, x.Depth, x.IsLeaf, x.IsActive, x.IsRequired, x.AllowsCustomValue, x.AllowsMultipleValues));

    // "BRANDS" stays a literal in the SQL, so the partial brand indexes apply.
    private IQueryable<ReferenceItem> BrandItems(Guid tenantId, Guid snapshotId) =>
        db.ReferenceItems.AsNoTracking().Where(x => x.TenantId == tenantId && x.SnapshotId == snapshotId && x.ResourceType == "BRANDS");

    private async Task<ServiceResult<BrandSnapshot>> CurrentBrandSnapshotAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken)
    {
        if (!await VisibleConnectionAsync(tenantId, connectionId, cancellationToken)) return NotFound<BrandSnapshot>();
        var snapshot = await db.ReferenceSnapshots.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ResourceType == Brands && x.ScopeExternalId == "" && x.IsCurrent)
            .OrderByDescending(x => x.FetchedAt)
            .Select(x => new BrandSnapshot(x.Id, x.FetchedAt, x.ItemCount))
            .FirstOrDefaultAsync(cancellationToken);
        return snapshot is null
            ? ServiceResult<BrandSnapshot>.Fail("REFERENCE_SNAPSHOT_UNAVAILABLE", "Doğrulanmış güncel reference snapshot yok; canlı platform çağrısı yapılmadı.", 422)
            : ServiceResult<BrandSnapshot>.Ok(snapshot);
    }

    public async Task<ServiceResult<IReadOnlyList<CatalogMappingView>>> ListMappingsAsync(Guid tenantId, string mappingType, Guid connectionId, string? scopeExternalId, CancellationToken cancellationToken)
    {
        if (!await VisibleConnectionAsync(tenantId, connectionId, cancellationToken)) return NotFound<IReadOnlyList<CatalogMappingView>>();
        var scope = string.IsNullOrWhiteSpace(scopeExternalId) ? "" : scopeExternalId.Trim();
        var query = Query(mappingType).AsNoTracking().Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId);
        // The mapping centre's read-only overview intentionally uses '*' to show
        // already saved category-scoped mappings before a category is selected.
        if (scope != "*") query = query.Where(x => x.ScopeExternalId == scope);
        var entities = await query.OrderBy(x => x.LocalId).ToListAsync(cancellationToken);
        if (mappingType != "categories" || entities.Count == 0) return ServiceResult<IReadOnlyList<CatalogMappingView>>.Ok(entities.Select(item => Map(item)).ToList());

        var categoryExternalIds = entities.Select(x => x.ExternalId).Distinct().ToArray();
        var attributeSnapshots = await db.ReferenceSnapshots.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ResourceType == "CATEGORY_ATTRIBUTES" && x.IsCurrent && categoryExternalIds.Contains(x.ScopeExternalId))
            .Select(x => new { x.Id, x.ScopeExternalId })
            .ToListAsync(cancellationToken);
        var attributeSnapshotIds = attributeSnapshots.Select(x => x.Id).ToArray();
        var requiredAttributes = await db.ReferenceItems.AsNoTracking()
            .Where(x => x.TenantId == tenantId && attributeSnapshotIds.Contains(x.SnapshotId) && x.ResourceType == "CATEGORY_ATTRIBUTES" && x.IsActive && x.IsRequired == true)
            .Select(x => new { x.SnapshotId, x.ExternalId })
            .ToListAsync(cancellationToken);
        var mappedAttributes = await db.AttributeMappings.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.Status == "VERIFIED" && categoryExternalIds.Contains(x.ScopeExternalId))
            .Select(x => new { x.ScopeExternalId, x.SnapshotId, x.ExternalId })
            .ToListAsync(cancellationToken);
        var requirements = requiredAttributes.Select(item => new RequiredAttributeMappingReference(item.SnapshotId, item.ExternalId)).ToArray();
        // A required attribute with a connection default (e.g. Menşei = TR) is covered without a mapping.
        var defaultedAttributeIds = await db.ConnectionAttributeDefaults.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId)
            .Select(x => x.ExternalAttributeId)
            .ToListAsync(cancellationToken);
        // n11's Marka is always sent from the product brand (N11ProductPublicationComposer), so it needs no mapping either.
        var platformCode = await db.PlatformConnections.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.Id == connectionId)
            .Select(x => x.PlatformCode)
            .SingleAsync(cancellationToken);
        if (platformCode == "N11") defaultedAttributeIds.Add(N11ProductPublicationComposer.BrandAttributeId);
        var verifiedMappings = mappedAttributes.Select(item => new RequiredAttributeMappingReference(item.SnapshotId, item.ExternalId))
            .Concat(attributeSnapshots.SelectMany(snapshot => defaultedAttributeIds.Select(attributeId => new RequiredAttributeMappingReference(snapshot.Id, attributeId))))
            .ToArray();
        var missingByScope = attributeSnapshots.ToDictionary(
            snapshot => snapshot.ScopeExternalId,
            snapshot => RequiredAttributeMappingPolicy.CountMissing(snapshot.Id, requirements, verifiedMappings));
        var checkedScopes = attributeSnapshots.Select(x => x.ScopeExternalId).ToHashSet(StringComparer.Ordinal);
        return ServiceResult<IReadOnlyList<CatalogMappingView>>.Ok(entities.Select(item => Map(item, checkedScopes.Contains(item.ExternalId) ? missingByScope.GetValueOrDefault(item.ExternalId) : null)).ToList());
    }

    public async Task<ServiceResult<CatalogMappingView?>> GetMappingAsync(Guid tenantId, string mappingType, Guid localId, Guid connectionId, string? scopeExternalId, string? externalId, CancellationToken cancellationToken)
    {
        if (!await VisibleConnectionAsync(tenantId, connectionId, cancellationToken)) return NotFound<CatalogMappingView?>();
        var scope = string.IsNullOrWhiteSpace(scopeExternalId) ? "" : scopeExternalId.Trim();
        var query = Query(mappingType).AsNoTracking().Where(x => x.TenantId == tenantId && x.LocalId == localId && x.ConnectionId == connectionId && x.ScopeExternalId == scope);
        if (!string.IsNullOrWhiteSpace(externalId)) query = query.Where(x => x.ExternalId == externalId.Trim());
        var mappings = await query.OrderBy(x => x.ExternalId).Take(2).ToListAsync(cancellationToken);
        if (mappings.Count > 1) return ServiceResult<CatalogMappingView?>.Fail("MAPPING_EXTERNAL_ID_REQUIRED", "Bu panel değerinin birden fazla pazar yeri değeri var; externalId belirtilmelidir.", 409);
        var mapping = mappings.SingleOrDefault();
        return ServiceResult<CatalogMappingView?>.Ok(mapping is null ? null : Map(mapping));
    }

    public async Task<ServiceResult<CatalogMappingView>> UpsertMappingAsync(Guid tenantId, string mappingType, Guid localId, long? expectedVersion, UpsertCatalogMappingCommand command, CancellationToken cancellationToken)
    {
        if (!await VisibleConnectionAsync(tenantId, command.ConnectionId, cancellationToken)) return NotFound<CatalogMappingView>();
        if (!await LocalExistsAsync(tenantId, mappingType, localId, cancellationToken)) return NotFound<CatalogMappingView>();
        if (!string.Equals(command.Status, "VERIFIED", StringComparison.Ordinal)) return ServiceResult<CatalogMappingView>.Fail("MAPPING_STATUS_INVALID", "Panel üzerinden yalnız VERIFIED mapping kaydedilebilir.", 422);
        var snapshot = await db.ReferenceSnapshots.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ConnectionId == command.ConnectionId && x.Id == command.SnapshotId && x.IsCurrent, cancellationToken);
        if (snapshot is null) return ServiceResult<CatalogMappingView>.Fail("REFERENCE_SNAPSHOT_UNAVAILABLE", "Mapping güncel ve aynı tenant/connection reference snapshot ister.", 422);
        var expectedResource = mappingType switch { "categories" => "CATEGORIES", "brands" => "BRANDS", "attributes" => "CATEGORY_ATTRIBUTES", "attribute-values" => "ATTRIBUTE_VALUES", _ => "" };
        if (!string.Equals(snapshot.ResourceType, expectedResource, StringComparison.Ordinal)) return ServiceResult<CatalogMappingView>.Fail("REFERENCE_RESOURCE_MISMATCH", "Snapshot mapping türüyle eşleşmiyor.", 422);
        var external = await db.ReferenceItems.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.SnapshotId == command.SnapshotId && x.ResourceType == expectedResource && x.ExternalId == command.ExternalId, cancellationToken);
        if (external is null) return ServiceResult<CatalogMappingView>.Fail("EXTERNAL_REFERENCE_NOT_FOUND", "External reference kimliği snapshot içinde bulunamadı.", 422);
        if (mappingType == "categories" && (!external.IsLeaf || !external.IsActive)) return ServiceResult<CatalogMappingView>.Fail("ACTIVE_LEAF_CATEGORY_REQUIRED", "Ürün mapping'i yalnız etkin leaf kategoriye yapılabilir.", 422);

        var scope = snapshot.ScopeExternalId;
        var externalId = command.ExternalId.Trim();
        var mapping = await Query(mappingType).SingleOrDefaultAsync(x => x.TenantId == tenantId && x.LocalId == localId && x.ConnectionId == command.ConnectionId && x.ScopeExternalId == scope && x.ExternalId == externalId, cancellationToken);
        if (mapping is null && mappingType != "attribute-values" && expectedVersion is not null)
        {
            var legacyMapping = await Query(mappingType).SingleOrDefaultAsync(x => x.TenantId == tenantId && x.LocalId == localId && x.ConnectionId == command.ConnectionId && x.ScopeExternalId == scope, cancellationToken);
            if (legacyMapping is not null && legacyMapping.Version == expectedVersion) mapping = legacyMapping;
        }
        // K25: a category or brand maps to one remote record per scope (unique index); a second one is a
        // conflict the user resolves by editing the existing mapping, not a 500.
        if (mapping is null && mappingType is ("categories" or "brands")
            && await Query(mappingType).AnyAsync(x => x.TenantId == tenantId && x.LocalId == localId && x.ConnectionId == command.ConnectionId && x.ScopeExternalId == scope, cancellationToken))
            return ServiceResult<CatalogMappingView>.Fail("MAPPING_EXISTS", "Bu kayıt için eşleme zaten var; mevcut eşlemeyi düzenleyin.", 409);
        if (mapping is null)
        {
            mapping = New(mappingType);
            mapping.Id = Guid.CreateVersion7(); mapping.TenantId = tenantId; mapping.LocalId = localId; mapping.ConnectionId = command.ConnectionId; mapping.ScopeExternalId = scope;
            db.Add(mapping);
        }
        else
        {
            if (expectedVersion is null) return ServiceResult<CatalogMappingView>.Fail("PRECONDITION_REQUIRED", "If-Match gereklidir.", 428);
            if (mapping.Version != expectedVersion) return ServiceResult<CatalogMappingView>.Fail("CONCURRENCY_CONFLICT", $"Kayıt sürümü değişti; güncel sürüm v{mapping.Version}.", 412);
            mapping.Version++;
            if (mappingType == "attribute-values") mapping.LocalId = localId;
        }
        mapping.SnapshotId = command.SnapshotId; mapping.ExternalId = externalId; mapping.Status = command.Status.Trim(); mapping.VerifiedAt = timeProvider.GetUtcNow();
        // A mapping a user saves is theirs, even if it was filled from the template before; it also
        // becomes the connection-wide template for the other categories.
        if (mapping is AttributeValueMapping valueMapping)
        {
            valueMapping.Source = MappingSources.Manual;
            if (command.UpdateTemplate)
            {
                await MappingTemplateStore.RecordValueAsync(db, tenantId, command.ConnectionId, scope, localId, externalId, mapping.VerifiedAt.Value, cancellationToken);
                await MappingTemplateFiller.FillValueInOtherCategoriesAsync(db, tenantId, command.ConnectionId, scope, localId, externalId, mapping.VerifiedAt.Value, cancellationToken);
            }
            await MappingTemplateFiller.ClearDismissalAsync(db, tenantId, command.ConnectionId, MappingTemplateFiller.ValueKind, scope, localId, cancellationToken);
        }
        if (mappingType == "attributes")
        {
            var role = NormalizeRequirementRole(command.Role);
            if (role is not ("ATTRIBUTE" or "OPTION")) return ServiceResult<CatalogMappingView>.Fail("MAPPING_ROLE_INVALID", "Özellik eşleme rolü ATTRIBUTE veya OPTION olmalıdır.", 422);
            if (mapping is AttributeMapping attributeMapping)
            {
                attributeMapping.Source = MappingSources.Manual;
                if (command.UpdateTemplate)
                    await MappingTemplateStore.RecordAttributeAsync(db, tenantId, command.ConnectionId, externalId, localId, role, mapping.VerifiedAt.Value, cancellationToken);
                await MappingTemplateFiller.ClearDismissalAsync(db, tenantId, command.ConnectionId, MappingTemplateFiller.AttributeKind, scope, localId, cancellationToken);
            }

            var mappedCategoryId = await db.CategoryMappings.AsNoTracking()
                .Where(x => x.TenantId == tenantId && x.ConnectionId == command.ConnectionId && x.ExternalId == scope && x.Status == "VERIFIED")
                .Select(x => (Guid?)x.LocalId)
                .FirstOrDefaultAsync(cancellationToken);
            if (mappedCategoryId is Guid categoryId)
            {
                var requirement = await db.CategoryAttributeRequirements
                    .SingleOrDefaultAsync(x => x.TenantId == tenantId && x.CategoryId == categoryId && x.AttributeId == localId, cancellationToken);
                if (role == "OPTION" && requirement?.Role != "OPTION")
                {
                    var optionCount = await db.CategoryAttributeRequirements.CountAsync(x => x.TenantId == tenantId && x.CategoryId == categoryId && x.Role == "OPTION" && x.AttributeId != localId, cancellationToken);
                    if (optionCount >= 2) return ServiceResult<CatalogMappingView>.Fail("OPTION_LIMIT_EXCEEDED", "Bir kategoride en fazla 2 seçenek grubu tanımlanabilir.", 422);
                }

                if (requirement is null)
                {
                    db.CategoryAttributeRequirements.Add(new CategoryAttributeRequirement
                    {
                        Id = Guid.CreateVersion7(),
                        TenantId = tenantId,
                        CategoryId = categoryId,
                        AttributeId = localId,
                        IsRequired = external.IsRequired == true,
                        AllowsCustomValue = external.AllowsCustomValue == true,
                        IsPanelScoped = true,
                        Role = role,
                        DisplayOrder = external.SortOrder ?? 0,
                        Version = 1
                    });
                }
                else if (!requirement.IsPanelScoped || requirement.Role != role || requirement.IsRequired != (external.IsRequired == true) || requirement.AllowsCustomValue != (external.AllowsCustomValue == true) || requirement.DisplayOrder != (external.SortOrder ?? requirement.DisplayOrder))
                {
                    requirement.IsPanelScoped = true;
                    requirement.Role = role;
                    requirement.IsRequired = external.IsRequired == true;
                    requirement.AllowsCustomValue = external.AllowsCustomValue == true;
                    requirement.DisplayOrder = external.SortOrder ?? requirement.DisplayOrder;
                    requirement.Version++;
                }
            }
        }
        await db.SaveChangesAsync(cancellationToken);
        // A newly mapped category inherits the attribute and value decisions made for the other
        // categories of this connection (see MappingTemplateFiller).
        if (mappingType == "categories")
        {
            await MappingTemplateFiller.FillCategoryAsync(db, tenantId, command.ConnectionId, externalId, timeProvider.GetUtcNow(), cancellationToken);
            // The category's marketplace fields and their value lists are read in the background, so the attribute
            // mapping and the product form have them without a button (skipped when a copy younger than a day is there).
            await ConnectionReferencePlan.QueueForSavedCategoryMappingAsync(db, tenantId, command.ConnectionId, externalId, $"mapping-reference:{command.ConnectionId:N}:{externalId}", timeProvider.GetUtcNow(), cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
        }
        return ServiceResult<CatalogMappingView>.Ok(Map(mapping));
    }

    // Platform bridge: on the other connections, which marketplace attribute names the panel
    // attribute is mapped to (e.g. Kumaş → Trendyol "Materyal") and which value names its values are
    // mapped to (Siyah → "Siyah"). The target connection is left out.
    public async Task<ServiceResult<MarketplaceNameHintsView>> MarketplaceNameHintsAsync(Guid tenantId, Guid localAttributeId, Guid targetConnectionId, CancellationToken cancellationToken)
    {
        if (!await db.AttributeDefinitions.AsNoTracking().AnyAsync(x => x.TenantId == tenantId && x.Id == localAttributeId, cancellationToken))
            return NotFound<MarketplaceNameHintsView>();
        var attributeTemplates = await db.AttributeMappingTemplates.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ConnectionId != targetConnectionId && x.LocalAttributeId == localAttributeId)
            .Select(x => new { x.ConnectionId, x.ExternalAttributeId })
            .ToListAsync(cancellationToken);
        // reference_items holds ~10M rows and is indexed by snapshot, so every lookup goes through
        // one current snapshot (any category of the attribute: Trendyol ids and names are stable).
        var attributeNames = new List<string>();
        foreach (var connectionTemplates in attributeTemplates.GroupBy(x => x.ConnectionId))
        {
            var snapshotIds = await db.ReferenceSnapshots.AsNoTracking()
                .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionTemplates.Key && x.ResourceType == "CATEGORY_ATTRIBUTES" && x.IsCurrent)
                .Select(x => x.Id)
                .ToListAsync(cancellationToken);
            var externalIds = connectionTemplates.Select(x => x.ExternalAttributeId).Distinct().ToArray();
            attributeNames.AddRange(await db.ReferenceItems.AsNoTracking()
                .Where(x => x.TenantId == tenantId && snapshotIds.Contains(x.SnapshotId) && x.ResourceType == "CATEGORY_ATTRIBUTES" && externalIds.Contains(x.ExternalId))
                .Select(x => x.Name.Trim())
                .Distinct()
                .ToListAsync(cancellationToken));
        }

        var localValueIds = await db.AttributeValues.AsNoTracking().Where(x => x.TenantId == tenantId && x.AttributeId == localAttributeId && x.IsActive).Select(x => x.Id).ToListAsync(cancellationToken);
        var valueTemplates = await db.AttributeValueMappingTemplates.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ConnectionId != targetConnectionId && localValueIds.Contains(x.LocalValueId))
            .Select(x => new { x.ConnectionId, x.ExternalAttributeId, x.LocalValueId, x.ExternalValueId })
            .ToListAsync(cancellationToken);
        var valueNames = new Dictionary<Guid, IReadOnlyList<string>>();
        foreach (var connectionTemplates in valueTemplates.GroupBy(x => (x.ConnectionId, x.ExternalAttributeId)))
        {
            var suffix = "/" + connectionTemplates.Key.ExternalAttributeId;
            var snapshotIds = await db.ReferenceSnapshots.AsNoTracking()
                .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionTemplates.Key.ConnectionId && x.ResourceType == "ATTRIBUTE_VALUES" && x.IsCurrent && x.ScopeExternalId.EndsWith(suffix))
                .OrderByDescending(x => x.ItemCount)
                .Select(x => x.Id)
                .Take(3)
                .ToListAsync(cancellationToken);
            var externalIds = connectionTemplates.Select(x => x.ExternalValueId).Distinct().ToArray();
            var names = (await db.ReferenceItems.AsNoTracking()
                    .Where(x => x.TenantId == tenantId && snapshotIds.Contains(x.SnapshotId) && x.ResourceType == "ATTRIBUTE_VALUES" && externalIds.Contains(x.ExternalId))
                    .Select(x => new { x.ExternalId, x.Name })
                    .ToListAsync(cancellationToken))
                .GroupBy(x => x.ExternalId)
                .ToDictionary(group => group.Key, group => group.First().Name.Trim());
            foreach (var template in connectionTemplates)
            {
                if (!names.TryGetValue(template.ExternalValueId, out var name) || name.Length == 0) continue;
                var current = valueNames.TryGetValue(template.LocalValueId, out var existing) ? existing : [];
                if (!current.Contains(name, StringComparer.OrdinalIgnoreCase)) valueNames[template.LocalValueId] = [.. current, name];
            }
        }
        return ServiceResult<MarketplaceNameHintsView>.Ok(new(attributeNames.Distinct(StringComparer.OrdinalIgnoreCase).ToList(), valueNames));
    }

    public async Task<ServiceResult<IReadOnlyDictionary<Guid, IReadOnlyList<string>>>> AttributeNameHintsAsync(Guid tenantId, Guid targetConnectionId, CancellationToken cancellationToken)
    {
        var templates = await db.AttributeMappingTemplates.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ConnectionId != targetConnectionId)
            .Select(x => new { x.ConnectionId, x.ExternalAttributeId, x.LocalAttributeId })
            .ToListAsync(cancellationToken);
        var hints = new Dictionary<Guid, IReadOnlyList<string>>();
        foreach (var connectionTemplates in templates.GroupBy(x => x.ConnectionId))
        {
            // Through the current attribute snapshots: reference_items is only indexed by snapshot.
            var snapshotIds = await db.ReferenceSnapshots.AsNoTracking()
                .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionTemplates.Key && x.ResourceType == "CATEGORY_ATTRIBUTES" && x.IsCurrent)
                .Select(x => x.Id)
                .ToListAsync(cancellationToken);
            var externalIds = connectionTemplates.Select(x => x.ExternalAttributeId).Distinct().ToArray();
            var names = (await db.ReferenceItems.AsNoTracking()
                    .Where(x => x.TenantId == tenantId && snapshotIds.Contains(x.SnapshotId) && x.ResourceType == "CATEGORY_ATTRIBUTES" && externalIds.Contains(x.ExternalId))
                    .Select(x => new { x.ExternalId, x.Name })
                    .ToListAsync(cancellationToken))
                .GroupBy(x => x.ExternalId)
                .ToDictionary(group => group.Key, group => group.First().Name.Trim());
            foreach (var template in connectionTemplates)
            {
                if (!names.TryGetValue(template.ExternalAttributeId, out var name) || name.Length == 0) continue;
                var current = hints.TryGetValue(template.LocalAttributeId, out var existing) ? existing : [];
                if (!current.Contains(name, StringComparer.OrdinalIgnoreCase)) hints[template.LocalAttributeId] = [.. current, name];
            }
        }
        return ServiceResult<IReadOnlyDictionary<Guid, IReadOnlyList<string>>>.Ok(hints);
    }

    public async Task<ServiceResult<MappingConflictsView>> ListMappingConflictsAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken)
    {
        if (!await VisibleConnectionAsync(tenantId, connectionId, cancellationToken)) return NotFound<MappingConflictsView>();
        var mappings = await db.AttributeValueMappings.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.Status == "VERIFIED")
            .Select(x => new { x.LocalId, x.ScopeExternalId, x.ExternalId, x.Version, x.VerifiedAt })
            .ToListAsync(cancellationToken);
        var rows = mappings
            .Select(x => new { x.LocalId, x.ScopeExternalId, x.ExternalId, x.Version, x.VerifiedAt, Category = x.ScopeExternalId.Split('/')[0], Attribute = MappingTemplateStore.AttributeOfValueScope(x.ScopeExternalId) ?? "" })
            .Where(x => x.Attribute.Length > 0)
            .ToList();
        var sameCategory = rows.GroupBy(x => (x.ScopeExternalId, x.LocalId)).Where(group => group.Select(x => x.ExternalId).Distinct().Count() > 1).ToList();
        var crossCategory = rows.GroupBy(x => (x.Attribute, x.LocalId)).Where(group => group.Select(x => x.ExternalId).Distinct().Count() > 1 && group.Select(x => x.Category).Distinct().Count() > 1).ToList();
        if (sameCategory.Count == 0 && crossCategory.Count == 0) return ServiceResult<MappingConflictsView>.Ok(new([], []));

        // Names for display: panel values, categories, attributes and values from the current snapshots.
        var localIds = sameCategory.Select(group => group.Key.LocalId).Concat(crossCategory.Select(group => group.Key.LocalId)).Distinct().ToArray();
        var localNames = await db.AttributeValues.AsNoTracking().Where(x => x.TenantId == tenantId && localIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.Value, cancellationToken);
        var scopes = sameCategory.SelectMany(group => group.Select(x => x.ScopeExternalId)).Concat(crossCategory.SelectMany(group => group.Select(x => x.ScopeExternalId))).Distinct().ToArray();
        var categories = scopes.Select(scope => scope.Split('/')[0]).Distinct().ToArray();
        var snapshots = await db.ReferenceSnapshots.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.IsCurrent
                && ((x.ResourceType == "ATTRIBUTE_VALUES" && scopes.Contains(x.ScopeExternalId)) || (x.ResourceType == "CATEGORY_ATTRIBUTES" && categories.Contains(x.ScopeExternalId)) || (x.ResourceType == "CATEGORIES" && x.ScopeExternalId == "")))
            .Select(x => new { x.Id, x.ResourceType, x.ScopeExternalId })
            .ToListAsync(cancellationToken);
        var snapshotIds = snapshots.Select(x => x.Id).ToArray();
        var conflictRows = sameCategory.SelectMany(group => group).Concat(crossCategory.SelectMany(group => group)).ToList();
        var valueIds = conflictRows.Select(x => x.ExternalId).Distinct().ToArray();
        var attributeIds = conflictRows.Select(x => x.Attribute).Distinct().ToArray();
        var items = await db.ReferenceItems.AsNoTracking()
            .Where(x => x.TenantId == tenantId && snapshotIds.Contains(x.SnapshotId)
                && ((x.ResourceType == "CATEGORIES" && categories.Contains(x.ExternalId))
                    || (x.ResourceType == "CATEGORY_ATTRIBUTES" && attributeIds.Contains(x.ExternalId))
                    || (x.ResourceType == "ATTRIBUTE_VALUES" && valueIds.Contains(x.ExternalId))))
            .Select(x => new { x.SnapshotId, x.ExternalId, x.Name })
            .ToListAsync(cancellationToken);
        var snapshotById = snapshots.ToDictionary(x => x.Id);
        var names = items.GroupBy(x => (snapshotById[x.SnapshotId].ResourceType, snapshotById[x.SnapshotId].ScopeExternalId, x.ExternalId)).ToDictionary(group => group.Key, group => group.First().Name);
        string Name(string resourceType, string scope, string externalId) => names.TryGetValue((resourceType, scope, externalId), out var name) ? name : externalId;
        var templates = await db.AttributeValueMappingTemplates.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && localIds.Contains(x.LocalValueId))
            .ToDictionaryAsync(x => (x.ExternalAttributeId, x.LocalValueId), x => x.ExternalValueId, cancellationToken);

        var same = sameCategory.Select(group =>
        {
            var (scope, localId) = group.Key;
            var category = scope.Split('/')[0];
            var attribute = MappingTemplateStore.AttributeOfValueScope(scope)!;
            return new MappingConflictView(scope, Name("CATEGORIES", "", category), Name("CATEGORY_ATTRIBUTES", category, attribute), localId, localNames.GetValueOrDefault(localId, ""),
                group.OrderByDescending(x => x.VerifiedAt).Select(x => new MappingConflictOptionView(x.ExternalId, Name("ATTRIBUTE_VALUES", scope, x.ExternalId), x.Version, x.VerifiedAt, 1)).ToList(),
                templates.GetValueOrDefault((attribute, localId)));
        }).OrderBy(x => x.CategoryName).ThenBy(x => x.AttributeName).ThenBy(x => x.LocalValueName).ToList();
        var cross = crossCategory.Select(group =>
        {
            var (attribute, localId) = group.Key;
            var sample = group.First();
            return new MappingConflictView("", "", Name("CATEGORY_ATTRIBUTES", sample.Category, attribute), localId, localNames.GetValueOrDefault(localId, ""),
                group.GroupBy(x => x.ExternalId).OrderByDescending(values => values.Count())
                    .Select(values => new MappingConflictOptionView(values.Key, Name("ATTRIBUTE_VALUES", values.First().ScopeExternalId, values.Key), 0, values.Max(x => x.VerifiedAt), values.Select(x => x.Category).Distinct().Count()))
                    .ToList(),
                templates.GetValueOrDefault((attribute, localId)));
        }).OrderBy(x => x.AttributeName).ThenBy(x => x.LocalValueName).ToList();
        return ServiceResult<MappingConflictsView>.Ok(new(same, cross));
    }

    public async Task<ServiceResult<IReadOnlyList<ConnectionAttributeDefaultView>>> ListAttributeDefaultsAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken)
    {
        if (!await VisibleConnectionAsync(tenantId, connectionId, cancellationToken)) return NotFound<IReadOnlyList<ConnectionAttributeDefaultView>>();
        var rows = await db.ConnectionAttributeDefaults.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId)
            .OrderBy(x => x.AttributeName)
            .ToListAsync(cancellationToken);
        return ServiceResult<IReadOnlyList<ConnectionAttributeDefaultView>>.Ok(rows.Select(MapDefault).ToList());
    }

    public async Task<ServiceResult<ConnectionAttributeDefaultView>> UpsertAttributeDefaultAsync(Guid tenantId, Guid connectionId, string externalAttributeId, UpsertConnectionAttributeDefaultCommand command, CancellationToken cancellationToken)
    {
        if (!await VisibleConnectionAsync(tenantId, connectionId, cancellationToken)) return NotFound<ConnectionAttributeDefaultView>();
        var attributeId = externalAttributeId.Trim();
        var attributeName = command.AttributeName?.Trim() ?? "";
        var valueId = string.IsNullOrWhiteSpace(command.ExternalValueId) ? null : command.ExternalValueId.Trim();
        var customValue = string.IsNullOrWhiteSpace(command.CustomValue) ? null : command.CustomValue.Trim();
        var sourceField = string.IsNullOrWhiteSpace(command.SourceField) ? null : command.SourceField.Trim().ToUpperInvariant();
        if (attributeId.Length is 0 or > 256 || attributeName.Length is 0 or > 256)
            return ServiceResult<ConnectionAttributeDefaultView>.Fail("ATTRIBUTE_DEFAULT_INVALID", "Varsayılan için pazaryeri özelliği ve adı gereklidir.", 422);
        if (new[] { valueId, customValue, sourceField }.Count(value => value is not null) != 1)
            return ServiceResult<ConnectionAttributeDefaultView>.Fail("ATTRIBUTE_DEFAULT_VALUE_REQUIRED", "Varsayılan için listeden bir değer, serbest metin ya da bir ürün bilgisi seçilmelidir.", 422);
        if (sourceField is not null && !ProductFieldSources.All.Contains(sourceField))
            return ServiceResult<ConnectionAttributeDefaultView>.Fail("ATTRIBUTE_DEFAULT_SOURCE_INVALID", "Ürün bilgisi olarak Model Kodu, Stok Kodu, Barkod veya Ürün Adı seçilebilir.", 422);
        // Pazarama and the others send listed value ids only, so a product field could never reach them.
        if (sourceField is not null && !await db.PlatformConnections.AsNoTracking().AnyAsync(x => x.TenantId == tenantId && x.Id == connectionId && ProductFieldSources.Platforms.Contains(x.PlatformCode), cancellationToken))
            return ServiceResult<ConnectionAttributeDefaultView>.Fail("ATTRIBUTE_DEFAULT_SOURCE_UNSUPPORTED", "Ürün bilgisi bu pazaryerinde kullanılamaz.", 422);
        if ((valueId?.Length ?? 0) > 256 || (customValue?.Length ?? 0) > 320 || (command.ValueName?.Trim().Length ?? 0) > 320)
            return ServiceResult<ConnectionAttributeDefaultView>.Fail("ATTRIBUTE_DEFAULT_INVALID", "Varsayılan değer çok uzun.", 422);
        var row = await db.ConnectionAttributeDefaults.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ExternalAttributeId == attributeId, cancellationToken);
        if (row is null)
        {
            row = new ConnectionAttributeDefault { Id = Guid.CreateVersion7(), TenantId = tenantId, ConnectionId = connectionId, ExternalAttributeId = attributeId, AttributeName = attributeName, Version = 1 };
            db.ConnectionAttributeDefaults.Add(row);
        }
        else row.Version++;
        row.AttributeName = attributeName;
        row.ExternalValueId = valueId;
        row.ValueName = valueId is null ? null : command.ValueName?.Trim();
        row.CustomValue = customValue;
        row.SourceField = sourceField;
        row.UpdatedAt = timeProvider.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
        return ServiceResult<ConnectionAttributeDefaultView>.Ok(MapDefault(row));
    }

    public async Task<ServiceResult<bool>> DeleteAttributeDefaultAsync(Guid tenantId, Guid connectionId, string externalAttributeId, CancellationToken cancellationToken)
    {
        if (!await VisibleConnectionAsync(tenantId, connectionId, cancellationToken)) return NotFound<bool>();
        var attributeId = externalAttributeId.Trim();
        var removed = await db.ConnectionAttributeDefaults.Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ExternalAttributeId == attributeId).ExecuteDeleteAsync(cancellationToken);
        return removed == 0 ? NotFound<bool>() : ServiceResult<bool>.Ok(true);
    }

    private static ConnectionAttributeDefaultView MapDefault(ConnectionAttributeDefault value) => new(value.ExternalAttributeId, value.AttributeName, value.ExternalValueId, value.ValueName, value.CustomValue, value.UpdatedAt, value.Version, value.SourceField);

    public async Task<ServiceResult<MappingTemplateFillView>> FillFromTemplatesAsync(Guid tenantId, Guid connectionId, string categoryExternalId, CancellationToken cancellationToken)
    {
        if (!await VisibleConnectionAsync(tenantId, connectionId, cancellationToken)) return NotFound<MappingTemplateFillView>();
        var category = categoryExternalId.Trim();
        if (category.Length == 0) return ServiceResult<MappingTemplateFillView>.Fail("CATEGORY_REQUIRED", "Doldurulacak pazaryeri kategorisi belirtilmelidir.", 422);
        if (!await db.CategoryMappings.AsNoTracking().AnyAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ExternalId == category && x.Status == "VERIFIED", cancellationToken))
            return ServiceResult<MappingTemplateFillView>.Fail("CATEGORY_MAPPING_REQUIRED", "Önce bu pazaryeri kategorisini bir panel kategorisiyle eşleyin.", 422);
        var result = await MappingTemplateFiller.FillCategoryAsync(db, tenantId, connectionId, category, timeProvider.GetUtcNow(), cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return ServiceResult<MappingTemplateFillView>.Ok(new(result.Attributes, result.Values));
    }

    public async Task<ServiceResult<bool>> DeleteMappingAsync(Guid tenantId, string mappingType, Guid localId, Guid connectionId, string? scopeExternalId, string? externalId, long expectedVersion, CancellationToken cancellationToken)
    {
        if (!await VisibleConnectionAsync(tenantId, connectionId, cancellationToken)) return NotFound<bool>();
        var scope = string.IsNullOrWhiteSpace(scopeExternalId) ? "" : scopeExternalId.Trim();
        var query = Query(mappingType).Where(x => x.TenantId == tenantId && x.LocalId == localId && x.ConnectionId == connectionId && x.ScopeExternalId == scope);
        if (!string.IsNullOrWhiteSpace(externalId)) query = query.Where(x => x.ExternalId == externalId.Trim());
        var candidates = await query.OrderBy(x => x.ExternalId).Take(2).ToListAsync(cancellationToken);
        if (candidates.Count > 1) return ServiceResult<bool>.Fail("MAPPING_EXTERNAL_ID_REQUIRED", "Bu panel değerinin birden fazla pazar yeri değeri var; externalId belirtilmelidir.", 409);
        var mapping = candidates.SingleOrDefault();
        if (mapping is null) return NotFound<bool>();
        if (mapping.Version != expectedVersion) return ServiceResult<bool>.Fail("CONCURRENCY_CONFLICT", $"Kayıt sürümü değişti; güncel sürüm v{mapping.Version}.", 412);
        db.Remove(mapping);
        // The user removed it from this category: the template fill must not bring it back here.
        if (mapping is AttributeValueMapping)
            await MappingTemplateFiller.DismissAsync(db, tenantId, connectionId, MappingTemplateFiller.ValueKind, mapping.ScopeExternalId, mapping.LocalId, timeProvider.GetUtcNow(), cancellationToken);
        else if (mapping is AttributeMapping)
            await MappingTemplateFiller.DismissAsync(db, tenantId, connectionId, MappingTemplateFiller.AttributeKind, mapping.ScopeExternalId, mapping.LocalId, timeProvider.GetUtcNow(), cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        if (mapping is AttributeValueMapping)
            await MappingTemplateStore.ForgetValueAsync(db, tenantId, connectionId, mapping.ScopeExternalId, mapping.LocalId, mapping.ExternalId, mapping.Id, cancellationToken);
        else if (mapping is AttributeMapping)
            await MappingTemplateStore.ForgetAttributeAsync(db, tenantId, connectionId, mapping.ExternalId, mapping.LocalId, mapping.Id, cancellationToken);
        return ServiceResult<bool>.Ok(true);
    }

    private IQueryable<CatalogMapping> Query(string type) => type switch
    {
        "categories" => db.CategoryMappings,
        "brands" => db.BrandMappings,
        "attributes" => db.AttributeMappings,
        "attribute-values" => db.AttributeValueMappings,
        _ => throw new ArgumentException("Geçersiz mapping türü.", nameof(type))
    };

    private static CatalogMapping New(string type) => type switch
    {
        "categories" => new CategoryMapping { ExternalId = "", Status = "" },
        "brands" => new BrandMapping { ExternalId = "", Status = "" },
        "attributes" => new AttributeMapping { ExternalId = "", Status = "" },
        "attribute-values" => new AttributeValueMapping { ExternalId = "", Status = "" },
        _ => throw new ArgumentException("Geçersiz mapping türü.", nameof(type))
    };

    private Task<bool> LocalExistsAsync(Guid tenantId, string type, Guid localId, CancellationToken cancellationToken) => type switch
    {
        "categories" => db.Categories.AnyAsync(x => x.TenantId == tenantId && x.Id == localId && x.IsActive && x.IsLeaf, cancellationToken),
        "brands" => db.Brands.AnyAsync(x => x.TenantId == tenantId && x.Id == localId && x.IsActive, cancellationToken),
        "attributes" => db.AttributeDefinitions.AnyAsync(x => x.TenantId == tenantId && x.Id == localId && x.IsActive, cancellationToken),
        "attribute-values" => db.AttributeValues.AnyAsync(x => x.TenantId == tenantId && x.Id == localId && x.IsActive, cancellationToken),
        _ => Task.FromResult(false)
    };

    private Task<bool> VisibleConnectionAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) =>
        db.PlatformConnections.AsNoTracking().AnyAsync(x => x.TenantId == tenantId && x.Id == connectionId && (x.Status == "ACTIVE" || x.Status == "VERIFIED"), cancellationToken);

    private static CatalogMappingView Map(CatalogMapping value, int? missingRequiredAttributeCount = null) => new(value.Id, value.ConnectionId, value.SnapshotId, value.LocalId, value.ScopeExternalId, value.ExternalId, value.Status, value.VerifiedAt, value.Version, missingRequiredAttributeCount,
        value switch { AttributeValueMapping valueMapping => valueMapping.Source, AttributeMapping attributeMapping => attributeMapping.Source, _ => null });
    private static string NormalizeRequirementRole(string? value) => string.Equals(value?.Trim(), "OPTION", StringComparison.OrdinalIgnoreCase) ? "OPTION" : string.Equals(value?.Trim(), "ATTRIBUTE", StringComparison.OrdinalIgnoreCase) ? "ATTRIBUTE" : value?.Trim().ToUpperInvariant() ?? "";
    private static ServiceResult<T> NotFound<T>() => ServiceResult<T>.Fail("RESOURCE_NOT_FOUND", "Kayıt bulunamadı.", 404);
}
