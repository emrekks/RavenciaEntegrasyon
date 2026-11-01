using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddQuestionProductExternalIdAndSubject : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ProductExternalId",
                schema: "sales",
                table: "marketplace_questions",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "QuestionSubject",
                schema: "sales",
                table: "marketplace_questions",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ProductExternalId",
                schema: "sales",
                table: "marketplace_questions");

            migrationBuilder.DropColumn(
                name: "QuestionSubject",
                schema: "sales",
                table: "marketplace_questions");
        }
    }
}
