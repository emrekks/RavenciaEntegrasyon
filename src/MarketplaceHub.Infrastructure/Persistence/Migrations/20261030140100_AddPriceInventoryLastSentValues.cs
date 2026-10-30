using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPriceInventoryLastSentValues : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "FulfilledByMarketplace",
                schema: "catalog",
                table: "marketplace_variant_links",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastSentAt",
                schema: "inventory",
                table: "channel_offers",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastSentCode",
                schema: "inventory",
                table: "channel_offers",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "LastSentListPrice",
                schema: "inventory",
                table: "channel_offers",
                type: "numeric(19,4)",
                precision: 19,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "LastSentQuantity",
                schema: "inventory",
                table: "channel_offers",
                type: "numeric(19,4)",
                precision: 19,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "LastSentSalePrice",
                schema: "inventory",
                table: "channel_offers",
                type: "numeric(19,4)",
                precision: 19,
                scale: 4,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FulfilledByMarketplace",
                schema: "catalog",
                table: "marketplace_variant_links");

            migrationBuilder.DropColumn(
                name: "LastSentAt",
                schema: "inventory",
                table: "channel_offers");

            migrationBuilder.DropColumn(
                name: "LastSentCode",
                schema: "inventory",
                table: "channel_offers");

            migrationBuilder.DropColumn(
                name: "LastSentListPrice",
                schema: "inventory",
                table: "channel_offers");

            migrationBuilder.DropColumn(
                name: "LastSentQuantity",
                schema: "inventory",
                table: "channel_offers");

            migrationBuilder.DropColumn(
                name: "LastSentSalePrice",
                schema: "inventory",
                table: "channel_offers");
        }
    }
}
