using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddQuestionCustomerKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CustomerKey",
                schema: "sales",
                table: "marketplace_questions",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_questions_tenant_customer_created",
                schema: "sales",
                table: "marketplace_questions",
                columns: new[] { "TenantId", "CustomerKey", "CreatedAt" },
                filter: "\"CustomerKey\" IS NOT NULL");

            // The questions already imported were read before the customer's number was kept: each Trendyol store reads
            // its question history once more from the start, in its bounded pages, and the re-read fills the number in.
            // The state's version moves so a sync run under way cannot write its old cursor back over this. Only stores
            // that are synced and finished their import; the start stays within the year Trendyol's filter reads.
            migrationBuilder.Sql("""
                UPDATE integration.marketplace_question_sync_states s
                SET "HistoryImported" = false, "ProgressStatus" = 'INITIAL', "ImportedCount" = 0,
                    "HistoryStatusIndex" = 0, "HistoryKindIndex" = 0, "HistoryWindowIndex" = 0, "HistoryPageIndex" = 0,
                    "HistoryStartedAt" = GREATEST(s."HistoryStartedAt", now() - interval '360 days'),
                    "Version" = s."Version" + 1
                FROM integration.platform_connections c
                WHERE c."TenantId" = s."TenantId" AND c."Id" = s."ConnectionId" AND upper(c."PlatformCode") = 'TRENDYOL'
                  AND c."Status" IN ('ACTIVE', 'VERIFIED') AND s."HistoryImported"
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The history re-read is not undone: the sync state is only a cursor, and the import finishes either way.
            migrationBuilder.DropIndex(
                name: "IX_questions_tenant_customer_created",
                schema: "sales",
                table: "marketplace_questions");

            migrationBuilder.DropColumn(
                name: "CustomerKey",
                schema: "sales",
                table: "marketplace_questions");
        }
    }
}
