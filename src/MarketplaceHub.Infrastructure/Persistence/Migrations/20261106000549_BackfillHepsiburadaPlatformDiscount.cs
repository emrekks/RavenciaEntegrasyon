using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class BackfillHepsiburadaPlatformDiscount : Migration
    {
        // Hepsiburada lines already stored carry Hepsiburada's own campaign discount a unit in the raw line
        // ("hbDiscount": {"unitPrice": {"amount": 194.85}}), as the mapper now reads it (HepsiburadaJsonMapper.PlatformDiscount):
        // Hepsiburada pays it to the seller, so it is part of the sale as Trendyol's own discount is. Only Hepsiburada lines,
        // only valid JSON, only a number above zero; a line without one stays null. Only the one column is written (no
        // version bump), only where it is still null, so a second run changes nothing. Live, 6 Nov 2026: 36 of 46
        // Hepsiburada lines carry one above zero.
        internal const string Backfill = """
            UPDATE sales.order_lines AS line
            SET "PlatformDiscount" = source.discount
            FROM (
                SELECT candidate."Id" AS line_id, candidate."TenantId" AS tenant_id,
                       CASE WHEN jsonb_typeof(candidate.value) = 'number' THEN candidate.value::text::numeric
                            WHEN jsonb_typeof(candidate.value) = 'string' AND (candidate.value #>> '{}') ~ '^[0-9]+(\.[0-9]+)?$' THEN (candidate.value #>> '{}')::numeric
                       END AS discount
                FROM (
                    SELECT ol."Id", ol."TenantId",
                           CASE WHEN jsonb_typeof(ol."SourceSnapshotJson"::jsonb -> 'hbDiscount') = 'object'
                                 AND jsonb_typeof(ol."SourceSnapshotJson"::jsonb -> 'hbDiscount' -> 'unitPrice') = 'object'
                                THEN ol."SourceSnapshotJson"::jsonb -> 'hbDiscount' -> 'unitPrice' -> 'amount' END AS value
                    FROM sales.order_lines AS ol
                    JOIN sales.orders AS o ON o."TenantId" = ol."TenantId" AND o."Id" = ol."OrderId"
                    JOIN integration.platform_connections AS connection
                      ON connection."TenantId" = o."TenantId" AND connection."Id" = o."ConnectionId"
                    WHERE connection."PlatformCode" = 'HEPSIBURADA'
                      AND ol."PlatformDiscount" IS NULL
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
              AND source.discount > 0
              AND line."PlatformDiscount" IS NULL;
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(Backfill);
        }

        // The values stay: they are what Hepsiburada sent on the line, which the mapper writes again on the next read.
        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
