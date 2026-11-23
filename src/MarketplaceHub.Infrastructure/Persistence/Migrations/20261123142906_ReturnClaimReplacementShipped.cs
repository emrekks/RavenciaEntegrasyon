using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ReturnClaimReplacementShipped : Migration
    {
        // The owner's "Kargoya Verildi" mark of an exchange package (23 Nov 2026): when he marked it and its source (MANUAL).
        // Additive only: two nullable columns, no row is written; null stays "not marked". The previous code ignores them
        // during the deploy window.
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ReplacementShippedAt",
                schema: "sales",
                table: "return_claims",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReplacementShippedSource",
                schema: "sales",
                table: "return_claims",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ReplacementShippedAt",
                schema: "sales",
                table: "return_claims");

            migrationBuilder.DropColumn(
                name: "ReplacementShippedSource",
                schema: "sales",
                table: "return_claims");
        }
    }
}
