using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddOrderShipmentDueAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ShipmentDueAt",
                schema: "sales",
                table: "orders",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.Sql("""
                WITH order_snapshots AS (
                    SELECT
                        "Id",
                        CASE
                            WHEN pg_input_is_valid("CustomerSnapshotJson", 'jsonb')
                            THEN "CustomerSnapshotJson"::jsonb
                        END AS snapshot
                    FROM sales.orders
                ), due_values AS (
                    SELECT
                        "Id",
                        COALESCE(
                            snapshot ->> 'agreedDeliveryDate',
                            snapshot ->> 'estimatedDeliveryEndDate',
                            snapshot ->> 'lastDeliveryDate',
                            snapshot ->> 'deliveryDate',
                            snapshot ->> 'estimatedDeliveryStartDate',
                            snapshot ->> 'dueDate',
                            snapshot ->> 'shipmentDueDate',
                            snapshot ->> 'deliveryDueAt'
                        ) AS raw_value
                    FROM order_snapshots
                )
                UPDATE sales.orders AS order_row
                SET "ShipmentDueAt" = to_timestamp(due_values.raw_value::double precision / 1000)
                FROM due_values
                WHERE order_row."Id" = due_values."Id"
                  AND order_row."ShipmentDueAt" IS NULL
                  AND due_values.raw_value ~ '^[0-9]+$';
                """);

            migrationBuilder.CreateIndex(
                name: "IX_orders_TenantId_ShipmentDueAt_OrderedAt_Id",
                schema: "sales",
                table: "orders",
                columns: new[] { "TenantId", "ShipmentDueAt", "OrderedAt", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_orders_TenantId_ShipmentDueAt_OrderedAt_Id",
                schema: "sales",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "ShipmentDueAt",
                schema: "sales",
                table: "orders");
        }
    }
}
