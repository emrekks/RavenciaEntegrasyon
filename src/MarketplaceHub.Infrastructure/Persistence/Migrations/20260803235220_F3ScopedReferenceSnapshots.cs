using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class F3ScopedReferenceSnapshots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_reference_snapshots_TenantId_ConnectionId_ResourceType_Cont~",
                schema: "integration",
                table: "reference_snapshots");

            migrationBuilder.DropIndex(
                name: "IX_reference_snapshots_TenantId_ConnectionId_ResourceType_IsCu~",
                schema: "integration",
                table: "reference_snapshots");

            migrationBuilder.DropIndex(
                name: "IX_category_mappings_TenantId_ConnectionId_LocalId_SnapshotId",
                schema: "catalog",
                table: "category_mappings");

            migrationBuilder.DropIndex(
                name: "IX_brand_mappings_TenantId_ConnectionId_LocalId_SnapshotId",
                schema: "catalog",
                table: "brand_mappings");

            migrationBuilder.DropIndex(
                name: "IX_attribute_value_mappings_TenantId_ConnectionId_LocalId_Snap~",
                schema: "catalog",
                table: "attribute_value_mappings");

            migrationBuilder.DropIndex(
                name: "IX_attribute_mappings_TenantId_ConnectionId_LocalId_SnapshotId",
                schema: "catalog",
                table: "attribute_mappings");

            migrationBuilder.AddColumn<string>(
                name: "ScopeExternalId",
                schema: "integration",
                table: "reference_snapshots",
                type: "character varying(512)",
                maxLength: 512,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "AllowsCustomValue",
                schema: "integration",
                table: "reference_items",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "AllowsMultipleValues",
                schema: "integration",
                table: "reference_items",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsRequired",
                schema: "integration",
                table: "reference_items",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ScopeExternalId",
                schema: "catalog",
                table: "category_mappings",
                type: "character varying(512)",
                maxLength: 512,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ScopeExternalId",
                schema: "catalog",
                table: "brand_mappings",
                type: "character varying(512)",
                maxLength: 512,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ScopeExternalId",
                schema: "catalog",
                table: "attribute_value_mappings",
                type: "character varying(512)",
                maxLength: 512,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ScopeExternalId",
                schema: "catalog",
                table: "attribute_mappings",
                type: "character varying(512)",
                maxLength: 512,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "IX_reference_snapshots_TenantId_ConnectionId_ResourceType_Sco~1",
                schema: "integration",
                table: "reference_snapshots",
                columns: new[] { "TenantId", "ConnectionId", "ResourceType", "ScopeExternalId", "IsCurrent" });

            migrationBuilder.CreateIndex(
                name: "IX_reference_snapshots_TenantId_ConnectionId_ResourceType_Scop~",
                schema: "integration",
                table: "reference_snapshots",
                columns: new[] { "TenantId", "ConnectionId", "ResourceType", "ScopeExternalId", "ContentHash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_category_mappings_TenantId_ConnectionId_LocalId_ScopeExtern~",
                schema: "catalog",
                table: "category_mappings",
                columns: new[] { "TenantId", "ConnectionId", "LocalId", "ScopeExternalId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_brand_mappings_TenantId_ConnectionId_LocalId_ScopeExternalId",
                schema: "catalog",
                table: "brand_mappings",
                columns: new[] { "TenantId", "ConnectionId", "LocalId", "ScopeExternalId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_attribute_value_mappings_TenantId_ConnectionId_LocalId_Scop~",
                schema: "catalog",
                table: "attribute_value_mappings",
                columns: new[] { "TenantId", "ConnectionId", "LocalId", "ScopeExternalId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_attribute_mappings_TenantId_ConnectionId_LocalId_ScopeExter~",
                schema: "catalog",
                table: "attribute_mappings",
                columns: new[] { "TenantId", "ConnectionId", "LocalId", "ScopeExternalId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_reference_snapshots_TenantId_ConnectionId_ResourceType_Sco~1",
                schema: "integration",
                table: "reference_snapshots");

            migrationBuilder.DropIndex(
                name: "IX_reference_snapshots_TenantId_ConnectionId_ResourceType_Scop~",
                schema: "integration",
                table: "reference_snapshots");

            migrationBuilder.DropIndex(
                name: "IX_category_mappings_TenantId_ConnectionId_LocalId_ScopeExtern~",
                schema: "catalog",
                table: "category_mappings");

            migrationBuilder.DropIndex(
                name: "IX_brand_mappings_TenantId_ConnectionId_LocalId_ScopeExternalId",
                schema: "catalog",
                table: "brand_mappings");

            migrationBuilder.DropIndex(
                name: "IX_attribute_value_mappings_TenantId_ConnectionId_LocalId_Scop~",
                schema: "catalog",
                table: "attribute_value_mappings");

            migrationBuilder.DropIndex(
                name: "IX_attribute_mappings_TenantId_ConnectionId_LocalId_ScopeExter~",
                schema: "catalog",
                table: "attribute_mappings");

            migrationBuilder.DropColumn(
                name: "ScopeExternalId",
                schema: "integration",
                table: "reference_snapshots");

            migrationBuilder.DropColumn(
                name: "AllowsCustomValue",
                schema: "integration",
                table: "reference_items");

            migrationBuilder.DropColumn(
                name: "AllowsMultipleValues",
                schema: "integration",
                table: "reference_items");

            migrationBuilder.DropColumn(
                name: "IsRequired",
                schema: "integration",
                table: "reference_items");

            migrationBuilder.DropColumn(
                name: "ScopeExternalId",
                schema: "catalog",
                table: "category_mappings");

            migrationBuilder.DropColumn(
                name: "ScopeExternalId",
                schema: "catalog",
                table: "brand_mappings");

            migrationBuilder.DropColumn(
                name: "ScopeExternalId",
                schema: "catalog",
                table: "attribute_value_mappings");

            migrationBuilder.DropColumn(
                name: "ScopeExternalId",
                schema: "catalog",
                table: "attribute_mappings");

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
                name: "IX_category_mappings_TenantId_ConnectionId_LocalId_SnapshotId",
                schema: "catalog",
                table: "category_mappings",
                columns: new[] { "TenantId", "ConnectionId", "LocalId", "SnapshotId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_brand_mappings_TenantId_ConnectionId_LocalId_SnapshotId",
                schema: "catalog",
                table: "brand_mappings",
                columns: new[] { "TenantId", "ConnectionId", "LocalId", "SnapshotId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_attribute_value_mappings_TenantId_ConnectionId_LocalId_Snap~",
                schema: "catalog",
                table: "attribute_value_mappings",
                columns: new[] { "TenantId", "ConnectionId", "LocalId", "SnapshotId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_attribute_mappings_TenantId_ConnectionId_LocalId_SnapshotId",
                schema: "catalog",
                table: "attribute_mappings",
                columns: new[] { "TenantId", "ConnectionId", "LocalId", "SnapshotId" },
                unique: true);
        }
    }
}
