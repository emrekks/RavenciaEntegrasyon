using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMarketplaceVariantLinkHealth : Migration
    {
        // Whether a linked code still holds at the marketplace: when an import last saw it and why it broke.
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "HealthChangedAt",
                schema: "catalog",
                table: "marketplace_variant_links",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "HealthReason",
                schema: "catalog",
                table: "marketplace_variant_links",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "HealthStatus",
                schema: "catalog",
                table: "marketplace_variant_links",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastSeenAt",
                schema: "catalog",
                table: "marketplace_variant_links",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "HealthChangedAt",
                schema: "catalog",
                table: "marketplace_variant_links");

            migrationBuilder.DropColumn(
                name: "HealthReason",
                schema: "catalog",
                table: "marketplace_variant_links");

            migrationBuilder.DropColumn(
                name: "HealthStatus",
                schema: "catalog",
                table: "marketplace_variant_links");

            migrationBuilder.DropColumn(
                name: "LastSeenAt",
                schema: "catalog",
                table: "marketplace_variant_links");
        }
    }
}
