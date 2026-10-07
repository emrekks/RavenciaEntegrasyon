using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddInvoiceWorkspaceKeysetIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "CREATE INDEX CONCURRENTLY \"IX_shipment_packages_TenantId_StatusOccurredAt_Id\" ON sales.shipment_packages (\"TenantId\", \"StatusOccurredAt\", \"Id\")",
                suppressTransaction: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "DROP INDEX CONCURRENTLY IF EXISTS sales.\"IX_shipment_packages_TenantId_StatusOccurredAt_Id\"",
                suppressTransaction: true);
        }
    }
}
