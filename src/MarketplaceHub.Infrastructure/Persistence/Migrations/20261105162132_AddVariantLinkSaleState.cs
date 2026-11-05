using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddVariantLinkSaleState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SaleState",
                schema: "catalog",
                table: "marketplace_variant_links",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "SaleStateReadAt",
                schema: "catalog",
                table: "marketplace_variant_links",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SaleStateReason",
                schema: "catalog",
                table: "marketplace_variant_links",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SaleState",
                schema: "catalog",
                table: "marketplace_variant_links");

            migrationBuilder.DropColumn(
                name: "SaleStateReadAt",
                schema: "catalog",
                table: "marketplace_variant_links");

            migrationBuilder.DropColumn(
                name: "SaleStateReason",
                schema: "catalog",
                table: "marketplace_variant_links");
        }
    }
}
