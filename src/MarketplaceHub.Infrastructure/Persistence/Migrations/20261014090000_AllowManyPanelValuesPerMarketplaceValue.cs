using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace MarketplaceHub.Infrastructure.Persistence.Migrations;

/// <summary>
/// A marketplace value can now be fed by several panel values, so the uniqueness moves from the marketplace value alone
/// to the (panel value, marketplace value) pair. Existing rows are untouched.
/// </summary>
[DbContext(typeof(AppDbContext))]
[Migration("20261014090000_AllowManyPanelValuesPerMarketplaceValue")]
public sealed class AllowManyPanelValuesPerMarketplaceValue : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "UX_attribute_value_mappings_remote_scope",
            schema: "catalog",
            table: "attribute_value_mappings");

        migrationBuilder.CreateIndex(
            name: "UX_attribute_value_mappings_local_remote_scope",
            schema: "catalog",
            table: "attribute_value_mappings",
            columns: new[] { "TenantId", "ConnectionId", "ScopeExternalId", "LocalId", "ExternalId" },
            unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Keep one panel value per marketplace value again (the most recently verified one) before restoring the narrower index.
        migrationBuilder.Sql(
            """
            WITH ranked_mappings AS (
                SELECT "Id",
                       ROW_NUMBER() OVER (
                           PARTITION BY "TenantId", "ConnectionId", "ScopeExternalId", "ExternalId"
                           ORDER BY "VerifiedAt" DESC NULLS LAST, "Version" DESC, "Id"
                       ) AS duplicate_rank
                FROM catalog.attribute_value_mappings
            )
            DELETE FROM catalog.attribute_value_mappings AS mapping
            USING ranked_mappings
            WHERE mapping."Id" = ranked_mappings."Id"
              AND ranked_mappings.duplicate_rank > 1;
            """);

        migrationBuilder.DropIndex(
            name: "UX_attribute_value_mappings_local_remote_scope",
            schema: "catalog",
            table: "attribute_value_mappings");

        migrationBuilder.CreateIndex(
            name: "UX_attribute_value_mappings_remote_scope",
            schema: "catalog",
            table: "attribute_value_mappings",
            columns: new[] { "TenantId", "ConnectionId", "ScopeExternalId", "ExternalId" },
            unique: true);
    }
}
