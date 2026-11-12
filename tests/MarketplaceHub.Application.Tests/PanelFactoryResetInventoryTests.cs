using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// Paneli Tamamen Sıfırla: every table the application has is classified, and the delete order the reset builds from
// the foreign keys runs the groups in the order the panel lists them.
public sealed class PanelFactoryResetInventoryTests
{
    // The tables the migrations create with SQL, outside the EF model.
    private static readonly string[] RawTables = ["integration.connection_backoffs", "integration.rate_limit_buckets", "integration.rate_limit_usage", "public.__EFMigrationsHistory"];

    private static IModel Model()
    {
        using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql("Host=localhost;Database=unused").Options);
        return db.Model;
    }

    private static string? TableOf(IEntityType entity) => entity.GetTableName() is { } name ? $"{entity.GetSchema() ?? "public"}.{name}" : null;

    private static (List<string> Tables, HashSet<string> TenantColumns, List<(string, string)> Keys) Schema()
    {
        var model = Model();
        var entities = model.GetEntityTypes().Where(x => TableOf(x) is not null).ToList();
        var tables = entities.Select(x => TableOf(x)!).Concat(RawTables).Distinct(StringComparer.Ordinal).ToList();
        var tenant = entities.Where(x => x.FindProperty("TenantId") is not null).Select(x => TableOf(x)!).ToHashSet(StringComparer.Ordinal);
        var keys = entities.SelectMany(x => x.GetForeignKeys().Select(fk => (Child: TableOf(x)!, Parent: TableOf(fk.PrincipalEntityType))))
            .Where(x => x.Parent is not null).Select(x => (x.Child, x.Parent!)).Distinct().ToList();
        return (tables, tenant, keys);
    }

    [Fact]
    public void EveryTable_IsClassifiedOnce_AndEveryListedTableExists()
    {
        var (tables, _, _) = Schema();
        Assert.Equal([], tables.Where(x => PanelFactoryResetInventory.Find(x) is null).ToList());
        Assert.Empty(PanelFactoryResetInventory.Tables.GroupBy(x => x.Name).Where(x => x.Count() > 1).Select(x => x.Key));
        Assert.Empty(PanelFactoryResetInventory.Tables.Select(x => x.Name).Except(tables));
        Assert.All(PanelFactoryResetInventory.Tables.Where(x => x.Action != PanelResetAction.Keep), x => Assert.NotNull(x.Group));
    }

    [Fact]
    public void ThePlan_DeletesChildrenFirst_GroupByGroup_AndNoKeptTablePointsAtADeletedOne()
    {
        var (tables, tenant, keys) = Schema();
        var plan = PanelFactoryResetService.BuildPlan(tables, tenant, keys);
        Assert.Null(plan.Problem);
        var order = plan.Order.Select(x => x.Name).ToList();
        Assert.Equal(PanelFactoryResetInventory.Tables.Count(x => x.Action == PanelResetAction.Delete), order.Count);
        foreach (var (child, parent) in keys.Where(x => x.Item1 != x.Item2 && order.Contains(x.Item1) && order.Contains(x.Item2)))
            Assert.True(order.IndexOf(child) < order.IndexOf(parent), $"{child} must go before {parent}");
        Assert.Equal(plan.Order.Select(x => x.Group!.Rank).Order(), plan.Order.Select(x => x.Group!.Rank));
        Assert.Contains("catalog.categories", plan.SelfReferencing);
        Assert.Contains("billing.invoices", plan.SelfReferencing);
    }

    [Fact]
    public void ThePlan_StopsOnAnUnknownTable_AndOnAKeptTablePointingAtADeletedOne()
    {
        var (tables, tenant, keys) = Schema();
        Assert.Contains("ops.new_log", PanelFactoryResetService.BuildPlan([.. tables, "ops.new_log"], tenant, keys).Problem);
        Assert.Contains("iam.tenant_settings → sales.orders", PanelFactoryResetService.BuildPlan(tables, tenant, [.. keys, ("iam.tenant_settings", "sales.orders")]).Problem);
        Assert.Contains("sales.orders", PanelFactoryResetService.BuildPlan(tables, tenant.Where(x => x != "sales.orders").ToHashSet(), keys).Problem);
    }

    [Fact]
    public void TheConnectionsAndTheirCredentialsStay_TheirDataAndCursorsGo()
    {
        foreach (var kept in new[] { "integration.platform_connections", "integration.platform_credentials", "integration.connection_sync_policies", "iam.users", "iam.tenants", "iam.tenant_memberships" })
            Assert.NotEqual(PanelResetAction.Delete, PanelFactoryResetInventory.Find(kept)!.Action);
        foreach (var removed in new[] { "integration.sync_cursors", "integration.jobs", "ops.audit_logs", "ops.ai_usage_logs", "ops.api_idempotency_records", "ops.operational_issues", "catalog.product_change_entries", "integration.price_stock_send_log", "sales.orders", "catalog.products", "integration.reference_items" })
            Assert.Equal(PanelResetAction.Delete, PanelFactoryResetInventory.Find(removed)!.Action);
        Assert.Equal(PanelFactoryResetInventory.Groups.Count + 2, PanelFactoryResetInventory.Steps.Count);
        Assert.Equal("Tamamlandı", PanelFactoryResetInventory.Steps[^1]);
    }
}
