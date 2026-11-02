using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceHub.Infrastructure.Persistence.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration(MigrationId)]
public sealed class CorrectTrendyolOrderTimes : Migration
{
    internal const string MigrationId = "20261102213000_CorrectTrendyolOrderTimes";

    // Trendyol's orderDate is a "GMT+3" epoch: the Turkey wall-clock time written as if it were UTC. Until this release it
    // was read as a real UTC epoch, so every Trendyol order time stood three hours ahead of the real instant (live 2 Nov
    // 2026: order 11776722829 arrived 19:01:53Z, stored as placed 22:01:41Z; 1,761 Trendyol orders, 118 of them stored as
    // placed after they arrived). The reading is fixed in TrendyolJsonMapper; the stored order times move back three hours
    // here. Only OrderedAt carried the shift: lastModifiedDate (LastRemoteModifiedAt, package and history times) and
    // agreedDeliveryDate (ShipmentDueAt, 23:59 Istanbul stored as 20:59Z) are real UTC epochs and stay.
    //
    // Guarded against a second run: EF writes this migration's history row only after Up, in the same transaction, so a
    // replay of this SQL once the migration is recorded changes nothing.
    internal const string Shift = """
        UPDATE sales.orders o
        SET "OrderedAt" = o."OrderedAt" - INTERVAL '3 hours',
            "Version" = o."Version" + 1
        FROM integration.platform_connections c
        WHERE c."Id" = o."ConnectionId" AND c."TenantId" = o."TenantId" AND c."PlatformCode" = 'TRENDYOL';
        """;

    internal const string Correction = $$"""
        DO $$
        BEGIN
            IF NOT EXISTS (SELECT 1 FROM public."__EFMigrationsHistory" WHERE "MigrationId" = '{{MigrationId}}') THEN
                {{Shift}}
            END IF;
        END
        $$;
        """;

    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(Correction);

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // The old values were wrong; there is nothing to restore.
    }
}
