using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMarketplaceVariantLinkCodes : Migration
    {
        // The marketplace's own barcode and stock code for a linked variant; filled by the next product import.
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ExternalBarcode",
                schema: "catalog",
                table: "marketplace_variant_links",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExternalSku",
                schema: "catalog",
                table: "marketplace_variant_links",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ExternalBarcode",
                schema: "catalog",
                table: "marketplace_variant_links");

            migrationBuilder.DropColumn(
                name: "ExternalSku",
                schema: "catalog",
                table: "marketplace_variant_links");
        }
    }
}
