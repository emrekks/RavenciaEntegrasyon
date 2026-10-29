using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RestoreHepsiburadaClaimLineVatRate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // T-2: a Hepsiburada claim read carries no vatRate, and the line took 0% over the rate the order read had
            // stored; the line's snapshot kept the order read's "vatRate". Live read-only check (29 Oct 2026): 6 of 37
            // Hepsiburada lines, all "ClaimCreated", stored 0 with "vatRate":10 in their snapshot. Each such line takes
            // back the numeric rate its own snapshot carries; a line without one, or with a reported 0, is left alone.
            // The casts are guarded (nested CASE): the planner may evaluate them on any platform's line before the join.
            migrationBuilder.Sql("""
                WITH hb AS (
                  SELECT l."Id", l."TenantId",
                         CASE WHEN pg_input_is_valid(l."SourceSnapshotJson", 'jsonb') THEN
                           CASE WHEN jsonb_typeof(l."SourceSnapshotJson"::jsonb->'vatRate') = 'number' THEN (l."SourceSnapshotJson"::jsonb->>'vatRate')::numeric END
                         END AS vat_rate
                  FROM sales.order_lines l
                  JOIN sales.orders o ON o."TenantId" = l."TenantId" AND o."Id" = l."OrderId"
                  JOIN integration.platform_connections c ON c."TenantId" = o."TenantId" AND c."Id" = o."ConnectionId" AND upper(c."PlatformCode") = 'HEPSIBURADA'
                  WHERE l."VatRate" = 0 AND l."SourceSnapshotJson" IS NOT NULL)
                UPDATE sales.order_lines l
                SET "VatRate" = hb.vat_rate, "Version" = l."Version" + 1
                FROM hb
                WHERE l."TenantId" = hb."TenantId" AND l."Id" = hb."Id"
                  AND l."VatRate" = 0
                  AND hb.vat_rate > 0 AND hb.vat_rate <= 100;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // A data repair: the 0% rates are not restored.
        }
    }
}
