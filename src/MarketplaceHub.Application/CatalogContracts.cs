namespace MarketplaceHub.Application;

public sealed record ServiceError(string Code, string Message, int Status, IReadOnlyDictionary<string, string[]>? FieldErrors = null);
public sealed record ServiceResult<T>(T? Value, ServiceError? Error)
{
    public bool Succeeded => Error is null;
    public static ServiceResult<T> Ok(T value) => new(value, null);
    public static ServiceResult<T> Fail(string code, string message, int status, IReadOnlyDictionary<string, string[]>? fields = null) => new(default, new(code, message, status, fields));
}

public sealed record PageResult<T>(IReadOnlyList<T> Items, string? NextCursor, bool HasMore);
public sealed record CategoryView(Guid Id, Guid? ParentId, string Name, string Path, int Depth, bool IsLeaf, bool IsActive, long Version);
public sealed record BrandView(Guid Id, string Name, bool IsActive, long Version);
public sealed record AttributeValueView(Guid Id, string Value, int SortOrder, bool IsActive);
public sealed record AttributeView(Guid Id, string Code, string Name, string DataType, string? SelectionMode, string? Unit, bool IsActive, long Version, IReadOnlyList<AttributeValueView> Values, IReadOnlyList<string>? Roles = null);
public sealed record ProductVariantView(
    Guid Id,
    string Sku,
    string? Barcode,
    string? ModelCode,
    string OptionSignature,
    string Status,
    long Version,
    decimal? Weight = null,
    decimal? Width = null,
    decimal? Height = null,
    decimal? Length = null,
    decimal? Desi = null,
    decimal OnHand = 0,
    decimal Available = 0,
    long? InventoryVersion = null,
    Guid? OfferId = null,
    decimal? ListPrice = null,
    decimal? SalePrice = null,
    string? Currency = null,
    string? OfferStatus = null,
    long? PriceVersion = null,
    long? OfferVersion = null,
    decimal? VatRate = null,
    string? VatInclusion = null,
    string? RoundingMode = null,
    decimal? SafetyStock = null,
    IReadOnlyList<string>? MediaUrls = null);
public sealed record ProductAttributeAssignmentView(Guid AttributeId, Guid? ValueId, string? TextValue, decimal? NumberValue, bool? BooleanValue, int SortOrder);
public sealed record ProductOptionValueView(Guid Id, string Label);
public sealed record ProductOptionView(Guid Id, string Label, IReadOnlyList<ProductOptionValueView> Values);
public sealed record ProductView(
    Guid Id,
    string Title,
    string Description,
    Guid? BrandId,
    Guid? CategoryId,
    string Status,
    DateTimeOffset UpdatedAt,
    long Version,
    IReadOnlyList<ProductVariantView> Variants,
    string? PrimaryImageUrl = null,
    decimal TotalStock = 0,
    decimal? StartingPrice = null,
    string Currency = "TRY",
    string? ModelCode = null,
    IReadOnlyList<string>? ActivePlatforms = null,
    IReadOnlyList<ProductAttributeAssignmentView>? Attributes = null,
    IReadOnlyList<ProductOptionView>? Options = null,
    IReadOnlyList<string>? MediaUrls = null);
public sealed record ListingProfileView(Guid Id, Guid ProductId, Guid ConnectionId, string? TitleOverride, string? DescriptionOverride, string? ExternalCategoryId, string? ExternalBrandId, int? DeliveryTimeDays, bool Enabled, string DesiredStatus, string ActualStatus, long Version);
public sealed record PublicationLineView(Guid VariantId, string Sku, string? Barcode, string DesiredStatus, string ActualStatus, string? RejectionCode);
public sealed record PublicationStatusView(Guid ProductId, Guid ConnectionId, Guid? ProfileId, string? DesiredStatus, string? ActualStatus, string? LastRejectionCode, Guid? LastJobId, string? LastJobStatus, IReadOnlyList<PublicationLineView> Lines);

