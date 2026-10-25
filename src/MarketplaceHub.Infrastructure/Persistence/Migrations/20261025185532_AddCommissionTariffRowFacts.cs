using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCommissionTariffRowFacts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Brand",
                schema: "catalog",
                table: "commission_tariff_rows",
                type: "character varying(160)",
                maxLength: 160,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Category",
                schema: "catalog",
                table: "commission_tariff_rows",
                type: "character varying(160)",
                maxLength: 160,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "Stock",
                schema: "catalog",
                table: "commission_tariff_rows",
                type: "numeric(19,4)",
                precision: 19,
                scale: 4,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Brand",
                schema: "catalog",
                table: "commission_tariff_rows");

            migrationBuilder.DropColumn(
                name: "Category",
                schema: "catalog",
                table: "commission_tariff_rows");

            migrationBuilder.DropColumn(
                name: "Stock",
                schema: "catalog",
                table: "commission_tariff_rows");
        }
    }
}
