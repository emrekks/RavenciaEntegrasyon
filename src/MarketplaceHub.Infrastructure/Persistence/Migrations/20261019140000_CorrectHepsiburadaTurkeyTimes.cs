using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceHub.Infrastructure.Persistence.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20261019140000_CorrectHepsiburadaTurkeyTimes")]
public sealed class CorrectHepsiburadaTurkeyTimes : Migration
{
    // One row per Hepsiburada order: its order and due dates read again from the line the marketplace sent, when the
    // stored value is still the old reading of that text.
    internal const string Corrections = """
        WITH hb AS (
            SELECT o."Id",
                   o."OrderedAt" AS "OldOrderedAt",
                   CASE WHEN od.value !~ '(Z|[+-][0-9]{2}:?[0-9]{2})$'
                             AND o."OrderedAt" = od.value::timestamp AT TIME ZONE 'UTC'
                        THEN od.value::timestamp AT TIME ZONE 'Europe/Istanbul' END AS "NewOrderedAt",
                   CASE WHEN dd.value !~ '(Z|[+-][0-9]{2}:?[0-9]{2})$'
                             AND o."ShipmentDueAt" = dd.value::timestamp AT TIME ZONE 'UTC'
                        THEN dd.value::timestamp AT TIME ZONE 'Europe/Istanbul' END AS "NewShipmentDueAt",
                   o."UpdatedAt" < TIMESTAMPTZ '2026-10-19 12:30:00+00' AS "ShiftChangeTime"
            FROM sales.orders o
            JOIN integration.platform_connections c ON c."Id" = o."ConnectionId" AND c."PlatformCode" = 'HEPSIBURADA'
            LEFT JOIN LATERAL (
                SELECT l."SourceSnapshotJson"::jsonb ->> 'orderDate' AS value
                FROM sales.order_lines l
                WHERE l."OrderId" = o."Id" AND l."SourceSnapshotJson" LIKE '{%'
                ORDER BY l."Id" LIMIT 1) od ON true
            LEFT JOIN LATERAL (
                SELECT l."SourceSnapshotJson"::jsonb ->> 'dueDate' AS value
                FROM sales.order_lines l
                WHERE l."OrderId" = o."Id" AND l."SourceSnapshotJson" LIKE '{%'
                ORDER BY l."Id" LIMIT 1) dd ON true)
        """;

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Hepsiburada sends order dates as Turkey wall time without an offset. Until 2026-10-19 12:30 UTC they were
        // read as UTC, so every Hepsiburada order date, deadline and change time stood three hours late. Package and
        // history times that were the order date follow it. The change time cannot be read again, so it moves back
        // three hours on orders not written since the fix; otherwise a correct newer read looks older and is skipped.
        // Orders are corrected last because the other statements find their rows by the old order date.
        migrationBuilder.Sql(Corrections + """
            UPDATE sales.shipment_packages p
            SET "StatusOccurredAt" = hb."NewOrderedAt"
            FROM hb
            WHERE p."OrderId" = hb."Id" AND hb."NewOrderedAt" IS NOT NULL AND p."StatusOccurredAt" = hb."OldOrderedAt";
            """);
        migrationBuilder.Sql(Corrections + """
            UPDATE sales.order_status_history h
            SET "OccurredAt" = hb."NewOrderedAt"
            FROM hb
            WHERE h."OrderId" = hb."Id" AND hb."NewOrderedAt" IS NOT NULL AND h."OccurredAt" = hb."OldOrderedAt";
            """);
        migrationBuilder.Sql(Corrections + """
            UPDATE sales.orders o
            SET "OrderedAt" = COALESCE(hb."NewOrderedAt", o."OrderedAt"),
                "ShipmentDueAt" = COALESCE(hb."NewShipmentDueAt", o."ShipmentDueAt"),
                "LastRemoteModifiedAt" = CASE WHEN hb."ShiftChangeTime" THEN o."LastRemoteModifiedAt" - INTERVAL '3 hours' ELSE o."LastRemoteModifiedAt" END,
                "Version" = o."Version" + 1
            FROM hb
            WHERE o."Id" = hb."Id" AND (hb."NewOrderedAt" IS NOT NULL OR hb."NewShipmentDueAt" IS NOT NULL OR hb."ShiftChangeTime");
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // The old values were wrong; there is nothing to restore.
    }
}
