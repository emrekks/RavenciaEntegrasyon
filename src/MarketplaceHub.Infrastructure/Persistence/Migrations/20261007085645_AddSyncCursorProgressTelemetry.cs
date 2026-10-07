using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSyncCursorProgressTelemetry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastCursorAdvancedAt",
                schema: "integration",
                table: "sync_cursors",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CursorStagnantSince",
                schema: "integration",
                table: "sync_cursors",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastCursorAdvancedAt",
                schema: "integration",
                table: "sync_cursors");

            migrationBuilder.DropColumn(
                name: "CursorStagnantSince",
                schema: "integration",
                table: "sync_cursors");
        }
    }
}
