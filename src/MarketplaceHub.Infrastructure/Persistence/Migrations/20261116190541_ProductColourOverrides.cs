using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceHub.Infrastructure.Persistence.Migrations
{
    // KATALOG-14 phase D1 (expand only): a colour card's own name and description (products.TitleOverride,
    // DescriptionOverride), nullable and empty. Adding a nullable column without a default is a catalog-only change in
    // PostgreSQL (no rewrite, a brief ACCESS EXCLUSIVE lock); the running image neither reads nor writes them. Nothing is
    // backfilled here: phase D2 fills them where a colour differs from its model.
    /// <inheritdoc />
    public partial class ProductColourOverrides : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DescriptionOverride",
                schema: "catalog",
                table: "products",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TitleOverride",
                schema: "catalog",
                table: "products",
                type: "character varying(320)",
                maxLength: 320,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DescriptionOverride",
                schema: "catalog",
                table: "products");

            migrationBuilder.DropColumn(
                name: "TitleOverride",
                schema: "catalog",
                table: "products");
        }
    }
}
