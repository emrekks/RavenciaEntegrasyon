using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddOrderBuyerType : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BuyerType",
                schema: "sales",
                table: "orders",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_orders_TenantId_BuyerType",
                schema: "sales",
                table: "orders",
                columns: new[] { "TenantId", "BuyerType" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_orders_TenantId_BuyerType",
                schema: "sales",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "BuyerType",
                schema: "sales",
                table: "orders");
        }
    }
}
