namespace MarketplaceHub.Application;

public sealed record ServiceError(string Code, string Message, int Status, IReadOnlyDictionary<string, string[]>? FieldErrors = null);
public sealed record ServiceResult<T>(T? Value, ServiceError? Error)
{
    public bool Succeeded => Error is null;
    public static ServiceResult<T> Ok(T value) => new(value, null);
    public static ServiceResult<T> Fail(string code, string message, int status, IReadOnlyDictionary<string, string[]>? fields = null) => new(default, new(code, message, status, fields));
}

// SupportedFilters names the optional filters the endpoint applies itself, so a client can stop filtering locally.
public sealed record PageResult<T>(IReadOnlyList<T> Items, string? NextCursor, bool HasMore, int? TotalCount = null, IReadOnlyList<string>? SupportedFilters = null);
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
    decimal? CostPrice = null,
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
    IReadOnlyList<string>? MediaUrls = null,
    IReadOnlyDictionary<string, string>? Options = null,
    IReadOnlyList<ProductVariantPlatformStatusView>? PlatformStatuses = null,
    decimal? DefaultListPrice = null,
    decimal? DefaultSalePrice = null,
    IReadOnlyList<ProductAttributeAssignmentView>? Attributes = null,
    // The product card the variant belongs to (a family view lists every colour card's variants).
    Guid? ProductId = null);
public sealed record ProductAttributeAssignmentView(Guid AttributeId, Guid? ValueId, string? TextValue, decimal? NumberValue, bool? BooleanValue, int SortOrder);
public sealed record ProductOptionValueView(Guid Id, string Label);
public sealed record ProductOptionView(Guid Id, string Label, IReadOnlyList<ProductOptionValueView> Values);
public sealed record ProductVariantPlatformStatusView(
    string Platform,
    string PlatformCode,
    bool IsLinked,
    string Status = "UNLINKED",
    Guid? ConnectionId = null,
    Guid? OfferId = null,
    decimal? ListPrice = null,
    decimal? SalePrice = null,
    string? Currency = null,
    decimal? VatRate = null,
    string? VatInclusion = null,
    string? RoundingMode = null,
    decimal? SafetyStock = null,
    long? OfferVersion = null,
    // Matched to a listing the marketplace holds (a variant link). IsLinked also counts a variant only queued for
    // publication there, which may never have arrived.
    bool IsMatched = false);
public sealed record ProductPlatformStatusView(
    string Platform,
    string Status,
    int MatchedVariantCount = 0,
    int VariantCount = 0,
    bool IsChecking = false,
    string PlatformCode = "TRENDYOL");
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
    IReadOnlyList<string>? MediaUrls = null,
    IReadOnlyList<string>? FamilyMediaUrls = null,
    IReadOnlyList<ProductPlatformStatusView>? PlatformStatuses = null,
    string? CategoryPath = null,
    IReadOnlyList<ProductFamilyMediaView>? FamilyMediaItems = null,
    bool HasCustomMediaOrder = false,
    decimal? DefaultListPrice = null,
    decimal? DefaultSalePrice = null,
    int? WarrantyMonths = null,
    Guid? TrendyolWebColorValueId = null)
{
    public IReadOnlyList<string>? FamilyOrderedMediaUrls { get; init; }
    // Every product card of the family (same brand and model code: one per colour), the opened one first.
    public IReadOnlyList<Guid>? FamilyProductIds { get; init; }
}
public sealed record ProductFamilyMediaView(string Url, IReadOnlyList<Guid> MediaIds, IReadOnlyList<string> SourceProductTitles);
public sealed record ProductSummaryView(
    int TotalCount,
    int ActiveCount,
    int OutOfStockCount,
    int LowStockCount,
    IReadOnlyList<string> Platforms);
public sealed record ListingProfileView(Guid Id, Guid ProductId, Guid ConnectionId, string? TitleOverride, string? DescriptionOverride, string? ExternalCategoryId, string? ExternalBrandId, int? DeliveryTimeDays, bool Enabled, string DesiredStatus, string ActualStatus, long Version);
public sealed record PublicationLineView(Guid VariantId, string Sku, string? Barcode, string DesiredStatus, string ActualStatus, string? RejectionCode);
public sealed record PublicationStatusView(Guid ProductId, Guid ConnectionId, Guid? ProfileId, string? DesiredStatus, string? ActualStatus, string? LastRejectionCode, Guid? LastJobId, string? LastJobStatus, IReadOnlyList<PublicationLineView> Lines);

