using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ReturnClaimResolvedWithSource : Migration
    {
        // Where a claim's ResolvedWith came from when the owner set it (MANUAL: "Değişim Onaylandı Olarak İşaretle",
        // 18 Nov 2026). Additive only: a nullable column, no row is written; null keeps today's meaning (the marketplace
        // said it, or nothing is set). The previous code ignores it during the deploy window.
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ResolvedWithSource",
                schema: "sales",
                table: "return_claims",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ResolvedWithSource",
                schema: "sales",
                table: "return_claims");
        }
    }
}
