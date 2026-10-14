using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

// Fills a marketplace category from the connection-wide templates. Example: T-shirt has
// "Panel Renk → Trendyol 12" and "Siyah → 13"; when Bluz lists attribute 12 and 13 is an active
// value of it, Bluz gets the same two mappings, marked Source = TEMPLATE. A mapping is never
// filled where the category already has one for that panel attribute/value (the user's own
// decision wins) or where the user removed it before (a dismissal). Changes are added to the
// context; the caller saves.
internal static class MappingTemplateFiller
{
    public const string AttributeKind = "ATTRIBUTE";
    public const string ValueKind = "VALUE";
    private const int MaximumOptionGroups = 2;

    public sealed record FillResult(int Attributes, int Values)
    {
        public static readonly FillResult None = new(0, 0);
        public FillResult Add(FillResult other) => new(Attributes + other.Attributes, Values + other.Values);
    }

    // Attributes of the category, then the values of every attribute whose value list is known.
    public static async Task<FillResult> FillCategoryAsync(AppDbContext db, Guid tenantId, Guid connectionId, string externalCategoryId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var attributes = await FillAttributesAsync(db, tenantId, connectionId, externalCategoryId, now, cancellationToken);
        var mappedAttributes = db.AttributeMappings.Local
            .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ScopeExternalId == externalCategoryId && x.Status == "VERIFIED")
            .Select(x => x.ExternalId)
            .Concat(await db.AttributeMappings.AsNoTracking()
                .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ScopeExternalId == externalCategoryId && x.Status == "VERIFIED")
                .Select(x => x.ExternalId)
                .ToListAsync(cancellationToken))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var result = new FillResult(attributes, 0);
        foreach (var externalAttributeId in mappedAttributes)
            result = result.Add(new FillResult(0, await FillValuesAsync(db, tenantId, connectionId, $"{externalCategoryId}/{externalAttributeId}", now, cancellationToken)));
        return result;
    }

    public static async Task<int> FillAttributesAsync(AppDbContext db, Guid tenantId, Guid connectionId, string externalCategoryId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var panelCategoryIds = await db.CategoryMappings.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ExternalId == externalCategoryId && x.Status == "VERIFIED")
            .Select(x => x.LocalId)
            .ToListAsync(cancellationToken);
        // Only categories a panel category is mapped to; browsing a category fills nothing.
        if (panelCategoryIds.Count == 0) return 0;
        var snapshot = await CurrentSnapshotAsync(db, tenantId, connectionId, "CATEGORY_ATTRIBUTES", externalCategoryId, cancellationToken);
        if (snapshot is null) return 0;
        var remoteAttributes = await db.ReferenceItems.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.SnapshotId == snapshot.Value && x.ResourceType == "CATEGORY_ATTRIBUTES" && x.IsActive)
            .ToListAsync(cancellationToken);
        if (remoteAttributes.Count == 0) return 0;
        var remoteIds = remoteAttributes.Select(x => x.ExternalId).ToArray();
        var templates = await db.AttributeMappingTemplates.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && remoteIds.Contains(x.ExternalAttributeId))
            .ToListAsync(cancellationToken);
        if (templates.Count == 0) return 0;

        var activeLocalAttributes = (await db.AttributeDefinitions.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.IsActive)
            .Select(x => x.Id)
            .ToListAsync(cancellationToken)).ToHashSet();
        var existing = await db.AttributeMappings.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ScopeExternalId == externalCategoryId)
            .Select(x => new { x.LocalId, x.ExternalId })
            .ToListAsync(cancellationToken);
        var dismissed = await DismissedAsync(db, tenantId, connectionId, AttributeKind, [externalCategoryId], cancellationToken);

        var filled = 0;
        foreach (var template in templates.OrderBy(x => x.ExternalAttributeId, StringComparer.Ordinal))
        {
            if (!activeLocalAttributes.Contains(template.LocalAttributeId)) continue;
            // The category already maps this marketplace attribute (from this or another panel
            // attribute): that is the user's decision for this category, and a second panel
            // attribute would send two values for one marketplace attribute.
            if (existing.Any(x => x.ExternalId == template.ExternalAttributeId)) continue;
            if (dismissed.Contains((externalCategoryId, template.LocalAttributeId))) continue;
            var remote = remoteAttributes.First(x => x.ExternalId == template.ExternalAttributeId);
            if (!await EnsureRequirementsAsync(db, tenantId, panelCategoryIds, template, remote, cancellationToken)) continue;
            db.AttributeMappings.Add(new AttributeMapping
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenantId,
                ConnectionId = connectionId,
                SnapshotId = snapshot.Value,
                LocalId = template.LocalAttributeId,
                ScopeExternalId = externalCategoryId,
                ExternalId = template.ExternalAttributeId,
                Status = "VERIFIED",
                VerifiedAt = now,
                Source = MappingSources.Template,
                Version = 1
            });
            existing.Add(new { LocalId = template.LocalAttributeId, ExternalId = template.ExternalAttributeId });
            filled++;
        }
        return filled;
    }

    public static async Task<int> FillValuesAsync(AppDbContext db, Guid tenantId, Guid connectionId, string valueScope, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (MappingTemplateStore.AttributeOfValueScope(valueScope) is not { } externalAttributeId) return 0;
        var externalCategoryId = valueScope[..valueScope.IndexOf('/')];
        var snapshot = await CurrentSnapshotAsync(db, tenantId, connectionId, "ATTRIBUTE_VALUES", valueScope, cancellationToken);
        if (snapshot is null) return 0;

        // Only values of the panel attributes this category maps to the marketplace attribute.
        var localAttributeIds = db.AttributeMappings.Local
            .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ScopeExternalId == externalCategoryId && x.ExternalId == externalAttributeId && x.Status == "VERIFIED")
            .Select(x => x.LocalId)
            .Concat(await db.AttributeMappings.AsNoTracking()
                .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ScopeExternalId == externalCategoryId && x.ExternalId == externalAttributeId && x.Status == "VERIFIED")
                .Select(x => x.LocalId)
                .ToListAsync(cancellationToken))
            .Distinct()
            .ToArray();
        if (localAttributeIds.Length == 0) return 0;
        var localValueIds = await db.AttributeValues.AsNoTracking()
            .Where(x => x.TenantId == tenantId && localAttributeIds.Contains(x.AttributeId) && x.IsActive)
            .Select(x => x.Id)
            .ToListAsync(cancellationToken);
        var templates = await db.AttributeValueMappingTemplates.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ExternalAttributeId == externalAttributeId && localValueIds.Contains(x.LocalValueId))
            .ToListAsync(cancellationToken);
        if (templates.Count == 0) return 0;

        var activeRemoteValues = (await db.ReferenceItems.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.SnapshotId == snapshot.Value && x.ResourceType == "ATTRIBUTE_VALUES" && x.IsActive)
            .Select(x => x.ExternalId)
            .ToListAsync(cancellationToken)).ToHashSet(StringComparer.Ordinal);
        var mappedLocalValues = (await db.AttributeValueMappings.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ScopeExternalId == valueScope)
            .Select(x => x.LocalId)
            .ToListAsync(cancellationToken)).ToHashSet();
        var dismissed = await DismissedAsync(db, tenantId, connectionId, ValueKind, [valueScope], cancellationToken);

        var filled = 0;
        foreach (var template in templates)
        {
            // The template value must exist in this category's list: Trendyol keeps value ids
            // stable, but not every category offers every value.
            if (!activeRemoteValues.Contains(template.ExternalValueId)) continue;
            if (mappedLocalValues.Contains(template.LocalValueId)) continue;
            if (dismissed.Contains((valueScope, template.LocalValueId))) continue;
            db.AttributeValueMappings.Add(new AttributeValueMapping
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenantId,
                ConnectionId = connectionId,
                SnapshotId = snapshot.Value,
                LocalId = template.LocalValueId,
                ScopeExternalId = valueScope,
                ExternalId = template.ExternalValueId,
                Status = "VERIFIED",
                VerifiedAt = now,
                Source = MappingSources.Template,
                Version = 1
            });
            mappedLocalValues.Add(template.LocalValueId);
            filled++;
        }
        return filled;
    }

    public static async Task DismissAsync(AppDbContext db, Guid tenantId, Guid connectionId, string kind, string scope, Guid localId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (await db.MappingTemplateDismissals.AnyAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.Kind == kind && x.ScopeExternalId == scope && x.LocalId == localId, cancellationToken)) return;
        db.MappingTemplateDismissals.Add(new MappingTemplateDismissal { Id = Guid.CreateVersion7(), TenantId = tenantId, ConnectionId = connectionId, Kind = kind, ScopeExternalId = scope, LocalId = localId, CreatedAt = now });
    }

    public static Task ClearDismissalAsync(AppDbContext db, Guid tenantId, Guid connectionId, string kind, string scope, Guid localId, CancellationToken cancellationToken) =>
        db.MappingTemplateDismissals
            .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.Kind == kind && x.ScopeExternalId == scope && x.LocalId == localId)
            .ExecuteDeleteAsync(cancellationToken);

    private static async Task<Guid?> CurrentSnapshotAsync(AppDbContext db, Guid tenantId, Guid connectionId, string resourceType, string scope, CancellationToken cancellationToken) =>
        db.ReferenceSnapshots.Local
            .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ResourceType == resourceType && x.ScopeExternalId == scope && x.IsCurrent)
            .OrderByDescending(x => x.FetchedAt)
            .Select(x => (Guid?)x.Id)
            .FirstOrDefault()
        ?? await db.ReferenceSnapshots.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ResourceType == resourceType && x.ScopeExternalId == scope && x.IsCurrent)
            .OrderByDescending(x => x.FetchedAt)
            .Select(x => (Guid?)x.Id)
            .FirstOrDefaultAsync(cancellationToken);

    private static async Task<HashSet<(string Scope, Guid LocalId)>> DismissedAsync(AppDbContext db, Guid tenantId, Guid connectionId, string kind, string[] scopes, CancellationToken cancellationToken) =>
        (await db.MappingTemplateDismissals.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.Kind == kind && scopes.Contains(x.ScopeExternalId))
            .Select(x => new { x.ScopeExternalId, x.LocalId })
            .ToListAsync(cancellationToken))
        .Select(x => (x.ScopeExternalId, x.LocalId))
        .ToHashSet();

    // The panel category must list the attribute (the same record a manual attribute mapping
    // creates). An option group is only filled while the category has room for it.
    private static async Task<bool> EnsureRequirementsAsync(AppDbContext db, Guid tenantId, IReadOnlyList<Guid> panelCategoryIds, AttributeMappingTemplate template, ReferenceItem remote, CancellationToken cancellationToken)
    {
        foreach (var categoryId in panelCategoryIds)
        {
            var requirement = db.CategoryAttributeRequirements.Local.FirstOrDefault(x => x.TenantId == tenantId && x.CategoryId == categoryId && x.AttributeId == template.LocalAttributeId)
                ?? await db.CategoryAttributeRequirements.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.CategoryId == categoryId && x.AttributeId == template.LocalAttributeId, cancellationToken);
            if (requirement is not null) continue;
            if (template.Role == "OPTION")
            {
                var optionGroups = await db.CategoryAttributeRequirements.CountAsync(x => x.TenantId == tenantId && x.CategoryId == categoryId && x.Role == "OPTION", cancellationToken)
                    + db.CategoryAttributeRequirements.Local.Count(x => x.TenantId == tenantId && x.CategoryId == categoryId && x.Role == "OPTION" && db.Entry(x).State == EntityState.Added);
                if (optionGroups >= MaximumOptionGroups) return false;
            }
            db.CategoryAttributeRequirements.Add(new CategoryAttributeRequirement
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenantId,
                CategoryId = categoryId,
                AttributeId = template.LocalAttributeId,
                IsRequired = remote.IsRequired == true,
                AllowsCustomValue = remote.AllowsCustomValue == true,
                IsPanelScoped = true,
                Role = template.Role,
                DisplayOrder = remote.SortOrder ?? 0,
                Version = 1
            });
        }
        return true;
    }
}
