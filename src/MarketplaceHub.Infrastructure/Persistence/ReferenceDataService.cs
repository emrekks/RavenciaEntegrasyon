using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

public sealed class ReferenceDataService(AppDbContext db, TimeProvider timeProvider) : IReferenceDataService
{
    public async Task<ServiceResult<ReferenceDataView>> ListAsync(Guid tenantId, Guid connectionId, string resourceType, string? parentExternalId, CancellationToken cancellationToken)
    {
        if (!await db.PlatformConnections.AnyAsync(x => x.TenantId == tenantId && x.Id == connectionId, cancellationToken)) return NotFound<ReferenceDataView>();
        var snapshot = await db.ReferenceSnapshots.AsNoTracking().Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ResourceType == resourceType && x.IsCurrent).OrderByDescending(x => x.FetchedAt).Select(x => new { x.Id, x.ResourceType, x.FetchedAt }).FirstOrDefaultAsync(cancellationToken);
        if (snapshot is null) return ServiceResult<ReferenceDataView>.Fail("REFERENCE_SNAPSHOT_UNAVAILABLE", "Doğrulanmış güncel reference snapshot yok; canlı platform çağrısı yapılmadı.", 422);
        var query = db.ReferenceItems.AsNoTracking().Where(x => x.TenantId == tenantId && x.SnapshotId == snapshot.Id && x.ResourceType == resourceType);
        if (parentExternalId is not null) query = query.Where(x => x.ParentExternalId == parentExternalId);
        var rows = await query.OrderBy(x => x.SortOrder).ThenBy(x => x.Name).Select(x => new ReferenceItemView(x.ExternalId, x.ParentExternalId, x.Name, x.Path, x.Depth, x.IsLeaf, x.IsActive)).ToListAsync(cancellationToken);
        return ServiceResult<ReferenceDataView>.Ok(new(snapshot.Id, snapshot.ResourceType, snapshot.FetchedAt, rows));
    }

    public async Task<ServiceResult<CatalogMappingView?>> GetMappingAsync(Guid tenantId, string mappingType, Guid localId, Guid connectionId, CancellationToken cancellationToken)
    {
        var mapping = await Query(mappingType).AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.LocalId == localId && x.ConnectionId == connectionId, cancellationToken);
        return ServiceResult<CatalogMappingView?>.Ok(mapping is null ? null : Map(mapping));
    }

    public async Task<ServiceResult<CatalogMappingView>> UpsertMappingAsync(Guid tenantId, string mappingType, Guid localId, long? expectedVersion, UpsertCatalogMappingCommand command, CancellationToken cancellationToken)
    {
        if (!await LocalExistsAsync(tenantId, mappingType, localId, cancellationToken)) return NotFound<CatalogMappingView>();
        if (!string.Equals(command.Status, "VERIFIED", StringComparison.Ordinal)) return ServiceResult<CatalogMappingView>.Fail("MAPPING_STATUS_INVALID", "Panel üzerinden yalnız VERIFIED mapping kaydedilebilir.", 422);
        var snapshot = await db.ReferenceSnapshots.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ConnectionId == command.ConnectionId && x.Id == command.SnapshotId && x.IsCurrent, cancellationToken);
        if (snapshot is null) return ServiceResult<CatalogMappingView>.Fail("REFERENCE_SNAPSHOT_UNAVAILABLE", "Mapping güncel ve aynı tenant/connection reference snapshot ister.", 422);
        var expectedResource = mappingType switch { "categories" => "CATEGORIES", "brands" => "BRAND", "attributes" => "ATTRIBUTE", "attribute-values" => "ATTRIBUTE_VALUE", _ => "" };
        if (!string.Equals(snapshot.ResourceType, expectedResource, StringComparison.Ordinal)) return ServiceResult<CatalogMappingView>.Fail("REFERENCE_RESOURCE_MISMATCH", "Snapshot mapping türüyle eşleşmiyor.", 422);
        var external = await db.ReferenceItems.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.SnapshotId == command.SnapshotId && x.ResourceType == expectedResource && x.ExternalId == command.ExternalId, cancellationToken);
        if (external is null) return ServiceResult<CatalogMappingView>.Fail("EXTERNAL_REFERENCE_NOT_FOUND", "External reference kimliği snapshot içinde bulunamadı.", 422);
        if (mappingType == "categories" && (!external.IsLeaf || !external.IsActive)) return ServiceResult<CatalogMappingView>.Fail("ACTIVE_LEAF_CATEGORY_REQUIRED", "Ürün mapping'i yalnız etkin leaf kategoriye yapılabilir.", 422);

        var mapping = await Query(mappingType).SingleOrDefaultAsync(x => x.TenantId == tenantId && x.LocalId == localId && x.ConnectionId == command.ConnectionId, cancellationToken);
        if (mapping is null)
        {
            mapping = New(mappingType);
            mapping.Id = Guid.CreateVersion7(); mapping.TenantId = tenantId; mapping.LocalId = localId; mapping.ConnectionId = command.ConnectionId;
            db.Add(mapping);
        }
        else
        {
            if (expectedVersion is null) return ServiceResult<CatalogMappingView>.Fail("PRECONDITION_REQUIRED", "If-Match gereklidir.", 428);
            if (mapping.Version != expectedVersion) return ServiceResult<CatalogMappingView>.Fail("CONCURRENCY_CONFLICT", $"Kayıt sürümü değişti; güncel sürüm v{mapping.Version}.", 412);
            mapping.Version++;
        }
        mapping.SnapshotId = command.SnapshotId; mapping.ExternalId = command.ExternalId.Trim(); mapping.Status = command.Status.Trim(); mapping.VerifiedAt = timeProvider.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
        return ServiceResult<CatalogMappingView>.Ok(Map(mapping));
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
        "brands" => db.Brands.AnyAsync(x => x.TenantId == tenantId && x.Id == localId, cancellationToken),
        "attributes" => db.AttributeDefinitions.AnyAsync(x => x.TenantId == tenantId && x.Id == localId, cancellationToken),
        "attribute-values" => db.AttributeValues.AnyAsync(x => x.TenantId == tenantId && x.Id == localId, cancellationToken),
        _ => Task.FromResult(false)
    };

    private static CatalogMappingView Map(CatalogMapping value) => new(value.Id, value.ConnectionId, value.SnapshotId, value.LocalId, value.ExternalId, value.Status, value.VerifiedAt, value.Version);
    private static ServiceResult<T> NotFound<T>() => ServiceResult<T>.Fail("RESOURCE_NOT_FOUND", "Kayıt bulunamadı.", 404);
}