public sealed record CreateCategoryCommand(string Name, Guid? ParentId);
public sealed record UpdateCategoryCommand(string Name, Guid? ParentId, bool IsActive);
public sealed record CreateBrandCommand(string Name);
public sealed record UpdateBrandCommand(string Name, bool IsActive);
public sealed record CreateAttributeValueCommand(string Value, int SortOrder);
public sealed record CreateAttributeCommand(string Code, string Name, string DataType, string? SelectionMode, string? Unit, IReadOnlyList<CreateAttributeValueCommand> Values);
public sealed record AttributeRequirementCommand(Guid AttributeId, bool IsRequired, bool AllowsCustomValue, int DisplayOrder, string Role = "ATTRIBUTE");
public sealed record CategoryAttributeRequirementView(Guid AttributeId, bool IsRequired, bool AllowsCustomValue, int DisplayOrder, AttributeView Attribute, string Role = "ATTRIBUTE");
public sealed record CreateVariantCommand(string Sku, string? Barcode, string? ModelCode, IReadOnlyDictionary<string, string>? Options = null, decimal? Weight = null, decimal? Width = null, decimal? Height = null, decimal? Length = null, decimal? Desi = null, IReadOnlyList<ProductAttributeCommand>? Attributes = null);
public sealed record UpdateVariantCommand(Guid Id, string Sku, string? Barcode, string? ModelCode);
public sealed record ProductAttributeCommand(Guid AttributeId, Guid? ValueId, string? TextValue, decimal? NumberValue, bool? BooleanValue, int SortOrder);
public sealed record CreateProductCommand(string Title, string Description, Guid? BrandId, Guid? CategoryId, IReadOnlyList<CreateVariantCommand> Variants, IReadOnlyList<ProductAttributeCommand>? Attributes = null, string? Status = null);
// Variant ekleme append-only'dir: envanter veya dış listeye bağlı mevcut satış satırları sessizce silinmez.
public sealed record UpdateProductCommand(string Title, string Description, Guid? BrandId, Guid? CategoryId, IReadOnlyList<ProductAttributeCommand>? Attributes = null, IReadOnlyList<CreateVariantCommand>? VariantsToCreate = null, IReadOnlyList<UpdateVariantCommand>? VariantUpdates = null, string? Status = null);
public sealed record UpsertListingProfileCommand(string? TitleOverride, string? DescriptionOverride, string? ExternalCategoryId, string? ExternalBrandId, int? DeliveryTimeDays, bool Enabled);

public interface ICatalogService
{
    Task<PageResult<CategoryView>> ListCategoriesAsync(Guid tenantId, int limit, string? after, CancellationToken cancellationToken);
    Task<ServiceResult<CategoryView>> GetCategoryAsync(Guid tenantId, Guid id, CancellationToken cancellationToken);
    Task<ServiceResult<CategoryView>> CreateCategoryAsync(Guid tenantId, CreateCategoryCommand command, CancellationToken cancellationToken);
    Task<ServiceResult<CategoryView>> UpdateCategoryAsync(Guid tenantId, Guid id, long expectedVersion, UpdateCategoryCommand command, CancellationToken cancellationToken);
    Task<PageResult<BrandView>> ListBrandsAsync(Guid tenantId, int limit, string? after, CancellationToken cancellationToken);
    Task<ServiceResult<BrandView>> GetBrandAsync(Guid tenantId, Guid id, CancellationToken cancellationToken);
    Task<ServiceResult<BrandView>> CreateBrandAsync(Guid tenantId, CreateBrandCommand command, CancellationToken cancellationToken);
    Task<ServiceResult<BrandView>> UpdateBrandAsync(Guid tenantId, Guid id, long expectedVersion, UpdateBrandCommand command, CancellationToken cancellationToken);
    Task<PageResult<AttributeView>> ListAttributesAsync(Guid tenantId, int limit, string? after, CancellationToken cancellationToken);
    Task<ServiceResult<AttributeView>> CreateAttributeAsync(Guid tenantId, CreateAttributeCommand command, CancellationToken cancellationToken);
    Task<ServiceResult<AttributeView>> DeactivateAttributeAsync(Guid tenantId, Guid attributeId, long expectedVersion, CancellationToken cancellationToken);
    Task<ServiceResult<AttributeView>> AddAttributeValuesAsync(Guid tenantId, Guid attributeId, IReadOnlyList<CreateAttributeValueCommand> values, CancellationToken cancellationToken);
    Task<ServiceResult<AttributeView>> DeactivateAttributeValueAsync(Guid tenantId, Guid attributeId, Guid valueId, CancellationToken cancellationToken);
    Task<ServiceResult<IReadOnlyList<CategoryAttributeRequirementView>>> GetRequirementsAsync(Guid tenantId, Guid categoryId, CancellationToken cancellationToken);
    Task<ServiceResult<IReadOnlyList<AttributeRequirementCommand>>> ReplaceRequirementsAsync(Guid tenantId, Guid categoryId, long expectedVersion, IReadOnlyList<AttributeRequirementCommand> requirements, CancellationToken cancellationToken);
    Task<PageResult<ProductView>> ListProductsAsync(Guid tenantId, int limit, string? after, string? status, CancellationToken cancellationToken);
    Task<ServiceResult<ProductView>> CreateProductAsync(Guid tenantId, CreateProductCommand command, CancellationToken cancellationToken);
    Task<ServiceResult<ProductView>> GetProductAsync(Guid tenantId, Guid id, CancellationToken cancellationToken);
    Task<ServiceResult<ProductView>> UpdateProductAsync(Guid tenantId, Guid id, long expectedVersion, UpdateProductCommand command, CancellationToken cancellationToken);
    Task<ServiceResult<ProductView>> ArchiveProductAsync(Guid tenantId, Guid id, long expectedVersion, CancellationToken cancellationToken);
    Task<ServiceResult<ListingProfileView>> GetListingProfileAsync(Guid tenantId, Guid productId, Guid connectionId, CancellationToken cancellationToken);
    Task<ServiceResult<ListingProfileView>> UpsertListingProfileAsync(Guid tenantId, Guid productId, Guid connectionId, long? expectedVersion, UpsertListingProfileCommand command, CancellationToken cancellationToken);
    Task<ServiceResult<Guid>> EnqueuePublicationAsync(Guid tenantId, Guid productId, Guid connectionId, string idempotencyKey, string correlationId, CancellationToken cancellationToken);
    Task<ServiceResult<Guid>> EnqueueProductUpdateAsync(Guid tenantId, Guid productId, Guid connectionId, string idempotencyKey, string correlationId, CancellationToken cancellationToken);
    Task<ServiceResult<Guid>> EnqueueProductArchiveAsync(Guid tenantId, Guid productId, Guid connectionId, bool archived, string idempotencyKey, string correlationId, CancellationToken cancellationToken);
    Task<ServiceResult<PublicationStatusView>> GetPublicationStatusAsync(Guid tenantId, Guid productId, Guid connectionId, CancellationToken cancellationToken);
}

