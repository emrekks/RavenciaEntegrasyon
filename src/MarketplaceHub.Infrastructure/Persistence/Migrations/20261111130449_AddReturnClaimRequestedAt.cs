using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddReturnClaimRequestedAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "RequestedAt",
                schema: "sales",
                table: "return_claims",
                type: "timestamp with time zone",
                nullable: true);

            // The claims read from now on bring their request date (Trendyol and Hepsiburada claimDate, n11 requestDate,
            // Pazarama refundDate, Shopify createdAt); the return reconciliation re-reads the last 90 days. A Trendyol
            // order the panel built from a claim (its order package was past the order history) keeps that claim as its
            // snapshot, and that claim's own claimDate (GMT epoch milliseconds) dates the claim now: only the claim the
            // snapshot names, only a numeric date, only an empty RequestedAt. Every other claim stays empty (unknown).
            migrationBuilder.Sql("""
                UPDATE sales.return_claims c
                SET "RequestedAt" = to_timestamp((s.snapshot ->> 'claimDate')::numeric / 1000)
                FROM (
                    SELECT o."TenantId", o."Id",
                           CASE WHEN o."CustomerSnapshotJson" LIKE '%"claimDate"%' THEN o."CustomerSnapshotJson"::jsonb END AS snapshot
                    FROM sales.orders o
                    JOIN integration.platform_connections p ON p."TenantId" = o."TenantId" AND p."Id" = o."ConnectionId"
                    WHERE p."PlatformCode" = 'TRENDYOL' AND o."CustomerSnapshotJson" LIKE '%"claimDate"%'
                ) s
                WHERE c."TenantId" = s."TenantId" AND c."OrderId" = s."Id"
                  AND c."RequestedAt" IS NULL
                  AND jsonb_typeof(s.snapshot -> 'claimDate') = 'number'
                  AND s.snapshot ->> 'claimId' = c."ExternalClaimId";
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RequestedAt",
                schema: "sales",
                table: "return_claims");
        }
    }
}
