using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceHub.Infrastructure.Persistence.Migrations
{
    // The listing profiles a product sync made for matched products (Source MARKETPLACE_IMPORT, shipped 6 Nov 2026) were
    // made without listing states, and the update job requires one per listing (live 7 Nov 2026: RYP003T's Trendyol update
    // stopped as PRODUCT_UPDATE_STATE_INCOMPLETE). This one-time pass gives each of their listings the state a sync now
    // writes with it (ImportedListingProfiles): DesiredStatus LIVE, ActualStatus UNKNOWN, no payload hash. UNKNOWN is never
    // read as live, so the variant's price and stock keep going by its link. Only imported profiles' listings without a
    // state on that connection are touched (NOT EXISTS), so the pass is idempotent and leaves every other state alone.
    /// <inheritdoc />
    public partial class AddImportedListingStates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                INSERT INTO catalog.marketplace_listing_states ("Id", "TenantId", "ConnectionId", "VariantId", "DesiredStatus", "ActualStatus", "LastRejectionCode", "PayloadHash", "Version")
                SELECT gen_random_uuid(), l."TenantId", p."ConnectionId", l."VariantId", 'LIVE', 'UNKNOWN', NULL, NULL, 1
                FROM catalog.channel_listing_variants l
                JOIN catalog.channel_listing_profiles p ON p."TenantId" = l."TenantId" AND p."Id" = l."ProfileId"
                WHERE p."Source" = 'MARKETPLACE_IMPORT'
                  AND NOT EXISTS (
                      SELECT 1 FROM catalog.marketplace_listing_states s
                      WHERE s."TenantId" = l."TenantId" AND s."ConnectionId" = p."ConnectionId" AND s."VariantId" = l."VariantId")
                ON CONFLICT ("TenantId", "ConnectionId", "VariantId") DO NOTHING;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The states stay: a later update or status read may have written to them, and the sync makes the same rows.
        }
    }
}
