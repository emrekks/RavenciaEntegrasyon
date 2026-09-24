using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace MarketplaceHub.Infrastructure.Persistence.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20260924150000_VariantDefaultPrices")]
public sealed class VariantDefaultPrices : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<decimal>(
            name: "DefaultListPrice",
            schema: "catalog",
            table: "product_variants",
            type: "numeric(19,4)",
            precision: 19,
            scale: 4,
            nullable: true);

        migrationBuilder.AddColumn<decimal>(
            name: "DefaultSalePrice",
            schema: "catalog",
            table: "product_variants",
            type: "numeric(19,4)",
            precision: 19,
            scale: 4,
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "DefaultListPrice", schema: "catalog", table: "product_variants");
        migrationBuilder.DropColumn(name: "DefaultSalePrice", schema: "catalog", table: "product_variants");
    }
}
