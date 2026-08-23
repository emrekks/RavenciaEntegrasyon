using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(AppDbContext))]
    [Migration("20260823093000_AddCategoryAttributeRequirementRole")]
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
