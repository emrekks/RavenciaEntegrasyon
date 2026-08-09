using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddOrderLineSourceSnapshot : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SourceSnapshotJson",
                schema: "sales",
                table: "order_lines",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SourceSnapshotJson",
                schema: "sales",
                table: "order_lines");
        }
    }
}
