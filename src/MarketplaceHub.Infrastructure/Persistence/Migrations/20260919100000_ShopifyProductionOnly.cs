using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceHub.Infrastructure.Persistence.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20260919100000_ShopifyProductionOnly")]
public partial class ShopifyProductionOnly : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            UPDATE integration.platform_connections AS staged
            SET "Environment" = 'PRODUCTION', "Version" = staged."Version" + 1
            WHERE staged."PlatformCode" = 'SHOPIFY'
              AND staged."Environment" = 'STAGE'
              AND NOT EXISTS (
                  SELECT 1
                  FROM integration.platform_connections AS live
                  WHERE live."TenantId" = staged."TenantId"
                    AND live."PlatformCode" = 'SHOPIFY'
                    AND live."Environment" = 'PRODUCTION'
                    AND live."ExternalStoreId" = staged."ExternalStoreId"
                    AND live."Status" <> 'DELETED');

            UPDATE integration.platform_capabilities AS capability
            SET "Environment" = 'PRODUCTION', "Version" = capability."Version" + 1
            FROM integration.platform_connections AS connection
            WHERE capability."TenantId" = connection."TenantId"
              AND capability."ConnectionId" = connection."Id"
              AND connection."PlatformCode" = 'SHOPIFY'
              AND connection."Environment" = 'PRODUCTION'
              AND capability."Environment" = 'STAGE';
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Shopify has no stage environment; the normalization is intentionally irreversible.
    }
}
