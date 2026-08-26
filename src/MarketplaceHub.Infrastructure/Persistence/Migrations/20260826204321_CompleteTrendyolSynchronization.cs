using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CompleteTrendyolSynchronization : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ConsecutiveFailureCount",
                schema: "integration",
                table: "sync_cursors",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastAttemptAt",
                schema: "integration",
                table: "sync_cursors",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LastChangedCount",
                schema: "integration",
                table: "sync_cursors",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<long>(
                name: "LastDurationMs",
                schema: "integration",
                table: "sync_cursors",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastError",
                schema: "integration",
                table: "sync_cursors",
                type: "character varying(1024)",
                maxLength: 1024,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastErrorAt",
                schema: "integration",
                table: "sync_cursors",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LastReceivedCount",
                schema: "integration",
                table: "sync_cursors",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "DirtyFieldsJson",
                schema: "catalog",
                table: "marketplace_product_links",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastError",
                schema: "catalog",
                table: "marketplace_product_links",
                type: "character varying(1024)",
                maxLength: 1024,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastImportedAt",
                schema: "catalog",
                table: "marketplace_product_links",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastImportedPayloadHash",
                schema: "catalog",
                table: "marketplace_product_links",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "LastImportedProductVersion",
                schema: "catalog",
                table: "marketplace_product_links",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastPublishedAt",
                schema: "catalog",
                table: "marketplace_product_links",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "LastPublishedProductVersion",
                schema: "catalog",
                table: "marketplace_product_links",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<string>(
                name: "SyncStatus",
                schema: "catalog",
                table: "marketplace_product_links",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "SYNCED");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ConsecutiveFailureCount",
                schema: "integration",
                table: "sync_cursors");

            migrationBuilder.DropColumn(
                name: "LastAttemptAt",
                schema: "integration",
                table: "sync_cursors");

            migrationBuilder.DropColumn(
                name: "LastChangedCount",
                schema: "integration",
                table: "sync_cursors");

            migrationBuilder.DropColumn(
                name: "LastDurationMs",
                schema: "integration",
                table: "sync_cursors");

            migrationBuilder.DropColumn(
                name: "LastError",
                schema: "integration",
                table: "sync_cursors");

            migrationBuilder.DropColumn(
                name: "LastErrorAt",
                schema: "integration",
                table: "sync_cursors");

            migrationBuilder.DropColumn(
                name: "LastReceivedCount",
                schema: "integration",
                table: "sync_cursors");

            migrationBuilder.DropColumn(
                name: "DirtyFieldsJson",
                schema: "catalog",
                table: "marketplace_product_links");

            migrationBuilder.DropColumn(
                name: "LastError",
                schema: "catalog",
                table: "marketplace_product_links");

            migrationBuilder.DropColumn(
                name: "LastImportedAt",
                schema: "catalog",
                table: "marketplace_product_links");

            migrationBuilder.DropColumn(
                name: "LastImportedPayloadHash",
                schema: "catalog",
                table: "marketplace_product_links");

            migrationBuilder.DropColumn(
                name: "LastImportedProductVersion",
                schema: "catalog",
                table: "marketplace_product_links");

            migrationBuilder.DropColumn(
                name: "LastPublishedAt",
                schema: "catalog",
                table: "marketplace_product_links");

            migrationBuilder.DropColumn(
                name: "LastPublishedProductVersion",
                schema: "catalog",
                table: "marketplace_product_links");

            migrationBuilder.DropColumn(
                name: "SyncStatus",
                schema: "catalog",
                table: "marketplace_product_links");
        }
    }
}
