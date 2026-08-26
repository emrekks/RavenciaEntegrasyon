using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CompleteSynchronizationTelemetry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "LastFailedCount",
                schema: "integration",
                table: "sync_cursors",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "LastInsertedCount",
                schema: "integration",
                table: "sync_cursors",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "LastRateLimitCount",
                schema: "integration",
                table: "sync_cursors",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "LastRequestCount",
                schema: "integration",
                table: "sync_cursors",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "LastRetryCount",
                schema: "integration",
                table: "sync_cursors",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "LastSkippedCount",
                schema: "integration",
                table: "sync_cursors",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "LastUpdatedCount",
                schema: "integration",
                table: "sync_cursors",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastFailedCount",
                schema: "integration",
                table: "sync_cursors");

            migrationBuilder.DropColumn(
                name: "LastInsertedCount",
                schema: "integration",
                table: "sync_cursors");

            migrationBuilder.DropColumn(
                name: "LastRateLimitCount",
                schema: "integration",
                table: "sync_cursors");

            migrationBuilder.DropColumn(
                name: "LastRequestCount",
                schema: "integration",
                table: "sync_cursors");

            migrationBuilder.DropColumn(
                name: "LastRetryCount",
                schema: "integration",
                table: "sync_cursors");

            migrationBuilder.DropColumn(
                name: "LastSkippedCount",
                schema: "integration",
                table: "sync_cursors");

            migrationBuilder.DropColumn(
                name: "LastUpdatedCount",
                schema: "integration",
                table: "sync_cursors");
        }
    }
}
