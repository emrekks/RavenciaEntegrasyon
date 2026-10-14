using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceHub.Infrastructure.Persistence.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20260917133000_AllowMultipleAttributeMappingsPerLocalField")]
public partial class AllowMultipleAttributeMappingsPerLocalField : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_attribute_mappings_TenantId_ConnectionId_LocalId_ScopeExter~",
            schema: "catalog",
            table: "attribute_mappings");

        migrationBuilder.CreateIndex(
            name: "UX_attribute_mappings_local_remote_scope",
            schema: "catalog",
            table: "attribute_mappings",
            columns: new[] { "TenantId", "ConnectionId", "LocalId", "ScopeExternalId", "ExternalId" },
            unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "UX_attribute_mappings_local_remote_scope",
            schema: "catalog",
            table: "attribute_mappings");

        migrationBuilder.CreateIndex(
            name: "IX_attribute_mappings_TenantId_ConnectionId_LocalId_ScopeExter~",
            schema: "catalog",
            table: "attribute_mappings",
            columns: new[] { "TenantId", "ConnectionId", "LocalId", "ScopeExternalId" },
            unique: true);
    }
}
