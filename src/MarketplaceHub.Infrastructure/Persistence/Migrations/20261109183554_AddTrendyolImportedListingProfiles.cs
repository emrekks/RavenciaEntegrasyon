using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceHub.Infrastructure.Persistence.Migrations
{
    // Imported listing profiles (ImportedListingProfiles, shipped 6 Nov 2026) were made only for the products a product
    // sync happened to read afterwards, so 914 of the 936 Trendyol-linked products had none and "Platformlarda Güncelle"
    // refused them with "yayın kaydı yok" (live 9 Nov 2026: SLR007VCD, seven colour cards). The nightly verification scan
    // now completes them; this one-time pass gives the Trendyol products the rows that scan would give, from what the panel
    // already holds: a product with a linked, non-archived variant whose link a marketplace read saw (LastSeenAt set) and
    // did not judge missing (HealthStatus null) gets the empty MARKETPLACE_IMPORT profile (Enabled, LIVE wanted, UNKNOWN,
    // no title, category, brand or other marketplace value), a listing variant per linked non-archived variant with the
    // link's barcode and stock code, and an UNKNOWN listing state where it has none. UNKNOWN is never read as live, so price
    // and stock keep going by the link. A product with a panel-made profile is left alone; every insert is guarded by NOT
    // EXISTS and ON CONFLICT, so the pass is idempotent. Nothing is sent to a marketplace.
    /// <inheritdoc />
    public partial class AddTrendyolImportedListingProfiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                INSERT INTO catalog.channel_listing_profiles ("Id", "TenantId", "ConnectionId", "ProductId", "Enabled", "DesiredStatus", "ActualStatus", "Version", "Source")
                SELECT gen_random_uuid(), seen."TenantId", seen."ConnectionId", seen."ProductId", TRUE, 'LIVE', 'UNKNOWN', 1, 'MARKETPLACE_IMPORT'
                FROM (
                    SELECT DISTINCT l."TenantId", l."ConnectionId", v."ProductId"
                    FROM catalog.marketplace_variant_links l
                    JOIN catalog.product_variants v ON v."TenantId" = l."TenantId" AND v."Id" = l."VariantId"
                    JOIN integration.platform_connections c ON c."TenantId" = l."TenantId" AND c."Id" = l."ConnectionId"
                    WHERE c."PlatformCode" = 'TRENDYOL' AND v."Status" <> 'Archived'
                      AND l."LastSeenAt" IS NOT NULL AND l."HealthStatus" IS NULL
                ) seen
                WHERE NOT EXISTS (
                    SELECT 1 FROM catalog.channel_listing_profiles p
                    WHERE p."TenantId" = seen."TenantId" AND p."ConnectionId" = seen."ConnectionId" AND p."ProductId" = seen."ProductId")
                ON CONFLICT ("TenantId", "ConnectionId", "ProductId") DO NOTHING;

                INSERT INTO catalog.channel_listing_variants ("Id", "TenantId", "ProfileId", "VariantId", "ExternalSku", "ExternalBarcode", "DesiredStatus", "ActualStatus")
                SELECT gen_random_uuid(), linked."TenantId", linked."ProfileId", linked."VariantId", linked."ExternalSku", linked."ExternalBarcode", 'LIVE', 'UNKNOWN'
                FROM (
                    SELECT DISTINCT ON (p."Id", v."Id") l."TenantId", p."Id" AS "ProfileId", v."Id" AS "VariantId",
                           NULLIF(btrim(l."ExternalSku"), '') AS "ExternalSku", NULLIF(btrim(l."ExternalBarcode"), '') AS "ExternalBarcode"
                    FROM catalog.channel_listing_profiles p
                    JOIN integration.platform_connections c ON c."TenantId" = p."TenantId" AND c."Id" = p."ConnectionId"
                    JOIN catalog.product_variants v ON v."TenantId" = p."TenantId" AND v."ProductId" = p."ProductId"
                    JOIN catalog.marketplace_variant_links l ON l."TenantId" = p."TenantId" AND l."ConnectionId" = p."ConnectionId" AND l."VariantId" = v."Id"
                    WHERE c."PlatformCode" = 'TRENDYOL' AND p."Source" = 'MARKETPLACE_IMPORT' AND v."Status" <> 'Archived'
                    ORDER BY p."Id", v."Id", l."Id"
                ) linked
                WHERE NOT EXISTS (
                    SELECT 1 FROM catalog.channel_listing_variants x
                    WHERE x."TenantId" = linked."TenantId" AND x."ProfileId" = linked."ProfileId" AND x."VariantId" = linked."VariantId")
                ON CONFLICT ("TenantId", "ProfileId", "VariantId") DO NOTHING;

                INSERT INTO catalog.marketplace_listing_states ("Id", "TenantId", "ConnectionId", "VariantId", "DesiredStatus", "ActualStatus", "LastRejectionCode", "PayloadHash", "Version")
                SELECT gen_random_uuid(), listed."TenantId", listed."ConnectionId", listed."VariantId", 'LIVE', 'UNKNOWN', NULL, NULL, 1
                FROM (
                    SELECT DISTINCT x."TenantId", p."ConnectionId", x."VariantId"
                    FROM catalog.channel_listing_variants x
                    JOIN catalog.channel_listing_profiles p ON p."TenantId" = x."TenantId" AND p."Id" = x."ProfileId"
                    JOIN integration.platform_connections c ON c."TenantId" = p."TenantId" AND c."Id" = p."ConnectionId"
                    WHERE c."PlatformCode" = 'TRENDYOL' AND p."Source" = 'MARKETPLACE_IMPORT'
                ) listed
                WHERE NOT EXISTS (
                    SELECT 1 FROM catalog.marketplace_listing_states s
                    WHERE s."TenantId" = listed."TenantId" AND s."ConnectionId" = listed."ConnectionId" AND s."VariantId" = listed."VariantId")
                ON CONFLICT ("TenantId", "ConnectionId", "VariantId") DO NOTHING;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The rows stay: a later update or status read may have written to them, and the nightly scan makes the same rows.
        }
    }
}