public sealed record ImportSessionView(Guid Id, string SourceType, string Status, Guid? SourceAssetId, int TotalRows, int ValidRows, int ErrorRows, int ReviewRows, DateTimeOffset UpdatedAt, long Version);
public sealed record ImportCandidateView(Guid Id, Guid StagingRecordId, Guid? ProductId, Guid? VariantId, string MatchRule, string Status, string SafeSummary, long Version);
public sealed record CreateImportCommand(string SourceType, Guid? ConnectionId);
public sealed record ColumnMappingEntry(string SourceColumn, string TargetField, int SortOrder);
public sealed record UpdateColumnMappingCommand(string ProfileName, string? VariantGroupKey, IReadOnlyList<ColumnMappingEntry> Mappings);
public sealed record ImportDecisionCommand(string Decision, Guid? ProductId, Guid? VariantId);
public sealed record ImportUpload(string FileName, string ContentType, Stream Content, long Length);

public interface IImportService
{
    Task<PageResult<ImportSessionView>> ListAsync(Guid tenantId, int limit, string? after, CancellationToken cancellationToken);
    Task<ServiceResult<ImportSessionView>> CreateAsync(Guid tenantId, CreateImportCommand command, CancellationToken cancellationToken);
    Task<ServiceResult<ImportSessionView>> AttachSourceAsync(Guid tenantId, Guid sessionId, ImportUpload upload, CancellationToken cancellationToken);
    Task<ServiceResult<ImportSessionView>> ConfigureColumnsAsync(Guid tenantId, Guid sessionId, long expectedVersion, UpdateColumnMappingCommand command, CancellationToken cancellationToken);
    Task<ServiceResult<Guid>> EnqueuePreviewAsync(Guid tenantId, Guid sessionId, string correlationId, CancellationToken cancellationToken);
    Task<ServiceResult<ImportSessionView>> GetAsync(Guid tenantId, Guid sessionId, CancellationToken cancellationToken);
    Task<PageResult<ImportCandidateView>> CandidatesAsync(Guid tenantId, Guid sessionId, int limit, string? after, CancellationToken cancellationToken);
    Task<ServiceResult<ImportCandidateView>> DecideAsync(Guid tenantId, Guid userId, Guid sessionId, Guid candidateId, long expectedVersion, ImportDecisionCommand command, CancellationToken cancellationToken);
    Task<ServiceResult<Guid>> EnqueueApplyAsync(Guid tenantId, Guid sessionId, string correlationId, CancellationToken cancellationToken);
    Task<ServiceResult<string>> BuildErrorsCsvAsync(Guid tenantId, Guid sessionId, CancellationToken cancellationToken);
}

