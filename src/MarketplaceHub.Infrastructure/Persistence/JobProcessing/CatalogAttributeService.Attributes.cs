using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

internal sealed partial class CatalogAttributeService
{
    internal async Task<ReferenceSnapshot?> EnsureReferenceSnapshot(Guid tenantId, Guid connectionId, string resourceType, string? parentExternalId, string correlationId, CancellationToken cancellationToken)
    {
        var scope = string.IsNullOrWhiteSpace(parentExternalId) ? "" : parentExternalId.Trim();
        var current = await db.ReferenceSnapshots
            .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ResourceType == resourceType && x.ScopeExternalId == scope && x.IsCurrent)
            .OrderByDescending(x => x.FetchedAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (current is not null && timeProvider.GetUtcNow() - current.FetchedAt < TimeSpan.FromHours(24)) return current;

        var payload = JsonSerializer.Serialize(new { resourceType, parentExternalId });
        try
        {
            if (!await SyncReferences(tenantId, connectionId, payload, correlationId, cancellationToken)) return current;
        }
        catch (JobProcessingException exception)
        {
            await RecordIssue(tenantId, $"product-reference-sync:{connectionId}:{resourceType}:{scope}", exception.Result.ErrorCode ?? "REFERENCE_SYNC_FAILED", exception.Result.ErrorSummary ?? "Pazar yeri kategori/özellik referansı eşitlenemedi; mevcut panel kayıtları korundu.", cancellationToken);
            return current;
        }

        return await db.ReferenceSnapshots
            .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ResourceType == resourceType && x.ScopeExternalId == scope && x.IsCurrent)
            .OrderByDescending(x => x.FetchedAt)
            .FirstOrDefaultAsync(cancellationToken);
    }

    internal async Task<CategoryAttributeContext?> EnsureCategoryAttributeContext(
        Guid tenantId,
        Guid connectionId,
        ReferenceSnapshot categorySnapshot,
        RemoteCatalogProduct snapshot,
        IReadOnlyList<ReferenceItem> categoryItems,
        IDictionary<string, AttributeDefinition> importedAttributeLibrary,
        IDictionary<string, CategoryAttributeContext> cache,
        string correlationId,
        CancellationToken cancellationToken)
    {
        var externalCategoryId = Short(snapshot.CategoryExternalId, 256);
        if (string.IsNullOrWhiteSpace(externalCategoryId)) return null;
        if (cache.TryGetValue(externalCategoryId, out var cached))
        {
            await EnsureObservedVariantAttributeValues(tenantId, cached, snapshot, cancellationToken);
            await EnsureExactWebColorValueMappings(tenantId, connectionId, cached, cancellationToken);
            return cached;
        }

        var categoryItem = categoryItems.FirstOrDefault(x => string.Equals(x.ExternalId, externalCategoryId, StringComparison.Ordinal));
        if (categoryItem is null)
        {
            await RecordIssue(tenantId, $"product-category-reference:{connectionId}:{externalCategoryId}", "PRODUCT_CATEGORY_REFERENCE_MISSING", "Ürünün kategori kimliği güncel pazar yeri snapshot'ında bulunamadı; kategori özellikleri eşlenmedi.", cancellationToken);
            return null;
        }

        var attributeSnapshot = await EnsureReferenceSnapshot(tenantId, connectionId, "CATEGORY_ATTRIBUTES", externalCategoryId, correlationId, cancellationToken);
        if (attributeSnapshot is null) return null;
        var remoteAttributes = await db.ReferenceItems.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.SnapshotId == attributeSnapshot.Id && x.ResourceType == "CATEGORY_ATTRIBUTES" && x.IsActive)
            .OrderBy(x => x.SortOrder).ThenBy(x => x.Name)
            .ToListAsync(cancellationToken);

        var observedNames = snapshot.Variants
            .SelectMany(x => x.Options.Keys)
            .Select(x => NormalizeCatalogKey(x, 320))
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToHashSet(StringComparer.Ordinal);
        var observedOptionValues = snapshot.Variants
            .SelectMany(x => x.Options)
            .Select(pair => (Axis: ObservedVariantValueAxis(pair.Key), Value: NormalizeCatalogKey(pair.Value, 320)))
            .Where(item => item.Axis is not null && !string.IsNullOrWhiteSpace(item.Value))
            .GroupBy(item => item.Axis!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Select(item => item.Value).ToHashSet(StringComparer.Ordinal), StringComparer.Ordinal);
        var candidateRemoteAttributes = remoteAttributes
            .Where(x => x.IsRequired == true || observedNames.Contains(NormalizeCatalogKey(x.Name, 320)) || VariantOptionAxis(x.Name) is not null)
            .ToList();
        var valuesByAttribute = new Dictionary<string, IReadOnlyList<ReferenceItem>>(StringComparer.Ordinal);
        foreach (var remoteAttribute in candidateRemoteAttributes)
        {
            var valueSnapshot = await EnsureReferenceSnapshot(tenantId, connectionId, "ATTRIBUTE_VALUES", $"{externalCategoryId}/{remoteAttribute.ExternalId}", correlationId, cancellationToken);
            if (valueSnapshot is null) continue;
            var values = await db.ReferenceItems.AsNoTracking()
                .Where(x => x.TenantId == tenantId && x.SnapshotId == valueSnapshot.Id && x.ResourceType == "ATTRIBUTE_VALUES" && x.IsActive)
                .OrderBy(x => x.SortOrder).ThenBy(x => x.Name)
                .ToListAsync(cancellationToken);
            if (ObservedVariantValueAxis(remoteAttribute.Name) is { } axis && observedOptionValues.TryGetValue(axis, out var usedValues))
                values = values.Where(value => usedValues.Contains(NormalizeCatalogKey(value.Name, 320))).ToList();
            // A category definition without active values is not a usable
            // product attribute. The exception is the real Renk slicer: it
            // can legitimately contain seller-defined values even when
            // Trendyol's category value endpoint returns an empty list.
            // Keep that field in the context so observed variant values can
            // populate the local Renk option instead of falling back to Web
            // Color.
            if (values.Count > 0 || IsRealColorOptionKey(remoteAttribute.Name))
                valuesByAttribute[remoteAttribute.ExternalId] = values;
        }

        var localCategory = await EnsureMappedCategory(tenantId, connectionId, categorySnapshot, categoryItem, cancellationToken);
        var attributes = new Dictionary<string, LocalCategoryAttribute>(StringComparer.Ordinal);
        foreach (var remoteAttribute in candidateRemoteAttributes.Where(x => valuesByAttribute.ContainsKey(x.ExternalId)))
        {
            var values = valuesByAttribute[remoteAttribute.ExternalId];
            var localAttribute = await EnsureMappedAttribute(tenantId, connectionId, localCategory, categoryItem, attributeSnapshot, remoteAttribute, values, importedAttributeLibrary, cancellationToken);
            attributes[NormalizeCatalogKey(remoteAttribute.Name, 320)] = localAttribute;
            attributes.TryAdd(NormalizeCatalogKey(localAttribute.Definition.Name, 320), localAttribute);
        }

        // Hide stale requirements created by older importer versions when the
        // marketplace no longer provides any values for the mapped attribute.
        // Keep the row for audit/recovery, but remove it from the product
        // editor and required-attribute validation until values return.
        var importedAttributeIds = await db.AttributeMappings.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ScopeExternalId == externalCategoryId && x.Status == "VERIFIED")
            .Select(x => x.LocalId)
            .ToListAsync(cancellationToken);
        var visibleAttributeIds = attributes.Values.Select(x => x.Definition.Id).ToHashSet();
        if (importedAttributeIds.Count > 0)
        {
            var staleRequirements = await db.CategoryAttributeRequirements
                .Where(x => x.TenantId == tenantId && x.CategoryId == localCategory.Id && importedAttributeIds.Contains(x.AttributeId) && !visibleAttributeIds.Contains(x.AttributeId))
                .ToListAsync(cancellationToken);
            foreach (var stale in staleRequirements)
            {
                if (!stale.IsPanelScoped && stale.Role == "ATTRIBUTE") continue;
                stale.IsPanelScoped = false;
                stale.Role = "ATTRIBUTE";
                stale.IsRequired = false;
                stale.AllowsCustomValue = false;
                stale.Version++;
            }
        }

        var result = new CategoryAttributeContext(localCategory, externalCategoryId, attributes);
        await EnsureObservedVariantAttributeValues(tenantId, result, snapshot, cancellationToken);
        await EnsureExactWebColorValueMappings(tenantId, connectionId, result, cancellationToken);
        cache[externalCategoryId] = result;
        return result;
    }

    internal async Task EnsureObservedVariantAttributeValues(
        Guid tenantId,
        CategoryAttributeContext categoryContext,
        RemoteCatalogProduct snapshot,
        CancellationToken cancellationToken)
    {
        foreach (var mapped in categoryContext.Attributes.Values
            .GroupBy(item => item.Definition.Id)
            .Select(group => group
                .OrderBy(item => IsWebColorOptionKey(item.Remote.Name) ? 1 : 0)
                .First()))
        {
            // Web Color is a presentation field. It may map to the same local
            // Renk attribute, but its marketplace values must never be added as
            // local option values. Only collect values from the real slicer
            // field (normally Trendyol's Renk/Color field).
            if (IsWebColorOptionKey(mapped.Remote.Name)) continue;
            var remoteName = NormalizeCatalogKey(mapped.Remote.Name, 320);
            var axis = VariantOptionAxis(mapped.Remote.Name);
            var values = snapshot.Variants
                .SelectMany(x => x.Options)
                .Where(pair => NormalizeCatalogKey(pair.Key, 320) == remoteName
                    || (axis is not null && VariantOptionAxis(pair.Key) == axis && !IsWebColorOptionKey(pair.Key)))
                .Select(pair => pair.Value.Trim())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (values.Count == 0) continue;
            mapped.Definition.DataType = AttributeDataType.SingleSelect;
            mapped.Definition.SelectionMode = "SINGLE";
            foreach (var valueText in values)
            {
                var normalized = NormalizeCatalogKey(valueText, 320);
                if (!categoryContext.ObservedVariantValueKeys.Add($"{mapped.Definition.Id:D}:{normalized}"))
                    continue;
                var valueKeys = CatalogLookupKeys(valueText, 320);
                var value = db.AttributeValues.Local.FirstOrDefault(x => x.TenantId == tenantId && x.AttributeId == mapped.Definition.Id && CatalogKeyNormalizer.SameKey(x.NormalizedValue, normalized, 320))
                    ?? await db.AttributeValues.Where(x => x.TenantId == tenantId && x.AttributeId == mapped.Definition.Id && valueKeys.Contains(x.NormalizedValue)).OrderBy(x => x.NormalizedValue == normalized ? 0 : 1).ThenBy(x => x.Id).FirstOrDefaultAsync(cancellationToken);
                if (value is not null)
                {
                    value.IsActive = true;
                    continue;
                }
                db.AttributeValues.Add(new AttributeValue
                {
                    Id = Guid.CreateVersion7(),
                    TenantId = tenantId,
                    AttributeId = mapped.Definition.Id,
                    Value = Short(valueText, 320),
                    NormalizedValue = normalized,
                    SortOrder = values.IndexOf(valueText),
                    IsActive = true,
                    Version = 1
                });
            }
        }
    }

    internal async Task<Category> EnsureMappedCategory(Guid tenantId, Guid connectionId, ReferenceSnapshot categorySnapshot, ReferenceItem remoteCategory, CancellationToken cancellationToken)
    {
        var mapping = await FindCategoryMapping(db, tenantId, connectionId, categorySnapshot.ScopeExternalId, remoteCategory.ExternalId, cancellationToken);
        Category? category = mapping is null ? null : await MappedCategory(db, tenantId, mapping, cancellationToken);
        var leafName = CategoryLeafName(remoteCategory.Name, remoteCategory.Path);
        var normalized = NormalizeCatalogKey(remoteCategory.Path, 160);
        var legacyNormalized = NormalizeCatalogKey(remoteCategory.Name, 160);
        if (string.IsNullOrWhiteSpace(normalized)) normalized = NormalizeCatalogKey(leafName, 160);
        var categoryKeys = CatalogLookupKeys(remoteCategory.Path).Concat(CatalogLookupKeys(remoteCategory.Name)).Concat(CatalogLookupKeys(leafName)).Distinct(StringComparer.Ordinal).ToArray();
        category ??= db.Categories.Local.FirstOrDefault(x => x.TenantId == tenantId && x.ParentId is null && (CatalogKeyNormalizer.SameKey(x.NormalizedName, normalized) || CatalogKeyNormalizer.SameKey(x.NormalizedName, legacyNormalized)))
            ?? await db.Categories.Where(x => x.TenantId == tenantId && x.ParentId == null && categoryKeys.Contains(x.NormalizedName)).OrderBy(x => x.NormalizedName == normalized ? 0 : x.NormalizedName == legacyNormalized ? 1 : 2).ThenBy(x => x.Id).FirstOrDefaultAsync(cancellationToken);
        // B-5: a panel category matched by its leaf name may already be mapped to another marketplace category of this scope
        // ("Kadın > Elbise" and "Çocuk > Elbise" both match "Elbise"). A second mapping to it breaks the unique index
        // (tenant, connection, local category, scope) and failed the model's import on every run; this one gets the panel
        // category of its own full path instead (found or opened), keyed by the path, and by the path and id if two
        // marketplace categories share the same path.
        if (mapping is null && category is not null && await CategoryMappedToAnother(tenantId, connectionId, categorySnapshot.ScopeExternalId, remoteCategory.ExternalId, category.Id, cancellationToken))
        {
            category = null;
            foreach (var key in new[] { normalized, NormalizeCatalogKey($"{remoteCategory.Path} {remoteCategory.ExternalId}", 160) }.Distinct(StringComparer.Ordinal))
            {
                var own = db.Categories.Local.FirstOrDefault(x => x.TenantId == tenantId && x.ParentId is null && x.NormalizedName == key)
                    ?? await db.Categories.Where(x => x.TenantId == tenantId && x.ParentId == null && x.NormalizedName == key).FirstOrDefaultAsync(cancellationToken);
                if (own is not null && await CategoryMappedToAnother(tenantId, connectionId, categorySnapshot.ScopeExternalId, remoteCategory.ExternalId, own.Id, cancellationToken)) continue;
                category = own ?? NewCategory(key);
                break;
            }
        }
        category ??= NewCategory(normalized);
        Category NewCategory(string key)
        {
            var now = timeProvider.GetUtcNow();
            var created = new Category { Id = Guid.CreateVersion7(), TenantId = tenantId, Name = leafName, NormalizedName = key, Path = leafName, Depth = 0, IsLeaf = true, IsActive = true, CreatedAt = now, UpdatedAt = now, Version = 1 };
            db.Categories.Add(created);
            return created;
        }
        if (db.Entry(category).State != EntityState.Added)
        {
            // K13: an existing panel category keeps its name, path and active state; only its leaf flag
            // follows its children (a category the user split is no longer a leaf).
            var isLeaf = !await db.Categories.AnyAsync(x => x.TenantId == tenantId && x.ParentId == category.Id && x.IsActive, cancellationToken);
            if (category.IsLeaf != isLeaf)
            {
                category.IsLeaf = isLeaf;
                category.UpdatedAt = timeProvider.GetUtcNow();
                category.Version++;
            }
        }

        if (mapping is null)
        {
            mapping = new CategoryMapping { Id = Guid.CreateVersion7(), TenantId = tenantId, ConnectionId = connectionId, SnapshotId = categorySnapshot.Id, LocalId = category.Id, ScopeExternalId = categorySnapshot.ScopeExternalId, ExternalId = remoteCategory.ExternalId, Status = "VERIFIED", VerifiedAt = timeProvider.GetUtcNow(), Version = 1 };
            db.CategoryMappings.Add(mapping);
        }
        else
        {
            mapping.SnapshotId = categorySnapshot.Id;
            mapping.Status = "VERIFIED";
            mapping.VerifiedAt = timeProvider.GetUtcNow();
            mapping.Version++;
        }
        return category;
    }

    // B-5: the mapping of a marketplace category, saved or added in this unit of work. A query alone does not see one
    // added since the last save, so a second lookup of the same category added a duplicate that broke the unique index.
    internal static async Task<CategoryMapping?> FindCategoryMapping(AppDbContext db, Guid tenantId, Guid connectionId, string? scopeExternalId, string externalId, CancellationToken cancellationToken) =>
        db.CategoryMappings.Local.FirstOrDefault(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ScopeExternalId == scopeExternalId && x.ExternalId == externalId && db.Entry(x).State != EntityState.Deleted)
        ?? await db.CategoryMappings.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ScopeExternalId == scopeExternalId && x.ExternalId == externalId, cancellationToken);

    // The panel category a mapping points at, also when both were added in this unit of work.
    internal static async Task<Category?> MappedCategory(AppDbContext db, Guid tenantId, CategoryMapping mapping, CancellationToken cancellationToken) =>
        db.Categories.Local.FirstOrDefault(x => x.TenantId == tenantId && x.Id == mapping.LocalId)
        ?? await db.Categories.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == mapping.LocalId, cancellationToken);

    // Whether another marketplace category of this connection and scope is already mapped to the panel category (saved or
    // added in this unit of work).
    private Task<bool> CategoryMappedToAnother(Guid tenantId, Guid connectionId, string? scopeExternalId, string externalId, Guid categoryId, CancellationToken cancellationToken) =>
        CategoryMappedToAnother(db, tenantId, connectionId, scopeExternalId, externalId, categoryId, cancellationToken);

    internal static async Task<bool> CategoryMappedToAnother(AppDbContext db, Guid tenantId, Guid connectionId, string? scopeExternalId, string externalId, Guid categoryId, CancellationToken cancellationToken) =>
        db.CategoryMappings.Local.Any(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ScopeExternalId == scopeExternalId && x.LocalId == categoryId && x.ExternalId != externalId && db.Entry(x).State != EntityState.Deleted)
        || await db.CategoryMappings.AnyAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ScopeExternalId == scopeExternalId && x.LocalId == categoryId && x.ExternalId != externalId, cancellationToken);

    internal async Task<LocalCategoryAttribute> EnsureMappedAttribute(
        Guid tenantId,
        Guid connectionId,
        Category category,
        ReferenceItem remoteCategory,
        ReferenceSnapshot attributeSnapshot,
        ReferenceItem remoteAttribute,
        IReadOnlyList<ReferenceItem> values,
        IDictionary<string, AttributeDefinition> importedAttributeLibrary,
        CancellationToken cancellationToken,
        string codePrefix = "TRD")
    {
        var optionAxis = VariantOptionAxis(remoteAttribute.Name);
        var canonicalName = IsWebColorOptionKey(remoteAttribute.Name) ? "Renk" : optionAxis switch
        {
            "COLOR" => "Renk",
            "SIZE" => "Beden",
            _ => Short(remoteAttribute.Name, 160)
        };
        var code = Short($"{codePrefix}_{remoteCategory.ExternalId}_{remoteAttribute.ExternalId}", 96);
        var dataType = values.Count > 0
            ? (remoteAttribute.AllowsMultipleValues == true ? AttributeDataType.MultiSelect : AttributeDataType.SingleSelect)
            : AttributeDataType.Text;
        var attributeKey = NormalizeCatalogKey(canonicalName, 160);
        var attribute = importedAttributeLibrary.TryGetValue(attributeKey, out var sharedAttribute)
            ? sharedAttribute
            : db.AttributeDefinitions.Local.FirstOrDefault(x => x.TenantId == tenantId && x.IsActive && NormalizeCatalogKey(x.Name, 160) == attributeKey)
                ?? (await db.AttributeDefinitions
                    .Where(x => x.TenantId == tenantId && x.IsActive)
                    .OrderBy(x => x.CreatedAt)
                    .ThenBy(x => x.Id)
                    .ToListAsync(cancellationToken))
                    .FirstOrDefault(x => NormalizeCatalogKey(x.Name, 160) == attributeKey)
                ?? db.AttributeDefinitions.Local.FirstOrDefault(x => x.TenantId == tenantId && x.Code == code)
                ?? await db.AttributeDefinitions.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Code == code, cancellationToken);
        if (values.Count == 0 && attribute is not null && (attribute.DataType is AttributeDataType.SingleSelect or AttributeDataType.MultiSelect))
            dataType = attribute.DataType;
        if (attribute is null && optionAxis is not null)
        {
            var existingAttributes = await db.AttributeDefinitions
                .Where(x => x.TenantId == tenantId && x.IsActive)
                .OrderBy(x => x.CreatedAt)
                .ToListAsync(cancellationToken);
            attribute = db.AttributeDefinitions.Local.FirstOrDefault(x => x.TenantId == tenantId && x.IsActive && VariantOptionAxis(x.Name) == optionAxis)
                ?? existingAttributes.FirstOrDefault(x => VariantOptionAxis(x.Name) == optionAxis);
        }
        if (attribute is null)
        {
            attribute = new AttributeDefinition { Id = Guid.CreateVersion7(), TenantId = tenantId, Code = code, Name = canonicalName, DataType = dataType, SelectionMode = dataType == AttributeDataType.MultiSelect ? "MULTI" : dataType == AttributeDataType.SingleSelect ? "SINGLE" : null, IsActive = true, CreatedAt = timeProvider.GetUtcNow(), UpdatedAt = timeProvider.GetUtcNow(), Version = 1 };
            db.AttributeDefinitions.Add(attribute);
        }
        else
        {
            attribute.Name = canonicalName;
            attribute.DataType = dataType;
            attribute.SelectionMode = dataType == AttributeDataType.MultiSelect ? "MULTI" : dataType == AttributeDataType.SingleSelect ? "SINGLE" : null;
            attribute.IsActive = true;
            attribute.UpdatedAt = timeProvider.GetUtcNow();
            attribute.Version++;
        }
        importedAttributeLibrary[attributeKey] = attribute;

        if (IsWebColorOptionKey(remoteAttribute.Name))
        {
            var legacyColorAttributes = await db.AttributeDefinitions
                .Where(x => x.TenantId == tenantId && x.IsActive && x.Id != attribute.Id)
                .ToListAsync(cancellationToken);
            foreach (var legacyColorAttribute in legacyColorAttributes.Where(x => IsWebColorOptionKey(x.Name)))
            {
                legacyColorAttribute.IsActive = false;
                legacyColorAttribute.UpdatedAt = timeProvider.GetUtcNow();
                legacyColorAttribute.Version++;
            }
        }

        // The same canonical option can be represented by multiple marketplace
        // fields. Reuse a requirement already added in this import context
        // before querying the database, otherwise two pending inserts can
        // violate the category/attribute uniqueness constraint on SaveChanges.
        var requirement = db.CategoryAttributeRequirements.Local.FirstOrDefault(x =>
                x.TenantId == tenantId
                && x.CategoryId == category.Id
                && x.AttributeId == attribute.Id)
            ?? await db.CategoryAttributeRequirements.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.CategoryId == category.Id && x.AttributeId == attribute.Id, cancellationToken);
        // Web Color may share the panel Renk source with the real Renk
        // option. Never let that presentation field demote an existing
        // variant-option requirement to a plain attribute.
        var role = requirement?.Role == "OPTION" || IsVariantOptionName(remoteAttribute.Name) ? "OPTION" : "ATTRIBUTE";
        if (requirement is null)
            db.CategoryAttributeRequirements.Add(new CategoryAttributeRequirement { Id = Guid.CreateVersion7(), TenantId = tenantId, CategoryId = category.Id, AttributeId = attribute.Id, IsRequired = remoteAttribute.IsRequired == true, AllowsCustomValue = remoteAttribute.AllowsCustomValue == true, IsPanelScoped = true, DisplayOrder = remoteAttribute.SortOrder ?? 0, Role = role, Version = 1 });
        else
        {
            requirement.IsRequired = remoteAttribute.IsRequired == true;
            requirement.AllowsCustomValue = remoteAttribute.AllowsCustomValue == true;
            requirement.DisplayOrder = remoteAttribute.SortOrder ?? requirement.DisplayOrder;
            requirement.Role = role;
            requirement.IsPanelScoped = true;
            requirement.Version++;
        }

        // Several marketplace fields (for example the real color slicer and
        // Web Color) can intentionally share the same panel attribute. Their
        // external field is part of the mapping identity, so both mappings
        // must be retained even when they are pending in this save batch.
        var attributeMapping = db.AttributeMappings.Local.FirstOrDefault(x =>
                x.TenantId == tenantId
                && x.ConnectionId == connectionId
                && x.LocalId == attribute.Id
                && x.ScopeExternalId == remoteCategory.ExternalId
                && x.ExternalId == remoteAttribute.ExternalId)
            ?? await db.AttributeMappings.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.LocalId == attribute.Id && x.ScopeExternalId == remoteCategory.ExternalId && x.ExternalId == remoteAttribute.ExternalId, cancellationToken);
        if (attributeMapping is null)
            db.AttributeMappings.Add(new AttributeMapping { Id = Guid.CreateVersion7(), TenantId = tenantId, ConnectionId = connectionId, SnapshotId = attributeSnapshot.Id, LocalId = attribute.Id, ScopeExternalId = remoteCategory.ExternalId, ExternalId = remoteAttribute.ExternalId, Status = "VERIFIED", VerifiedAt = timeProvider.GetUtcNow(), Version = 1 });
        else
        {
            attributeMapping.SnapshotId = attributeSnapshot.Id;
            attributeMapping.ExternalId = remoteAttribute.ExternalId;
            attributeMapping.Status = "VERIFIED";
            attributeMapping.VerifiedAt = timeProvider.GetUtcNow();
            attributeMapping.Version++;
        }

        // Web Color is a marketplace presentation field, not a second local
        // option group. Its many-to-one value mapping is maintained explicitly
        // in the mapping workspace (for example Mürdüm -> Mor), so importing
        // the remote list must not create marketplace-named values under the
        // panel's Renk attribute or overwrite those custom mappings.
        if (IsWebColorOptionKey(remoteAttribute.Name))
            return new LocalCategoryAttribute(attribute, remoteAttribute, values, role);

        if (values.Count == 0) return new LocalCategoryAttribute(attribute, remoteAttribute, values, role);
        // The attribute's stored values, its value scope's current snapshot and the scope's stored value mappings are read
        // once for the whole value list (a Trendyol attribute can list hundreds of values and the import rebuilds every
        // category context per model, live 12 Nov 2026: ~4,000 queries per model). Each value below picks from them what
        // its own queries used to return (StoredAttributeValues); nothing in this loop saves, so the rows stay as read.
        var valueScope = $"{remoteCategory.ExternalId}/{remoteAttribute.ExternalId}";
        var storedValues = await StoredAttributeValues.ReadAsync(db, tenantId, attribute.Id, values.SelectMany(remoteValue => CatalogLookupKeys(remoteValue.Name, 320)), cancellationToken);
        var valueSnapshot = await db.ReferenceSnapshots.AsNoTracking().Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ResourceType == "ATTRIBUTE_VALUES" && x.ScopeExternalId == valueScope && x.IsCurrent).OrderByDescending(x => x.FetchedAt).FirstOrDefaultAsync(cancellationToken);
        var storedMappings = valueSnapshot is null
            ? null
            : await StoredAttributeValueMappings.ReadAsync(db, tenantId, connectionId, valueScope, cancellationToken);
        foreach (var remoteValue in values)
        {
            var normalizedValue = NormalizeCatalogKey(remoteValue.Name, 320);
            var valueKeys = CatalogLookupKeys(remoteValue.Name, 320);
            var value = db.AttributeValues.Local.FirstOrDefault(x => x.TenantId == tenantId && x.AttributeId == attribute.Id && CatalogKeyNormalizer.SameKey(x.NormalizedValue, normalizedValue, 320))
                ?? storedValues.Track(storedValues.First(valueKeys, normalizedValue));
            if (value is null)
            {
                value = new AttributeValue { Id = Guid.CreateVersion7(), TenantId = tenantId, AttributeId = attribute.Id, Value = Short(remoteValue.Name, 320), NormalizedValue = normalizedValue, SortOrder = remoteValue.SortOrder ?? 0, IsActive = true, Version = 1 };
                db.AttributeValues.Add(value);
            }
            else
            {
                value.Value = Short(remoteValue.Name, 320);
                value.SortOrder = remoteValue.SortOrder ?? value.SortOrder;
                value.IsActive = true;
                value.Version++;
            }
            if (valueSnapshot is null || storedMappings is null) continue;
            var valueMapping = db.AttributeValueMappings.Local.FirstOrDefault(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.LocalId == value.Id && x.ScopeExternalId == valueScope && x.ExternalId == remoteValue.ExternalId)
                ?? storedMappings.Track(storedMappings.Exact(value.Id, remoteValue.ExternalId));
            valueMapping ??= db.AttributeValueMappings.Local.FirstOrDefault(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ScopeExternalId == valueScope && x.ExternalId == remoteValue.ExternalId)
                ?? storedMappings.Track(storedMappings.FirstByLocalId(remoteValue.ExternalId));
            if (valueMapping is null)
                db.AttributeValueMappings.Add(new AttributeValueMapping { Id = Guid.CreateVersion7(), TenantId = tenantId, ConnectionId = connectionId, SnapshotId = valueSnapshot.Id, LocalId = value.Id, ScopeExternalId = valueScope, ExternalId = remoteValue.ExternalId, Status = "VERIFIED", VerifiedAt = timeProvider.GetUtcNow(), Version = 1 });
            else
            {
                valueMapping.SnapshotId = valueSnapshot.Id;
                valueMapping.ExternalId = remoteValue.ExternalId;
                valueMapping.Status = "VERIFIED";
                valueMapping.VerifiedAt = timeProvider.GetUtcNow();
                valueMapping.Version++;
            }
        }
        return new LocalCategoryAttribute(attribute, remoteAttribute, values, role);
    }

    internal async Task EnsureExactWebColorValueMappings(
        Guid tenantId,
        Guid connectionId,
        string categoryExternalId,
        AttributeDefinition localAttribute,
        ReferenceItem remoteAttribute,
        IReadOnlyList<ReferenceItem> remoteValues,
        CategoryAttributeContext categoryContext,
        CancellationToken cancellationToken)
    {
        var hasNewTrackedValue = db.AttributeValues.Local.Any(x =>
            x.TenantId == tenantId
            && x.AttributeId == localAttribute.Id
            && x.IsActive
            && !categoryContext.ExactWebColorValueIds.Contains(x.Id));
        if (categoryContext.ExactWebColorInitializedAttributeIds.Contains(localAttribute.Id) && !hasNewTrackedValue)
            return;

        var valueScope = $"{categoryExternalId}/{remoteAttribute.ExternalId}";
        var valueSnapshot = await db.ReferenceSnapshots.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ResourceType == "ATTRIBUTE_VALUES" && x.ScopeExternalId == valueScope && x.IsCurrent)
            .OrderByDescending(x => x.FetchedAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (valueSnapshot is null) return;

        var trackedLocalValues = db.AttributeValues.Local
            .Where(x => x.TenantId == tenantId && x.AttributeId == localAttribute.Id && x.IsActive)
            .ToList();
        var trackedLocalValueIds = trackedLocalValues.Select(x => x.Id).ToHashSet();
        var localValues = trackedLocalValues
            .Concat(await db.AttributeValues
                .Where(x => x.TenantId == tenantId && x.AttributeId == localAttribute.Id && x.IsActive && !trackedLocalValueIds.Contains(x.Id))
                .ToListAsync(cancellationToken))
            .ToList();
        var remoteByValue = remoteValues
            .GroupBy(x => NormalizeCatalogKey(x.Name, 320), StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.First(), StringComparer.Ordinal);

        categoryContext.ExactWebColorInitializedAttributeIds.Add(localAttribute.Id);
        // The scope's stored mappings, read once for every value below (nothing here saves, so they stay as read).
        var storedMappedValueIds = (await db.AttributeValueMappings.AsNoTracking()
                .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ScopeExternalId == valueScope)
                .Select(x => x.LocalId)
                .ToListAsync(cancellationToken))
            .ToHashSet();
        foreach (var localValue in localValues.Where(x => categoryContext.ExactWebColorValueIds.Add(x.Id)))
        {
            if (!remoteByValue.TryGetValue(localValue.NormalizedValue, out var remoteValue)) continue;

            // A saved custom mapping takes precedence over the exact-name
            // default and is intentionally never overwritten by a sync. A value
            // may have several mappings in a scope, so this is an existence check.
            if (db.AttributeValueMappings.Local.Any(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.LocalId == localValue.Id && x.ScopeExternalId == valueScope)
                || storedMappedValueIds.Contains(localValue.Id)) continue;

            db.AttributeValueMappings.Add(new AttributeValueMapping
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenantId,
                ConnectionId = connectionId,
                SnapshotId = valueSnapshot.Id,
                LocalId = localValue.Id,
                ScopeExternalId = valueScope,
                ExternalId = remoteValue.ExternalId,
                Status = "VERIFIED",
                VerifiedAt = timeProvider.GetUtcNow(),
                Version = 1
            });
        }
    }

    internal async Task EnsureExactWebColorValueMappings(
        Guid tenantId,
        Guid connectionId,
        CategoryAttributeContext categoryContext,
        CancellationToken cancellationToken)
    {
        foreach (var mapped in categoryContext.Attributes.Values
            .Where(x => IsWebColorOptionKey(x.Remote.Name))
            .GroupBy(x => x.Definition.Id)
            .Select(x => x.First()))
        {
            // Exact matches are safe defaults for the special mapping. They
            // make values such as “Çok Renkli” publishable immediately while
            // leaving non-identical, user-defined mappings (for example
            // Mürdüm -> Mor) untouched.
            await EnsureExactWebColorValueMappings(tenantId, connectionId, categoryContext.ExternalCategoryId, mapped.Definition, mapped.Remote, mapped.Values, categoryContext, cancellationToken);
        }
    }

    internal async Task UpsertProductAttributeAssignments(Guid tenantId, Guid connectionId, Product product, ProductVariant variant, IReadOnlyDictionary<string, string> options, CategoryAttributeContext categoryContext, CancellationToken cancellationToken)
    {
        var sortOrder = 0;
        foreach (var pair in options)
        {
            if (IsWebColorOptionKey(pair.Key))
            {
                sortOrder++;
                continue;
            }
            if (!TryGetMappedAttribute(categoryContext.Attributes, pair.Key, out var mapped))
            {
                sortOrder++;
                continue;
            }
            if (IsCatalogProductOption(mapped, pair.Key))
            {
                sortOrder++;
                continue;
            }

            var valueKey = NormalizeCatalogKey(pair.Value, 320);
            var remoteValue = mapped.Values.FirstOrDefault(x => NormalizeCatalogKey(x.Name, 320) == valueKey);
            Guid? valueId = null;
            string? textValue = null;
            decimal? numberValue = null;
            bool? booleanValue = null;
            var localValue = await ResolveMappedAttributeValue(tenantId, connectionId, categoryContext, mapped, remoteValue, pair.Value, cancellationToken);
            if (localValue is not null)
            {
                valueId = localValue.Id;
            }
            else if (mapped.Remote.AllowsCustomValue == true)
            {
                if (mapped.Definition.DataType == AttributeDataType.Number && decimal.TryParse(pair.Value, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var number)) numberValue = number;
                else if (mapped.Definition.DataType == AttributeDataType.Boolean && bool.TryParse(pair.Value, out var boolean)) booleanValue = boolean;
                else textValue = Short(pair.Value, 320);
            }
            else
            {
                await RecordIssue(tenantId, $"product-attribute-value:{product.Id}:{variant.Id}:{mapped.Definition.Id}:{valueKey}", "PRODUCT_ATTRIBUTE_VALUE_UNMAPPED", $"Pazar yeri ürün özelliği '{pair.Key}: {pair.Value}' için güncel panel değeri bulunamadı; atama yapılmadı.", cancellationToken);
                sortOrder++;
                continue;
            }
            if (valueId is null && textValue is null && numberValue is null && booleanValue is null)
            {
                await RecordIssue(tenantId, $"product-attribute-value:{product.Id}:{variant.Id}:{mapped.Definition.Id}:{valueKey}", "PRODUCT_ATTRIBUTE_VALUE_UNMAPPED", $"Pazar yeri ürün özelliği '{pair.Key}: {pair.Value}' için panel değeri eşlenemedi; atama yapılmadı.", cancellationToken);
                sortOrder++;
                continue;
            }

            var assignment = db.ProductAttributeAssignments.Local.FirstOrDefault(x => x.TenantId == tenantId && x.ProductId == product.Id && x.VariantId == variant.Id && x.AttributeId == mapped.Definition.Id)
                ?? await db.ProductAttributeAssignments.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ProductId == product.Id && x.VariantId == variant.Id && x.AttributeId == mapped.Definition.Id, cancellationToken);
            if (assignment is null)
            {
                assignment = new ProductAttributeAssignment { Id = Guid.CreateVersion7(), TenantId = tenantId, ProductId = product.Id, VariantId = variant.Id, AttributeId = mapped.Definition.Id, ValueId = valueId, TextValue = textValue, NumberValue = numberValue, BooleanValue = booleanValue, SortOrder = sortOrder, Version = 1 };
                db.ProductAttributeAssignments.Add(assignment);
            }
            else
            {
                assignment.ValueId = valueId;
                assignment.TextValue = textValue;
                assignment.NumberValue = numberValue;
                assignment.BooleanValue = booleanValue;
                assignment.SortOrder = sortOrder;
                assignment.Version++;
            }
            sortOrder++;
        }

        await UpsertDerivedWebColorAttributeAssignment(tenantId, connectionId, product, variant, options, categoryContext, cancellationToken);
    }

    private async Task UpsertDerivedWebColorAttributeAssignment(
        Guid tenantId,
        Guid connectionId,
        Product product,
        ProductVariant variant,
        IReadOnlyDictionary<string, string> options,
        CategoryAttributeContext categoryContext,
        CancellationToken cancellationToken)
    {
        var webColor = categoryContext.Attributes.Values
            .Where(item => IsWebColorOptionKey(item.Remote.Name))
            .GroupBy(item => item.Definition.Id)
            .Select(group => group.First())
            .FirstOrDefault();
        var realColor = options.FirstOrDefault(pair => IsRealColorOptionKey(pair.Key));
        if (webColor is null || string.IsNullOrWhiteSpace(realColor.Key) || string.IsNullOrWhiteSpace(realColor.Value)) return;
        if (!TryGetMappedAttribute(categoryContext.Attributes, realColor.Key, out var colorAttribute)) return;

        var remoteColorValue = colorAttribute.Values.FirstOrDefault(value => NormalizeCatalogKey(value.Name, 320) == NormalizeCatalogKey(realColor.Value, 320));
        var localColorValue = await ResolveMappedAttributeValue(tenantId, connectionId, categoryContext, colorAttribute, remoteColorValue, realColor.Value, cancellationToken);
        if (localColorValue is null)
        {
            await RecordIssue(tenantId, $"product-web-color-value:{product.Id}:{variant.Id}:{NormalizeCatalogKey(realColor.Value, 320)}", "PRODUCT_WEBCOLOR_VALUE_UNMAPPED", $"Pazar yeri Renk değeri '{realColor.Value}' için panel Renk değeri bulunamadı; Web Color atanmadı.", cancellationToken);
            return;
        }

        var webColorExternalId = await ResolveWebColorExternalIdAsync(tenantId, connectionId, categoryContext, webColor, localColorValue, cancellationToken);
        if (string.IsNullOrWhiteSpace(webColorExternalId))
        {
            await RecordIssue(tenantId, $"product-web-color-mapping:{product.Id}:{variant.Id}:{localColorValue.Id}", "PRODUCT_WEBCOLOR_MAPPING_REQUIRED", $"Panel Renk değeri '{localColorValue.Value}' için Web Color eşlemesi bulunamadı; Web Color atanmadı.", cancellationToken);
            return;
        }

        var assignment = db.ProductAttributeAssignments.Local.FirstOrDefault(x => x.TenantId == tenantId && x.ProductId == product.Id && x.VariantId == variant.Id && x.AttributeId == webColor.Definition.Id)
            ?? await db.ProductAttributeAssignments.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ProductId == product.Id && x.VariantId == variant.Id && x.AttributeId == webColor.Definition.Id, cancellationToken);
        if (assignment is null)
        {
            db.ProductAttributeAssignments.Add(new ProductAttributeAssignment
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenantId,
                ProductId = product.Id,
                VariantId = variant.Id,
                AttributeId = webColor.Definition.Id,
                ValueId = localColorValue.Id,
                SortOrder = webColor.Remote.SortOrder ?? 0,
                Version = 1
            });
        }
        else
        {
            assignment.ValueId = localColorValue.Id;
            assignment.TextValue = null;
            assignment.NumberValue = null;
            assignment.BooleanValue = null;
            assignment.SortOrder = webColor.Remote.SortOrder ?? assignment.SortOrder;
            assignment.Version++;
        }
    }

    private async Task<string?> ResolveWebColorExternalIdAsync(
        Guid tenantId,
        Guid connectionId,
        CategoryAttributeContext categoryContext,
        LocalCategoryAttribute webColor,
        AttributeValue localColorValue,
        CancellationToken cancellationToken)
    {
        var valueScope = $"{categoryContext.ExternalCategoryId}/{webColor.Remote.ExternalId}";
        // Several mappings may exist for one colour; the latest verified one that is still a valid
        // Web Color value wins (the same rule the publication composer uses).
        var mapping = db.AttributeValueMappings.Local
            .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.LocalId == localColorValue.Id && x.ScopeExternalId == valueScope && x.Status == "VERIFIED")
            .Concat(await db.AttributeValueMappings.AsNoTracking()
                .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.LocalId == localColorValue.Id && x.ScopeExternalId == valueScope && x.Status == "VERIFIED")
                .ToListAsync(cancellationToken))
            .Where(candidate => webColor.Values.Any(value => value.ExternalId == candidate.ExternalId))
            .OrderByDescending(candidate => candidate.VerifiedAt ?? DateTimeOffset.MinValue)
            .ThenByDescending(candidate => candidate.Id)
            .FirstOrDefault();
        if (mapping is not null) return mapping.ExternalId;

        var exact = webColor.Values.FirstOrDefault(value => NormalizeCatalogKey(value.Name, 320) == NormalizeCatalogKey(localColorValue.Value, 320));
        if (exact is not null) return exact.ExternalId;
        foreach (var fallbackKey in CatalogColorMappingPolicy.FallbackKeys(localColorValue.Value))
        {
            var fallback = webColor.Values.FirstOrDefault(value => NormalizeCatalogKey(value.Name, 320).Replace("-", "", StringComparison.Ordinal) == fallbackKey);
            if (fallback is not null) return fallback.ExternalId;
        }
        return null;
    }

    internal async Task PromoteCommonImportedAttributes(Guid tenantId, Product product, IReadOnlyList<ProductVariant> importedVariants, CategoryAttributeContext categoryContext, CancellationToken cancellationToken)
    {
        var variantIds = importedVariants.Select(x => x.Id).Distinct().ToArray();
        if (variantIds.Length == 0) return;

        var importedAttributeIds = categoryContext.Attributes.Values
            .Where(x => x.Role != "OPTION")
            .Select(x => x.Definition.Id)
            .ToHashSet();
        if (importedAttributeIds.Count == 0) return;

        var trackedAssignments = db.ProductAttributeAssignments.Local
            .Where(x => x.TenantId == tenantId && x.ProductId == product.Id && x.VariantId.HasValue && variantIds.Contains(x.VariantId.Value))
            .ToList();
        var storedAssignments = await db.ProductAttributeAssignments
            .Where(x => x.TenantId == tenantId && x.ProductId == product.Id && x.VariantId.HasValue && variantIds.Contains(x.VariantId.Value))
            .ToListAsync(cancellationToken);
        var assignments = trackedAssignments
            .Concat(storedAssignments)
            .GroupBy(x => x.Id)
            .Select(x => x.First())
            .Where(x => importedAttributeIds.Contains(x.AttributeId))
            .ToList();

        foreach (var attributeId in importedAttributeIds)
        {
            var byVariant = assignments
                .Where(x => x.AttributeId == attributeId && x.VariantId is not null)
                .GroupBy(x => x.VariantId!.Value)
                .ToDictionary(x => x.Key, x => x.ToList());
            var common = byVariant.Count == variantIds.Length && byVariant.Values.All(x => x.Count == 1)
                ? byVariant.Values.Select(x => x[0]).ToList()
                : [];
            var first = common.FirstOrDefault();
            var isCommon = first is not null && common.Skip(1).All(x => SameImportedAttributeValue(first, x));
            var global = db.ProductAttributeAssignments.Local.FirstOrDefault(x => x.TenantId == tenantId && x.ProductId == product.Id && x.VariantId == null && x.AttributeId == attributeId)
                ?? await db.ProductAttributeAssignments.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ProductId == product.Id && x.VariantId == null && x.AttributeId == attributeId, cancellationToken);

            if (!isCommon)
            {
                if (global is not null) db.ProductAttributeAssignments.Remove(global);
                continue;
            }

            // One panel attribute can intentionally serve multiple marketplace fields
            // (for example, the product color and its Web Color presentation field).
            var mapped = categoryContext.Attributes.Values.First(x => x.Definition.Id == attributeId);
            if (global is null)
            {
                global = new ProductAttributeAssignment
                {
                    Id = Guid.CreateVersion7(),
                    TenantId = tenantId,
                    ProductId = product.Id,
                    VariantId = null,
                    AttributeId = attributeId,
                    ValueId = first!.ValueId,
                    TextValue = first.TextValue,
                    NumberValue = first.NumberValue,
                    BooleanValue = first.BooleanValue,
                    SortOrder = mapped.Remote.SortOrder ?? first.SortOrder,
                    Version = 1
                };
                db.ProductAttributeAssignments.Add(global);
            }
            else
            {
                global.ValueId = first!.ValueId;
                global.TextValue = first.TextValue;
                global.NumberValue = first.NumberValue;
                global.BooleanValue = first.BooleanValue;
                global.SortOrder = mapped.Remote.SortOrder ?? first.SortOrder;
                global.Version++;
            }

            db.ProductAttributeAssignments.RemoveRange(common);
        }
    }

    private static bool SameImportedAttributeValue(ProductAttributeAssignment left, ProductAttributeAssignment right) =>
        left.ValueId == right.ValueId
        && string.Equals(left.TextValue, right.TextValue, StringComparison.Ordinal)
        && left.NumberValue == right.NumberValue
        && left.BooleanValue == right.BooleanValue;

    internal async Task<AttributeValue?> ResolveMappedAttributeValue(
        Guid tenantId,
        Guid connectionId,
        CategoryAttributeContext categoryContext,
        LocalCategoryAttribute mapped,
        ReferenceItem? remoteValue,
        string remoteValueText,
        CancellationToken cancellationToken)
    {
        var valueScope = $"{categoryContext.ExternalCategoryId}/{mapped.Remote.ExternalId}";
        if (remoteValue is not null)
        {
            // One Trendyol value may be mapped to several panel values. The import picks one
            // deterministically: the panel value named like the Trendyol value, otherwise the most
            // recently verified mapping (before, an unordered query picked any of them).
            var mappings = db.AttributeValueMappings.Local
                .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ScopeExternalId == valueScope && x.ExternalId == remoteValue.ExternalId && x.Status == "VERIFIED")
                .Concat(await db.AttributeValueMappings.AsNoTracking()
                    .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ScopeExternalId == valueScope && x.ExternalId == remoteValue.ExternalId && x.Status == "VERIFIED")
                    .ToListAsync(cancellationToken))
                .GroupBy(mapping => mapping.LocalId)
                .Select(group => group.OrderByDescending(mapping => mapping.VerifiedAt ?? DateTimeOffset.MinValue).ThenByDescending(mapping => mapping.Id).First())
                .ToList();
            if (mappings.Count > 0)
            {
                var localIds = mappings.Select(mapping => mapping.LocalId).ToArray();
                var mappedValues = db.AttributeValues.Local
                    .Where(x => x.TenantId == tenantId && localIds.Contains(x.Id) && x.AttributeId == mapped.Definition.Id && x.IsActive)
                    .ToList();
                var trackedIds = mappedValues.Select(x => x.Id).ToHashSet();
                mappedValues.AddRange(await db.AttributeValues.AsNoTracking()
                    .Where(x => x.TenantId == tenantId && localIds.Contains(x.Id) && !trackedIds.Contains(x.Id) && x.AttributeId == mapped.Definition.Id && x.IsActive)
                    .ToListAsync(cancellationToken));
                var remoteKey = NormalizeCatalogKey(remoteValue.Name, 320);
                var chosen = mappedValues.FirstOrDefault(value => CatalogKeyNormalizer.SameKey(value.NormalizedValue, remoteKey, 320))
                    ?? mappings
                        .OrderByDescending(mapping => mapping.VerifiedAt ?? DateTimeOffset.MinValue)
                        .ThenByDescending(mapping => mapping.Id)
                        .Select(mapping => mappedValues.FirstOrDefault(value => value.Id == mapping.LocalId))
                        .FirstOrDefault(value => value is not null);
                if (chosen is not null) return chosen;
            }
        }

        var normalized = NormalizeCatalogKey(remoteValueText, 320);
        if (IsWebColorOptionKey(mapped.Remote.Name))
        {
            var localValues = await db.AttributeValues.AsNoTracking()
                .Where(x => x.TenantId == tenantId && x.AttributeId == mapped.Definition.Id && x.IsActive)
                .ToListAsync(cancellationToken);
            foreach (var fallbackKey in CatalogColorMappingPolicy.FallbackKeys(remoteValueText))
            {
                var fallback = localValues.FirstOrDefault(value => NormalizeCatalogKey(value.Value, 320).Replace("-", "", StringComparison.Ordinal) == fallbackKey);
                if (fallback is not null) return fallback;
            }
        }
        var normalizedKeys = CatalogLookupKeys(remoteValueText, 320);
        return db.AttributeValues.Local.FirstOrDefault(x => x.TenantId == tenantId && x.AttributeId == mapped.Definition.Id && x.IsActive && CatalogKeyNormalizer.SameKey(x.NormalizedValue, normalized, 320))
            ?? await db.AttributeValues.AsNoTracking().Where(x => x.TenantId == tenantId && x.AttributeId == mapped.Definition.Id && x.IsActive && normalizedKeys.Contains(x.NormalizedValue)).OrderBy(x => x.NormalizedValue == normalized ? 0 : 1).ThenBy(x => x.Id).FirstOrDefaultAsync(cancellationToken);
    }

    internal sealed class CategoryAttributeContext(Category LocalCategory, string ExternalCategoryId, IReadOnlyDictionary<string, LocalCategoryAttribute> Attributes)
    {
        public Category LocalCategory { get; } = LocalCategory;
        public string ExternalCategoryId { get; } = ExternalCategoryId;
        public IReadOnlyDictionary<string, LocalCategoryAttribute> Attributes { get; } = Attributes;
        public HashSet<string> ObservedVariantValueKeys { get; } = new(StringComparer.Ordinal);
        public HashSet<Guid> ExactWebColorInitializedAttributeIds { get; } = [];
        public HashSet<Guid> ExactWebColorValueIds { get; } = [];
    }
    internal sealed record LocalCategoryAttribute(AttributeDefinition Definition, ReferenceItem Remote, IReadOnlyList<ReferenceItem> Values, string Role);
}
