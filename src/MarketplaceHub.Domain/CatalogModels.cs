namespace MarketplaceHub.Domain;

public enum ProductStatus { Draft = 0, Archived = 1, Active = 2 }
public enum AttributeDataType { Text, Number, SingleSelect, MultiSelect, Boolean }

public sealed class Product
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public required string Title { get; set; }
    public required string Description { get; set; }
    public Guid? BrandId { get; set; }
    public Guid? CategoryId { get; set; }
    public decimal? DefaultListPrice { get; set; }
    public decimal? DefaultSalePrice { get; set; }
    public ProductStatus Status { get; set; } = ProductStatus.Active;
    public long SourcePolicyVersion { get; set; } = 1;
    public DateTimeOffset? ArchivedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public long Version { get; set; } = 1;
}

public sealed class Category
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid? ParentId { get; set; }
    public required string Name { get; set; }
    public required string NormalizedName { get; set; }
    public required string Path { get; set; }
    public int Depth { get; set; }
    public bool IsLeaf { get; set; } = true;
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public long Version { get; set; } = 1;
}

public sealed class Brand
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public required string Name { get; set; }
    public required string NormalizedName { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public long Version { get; set; } = 1;
}

public sealed class AttributeDefinition
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public required string Code { get; set; }
    public required string Name { get; set; }
    public AttributeDataType DataType { get; set; }
    public string? SelectionMode { get; set; }
    public string? Unit { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public long Version { get; set; } = 1;
}

public sealed class AttributeValue
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid AttributeId { get; set; }
    public required string Value { get; set; }
    public required string NormalizedValue { get; set; }
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
    public long Version { get; set; } = 1;
}

public sealed class CategoryAttributeRequirement
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid CategoryId { get; set; }
    public Guid AttributeId { get; set; }
    public bool IsRequired { get; set; }
    public bool AllowsCustomValue { get; set; }
    public bool IsPanelScoped { get; set; }
    public string Role { get; set; } = "ATTRIBUTE";
    public int DisplayOrder { get; set; }
    public long Version { get; set; } = 1;
}

public sealed class ProductAttributeAssignment
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid ProductId { get; set; }
    public Guid? VariantId { get; set; }
    public Guid AttributeId { get; set; }
    public Guid? ValueId { get; set; }
    public string? TextValue { get; set; }
    public decimal? NumberValue { get; set; }
    public bool? BooleanValue { get; set; }
    public int SortOrder { get; set; }
    public long Version { get; set; } = 1;

    public bool HasExactlyOneTypedValue() =>
        new object?[] { ValueId, TextValue, NumberValue, BooleanValue }.Count(value => value is not null) == 1;
}

public sealed class ProductVariant
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid ProductId { get; set; }
    public int SortOrder { get; set; }
    public required string Sku { get; set; }
    public required string SkuNormalized { get; set; }
    public string? Barcode { get; set; }
    public string? BarcodeNormalized { get; set; }
    public string? ModelCode { get; set; }
    public required string OptionSignature { get; set; }
    public ProductStatus Status { get; set; } = ProductStatus.Active;
    public decimal? Weight { get; set; }
    public decimal? Width { get; set; }
    public decimal? Height { get; set; }
    public decimal? Length { get; set; }
    public decimal? Desi { get; set; }
    public decimal? CostPrice { get; set; }
    public decimal? DefaultListPrice { get; set; }
    public decimal? DefaultSalePrice { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public long Version { get; set; } = 1;
}

public sealed class ProductOption
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid ProductId { get; set; }
    public required string Label { get; set; }
    public required string NormalizedKey { get; set; }
    public int SortOrder { get; set; }
}

public sealed class ProductOptionValue
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid OptionId { get; set; }
    public required string Label { get; set; }
    public required string NormalizedKey { get; set; }
    public int SortOrder { get; set; }
}

public sealed class VariantOptionValue
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid VariantId { get; set; }
    public Guid OptionId { get; set; }
    public Guid OptionValueId { get; set; }
}

public sealed class ProductMedia
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid ProductId { get; set; }
    public Guid? VariantId { get; set; }
    public Guid FileAssetId { get; set; }
    public required string MediaRole { get; set; }
    public int SortOrder { get; set; }
    public string? AltText { get; set; }
    public required string Status { get; set; }
}