public sealed record CreateCategoryCommand(string Name, Guid? ParentId);
public sealed record UpdateCategoryCommand(string Name, Guid? ParentId, bool IsActive);
public sealed record CreateBrandCommand(string Name);
public sealed record UpdateBrandCommand(string Name, bool IsActive);
public sealed record CreateAttributeValueCommand(string Value, int SortOrder);
public sealed record UpdateAttributeValueCommand(string Value, int SortOrder);
// Only the shown name changes; the attribute code (and every mapping that points at the attribute) stays.
public sealed record RenameAttributeCommand(string Name);
public sealed record ReorderAttributeValuesCommand(IReadOnlyList<Guid> ValueIds);
public sealed record CreateAttributeCommand(string Code, string Name, string DataType, string? SelectionMode, string? Unit, IReadOnlyList<CreateAttributeValueCommand> Values);
public sealed record AttributeRequirementCommand(Guid AttributeId, bool IsRequired, bool AllowsCustomValue, int DisplayOrder, string Role = "ATTRIBUTE");
public sealed record CategoryAttributeRequirementView(Guid AttributeId, bool IsRequired, bool AllowsCustomValue, int DisplayOrder, AttributeView Attribute, string Role = "ATTRIBUTE", bool IsWebColor = false);
public sealed record CreateVariantCommand(string Sku, string? Barcode, string? ModelCode, IReadOnlyDictionary<string, string>? Options = null, decimal? Weight = null, decimal? Width = null, decimal? Height = null, decimal? Length = null, decimal? Desi = null, IReadOnlyList<ProductAttributeCommand>? Attributes = null, int SortOrder = 0, decimal? CostPrice = null, decimal? DefaultListPrice = null, decimal? DefaultSalePrice = null);
// CostPrice: null leaves the stored cost as it is. A sent cost carries LoadedCostPrice, the cost the editor was
// loaded with (null: it was empty); the update is refused when the stored cost is no longer that one (K2). Desi works
// the same way with LoadedDesi, since the products import writes desi too.
public sealed record UpdateVariantCommand(Guid Id, string Sku, string? Barcode, string? ModelCode, int SortOrder = 0, IReadOnlyDictionary<string, string>? Options = null, IReadOnlyList<ProductAttributeCommand>? Attributes = null, decimal? CostPrice = null, decimal? DefaultListPrice = null, decimal? DefaultSalePrice = null, decimal? LoadedCostPrice = null, decimal? Desi = null, decimal? LoadedDesi = null);
public sealed record ProductAttributeCommand(Guid AttributeId, Guid? ValueId, string? TextValue, decimal? NumberValue, bool? BooleanValue, int SortOrder);
public sealed record CreateProductCommand(string Title, string Description, Guid? BrandId, Guid? CategoryId, IReadOnlyList<CreateVariantCommand> Variants, IReadOnlyList<ProductAttributeCommand>? Attributes = null, string? Status = null, decimal? DefaultListPrice = null, decimal? DefaultSalePrice = null, int? WarrantyMonths = null, Guid? TrendyolWebColorValueId = null);
public sealed record BulkProductStatusCommand(IReadOnlyList<Guid> ProductIds, string Status);
public sealed record BulkProductDeleteCommand(IReadOnlyList<Guid> ProductIds);
// Variant ekleme append-only'dir: envanter veya dış listeye bağlı mevcut satış satırları sessizce silinmez.
// Product list "Platformlara Yayınla": every selected card to every selected marketplace.
public sealed record BulkPublicationCommand(IReadOnlyList<Guid> ProductIds, IReadOnlyList<Guid> ConnectionIds);
public sealed record BulkPublicationFailure(Guid ProductId, string ProductTitle, Guid ConnectionId, string Code, string Message);
public sealed record BulkPublicationResult(int Queued, int Skipped, IReadOnlyList<BulkPublicationFailure> Failed);
public sealed record UpdateProductCommand(string Title, string Description, Guid? BrandId, Guid? CategoryId, IReadOnlyList<ProductAttributeCommand>? Attributes = null, IReadOnlyList<CreateVariantCommand>? VariantsToCreate = null, IReadOnlyList<UpdateVariantCommand>? VariantUpdates = null, string? Status = null, decimal? DefaultListPrice = null, decimal? DefaultSalePrice = null, IReadOnlyList<Guid>? VariantIdsToDelete = null, int? WarrantyMonths = null, Guid? TrendyolWebColorValueId = null);
public sealed record UpsertListingProfileCommand(string? TitleOverride, string? DescriptionOverride, string? ExternalCategoryId, string? ExternalBrandId, int? DeliveryTimeDays, bool Enabled);

