using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceHub.Infrastructure.Persistence.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20260902130000_ProductImportProgress")]
public partial class ProductImportProgress : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(name: "ProgressCurrent", schema: "integration", table: "jobs", nullable: false, defaultValue: 0);
        migrationBuilder.AddColumn<int>(name: "ProgressTotal", schema: "integration", table: "jobs", nullable: true);
        migrationBuilder.AddColumn<int>(name: "ProgressPercent", schema: "integration", table: "jobs", nullable: true);
        migrationBuilder.AddColumn<string>(name: "ProgressLabel", schema: "integration", table: "jobs", type: "character varying(256)", maxLength: 256, nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "ProgressCurrent", schema: "integration", table: "jobs");
        migrationBuilder.DropColumn(name: "ProgressTotal", schema: "integration", table: "jobs");
        migrationBuilder.DropColumn(name: "ProgressPercent", schema: "integration", table: "jobs");
        migrationBuilder.DropColumn(name: "ProgressLabel", schema: "integration", table: "jobs");
    }
}
