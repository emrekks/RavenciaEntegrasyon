using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddProductChangeFieldValues : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Field",
                schema: "catalog",
                table: "product_change_entries",
                type: "character varying(24)",
                maxLength: 24,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NewValue",
                schema: "catalog",
                table: "product_change_entries",
                type: "character varying(400)",
                maxLength: 400,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PreviousValue",
                schema: "catalog",
                table: "product_change_entries",
                type: "character varying(400)",
                maxLength: 400,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Field",
                schema: "catalog",
                table: "product_change_entries");

            migrationBuilder.DropColumn(
                name: "NewValue",
                schema: "catalog",
                table: "product_change_entries");

            migrationBuilder.DropColumn(
                name: "PreviousValue",
                schema: "catalog",
                table: "product_change_entries");
        }
    }
}
