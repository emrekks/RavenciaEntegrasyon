using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceHub.Infrastructure.Persistence.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration(MigrationId)]
public sealed class OwnerAcceptsInvoiceLeftovers : Migration
{
    internal const string MigrationId = "20261109120000_OwnerAcceptsInvoiceLeftovers";

    // The owner's decision of 9 Nov 2026 on four leftovers that still showed as open problems on live:
    //
    // - Issue 01a15917-… INVOICE_ADJUSTMENT_REQUIRED (order 4486229624, invoice DAP2026000000015, open since 20 Oct):
    //   "başarılı say". The invoice it names (01a121b0-…) no longer exists in billing.invoices, so nothing else refers to it
    //   and nothing re-opens it; the issue is resolved with the owner's note.
    // - INVOICE_SUBMIT jobs of invoice 01a135af-… (order 4623308608, 13 Oct): 01a135af-3bb2-… in MANUAL_REVIEW with
    //   INVOICE_SUBMISSION_OUTCOME_UNKNOWN, and the retries 01a135f7-… and 01a135fe-… BLOCKED with EFATURAM_REQUEST_REJECTED:
    //   "kabul say". The invoice itself was issued by a later retry (job 01a1360a-…, attempt 4: DAP2026000000436, ETTN
    //   dc53da38-…, PDF stored), then cancelled and replaced by DAP2026000000437 (Completed). The three jobs are closed as
    //   SUCCEEDED with OWNER_ACCEPTED and the note; no invoice, attempt or document row is touched.
    //
    // Each row changes only while it still has the state seen on 9 Nov (status and code in the WHERE), so a second run, or a
    // row changed meanwhile, is a no-op. Each changed row is written to ops.audit_logs.
    internal const string Accept = """
        DO $$
        BEGIN
            DROP TABLE IF EXISTS pg_temp.owner_accepted_rows;
            CREATE TEMP TABLE owner_accepted_rows (tenant_id uuid, target_type text, target_id uuid, reason text) ON COMMIT DROP;

            WITH changed AS (
                UPDATE ops.operational_issues
                SET "Status" = 'Resolved',
                    "ResolvedAt" = now(),
                    "LastSeenAt" = now(),
                    "Summary" = "Summary" || ' — Sahip kararı 9 Kas 2026: başarılı sayıldı.'
                WHERE "Id" = '01a15917-ec34-7b89-b408-c86ca123c5cf'
                  AND "TenantId" = '0fcd3304-4bd4-4cfb-bbed-a2c197b9d5b1'
                  AND "Code" = 'INVOICE_ADJUSTMENT_REQUIRED'
                  AND "Status" = 'Open'
                RETURNING "TenantId", "Id")
            INSERT INTO owner_accepted_rows
            SELECT "TenantId", 'OperationalIssue', "Id", 'Sahip kararı 9 Kas 2026: DAP2026000000015 düzeltmesi başarılı sayıldı.' FROM changed;

            WITH changed AS (
                UPDATE integration.jobs
                SET "Status" = 'SUCCEEDED',
                    "LastErrorCode" = 'OWNER_ACCEPTED',
                    "LastErrorSummary" = 'Sahip kararı 9 Kas 2026: başarılı/kabul sayıldı. Bu gönderimin sonucu belirsiz kalmıştı; fatura 13 Eki 16:13''te sonraki denemeyle DAP2026000000436 olarak kesildi (sonra iptal edilip DAP2026000000437 ile değiştirildi).',
                    "Version" = "Version" + 1
                WHERE "Id" = '01a135af-3bb2-7e59-ba6d-1001ba31d196'
                  AND "TenantId" = '0fcd3304-4bd4-4cfb-bbed-a2c197b9d5b1'
                  AND "JobType" = 'INVOICE_SUBMIT'
                  AND "Status" = 'MANUAL_REVIEW'
                  AND "LastErrorCode" = 'INVOICE_SUBMISSION_OUTCOME_UNKNOWN'
                RETURNING "TenantId", "Id")
            INSERT INTO owner_accepted_rows
            SELECT "TenantId", 'IntegrationJob', "Id", 'Sahip kararı 9 Kas 2026: sonucu belirsiz fatura gönderimi başarılı/kabul sayıldı.' FROM changed;

            WITH changed AS (
                UPDATE integration.jobs
                SET "Status" = 'SUCCEEDED',
                    "LastErrorCode" = 'OWNER_ACCEPTED',
                    "LastErrorSummary" = 'Sahip kararı 9 Kas 2026: kabul sayıldı. E-Faturam bu denemeyi reddetmişti; fatura 13 Eki 16:13''te sonraki denemeyle DAP2026000000436 olarak kesildi (sonra iptal edilip DAP2026000000437 ile değiştirildi).',
                    "Version" = "Version" + 1
                WHERE "Id" IN ('01a135f7-e9b1-7d46-8f2c-199a43ae96ef', '01a135fe-75cc-76ea-8434-c54bdebd7ee7')
                  AND "TenantId" = '0fcd3304-4bd4-4cfb-bbed-a2c197b9d5b1'
                  AND "JobType" = 'INVOICE_SUBMIT'
                  AND "Status" = 'BLOCKED'
                  AND "LastErrorCode" = 'EFATURAM_REQUEST_REJECTED'
                RETURNING "TenantId", "Id")
            INSERT INTO owner_accepted_rows
            SELECT "TenantId", 'IntegrationJob', "Id", 'Sahip kararı 9 Kas 2026: E-Faturam reddi kabul sayıldı.' FROM changed;

            INSERT INTO ops.audit_logs ("TenantId", "ActorUserId", "Action", "TargetType", "TargetId", "Reason", "CorrelationId", "CreatedAt")
            SELECT r.tenant_id, NULL::uuid, 'OWNER_ACCEPTED', r.target_type, r.target_id::text, r.reason,
                   'migration:20261109120000_OwnerAcceptsInvoiceLeftovers', now()
            FROM owner_accepted_rows r;

            TRUNCATE owner_accepted_rows;
        END
        $$;
        """;

    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(Accept);

    // Not reversed: the owner decided. ops.audit_logs (CorrelationId 'migration:20261109120000_OwnerAcceptsInvoiceLeftovers')
    // lists each changed row.
    protected override void Down(MigrationBuilder migrationBuilder) { }
}
