using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RepairTrendyolClaimBuiltOrderAmounts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // A Trendyol order first built from a return claim stored the claim's gross (the returned lines only, at their
            // undiscounted price) as its sale and net, with discount 0, on the order and on its one package. The order read
            // that completed it replaced the lines but not the package amounts (its Delivered status was already
            // recorded), and the order totals are rebuilt from the package amounts, so the claim's figures stayed.
            // Live read-only check (30 Oct 2026): 141 Trendyol orders (and their 141 packages), e.g. 11738971444 with
            // 1.598 / 0 / 1.598 where Trendyol shows 1.598 / 80,98 / 1.517,02.
            //
            // Each such order and its package take the totals of the order read's own lines (Trendyol's per-unit
            // amount, discount and price times the quantity): GrossAmount = sum(amount x qty), DiscountAmount =
            // sum(discount x qty), NetAmount = sum(price x qty). Only when every condition holds:
            //   - a Trendyol connection; the order no longer carries the return-claim mark (it was completed);
            //   - every line's snapshot has numeric amount, discount and price, its UnitPrice equals that price, and no
            //     line has a cancelled quantity;
            //   - the line discount is above 0 and sale - discount = net on the lines (to the cent);
            //   - the order stores discount 0 and net = gross, and has exactly one package storing the same gross,
            //     discount 0 and the same net.
            // After the repair the order no longer stores discount 0, so a second run changes nothing. Version + 1 on
            // both rows; UpdatedAt is left as the last marketplace change. The casts are guarded (nested CASE).
            migrationBuilder.Sql("""
                WITH tl AS (
                  SELECT l."TenantId", l."OrderId", l."OrderedQuantity" AS qty, l."CancelledQuantity" AS cancelled, l."UnitPrice" AS unit_price,
                         CASE WHEN pg_input_is_valid(l."SourceSnapshotJson", 'jsonb') THEN l."SourceSnapshotJson"::jsonb END AS j
                  FROM sales.order_lines l
                  JOIN sales.orders o ON o."TenantId" = l."TenantId" AND o."Id" = l."OrderId"
                  JOIN integration.platform_connections c ON c."TenantId" = o."TenantId" AND c."Id" = o."ConnectionId" AND upper(c."PlatformCode") = 'TRENDYOL'),
                lv AS (
                  SELECT "TenantId", "OrderId", qty, cancelled, unit_price,
                         CASE WHEN jsonb_typeof(j->'amount') = 'number' THEN (j->>'amount')::numeric END AS amount,
                         CASE WHEN jsonb_typeof(j->'discount') = 'number' THEN (j->>'discount')::numeric END AS discount,
                         CASE WHEN jsonb_typeof(j->'price') = 'number' THEN (j->>'price')::numeric END AS price
                  FROM tl),
                ot AS (
                  SELECT "TenantId", "OrderId",
                         bool_and(amount IS NOT NULL AND discount IS NOT NULL AND price IS NOT NULL AND unit_price = price) AS all_known,
                         sum(cancelled) AS cancelled,
                         sum(amount * qty) AS gross, sum(discount * qty) AS discount, sum(price * qty) AS net
                  FROM lv GROUP BY "TenantId", "OrderId"),
                pk AS (
                  SELECT "TenantId", "OrderId", min("Id"::text)::uuid AS package_id, count(*) AS packages
                  FROM sales.shipment_packages GROUP BY "TenantId", "OrderId"),
                candidates AS (
                  SELECT o."TenantId", o."Id" AS order_id, pk.package_id, ot.gross, ot.discount, ot.net
                  FROM sales.orders o
                  JOIN ot ON ot."TenantId" = o."TenantId" AND ot."OrderId" = o."Id"
                  JOIN pk ON pk."TenantId" = o."TenantId" AND pk."OrderId" = o."Id" AND pk.packages = 1
                  JOIN sales.shipment_packages p ON p."TenantId" = o."TenantId" AND p."Id" = pk.package_id
                  WHERE ot.all_known AND ot.cancelled = 0 AND ot.discount > 0
                    AND abs(ot.gross - ot.discount - ot.net) < 0.005
                    AND o."DiscountAmount" = 0 AND o."NetAmount" = o."GrossAmount"
                    AND p."DiscountAmount" = 0 AND p."GrossAmount" = o."GrossAmount" AND p."NetAmount" = o."NetAmount"
                    AND NOT COALESCE(CASE WHEN pg_input_is_valid(o."CustomerSnapshotJson", 'jsonb') THEN
                          o."CustomerSnapshotJson"::jsonb->>'_ravenciaReadModelSource' = 'RETURN_CLAIM'
                          OR (o."CustomerSnapshotJson"::jsonb ? 'claimId' AND o."CustomerSnapshotJson"::jsonb ? 'claimDate')
                        ELSE true END, false)),
                repaired_packages AS (
                  UPDATE sales.shipment_packages p
                  SET "GrossAmount" = c.gross, "DiscountAmount" = c.discount, "NetAmount" = c.net, "Version" = p."Version" + 1
                  FROM candidates c
                  WHERE p."TenantId" = c."TenantId" AND p."Id" = c.package_id
                  RETURNING p."Id")
                UPDATE sales.orders o
                SET "GrossAmount" = c.gross, "DiscountAmount" = c.discount, "NetAmount" = c.net, "Version" = o."Version" + 1
                FROM candidates c
                WHERE o."TenantId" = c."TenantId" AND o."Id" = c.order_id;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // A data repair: the claim's figures are not restored.
        }
    }
}
