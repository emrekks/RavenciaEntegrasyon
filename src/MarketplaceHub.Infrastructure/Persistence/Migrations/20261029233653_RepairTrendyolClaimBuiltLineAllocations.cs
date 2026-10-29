using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RepairTrendyolClaimBuiltLineAllocations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // A Trendyol order first built from a return claim recorded its package's Delivered event with only the
            // returned lines allocated. The order read that brought the other lines found the event already recorded and
            // allocated none of them, so those lines belong to no package and show 0 shipped / 0 delivered.
            // Live read-only check (30 Oct 2026): 28 lines of 23 Trendyol orders, each order with one Delivered package
            // whose other lines are allocated under one event (1 allocated / 1 shipped / 1 delivered).
            //
            // Each such line gets the allocation the read would have written, under the package's one event:
            // AllocatedQuantity = ShippedQuantity = DeliveredQuantity = OrderedQuantity, Cancelled = Returned = 0; and the
            // line takes ShippedQuantity = DeliveredQuantity = OrderedQuantity (Version + 1), as its allocated siblings
            // hold. Only when every condition holds:
            //   - a Trendyol connection; the order no longer carries the return-claim mark;
            //   - the line has no allocation in any package; ordered > 0, nothing cancelled, returned, shipped or delivered;
            //   - the order has exactly one package, Delivered (status and raw status), whose existing allocations all
            //     carry one event id and that id names the package.
            // Stock is not touched: it follows the lines' ordered and cancelled quantities, which do not change, so no
            // unit is taken from stock again. A second run finds the lines allocated and changes nothing.
            migrationBuilder.Sql("""
                WITH pk AS (
                  SELECT p."TenantId", p."OrderId", min(p."Id"::text)::uuid AS package_id, count(*) AS packages
                  FROM sales.shipment_packages p GROUP BY p."TenantId", p."OrderId"),
                ev AS (
                  SELECT a."TenantId", a."PackageId", min(a."SourceEventId") AS event_id, count(DISTINCT a."SourceEventId") AS events
                  FROM sales.package_line_allocations a GROUP BY a."TenantId", a."PackageId"),
                candidates AS (
                  SELECT l."TenantId", l."Id" AS line_id, l."OrderedQuantity" AS qty, p."Id" AS package_id, ev.event_id
                  FROM sales.order_lines l
                  JOIN sales.orders o ON o."TenantId" = l."TenantId" AND o."Id" = l."OrderId"
                  JOIN integration.platform_connections c ON c."TenantId" = o."TenantId" AND c."Id" = o."ConnectionId" AND upper(c."PlatformCode") = 'TRENDYOL'
                  JOIN pk ON pk."TenantId" = o."TenantId" AND pk."OrderId" = o."Id" AND pk.packages = 1
                  JOIN sales.shipment_packages p ON p."TenantId" = pk."TenantId" AND p."Id" = pk.package_id
                  JOIN ev ON ev."TenantId" = p."TenantId" AND ev."PackageId" = p."Id" AND ev.events = 1
                  WHERE p."Status" = 'Delivered' AND p."RawStatus" = 'Delivered'
                    AND ev.event_id LIKE p."ExternalPackageId" || ':%'
                    AND l."OrderedQuantity" > 0 AND l."CancelledQuantity" = 0 AND l."ReturnedQuantity" = 0
                    AND l."ShippedQuantity" = 0 AND l."DeliveredQuantity" = 0
                    AND NOT EXISTS (SELECT 1 FROM sales.package_line_allocations a WHERE a."TenantId" = l."TenantId" AND a."OrderLineId" = l."Id")
                    AND NOT COALESCE(CASE WHEN pg_input_is_valid(o."CustomerSnapshotJson", 'jsonb') THEN
                          o."CustomerSnapshotJson"::jsonb->>'_ravenciaReadModelSource' = 'RETURN_CLAIM'
                          OR (o."CustomerSnapshotJson"::jsonb ? 'claimId' AND o."CustomerSnapshotJson"::jsonb ? 'claimDate')
                        ELSE true END, false)),
                added AS (
                  INSERT INTO sales.package_line_allocations
                    ("Id", "TenantId", "PackageId", "OrderLineId", "AllocatedQuantity", "CancelledQuantity", "ShippedQuantity", "DeliveredQuantity", "ReturnedQuantity", "SourceEventId")
                  SELECT gen_random_uuid(), c."TenantId", c.package_id, c.line_id, c.qty, 0, c.qty, c.qty, 0, c.event_id
                  FROM candidates c
                  RETURNING "OrderLineId")
                UPDATE sales.order_lines l
                SET "ShippedQuantity" = c.qty, "DeliveredQuantity" = c.qty, "Version" = l."Version" + 1
                FROM candidates c
                WHERE l."TenantId" = c."TenantId" AND l."Id" = c.line_id;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // A data repair: the missing allocations are not removed again.
        }
    }
}
