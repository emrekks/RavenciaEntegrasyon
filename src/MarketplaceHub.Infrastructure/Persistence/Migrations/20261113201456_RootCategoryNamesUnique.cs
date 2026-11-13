using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RootCategoryNamesUnique : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_categories_TenantId_ParentId_NormalizedName",
                schema: "catalog",
                table: "categories");

            migrationBuilder.CreateIndex(
                name: "IX_categories_TenantId_ParentId_NormalizedName",
                schema: "catalog",
                table: "categories",
                columns: new[] { "TenantId", "ParentId", "NormalizedName" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_categories_TenantId_ParentId_NormalizedName",
                schema: "catalog",
                table: "categories");

            migrationBuilder.CreateIndex(
                name: "IX_categories_TenantId_ParentId_NormalizedName",
                schema: "catalog",
                table: "categories",
                columns: new[] { "TenantId", "ParentId", "NormalizedName" },
                unique: true);
        }
    }
}
