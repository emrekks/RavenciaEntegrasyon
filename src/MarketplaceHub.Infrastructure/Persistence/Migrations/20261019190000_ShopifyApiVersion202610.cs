using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceHub.Infrastructure.Persistence.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20261019190000_ShopifyApiVersion202610")]
public sealed class ShopifyApiVersion202610 : Migration
{
    // Requests already go out with the adapter's version; the connection row only labels it, so the label follows
    // the move from 2026-07 to 2026-10 (the latest stable Admin API, nothing we use changed between them).
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql("""UPDATE integration.platform_connections SET "ApiVersion" = '2026-10' WHERE "PlatformCode" = 'SHOPIFY' AND "ApiVersion" = '2026-07';""");

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql("""UPDATE integration.platform_connections SET "ApiVersion" = '2026-07' WHERE "PlatformCode" = 'SHOPIFY' AND "ApiVersion" = '2026-10';""");
}
