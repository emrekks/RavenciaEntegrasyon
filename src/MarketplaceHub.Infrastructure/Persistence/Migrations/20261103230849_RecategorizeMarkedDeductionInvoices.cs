using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RecategorizeMarkedDeductionInvoices : Migration
    {
        // Deduction invoices read through the PlatformServiceFee filter were all stored as SERVICE_FEE, cargo invoices and
        // advertising charges too (live, 3 Nov 2026), so the reports counted cargo twice. Their own name decides, as
        // TrendyolFinanceCategorizer.ByKeyword does; a later read stores the same category.
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE sales.marketplace_finance_transactions AS t
                SET "Category" = c.category
                FROM (
                    SELECT "Id",
                        CASE
                            WHEN lower(coalesce("TransactionType", '') || ' ' || coalesce("Description", '')) LIKE '%kargo%' THEN 'CARGO'
                            WHEN lower(coalesce("TransactionType", '') || ' ' || coalesce("Description", '')) ~ 'erken (ö|o)deme' THEN 'EARLY_PAYMENT'
                            WHEN lower(coalesce("TransactionType", '') || ' ' || coalesce("Description", '')) LIKE '%reklam%' THEN 'ADVERTISING'
                            WHEN lower(coalesce("TransactionType", '') || ' ' || coalesce("Description", '')) ~ 'ceza|gecikme' THEN 'PENALTY'
                            WHEN lower(coalesce("TransactionType", '') || ' ' || coalesce("Description", '')) LIKE '%stopaj%' THEN 'STOPPAGE'
                            WHEN lower(coalesce("TransactionType", '') || ' ' || coalesce("Description", '')) LIKE '%komisyon%' THEN 'COMMISSION'
                        END AS category
                    FROM sales.marketplace_finance_transactions
                    WHERE "PlatformCode" = 'TRENDYOL' AND "RequestedType" = 'DeductionInvoices' AND "TransactionSubType" = 'PlatformServiceFee' AND "Category" = 'SERVICE_FEE'
                ) AS c
                WHERE t."Id" = c."Id" AND c.category IS NOT NULL;
                """);
        }

        // The SERVICE_FEE category was wrong for these rows; it is not restored.
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
