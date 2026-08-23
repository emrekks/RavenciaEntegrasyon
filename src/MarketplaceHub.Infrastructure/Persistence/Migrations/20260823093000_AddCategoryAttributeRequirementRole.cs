using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCategoryAttributeRequirementRole : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Role",
                schema: "catalog",
                table: "category_attribute_requirements",
                type: "character varying(24)",
                maxLength: 24,
                nullable: false,
                defaultValue: "ATTRIBUTE");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Role",
                schema: "catalog",
                table: "category_attribute_requirements");
        }
    }
}
