using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceHub.Infrastructure.Persistence.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20261109200000_NotifyCatalogRequirementChanges")]
public sealed class NotifyCatalogRequirementChanges : Migration
{
    // Every table GET /catalog/categories/{id}/attribute-requirements reads from. Statement triggers, not row
    // triggers: a sync that writes thousands of reference rows raises one notification per statement, and identical
    // notifications inside one transaction are delivered once by PostgreSQL.
    // The reference table the syncs write most comes first, so its lock is not awaited while the others are held.
    private static readonly string[] Tables =
    [
        "integration.reference_items",
        "catalog.categories",
        "catalog.category_attribute_requirements",
        "catalog.attribute_definitions",
        "catalog.attribute_values",
        "catalog.attribute_mappings",
        "catalog.category_mappings",
    ];

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // The API LISTENs on this channel and clears its requirements cache (CategoryRequirementsChangeListener).
        migrationBuilder.Sql($$"""
            CREATE OR REPLACE FUNCTION catalog.notify_requirements_changed() RETURNS trigger
            LANGUAGE plpgsql AS $function$
            BEGIN
                PERFORM pg_notify('{{CatalogRequirementNotifications.Channel}}', '');
                RETURN NULL;
            END
            $function$;
            """);
        foreach (var table in Tables)
            migrationBuilder.Sql($"""
                CREATE TRIGGER trg_notify_requirements_changed
                    AFTER INSERT OR UPDATE OR DELETE OR TRUNCATE ON {table}
                    FOR EACH STATEMENT EXECUTE FUNCTION catalog.notify_requirements_changed();
                """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        foreach (var table in Tables)
            migrationBuilder.Sql($"DROP TRIGGER IF EXISTS trg_notify_requirements_changed ON {table};");
        migrationBuilder.Sql("DROP FUNCTION IF EXISTS catalog.notify_requirements_changed();");
    }
}
