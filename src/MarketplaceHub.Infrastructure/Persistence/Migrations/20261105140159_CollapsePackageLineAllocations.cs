using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceHub.Infrastructure.Persistence.Migrations
{
    // T2-DO1: an order read wrote a package line's allocation once per package event, so a line held one row per read
    // that saw its package (live 5 Nov 2026: 3394 rows on 2520 package lines, 874 extra rows on 371 lines of 287 orders,
    // up to 7 rows for a one-unit line). The panel's readers took the latest row, but a sum over the rows counted a line
    // several times. From now on a package line has one allocation, rewritten by each later event (unique per tenant,
    // package and line).
    //
    // Each package line keeps one row: the one written by the package's current event (its ExternalPackageId and
    // StatusOccurredAt, the row the line projection reads), else the latest event's, then the highest id. Live, all 371
    // lines' current-event row was also their latest. Every other row is written to ops.audit_logs (CorrelationId
    // 'migration:20261105140159_CollapsePackageLineAllocations', with its event and quantities) and deleted. The table is
    // locked for the whole migration (ACCESS EXCLUSIVE, which the index swap takes anyway; the table is about 1 MB) so
    // no row slips in before the unique index exists. A second run finds nothing to delete.
    /// <inheritdoc />
    public partial class CollapsePackageLineAllocations : Migration
    {
        internal const string Collapse = """
            DO $$
            BEGIN
                LOCK TABLE sales.package_line_allocations IN ACCESS EXCLUSIVE MODE;
                DROP TABLE IF EXISTS pg_temp.package_line_allocation_extras;
                CREATE TEMP TABLE package_line_allocation_extras ON COMMIT DROP AS
                SELECT ranked."Id", ranked."TenantId", ranked."PackageId", ranked."OrderLineId", ranked."SourceEventId",
                       ranked."AllocatedQuantity", ranked."CancelledQuantity", ranked."ShippedQuantity", ranked."DeliveredQuantity", ranked."ReturnedQuantity"
                FROM (
                    SELECT a.*,
                           row_number() OVER (
                               PARTITION BY a."TenantId", a."PackageId", a."OrderLineId"
                               ORDER BY COALESCE(a."SourceEventId" = p."ExternalPackageId" || ':' || floor(extract(epoch FROM p."StatusOccurredAt") * 1000)::bigint::text, false) DESC,
                                        CASE WHEN a."SourceEventId" ~ ':[0-9]{1,18}$' THEN substring(a."SourceEventId" FROM ':([0-9]{1,18})$')::bigint END DESC NULLS LAST,
                                        a."SourceEventId" COLLATE "C" DESC,
                                        a."Id" DESC) AS keep_rank
                    FROM sales.package_line_allocations a
                    LEFT JOIN sales.shipment_packages p ON p."TenantId" = a."TenantId" AND p."Id" = a."PackageId"
                ) ranked
                WHERE ranked.keep_rank > 1;

                INSERT INTO ops.audit_logs ("TenantId", "ActorUserId", "Action", "TargetType", "TargetId", "Reason", "CorrelationId", "CreatedAt")
                SELECT e."TenantId", NULL::uuid, 'PACKAGE_LINE_ALLOCATION_COLLAPSED', 'PackageLineAllocation', e."Id"::text,
                       left('Paket satırının eski olay tahsisi silindi (paket satırı başına tek tahsis): paket ' || e."PackageId"::text
                            || ', sipariş satırı ' || e."OrderLineId"::text || ', olay ' || e."SourceEventId"
                            || ', tahsis/iptal/kargo/teslim/iade ' || e."AllocatedQuantity"::text || '/' || e."CancelledQuantity"::text || '/'
                            || e."ShippedQuantity"::text || '/' || e."DeliveredQuantity"::text || '/' || e."ReturnedQuantity"::text, 512),
                       'migration:20261105140159_CollapsePackageLineAllocations', now()
                FROM package_line_allocation_extras e;

                DELETE FROM sales.package_line_allocations a
                USING package_line_allocation_extras e
                WHERE a."Id" = e."Id";

                TRUNCATE package_line_allocation_extras;
            END
            $$;
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(Collapse);

            migrationBuilder.DropIndex(
                name: "IX_package_line_allocations_TenantId_PackageId_OrderLineId_Sou~",
                schema: "sales",
                table: "package_line_allocations");

            migrationBuilder.CreateIndex(
                name: "IX_package_line_allocations_TenantId_PackageId_OrderLineId",
                schema: "sales",
                table: "package_line_allocations",
                columns: new[] { "TenantId", "PackageId", "OrderLineId" },
                unique: true);
        }

        // The deleted rows are not put back: they are older events of the same package line, and ops.audit_logs lists
        // each with its quantities.
        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_package_line_allocations_TenantId_PackageId_OrderLineId",
                schema: "sales",
                table: "package_line_allocations");

            migrationBuilder.CreateIndex(
                name: "IX_package_line_allocations_TenantId_PackageId_OrderLineId_Sou~",
                schema: "sales",
                table: "package_line_allocations",
                columns: new[] { "TenantId", "PackageId", "OrderLineId", "SourceEventId" },
                unique: true);
        }
    }
}
