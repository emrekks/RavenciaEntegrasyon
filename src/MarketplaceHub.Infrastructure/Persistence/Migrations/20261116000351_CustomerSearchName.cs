using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CustomerSearchName : Migration
    {
        // SIPARIS-STOK-9: the order's customer name as the screens show it, kept for the Returns search (OrderCustomerSearchNames).
        // Two nullable columns (no rewrite); the names are filled by the app (tracked saves and the maintenance backfill, in
        // batches), as the rule is C# code. The trigger empties a name whose snapshots changed in an UPDATE that did not bring
        // a new stamp (a snapshot write outside the change tracker, or the previous release during the deploy), so a kept
        // name is never older than its snapshots; an empty name is read from the order's view, as before.
        // SIPARIS-STOK-2: a partial index for the unmatched-line rematch, built CONCURRENTLY (AddProductVariantModelCodeIndex).
        /// <inheritdoc />
        // Ordering rule: the CONCURRENTLY statements come first. EF commits its open transaction at the first
        // suppressTransaction command, so anything above them would be applied for good before the index; if the index build
        // then failed, a rerun would stop on the existing columns and never reach the DROP INDEX that clears the INVALID
        // index. With the index first, a failed build leaves nothing else applied and a rerun starts clean; the columns, the
        // function, the trigger and the history row then commit together in one transaction.
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("SET lock_timeout = 0", suppressTransaction: true);
            migrationBuilder.Sql(
                "DROP INDEX CONCURRENTLY IF EXISTS sales.\"IX_order_lines_TenantId_Id_Unmatched\"",
                suppressTransaction: true);
            migrationBuilder.Sql(
                "CREATE INDEX CONCURRENTLY \"IX_order_lines_TenantId_Id_Unmatched\" ON sales.order_lines (\"TenantId\", \"Id\") WHERE \"VariantId\" IS NULL",
                suppressTransaction: true);
            migrationBuilder.Sql("RESET lock_timeout", suppressTransaction: true);

            migrationBuilder.AddColumn<string>(
                name: "CustomerSearchName",
                schema: "sales",
                table: "orders",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "CustomerSearchStamp",
                schema: "sales",
                table: "orders",
                type: "uuid",
                nullable: true);

            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION sales.orders_customer_search_name_stale() RETURNS trigger
                LANGUAGE plpgsql AS $function$
                BEGIN
                    IF NEW."CustomerSearchStamp" IS NOT DISTINCT FROM OLD."CustomerSearchStamp" THEN
                        NEW."CustomerSearchName" := NULL;
                    END IF;
                    RETURN NEW;
                END
                $function$;
                """);
            migrationBuilder.Sql("""
                CREATE TRIGGER orders_customer_search_name_stale
                BEFORE UPDATE OF "CustomerSnapshotJson", "InvoiceAddressSnapshotJson", "ShipmentAddressSnapshotJson" ON sales.orders
                FOR EACH ROW
                WHEN (NEW."CustomerSnapshotJson" IS DISTINCT FROM OLD."CustomerSnapshotJson"
                    OR NEW."InvoiceAddressSnapshotJson" IS DISTINCT FROM OLD."InvoiceAddressSnapshotJson"
                    OR NEW."ShipmentAddressSnapshotJson" IS DISTINCT FROM OLD."ShipmentAddressSnapshotJson")
                EXECUTE FUNCTION sales.orders_customer_search_name_stale();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "DROP INDEX CONCURRENTLY IF EXISTS sales.\"IX_order_lines_TenantId_Id_Unmatched\"",
                suppressTransaction: true);
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS orders_customer_search_name_stale ON sales.orders");
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS sales.orders_customer_search_name_stale()");

            migrationBuilder.DropColumn(
                name: "CustomerSearchName",
                schema: "sales",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "CustomerSearchStamp",
                schema: "sales",
                table: "orders");
        }
    }
}
