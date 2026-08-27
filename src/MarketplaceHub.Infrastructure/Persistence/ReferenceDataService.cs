using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

public sealed class ReferenceDataService(AppDbContext db, TimeProvider timeProvider) : IReferenceDataService
{
    public async Task<ServiceResult<ReferenceDataView>> ListAsync(Guid tenantId, Guid connectionId, string resourceType, string? parentExternalId, CancellationToken cancellationToken)
    {
        if (!await db.PlatformConnections.AnyAsync(x => x.TenantId == tenantId && x.Id == connectionId, cancellationToken)) return NotFound<ReferenceDataView>();
        var scope = string.IsNullOrWhiteSpace(parentExternalId) ? "" : parentExternalId.Trim();
        var snapshot = await db.ReferenceSnapshots.AsNoTracking().Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ResourceType == resourceType && x.ScopeExternalId == scope && x.IsCurrent).OrderByDescending(x => x.FetchedAt).Select(x => new { x.Id, x.ResourceType, x.FetchedAt }).FirstOrDefaultAsync(cancellationToken);
        if (snapshot is null) return ServiceResult<ReferenceDataView>.Fail("REFERENCE_SNAPSHOT_UNAVAILABLE", "Doğrulanmış güncel reference snapshot yok; canlı platform çağrısı yapılmadı.", 422);
        var query = db.ReferenceItems.AsNoTracking().Where(x => x.TenantId == tenantId && x.SnapshotId == snapshot.Id && x.ResourceType == resourceType);
        if (parentExternalId is not null) query = query.Where(x => x.ParentExternalId == parentExternalId);
        var rows = await query.OrderBy(x => x.SortOrder).ThenBy(x => x.Name).Select(x => new ReferenceItemView(x.ExternalId, x.ParentExternalId, x.Name, x.Path, x.Depth, x.IsLeaf, x.IsActive, x.IsRequired, x.AllowsCustomValue, x.AllowsMultipleValues)).ToListAsync(cancellationToken);
        return ServiceResult<ReferenceDataView>.Ok(new(snapshot.Id, snapshot.ResourceType, snapshot.FetchedAt, rows));
    }

    public async Task<ServiceResult<IReadOnlyList<CatalogMappingView>>> ListMappingsAsync(Guid tenantId, string mappingType, Guid connectionId, string? scopeExternalId, CancellationToken cancellationToken)
    {
        if (!await db.PlatformConnections.AsNoTracking().AnyAsync(x => x.TenantId == tenantId && x.Id == connectionId, cancellationToken)) return NotFound<IReadOnlyList<CatalogMappingView>>();
        var scope = string.IsNullOrWhiteSpace(scopeExternalId) ? "" : scopeExternalId.Trim();
        var query = Query(mappingType).AsNoTracking().Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId);
        // The mapping centre's read-only overview intentionally uses '*' to show
        // already saved category-scoped mappings before a category is selected.
        if (scope != "*") query = query.Where(x => x.ScopeExternalId == scope);
        var entities = await query.OrderBy(x => x.LocalId).ToListAsync(cancellationToken);
        return ServiceResult<IReadOnlyList<CatalogMappingView>>.Ok(entities.Select(Map).ToList());
    }

    public async Task<ServiceResult<CatalogMappingView?>> GetMappingAsync(Guid tenantId, string mappingType, Guid localId, Guid connectionId, string? scopeExternalId, CancellationToken cancellationToken)
    {
        var scope = string.IsNullOrWhiteSpace(scopeExternalId) ? "" : scopeExternalId.Trim();
        var mapping = await Query(mappingType).AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.LocalId == localId && x.ConnectionId == connectionId && x.ScopeExternalId == scope, cancellationToken);
        return ServiceResult<CatalogMappingView?>.Ok(mapping is null ? null : Map(mapping));
    }

    public async Task<ServiceResult<CatalogMappingView>> UpsertMappingAsync(Guid tenantId, string mappingType, Guid localId, long? expectedVersion, UpsertCatalogMappingCommand command, CancellationToken cancellationToken)
    {
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
        var mapping = await Query(mappingType).SingleOrDefaultAsync(x => x.TenantId == tenantId && x.LocalId == localId && x.ConnectionId == command.ConnectionId && x.ScopeExternalId == scope, cancellationToken);
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
        }
        mapping.SnapshotId = command.SnapshotId; mapping.ExternalId = command.ExternalId.Trim(); mapping.Status = command.Status.Trim(); mapping.VerifiedAt = timeProvider.GetUtcNow();
        if (mappingType == "attributes")
        {
            var role = NormalizeRequirementRole(command.Role);
            if (role is not ("ATTRIBUTE" or "OPTION")) return ServiceResult<CatalogMappingView>.Fail("MAPPING_ROLE_INVALID", "Özellik eşleme rolü ATTRIBUTE veya OPTION olmalıdır.", 422);

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
                        Role = role,
                        DisplayOrder = external.SortOrder ?? 0,
                        Version = 1
                    });
                }
                else if (requirement.Role != role || requirement.IsRequired != (external.IsRequired == true) || requirement.AllowsCustomValue != (external.AllowsCustomValue == true) || requirement.DisplayOrder != (external.SortOrder ?? requirement.DisplayOrder))
                {
                    requirement.Role = role;
                    requirement.IsRequired = external.IsRequired == true;
                    requirement.AllowsCustomValue = external.AllowsCustomValue == true;
                    requirement.DisplayOrder = external.SortOrder ?? requirement.DisplayOrder;
                    requirement.Version++;
                }
            }
        }
        await db.SaveChangesAsync(cancellationToken);
        return ServiceResult<CatalogMappingView>.Ok(Map(mapping));
    }

    public async Task<ServiceResult<bool>> DeleteMappingAsync(Guid tenantId, string mappingType, Guid localId, Guid connectionId, string? scopeExternalId, long expectedVersion, CancellationToken cancellationToken)
    {
        var scope = string.IsNullOrWhiteSpace(scopeExternalId) ? "" : scopeExternalId.Trim();
        var mapping = await Query(mappingType).SingleOrDefaultAsync(x => x.TenantId == tenantId && x.LocalId == localId && x.ConnectionId == connectionId && x.ScopeExternalId == scope, cancellationToken);
        if (mapping is null) return NotFound<bool>();
        if (mapping.Version != expectedVersion) return ServiceResult<bool>.Fail("CONCURRENCY_CONFLICT", $"Kayıt sürümü değişti; güncel sürüm v{mapping.Version}.", 412);
        db.Remove(mapping);
        await db.SaveChangesAsync(cancellationToken);
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

    private static CatalogMappingView Map(CatalogMapping value) => new(value.Id, value.ConnectionId, value.SnapshotId, value.LocalId, value.ScopeExternalId, value.ExternalId, value.Status, value.VerifiedAt, value.Version);
    private static string NormalizeRequirementRole(string? value) => string.Equals(value?.Trim(), "OPTION", StringComparison.OrdinalIgnoreCase) ? "OPTION" : string.Equals(value?.Trim(), "ATTRIBUTE", StringComparison.OrdinalIgnoreCase) ? "ATTRIBUTE" : value?.Trim().ToUpperInvariant() ?? "";
    private static ServiceResult<T> NotFound<T>() => ServiceResult<T>.Fail("RESOURCE_NOT_FOUND", "Kayıt bulunamadı.", 404);
}
