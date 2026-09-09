using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ScopeOperationalIssueDedupeByTenant : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_operational_issues_DedupeKey",
                schema: "ops",
                table: "operational_issues");

            migrationBuilder.CreateIndex(
                name: "IX_operational_issues_TenantId_DedupeKey",
                schema: "ops",
                table: "operational_issues",
                columns: new[] { "TenantId", "DedupeKey" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_operational_issues_TenantId_DedupeKey",
                schema: "ops",
                table: "operational_issues");

            migrationBuilder.CreateIndex(
                name: "IX_operational_issues_DedupeKey",
                schema: "ops",
                table: "operational_issues",
                column: "DedupeKey",
                unique: true);
        }
    }
}
