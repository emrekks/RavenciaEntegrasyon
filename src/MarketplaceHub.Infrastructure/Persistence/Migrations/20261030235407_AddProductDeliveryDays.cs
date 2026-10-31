using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddProductDeliveryDays : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "DeliveryDays",
                schema: "catalog",
                table: "products",
                type: "integer",
                nullable: true);

            // Teslimat Süresi starts at 1 day on every existing card. Nothing held a per-product delivery time before:
            // every listing profile's DeliveryTimeDays was empty on live (read 31 October 2026), and the imports keep none.
            migrationBuilder.Sql("""UPDATE catalog.products SET "DeliveryDays" = 1 WHERE "DeliveryDays" IS NULL;""");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DeliveryDays",
                schema: "catalog",
                table: "products");
        }
    }
}
