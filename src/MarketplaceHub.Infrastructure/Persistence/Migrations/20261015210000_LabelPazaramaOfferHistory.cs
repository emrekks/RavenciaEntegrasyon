using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceHub.Infrastructure.Persistence.Migrations
{
    // Offers that the Pazarama catalog mapping opened were recorded with the n11 label because the
    // seeding code did not know the platform. Only the history text changes.
    [DbContext(typeof(AppDbContext))]
    [Migration("20261015210000_LabelPazaramaOfferHistory")]
    public partial class LabelPazaramaOfferHistory : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder) =>
            migrationBuilder.Sql(@"UPDATE inventory.channel_price_history h
SET ""Reason"" = 'PAZARAMA_CATALOG_MAPPING', ""ActorSource"" = 'SYSTEM:PAZARAMA'
FROM inventory.channel_offers o
JOIN integration.platform_connections c ON c.""Id"" = o.""ConnectionId""
WHERE h.""OfferId"" = o.""Id"" AND c.""PlatformCode"" = 'PAZARAMA' AND h.""Reason"" = 'N11_CATALOG_MAPPING';");

        protected override void Down(MigrationBuilder migrationBuilder) =>
            migrationBuilder.Sql(@"UPDATE inventory.channel_price_history h
SET ""Reason"" = 'N11_CATALOG_MAPPING', ""ActorSource"" = 'SYSTEM:N11'
FROM inventory.channel_offers o
JOIN integration.platform_connections c ON c.""Id"" = o.""ConnectionId""
WHERE h.""OfferId"" = o.""Id"" AND c.""PlatformCode"" = 'PAZARAMA' AND h.""Reason"" = 'PAZARAMA_CATALOG_MAPPING';");
    }
}