public sealed class PlatformConnection
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid PublicId { get; set; }
    public required string PlatformCode { get; set; }
    public required string Environment { get; set; }
    public required string DisplayName { get; set; }
    public required string ExternalStoreId { get; set; }
    public required string Status { get; set; }
    public required string ApiVersion { get; set; }
    public string SettingsJson { get; set; } = "{}";
    public DateTimeOffset? LastTestedAt { get; set; }
    public DateTimeOffset? LastSuccessAt { get; set; }
    public string? LastErrorCode { get; set; }
    // When the connection was last disabled (or hidden); re-enabling it needs
    // a successful connection test newer than this.
    public DateTimeOffset? DisabledAt { get; set; }
    public long Version { get; set; } = 1;
}

public sealed class ReferenceSnapshot
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid ConnectionId { get; set; }
    public required string ResourceType { get; set; }
    public string ScopeExternalId { get; set; } = "";
    public required string SourceVersion { get; set; }
    public required string ContentHash { get; set; }
    public Guid? AssetId { get; set; }
    public DateTimeOffset FetchedAt { get; set; }
    public bool IsCurrent { get; set; }
    public int ItemCount { get; set; }
}

public sealed class ReferenceItem
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid ConnectionId { get; set; }
    public Guid SnapshotId { get; set; }
    public required string ResourceType { get; set; }
    public required string ExternalId { get; set; }
    public string? ParentExternalId { get; set; }
    public required string Name { get; set; }
    public required string NormalizedName { get; set; }
    public required string Path { get; set; }
    public int Depth { get; set; }
    public bool IsLeaf { get; set; }
    public bool IsActive { get; set; }
    public bool? IsRequired { get; set; }
    public bool? AllowsCustomValue { get; set; }
    public bool? AllowsMultipleValues { get; set; }
    public required string PayloadHash { get; set; }
    public int? SortOrder { get; set; }
}

public abstract class CatalogMapping
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid ConnectionId { get; set; }
    public Guid SnapshotId { get; set; }
    public Guid LocalId { get; set; }
    public string ScopeExternalId { get; set; } = "";
    public required string ExternalId { get; set; }
    public required string Status { get; set; }
    public DateTimeOffset? VerifiedAt { get; set; }
    public long Version { get; set; } = 1;
}

public sealed class CategoryMapping : CatalogMapping;
public sealed class BrandMapping : CatalogMapping;
public sealed class AttributeMapping : CatalogMapping
{
    // MANUAL: saved by a user (or by the import); TEMPLATE: filled from the connection-wide template.
    public string Source { get; set; } = MappingSources.Manual;
}

public sealed class AttributeValueMapping : CatalogMapping
{
    public string Source { get; set; } = MappingSources.Manual;
}

public static class MappingSources
{
    public const string Manual = "MANUAL";
    public const string Template = "TEMPLATE";
}

// Connection-wide decision behind the category-scoped attribute mappings: marketplace attribute
// ExternalAttributeId (e.g. Trendyol "Renk" 47) is panel attribute LocalAttributeId. Trendyol keeps
// attribute ids stable across categories, so one decision serves every category that lists it.
public sealed class AttributeMappingTemplate
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid ConnectionId { get; set; }
    public required string ExternalAttributeId { get; set; }
    public Guid LocalAttributeId { get; set; }
    public string Role { get; set; } = "ATTRIBUTE";
    public DateTimeOffset UpdatedAt { get; set; }
    public long Version { get; set; } = 1;
}

// A fixed value a connection sends for a marketplace attribute whenever the product has none, e.g.
// Trendyol "Menşei" = TR or "Yaş Grubu" = Yetişkin. ExternalValueId for list attributes,
// CustomValue for free-text ones. Product values always win over the default.
public sealed class ConnectionAttributeDefault
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid ConnectionId { get; set; }
    public required string ExternalAttributeId { get; set; }
    public required string AttributeName { get; set; }
    public string? ExternalValueId { get; set; }
    public string? ValueName { get; set; }
    public string? CustomValue { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public long Version { get; set; } = 1;
}

