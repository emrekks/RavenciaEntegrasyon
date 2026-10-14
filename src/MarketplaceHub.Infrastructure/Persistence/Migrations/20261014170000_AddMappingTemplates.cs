using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMappingTemplates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Source",
                schema: "catalog",
                table: "attribute_value_mappings",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "MANUAL");

            migrationBuilder.AddColumn<string>(
                name: "Source",
                schema: "catalog",
                table: "attribute_mappings",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "MANUAL");

            migrationBuilder.CreateTable(
                name: "attribute_mapping_templates",
                schema: "catalog",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConnectionId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExternalAttributeId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    LocalAttributeId = table.Column<Guid>(type: "uuid", nullable: false),
                    Role = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_attribute_mapping_templates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "attribute_value_mapping_templates",
                schema: "catalog",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConnectionId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExternalAttributeId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    LocalValueId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExternalValueId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_attribute_value_mapping_templates", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_attribute_mapping_templates_TenantId_ConnectionId_ExternalA~",
                schema: "catalog",
                table: "attribute_mapping_templates",
                columns: new[] { "TenantId", "ConnectionId", "ExternalAttributeId", "LocalAttributeId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_attribute_value_mapping_templates_TenantId_ConnectionId_Ext~",
                schema: "catalog",
                table: "attribute_value_mapping_templates",
                columns: new[] { "TenantId", "ConnectionId", "ExternalAttributeId", "LocalValueId" },
                unique: true);

            // Seed the templates from the existing category-scoped decisions: for every key the most
            // recently verified mapping wins (785 attribute rows hold 71 decisions, 20,546 value rows
            // hold about 2,350 decisions in production at the time of writing).
            migrationBuilder.Sql("""
                INSERT INTO catalog.attribute_mapping_templates ("Id", "TenantId", "ConnectionId", "ExternalAttributeId", "LocalAttributeId", "Role", "UpdatedAt", "Version")
                SELECT DISTINCT ON (m."TenantId", m."ConnectionId", m."ExternalId", m."LocalId")
                       gen_random_uuid(), m."TenantId", m."ConnectionId", m."ExternalId", m."LocalId",
                       coalesce(requirement."Role", 'ATTRIBUTE'), coalesce(m."VerifiedAt", now()), 1
                FROM catalog.attribute_mappings m
                LEFT JOIN catalog.category_mappings category
                  ON category."TenantId" = m."TenantId" AND category."ConnectionId" = m."ConnectionId"
                 AND category."ExternalId" = m."ScopeExternalId" AND category."Status" = 'VERIFIED'
                LEFT JOIN catalog.category_attribute_requirements requirement
                  ON requirement."TenantId" = m."TenantId" AND requirement."CategoryId" = category."LocalId" AND requirement."AttributeId" = m."LocalId"
                WHERE m."Status" = 'VERIFIED'
                ORDER BY m."TenantId", m."ConnectionId", m."ExternalId", m."LocalId", m."VerifiedAt" DESC NULLS LAST, m."Id" DESC;

                INSERT INTO catalog.attribute_value_mapping_templates ("Id", "TenantId", "ConnectionId", "ExternalAttributeId", "LocalValueId", "ExternalValueId", "UpdatedAt", "Version")
                SELECT DISTINCT ON (m."TenantId", m."ConnectionId", split_part(m."ScopeExternalId", '/', 2), m."LocalId")
                       gen_random_uuid(), m."TenantId", m."ConnectionId", split_part(m."ScopeExternalId", '/', 2), m."LocalId", m."ExternalId",
                       coalesce(m."VerifiedAt", now()), 1
                FROM catalog.attribute_value_mappings m
                WHERE m."Status" = 'VERIFIED' AND split_part(m."ScopeExternalId", '/', 2) <> ''
                ORDER BY m."TenantId", m."ConnectionId", split_part(m."ScopeExternalId", '/', 2), m."LocalId", m."VerifiedAt" DESC NULLS LAST, m."Id" DESC;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "attribute_mapping_templates",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "attribute_value_mapping_templates",
                schema: "catalog");

            migrationBuilder.DropColumn(
                name: "Source",
                schema: "catalog",
                table: "attribute_value_mappings");

            migrationBuilder.DropColumn(
                name: "Source",
                schema: "catalog",
                table: "attribute_mappings");
        }
    }
}
