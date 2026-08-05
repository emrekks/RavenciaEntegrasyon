using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceHub.Infrastructure.Persistence.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20260805183000_SanitizeEfaturamProviderManagedSettings")]
public partial class SanitizeEfaturamProviderManagedSettings : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            UPDATE integration.platform_connections
            SET "SettingsJson" = jsonb_build_object(
                    'ExternalWritesEnabled',
                    COALESCE(("SettingsJson"::jsonb ->> 'ExternalWritesEnabled')::boolean, false)
                )::text,
                "Version" = "Version" + 1
            WHERE "PlatformCode" = 'TRENDYOL_EFATURAM';
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Intentionally irreversible: removed fiscal account and carrier settings
        // must not be reconstructed after they have been scrubbed.
    }
}
