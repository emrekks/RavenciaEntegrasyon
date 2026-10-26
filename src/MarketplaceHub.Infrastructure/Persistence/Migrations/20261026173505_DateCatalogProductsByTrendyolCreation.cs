using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DateCatalogProductsByTrendyolCreation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The product list is newest first by CreatedAt. The Trendyol import of 26 Oct 2026 stamped each finalizing
            // run with its own time, so products from its later runs (older on Trendyol) sat on top. A product linked to
            // Trendyol now takes the date Trendyol created it (creationDate in the import's staged snapshot), as new
            // imports do (CatalogImportOrdering.RemoteCreatedAt). Only moves a date earlier: a product made in the panel
            // before it was listed keeps its own date. Live read-only dry run: 907 of 944 products, 2022-04-20 to
            // 2026-10-07; the 37 without a staged snapshot keep their import time.
            migrationBuilder.Sql("""
                WITH staged AS (
                  SELECT s."TenantId", s."ConnectionId", s."ExternalProductId",
                         min(to_timestamp(substring(s."SnapshotJson"->>'RawJson' from '"creationDate":([0-9]{12,14})')::bigint / 1000.0)) AS created
                  FROM catalog.product_import_staging_records s
                  JOIN integration.platform_connections c ON c."TenantId" = s."TenantId" AND c."Id" = s."ConnectionId" AND upper(c."PlatformCode") = 'TRENDYOL'
                  WHERE s."SnapshotJson"->>'RawJson' ~ '"creationDate":[0-9]{12,14}'
                  GROUP BY 1, 2, 3),
                first_listed AS (
                  SELECT l."TenantId", l."ProductId", min(st.created) AS created
                  FROM catalog.marketplace_product_links l
                  JOIN staged st ON st."TenantId" = l."TenantId" AND st."ConnectionId" = l."ConnectionId" AND st."ExternalProductId" = l."ExternalId"
                  GROUP BY 1, 2)
                UPDATE catalog.products p
                SET "CreatedAt" = f.created
                FROM first_listed f
                WHERE p."TenantId" = f."TenantId" AND p."Id" = f."ProductId"
                  AND f.created < p."CreatedAt" AND f.created >= timestamptz '2000-01-01';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // A data repair: the import-time dates are not restored.
        }
    }
}
