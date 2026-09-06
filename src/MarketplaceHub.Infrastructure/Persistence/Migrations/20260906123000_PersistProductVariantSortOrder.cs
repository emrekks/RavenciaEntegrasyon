using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceHub.Infrastructure.Persistence.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20260906123000_PersistProductVariantSortOrder")]
public partial class PersistProductVariantSortOrder : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_product_variants_TenantId_ProductId",
            schema: "catalog",
            table: "product_variants");

        migrationBuilder.AddColumn<int>(
            name: "SortOrder",
            schema: "catalog",
            table: "product_variants",
            type: "integer",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.CreateIndex(
            name: "IX_product_variants_TenantId_ProductId_SortOrder",
            schema: "catalog",
            table: "product_variants",
            columns: new[] { "TenantId", "ProductId", "SortOrder" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_product_variants_TenantId_ProductId_SortOrder",
            schema: "catalog",
            table: "product_variants");

        migrationBuilder.DropColumn(
            name: "SortOrder",
            schema: "catalog",
            table: "product_variants");

        migrationBuilder.CreateIndex(
            name: "IX_product_variants_TenantId_ProductId",
            schema: "catalog",
            table: "product_variants",
            columns: new[] { "TenantId", "ProductId" });
    }
}
