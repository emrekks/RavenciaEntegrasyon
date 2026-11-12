using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DropDeadDashboardSnapshotColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ActiveConnections",
                schema: "dashboard",
                table: "snapshot");

            migrationBuilder.DropColumn(
                name: "DueSoonInvoices",
                schema: "dashboard",
                table: "snapshot");

            migrationBuilder.DropColumn(
                name: "LastVerifiedSynchronizationAt",
                schema: "dashboard",
                table: "snapshot");

            migrationBuilder.DropColumn(
                name: "LowStockProducts",
                schema: "dashboard",
                table: "snapshot");

            migrationBuilder.DropColumn(
                name: "MonthOrders",
                schema: "dashboard",
                table: "snapshot");

            migrationBuilder.DropColumn(
                name: "MonthProductQuantity",
                schema: "dashboard",
                table: "snapshot");

            migrationBuilder.DropColumn(
                name: "OldestQueuedJobAt",
                schema: "dashboard",
                table: "snapshot");

            migrationBuilder.DropColumn(
                name: "OldestStockObservationAt",
                schema: "dashboard",
                table: "snapshot");

            migrationBuilder.DropColumn(
                name: "RecentRateLimitJobCount",
                schema: "dashboard",
                table: "snapshot");

            migrationBuilder.DropColumn(
                name: "TodayOrders",
                schema: "dashboard",
                table: "snapshot");

            migrationBuilder.DropColumn(
                name: "TodayProductQuantity",
                schema: "dashboard",
                table: "snapshot");

            migrationBuilder.DropColumn(
                name: "UninvoicedInvoices",
                schema: "dashboard",
                table: "snapshot");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ActiveConnections",
                schema: "dashboard",
                table: "snapshot",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "DueSoonInvoices",
                schema: "dashboard",
                table: "snapshot",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastVerifiedSynchronizationAt",
                schema: "dashboard",
                table: "snapshot",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LowStockProducts",
                schema: "dashboard",
                table: "snapshot",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "MonthOrders",
                schema: "dashboard",
                table: "snapshot",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<decimal>(
                name: "MonthProductQuantity",
                schema: "dashboard",
                table: "snapshot",
                type: "numeric",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "OldestQueuedJobAt",
                schema: "dashboard",
                table: "snapshot",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "OldestStockObservationAt",
                schema: "dashboard",
                table: "snapshot",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RecentRateLimitJobCount",
                schema: "dashboard",
                table: "snapshot",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "TodayOrders",
                schema: "dashboard",
                table: "snapshot",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<decimal>(
                name: "TodayProductQuantity",
                schema: "dashboard",
                table: "snapshot",
                type: "numeric",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<int>(
                name: "UninvoicedInvoices",
                schema: "dashboard",
                table: "snapshot",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }
    }
}
