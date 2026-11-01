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

            // Trendyol is the only marketplace whose stored package times are its cancellation times (the last change
            // of a package that ends cancelled), so its cancelled lines take that time; every other line stays
            // unknown until a read brings the marketplace's own time. Split parents (UnPacked) are not cancellations.
            migrationBuilder.Sql("""
                UPDATE sales.order_lines AS line
                SET "CancelledAt" = source.cancelled_at
                FROM (
                    SELECT allocation."OrderLineId" AS line_id, max(package."StatusOccurredAt") AS cancelled_at
                    FROM sales.package_line_allocations AS allocation
                    JOIN sales.shipment_packages AS package
                      ON package."TenantId" = allocation."TenantId" AND package."Id" = allocation."PackageId"
                    JOIN integration.platform_connections AS connection
                      ON connection."TenantId" = package."TenantId" AND connection."Id" = package."ConnectionId"
                    WHERE connection."PlatformCode" = 'TRENDYOL'
                      AND package."Status" = 'Cancelled'
                      AND upper(replace(package."RawStatus", '_', '')) IN ('CANCELLED', 'CANCELED', 'UNSUPPLIED')
                    GROUP BY allocation."OrderLineId"
                ) AS source
                WHERE line."Id" = source.line_id
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