// The codes a marketplace holds for each variant of a product's colour family, as its last import read them:
// the Hepsiburada hbSku and the Shopify variant id are the link ids, beside the marketplace barcode and stock code.
public sealed record ProductMarketplaceCodesView(IReadOnlyList<MarketplaceCodeConnectionView> Connections, IReadOnlyList<VariantMarketplaceCodesView> Variants);
public sealed record MarketplaceCodeConnectionView(Guid ConnectionId, string PlatformCode, string DisplayName);
public sealed record VariantMarketplaceCodesView(Guid VariantId, string Sku, string? Barcode, IReadOnlyList<VariantMarketplaceCodeView> Codes);
// LastSeenAt: when a product import last read the code at the marketplace (null: never verified). HealthStatus:
// null when healthy, NOT_FOUND or REJECTED when the marketplace no longer knows it (HealthReason says why).
public sealed record VariantMarketplaceCodeView(Guid ConnectionId, string ExternalId, string? ExternalBarcode, string? ExternalSku, DateTimeOffset? LastSeenAt = null, string? HealthStatus = null, string? HealthReason = null);
// Codes typed in the product editor: a code links the variant on that connection, an empty one removes its link.
public sealed record UpdateMarketplaceCodesCommand(IReadOnlyList<MarketplaceCodeChange>? Codes);
public sealed record MarketplaceCodeChange(Guid VariantId, Guid ConnectionId, string? Code);

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
    Task<ServiceResult<AttributeView>> RenameAttributeAsync(Guid tenantId, Guid attributeId, long expectedVersion, RenameAttributeCommand command, CancellationToken cancellationToken);
    Task<ServiceResult<AttributeView>> AddAttributeValuesAsync(Guid tenantId, Guid attributeId, IReadOnlyList<CreateAttributeValueCommand> values, CancellationToken cancellationToken);
    Task<ServiceResult<AttributeView>> UpdateAttributeValueAsync(Guid tenantId, Guid attributeId, Guid valueId, long expectedVersion, UpdateAttributeValueCommand command, CancellationToken cancellationToken);
    Task<ServiceResult<AttributeView>> ReorderAttributeValuesAsync(Guid tenantId, Guid attributeId, long expectedVersion, ReorderAttributeValuesCommand command, CancellationToken cancellationToken);
    Task<ServiceResult<AttributeView>> DeactivateAttributeValueAsync(Guid tenantId, Guid attributeId, Guid valueId, CancellationToken cancellationToken);
    Task<ServiceResult<IReadOnlyList<Guid>>> UsedAttributeValueIdsAsync(Guid tenantId, Guid attributeId, CancellationToken cancellationToken);
    Task<ServiceResult<IReadOnlyList<CategoryAttributeRequirementView>>> GetRequirementsAsync(Guid tenantId, Guid categoryId, CancellationToken cancellationToken);
    Task<ServiceResult<IReadOnlyList<AttributeRequirementCommand>>> ReplaceRequirementsAsync(Guid tenantId, Guid categoryId, long expectedVersion, IReadOnlyList<AttributeRequirementCommand> requirements, CancellationToken cancellationToken);
    Task<PageResult<ProductView>> ListProductsAsync(Guid tenantId, int limit, string? after, string? status, string? search, string? platform, string? stock, string? sort, CancellationToken cancellationToken);
    Task<ProductSummaryView> ProductSummaryAsync(Guid tenantId, CancellationToken cancellationToken);
    Task<ServiceResult<int>> BulkSetStatusAsync(Guid tenantId, BulkProductStatusCommand command, CancellationToken cancellationToken);
    Task<ServiceResult<int>> DeleteProductAsync(Guid tenantId, Guid id, long expectedVersion, CancellationToken cancellationToken);
    Task<ServiceResult<int>> BulkDeleteProductsAsync(Guid tenantId, BulkProductDeleteCommand command, CancellationToken cancellationToken);
    Task<ServiceResult<ProductView>> CreateProductAsync(Guid tenantId, CreateProductCommand command, CancellationToken cancellationToken);
    Task<ServiceResult<ProductView>> DuplicateProductAsync(Guid tenantId, Guid productId, CancellationToken cancellationToken);
    Task<ServiceResult<ProductView>> GetProductAsync(Guid tenantId, Guid id, CancellationToken cancellationToken);
    Task<ServiceResult<ProductMarketplaceCodesView>> GetProductMarketplaceCodesAsync(Guid tenantId, Guid id, CancellationToken cancellationToken);
    Task<ServiceResult<ProductMarketplaceCodesView>> UpdateProductMarketplaceCodesAsync(Guid tenantId, Guid id, UpdateMarketplaceCodesCommand command, CancellationToken cancellationToken);
    Task<ServiceResult<ProductView>> UpdateProductAsync(Guid tenantId, Guid id, long expectedVersion, UpdateProductCommand command, CancellationToken cancellationToken);
    Task<ServiceResult<ProductView>> ArchiveProductAsync(Guid tenantId, Guid id, long expectedVersion, CancellationToken cancellationToken);
    Task<ServiceResult<ListingProfileView>> GetListingProfileAsync(Guid tenantId, Guid productId, Guid connectionId, CancellationToken cancellationToken);
    Task<ServiceResult<ListingProfileView>> UpsertListingProfileAsync(Guid tenantId, Guid productId, Guid connectionId, long? expectedVersion, UpsertListingProfileCommand command, CancellationToken cancellationToken);
    Task<ServiceResult<Guid>> EnqueuePublicationAsync(Guid tenantId, Guid productId, Guid connectionId, string idempotencyKey, string correlationId, CancellationToken cancellationToken);
    Task<ServiceResult<BulkPublicationResult>> EnqueueBulkPublicationAsync(Guid tenantId, Guid userId, BulkPublicationCommand command, string idempotencyKey, string correlationId, CancellationToken cancellationToken);
    Task<ServiceResult<Guid>> EnqueueProductUpdateAsync(Guid tenantId, Guid productId, Guid connectionId, string idempotencyKey, string correlationId, CancellationToken cancellationToken);
    Task<ServiceResult<Guid>> EnqueueProductArchiveAsync(Guid tenantId, Guid productId, Guid connectionId, bool archived, string idempotencyKey, string correlationId, CancellationToken cancellationToken);
    Task<ServiceResult<Guid>> EnqueuePazaramaSaleStatusAsync(Guid tenantId, Guid connectionId, IReadOnlyList<string> barcodes, bool close, string idempotencyKey, string correlationId, CancellationToken cancellationToken);
    Task<ServiceResult<Guid>> RequestPublicationStatusRefreshAsync(Guid tenantId, Guid productId, Guid connectionId, CancellationToken cancellationToken);
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

