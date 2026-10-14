using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddInventoryRemoteObservation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ObservedRemoteAt",
                schema: "inventory",
                table: "inventory_items",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "ObservedRemoteQuantity",
                schema: "inventory",
                table: "inventory_items",
                type: "numeric(19,4)",
                precision: 19,
                scale: 4,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_inventory_items_TenantId_ObservedRemoteAt",
                schema: "inventory",
                table: "inventory_items",
                columns: new[] { "TenantId", "ObservedRemoteAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_inventory_items_TenantId_ObservedRemoteAt",
                schema: "inventory",
                table: "inventory_items");

            migrationBuilder.DropColumn(
                name: "ObservedRemoteAt",
                schema: "inventory",
                table: "inventory_items");

            migrationBuilder.DropColumn(
                name: "ObservedRemoteQuantity",
                schema: "inventory",
                table: "inventory_items");
        }
    }
}
