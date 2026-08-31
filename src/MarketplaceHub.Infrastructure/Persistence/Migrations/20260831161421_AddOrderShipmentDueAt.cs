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
                UPDATE sales.orders AS order_row
                SET "ShipmentDueAt" = to_timestamp(due_value.raw_value::double precision / 1000)
                FROM LATERAL (
                    SELECT COALESCE(
                        order_row."CustomerSnapshotJson"::jsonb ->> 'agreedDeliveryDate',
                        order_row."CustomerSnapshotJson"::jsonb ->> 'estimatedDeliveryEndDate',
                        order_row."CustomerSnapshotJson"::jsonb ->> 'lastDeliveryDate',
                        order_row."CustomerSnapshotJson"::jsonb ->> 'deliveryDate',
                        order_row."CustomerSnapshotJson"::jsonb ->> 'estimatedDeliveryStartDate',
                        order_row."CustomerSnapshotJson"::jsonb ->> 'dueDate',
                        order_row."CustomerSnapshotJson"::jsonb ->> 'shipmentDueDate',
                        order_row."CustomerSnapshotJson"::jsonb ->> 'deliveryDueAt'
                    ) AS raw_value
                ) AS due_value
                WHERE order_row."ShipmentDueAt" IS NULL
                  AND due_value.raw_value ~ '^[0-9]+$';
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
