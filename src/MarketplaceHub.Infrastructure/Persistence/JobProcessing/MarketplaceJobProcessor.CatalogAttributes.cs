using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using CategoryAttributeContext = MarketplaceHub.Infrastructure.Persistence.CatalogAttributeService.CategoryAttributeContext;
using LocalCategoryAttribute = MarketplaceHub.Infrastructure.Persistence.CatalogAttributeService.LocalCategoryAttribute;

namespace MarketplaceHub.Infrastructure.Persistence;

// ISLEYICI-9 stage 3d step 1 (16 Nov 2026): the catalog attribute machinery lives in CatalogAttributeService, one per run,
// shared by the product import and the catalog structure import; the processor's methods of the same names call into it.
// The service reads references through this processor's SyncReferences, handed in as a delegate.
public sealed partial class MarketplaceJobProcessor
{
    private CatalogAttributeService? catalogAttributes;

    private CatalogAttributeService CatalogAttributes =>
        catalogAttributes is { } current && ReferenceEquals(current.Run, run)
            ? current
            : catalogAttributes = new CatalogAttributeService(db, timeProvider, SyncReferences, run);

    private Task<ReferenceSnapshot?> EnsureReferenceSnapshot(Guid tenantId, Guid connectionId, string resourceType, string? parentExternalId, string correlationId, CancellationToken cancellationToken) =>
        CatalogAttributes.EnsureReferenceSnapshot(tenantId, connectionId, resourceType, parentExternalId, correlationId, cancellationToken);

    private Task<CategoryAttributeContext?> EnsureCategoryAttributeContext(Guid tenantId, Guid connectionId, ReferenceSnapshot categorySnapshot, RemoteCatalogProduct snapshot, IReadOnlyList<ReferenceItem> categoryItems, IDictionary<string, AttributeDefinition> importedAttributeLibrary, IDictionary<string, CategoryAttributeContext> cache, string correlationId, CancellationToken cancellationToken) =>
        CatalogAttributes.EnsureCategoryAttributeContext(tenantId, connectionId, categorySnapshot, snapshot, categoryItems, importedAttributeLibrary, cache, correlationId, cancellationToken);

    private Task EnsureObservedVariantAttributeValues(Guid tenantId, CategoryAttributeContext categoryContext, RemoteCatalogProduct snapshot, CancellationToken cancellationToken) =>
        CatalogAttributes.EnsureObservedVariantAttributeValues(tenantId, categoryContext, snapshot, cancellationToken);

    private Task<LocalCategoryAttribute> EnsureMappedAttribute(Guid tenantId, Guid connectionId, Category category, ReferenceItem remoteCategory, ReferenceSnapshot attributeSnapshot, ReferenceItem remoteAttribute, IReadOnlyList<ReferenceItem> values, IDictionary<string, AttributeDefinition> importedAttributeLibrary, CancellationToken cancellationToken, string codePrefix = "TRD") =>
        CatalogAttributes.EnsureMappedAttribute(tenantId, connectionId, category, remoteCategory, attributeSnapshot, remoteAttribute, values, importedAttributeLibrary, cancellationToken, codePrefix);

    private Task EnsureExactWebColorValueMappings(Guid tenantId, Guid connectionId, string categoryExternalId, AttributeDefinition localAttribute, ReferenceItem remoteAttribute, IReadOnlyList<ReferenceItem> remoteValues, CategoryAttributeContext categoryContext, CancellationToken cancellationToken) =>
        CatalogAttributes.EnsureExactWebColorValueMappings(tenantId, connectionId, categoryExternalId, localAttribute, remoteAttribute, remoteValues, categoryContext, cancellationToken);

    private Task EnsureExactWebColorValueMappings(Guid tenantId, Guid connectionId, CategoryAttributeContext categoryContext, CancellationToken cancellationToken) =>
        CatalogAttributes.EnsureExactWebColorValueMappings(tenantId, connectionId, categoryContext, cancellationToken);

    private Task UpsertProductAttributeAssignments(Guid tenantId, Guid connectionId, Product product, ProductVariant variant, IReadOnlyDictionary<string, string> options, CategoryAttributeContext categoryContext, CancellationToken cancellationToken) =>
        CatalogAttributes.UpsertProductAttributeAssignments(tenantId, connectionId, product, variant, options, categoryContext, cancellationToken);

    private Task PromoteCommonImportedAttributes(Guid tenantId, Product product, IReadOnlyList<ProductVariant> importedVariants, CategoryAttributeContext categoryContext, CancellationToken cancellationToken) =>
        CatalogAttributes.PromoteCommonImportedAttributes(tenantId, product, importedVariants, categoryContext, cancellationToken);

    private Task<AttributeValue?> ResolveMappedAttributeValue(Guid tenantId, Guid connectionId, CategoryAttributeContext categoryContext, LocalCategoryAttribute mapped, ReferenceItem? remoteValue, string remoteValueText, CancellationToken cancellationToken) =>
        CatalogAttributes.ResolveMappedAttributeValue(tenantId, connectionId, categoryContext, mapped, remoteValue, remoteValueText, cancellationToken);
}
