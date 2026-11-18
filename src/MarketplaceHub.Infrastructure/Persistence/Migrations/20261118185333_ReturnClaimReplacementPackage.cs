using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ReturnClaimReplacementPackage : Migration
    {
        // The exchange package Trendyol names on a claim (getClaims replacementOutboundpackageinfo: package id, carrier,
        // tracking number and link), 18 Nov 2026. Additive only: four nullable columns, no row is written; null stays
        // "not named" (unknown). The previous code ignores them during the deploy window.
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ReplacementCargoProviderName",
                schema: "sales",
                table: "return_claims",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReplacementCargoTrackingLink",
                schema: "sales",
                table: "return_claims",
                type: "character varying(2048)",
                maxLength: 2048,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReplacementCargoTrackingNumber",
                schema: "sales",
                table: "return_claims",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReplacementPackageId",
                schema: "sales",
                table: "return_claims",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ReplacementCargoProviderName",
                schema: "sales",
                table: "return_claims");

            migrationBuilder.DropColumn(
                name: "ReplacementCargoTrackingLink",
                schema: "sales",
                table: "return_claims");

            migrationBuilder.DropColumn(
                name: "ReplacementCargoTrackingNumber",
                schema: "sales",
                table: "return_claims");

            migrationBuilder.DropColumn(
                name: "ReplacementPackageId",
                schema: "sales",
                table: "return_claims");
        }
    }
}
