using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RepairOrderStockAndReleaseReservations : Migration
    {
        // The 11 Trendyol order lines first read already shipped or delivered during the order-read outages of 28 and
        // 30 Oct 2026 (live read-only check, 30 Oct 2026): their first projection took the shipped unit as the stock
        // baseline, so it never left stock. Line id, inventory item id.
        internal static readonly (string LineId, string ItemId)[] MissedLines =
        [
            ("01a1823a-c7cb-7a63-9a05-9a8532fa2e36", "01a178da-c96c-7d80-946c-27f684f4f1d4"), // 11757573145 MZ033HLK06
            ("01a1823a-c7a4-7aed-8b7c-461f7f74b651", "01a178cf-8b7f-7dab-90ee-0e01b94d851b"), // 11757645037 MZ052BOD-33
            ("01a1823a-c7a1-7445-90a7-731f0851f050", "01a178dc-28fd-7851-90b0-daef1dff8d5f"), // 11757771133 MZ034YS65-B
            ("01a1823a-c79c-74a2-946e-850f94353219", "01a178d4-5b51-7ed1-b1d9-0612b31bfdae"), // 11757781395 MZ043DDC21
            ("01a1823a-c7c2-77ab-850a-2b4ef98c4c6e", "01a178d0-f489-7a44-b9fc-1556b3f0c1e3"), // 11757839363 MZ049BOC-14
            ("01a1823a-c7d5-7e92-95ce-6b81c08ceafe", "01a178e3-ede8-7e5e-bf04-4189e3e360f7"), // 11757862002 MZ019CD48B
            ("01a1823a-c7d5-7db5-b083-9261fa1842f3", "01a178e3-e4cf-7f4f-bbd4-57c80079663a"), // 11757862002 MZ019CD36B
            ("01a1823a-c7d5-7c99-923a-030295de7bd8", "01a178dd-7221-7dce-a928-d848598a949c"), // 11757862002 MZ029YD69-B
            ("01a18cec-1bd4-7177-ab0a-ab1dd1a89620", "01a178ce-f922-7d9a-93c9-8f40636f1b85"), // 11764019364 RYP004-12
            ("01a18cec-1bd2-7c2e-a8d6-5194ad0d1c6c", "01a178d1-cdf8-79ff-85f3-c5d7ea3b4289"), // 11764205286 MZDT005ECD01
            ("01a18cec-1be6-7df5-9a77-8b11dd29e0e7", "01a178cf-ce5e-7d53-8ff2-69a694b9b52f"), // 11764254823 MZ052BOD-04
        ];

        // Part 1: each missed line takes its units from stock now, as its first read should have (owner decision: an order
        // line deducts stock the first time it is read, whatever its status). Its baseline is cancelled by a correction
        // baseline row (-target), its ORDER_RECEIVED row takes what on-hand can give (never below zero unless the
        // connection allows negative stock) and the rest is its shortfall marker (ORDER_STOCK_SHORTFALL, no on-hand
        // change), so the order projection afterwards finds the line settled and moves nothing. On-hand, Available
        // (= greatest(0, OnHand - Reserved)), ProjectionVersion and Version change with the deduction.
        // Only while the line is exactly as found: on a Trendyol connection, its baseline rows sum to its committed
        // quantity and are its only stock rows besides shipment progress (no ORDER_RECEIVED, ORDER_SHIPPED, shortfall
        // or reservation), and no other missed line shares its item. A second run finds the baseline at zero and does
        // nothing.
        internal static string RepairMissedLinesSql(IEnumerable<(string LineId, string ItemId)> lines) => $$"""
            WITH targets(line_id, item_id) AS (VALUES
              {{string.Join(",\n  ", lines.Select(x => $"('{Guid.Parse(x.LineId):D}'::uuid, '{Guid.Parse(x.ItemId):D}'::uuid)"))}}),
            lines AS (
              SELECT l."TenantId", l."Id" AS line_id, l."OrderId", i."Id" AS item_id, i."OnHand", COALESCE(p."NegativeStockAllowed", false) AS negative_allowed,
                     GREATEST(l."OrderedQuantity" - LEAST(l."OrderedQuantity", l."CancelledQuantity"), LEAST(l."OrderedQuantity", GREATEST(l."ShippedQuantity", l."DeliveredQuantity"))) AS target
              FROM targets t
              JOIN sales.order_lines l ON l."Id" = t.line_id
              JOIN sales.orders o ON o."TenantId" = l."TenantId" AND o."Id" = l."OrderId"
              JOIN integration.platform_connections c ON c."TenantId" = o."TenantId" AND c."Id" = o."ConnectionId" AND upper(c."PlatformCode") = 'TRENDYOL'
              JOIN inventory.inventory_items i ON i."TenantId" = l."TenantId" AND i."Id" = t.item_id AND i."VariantId" = l."VariantId" AND i."LocationCode" = 'MAIN'
              LEFT JOIN inventory.connection_inventory_policies p ON p."TenantId" = o."TenantId" AND p."ConnectionId" = o."ConnectionId"),
            candidates AS (
              SELECT x.*, CASE WHEN x.negative_allowed THEN x.target ELSE LEAST(x.target, GREATEST(x."OnHand", 0)) END AS taken
              FROM lines x
              WHERE x.target > 0
                AND (SELECT sum(e."QuantityDelta") FROM inventory.stock_ledger_entries e
                     WHERE e."TenantId" = x."TenantId" AND e."InventoryItemId" = x.item_id AND e."SourceType" = 'ORDER_LINE'
                       AND e."SourceId" = x.line_id::text AND e."MovementType" = 'ORDER_STOCK_BASELINE') = x.target
                AND NOT EXISTS (SELECT 1 FROM inventory.stock_ledger_entries e
                     WHERE e."TenantId" = x."TenantId" AND e."SourceType" = 'ORDER_LINE' AND e."SourceId" = x.line_id::text
                       AND e."MovementType" NOT IN ('ORDER_STOCK_BASELINE', 'ORDER_SHIPMENT_PROGRESS'))
                AND NOT EXISTS (SELECT 1 FROM inventory.stock_reservations r
                     WHERE r."TenantId" = x."TenantId" AND r."SourceType" = 'ORDER_LINE' AND r."SourceId" = x.line_id::text)
                AND (SELECT count(*) FROM lines y WHERE y.item_id = x.item_id) = 1),
            baseline AS (
              INSERT INTO inventory.stock_ledger_entries ("Id", "TenantId", "InventoryItemId", "MovementType", "QuantityDelta", "SourceType", "SourceId", "SourceEventId", "IdempotencyKey", "OccurredAt", "RecordedAt", "ActorUserId", "CorrelationId")
              SELECT uuidv7(), c."TenantId", c.item_id, 'ORDER_STOCK_BASELINE', -c.target, 'ORDER_LINE', c.line_id::text,
                     'repair-20261030:baseline:' || c.line_id::text, 'order-stock-baseline-repair-20261030:' || replace(c.item_id::text, '-', '') || ':' || c.line_id::text,
                     now(), now(), NULL, 'order:' || replace(c."OrderId"::text, '-', '')
              FROM candidates c
              RETURNING "SourceId"),
            received AS (
              INSERT INTO inventory.stock_ledger_entries ("Id", "TenantId", "InventoryItemId", "MovementType", "QuantityDelta", "SourceType", "SourceId", "SourceEventId", "IdempotencyKey", "OccurredAt", "RecordedAt", "ActorUserId", "CorrelationId")
              SELECT uuidv7(), c."TenantId", c.item_id, 'ORDER_RECEIVED', -c.taken, 'ORDER_LINE', c.line_id::text,
                     'repair-20261030:received:' || c.line_id::text, 'order-stock-received:' || replace(c.item_id::text, '-', '') || ':repair-20261030:' || c.line_id::text,
                     now(), now(), NULL, 'order:' || replace(c."OrderId"::text, '-', '')
              FROM candidates c WHERE c.taken > 0
              RETURNING "SourceId"),
            shortfall AS (
              INSERT INTO inventory.stock_ledger_entries ("Id", "TenantId", "InventoryItemId", "MovementType", "QuantityDelta", "SourceType", "SourceId", "SourceEventId", "IdempotencyKey", "OccurredAt", "RecordedAt", "ActorUserId", "CorrelationId")
              SELECT uuidv7(), c."TenantId", c.item_id, 'ORDER_STOCK_SHORTFALL', c.target - c.taken, 'ORDER_LINE', c.line_id::text,
                     'repair-20261030:shortfall:' || c.line_id::text, 'order-stock-shortfall:' || replace(c.item_id::text, '-', '') || ':repair-20261030:' || c.line_id::text,
                     now(), now(), NULL, 'order:' || replace(c."OrderId"::text, '-', '')
              FROM candidates c WHERE c.target > c.taken
              RETURNING "SourceId")
            UPDATE inventory.inventory_items i
            SET "OnHand" = i."OnHand" - c.taken,
                "Available" = GREATEST(0, i."OnHand" - c.taken - i."Reserved"),
                "ProjectionVersion" = i."ProjectionVersion" + 1,
                "Version" = i."Version" + 1
            FROM candidates c
            WHERE i."TenantId" = c."TenantId" AND i."Id" = c.item_id AND c.taken > 0;
            """;

        // Part 2: owner decision, the panel holds no reservations. Every active order-line reservation of an order that
        // has settled (shipped, delivered, undelivered, returned or cancelled; 17 on live on 30 Oct 2026, all Trendyol,
        // 16 delivered and 1 shipped) is released without touching on-hand: an ORDER_RESERVATION_RELEASED row gives the
        // reserved units back to Available, and a shortfall marker keeps them as units the order could not take from
        // stock, so the order projection neither takes them from stock later nor reserves them again. Reserved goes
        // down by the released quantity, Available = greatest(0, OnHand - Reserved), ProjectionVersion and Version move,
        // and the line's STOCK_ORDER_CONSUMPTION_BLOCKED issue is resolved. A second run finds no active reservation.
        // An open order's reservation is left to the order projection, which converts it the same way when it reads the
        // order again.
        // tenantId: limits the release to one tenant (tests); the migration releases them for every tenant.
        internal static string ReleaseReservationsSql(Guid? tenantId = null) => $$"""
            WITH released AS (
              UPDATE inventory.stock_reservations r
              SET "Status" = 'Released', "ReleasedAt" = now(), "ExpiresAt" = NULL, "Version" = r."Version" + 1
              FROM sales.order_lines l
              JOIN sales.orders o ON o."TenantId" = l."TenantId" AND o."Id" = l."OrderId"
              WHERE r."SourceType" = 'ORDER_LINE' AND r."Status" = 'Active'{{(tenantId is { } tenant ? $" AND r.\"TenantId\" = '{tenant:D}'" : "")}}
                AND l."TenantId" = r."TenantId" AND l."Id"::text = r."SourceId"
                AND o."DerivedStatus" IN ('SHIPPED', 'DELIVERED', 'UNDELIVERED', 'RETURN_IN_TRANSIT', 'RETURNED', 'CANCELLED')
              RETURNING r."Id", r."TenantId", r."InventoryItemId", r."SourceId", r."Quantity", l."OrderId"),
            release_rows AS (
              INSERT INTO inventory.stock_ledger_entries ("Id", "TenantId", "InventoryItemId", "MovementType", "QuantityDelta", "SourceType", "SourceId", "SourceEventId", "IdempotencyKey", "OccurredAt", "RecordedAt", "ActorUserId", "CorrelationId")
              SELECT uuidv7(), x."TenantId", x."InventoryItemId", 'ORDER_RESERVATION_RELEASED', x."Quantity", 'ORDER_LINE', x."SourceId",
                     'repair-20261030:reservation-to-shortfall:' || replace(x."Id"::text, '-', ''), 'order-reservation:repair-20261030:' || replace(x."Id"::text, '-', ''),
                     now(), now(), NULL, 'order:' || replace(x."OrderId"::text, '-', '')
              FROM released x
              RETURNING "SourceId"),
            shortfall_rows AS (
              INSERT INTO inventory.stock_ledger_entries ("Id", "TenantId", "InventoryItemId", "MovementType", "QuantityDelta", "SourceType", "SourceId", "SourceEventId", "IdempotencyKey", "OccurredAt", "RecordedAt", "ActorUserId", "CorrelationId")
              SELECT uuidv7(), x."TenantId", x."InventoryItemId", 'ORDER_STOCK_SHORTFALL', x."Quantity", 'ORDER_LINE', x."SourceId",
                     'repair-20261030:reservation-to-shortfall:' || replace(x."Id"::text, '-', ''), 'order-stock-shortfall:' || replace(x."InventoryItemId"::text, '-', '') || ':repair-20261030:' || replace(x."Id"::text, '-', ''),
                     now(), now(), NULL, 'order:' || replace(x."OrderId"::text, '-', '')
              FROM released x
              RETURNING "SourceId"),
            issues AS (
              UPDATE ops.operational_issues s
              SET "Status" = 'Resolved', "ResolvedAt" = now()
              FROM released x
              WHERE s."TenantId" = x."TenantId" AND s."DedupeKey" = 'stock-order-consumption:' || replace(x."SourceId", '-', '') AND s."Status" <> 'Resolved'
              RETURNING s."Id"),
            per_item AS (
              SELECT "TenantId", "InventoryItemId", sum("Quantity") AS quantity FROM released GROUP BY "TenantId", "InventoryItemId")
            UPDATE inventory.inventory_items i
            SET "Reserved" = GREATEST(0, i."Reserved" - p.quantity),
                "Available" = GREATEST(0, i."OnHand" - GREATEST(0, i."Reserved" - p.quantity)),
                "ProjectionVersion" = i."ProjectionVersion" + 1,
                "Version" = i."Version" + 1
            FROM per_item p
            WHERE i."TenantId" = p."TenantId" AND i."Id" = p."InventoryItemId";
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(RepairMissedLinesSql(MissedLines));
            migrationBuilder.Sql(ReleaseReservationsSql());
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // A data repair: the deducted stock and the released reservations are not put back.
        }
    }
}
