using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ProjectPttAvmPackageLines : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Since 24 October (audit S4) a PTT AVM package stores the time its status was first seen, which names no
            // event, so the line projection matched none of its allocations: cancelled, shipped and delivered quantities
            // stayed 0 (audit 29 Oct S-1; live: 3 of 3 lines). The order sync now projects the event the read reports,
            // but a delivered or cancelled order is not read again, so its lines are projected here once from the
            // allocations already stored, as PackageLineProjectionPolicy does: the allocations of the event the
            // package's current status came from (the latest status row with that raw status that wrote allocations).
            // Only PTT AVM orders with a single package (PTT AVM's one package per order) whose allocations hold no event
            // named by the stored status time, and only lines whose quantities differ. Stock is not moved here: it follows
            // only when the order is read again (ProjectOrderReservations reads these quantities), e.g. a full PTT AVM
            // order scan, which reads the last three months.
            migrationBuilder.Sql("""
                WITH current_event AS (
                    SELECT DISTINCT ON (p."Id") p."TenantId", p."Id" AS "PackageId", h."SourceEventId"
                    FROM sales.shipment_packages p
                    JOIN integration.platform_connections c ON c."TenantId" = p."TenantId" AND c."Id" = p."ConnectionId"
                    JOIN sales.order_status_history h ON h."TenantId" = p."TenantId" AND h."OrderId" = p."OrderId" AND h."PackageId" = p."Id" AND h."RawStatus" = p."RawStatus"
                    WHERE upper(c."PlatformCode") = 'PTTAVM'
                      AND p."RawStatus" <> 'UNPACKED_BY_PANEL'
                      AND upper(replace(trim(p."RawStatus"), '_', '')) <> 'UNPACKED'
                      AND h."SourceEventId" NOT LIKE 'REJECTED:%'
                      AND NOT EXISTS (
                          SELECT 1 FROM sales.shipment_packages other
                          WHERE other."TenantId" = p."TenantId" AND other."OrderId" = p."OrderId" AND other."Id" <> p."Id")
                      AND EXISTS (
                          SELECT 1 FROM sales.package_line_allocations a
                          WHERE a."TenantId" = p."TenantId" AND a."PackageId" = p."Id" AND a."SourceEventId" = h."SourceEventId")
                      AND NOT EXISTS (
                          SELECT 1 FROM sales.package_line_allocations a
                          WHERE a."TenantId" = p."TenantId" AND a."PackageId" = p."Id"
                            AND a."SourceEventId" = p."ExternalPackageId" || ':' || floor(extract(epoch FROM p."StatusOccurredAt") * 1000)::bigint)
                    ORDER BY p."Id", h."RecordedAt" DESC, h."Id" DESC
                ),
                projected AS (
                    SELECT a."TenantId", a."OrderLineId",
                           sum(a."CancelledQuantity") AS cancelled,
                           sum(a."ShippedQuantity") AS shipped,
                           sum(a."DeliveredQuantity") AS delivered,
                           sum(a."ReturnedQuantity") AS returned
                    FROM current_event e
                    JOIN sales.package_line_allocations a ON a."TenantId" = e."TenantId" AND a."PackageId" = e."PackageId" AND a."SourceEventId" = e."SourceEventId"
                    GROUP BY a."TenantId", a."OrderLineId"
                )
                UPDATE sales.order_lines l
                SET "CancelledQuantity" = pr.cancelled,
                    "ShippedQuantity" = pr.shipped,
                    "DeliveredQuantity" = pr.delivered,
                    "ReturnedQuantity" = pr.returned,
                    "Version" = l."Version" + 1
                FROM projected pr
                WHERE l."TenantId" = pr."TenantId" AND l."Id" = pr."OrderLineId"
                  AND (l."CancelledQuantity", l."ShippedQuantity", l."DeliveredQuantity", l."ReturnedQuantity")
                      IS DISTINCT FROM (pr.cancelled, pr.shipped, pr.delivered, pr.returned);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // A projection of the stored allocations: the unprojected zeros are not restored.
        }
    }
}
