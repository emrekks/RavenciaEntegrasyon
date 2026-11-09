using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceHub.Infrastructure.Persistence.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration(MigrationId)]
public sealed class CloseZeroPackageConflictIssue : Migration
{
    internal const string MigrationId = "20261109180000_CloseZeroPackageConflictIssue";

    // The owner's decision of 9 Nov 2026 ("Evet, kapat"): the issue 01a14a69-… ORDER_PACKAGE_ID_CONFLICT on package id '0'
    // (order-package-identity:01a05a69-…:0, 6 occurrences, last 6 Nov) stayed open after its cause was fixed by
    // 20261109171149_AllowTrendyolZeroPackagePlaceholders (the unique index and the conflict check skip '0'). It is resolved
    // with the owner's note, only while it is still open with that code, and written to ops.audit_logs; a second run is a no-op.
    internal const string Close = """
        WITH changed AS (
            UPDATE ops.operational_issues
            SET "Status" = 'Resolved',
                "ResolvedAt" = now(),
                "LastSeenAt" = now(),
                "Summary" = "Summary" || ' — Sahip kararı 9 Kas 2026: ''0'' paket kimliği artık çakışma sayılmıyor, kapatıldı.'
            WHERE "Id" = '01a14a69-f804-728c-a908-fbb302c95f3b'
              AND "TenantId" = '0fcd3304-4bd4-4cfb-bbed-a2c197b9d5b1'
              AND "Code" = 'ORDER_PACKAGE_ID_CONFLICT'
              AND "Status" = 'Open'
            RETURNING "TenantId", "Id")
        INSERT INTO ops.audit_logs ("TenantId", "ActorUserId", "Action", "TargetType", "TargetId", "Reason", "CorrelationId", "CreatedAt")
        SELECT "TenantId", NULL::uuid, 'OWNER_ACCEPTED', 'OperationalIssue', "Id"::text,
               'Sahip kararı 9 Kas 2026: ''0'' paket kimliği artık çakışma sayılmıyor, kapatıldı.',
               'migration:20261109180000_CloseZeroPackageConflictIssue', now()
        FROM changed;
        """;

    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(Close);

    // Not reversed: the owner decided. ops.audit_logs (CorrelationId 'migration:20261109180000_CloseZeroPackageConflictIssue')
    // records the change.
    protected override void Down(MigrationBuilder migrationBuilder) { }
}
