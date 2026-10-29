using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PromoteShopifyAcceptedVariantsToLive : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // A Shopify product is written and published in one call, so a SKU Shopify accepted is live; the publication
            // result still wrote it CREATE_ACCEPTED while its card read LIVE, and the product list counted none of those
            // variants (live 2026-10-29: the 24 MZ035YNC variants). Only SKUs of cards Shopify reports LIVE are moved.
            migrationBuilder.Sql("""
                UPDATE catalog.channel_listing_variants lv
                SET "ActualStatus" = 'LIVE', "RejectionCode" = NULL
                FROM catalog.channel_listing_profiles pr
                JOIN integration.platform_connections c ON c."TenantId" = pr."TenantId" AND c."Id" = pr."ConnectionId"
                WHERE lv."TenantId" = pr."TenantId" AND lv."ProfileId" = pr."Id"
                  AND upper(c."PlatformCode") = 'SHOPIFY'
                  AND pr."ActualStatus" = 'LIVE'
                  AND lv."ActualStatus" = 'CREATE_ACCEPTED';

                UPDATE catalog.marketplace_listing_states s
                SET "ActualStatus" = 'LIVE', "LastRejectionCode" = NULL, "Version" = s."Version" + 1
                FROM integration.platform_connections c
                WHERE c."TenantId" = s."TenantId" AND c."Id" = s."ConnectionId"
                  AND upper(c."PlatformCode") = 'SHOPIFY'
                  AND s."ActualStatus" = 'CREATE_ACCEPTED'
                  AND EXISTS (
                      SELECT 1
                      FROM catalog.channel_listing_variants lv
                      JOIN catalog.channel_listing_profiles pr ON pr."TenantId" = lv."TenantId" AND pr."Id" = lv."ProfileId"
                      WHERE lv."TenantId" = s."TenantId" AND lv."VariantId" = s."VariantId"
                        AND pr."ConnectionId" = s."ConnectionId" AND pr."ActualStatus" = 'LIVE' AND lv."ActualStatus" = 'LIVE');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The rows it moved cannot be told apart from SKUs a later status check confirmed live; nothing to undo.
        }
    }
}
