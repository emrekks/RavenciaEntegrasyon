using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

public sealed class ReferenceDataService(AppDbContext db, TimeProvider timeProvider) : IReferenceDataService
{
    public async Task<ServiceResult<IReadOnlyList<ReferenceItemView>>> ListAsync(Guid tenantId, Guid connectionId, string resourceType, string? parentExternalId, CancellationToken cancellationToken)
    {
        if (!await db.PlatformConnections.AnyAsync(x => x.TenantId == tenantId && x.Id == connectionId, cancellationToken)) return NotFound<IReadOnlyList<ReferenceItemView>>();
        var snapshotId = await db.ReferenceSnapshots.AsNoTracking().Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ResourceType == resourceType && x.IsCurrent).OrderByDescending(x => x.FetchedAt).Select(x => (Guid?)x.Id).FirstOrDefaultAsync(cancellationToken);
        if (snapshotId is null) return ServiceResult<IReadOnlyList<ReferenceItemView>>.Fail("REFERENCE_SNAPSHOT_UNAVAILABLE", "Doğrulanmış güncel reference snapshot yok; canlı platform çağrısı yapılmadı.", 422);
        var query = db.ReferenceItems.AsNoTracking().Where(x => x.TenantId == tenantId && x.SnapshotId == snapshotId && x.ResourceType == resourceType);
        if (parentExternalId is not null) query = query.Where(x => x.ParentExternalId == parentExternalId);
        var rows = await query.OrderBy(x => x.SortOrder).ThenBy(x => x.Name).Select(x => new ReferenceItemView(x.ExternalId, x.ParentExternalId, x.Name, x.Path, x.Depth, x.IsLeaf, x.IsActive)).ToListAsync(cancellationToken);
        return ServiceResult<IReadOnlyList<ReferenceItemView>>.Ok(rows);
    }

    public async Task<ServiceResult<CatalogMappingView>> GetMappingAsync(Guid tenantId, string mappingType, Guid localId, Guid connectionId, CancellationToken cancellationToken)
    {
        var mapping = await Query(mappingType).AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.LocalId == localId && x.ConnectionId == connectionId, cancellationToken);
        return mapping is null ? NotFound<CatalogMappingView>() : ServiceResult<CatalogMappingView>.Ok(Map(mapping));
    }

    public async Task<ServiceResult<CatalogMappingView>> UpsertMappingAsync(Guid tenantId, string mappingType, Guid localId, long? expectedVersion, UpsertCatalogMappingCommand command, CancellationToken cancellationToken)
    {
        if (!await LocalExistsAsync(tenantId, mappingType, localId, cancellationToken)) return NotFound<CatalogMappingView>();
        var snapshot = await db.ReferenceSnapshots.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ConnectionId == command.ConnectionId && x.Id == command.SnapshotId && x.IsCurrent, cancellationToken);
        if (snapshot is null) return ServiceResult<CatalogMappingView>.Fail("REFERENCE_SNAPSHOT_UNAVAILABLE", "Mapping güncel ve aynı tenant/connection reference snapshot ister.", 422);
        var external = await db.ReferenceItems.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.SnapshotId == command.SnapshotId && x.ExternalId == command.ExternalId, cancellationToken);
        if (external is null) return ServiceResult<CatalogMappingView>.Fail("EXTERNAL_REFERENCE_NOT_FOUND", "External reference kimliği snapshot içinde bulunamadı.", 422);
        if (mappingType == "categories" && !external.IsLeaf) return ServiceResult<CatalogMappingView>.Fail("NON_LEAF_CATEGORY_REJECTED", "Ürün mapping'i yalnız leaf kategoriye yapılabilir.", 422);

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
        "categories" => db.Categories.AnyAsync(x => x.TenantId == tenantId && x.Id == localId, cancellationToken),
        "brands" => db.Brands.AnyAsync(x => x.TenantId == tenantId && x.Id == localId, cancellationToken),
        "attributes" => db.AttributeDefinitions.AnyAsync(x => x.TenantId == tenantId && x.Id == localId, cancellationToken),
        "attribute-values" => db.AttributeValues.AnyAsync(x => x.TenantId == tenantId && x.Id == localId, cancellationToken),
        _ => Task.FromResult(false)
    };

    private static CatalogMappingView Map(CatalogMapping value) => new(value.Id, value.ConnectionId, value.SnapshotId, value.LocalId, value.ExternalId, value.Status, value.VerifiedAt, value.Version);
    private static ServiceResult<T> NotFound<T>() => ServiceResult<T>.Fail("RESOURCE_NOT_FOUND", "Kayıt bulunamadı.", 404);
}
