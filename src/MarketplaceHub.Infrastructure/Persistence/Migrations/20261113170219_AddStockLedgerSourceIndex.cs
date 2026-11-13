using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddStockLedgerSourceIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_stock_ledger_entries_TenantId_SourceType_SourceId",
                schema: "inventory",
                table: "stock_ledger_entries",
                columns: new[] { "TenantId", "SourceType", "SourceId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_stock_ledger_entries_TenantId_SourceType_SourceId",
                schema: "inventory",
                table: "stock_ledger_entries");
        }
    }
}