public sealed record InventoryItemView(Guid Id, Guid VariantId, string Sku, string LocationCode, decimal OnHand, decimal Reserved, decimal Available, long ProjectionVersion, DateTimeOffset? ReconciledAt, long Version, decimal? ObservedRemoteQuantity = null, DateTimeOffset? ObservedRemoteAt = null);
public sealed record LedgerEntryView(Guid Id, string MovementType, decimal QuantityDelta, string SourceType, string SourceId, DateTimeOffset OccurredAt, string CorrelationId);
public sealed record StockAdjustmentCommand(decimal QuantityDelta, string Reason, string SourceEventId);
// Sets MAIN on-hand stock to an absolute value only while it still equals ExpectedOnHand (the value the user saw).
public sealed record StockSetCommand(decimal OnHand, decimal ExpectedOnHand, string Reason, string SourceEventId);
public sealed record ChannelOfferView(Guid Id, Guid ConnectionId, Guid VariantId, decimal ListPrice, decimal SalePrice, string Currency, decimal VatRate, string VatInclusion, string RoundingMode, decimal SafetyStock, string Status, long PriceVersion, long Version);
public sealed record UpsertChannelOfferCommand(Guid ConnectionId, Guid VariantId, decimal ListPrice, decimal SalePrice, string Currency, decimal VatRate, string VatInclusion, string RoundingMode, decimal SafetyStock, string Status, string Reason);
public sealed record UpdateChannelOfferCommand(decimal ListPrice, decimal SalePrice, string Currency, decimal VatRate, string VatInclusion, string RoundingMode, decimal SafetyStock, string Status, string Reason);

