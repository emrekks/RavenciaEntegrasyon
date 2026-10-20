using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class WorkerConflictCountsLockGroupsAndRetention : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastSliceAt",
                schema: "integration",
                table: "sync_cursors",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ResolvedAt",
                schema: "ops",
                table: "operational_issues",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ConflictCount",
                schema: "integration",
                table: "jobs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "LockGroup",
                schema: "integration",
                table: "jobs",
                type: "character varying(48)",
                maxLength: 48,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastCheckedAt",
                schema: "integration",
                table: "external_effect_records",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "UnclearChecks",
                schema: "integration",
                table: "external_effect_records",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "VerificationSeconds",
                schema: "integration",
                table: "external_effect_records",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastSliceAt",
                schema: "integration",
                table: "sync_cursors");

            migrationBuilder.DropColumn(
                name: "ResolvedAt",
                schema: "ops",
                table: "operational_issues");

            migrationBuilder.DropColumn(
                name: "ConflictCount",
                schema: "integration",
                table: "jobs");

            migrationBuilder.DropColumn(
                name: "LockGroup",
                schema: "integration",
                table: "jobs");

            migrationBuilder.DropColumn(
                name: "LastCheckedAt",
                schema: "integration",
                table: "external_effect_records");

            migrationBuilder.DropColumn(
                name: "UnclearChecks",
                schema: "integration",
                table: "external_effect_records");

            migrationBuilder.DropColumn(
                name: "VerificationSeconds",
                schema: "integration",
                table: "external_effect_records");
        }
    }
}