// A mapping the user removed from one category: the template fill must not bring it back there.
// Kind is ATTRIBUTE (scope = category, LocalId = panel attribute) or VALUE (scope =
// "{category}/{attribute}", LocalId = panel value). Saving that mapping again clears it.
public sealed class MappingTemplateDismissal
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid ConnectionId { get; set; }
    public required string Kind { get; set; }
    public required string ScopeExternalId { get; set; }
    public Guid LocalId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

// Connection-wide value decision: in marketplace attribute ExternalAttributeId, panel value
// LocalValueId is marketplace value ExternalValueId (e.g. Renk 47 / Siyah -> 1001).
public sealed class AttributeValueMappingTemplate
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid ConnectionId { get; set; }
    public required string ExternalAttributeId { get; set; }
    public Guid LocalValueId { get; set; }
    public required string ExternalValueId { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public long Version { get; set; } = 1;
}

public sealed class MarketplaceProductLink
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid ConnectionId { get; set; }
    public Guid ProductId { get; set; }
    public required string ExternalId { get; set; }
    // The code the marketplace keeps the product by when ExternalId is its numeric id: PTT AVM's product barcode
    // (the model code of a product with variants, the product's own code otherwise), which price and stock go by.
    public string? ExternalCode { get; set; }
    public string? LastImportedPayloadHash { get; set; }
    public long LastImportedProductVersion { get; set; }
    public long LastPublishedProductVersion { get; set; }
    public DateTimeOffset? LastImportedAt { get; set; }
    public DateTimeOffset? LastPublishedAt { get; set; }
    public string SyncStatus { get; set; } = "SYNCED";
    public string? DirtyFieldsJson { get; set; }
    public string? LastError { get; set; }
    public long Version { get; set; } = 1;
}

public sealed class MarketplaceVariantLink
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid ConnectionId { get; set; }
    public Guid VariantId { get; set; }
    public required string ExternalId { get; set; }
    // The barcode and stock code the marketplace holds for the variant, as its last import read them. They may
    // differ from the panel's: writes address the marketplace by its own codes.
    public string? ExternalBarcode { get; set; }
    public string? ExternalSku { get; set; }
    public long Version { get; set; } = 1;
}

public sealed class MarketplaceListingState
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid ConnectionId { get; set; }
    public Guid VariantId { get; set; }
    public required string DesiredStatus { get; set; }
    public required string ActualStatus { get; set; }
    public string? LastRejectionCode { get; set; }
    public string? PayloadHash { get; set; }
    public long Version { get; set; } = 1;
}

public sealed class ExternalIdentifierAlias
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid ConnectionId { get; set; }
    public required string EntityType { get; set; }
    public Guid LocalId { get; set; }
    public required string ExternalId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class ChannelListingProfile
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid ConnectionId { get; set; }
    public Guid ProductId { get; set; }
    public string? TitleOverride { get; set; }
    public string? DescriptionOverride { get; set; }
    public string? ExternalCategoryId { get; set; }
    public string? ExternalBrandId { get; set; }
    public int? DeliveryTimeDays { get; set; }
    public string? CargoProfile { get; set; }
    public string? Origin { get; set; }
    public string? Warranty { get; set; }
    public string? PackageContent { get; set; }
    public decimal? VatOverride { get; set; }
    public bool Enabled { get; set; }
    public required string DesiredStatus { get; set; }
    public required string ActualStatus { get; set; }
    public string? LastRejectionCode { get; set; }
    public long Version { get; set; } = 1;
}

public sealed class ChannelListingVariant
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid ProfileId { get; set; }
    public Guid VariantId { get; set; }
    public string? ExternalSku { get; set; }
    public string? ExternalBarcode { get; set; }
    public required string DesiredStatus { get; set; }
    public required string ActualStatus { get; set; }
    public string? RejectionCode { get; set; }
}

public sealed class ChannelListingAttribute
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid ProfileId { get; set; }
    public Guid AttributeId { get; set; }
    public string? ExternalAttributeId { get; set; }
    public string? ExternalValueId { get; set; }
    public string? CustomValue { get; set; }
}

public sealed class ChannelMediaOrder
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid ProfileId { get; set; }
    public Guid MediaId { get; set; }
    public int SortOrder { get; set; }
}
