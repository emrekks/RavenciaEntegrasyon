using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddVariantLinkLookup : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastLookupAt",
                schema: "catalog",
                table: "marketplace_variant_links",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastLookupOutcome",
                schema: "catalog",
                table: "marketplace_variant_links",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastLookupAt",
                schema: "catalog",
                table: "marketplace_variant_links");

            migrationBuilder.DropColumn(
                name: "LastLookupOutcome",
                schema: "catalog",
                table: "marketplace_variant_links");
        }
    }
}
