using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddOrderLinePlatformDiscount : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "PlatformDiscount",
                schema: "sales",
                table: "order_lines",
                type: "numeric(19,4)",
                precision: 19,
                scale: 4,
                nullable: true);

            // Trendyol lines already stored carry their own discount in the raw line (lineTyDiscount, older reads also
            // tyDiscount, per unit), as the mapper now reads it. Only Trendyol lines, only valid JSON, only a number that
            // is not negative; a line without the field stays unknown (null). Live, 2 Nov 2026: 2,268 of 2,298 Trendyol
            // lines carry it, 2 of them above zero.
            migrationBuilder.Sql("""
                UPDATE sales.order_lines AS line
                SET "PlatformDiscount" = source.discount
                FROM (
                    SELECT candidate."Id" AS line_id, candidate."TenantId" AS tenant_id,
                           CASE WHEN jsonb_typeof(candidate.value) = 'number' THEN candidate.value::text::numeric
                                WHEN jsonb_typeof(candidate.value) = 'string' AND (candidate.value #>> '{}') ~ '^[0-9]+(\.[0-9]+)?$' THEN (candidate.value #>> '{}')::numeric
                           END AS discount
                    FROM (
                        SELECT ol."Id", ol."TenantId",
                               coalesce(nullif(ol."SourceSnapshotJson"::jsonb -> 'lineTyDiscount', 'null'::jsonb),
                                        nullif(ol."SourceSnapshotJson"::jsonb -> 'tyDiscount', 'null'::jsonb)) AS value
                        FROM sales.order_lines AS ol
                        JOIN sales.orders AS o ON o."TenantId" = ol."TenantId" AND o."Id" = ol."OrderId"
                        JOIN integration.platform_connections AS connection
                          ON connection."TenantId" = o."TenantId" AND connection."Id" = o."ConnectionId"
                        WHERE connection."PlatformCode" = 'TRENDYOL'
                          AND ol."SourceSnapshotJson" IS NOT NULL
                          -- CASE keeps the cast behind the check: AND does not fix the order of evaluation.
                          AND CASE WHEN pg_input_is_valid(ol."SourceSnapshotJson", 'jsonb')
                                   THEN jsonb_typeof(ol."SourceSnapshotJson"::jsonb) = 'object'
                                   ELSE false END
                        -- OFFSET 0 keeps the outer conditions (and their casts) from being pushed below the check.
                        OFFSET 0
                    ) AS candidate
                ) AS source
                WHERE line."TenantId" = source.tenant_id
                  AND line."Id" = source.line_id
                  AND source.discount IS NOT NULL
                  AND source.discount >= 0
                  AND line."PlatformDiscount" IS NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PlatformDiscount",
                schema: "sales",
                table: "order_lines");
        }
    }
}
