using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddReturnLineMarketplaceOutcome : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AcceptDetail",
                schema: "sales",
                table: "return_lines",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "AcceptedBySeller",
                schema: "sales",
                table: "return_lines",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MarketplaceNote",
                schema: "sales",
                table: "return_lines",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "Resolved",
                schema: "sales",
                table: "return_lines",
                type: "boolean",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AcceptDetail",
                schema: "sales",
                table: "return_lines");

            migrationBuilder.DropColumn(
                name: "AcceptedBySeller",
                schema: "sales",
                table: "return_lines");

            migrationBuilder.DropColumn(
                name: "MarketplaceNote",
                schema: "sales",
                table: "return_lines");

            migrationBuilder.DropColumn(
                name: "Resolved",
                schema: "sales",
                table: "return_lines");
        }
    }
}