public interface IInventoryService
{
    Task<PageResult<InventoryItemView>> ListAsync(Guid tenantId, int limit, string? after, CancellationToken cancellationToken);
    Task<PageResult<LedgerEntryView>> LedgerAsync(Guid tenantId, Guid variantId, int limit, string? after, CancellationToken cancellationToken);
    Task<ServiceResult<InventoryItemView>> AdjustAsync(Guid tenantId, Guid userId, Guid variantId, StockAdjustmentCommand command, string idempotencyKey, string correlationId, CancellationToken cancellationToken);
    Task<ServiceResult<InventoryItemView>> SetOnHandAsync(Guid tenantId, Guid userId, Guid variantId, StockSetCommand command, string idempotencyKey, string correlationId, CancellationToken cancellationToken);
    Task<ServiceResult<ChannelOfferView>> UpsertOfferAsync(Guid tenantId, Guid userId, UpsertChannelOfferCommand command, CancellationToken cancellationToken);
    Task<ServiceResult<ChannelOfferView>> GetOfferAsync(Guid tenantId, Guid id, CancellationToken cancellationToken);
    Task<ServiceResult<ChannelOfferView>> UpdateOfferAsync(Guid tenantId, Guid userId, Guid id, long expectedVersion, UpdateChannelOfferCommand command, CancellationToken cancellationToken);
    Task<ServiceResult<Guid>> ValidateExternalSyncAsync(Guid tenantId, string operation, CancellationToken cancellationToken);
    Task<ServiceResult<Guid>> EnqueuePriceInventorySyncAsync(Guid tenantId, Guid connectionId, string idempotencyKey, string correlationId, CancellationToken cancellationToken, string? stockCode = null);
    Task<ServiceResult<Guid>> EnqueueProductPriceInventorySyncAsync(Guid tenantId, Guid productId, Guid connectionId, string idempotencyKey, string correlationId, CancellationToken cancellationToken);
}

public interface IImportJobProcessor
{
    Task<bool> ProcessAsync(Guid tenantId, Guid sessionId, string operation, CancellationToken cancellationToken);
}

public sealed record ReferenceItemView(string ExternalId, string? ParentExternalId, string Name, string Path, int Depth, bool IsLeaf, bool IsActive, bool? IsRequired, bool? AllowsCustomValue, bool? AllowsMultipleValues);
public sealed record ReferenceDataView(Guid SnapshotId, string ResourceType, DateTimeOffset FetchedAt, IReadOnlyList<ReferenceItemView> Items);
// Source: MANUAL or TEMPLATE for attribute and value mappings (filled from another category's
// decision), null for category and brand mappings.
public sealed record CatalogMappingView(Guid Id, Guid ConnectionId, Guid SnapshotId, Guid LocalId, string ScopeExternalId, string ExternalId, string Status, DateTimeOffset? VerifiedAt, long Version, int? MissingRequiredAttributeCount = null, string? Source = null);
// UpdateTemplate: also use this decision for the connection's other categories (the default);
// false keeps it to this category only.
public sealed record UpsertCatalogMappingCommand(Guid ConnectionId, Guid SnapshotId, string ExternalId, string Status, string Role = "ATTRIBUTE", bool UpdateTemplate = true);

public sealed record MappingTemplateFillView(int Attributes, int Values);

// Names a panel attribute and its values carry on the other marketplace connections (from their
// templates), used as extra hints when suggesting matches on another marketplace.
public sealed record MarketplaceNameHintsView(IReadOnlyList<string> AttributeNames, IReadOnlyDictionary<Guid, IReadOnlyList<string>> ValueNames);

// A value sent for a marketplace attribute whenever the product has none (e.g. Menşei = TR).
// SourceField: instead of a fixed value, a field of the product itself (MODEL_CODE, SKU, BARCODE, TITLE).
public sealed record ConnectionAttributeDefaultView(string ExternalAttributeId, string AttributeName, string? ExternalValueId, string? ValueName, string? CustomValue, DateTimeOffset UpdatedAt, long Version, string? SourceField = null);
public sealed record UpsertConnectionAttributeDefaultCommand(string AttributeName, string? ExternalValueId, string? ValueName, string? CustomValue, string? SourceField = null);

