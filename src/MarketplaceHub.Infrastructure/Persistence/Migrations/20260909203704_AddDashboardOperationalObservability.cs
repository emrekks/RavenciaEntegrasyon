using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDashboardOperationalObservability : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "DeadJobCount",
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
                name: "ManualReviewJobCount",
                schema: "dashboard",
                table: "snapshot",
                type: "integer",
                nullable: false,
                defaultValue: 0);

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
                name: "RecentJobCount",
                schema: "dashboard",
                table: "snapshot",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "RecentRateLimitJobCount",
                schema: "dashboard",
                table: "snapshot",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DeadJobCount",
                schema: "dashboard",
                table: "snapshot");

            migrationBuilder.DropColumn(
                name: "LastVerifiedSynchronizationAt",
                schema: "dashboard",
                table: "snapshot");

            migrationBuilder.DropColumn(
                name: "ManualReviewJobCount",
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
                name: "RecentJobCount",
                schema: "dashboard",
                table: "snapshot");

            migrationBuilder.DropColumn(
                name: "RecentRateLimitJobCount",
                schema: "dashboard",
                table: "snapshot");
        }
    }
}
