using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPttAvmManualWritePolicy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // PTT AVM's "Manuel Dış Yazmalar" switch (MANUAL_WRITE) gates the price-stock and product sends a user starts.
            // Before it existed those sends were gated by the master write switch alone, so every PTT AVM connection
            // gets the switch ON and nothing changes until the user turns it off. A missing row refuses the send, so the
            // row is written here rather than waiting for the scheduler. Existing rows are left as they are.
            migrationBuilder.Sql("""
                INSERT INTO integration.connection_sync_policies
                    ("Id", "TenantId", "ConnectionId", "ResourceType", "IntervalSeconds", "OverlapSeconds", "JitterSeconds", "Enabled", "UserDefined", "Version")
                SELECT gen_random_uuid(), c."TenantId", c."Id", 'MANUAL_WRITE', 0, 0, 0, true, false, 1
                FROM integration.platform_connections c
                WHERE upper(c."PlatformCode") = 'PTTAVM' AND c."Status" <> 'DELETED'
                ON CONFLICT ("TenantId", "ConnectionId", "ResourceType") DO NOTHING;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DELETE FROM integration.connection_sync_policies
                WHERE "ResourceType" = 'MANUAL_WRITE';
                """);
        }
    }
}