// The brand name typed for a panel brand on a connection whose marketplace has no brand list (Hepsiburada, n11,
// Shopify). Without one the panel brand name is sent.
public sealed record BrandNameMappingView(Guid LocalId, string BrandName, string Name, DateTimeOffset UpdatedAt, long Version);
public sealed record UpsertBrandNameMappingCommand(string Name);

// Value mappings to review. SameCategory: one panel value mapped to several marketplace values in
// one category (only one can be sent). CrossCategory: one panel value mapped to different
// marketplace values in different categories of the same marketplace attribute.
public sealed record MappingConflictOptionView(string ExternalId, string Name, long Version, DateTimeOffset? VerifiedAt, int Categories);
public sealed record MappingConflictView(string ScopeExternalId, string CategoryName, string AttributeName, Guid LocalValueId, string LocalValueName, IReadOnlyList<MappingConflictOptionView> Options, string? TemplateExternalId);
public sealed record MappingConflictsView(IReadOnlyList<MappingConflictView> SameCategory, IReadOnlyList<MappingConflictView> CrossCategory);

public interface IReferenceDataService
{
    Task<ServiceResult<MappingConflictsView>> ListMappingConflictsAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken);
    Task<ServiceResult<MarketplaceNameHintsView>> MarketplaceNameHintsAsync(Guid tenantId, Guid localAttributeId, Guid targetConnectionId, CancellationToken cancellationToken);
    // For every panel attribute: the marketplace attribute names it is mapped to on the other connections.
    Task<ServiceResult<IReadOnlyDictionary<Guid, IReadOnlyList<string>>>> AttributeNameHintsAsync(Guid tenantId, Guid targetConnectionId, CancellationToken cancellationToken);
    Task<ServiceResult<IReadOnlyList<ConnectionAttributeDefaultView>>> ListAttributeDefaultsAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken);
    Task<ServiceResult<ConnectionAttributeDefaultView>> UpsertAttributeDefaultAsync(Guid tenantId, Guid connectionId, string externalAttributeId, UpsertConnectionAttributeDefaultCommand command, CancellationToken cancellationToken);
    Task<ServiceResult<bool>> DeleteAttributeDefaultAsync(Guid tenantId, Guid connectionId, string externalAttributeId, CancellationToken cancellationToken);
    Task<ServiceResult<IReadOnlyList<BrandNameMappingView>>> ListBrandNameMappingsAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken);
    Task<ServiceResult<BrandNameMappingView>> UpsertBrandNameMappingAsync(Guid tenantId, Guid connectionId, Guid localId, long? expectedVersion, UpsertBrandNameMappingCommand command, CancellationToken cancellationToken);
    Task<ServiceResult<bool>> DeleteBrandNameMappingAsync(Guid tenantId, Guid connectionId, Guid localId, long expectedVersion, CancellationToken cancellationToken);
    // Fills a mapped marketplace category from the decisions made for the connection's other categories.
    Task<ServiceResult<MappingTemplateFillView>> FillFromTemplatesAsync(Guid tenantId, Guid connectionId, string categoryExternalId, CancellationToken cancellationToken);
    Task<ServiceResult<ReferenceDataView>> ListAsync(Guid tenantId, Guid connectionId, string resourceType, string? parentExternalId, CancellationToken cancellationToken);
    Task<ServiceResult<IReadOnlyList<CatalogMappingView>>> ListMappingsAsync(Guid tenantId, string mappingType, Guid connectionId, string? scopeExternalId, CancellationToken cancellationToken);
    Task<ServiceResult<CatalogMappingView?>> GetMappingAsync(Guid tenantId, string mappingType, Guid localId, Guid connectionId, string? scopeExternalId, string? externalId, CancellationToken cancellationToken);
    Task<ServiceResult<CatalogMappingView>> UpsertMappingAsync(Guid tenantId, string mappingType, Guid localId, long? expectedVersion, UpsertCatalogMappingCommand command, CancellationToken cancellationToken);
    Task<ServiceResult<bool>> DeleteMappingAsync(Guid tenantId, string mappingType, Guid localId, Guid connectionId, string? scopeExternalId, string? externalId, long expectedVersion, CancellationToken cancellationToken);
}
