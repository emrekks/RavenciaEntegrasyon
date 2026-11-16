using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceHub.Infrastructure.Persistence.Migrations;

// 16 Nov 2026: the buyer-kind rule changed for Hepsiburada. Its 11111111111 identity no longer makes a buyer individual
// by itself; a valid VKN with a tax office makes it corporate, and unclear tax details wait for the owner
// (TrendyolEFaturamCanonicalPayload.HepsiburadaBuyerEvidence). As OrderBuyerTypes asks for a rule change, the stored kind
// is cleared so OrderBuyerTypes.BackfillAsync (the maintenance pass at worker startup and every hour) recomputes it by the
// new rule. Only Hepsiburada orders, only the one BuyerType column (no version bump, no snapshot or invoice touched);
// every other marketplace keeps its stored kind. Live 16 Nov 2026 (read-only): 37 Hepsiburada orders, all Individual; the
// two with a valid VKN and a tax office (4476971250, 4377390652) become Corporate, the other 35 stay Individual.
// Rerunning it only clears the same rows again, which the next backfill fills in the same way.
[DbContext(typeof(AppDbContext))]
[Migration("20261116180500_RecomputeHepsiburadaBuyerType")]
public sealed class RecomputeHepsiburadaBuyerType : Migration
{
    internal const string Clear = """
        UPDATE sales.orders AS o
        SET "BuyerType" = NULL
        FROM integration.platform_connections AS connection
        WHERE connection."TenantId" = o."TenantId"
          AND connection."Id" = o."ConnectionId"
          AND connection."PlatformCode" = 'HEPSIBURADA'
          AND o."BuyerType" IS NOT NULL
        """;

    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(Clear);

    // The cleared kinds are recomputed by the backfill either way; nothing to restore.
    protected override void Down(MigrationBuilder migrationBuilder)
    {
    }
}
