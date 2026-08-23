using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

internal static class CatalogModelConfiguration
{
    public static void ConfigureCatalogModels(this ModelBuilder builder)
    {
        ConfigureCatalog(builder);
        ConfigureReferencesAndListings(builder);
        ConfigureImports(builder);
        ConfigureInventory(builder);
    }

    private static void ConfigureCatalog(ModelBuilder builder)
    {
        builder.Entity<Product>(entity =>
        {
            entity.ToTable("products", "catalog"); entity.HasKey(x => x.Id); entity.HasAlternateKey(x => new { x.TenantId, x.Id });
            entity.Property(x => x.Title).HasMaxLength(320); entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(24);
            entity.Property(x => x.Version).IsConcurrencyToken(); entity.HasIndex(x => new { x.TenantId, x.Status, x.UpdatedAt });
            entity.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Category>().WithMany().HasForeignKey(x => new { x.TenantId, x.CategoryId }).HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Brand>().WithMany().HasForeignKey(x => new { x.TenantId, x.BrandId }).HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<Category>(entity =>
        {
            entity.ToTable("categories", "catalog"); entity.HasKey(x => x.Id); entity.HasAlternateKey(x => new { x.TenantId, x.Id });
            entity.Property(x => x.Name).HasMaxLength(160); entity.Property(x => x.NormalizedName).HasMaxLength(160); entity.Property(x => x.Path).HasMaxLength(1024);
            entity.Property(x => x.Version).IsConcurrencyToken(); entity.HasIndex(x => new { x.TenantId, x.ParentId, x.NormalizedName }).IsUnique();
            entity.HasOne<Category>().WithMany().HasForeignKey(x => new { x.TenantId, x.ParentId }).HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<Brand>(entity =>
        {
            entity.ToTable("brands", "catalog"); entity.HasKey(x => x.Id); entity.HasAlternateKey(x => new { x.TenantId, x.Id });
            entity.Property(x => x.Name).HasMaxLength(160); entity.Property(x => x.NormalizedName).HasMaxLength(160); entity.Property(x => x.Version).IsConcurrencyToken();
            entity.HasIndex(x => new { x.TenantId, x.NormalizedName }).IsUnique(); entity.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<AttributeDefinition>(entity =>
        {
            entity.ToTable("attribute_definitions", "catalog", table => table.HasCheckConstraint("ck_attribute_definition_data_type", "\"DataType\" IN ('TEXT','NUMBER','SINGLE_SELECT','MULTI_SELECT','BOOLEAN')"));
            entity.HasKey(x => x.Id); entity.HasAlternateKey(x => new { x.TenantId, x.Id }); entity.Property(x => x.Code).HasMaxLength(96); entity.Property(x => x.Name).HasMaxLength(160);
            entity.Property(x => x.DataType).HasConversion(value => AttributeDataTypeWire(value), value => ParseAttributeDataType(value)).HasMaxLength(24);
            entity.Property(x => x.SelectionMode).HasMaxLength(24); entity.Property(x => x.Unit).HasMaxLength(32); entity.Property(x => x.Version).IsConcurrencyToken();
            entity.HasIndex(x => new { x.TenantId, x.Code }).IsUnique(); entity.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<AttributeValue>(entity =>
        {
            entity.ToTable("attribute_values", "catalog"); entity.HasKey(x => x.Id); entity.HasAlternateKey(x => new { x.TenantId, x.Id });
            entity.Property(x => x.Value).HasMaxLength(320); entity.Property(x => x.NormalizedValue).HasMaxLength(320); entity.Property(x => x.Version).IsConcurrencyToken();
            entity.HasIndex(x => new { x.TenantId, x.AttributeId, x.NormalizedValue }).IsUnique();
            entity.HasOne<AttributeDefinition>().WithMany().HasForeignKey(x => new { x.TenantId, x.AttributeId }).HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<CategoryAttributeRequirement>(entity =>
        {
            entity.ToTable("category_attribute_requirements", "catalog"); entity.HasKey(x => x.Id); entity.Property(x => x.Version).IsConcurrencyToken();
            entity.Property(x => x.Role).HasMaxLength(24).HasDefaultValue("ATTRIBUTE");
            entity.HasIndex(x => new { x.TenantId, x.CategoryId, x.AttributeId }).IsUnique();
            entity.HasOne<Category>().WithMany().HasForeignKey(x => new { x.TenantId, x.CategoryId }).HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<AttributeDefinition>().WithMany().HasForeignKey(x => new { x.TenantId, x.AttributeId }).HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<ProductVariant>(entity =>
        {
            entity.ToTable("product_variants", "catalog"); entity.HasKey(x => x.Id); entity.HasAlternateKey(x => new { x.TenantId, x.Id });
            entity.Property(x => x.Sku).HasMaxLength(160); entity.Property(x => x.SkuNormalized).HasMaxLength(160); entity.Property(x => x.Barcode).HasMaxLength(160); entity.Property(x => x.BarcodeNormalized).HasMaxLength(160);
            entity.Property(x => x.OptionSignature).HasMaxLength(512); entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(24); entity.Property(x => x.Version).IsConcurrencyToken();
            entity.Property(x => x.Weight).HasPrecision(19, 4); entity.Property(x => x.Width).HasPrecision(19, 4); entity.Property(x => x.Height).HasPrecision(19, 4); entity.Property(x => x.Length).HasPrecision(19, 4); entity.Property(x => x.Desi).HasPrecision(19, 4);
            entity.HasIndex(x => new { x.TenantId, x.SkuNormalized }).IsUnique(); entity.HasIndex(x => new { x.TenantId, x.BarcodeNormalized }).IsUnique();
            entity.HasOne<Product>().WithMany().HasForeignKey(x => new { x.TenantId, x.ProductId }).HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<ProductAttributeAssignment>(entity =>
        {
            entity.ToTable("product_attribute_assignments", "catalog", table => table.HasCheckConstraint("ck_product_attribute_assignments_exactly_one_value", "num_nonnulls(\"ValueId\",\"TextValue\",\"NumberValue\",\"BooleanValue\")=1"));
            entity.HasKey(x => x.Id); entity.Property(x => x.NumberValue).HasPrecision(19, 4); entity.Property(x => x.Version).IsConcurrencyToken();
            entity.HasOne<Product>().WithMany().HasForeignKey(x => new { x.TenantId, x.ProductId }).HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<ProductVariant>().WithMany().HasForeignKey(x => new { x.TenantId, x.VariantId }).HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<AttributeDefinition>().WithMany().HasForeignKey(x => new { x.TenantId, x.AttributeId }).HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<AttributeValue>().WithMany().HasForeignKey(x => new { x.TenantId, x.ValueId }).HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(x => new { x.TenantId, x.ProductId, x.VariantId, x.AttributeId, x.ValueId }).IsUnique();
        });
        builder.Entity<ProductOption>(entity =>
        {
            entity.ToTable("product_options", "catalog"); entity.HasKey(x => x.Id); entity.HasAlternateKey(x => new { x.TenantId, x.Id });
            entity.Property(x => x.Label).HasMaxLength(160); entity.Property(x => x.NormalizedKey).HasMaxLength(160); entity.HasIndex(x => new { x.TenantId, x.ProductId, x.NormalizedKey }).IsUnique();
            entity.HasOne<Product>().WithMany().HasForeignKey(x => new { x.TenantId, x.ProductId }).HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<ProductOptionValue>(entity =>
        {
            entity.ToTable("product_option_values", "catalog"); entity.HasKey(x => x.Id); entity.HasAlternateKey(x => new { x.TenantId, x.Id });
            entity.Property(x => x.Label).HasMaxLength(160); entity.Property(x => x.NormalizedKey).HasMaxLength(160); entity.HasIndex(x => new { x.TenantId, x.OptionId, x.NormalizedKey }).IsUnique();
            entity.HasOne<ProductOption>().WithMany().HasForeignKey(x => new { x.TenantId, x.OptionId }).HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<VariantOptionValue>(entity =>
        {
            entity.ToTable("variant_option_values", "catalog"); entity.HasKey(x => x.Id); entity.HasIndex(x => new { x.TenantId, x.VariantId, x.OptionId }).IsUnique();
            entity.HasOne<ProductVariant>().WithMany().HasForeignKey(x => new { x.TenantId, x.VariantId }).HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<ProductOptionValue>().WithMany().HasForeignKey(x => new { x.TenantId, x.OptionValueId }).HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<ProductMedia>(entity =>
        {
            entity.ToTable("product_media", "catalog"); entity.HasKey(x => x.Id); entity.Property(x => x.MediaRole).HasMaxLength(32); entity.Property(x => x.Status).HasMaxLength(24);
            entity.HasIndex(x => new { x.TenantId, x.ProductId, x.VariantId, x.SortOrder }).IsUnique();
            entity.HasOne<Product>().WithMany().HasForeignKey(x => new { x.TenantId, x.ProductId }).HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<ProductVariant>().WithMany().HasForeignKey(x => new { x.TenantId, x.VariantId }).HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<FileAsset>().WithMany().HasForeignKey(x => x.FileAssetId).OnDelete(DeleteBehavior.Restrict);
        });
    }

    private static void ConfigureReferencesAndListings(ModelBuilder builder)
    {
        builder.Entity<PlatformConnection>(entity =>
        {
            entity.ToTable("platform_connections", "integration"); entity.HasKey(x => x.Id); entity.HasAlternateKey(x => new { x.TenantId, x.Id });
            entity.Property(x => x.PlatformCode).HasMaxLength(64); entity.Property(x => x.Environment).HasMaxLength(24); entity.Property(x => x.DisplayName).HasMaxLength(160); entity.Property(x => x.ExternalStoreId).HasMaxLength(256); entity.Property(x => x.Status).HasMaxLength(24); entity.Property(x => x.ApiVersion).HasMaxLength(64);
            entity.HasIndex(x => x.PublicId).IsUnique(); entity.HasIndex(x => new { x.TenantId, x.PlatformCode, x.Environment, x.ExternalStoreId }).IsUnique(); entity.Property(x => x.Version).IsConcurrencyToken();
            entity.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<ReferenceSnapshot>(entity =>
        {
            entity.ToTable("reference_snapshots", "integration"); entity.HasKey(x => x.Id); entity.HasAlternateKey(x => new { x.TenantId, x.Id }); entity.Property(x => x.ResourceType).HasMaxLength(64); entity.Property(x => x.ScopeExternalId).HasMaxLength(512); entity.Property(x => x.ContentHash).HasMaxLength(128);
            entity.HasIndex(x => new { x.TenantId, x.ConnectionId, x.ResourceType, x.ScopeExternalId, x.ContentHash }).IsUnique(); entity.HasIndex(x => new { x.TenantId, x.ConnectionId, x.ResourceType, x.ScopeExternalId, x.IsCurrent });
            entity.HasOne<PlatformConnection>().WithMany().HasForeignKey(x => new { x.TenantId, x.ConnectionId }).HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<ReferenceItem>(entity =>
        {
            entity.ToTable("reference_items", "integration"); entity.HasKey(x => x.Id); entity.Property(x => x.ResourceType).HasMaxLength(64); entity.Property(x => x.ExternalId).HasMaxLength(256); entity.Property(x => x.NormalizedName).HasMaxLength(320); entity.Property(x => x.Path).HasMaxLength(1024); entity.Property(x => x.PayloadHash).HasMaxLength(128);
            entity.HasIndex(x => new { x.TenantId, x.SnapshotId, x.ResourceType, x.ExternalId }).IsUnique(); entity.HasIndex(x => new { x.TenantId, x.SnapshotId, x.ParentExternalId });
            entity.HasOne<ReferenceSnapshot>().WithMany().HasForeignKey(x => new { x.TenantId, x.SnapshotId }).HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<CatalogMapping>().UseTpcMappingStrategy();
        ConfigureMapping<CategoryMapping>(builder, "category_mappings"); ConfigureMapping<BrandMapping>(builder, "brand_mappings");
        ConfigureMapping<AttributeMapping>(builder, "attribute_mappings"); ConfigureMapping<AttributeValueMapping>(builder, "attribute_value_mappings");
        builder.Entity<MarketplaceProductLink>(entity =>
        {
            entity.ToTable("marketplace_product_links", "catalog"); entity.HasKey(x => x.Id); entity.HasIndex(x => new { x.TenantId, x.ConnectionId, x.ExternalId }).IsUnique(); entity.HasIndex(x => new { x.TenantId, x.ConnectionId, x.ProductId }).IsUnique(); entity.Property(x => x.Version).IsConcurrencyToken();
            entity.HasOne<Product>().WithMany().HasForeignKey(x => new { x.TenantId, x.ProductId }).HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<MarketplaceVariantLink>(entity =>
        {
            entity.ToTable("marketplace_variant_links", "catalog"); entity.HasKey(x => x.Id); entity.HasIndex(x => new { x.TenantId, x.ConnectionId, x.ExternalId }).IsUnique(); entity.HasIndex(x => new { x.TenantId, x.ConnectionId, x.VariantId }).IsUnique(); entity.Property(x => x.Version).IsConcurrencyToken();
            entity.HasOne<ProductVariant>().WithMany().HasForeignKey(x => new { x.TenantId, x.VariantId }).HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<MarketplaceListingState>(entity =>
        {
            entity.ToTable("marketplace_listing_states", "catalog"); entity.HasKey(x => x.Id); entity.HasIndex(x => new { x.TenantId, x.ConnectionId, x.VariantId }).IsUnique(); entity.Property(x => x.Version).IsConcurrencyToken();
        });
        builder.Entity<ExternalIdentifierAlias>(entity =>
        {
            entity.ToTable("external_identifier_aliases", "catalog"); entity.HasKey(x => x.Id); entity.HasIndex(x => new { x.TenantId, x.ConnectionId, x.EntityType, x.ExternalId }).IsUnique();
        });
        builder.Entity<ChannelListingProfile>(entity =>
        {
            entity.ToTable("channel_listing_profiles", "catalog"); entity.HasKey(x => x.Id); entity.HasAlternateKey(x => new { x.TenantId, x.Id }); entity.Property(x => x.VatOverride).HasPrecision(7, 4); entity.Property(x => x.Version).IsConcurrencyToken();
            entity.HasIndex(x => new { x.TenantId, x.ConnectionId, x.ProductId }).IsUnique(); entity.HasOne<Product>().WithMany().HasForeignKey(x => new { x.TenantId, x.ProductId }).HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<ChannelListingVariant>(entity =>
        {
            entity.ToTable("channel_listing_variants", "catalog"); entity.HasKey(x => x.Id); entity.HasIndex(x => new { x.TenantId, x.ProfileId, x.VariantId }).IsUnique(); entity.HasOne<ChannelListingProfile>().WithMany().HasForeignKey(x => new { x.TenantId, x.ProfileId }).HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<ChannelListingAttribute>(entity =>
        {
            entity.ToTable("channel_listing_attributes", "catalog"); entity.HasKey(x => x.Id); entity.HasIndex(x => new { x.TenantId, x.ProfileId, x.AttributeId }).IsUnique(); entity.HasOne<ChannelListingProfile>().WithMany().HasForeignKey(x => new { x.TenantId, x.ProfileId }).HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<ChannelMediaOrder>(entity =>
        {
            entity.ToTable("channel_media_order", "catalog"); entity.HasKey(x => x.Id); entity.HasIndex(x => new { x.TenantId, x.ProfileId, x.MediaId, x.SortOrder }).IsUnique(); entity.HasOne<ChannelListingProfile>().WithMany().HasForeignKey(x => new { x.TenantId, x.ProfileId }).HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Cascade);
        });
    }

    private static void ConfigureImports(ModelBuilder builder)
    {
        builder.Entity<ImportSession>(entity =>
        {
            entity.ToTable("import_sessions", "catalog"); entity.HasKey(x => x.Id); entity.HasAlternateKey(x => new { x.TenantId, x.Id }); entity.Property(x => x.SourceType).HasConversion(value => value.ToString().ToUpperInvariant(), value => Enum.Parse<ImportSourceType>(value, true)).HasMaxLength(24); entity.Property(x => x.Status).HasConversion(value => ImportStatusWire(value), value => ParseImportStatus(value)).HasMaxLength(32); entity.Property(x => x.Version).IsConcurrencyToken(); entity.HasIndex(x => new { x.TenantId, x.Status });
        });
        builder.Entity<ImportColumnProfile>(entity =>
        {
            entity.ToTable("import_column_profiles", "catalog"); entity.HasKey(x => x.Id); entity.HasAlternateKey(x => new { x.TenantId, x.Id }); entity.Property(x => x.Name).HasMaxLength(160); entity.Property(x => x.Version).IsConcurrencyToken();
        });
        builder.Entity<ImportColumnMapping>(entity =>
        {
            entity.ToTable("import_column_mappings", "catalog"); entity.HasKey(x => x.Id); entity.HasIndex(x => new { x.TenantId, x.ProfileId, x.SourceColumn }).IsUnique(); entity.HasOne<ImportColumnProfile>().WithMany().HasForeignKey(x => new { x.TenantId, x.ProfileId }).HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<ImportStagingRecord>(entity =>
        {
            entity.ToTable("import_staging_records", "catalog"); entity.HasKey(x => x.Id); entity.HasAlternateKey(x => new { x.TenantId, x.Id }); entity.Property(x => x.RowHash).HasMaxLength(128); entity.Property(x => x.ReviewStatus).HasMaxLength(32); entity.HasIndex(x => new { x.TenantId, x.SessionId, x.RowNumber }).IsUnique(); entity.HasIndex(x => new { x.TenantId, x.SessionId, x.ExternalRecordId }).IsUnique(); entity.HasOne<ImportSession>().WithMany().HasForeignKey(x => new { x.TenantId, x.SessionId }).HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<ImportMatchCandidate>(entity =>
        {
            entity.ToTable("import_match_candidates", "catalog"); entity.HasKey(x => x.Id); entity.HasAlternateKey(x => new { x.TenantId, x.Id }); entity.Property(x => x.MatchRule).HasMaxLength(48); entity.Property(x => x.Status).HasMaxLength(32); entity.Property(x => x.Version).IsConcurrencyToken(); entity.HasIndex(x => new { x.TenantId, x.SessionId, x.StagingRecordId, x.VariantId });
        });
        builder.Entity<ImportDecision>(entity =>
        {
            entity.ToTable("import_decisions", "catalog"); entity.HasKey(x => x.Id); entity.Property(x => x.Decision).HasConversion<string>().HasMaxLength(16); entity.Property(x => x.Version).IsConcurrencyToken(); entity.HasIndex(x => new { x.TenantId, x.CandidateId }).IsUnique(); entity.HasOne<ImportMatchCandidate>().WithMany().HasForeignKey(x => new { x.TenantId, x.CandidateId }).HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<FieldProvenance>(entity =>
        {
            entity.ToTable("field_provenance", "catalog"); entity.HasKey(x => x.Id); entity.Property(x => x.FieldName).HasMaxLength(96); entity.Property(x => x.ValueHash).HasMaxLength(128); entity.HasIndex(x => new { x.TenantId, x.SessionId, x.ProductId, x.VariantId, x.FieldName, x.StagingRecordId }).IsUnique();
        });
    }

    private static void ConfigureInventory(ModelBuilder builder)
    {
        builder.Entity<ConnectionInventoryPolicy>(entity =>
        {
            entity.ToTable("connection_inventory_policies", "inventory"); entity.HasKey(x => x.Id); entity.Property(x => x.DefaultSafetyStock).HasPrecision(19, 4); entity.Property(x => x.Version).IsConcurrencyToken(); entity.HasIndex(x => new { x.TenantId, x.ConnectionId }).IsUnique();
        });
        builder.Entity<InventoryLocation>(entity =>
        {
            entity.ToTable("inventory_locations", "inventory"); entity.HasKey(x => x.Id); entity.HasAlternateKey(x => new { x.TenantId, x.Id }); entity.Property(x => x.Code).HasMaxLength(64); entity.Property(x => x.Name).HasMaxLength(160); entity.Property(x => x.Status).HasMaxLength(24); entity.Property(x => x.Version).IsConcurrencyToken(); entity.HasIndex(x => new { x.TenantId, x.Code }).IsUnique();
        });
        builder.Entity<ConnectionLocationMapping>(entity =>
        {
            entity.ToTable("connection_location_mappings", "inventory"); entity.HasKey(x => x.Id); entity.Property(x => x.ExternalLocationId).HasMaxLength(256); entity.Property(x => x.Status).HasMaxLength(24); entity.Property(x => x.Version).IsConcurrencyToken(); entity.HasIndex(x => new { x.TenantId, x.ConnectionId, x.ExternalLocationId }).IsUnique(); entity.HasIndex(x => new { x.TenantId, x.ConnectionId, x.LocationId }).IsUnique(); entity.HasOne<InventoryLocation>().WithMany().HasForeignKey(x => new { x.TenantId, x.LocationId }).HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<InventoryItem>(entity =>
        {
            entity.ToTable("inventory_items", "inventory", table => table.HasCheckConstraint("ck_inventory_items_projection", "\"Reserved\" >= 0 AND \"Available\" = greatest(0,\"OnHand\"-\"Reserved\")")); entity.HasKey(x => x.Id); entity.HasAlternateKey(x => new { x.TenantId, x.Id }); entity.Property(x => x.LocationCode).HasMaxLength(64); entity.Property(x => x.OnHand).HasPrecision(19, 4); entity.Property(x => x.Reserved).HasPrecision(19, 4); entity.Property(x => x.Available).HasPrecision(19, 4); entity.Property(x => x.Version).IsConcurrencyToken(); entity.HasIndex(x => new { x.TenantId, x.VariantId, x.LocationCode }).IsUnique(); entity.HasIndex(x => new { x.TenantId, x.Available }); entity.HasOne<ProductVariant>().WithMany().HasForeignKey(x => new { x.TenantId, x.VariantId }).HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<StockLedgerEntry>(entity =>
        {
            entity.ToTable("stock_ledger_entries", "inventory"); entity.HasKey(x => x.Id); entity.Property(x => x.QuantityDelta).HasPrecision(19, 4); entity.Property(x => x.MovementType).HasMaxLength(48); entity.Property(x => x.SourceType).HasMaxLength(48); entity.Property(x => x.IdempotencyKey).HasMaxLength(256); entity.Property(x => x.CorrelationId).HasMaxLength(128); entity.HasIndex(x => new { x.TenantId, x.IdempotencyKey }).IsUnique(); entity.HasIndex(x => new { x.TenantId, x.InventoryItemId, x.OccurredAt, x.Id }); entity.HasOne<InventoryItem>().WithMany().HasForeignKey(x => new { x.TenantId, x.InventoryItemId }).HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<StockReservation>(entity =>
        {
            entity.ToTable("stock_reservations", "inventory", table => table.HasCheckConstraint("ck_stock_reservation_quantity", "\"Quantity\" > 0")); entity.HasKey(x => x.Id); entity.Property(x => x.Quantity).HasPrecision(19, 4); entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(24); entity.Property(x => x.Version).IsConcurrencyToken(); entity.HasIndex(x => new { x.TenantId, x.SourceType, x.SourceId, x.InventoryItemId }).IsUnique(); entity.HasIndex(x => new { x.TenantId, x.Status, x.ExpiresAt }); entity.HasOne<InventoryItem>().WithMany().HasForeignKey(x => new { x.TenantId, x.InventoryItemId }).HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<ChannelOffer>(entity =>
        {
            entity.ToTable("channel_offers", "inventory", table => { table.HasCheckConstraint("ck_channel_offer_prices", "\"ListPrice\" >= \"SalePrice\" AND \"ListPrice\" >= 0 AND \"SalePrice\" >= 0"); table.HasCheckConstraint("ck_channel_offer_currency", "char_length(\"Currency\")=3"); }); entity.HasKey(x => x.Id); entity.HasAlternateKey(x => new { x.TenantId, x.Id }); entity.Property(x => x.ListPrice).HasPrecision(19, 4); entity.Property(x => x.SalePrice).HasPrecision(19, 4); entity.Property(x => x.Currency).HasColumnType("char(3)"); entity.Property(x => x.VatRate).HasPrecision(7, 4); entity.Property(x => x.SafetyStock).HasPrecision(19, 4); entity.Property(x => x.Version).IsConcurrencyToken(); entity.HasIndex(x => new { x.TenantId, x.ConnectionId, x.VariantId }).IsUnique(); entity.HasOne<ProductVariant>().WithMany().HasForeignKey(x => new { x.TenantId, x.VariantId }).HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<ChannelPriceHistory>(entity =>
        {
            entity.ToTable("channel_price_history", "inventory"); entity.HasKey(x => x.Id); entity.Property(x => x.ListPrice).HasPrecision(19, 4); entity.Property(x => x.SalePrice).HasPrecision(19, 4); entity.Property(x => x.Currency).HasColumnType("char(3)"); entity.HasIndex(x => new { x.TenantId, x.OfferId, x.PriceVersion }).IsUnique(); entity.HasOne<ChannelOffer>().WithMany().HasForeignKey(x => new { x.TenantId, x.OfferId }).HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        });
    }

    private static void ConfigureMapping<TMapping>(ModelBuilder builder, string tableName) where TMapping : CatalogMapping
    {
        builder.Entity<TMapping>(entity =>
        {
            entity.ToTable(tableName, "catalog"); entity.Property(x => x.ScopeExternalId).HasMaxLength(512); entity.Property(x => x.ExternalId).HasMaxLength(256); entity.Property(x => x.Status).HasMaxLength(24); entity.Property(x => x.Version).IsConcurrencyToken(); entity.HasIndex(x => new { x.TenantId, x.ConnectionId, x.LocalId, x.ScopeExternalId }).IsUnique();
        });
    }

    private static string AttributeDataTypeWire(AttributeDataType value) => value switch
    {
        AttributeDataType.Text => "TEXT",
        AttributeDataType.Number => "NUMBER",
        AttributeDataType.SingleSelect => "SINGLE_SELECT",
        AttributeDataType.MultiSelect => "MULTI_SELECT",
        AttributeDataType.Boolean => "BOOLEAN",
        _ => throw new InvalidOperationException("Unknown attribute data type.")
    };

    private static AttributeDataType ParseAttributeDataType(string value) => value switch
    {
        "TEXT" => AttributeDataType.Text,
        "NUMBER" => AttributeDataType.Number,
        "SINGLE_SELECT" => AttributeDataType.SingleSelect,
        "MULTI_SELECT" => AttributeDataType.MultiSelect,
        "BOOLEAN" => AttributeDataType.Boolean,
        _ => throw new InvalidOperationException("Unknown attribute data type value.")
    };

    private static string ImportStatusWire(ImportSessionStatus value) => value switch
    {
        ImportSessionStatus.Created => "CREATED",
        ImportSessionStatus.Fetching => "FETCHING",
        ImportSessionStatus.Matching => "MATCHING",
        ImportSessionStatus.ReviewRequired => "REVIEW_REQUIRED",
        ImportSessionStatus.ReadyToApply => "READY_TO_APPLY",
        ImportSessionStatus.Applying => "APPLYING",
        ImportSessionStatus.Completed => "COMPLETED",
        ImportSessionStatus.PartiallyCompleted => "PARTIALLY_COMPLETED",
        ImportSessionStatus.Failed => "FAILED",
        ImportSessionStatus.Cancelled => "CANCELLED",
        _ => throw new InvalidOperationException("Unknown import status.")
    };

    private static ImportSessionStatus ParseImportStatus(string value) => value switch
    {
        "CREATED" => ImportSessionStatus.Created,
        "FETCHING" => ImportSessionStatus.Fetching,
        "MATCHING" => ImportSessionStatus.Matching,
        "REVIEW_REQUIRED" => ImportSessionStatus.ReviewRequired,
        "READY_TO_APPLY" => ImportSessionStatus.ReadyToApply,
        "APPLYING" => ImportSessionStatus.Applying,
        "COMPLETED" => ImportSessionStatus.Completed,
        "PARTIALLY_COMPLETED" => ImportSessionStatus.PartiallyCompleted,
        "FAILED" => ImportSessionStatus.Failed,
        "CANCELLED" => ImportSessionStatus.Cancelled,
        _ => throw new InvalidOperationException("Unknown import status value.")
    };
}
