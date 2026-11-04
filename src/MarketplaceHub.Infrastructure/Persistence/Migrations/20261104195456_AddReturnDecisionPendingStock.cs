using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddReturnDecisionPendingStock : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "StockLinesJson",
                schema: "sales",
                table: "return_decisions",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "StockSettledAt",
                schema: "sales",
                table: "return_decisions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StockStatus",
                schema: "sales",
                table: "return_decisions",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "StockLinesJson",
                schema: "sales",
                table: "return_decisions");

            migrationBuilder.DropColumn(
                name: "StockSettledAt",
                schema: "sales",
                table: "return_decisions");

            migrationBuilder.DropColumn(
                name: "StockStatus",
                schema: "sales",
                table: "return_decisions");
        }
    }
}
