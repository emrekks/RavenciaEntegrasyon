using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ModelLinksAllMarketplaces : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ExternalCode",
                schema: "catalog",
                table: "marketplace_model_links",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.Sql(BackfillSql);
        }

        // Backfill (MODEL-KAYDI-PLANI Faz 2, the other marketplaces): where a model's cards hold exactly one product link on
        // an N11, Pazarama or PTT AVM connection and that link is the marketplace's model-level listing (N11 "main:<code>",
        // Pazarama "group:<code>", any PTT AVM product with its product barcode), the model is listed as that product, but
        // only when no other card of the model holds a variant link there (the Shopify backfill's rule): a variant link does
        // not say which listing it belongs to, so a multi-colour model whose link sits on one colour card is left to the
        // next import, which records it from the listing's own variants (MarketplaceModelLinks.RecordAsync). A model with
        // two or more links on the connection and a card without a model (KENE01) get nothing. Trendyol's card links are
        // colour contents and its productMainId is stored nowhere: its next product import records it; Hepsiburada has no
        // model. Only new rows; an existing model link is kept. Down drops the column only: the rows stay (old code never
        // reads them). Live dry run 14 Nov 2026: N11 62, Pazarama 59, PTT AVM 68.
        internal const string BackfillSql = """
            WITH links AS (
                SELECT l."TenantId", l."ConnectionId", c."PlatformCode", p."FamilyId" AS model, l."ExternalId", l."ExternalCode", l."ProductId"
                FROM catalog.marketplace_product_links l
                JOIN integration.platform_connections c ON c."Id" = l."ConnectionId" AND c."TenantId" = l."TenantId" AND c."PlatformCode" IN ('N11', 'PAZARAMA', 'PTTAVM')
                JOIN catalog.products p ON p."TenantId" = l."TenantId" AND p."Id" = l."ProductId"
                WHERE p."FamilyId" IS NOT NULL
            ), one AS (
                SELECT "TenantId", "ConnectionId", min("PlatformCode") AS platform, model, min("ExternalId") AS external_id, min("ExternalCode") AS external_code, min("ProductId"::text)::uuid AS card
                FROM links GROUP BY "TenantId", "ConnectionId", model
                HAVING count(*) = 1
            )
            INSERT INTO catalog.marketplace_model_links ("Id", "TenantId", "ConnectionId", "ModelId", "ExternalId", "ExternalCode", "CreatedAt", "LastSeenAt", "Version")
            SELECT gen_random_uuid(), o."TenantId", o."ConnectionId", o.model, o.external_id,
                CASE o.platform WHEN 'PTTAVM' THEN o.external_code WHEN 'PAZARAMA' THEN substr(o.external_id, 7) END,
                now(), NULL, 1
            FROM one o
            WHERE ((o.platform = 'N11' AND o.external_id LIKE 'main:_%')
               OR (o.platform = 'PAZARAMA' AND o.external_id LIKE 'group:_%')
               OR o.platform = 'PTTAVM')
              AND NOT EXISTS (
                SELECT 1 FROM catalog.products p
                JOIN catalog.product_variants v ON v."TenantId" = p."TenantId" AND v."ProductId" = p."Id"
                JOIN catalog.marketplace_variant_links vl ON vl."TenantId" = v."TenantId" AND vl."VariantId" = v."Id" AND vl."ConnectionId" = o."ConnectionId"
                WHERE p."TenantId" = o."TenantId" AND p."FamilyId" = o.model AND p."Id" <> o.card)
            ON CONFLICT ("TenantId", "ConnectionId", "ModelId") DO NOTHING;
            """;

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ExternalCode",
                schema: "catalog",
                table: "marketplace_model_links");
        }
    }
}
