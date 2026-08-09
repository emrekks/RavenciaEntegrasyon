namespace MarketplaceHub.Domain;

public enum ProductStatus { Draft, Archived }
public enum AttributeDataType { Text, Number, SingleSelect, MultiSelect, Boolean }

public sealed class Product
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public required string Title { get; set; }
    public required string Description { get; set; }
    public Guid? BrandId { get; set; }
    public Guid? CategoryId { get; set; }
    public ProductStatus Status { get; set; } = ProductStatus.Draft;
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
    public required string Sku { get; set; }
    public required string SkuNormalized { get; set; }
    public string? Barcode { get; set; }
    public string? BarcodeNormalized { get; set; }
    public string? ModelCode { get; set; }
    public required string OptionSignature { get; set; }
    public ProductStatus Status { get; set; } = ProductStatus.Draft;
    public decimal? Weight { get; set; }
    public decimal? Width { get; set; }
    public decimal? Height { get; set; }
    public decimal? Length { get; set; }
    public decimal? Desi { get; set; }
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
public sealed class AttributeMapping : CatalogMapping;
public sealed class AttributeValueMapping : CatalogMapping;

public sealed class MarketplaceProductLink
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid ConnectionId { get; set; }
    public Guid ProductId { get; set; }
    public required string ExternalId { get; set; }
    public long Version { get; set; } = 1;
}

public sealed class MarketplaceVariantLink
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid ConnectionId { get; set; }
    public Guid VariantId { get; set; }
    public required string ExternalId { get; set; }
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
