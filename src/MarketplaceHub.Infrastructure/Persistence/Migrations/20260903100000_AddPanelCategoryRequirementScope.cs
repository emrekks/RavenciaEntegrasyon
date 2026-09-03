using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceHub.Infrastructure.Persistence.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20260903100000_AddPanelCategoryRequirementScope")]
public partial class AddPanelCategoryRequirementScope : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>(
            name: "IsPanelScoped",
            schema: "catalog",
            table: "category_attribute_requirements",
            type: "boolean",
            nullable: false,
            defaultValue: false);

        migrationBuilder.Sql("""
            UPDATE catalog.category_attribute_requirements AS requirement
            SET "IsPanelScoped" = requirement."Role" = 'OPTION'
                OR NOT EXISTS (
                    SELECT 1
                    FROM catalog.attribute_definitions AS attribute
                    WHERE attribute."TenantId" = requirement."TenantId"
                      AND attribute."Id" = requirement."AttributeId"
                      AND attribute."Code" LIKE 'TRD_%'
                );
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "IsPanelScoped",
            schema: "catalog",
            table: "category_attribute_requirements");
    }
}
