using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MarketplaceDeliveryInvoiceLink : Migration
    {
        // Owner decision 17 Nov 2026 (MarketplaceInvoiceLinkPolicy): each marketplace delivery keeps the link it sent, so a
        // resend or the Hepsiburada exchange re-send carries the same link whichever kind it was. Expand only: a nullable
        // column without default (no rewrite, catalog-only change); the running image neither reads nor writes it, and
        // existing rows stay null (their link is read the old way). No backfill.
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "InvoiceLink",
                schema: "billing",
                table: "marketplace_delivery_states",
                type: "character varying(4096)",
                maxLength: 4096,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "InvoiceLink",
                schema: "billing",
                table: "marketplace_delivery_states");
        }
    }
}
