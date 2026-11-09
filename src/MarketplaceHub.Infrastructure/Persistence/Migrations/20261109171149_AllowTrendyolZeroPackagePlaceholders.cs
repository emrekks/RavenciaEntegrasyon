using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AllowTrendyolZeroPackagePlaceholders : Migration
    {
        /// <inheritdoc />
        // Schema only: Trendyol gives every order awaiting payment the package number "0" until its real package
        // exists, so several orders of one store hold it at once. Every other number stays unique per store.
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_shipment_packages_TenantId_ConnectionId_ExternalPackageId",
                schema: "sales",
                table: "shipment_packages");

            migrationBuilder.CreateIndex(
                name: "IX_shipment_packages_TenantId_ConnectionId_ExternalPackageId",
                schema: "sales",
                table: "shipment_packages",
                columns: new[] { "TenantId", "ConnectionId", "ExternalPackageId" },
                unique: true,
                filter: "\"ExternalPackageId\" <> '0'");
        }

        /// <inheritdoc />
        // Rolling back fails while two orders of one store hold "0"; those rows would have to be closed by hand first.
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_shipment_packages_TenantId_ConnectionId_ExternalPackageId",
                schema: "sales",
                table: "shipment_packages");

            migrationBuilder.CreateIndex(
                name: "IX_shipment_packages_TenantId_ConnectionId_ExternalPackageId",
                schema: "sales",
                table: "shipment_packages",
                columns: new[] { "TenantId", "ConnectionId", "ExternalPackageId" },
                unique: true);
        }
    }
}
