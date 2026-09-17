using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceHub.Infrastructure.Persistence.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20260918110000_SplitPriceAndStockWritePolicies")]
public partial class SplitPriceAndStockWritePolicies : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DELETE FROM integration.connection_sync_policies AS legacy
            WHERE legacy."ResourceType" = 'PRICE_STOCK_WRITE'
              AND EXISTS (
                  SELECT 1
                  FROM integration.connection_sync_policies AS current
                  WHERE current."TenantId" = legacy."TenantId"
                    AND current."ConnectionId" = legacy."ConnectionId"
                    AND current."ResourceType" = 'PRICE_WRITE');

            UPDATE integration.connection_sync_policies
            SET "ResourceType" = 'PRICE_WRITE', "Version" = "Version" + 1
            WHERE "ResourceType" = 'PRICE_STOCK_WRITE';

            DELETE FROM integration.connection_sync_policies
            WHERE "ResourceType" = 'PRODUCT_WRITE';
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DELETE FROM integration.connection_sync_policies AS legacy
            WHERE legacy."ResourceType" = 'PRICE_STOCK_WRITE';

            UPDATE integration.connection_sync_policies
            SET "ResourceType" = 'PRICE_STOCK_WRITE', "Version" = "Version" + 1
            WHERE "ResourceType" = 'PRICE_WRITE';

            DELETE FROM integration.connection_sync_policies
            WHERE "ResourceType" = 'STOCK_WRITE';
            """);
    }
}
