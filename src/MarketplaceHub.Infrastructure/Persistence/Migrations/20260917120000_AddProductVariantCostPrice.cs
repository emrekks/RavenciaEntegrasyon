using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceHub.Infrastructure.Persistence.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20260917120000_AddProductVariantCostPrice")]
public partial class AddProductVariantCostPrice : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<decimal>(
            name: "CostPrice",
            schema: "catalog",
            table: "product_variants",
            type: "numeric(19,4)",
            precision: 19,
            scale: 4,
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "CostPrice",
            schema: "catalog",
            table: "product_variants");
    }
}
