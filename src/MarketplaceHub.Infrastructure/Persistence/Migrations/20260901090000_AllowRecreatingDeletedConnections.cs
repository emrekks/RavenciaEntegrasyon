using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceHub.Infrastructure.Persistence.Migrations;

public partial class AllowRecreatingDeletedConnections : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_platform_connections_TenantId_PlatformCode_Environment_ExternalStoreId",
            schema: "integration",
            table: "platform_connections");

        migrationBuilder.CreateIndex(
            name: "IX_platform_connections_TenantId_PlatformCode_Environment_ExternalStoreId",
            schema: "integration",
            table: "platform_connections",
            columns: new[] { "TenantId", "PlatformCode", "Environment", "ExternalStoreId" },
            unique: true,
            filter: "\"Status\" <> 'DELETED'");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_platform_connections_TenantId_PlatformCode_Environment_ExternalStoreId",
            schema: "integration",
            table: "platform_connections");

        migrationBuilder.CreateIndex(
            name: "IX_platform_connections_TenantId_PlatformCode_Environment_ExternalStoreId",
            schema: "integration",
            table: "platform_connections",
            columns: new[] { "TenantId", "PlatformCode", "Environment", "ExternalStoreId" },
            unique: true);
    }
}
