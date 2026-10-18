using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddExternalEffectContext : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Action",
                schema: "integration",
                table: "external_effect_records",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ConnectionId",
                schema: "integration",
                table: "external_effect_records",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExpectedValue",
                schema: "integration",
                table: "external_effect_records",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Outcome",
                schema: "integration",
                table: "external_effect_records",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TargetId",
                schema: "integration",
                table: "external_effect_records",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_external_effect_records_CreatedAt",
                schema: "integration",
                table: "external_effect_records",
                column: "CreatedAt",
                filter: "\"CompletedAt\" IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_external_effect_records_CreatedAt",
                schema: "integration",
                table: "external_effect_records");

            migrationBuilder.DropColumn(
                name: "Action",
                schema: "integration",
                table: "external_effect_records");

            migrationBuilder.DropColumn(
                name: "ConnectionId",
                schema: "integration",
                table: "external_effect_records");

            migrationBuilder.DropColumn(
                name: "ExpectedValue",
                schema: "integration",
                table: "external_effect_records");

            migrationBuilder.DropColumn(
                name: "Outcome",
                schema: "integration",
                table: "external_effect_records");

            migrationBuilder.DropColumn(
                name: "TargetId",
                schema: "integration",
                table: "external_effect_records");
        }
    }
}
