using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MarketplaceModelLinks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "marketplace_model_links",
                schema: "catalog",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConnectionId = table.Column<Guid>(type: "uuid", nullable: false),
                    ModelId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExternalId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastSeenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_marketplace_model_links", x => x.Id);
                    table.ForeignKey(
                        name: "FK_marketplace_model_links_product_models_TenantId_ModelId",
                        columns: x => new { x.TenantId, x.ModelId },
                        principalSchema: "catalog",
                        principalTable: "product_models",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_marketplace_model_links_TenantId_ConnectionId_ExternalId",
                schema: "catalog",
                table: "marketplace_model_links",
                columns: new[] { "TenantId", "ConnectionId", "ExternalId" });

            migrationBuilder.CreateIndex(
                name: "IX_marketplace_model_links_TenantId_ConnectionId_ModelId",
                schema: "catalog",
                table: "marketplace_model_links",
                columns: new[] { "TenantId", "ConnectionId", "ModelId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_marketplace_model_links_TenantId_ModelId",
                schema: "catalog",
                table: "marketplace_model_links",
                columns: new[] { "TenantId", "ModelId" });

            migrationBuilder.Sql(BackfillSql);
        }

        // Backfill (MODEL-KAYDI-PLANI Faz 2, Shopify): a model whose one colour card holds a Shopify product link, while no
        // other card of the model holds a Shopify variant, is listed as that Shopify product. A multi-colour Shopify product
        // has no product link to start from: the next Shopify product match (or import) run records it
        // (MarketplaceModelLinks.RecordAsync). Only new rows; safe to run again.
        internal const string BackfillSql = """
            WITH links AS (
                SELECT l."TenantId", l."ConnectionId", p."FamilyId" AS model, l."ExternalId", l."ProductId"
                FROM catalog.marketplace_product_links l
                JOIN integration.platform_connections c ON c."Id" = l."ConnectionId" AND c."TenantId" = l."TenantId" AND c."PlatformCode" = 'SHOPIFY'
                JOIN catalog.products p ON p."TenantId" = l."TenantId" AND p."Id" = l."ProductId"
                WHERE p."FamilyId" IS NOT NULL
            ), one AS (
                SELECT "TenantId", "ConnectionId", model, min("ExternalId") AS external_id, min("ProductId"::text)::uuid AS card
                FROM links GROUP BY "TenantId", "ConnectionId", model
                HAVING count(*) = 1
            )
            INSERT INTO catalog.marketplace_model_links ("Id", "TenantId", "ConnectionId", "ModelId", "ExternalId", "CreatedAt", "LastSeenAt", "Version")
            SELECT gen_random_uuid(), o."TenantId", o."ConnectionId", o.model, o.external_id, now(), NULL, 1
            FROM one o
            WHERE NOT EXISTS (
                SELECT 1 FROM catalog.products p
                JOIN catalog.product_variants v ON v."TenantId" = p."TenantId" AND v."ProductId" = p."Id"
                JOIN catalog.marketplace_variant_links vl ON vl."TenantId" = v."TenantId" AND vl."VariantId" = v."Id" AND vl."ConnectionId" = o."ConnectionId"
                WHERE p."TenantId" = o."TenantId" AND p."FamilyId" = o.model AND p."Id" <> o.card)
            ON CONFLICT ("TenantId", "ConnectionId", "ModelId") DO NOTHING;
            """;

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "marketplace_model_links",
                schema: "catalog");
        }
    }
}
