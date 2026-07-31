using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceHub.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class F2CatalogInventoryCore : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.EnsureSchema(
            name: "catalog");

        migrationBuilder.EnsureSchema(
            name: "inventory");

        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "ArchivedAt",
            schema: "ops",
            table: "file_assets",
            type: "timestamp with time zone",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "Classification",
            schema: "ops",
            table: "file_assets",
            type: "character varying(48)",
            maxLength: 48,
            nullable: false,
            defaultValue: "");

        migrationBuilder.AddColumn<string>(
            name: "OriginalNameSafe",
            schema: "ops",
            table: "file_assets",
            type: "character varying(256)",
            maxLength: 256,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "Status",
            schema: "ops",
            table: "file_assets",
            type: "character varying(24)",
            maxLength: 24,
            nullable: false,
            defaultValue: "");

        migrationBuilder.CreateTable(
            name: "api_idempotency_records",
            schema: "ops",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                RouteTemplate = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                IdempotencyKey = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                RequestHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                State = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                ResponseStatus = table.Column<int>(type: "integer", nullable: true),
                ResourceId = table.Column<Guid>(type: "uuid", nullable: true),
                JobId = table.Column<Guid>(type: "uuid", nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_api_idempotency_records", x => x.Id);
                table.ForeignKey(
                    name: "FK_api_idempotency_records_tenants_TenantId",
                    column: x => x.TenantId,
                    principalSchema: "iam",
                    principalTable: "tenants",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "attribute_definitions",
            schema: "catalog",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                Code = table.Column<string>(type: "character varying(96)", maxLength: 96, nullable: false),
                Name = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                DataType = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                SelectionMode = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: true),
                Unit = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                IsActive = table.Column<bool>(type: "boolean", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                Version = table.Column<long>(type: "bigint", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_attribute_definitions", x => x.Id);
                table.UniqueConstraint("AK_attribute_definitions_TenantId_Id", x => new { x.TenantId, x.Id });
                table.CheckConstraint("ck_attribute_definition_data_type", "\"DataType\" IN ('TEXT','NUMBER','SINGLE_SELECT','MULTI_SELECT','BOOLEAN')");
                table.ForeignKey(
                    name: "FK_attribute_definitions_tenants_TenantId",
                    column: x => x.TenantId,
                    principalSchema: "iam",
                    principalTable: "tenants",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "attribute_mappings",
            schema: "catalog",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                ConnectionId = table.Column<Guid>(type: "uuid", nullable: false),
                SnapshotId = table.Column<Guid>(type: "uuid", nullable: false),
                LocalId = table.Column<Guid>(type: "uuid", nullable: false),
                ExternalId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                Status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                VerifiedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                Version = table.Column<long>(type: "bigint", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_attribute_mappings", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "attribute_value_mappings",
            schema: "catalog",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                ConnectionId = table.Column<Guid>(type: "uuid", nullable: false),
                SnapshotId = table.Column<Guid>(type: "uuid", nullable: false),
                LocalId = table.Column<Guid>(type: "uuid", nullable: false),
                ExternalId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                Status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                VerifiedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                Version = table.Column<long>(type: "bigint", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_attribute_value_mappings", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "brand_mappings",
            schema: "catalog",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                ConnectionId = table.Column<Guid>(type: "uuid", nullable: false),
                SnapshotId = table.Column<Guid>(type: "uuid", nullable: false),
                LocalId = table.Column<Guid>(type: "uuid", nullable: false),
                ExternalId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                Status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                VerifiedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                Version = table.Column<long>(type: "bigint", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_brand_mappings", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "brands",
            schema: "catalog",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                Name = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                NormalizedName = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                IsActive = table.Column<bool>(type: "boolean", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                Version = table.Column<long>(type: "bigint", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_brands", x => x.Id);
                table.UniqueConstraint("AK_brands_TenantId_Id", x => new { x.TenantId, x.Id });
                table.ForeignKey(
                    name: "FK_brands_tenants_TenantId",
                    column: x => x.TenantId,
                    principalSchema: "iam",
                    principalTable: "tenants",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "categories",
            schema: "catalog",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                ParentId = table.Column<Guid>(type: "uuid", nullable: true),
                Name = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                NormalizedName = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                Path = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                Depth = table.Column<int>(type: "integer", nullable: false),
                IsLeaf = table.Column<bool>(type: "boolean", nullable: false),
                IsActive = table.Column<bool>(type: "boolean", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                Version = table.Column<long>(type: "bigint", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_categories", x => x.Id);
                table.UniqueConstraint("AK_categories_TenantId_Id", x => new { x.TenantId, x.Id });
                table.ForeignKey(
                    name: "FK_categories_categories_TenantId_ParentId",
                    columns: x => new { x.TenantId, x.ParentId },
                    principalSchema: "catalog",
                    principalTable: "categories",
                    principalColumns: new[] { "TenantId", "Id" },
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_categories_tenants_TenantId",
                    column: x => x.TenantId,
                    principalSchema: "iam",
                    principalTable: "tenants",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "category_mappings",
            schema: "catalog",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                ConnectionId = table.Column<Guid>(type: "uuid", nullable: false),
                SnapshotId = table.Column<Guid>(type: "uuid", nullable: false),
                LocalId = table.Column<Guid>(type: "uuid", nullable: false),
                ExternalId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                Status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                VerifiedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                Version = table.Column<long>(type: "bigint", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_category_mappings", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "connection_inventory_policies",
            schema: "inventory",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                ConnectionId = table.Column<Guid>(type: "uuid", nullable: false),
                AuthorityMode = table.Column<string>(type: "text", nullable: false),
                ReservationMode = table.Column<string>(type: "text", nullable: false),
                ReserveOnStatuses = table.Column<string>(type: "text", nullable: false),
                ReleaseOnStatuses = table.Column<string>(type: "text", nullable: false),
                NegativeStockAllowed = table.Column<bool>(type: "boolean", nullable: false),
                DefaultSafetyStock = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                Version = table.Column<long>(type: "bigint", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_connection_inventory_policies", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "external_identifier_aliases",
            schema: "catalog",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                ConnectionId = table.Column<Guid>(type: "uuid", nullable: false),
                EntityType = table.Column<string>(type: "text", nullable: false),
                LocalId = table.Column<Guid>(type: "uuid", nullable: false),
                ExternalId = table.Column<string>(type: "text", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_external_identifier_aliases", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "field_provenance",
            schema: "catalog",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                SessionId = table.Column<Guid>(type: "uuid", nullable: false),
                ProductId = table.Column<Guid>(type: "uuid", nullable: false),
                VariantId = table.Column<Guid>(type: "uuid", nullable: true),
                FieldName = table.Column<string>(type: "character varying(96)", maxLength: 96, nullable: false),
                StagingRecordId = table.Column<Guid>(type: "uuid", nullable: false),
                ValueHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                AppliedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_field_provenance", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "import_column_profiles",
            schema: "catalog",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                Name = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                Version = table.Column<long>(type: "bigint", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_import_column_profiles", x => x.Id);
                table.UniqueConstraint("AK_import_column_profiles_TenantId_Id", x => new { x.TenantId, x.Id });
            });

        migrationBuilder.CreateTable(
            name: "import_match_candidates",
            schema: "catalog",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                SessionId = table.Column<Guid>(type: "uuid", nullable: false),
                StagingRecordId = table.Column<Guid>(type: "uuid", nullable: false),
                ProductId = table.Column<Guid>(type: "uuid", nullable: true),
                VariantId = table.Column<Guid>(type: "uuid", nullable: true),
                MatchRule = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: false),
                Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                SafeSummary = table.Column<string>(type: "text", nullable: false),
                Version = table.Column<long>(type: "bigint", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_import_match_candidates", x => x.Id);
                table.UniqueConstraint("AK_import_match_candidates_TenantId_Id", x => new { x.TenantId, x.Id });
            });

        migrationBuilder.CreateTable(
            name: "import_sessions",
            schema: "catalog",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                SourceType = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                ConnectionId = table.Column<Guid>(type: "uuid", nullable: true),
                SourceAssetId = table.Column<Guid>(type: "uuid", nullable: true),
                ColumnProfileId = table.Column<Guid>(type: "uuid", nullable: true),
                Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                VariantGroupKey = table.Column<string>(type: "text", nullable: true),
                TotalRows = table.Column<int>(type: "integer", nullable: false),
                ValidRows = table.Column<int>(type: "integer", nullable: false),
                ErrorRows = table.Column<int>(type: "integer", nullable: false),
                ReviewRows = table.Column<int>(type: "integer", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                Version = table.Column<long>(type: "bigint", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_import_sessions", x => x.Id);
                table.UniqueConstraint("AK_import_sessions_TenantId_Id", x => new { x.TenantId, x.Id });
            });

        migrationBuilder.CreateTable(
            name: "inventory_locations",
            schema: "inventory",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                Code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                Name = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                Status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                Priority = table.Column<int>(type: "integer", nullable: false),
                Version = table.Column<long>(type: "bigint", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_inventory_locations", x => x.Id);
                table.UniqueConstraint("AK_inventory_locations_TenantId_Id", x => new { x.TenantId, x.Id });
            });

        migrationBuilder.CreateTable(
            name: "marketplace_listing_states",
            schema: "catalog",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                ConnectionId = table.Column<Guid>(type: "uuid", nullable: false),
                VariantId = table.Column<Guid>(type: "uuid", nullable: false),
                DesiredStatus = table.Column<string>(type: "text", nullable: false),
                ActualStatus = table.Column<string>(type: "text", nullable: false),
                LastRejectionCode = table.Column<string>(type: "text", nullable: true),
                PayloadHash = table.Column<string>(type: "text", nullable: true),
                Version = table.Column<long>(type: "bigint", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_marketplace_listing_states", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "platform_connections",
            schema: "integration",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                PublicId = table.Column<Guid>(type: "uuid", nullable: false),
                PlatformCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                Environment = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                DisplayName = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                ExternalStoreId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                Status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                ApiVersion = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                SettingsJson = table.Column<string>(type: "text", nullable: false),
                LastTestedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                LastSuccessAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                LastErrorCode = table.Column<string>(type: "text", nullable: true),
                Version = table.Column<long>(type: "bigint", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_platform_connections", x => x.Id);
                table.UniqueConstraint("AK_platform_connections_TenantId_Id", x => new { x.TenantId, x.Id });
                table.ForeignKey(
                    name: "FK_platform_connections_tenants_TenantId",
                    column: x => x.TenantId,
                    principalSchema: "iam",
                    principalTable: "tenants",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "attribute_values",
            schema: "catalog",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                AttributeId = table.Column<Guid>(type: "uuid", nullable: false),
                Value = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                NormalizedValue = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                SortOrder = table.Column<int>(type: "integer", nullable: false),
                IsActive = table.Column<bool>(type: "boolean", nullable: false),
                Version = table.Column<long>(type: "bigint", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_attribute_values", x => x.Id);
                table.UniqueConstraint("AK_attribute_values_TenantId_Id", x => new { x.TenantId, x.Id });
                table.ForeignKey(
                    name: "FK_attribute_values_attribute_definitions_TenantId_AttributeId",
                    columns: x => new { x.TenantId, x.AttributeId },
                    principalSchema: "catalog",
                    principalTable: "attribute_definitions",
                    principalColumns: new[] { "TenantId", "Id" },
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "category_attribute_requirements",
            schema: "catalog",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                CategoryId = table.Column<Guid>(type: "uuid", nullable: false),
                AttributeId = table.Column<Guid>(type: "uuid", nullable: false),
                IsRequired = table.Column<bool>(type: "boolean", nullable: false),
                AllowsCustomValue = table.Column<bool>(type: "boolean", nullable: false),
                DisplayOrder = table.Column<int>(type: "integer", nullable: false),
                Version = table.Column<long>(type: "bigint", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_category_attribute_requirements", x => x.Id);
                table.ForeignKey(
                    name: "FK_category_attribute_requirements_attribute_definitions_Tenan~",
                    columns: x => new { x.TenantId, x.AttributeId },
                    principalSchema: "catalog",
                    principalTable: "attribute_definitions",
                    principalColumns: new[] { "TenantId", "Id" },
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_category_attribute_requirements_categories_TenantId_Categor~",
                    columns: x => new { x.TenantId, x.CategoryId },
                    principalSchema: "catalog",
                    principalTable: "categories",
                    principalColumns: new[] { "TenantId", "Id" },
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "products",
            schema: "catalog",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                Title = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                Description = table.Column<string>(type: "text", nullable: false),
                BrandId = table.Column<Guid>(type: "uuid", nullable: true),
                CategoryId = table.Column<Guid>(type: "uuid", nullable: true),
                Status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                SourcePolicyVersion = table.Column<long>(type: "bigint", nullable: false),
                ArchivedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                Version = table.Column<long>(type: "bigint", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_products", x => x.Id);
                table.UniqueConstraint("AK_products_TenantId_Id", x => new { x.TenantId, x.Id });
                table.ForeignKey(
                    name: "FK_products_brands_TenantId_BrandId",
                    columns: x => new { x.TenantId, x.BrandId },
                    principalSchema: "catalog",
                    principalTable: "brands",
                    principalColumns: new[] { "TenantId", "Id" },
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_products_categories_TenantId_CategoryId",
                    columns: x => new { x.TenantId, x.CategoryId },
                    principalSchema: "catalog",
                    principalTable: "categories",
                    principalColumns: new[] { "TenantId", "Id" },
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_products_tenants_TenantId",
                    column: x => x.TenantId,
                    principalSchema: "iam",
                    principalTable: "tenants",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "import_column_mappings",
            schema: "catalog",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                ProfileId = table.Column<Guid>(type: "uuid", nullable: false),
                SourceColumn = table.Column<string>(type: "text", nullable: false),
                TargetField = table.Column<string>(type: "text", nullable: false),
                SortOrder = table.Column<int>(type: "integer", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_import_column_mappings", x => x.Id);
                table.ForeignKey(
                    name: "FK_import_column_mappings_import_column_profiles_TenantId_Prof~",
                    columns: x => new { x.TenantId, x.ProfileId },
                    principalSchema: "catalog",
                    principalTable: "import_column_profiles",
                    principalColumns: new[] { "TenantId", "Id" },
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "import_decisions",
            schema: "catalog",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                CandidateId = table.Column<Guid>(type: "uuid", nullable: false),
                Decision = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                LinkProductId = table.Column<Guid>(type: "uuid", nullable: true),
                LinkVariantId = table.Column<Guid>(type: "uuid", nullable: true),
                ActorUserId = table.Column<Guid>(type: "uuid", nullable: false),
                DecidedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                Version = table.Column<long>(type: "bigint", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_import_decisions", x => x.Id);
                table.ForeignKey(
                    name: "FK_import_decisions_import_match_candidates_TenantId_Candidate~",
                    columns: x => new { x.TenantId, x.CandidateId },
                    principalSchema: "catalog",
                    principalTable: "import_match_candidates",
                    principalColumns: new[] { "TenantId", "Id" },
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "import_staging_records",
            schema: "catalog",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                SessionId = table.Column<Guid>(type: "uuid", nullable: false),
                RowNumber = table.Column<int>(type: "integer", nullable: false),
                ExternalRecordId = table.Column<string>(type: "text", nullable: true),
                RawJson = table.Column<string>(type: "text", nullable: false),
                SafeValuesJson = table.Column<string>(type: "text", nullable: false),
                ValidationErrorsJson = table.Column<string>(type: "text", nullable: false),
                RowHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                SkuNormalized = table.Column<string>(type: "text", nullable: true),
                BarcodeNormalized = table.Column<string>(type: "text", nullable: true),
                ReviewStatus = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_import_staging_records", x => x.Id);
                table.UniqueConstraint("AK_import_staging_records_TenantId_Id", x => new { x.TenantId, x.Id });
                table.ForeignKey(
                    name: "FK_import_staging_records_import_sessions_TenantId_SessionId",
                    columns: x => new { x.TenantId, x.SessionId },
                    principalSchema: "catalog",
                    principalTable: "import_sessions",
                    principalColumns: new[] { "TenantId", "Id" },
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "connection_location_mappings",
            schema: "inventory",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                ConnectionId = table.Column<Guid>(type: "uuid", nullable: false),
                LocationId = table.Column<Guid>(type: "uuid", nullable: false),
                ExternalLocationId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                Status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                Version = table.Column<long>(type: "bigint", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_connection_location_mappings", x => x.Id);
                table.ForeignKey(
                    name: "FK_connection_location_mappings_inventory_locations_TenantId_L~",
                    columns: x => new { x.TenantId, x.LocationId },
                    principalSchema: "inventory",
                    principalTable: "inventory_locations",
                    principalColumns: new[] { "TenantId", "Id" },
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "reference_snapshots",
            schema: "integration",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                ConnectionId = table.Column<Guid>(type: "uuid", nullable: false),
                ResourceType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                SourceVersion = table.Column<string>(type: "text", nullable: false),
                ContentHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                AssetId = table.Column<Guid>(type: "uuid", nullable: true),
                FetchedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                IsCurrent = table.Column<bool>(type: "boolean", nullable: false),
                ItemCount = table.Column<int>(type: "integer", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_reference_snapshots", x => x.Id);
                table.UniqueConstraint("AK_reference_snapshots_TenantId_Id", x => new { x.TenantId, x.Id });
                table.ForeignKey(
                    name: "FK_reference_snapshots_platform_connections_TenantId_Connectio~",
                    columns: x => new { x.TenantId, x.ConnectionId },
                    principalSchema: "integration",
                    principalTable: "platform_connections",
                    principalColumns: new[] { "TenantId", "Id" },
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "channel_listing_profiles",
            schema: "catalog",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                ConnectionId = table.Column<Guid>(type: "uuid", nullable: false),
                ProductId = table.Column<Guid>(type: "uuid", nullable: false),
                TitleOverride = table.Column<string>(type: "text", nullable: true),
                DescriptionOverride = table.Column<string>(type: "text", nullable: true),
                ExternalCategoryId = table.Column<string>(type: "text", nullable: true),
                ExternalBrandId = table.Column<string>(type: "text", nullable: true),
                DeliveryTimeDays = table.Column<int>(type: "integer", nullable: true),
                CargoProfile = table.Column<string>(type: "text", nullable: true),
                Origin = table.Column<string>(type: "text", nullable: true),
                Warranty = table.Column<string>(type: "text", nullable: true),
                PackageContent = table.Column<string>(type: "text", nullable: true),
                VatOverride = table.Column<decimal>(type: "numeric(7,4)", precision: 7, scale: 4, nullable: true),
                Enabled = table.Column<bool>(type: "boolean", nullable: false),
                DesiredStatus = table.Column<string>(type: "text", nullable: false),
                ActualStatus = table.Column<string>(type: "text", nullable: false),
                LastRejectionCode = table.Column<string>(type: "text", nullable: true),
                Version = table.Column<long>(type: "bigint", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_channel_listing_profiles", x => x.Id);
                table.UniqueConstraint("AK_channel_listing_profiles_TenantId_Id", x => new { x.TenantId, x.Id });
                table.ForeignKey(
                    name: "FK_channel_listing_profiles_products_TenantId_ProductId",
                    columns: x => new { x.TenantId, x.ProductId },
                    principalSchema: "catalog",
                    principalTable: "products",
                    principalColumns: new[] { "TenantId", "Id" },
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "marketplace_product_links",
            schema: "catalog",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                ConnectionId = table.Column<Guid>(type: "uuid", nullable: false),
                ProductId = table.Column<Guid>(type: "uuid", nullable: false),
                ExternalId = table.Column<string>(type: "text", nullable: false),
                Version = table.Column<long>(type: "bigint", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_marketplace_product_links", x => x.Id);
                table.ForeignKey(
                    name: "FK_marketplace_product_links_products_TenantId_ProductId",
                    columns: x => new { x.TenantId, x.ProductId },
                    principalSchema: "catalog",
                    principalTable: "products",
                    principalColumns: new[] { "TenantId", "Id" },
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "product_options",
            schema: "catalog",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                ProductId = table.Column<Guid>(type: "uuid", nullable: false),
                Label = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                NormalizedKey = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                SortOrder = table.Column<int>(type: "integer", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_product_options", x => x.Id);
                table.UniqueConstraint("AK_product_options_TenantId_Id", x => new { x.TenantId, x.Id });
                table.ForeignKey(
                    name: "FK_product_options_products_TenantId_ProductId",
                    columns: x => new { x.TenantId, x.ProductId },
                    principalSchema: "catalog",
                    principalTable: "products",
                    principalColumns: new[] { "TenantId", "Id" },
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "product_variants",
            schema: "catalog",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                ProductId = table.Column<Guid>(type: "uuid", nullable: false),
                Sku = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                SkuNormalized = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                Barcode = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                BarcodeNormalized = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                ModelCode = table.Column<string>(type: "text", nullable: true),
                OptionSignature = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                Status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                Weight = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: true),
                Width = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: true),
                Height = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: true),
                Length = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                Version = table.Column<long>(type: "bigint", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_product_variants", x => x.Id);
                table.UniqueConstraint("AK_product_variants_TenantId_Id", x => new { x.TenantId, x.Id });
                table.ForeignKey(
                    name: "FK_product_variants_products_TenantId_ProductId",
                    columns: x => new { x.TenantId, x.ProductId },
                    principalSchema: "catalog",
                    principalTable: "products",
                    principalColumns: new[] { "TenantId", "Id" },
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "reference_items",
            schema: "integration",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                ConnectionId = table.Column<Guid>(type: "uuid", nullable: false),
                SnapshotId = table.Column<Guid>(type: "uuid", nullable: false),
                ResourceType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                ExternalId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                ParentExternalId = table.Column<string>(type: "text", nullable: true),
                Name = table.Column<string>(type: "text", nullable: false),
                NormalizedName = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                Path = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                Depth = table.Column<int>(type: "integer", nullable: false),
                IsLeaf = table.Column<bool>(type: "boolean", nullable: false),
                IsActive = table.Column<bool>(type: "boolean", nullable: false),
                PayloadHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                SortOrder = table.Column<int>(type: "integer", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_reference_items", x => x.Id);
                table.ForeignKey(
                    name: "FK_reference_items_reference_snapshots_TenantId_SnapshotId",
                    columns: x => new { x.TenantId, x.SnapshotId },
                    principalSchema: "integration",
                    principalTable: "reference_snapshots",
                    principalColumns: new[] { "TenantId", "Id" },
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "channel_listing_attributes",
            schema: "catalog",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                ProfileId = table.Column<Guid>(type: "uuid", nullable: false),
                AttributeId = table.Column<Guid>(type: "uuid", nullable: false),
                ExternalAttributeId = table.Column<string>(type: "text", nullable: true),
                ExternalValueId = table.Column<string>(type: "text", nullable: true),
                CustomValue = table.Column<string>(type: "text", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_channel_listing_attributes", x => x.Id);
                table.ForeignKey(
                    name: "FK_channel_listing_attributes_channel_listing_profiles_TenantI~",
                    columns: x => new { x.TenantId, x.ProfileId },
                    principalSchema: "catalog",
                    principalTable: "channel_listing_profiles",
                    principalColumns: new[] { "TenantId", "Id" },
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "channel_listing_variants",
            schema: "catalog",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                ProfileId = table.Column<Guid>(type: "uuid", nullable: false),
                VariantId = table.Column<Guid>(type: "uuid", nullable: false),
                ExternalSku = table.Column<string>(type: "text", nullable: true),
                ExternalBarcode = table.Column<string>(type: "text", nullable: true),
                DesiredStatus = table.Column<string>(type: "text", nullable: false),
                ActualStatus = table.Column<string>(type: "text", nullable: false),
                RejectionCode = table.Column<string>(type: "text", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_channel_listing_variants", x => x.Id);
                table.ForeignKey(
                    name: "FK_channel_listing_variants_channel_listing_profiles_TenantId_~",
                    columns: x => new { x.TenantId, x.ProfileId },
                    principalSchema: "catalog",
                    principalTable: "channel_listing_profiles",
                    principalColumns: new[] { "TenantId", "Id" },
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "channel_media_order",
            schema: "catalog",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                ProfileId = table.Column<Guid>(type: "uuid", nullable: false),
                MediaId = table.Column<Guid>(type: "uuid", nullable: false),
                SortOrder = table.Column<int>(type: "integer", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_channel_media_order", x => x.Id);
                table.ForeignKey(
                    name: "FK_channel_media_order_channel_listing_profiles_TenantId_Profi~",
                    columns: x => new { x.TenantId, x.ProfileId },
                    principalSchema: "catalog",
                    principalTable: "channel_listing_profiles",
                    principalColumns: new[] { "TenantId", "Id" },
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "product_option_values",
            schema: "catalog",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                OptionId = table.Column<Guid>(type: "uuid", nullable: false),
                Label = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                NormalizedKey = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                SortOrder = table.Column<int>(type: "integer", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_product_option_values", x => x.Id);
                table.UniqueConstraint("AK_product_option_values_TenantId_Id", x => new { x.TenantId, x.Id });
                table.ForeignKey(
                    name: "FK_product_option_values_product_options_TenantId_OptionId",
                    columns: x => new { x.TenantId, x.OptionId },
                    principalSchema: "catalog",
                    principalTable: "product_options",
                    principalColumns: new[] { "TenantId", "Id" },
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "channel_offers",
            schema: "inventory",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                ConnectionId = table.Column<Guid>(type: "uuid", nullable: false),
                VariantId = table.Column<Guid>(type: "uuid", nullable: false),
                ListPrice = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                SalePrice = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                Currency = table.Column<string>(type: "char(3)", nullable: false),
                VatRate = table.Column<decimal>(type: "numeric(7,4)", precision: 7, scale: 4, nullable: false),
                VatInclusion = table.Column<string>(type: "text", nullable: false),
                RoundingMode = table.Column<string>(type: "text", nullable: false),
                SafetyStock = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                Status = table.Column<string>(type: "text", nullable: false),
                PriceVersion = table.Column<long>(type: "bigint", nullable: false),
                LastPriceHash = table.Column<string>(type: "text", nullable: true),
                LastStockProjectionVersion = table.Column<long>(type: "bigint", nullable: true),
                Version = table.Column<long>(type: "bigint", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_channel_offers", x => x.Id);
                table.UniqueConstraint("AK_channel_offers_TenantId_Id", x => new { x.TenantId, x.Id });
                table.CheckConstraint("ck_channel_offer_currency", "char_length(\"Currency\")=3");
                table.CheckConstraint("ck_channel_offer_prices", "\"ListPrice\" >= \"SalePrice\" AND \"ListPrice\" >= 0 AND \"SalePrice\" >= 0");
                table.ForeignKey(
                    name: "FK_channel_offers_product_variants_TenantId_VariantId",
                    columns: x => new { x.TenantId, x.VariantId },
                    principalSchema: "catalog",
                    principalTable: "product_variants",
                    principalColumns: new[] { "TenantId", "Id" },
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "inventory_items",
            schema: "inventory",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                VariantId = table.Column<Guid>(type: "uuid", nullable: false),
                LocationCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                OnHand = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                Reserved = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                Available = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                ProjectionVersion = table.Column<long>(type: "bigint", nullable: false),
                ReconciledAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                Version = table.Column<long>(type: "bigint", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_inventory_items", x => x.Id);
                table.UniqueConstraint("AK_inventory_items_TenantId_Id", x => new { x.TenantId, x.Id });
                table.CheckConstraint("ck_inventory_items_projection", "\"Reserved\" >= 0 AND \"Available\" = greatest(0,\"OnHand\"-\"Reserved\")");
                table.ForeignKey(
                    name: "FK_inventory_items_product_variants_TenantId_VariantId",
                    columns: x => new { x.TenantId, x.VariantId },
                    principalSchema: "catalog",
                    principalTable: "product_variants",
                    principalColumns: new[] { "TenantId", "Id" },
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "marketplace_variant_links",
            schema: "catalog",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                ConnectionId = table.Column<Guid>(type: "uuid", nullable: false),
                VariantId = table.Column<Guid>(type: "uuid", nullable: false),
                ExternalId = table.Column<string>(type: "text", nullable: false),
                Version = table.Column<long>(type: "bigint", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_marketplace_variant_links", x => x.Id);
                table.ForeignKey(
                    name: "FK_marketplace_variant_links_product_variants_TenantId_Variant~",
                    columns: x => new { x.TenantId, x.VariantId },
                    principalSchema: "catalog",
                    principalTable: "product_variants",
                    principalColumns: new[] { "TenantId", "Id" },
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "product_attribute_assignments",
            schema: "catalog",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                ProductId = table.Column<Guid>(type: "uuid", nullable: false),
                VariantId = table.Column<Guid>(type: "uuid", nullable: true),
                AttributeId = table.Column<Guid>(type: "uuid", nullable: false),
                ValueId = table.Column<Guid>(type: "uuid", nullable: true),
                TextValue = table.Column<string>(type: "text", nullable: true),
                NumberValue = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: true),
                BooleanValue = table.Column<bool>(type: "boolean", nullable: true),
                SortOrder = table.Column<int>(type: "integer", nullable: false),
                Version = table.Column<long>(type: "bigint", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_product_attribute_assignments", x => x.Id);
                table.CheckConstraint("ck_product_attribute_assignments_exactly_one_value", "num_nonnulls(\"ValueId\",\"TextValue\",\"NumberValue\",\"BooleanValue\")=1");
                table.ForeignKey(
                    name: "FK_product_attribute_assignments_attribute_definitions_TenantI~",
                    columns: x => new { x.TenantId, x.AttributeId },
                    principalSchema: "catalog",
                    principalTable: "attribute_definitions",
                    principalColumns: new[] { "TenantId", "Id" },
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_product_attribute_assignments_attribute_values_TenantId_Val~",
                    columns: x => new { x.TenantId, x.ValueId },
                    principalSchema: "catalog",
                    principalTable: "attribute_values",
                    principalColumns: new[] { "TenantId", "Id" },
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_product_attribute_assignments_product_variants_TenantId_Var~",
                    columns: x => new { x.TenantId, x.VariantId },
                    principalSchema: "catalog",
                    principalTable: "product_variants",
                    principalColumns: new[] { "TenantId", "Id" },
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_product_attribute_assignments_products_TenantId_ProductId",
                    columns: x => new { x.TenantId, x.ProductId },
                    principalSchema: "catalog",
                    principalTable: "products",
                    principalColumns: new[] { "TenantId", "Id" },
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "product_media",
            schema: "catalog",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                ProductId = table.Column<Guid>(type: "uuid", nullable: false),
                VariantId = table.Column<Guid>(type: "uuid", nullable: true),
                FileAssetId = table.Column<Guid>(type: "uuid", nullable: false),
                MediaRole = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                SortOrder = table.Column<int>(type: "integer", nullable: false),
                AltText = table.Column<string>(type: "text", nullable: true),
                Status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_product_media", x => x.Id);
                table.ForeignKey(
                    name: "FK_product_media_file_assets_FileAssetId",
                    column: x => x.FileAssetId,
                    principalSchema: "ops",
                    principalTable: "file_assets",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_product_media_product_variants_TenantId_VariantId",
                    columns: x => new { x.TenantId, x.VariantId },
                    principalSchema: "catalog",
                    principalTable: "product_variants",
                    principalColumns: new[] { "TenantId", "Id" },
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_product_media_products_TenantId_ProductId",
                    columns: x => new { x.TenantId, x.ProductId },
                    principalSchema: "catalog",
                    principalTable: "products",
                    principalColumns: new[] { "TenantId", "Id" },
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "variant_option_values",
            schema: "catalog",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                VariantId = table.Column<Guid>(type: "uuid", nullable: false),
                OptionId = table.Column<Guid>(type: "uuid", nullable: false),
                OptionValueId = table.Column<Guid>(type: "uuid", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_variant_option_values", x => x.Id);
                table.ForeignKey(
                    name: "FK_variant_option_values_product_option_values_TenantId_Option~",
                    columns: x => new { x.TenantId, x.OptionValueId },
                    principalSchema: "catalog",
                    principalTable: "product_option_values",
                    principalColumns: new[] { "TenantId", "Id" },
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_variant_option_values_product_variants_TenantId_VariantId",
                    columns: x => new { x.TenantId, x.VariantId },
                    principalSchema: "catalog",
                    principalTable: "product_variants",
                    principalColumns: new[] { "TenantId", "Id" },
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "channel_price_history",
            schema: "inventory",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                OfferId = table.Column<Guid>(type: "uuid", nullable: false),
                PriceVersion = table.Column<long>(type: "bigint", nullable: false),
                ListPrice = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                SalePrice = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                Currency = table.Column<string>(type: "char(3)", nullable: false),
                Reason = table.Column<string>(type: "text", nullable: false),
                ActorSource = table.Column<string>(type: "text", nullable: false),
                EffectiveAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_channel_price_history", x => x.Id);
                table.ForeignKey(
                    name: "FK_channel_price_history_channel_offers_TenantId_OfferId",
                    columns: x => new { x.TenantId, x.OfferId },
                    principalSchema: "inventory",
                    principalTable: "channel_offers",
                    principalColumns: new[] { "TenantId", "Id" },
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "stock_ledger_entries",
            schema: "inventory",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                InventoryItemId = table.Column<Guid>(type: "uuid", nullable: false),
                MovementType = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: false),
                QuantityDelta = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                SourceType = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: false),
                SourceId = table.Column<string>(type: "text", nullable: false),
                SourceEventId = table.Column<string>(type: "text", nullable: false),
                IdempotencyKey = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                RecordedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                ActorUserId = table.Column<Guid>(type: "uuid", nullable: true),
                CorrelationId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_stock_ledger_entries", x => x.Id);
                table.ForeignKey(
                    name: "FK_stock_ledger_entries_inventory_items_TenantId_InventoryItem~",
                    columns: x => new { x.TenantId, x.InventoryItemId },
                    principalSchema: "inventory",
                    principalTable: "inventory_items",
                    principalColumns: new[] { "TenantId", "Id" },
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "stock_reservations",
            schema: "inventory",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                InventoryItemId = table.Column<Guid>(type: "uuid", nullable: false),
                SourceType = table.Column<string>(type: "text", nullable: false),
                SourceId = table.Column<string>(type: "text", nullable: false),
                Quantity = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                Status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                ReleasedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                Version = table.Column<long>(type: "bigint", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_stock_reservations", x => x.Id);
                table.CheckConstraint("ck_stock_reservation_quantity", "\"Quantity\" > 0");
                table.ForeignKey(
                    name: "FK_stock_reservations_inventory_items_TenantId_InventoryItemId",
                    columns: x => new { x.TenantId, x.InventoryItemId },
                    principalSchema: "inventory",
                    principalTable: "inventory_items",
                    principalColumns: new[] { "TenantId", "Id" },
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex(
            name: "IX_file_assets_TenantId_Sha256_Classification",
            schema: "ops",
            table: "file_assets",
            columns: new[] { "TenantId", "Sha256", "Classification" });

        migrationBuilder.CreateIndex(
            name: "IX_api_idempotency_records_ExpiresAt",
            schema: "ops",
            table: "api_idempotency_records",
            column: "ExpiresAt");

        migrationBuilder.CreateIndex(
            name: "IX_api_idempotency_records_TenantId_RouteTemplate_IdempotencyK~",
            schema: "ops",
            table: "api_idempotency_records",
            columns: new[] { "TenantId", "RouteTemplate", "IdempotencyKey" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_attribute_definitions_TenantId_Code",
            schema: "catalog",
            table: "attribute_definitions",
            columns: new[] { "TenantId", "Code" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_attribute_mappings_TenantId_ConnectionId_LocalId_SnapshotId",
            schema: "catalog",
            table: "attribute_mappings",
            columns: new[] { "TenantId", "ConnectionId", "LocalId", "SnapshotId" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_attribute_value_mappings_TenantId_ConnectionId_LocalId_Snap~",
            schema: "catalog",
            table: "attribute_value_mappings",
            columns: new[] { "TenantId", "ConnectionId", "LocalId", "SnapshotId" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_attribute_values_TenantId_AttributeId_NormalizedValue",
            schema: "catalog",
            table: "attribute_values",
            columns: new[] { "TenantId", "AttributeId", "NormalizedValue" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_brand_mappings_TenantId_ConnectionId_LocalId_SnapshotId",
            schema: "catalog",
            table: "brand_mappings",
            columns: new[] { "TenantId", "ConnectionId", "LocalId", "SnapshotId" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_brands_TenantId_NormalizedName",
            schema: "catalog",
            table: "brands",
            columns: new[] { "TenantId", "NormalizedName" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_categories_TenantId_ParentId_NormalizedName",
            schema: "catalog",
            table: "categories",
            columns: new[] { "TenantId", "ParentId", "NormalizedName" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_category_attribute_requirements_TenantId_AttributeId",
            schema: "catalog",
            table: "category_attribute_requirements",
            columns: new[] { "TenantId", "AttributeId" });

        migrationBuilder.CreateIndex(
            name: "IX_category_attribute_requirements_TenantId_CategoryId_Attribu~",
            schema: "catalog",
            table: "category_attribute_requirements",
            columns: new[] { "TenantId", "CategoryId", "AttributeId" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_category_mappings_TenantId_ConnectionId_LocalId_SnapshotId",
            schema: "catalog",
            table: "category_mappings",
            columns: new[] { "TenantId", "ConnectionId", "LocalId", "SnapshotId" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_channel_listing_attributes_TenantId_ProfileId_AttributeId",
            schema: "catalog",
            table: "channel_listing_attributes",
            columns: new[] { "TenantId", "ProfileId", "AttributeId" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_channel_listing_profiles_TenantId_ConnectionId_ProductId",
            schema: "catalog",
            table: "channel_listing_profiles",
            columns: new[] { "TenantId", "ConnectionId", "ProductId" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_channel_listing_profiles_TenantId_ProductId",
            schema: "catalog",
            table: "channel_listing_profiles",
            columns: new[] { "TenantId", "ProductId" });

        migrationBuilder.CreateIndex(
            name: "IX_channel_listing_variants_TenantId_ProfileId_VariantId",
            schema: "catalog",
            table: "channel_listing_variants",
            columns: new[] { "TenantId", "ProfileId", "VariantId" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_channel_media_order_TenantId_ProfileId_MediaId_SortOrder",
            schema: "catalog",
            table: "channel_media_order",
            columns: new[] { "TenantId", "ProfileId", "MediaId", "SortOrder" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_channel_offers_TenantId_ConnectionId_VariantId",
            schema: "inventory",
            table: "channel_offers",
            columns: new[] { "TenantId", "ConnectionId", "VariantId" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_channel_offers_TenantId_VariantId",
            schema: "inventory",
            table: "channel_offers",
            columns: new[] { "TenantId", "VariantId" });

        migrationBuilder.CreateIndex(
            name: "IX_channel_price_history_TenantId_OfferId_PriceVersion",
            schema: "inventory",
            table: "channel_price_history",
            columns: new[] { "TenantId", "OfferId", "PriceVersion" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_connection_inventory_policies_TenantId_ConnectionId",
            schema: "inventory",
            table: "connection_inventory_policies",
            columns: new[] { "TenantId", "ConnectionId" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_connection_location_mappings_TenantId_ConnectionId_External~",
            schema: "inventory",
            table: "connection_location_mappings",
            columns: new[] { "TenantId", "ConnectionId", "ExternalLocationId" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_connection_location_mappings_TenantId_ConnectionId_Location~",
            schema: "inventory",
            table: "connection_location_mappings",
            columns: new[] { "TenantId", "ConnectionId", "LocationId" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_connection_location_mappings_TenantId_LocationId",
            schema: "inventory",
            table: "connection_location_mappings",
            columns: new[] { "TenantId", "LocationId" });

        migrationBuilder.CreateIndex(
            name: "IX_external_identifier_aliases_TenantId_ConnectionId_EntityTyp~",
            schema: "catalog",
            table: "external_identifier_aliases",
            columns: new[] { "TenantId", "ConnectionId", "EntityType", "ExternalId" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_field_provenance_TenantId_SessionId_ProductId_VariantId_Fie~",
            schema: "catalog",
            table: "field_provenance",
            columns: new[] { "TenantId", "SessionId", "ProductId", "VariantId", "FieldName", "StagingRecordId" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_import_column_mappings_TenantId_ProfileId_SourceColumn",
            schema: "catalog",
            table: "import_column_mappings",
            columns: new[] { "TenantId", "ProfileId", "SourceColumn" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_import_decisions_TenantId_CandidateId",
            schema: "catalog",
            table: "import_decisions",
            columns: new[] { "TenantId", "CandidateId" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_import_match_candidates_TenantId_SessionId_StagingRecordId_~",
            schema: "catalog",
            table: "import_match_candidates",
            columns: new[] { "TenantId", "SessionId", "StagingRecordId", "VariantId" });

        migrationBuilder.CreateIndex(
            name: "IX_import_sessions_TenantId_Status",
            schema: "catalog",
            table: "import_sessions",
            columns: new[] { "TenantId", "Status" });

        migrationBuilder.CreateIndex(
            name: "IX_import_staging_records_TenantId_SessionId_ExternalRecordId",
            schema: "catalog",
            table: "import_staging_records",
            columns: new[] { "TenantId", "SessionId", "ExternalRecordId" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_import_staging_records_TenantId_SessionId_RowNumber",
            schema: "catalog",
            table: "import_staging_records",
            columns: new[] { "TenantId", "SessionId", "RowNumber" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_inventory_items_TenantId_Available",
            schema: "inventory",
            table: "inventory_items",
            columns: new[] { "TenantId", "Available" });

        migrationBuilder.CreateIndex(
            name: "IX_inventory_items_TenantId_VariantId_LocationCode",
            schema: "inventory",
            table: "inventory_items",
            columns: new[] { "TenantId", "VariantId", "LocationCode" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_inventory_locations_TenantId_Code",
            schema: "inventory",
            table: "inventory_locations",
            columns: new[] { "TenantId", "Code" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_marketplace_listing_states_TenantId_ConnectionId_VariantId",
            schema: "catalog",
            table: "marketplace_listing_states",
            columns: new[] { "TenantId", "ConnectionId", "VariantId" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_marketplace_product_links_TenantId_ConnectionId_ExternalId",
            schema: "catalog",
            table: "marketplace_product_links",
            columns: new[] { "TenantId", "ConnectionId", "ExternalId" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_marketplace_product_links_TenantId_ConnectionId_ProductId",
            schema: "catalog",
            table: "marketplace_product_links",
            columns: new[] { "TenantId", "ConnectionId", "ProductId" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_marketplace_product_links_TenantId_ProductId",
            schema: "catalog",
            table: "marketplace_product_links",
            columns: new[] { "TenantId", "ProductId" });

        migrationBuilder.CreateIndex(
            name: "IX_marketplace_variant_links_TenantId_ConnectionId_ExternalId",
            schema: "catalog",
            table: "marketplace_variant_links",
            columns: new[] { "TenantId", "ConnectionId", "ExternalId" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_marketplace_variant_links_TenantId_ConnectionId_VariantId",
            schema: "catalog",
            table: "marketplace_variant_links",
            columns: new[] { "TenantId", "ConnectionId", "VariantId" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_marketplace_variant_links_TenantId_VariantId",
            schema: "catalog",
            table: "marketplace_variant_links",
            columns: new[] { "TenantId", "VariantId" });

        migrationBuilder.CreateIndex(
            name: "IX_platform_connections_PublicId",
            schema: "integration",
            table: "platform_connections",
            column: "PublicId",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_platform_connections_TenantId_PlatformCode_Environment_Exte~",
            schema: "integration",
            table: "platform_connections",
            columns: new[] { "TenantId", "PlatformCode", "Environment", "ExternalStoreId" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_product_attribute_assignments_TenantId_AttributeId",
            schema: "catalog",
            table: "product_attribute_assignments",
            columns: new[] { "TenantId", "AttributeId" });

        migrationBuilder.CreateIndex(
            name: "IX_product_attribute_assignments_TenantId_ProductId_VariantId_~",
            schema: "catalog",
            table: "product_attribute_assignments",
            columns: new[] { "TenantId", "ProductId", "VariantId", "AttributeId", "ValueId" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_product_attribute_assignments_TenantId_ValueId",
            schema: "catalog",
            table: "product_attribute_assignments",
            columns: new[] { "TenantId", "ValueId" });

        migrationBuilder.CreateIndex(
            name: "IX_product_attribute_assignments_TenantId_VariantId",
            schema: "catalog",
            table: "product_attribute_assignments",
            columns: new[] { "TenantId", "VariantId" });

        migrationBuilder.CreateIndex(
            name: "IX_product_media_FileAssetId",
            schema: "catalog",
            table: "product_media",
            column: "FileAssetId");

        migrationBuilder.CreateIndex(
            name: "IX_product_media_TenantId_ProductId_VariantId_SortOrder",
            schema: "catalog",
            table: "product_media",
            columns: new[] { "TenantId", "ProductId", "VariantId", "SortOrder" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_product_media_TenantId_VariantId",
            schema: "catalog",
            table: "product_media",
            columns: new[] { "TenantId", "VariantId" });

        migrationBuilder.CreateIndex(
            name: "IX_product_option_values_TenantId_OptionId_NormalizedKey",
            schema: "catalog",
            table: "product_option_values",
            columns: new[] { "TenantId", "OptionId", "NormalizedKey" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_product_options_TenantId_ProductId_NormalizedKey",
            schema: "catalog",
            table: "product_options",
            columns: new[] { "TenantId", "ProductId", "NormalizedKey" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_product_variants_TenantId_BarcodeNormalized",
            schema: "catalog",
            table: "product_variants",
            columns: new[] { "TenantId", "BarcodeNormalized" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_product_variants_TenantId_ProductId",
            schema: "catalog",
            table: "product_variants",
            columns: new[] { "TenantId", "ProductId" });

        migrationBuilder.CreateIndex(
            name: "IX_product_variants_TenantId_SkuNormalized",
            schema: "catalog",
            table: "product_variants",
            columns: new[] { "TenantId", "SkuNormalized" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_products_TenantId_BrandId",
            schema: "catalog",
            table: "products",
            columns: new[] { "TenantId", "BrandId" });

        migrationBuilder.CreateIndex(
            name: "IX_products_TenantId_CategoryId",
            schema: "catalog",
            table: "products",
            columns: new[] { "TenantId", "CategoryId" });

        migrationBuilder.CreateIndex(
            name: "IX_products_TenantId_Status_UpdatedAt",
            schema: "catalog",
            table: "products",
            columns: new[] { "TenantId", "Status", "UpdatedAt" });

        migrationBuilder.CreateIndex(
            name: "IX_reference_items_TenantId_SnapshotId_ParentExternalId",
            schema: "integration",
            table: "reference_items",
            columns: new[] { "TenantId", "SnapshotId", "ParentExternalId" });

        migrationBuilder.CreateIndex(
            name: "IX_reference_items_TenantId_SnapshotId_ResourceType_ExternalId",
            schema: "integration",
            table: "reference_items",
            columns: new[] { "TenantId", "SnapshotId", "ResourceType", "ExternalId" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_reference_snapshots_TenantId_ConnectionId_ResourceType_Cont~",
            schema: "integration",
            table: "reference_snapshots",
            columns: new[] { "TenantId", "ConnectionId", "ResourceType", "ContentHash" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_reference_snapshots_TenantId_ConnectionId_ResourceType_IsCu~",
            schema: "integration",
            table: "reference_snapshots",
            columns: new[] { "TenantId", "ConnectionId", "ResourceType", "IsCurrent" });

        migrationBuilder.CreateIndex(
            name: "IX_stock_ledger_entries_TenantId_IdempotencyKey",
            schema: "inventory",
            table: "stock_ledger_entries",
            columns: new[] { "TenantId", "IdempotencyKey" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_stock_ledger_entries_TenantId_InventoryItemId_OccurredAt_Id",
            schema: "inventory",
            table: "stock_ledger_entries",
            columns: new[] { "TenantId", "InventoryItemId", "OccurredAt", "Id" });

        migrationBuilder.CreateIndex(
            name: "IX_stock_reservations_TenantId_InventoryItemId",
            schema: "inventory",
            table: "stock_reservations",
            columns: new[] { "TenantId", "InventoryItemId" });

        migrationBuilder.CreateIndex(
            name: "IX_stock_reservations_TenantId_SourceType_SourceId_InventoryIt~",
            schema: "inventory",
            table: "stock_reservations",
            columns: new[] { "TenantId", "SourceType", "SourceId", "InventoryItemId" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_stock_reservations_TenantId_Status_ExpiresAt",
            schema: "inventory",
            table: "stock_reservations",
            columns: new[] { "TenantId", "Status", "ExpiresAt" });

        migrationBuilder.CreateIndex(
            name: "IX_variant_option_values_TenantId_OptionValueId",
            schema: "catalog",
            table: "variant_option_values",
            columns: new[] { "TenantId", "OptionValueId" });

        migrationBuilder.CreateIndex(
            name: "IX_variant_option_values_TenantId_VariantId_OptionId",
            schema: "catalog",
            table: "variant_option_values",
            columns: new[] { "TenantId", "VariantId", "OptionId" },
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "api_idempotency_records",
            schema: "ops");

        migrationBuilder.DropTable(
            name: "attribute_mappings",
            schema: "catalog");

        migrationBuilder.DropTable(
            name: "attribute_value_mappings",
            schema: "catalog");

        migrationBuilder.DropTable(
            name: "brand_mappings",
            schema: "catalog");

        migrationBuilder.DropTable(
            name: "category_attribute_requirements",
            schema: "catalog");

        migrationBuilder.DropTable(
            name: "category_mappings",
            schema: "catalog");

        migrationBuilder.DropTable(
            name: "channel_listing_attributes",
            schema: "catalog");

        migrationBuilder.DropTable(
            name: "channel_listing_variants",
            schema: "catalog");

        migrationBuilder.DropTable(
            name: "channel_media_order",
            schema: "catalog");

        migrationBuilder.DropTable(
            name: "channel_price_history",
            schema: "inventory");

        migrationBuilder.DropTable(
            name: "connection_inventory_policies",
            schema: "inventory");

        migrationBuilder.DropTable(
            name: "connection_location_mappings",
            schema: "inventory");

        migrationBuilder.DropTable(
            name: "external_identifier_aliases",
            schema: "catalog");

        migrationBuilder.DropTable(
            name: "field_provenance",
            schema: "catalog");

        migrationBuilder.DropTable(
            name: "import_column_mappings",
            schema: "catalog");

        migrationBuilder.DropTable(
            name: "import_decisions",
            schema: "catalog");

        migrationBuilder.DropTable(
            name: "import_staging_records",
            schema: "catalog");

        migrationBuilder.DropTable(
            name: "marketplace_listing_states",
            schema: "catalog");

        migrationBuilder.DropTable(
            name: "marketplace_product_links",
            schema: "catalog");

        migrationBuilder.DropTable(
            name: "marketplace_variant_links",
            schema: "catalog");

        migrationBuilder.DropTable(
            name: "product_attribute_assignments",
            schema: "catalog");

        migrationBuilder.DropTable(
            name: "product_media",
            schema: "catalog");

        migrationBuilder.DropTable(
            name: "reference_items",
            schema: "integration");

        migrationBuilder.DropTable(
            name: "stock_ledger_entries",
            schema: "inventory");

        migrationBuilder.DropTable(
            name: "stock_reservations",
            schema: "inventory");

        migrationBuilder.DropTable(
            name: "variant_option_values",
            schema: "catalog");

        migrationBuilder.DropTable(
            name: "channel_listing_profiles",
            schema: "catalog");

        migrationBuilder.DropTable(
            name: "channel_offers",
            schema: "inventory");

        migrationBuilder.DropTable(
            name: "inventory_locations",
            schema: "inventory");

        migrationBuilder.DropTable(
            name: "import_column_profiles",
            schema: "catalog");

        migrationBuilder.DropTable(
            name: "import_match_candidates",
            schema: "catalog");

        migrationBuilder.DropTable(
            name: "import_sessions",
            schema: "catalog");

        migrationBuilder.DropTable(
            name: "attribute_values",
            schema: "catalog");

        migrationBuilder.DropTable(
            name: "reference_snapshots",
            schema: "integration");

        migrationBuilder.DropTable(
            name: "inventory_items",
            schema: "inventory");

        migrationBuilder.DropTable(
            name: "product_option_values",
            schema: "catalog");

        migrationBuilder.DropTable(
            name: "attribute_definitions",
            schema: "catalog");

        migrationBuilder.DropTable(
            name: "platform_connections",
            schema: "integration");

        migrationBuilder.DropTable(
            name: "product_variants",
            schema: "catalog");

        migrationBuilder.DropTable(
            name: "product_options",
            schema: "catalog");

        migrationBuilder.DropTable(
            name: "products",
            schema: "catalog");

        migrationBuilder.DropTable(
            name: "brands",
            schema: "catalog");

        migrationBuilder.DropTable(
            name: "categories",
            schema: "catalog");

        migrationBuilder.DropIndex(
            name: "IX_file_assets_TenantId_Sha256_Classification",
            schema: "ops",
            table: "file_assets");

        migrationBuilder.DropColumn(
            name: "ArchivedAt",
            schema: "ops",
            table: "file_assets");

        migrationBuilder.DropColumn(
            name: "Classification",
            schema: "ops",
            table: "file_assets");

        migrationBuilder.DropColumn(
            name: "OriginalNameSafe",
            schema: "ops",
            table: "file_assets");

        migrationBuilder.DropColumn(
            name: "Status",
            schema: "ops",
            table: "file_assets");
    }
}
