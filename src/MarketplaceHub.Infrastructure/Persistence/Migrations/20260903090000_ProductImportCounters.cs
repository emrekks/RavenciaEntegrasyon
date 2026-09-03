using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceHub.Infrastructure.Persistence.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20260903090000_ProductImportCounters")]
public partial class ProductImportCounters : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(name: "ProgressReceived", schema: "integration", table: "jobs", nullable: false, defaultValue: 0);
        migrationBuilder.AddColumn<int>(name: "ProgressProcessed", schema: "integration", table: "jobs", nullable: false, defaultValue: 0);
        migrationBuilder.AddColumn<int>(name: "ProgressSkipped", schema: "integration", table: "jobs", nullable: false, defaultValue: 0);
        migrationBuilder.AddColumn<int>(name: "ProgressFailed", schema: "integration", table: "jobs", nullable: false, defaultValue: 0);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "ProgressReceived", schema: "integration", table: "jobs");
        migrationBuilder.DropColumn(name: "ProgressProcessed", schema: "integration", table: "jobs");
        migrationBuilder.DropColumn(name: "ProgressSkipped", schema: "integration", table: "jobs");
        migrationBuilder.DropColumn(name: "ProgressFailed", schema: "integration", table: "jobs");
    }
}