public sealed record InventoryItemView(Guid Id, Guid VariantId, string Sku, string LocationCode, decimal OnHand, decimal Reserved, decimal Available, long ProjectionVersion, DateTimeOffset? ReconciledAt, long Version);
public sealed record LedgerEntryView(Guid Id, string MovementType, decimal QuantityDelta, string SourceType, string SourceId, DateTimeOffset OccurredAt, string CorrelationId);
public sealed record StockAdjustmentCommand(decimal QuantityDelta, string Reason, string SourceEventId);
public sealed record ChannelOfferView(Guid Id, Guid ConnectionId, Guid VariantId, decimal ListPrice, decimal SalePrice, string Currency, decimal VatRate, string VatInclusion, string RoundingMode, decimal SafetyStock, string Status, long PriceVersion, long Version);
public sealed record UpsertChannelOfferCommand(Guid ConnectionId, Guid VariantId, decimal ListPrice, decimal SalePrice, string Currency, decimal VatRate, string VatInclusion, string RoundingMode, decimal SafetyStock, string Status, string Reason);
public sealed record UpdateChannelOfferCommand(decimal ListPrice, decimal SalePrice, string Currency, decimal VatRate, string VatInclusion, string RoundingMode, decimal SafetyStock, string Status, string Reason);

public interface IInventoryService
{
    Task<PageResult<InventoryItemView>> ListAsync(Guid tenantId, int limit, string? after, CancellationToken cancellationToken);
    Task<PageResult<LedgerEntryView>> LedgerAsync(Guid tenantId, Guid variantId, int limit, string? after, CancellationToken cancellationToken);
    Task<ServiceResult<InventoryItemView>> AdjustAsync(Guid tenantId, Guid userId, Guid variantId, StockAdjustmentCommand command, string idempotencyKey, string correlationId, CancellationToken cancellationToken);
    Task<ServiceResult<ChannelOfferView>> UpsertOfferAsync(Guid tenantId, Guid userId, UpsertChannelOfferCommand command, CancellationToken cancellationToken);
    Task<ServiceResult<ChannelOfferView>> GetOfferAsync(Guid tenantId, Guid id, CancellationToken cancellationToken);
    Task<ServiceResult<ChannelOfferView>> UpdateOfferAsync(Guid tenantId, Guid userId, Guid id, long expectedVersion, UpdateChannelOfferCommand command, CancellationToken cancellationToken);
    Task<ServiceResult<Guid>> ValidateExternalSyncAsync(Guid tenantId, string operation, CancellationToken cancellationToken);
    Task<ServiceResult<Guid>> EnqueuePriceInventorySyncAsync(Guid tenantId, Guid connectionId, string idempotencyKey, string correlationId, CancellationToken cancellationToken);
}

public interface IImportJobProcessor
{
    Task<bool> ProcessAsync(Guid tenantId, Guid sessionId, string operation, CancellationToken cancellationToken);
}

public sealed record ReferenceItemView(string ExternalId, string? ParentExternalId, string Name, string Path, int Depth, bool IsLeaf, bool IsActive, bool? IsRequired, bool? AllowsCustomValue, bool? AllowsMultipleValues);
public sealed record ReferenceDataView(Guid SnapshotId, string ResourceType, DateTimeOffset FetchedAt, IReadOnlyList<ReferenceItemView> Items);
public sealed record CatalogMappingView(Guid Id, Guid ConnectionId, Guid SnapshotId, Guid LocalId, string ScopeExternalId, string ExternalId, string Status, DateTimeOffset? VerifiedAt, long Version);
public sealed record UpsertCatalogMappingCommand(Guid ConnectionId, Guid SnapshotId, string ExternalId, string Status, string Role = "ATTRIBUTE");

public interface IReferenceDataService
{
    Task<ServiceResult<ReferenceDataView>> ListAsync(Guid tenantId, Guid connectionId, string resourceType, string? parentExternalId, CancellationToken cancellationToken);
    Task<ServiceResult<IReadOnlyList<CatalogMappingView>>> ListMappingsAsync(Guid tenantId, string mappingType, Guid connectionId, string? scopeExternalId, CancellationToken cancellationToken);
    Task<ServiceResult<CatalogMappingView?>> GetMappingAsync(Guid tenantId, string mappingType, Guid localId, Guid connectionId, string? scopeExternalId, CancellationToken cancellationToken);
    Task<ServiceResult<CatalogMappingView>> UpsertMappingAsync(Guid tenantId, string mappingType, Guid localId, long? expectedVersion, UpsertCatalogMappingCommand command, CancellationToken cancellationToken);
    Task<ServiceResult<bool>> DeleteMappingAsync(Guid tenantId, string mappingType, Guid localId, Guid connectionId, string? scopeExternalId, long expectedVersion, CancellationToken cancellationToken);
}
