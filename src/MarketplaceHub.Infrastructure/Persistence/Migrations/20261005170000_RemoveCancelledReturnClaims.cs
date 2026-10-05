using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace MarketplaceHub.Infrastructure.Persistence.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20261005170000_RemoveCancelledReturnClaims")]
public sealed class RemoveCancelledReturnClaims : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            DELETE FROM sales.return_stock_dispositions AS disposition
            USING sales.return_claims AS claim
            WHERE disposition."TenantId" = claim."TenantId"
              AND disposition."ClaimId" = claim."Id"
              AND claim."Status" = 'Cancelled';

            DELETE FROM sales.return_evidence AS evidence
            USING sales.return_claims AS claim
            WHERE evidence."TenantId" = claim."TenantId"
              AND evidence."ClaimId" = claim."Id"
              AND claim."Status" = 'Cancelled';

            DELETE FROM sales.return_decisions AS decision
            USING sales.return_claims AS claim
            WHERE decision."TenantId" = claim."TenantId"
              AND decision."ClaimId" = claim."Id"
              AND claim."Status" = 'Cancelled';

            DELETE FROM sales.return_lines AS line
            USING sales.return_claims AS claim
            WHERE line."TenantId" = claim."TenantId"
              AND line."ClaimId" = claim."Id"
              AND claim."Status" = 'Cancelled';

            DELETE FROM sales.return_claims
            WHERE "Status" = 'Cancelled';
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Deleted marketplace cancellation records cannot be reconstructed.
    }
}
