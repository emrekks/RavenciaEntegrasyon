using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddUniqueActiveProductMediaHash : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "UX_file_assets_active_product_media_sha256",
                schema: "ops",
                table: "file_assets",
                columns: new[] { "TenantId", "Sha256" },
                unique: true,
                filter: "\"Classification\" = 'PRODUCT_MEDIA' AND \"ArchivedAt\" IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_file_assets_active_product_media_sha256",
                schema: "ops",
                table: "file_assets");
        }
    }
}
