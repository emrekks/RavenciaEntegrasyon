using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TakeHepsiburadaLineProductBarcode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // A Hepsiburada order line's "barcode" is its package's cargo barcode, the same on every line of the package;
            // the product's own barcode is "productBarcode". Lines took the cargo one until HepsiburadaJsonMapper.MapLine
            // preferred productBarcode, so the order screens showed one barcode for different products. Each line now
            // takes the productBarcode its own stored snapshot carries. Live read-only dry run: 35 of 36 lines.
            // The cast is guarded (pg_input_is_valid): the planner may evaluate it on any platform's line before the join.
            migrationBuilder.Sql("""
                WITH hb AS (
                  SELECT l."Id", l."TenantId",
                         CASE WHEN pg_input_is_valid(l."SourceSnapshotJson", 'jsonb') THEN btrim(l."SourceSnapshotJson"::jsonb->>'productBarcode') END AS product_barcode
                  FROM sales.order_lines l
                  JOIN sales.orders o ON o."TenantId" = l."TenantId" AND o."Id" = l."OrderId"
                  JOIN integration.platform_connections c ON c."TenantId" = o."TenantId" AND c."Id" = o."ConnectionId" AND upper(c."PlatformCode") = 'HEPSIBURADA'
                  WHERE l."SourceSnapshotJson" IS NOT NULL)
                UPDATE sales.order_lines l
                SET "Barcode" = hb.product_barcode, "Version" = l."Version" + 1
                FROM hb
                WHERE l."TenantId" = hb."TenantId" AND l."Id" = hb."Id"
                  AND hb.product_barcode <> '' AND length(hb.product_barcode) <= 160
                  AND l."Barcode" IS DISTINCT FROM hb.product_barcode;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // A data repair: the cargo barcodes are not restored.
        }
    }
}
