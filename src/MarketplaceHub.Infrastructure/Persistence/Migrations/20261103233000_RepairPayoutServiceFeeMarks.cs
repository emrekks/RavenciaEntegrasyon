using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceHub.Infrastructure.Persistence.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration(MigrationId)]
public sealed class RepairPayoutServiceFeeMarks : Migration
{
    internal const string MigrationId = "20261103233000_RepairPayoutServiceFeeMarks";

    // The first payout read (AddMarketplacePayouts, 3 Nov 2026 21:58Z) asked Trendyol for each payout's deduction invoices
    // with the PlatformServiceFee filter too; asked by payout id, Trendyol ignores the filter and answers every deduction
    // invoice of the payout, so cargo, advertising and other invoices were marked as platform service fees (live: 95
    // "Kargo Fatura" 168,487.89 TL, 12 "Reklam Bedeli"...). The report then found almost no cargo invoice, estimated the
    // cargo again and the profit margin fell from 21.1% to 12.3%. The read no longer sends the filter by payout id; here the
    // wrong marks go: a deduction invoice marked so whose own name is not "Platform Hizmet Bedeli" loses the mark and takes
    // the category TrendyolFinanceCategorizer.ByKeyword gives its name (kargo, erken ödeme, reklam, ceza/gecikme, stopaj,
    // komisyon, hizmet/platform; none of them: another deduction). The marks Trendyol gave through the filter by date
    // (every "Platform Hizmet Bedeli" and "AZ-Platform Hizmet Bedeli") stay. Running it again changes nothing.
    internal const string Repair = """
        UPDATE sales.marketplace_finance_transactions
        SET "TransactionSubType" = NULL,
            "Category" = CASE
                WHEN coalesce("TransactionType", '') || ' ' || coalesce("Description", '') ILIKE '%kargo%' THEN 'CARGO'
                WHEN coalesce("TransactionType", '') || ' ' || coalesce("Description", '') ILIKE '%erken ödeme%' THEN 'EARLY_PAYMENT'
                WHEN coalesce("TransactionType", '') || ' ' || coalesce("Description", '') ILIKE '%reklam%' THEN 'ADVERTISING'
                WHEN coalesce("TransactionType", '') || ' ' || coalesce("Description", '') ILIKE ANY (ARRAY['%ceza%', '%gecikme%']) THEN 'PENALTY'
                WHEN coalesce("TransactionType", '') || ' ' || coalesce("Description", '') ILIKE '%stopaj%' THEN 'STOPPAGE'
                WHEN coalesce("TransactionType", '') || ' ' || coalesce("Description", '') ILIKE '%komisyon%' THEN 'COMMISSION'
                WHEN coalesce("TransactionType", '') || ' ' || coalesce("Description", '') ILIKE ANY (ARRAY['%hizmet%', '%platform%']) THEN 'SERVICE_FEE'
                ELSE 'OTHER_DEDUCTION'
            END
        WHERE "PlatformCode" = 'TRENDYOL' AND "Source" = 'OTHER_FINANCIALS' AND "RequestedType" = 'DeductionInvoices'
          AND "TransactionSubType" = 'PlatformServiceFee' AND coalesce("TransactionType", '') NOT ILIKE '%Platform Hizmet Bedeli%';
        """;

    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(Repair);

    // The wrong marks are not worth restoring.
    protected override void Down(MigrationBuilder migrationBuilder) { }
}
