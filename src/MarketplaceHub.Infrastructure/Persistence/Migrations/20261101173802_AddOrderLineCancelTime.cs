using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddOrderLineCancelTime : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CancelSeenAt",
                schema: "sales",
                table: "order_lines",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CancelledAt",
                schema: "sales",
                table: "order_lines",
                type: "timestamp with time zone",
                nullable: true);

            // Trendyol's stored time of a package that ends cancelled is its last change. It is the cancellation time
            // only where the panel saw the cancellation as it happened (recorded within the hour); a package first
            // read later (the 26 October re-import) may have changed after it was cancelled, so its time stays
            // unknown. Other marketplaces' stored times are not cancellation times. Split parents are left out.
            migrationBuilder.Sql("""
                UPDATE sales.order_lines AS line
                SET "CancelledAt" = source.cancelled_at
                FROM (
                    SELECT allocation."TenantId" AS tenant_id, allocation."OrderLineId" AS line_id, max(history."OccurredAt") AS cancelled_at
                    FROM sales.package_line_allocations AS allocation
                    JOIN sales.shipment_packages AS package
                      ON package."TenantId" = allocation."TenantId" AND package."Id" = allocation."PackageId"
                    JOIN integration.platform_connections AS connection
                      ON connection."TenantId" = package."TenantId" AND connection."Id" = package."ConnectionId"
                    JOIN sales.order_status_history AS history
                      ON history."TenantId" = package."TenantId" AND history."PackageId" = package."Id"
                    WHERE connection."PlatformCode" = 'TRENDYOL'
                      AND package."Status" = 'Cancelled'
                      AND upper(replace(package."RawStatus", '_', '')) IN ('CANCELLED', 'CANCELED', 'UNSUPPLIED')
                      AND history."CanonicalStatus" = 'CANCELLED'
                      AND history."OccurredAt" = package."StatusOccurredAt"
                      AND history."RecordedAt" >= history."OccurredAt"
                      AND history."RecordedAt" <= history."OccurredAt" + interval '1 hour'
                    GROUP BY allocation."TenantId", allocation."OrderLineId"
                ) AS source
                WHERE line."TenantId" = source.tenant_id
                  AND line."Id" = source.line_id
                  AND line."CancelledQuantity" > 0
                  AND line."CancelledAt" IS NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CancelSeenAt",
                schema: "sales",
                table: "order_lines");

            migrationBuilder.DropColumn(
                name: "CancelledAt",
                schema: "sales",
                table: "order_lines");
        }
    }
}
