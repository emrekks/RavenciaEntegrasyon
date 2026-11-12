using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// Ayarlar › Veri: every table the application has is classified; the categories together are exactly what Paneli Tamamen
// Sıfırla removes (every tenant table but the kept ones); every choice of categories plans without a broken key, and the
// delete order runs the categories in the order the page lists them.
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

    private static (List<string> Tables, HashSet<string> TenantColumns, List<PanelFactoryResetService.ForeignKey> Keys) Schema()
    {
        var model = Model();
        var entities = model.GetEntityTypes().Where(x => TableOf(x) is not null).ToList();
        var tables = entities.Select(x => TableOf(x)!).Concat(RawTables).Distinct(StringComparer.Ordinal).ToList();
        var tenant = entities.Where(x => x.FindProperty("TenantId") is not null).Select(x => TableOf(x)!).ToHashSet(StringComparer.Ordinal);
        var keys = entities.SelectMany(x => x.GetForeignKeys().Where(fk => TableOf(fk.PrincipalEntityType) is not null)
                .Select(fk => new PanelFactoryResetService.ForeignKey(TableOf(x)!, TableOf(fk.PrincipalEntityType)!, fk.DeleteBehavior is DeleteBehavior.Cascade or DeleteBehavior.SetNull)))
            .Distinct().ToList();
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
        Assert.All(PanelFactoryResetInventory.Groups, group => Assert.Contains(PanelFactoryResetInventory.Tables, x => x.Group == group && x.Action == PanelResetAction.Delete));
    }

    [Fact]
    public void TheCategoriesTogether_AreExactlyTheFullReset_EveryTenantTableButTheKeptOnes()
    {
        var (tables, tenant, keys) = Schema();
        var byCategory = PanelFactoryResetInventory.Groups.SelectMany(group => PanelFactoryResetInventory.Tables.Where(x => x.Group == group && x.Action == PanelResetAction.Delete).Select(x => x.Name)).Order(StringComparer.Ordinal).ToList();
        var full = PanelFactoryResetService.BuildPlan(tables, tenant, keys, PanelFactoryResetInventory.Groups).Order.Select(x => x.Name).Order(StringComparer.Ordinal).ToList();
        var kept = PanelFactoryResetInventory.Tables.Where(x => x.Action != PanelResetAction.Delete).Select(x => x.Name).ToHashSet(StringComparer.Ordinal);
        var tenantTablesButKept = tenant.Where(x => !kept.Contains(x)).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(tenantTablesButKept, byCategory);
        Assert.Equal(tenantTablesButKept, full);
        Assert.Equal(PanelFactoryResetInventory.Groups.Select(x => x.Key), PanelFactoryResetInventory.Close(PanelFactoryResetInventory.Groups.Select(x => x.Key))!.Select(x => x.Key));
    }

    [Fact]
    public void EveryChoiceOfCategories_PlansChildrenFirst_InCategoryOrder_WithoutABrokenKey()
    {
        var (tables, tenant, keys) = Schema();
        var groups = PanelFactoryResetInventory.Groups;
        // A key joins two categories: every single one, every pair and every all-but-one choice meets each key both ways.
        var choices = groups.Select(x => new[] { x.Key })
            .Concat(groups.SelectMany((x, i) => groups.Skip(i + 1).Select(y => new[] { x.Key, y.Key })))
            .Concat(groups.Select(x => groups.Where(y => y != x).Select(y => y.Key).ToArray()))
            .Append(groups.Select(x => x.Key).ToArray());
        foreach (var choice in choices)
        {
            var chosen = PanelFactoryResetInventory.Close(choice)!;
            var plan = PanelFactoryResetService.BuildPlan(tables, tenant, keys, chosen);
            Assert.True(plan.Problem is null, $"{string.Join(',', chosen.Select(x => x.Key))}: {plan.Problem}");
            Assert.Equal(plan.Order.Select(x => x.Group!.Rank).Order(), plan.Order.Select(x => x.Group!.Rank));
            var order = plan.Order.Select(x => x.Name).ToList();
            foreach (var key in keys.Where(x => x.Child != x.Parent && order.Contains(x.Child) && order.Contains(x.Parent)))
                Assert.True(order.IndexOf(key.Child) < order.IndexOf(key.Parent), $"{key.Child} must go before {key.Parent}");
        }
    }

    [Fact]
    public void ACategoryBringsWhatItRequires_AndDetachesWhatStays()
    {
        var (tables, tenant, keys) = Schema();
        Assert.Equal(["INVOICES", "ORDERS"], PanelFactoryResetInventory.Close(["ORDERS"])!.Select(x => x.Key));
        Assert.Equal(["STOCK", "PRODUCTS"], PanelFactoryResetInventory.Close(["PRODUCTS"])!.Select(x => x.Key));
        Assert.Null(PanelFactoryResetInventory.Close(["NOPE"]));
        var brands = PanelFactoryResetService.BuildPlan(tables, tenant, keys, PanelFactoryResetInventory.Close(["BRANDS"])!);
        Assert.Equal(["catalog.brand_mappings", "catalog.brands"], brands.Order.Select(x => x.Name).Order(StringComparer.Ordinal));
        Assert.Contains(brands.Detaches, x => x.Sql.Contains("\"BrandId\" = NULL", StringComparison.Ordinal));
        var products = PanelFactoryResetService.BuildPlan(tables, tenant, keys, PanelFactoryResetInventory.Close(["PRODUCTS"])!);
        Assert.Contains(products.Detaches, x => x.Sql.Contains("sales.order_lines", StringComparison.Ordinal));
        Assert.Contains(products.Detaches, x => x.Sql.Contains("sales.return_stock_dispositions", StringComparison.Ordinal));
        Assert.Empty(PanelFactoryResetService.BuildPlan(tables, tenant, keys, PanelFactoryResetInventory.Groups).Detaches);
        Assert.Contains("catalog.categories", products.SelfReferencing.Concat(PanelFactoryResetService.BuildPlan(tables, tenant, keys, PanelFactoryResetInventory.Groups).SelfReferencing));
    }

    [Fact]
    public void ThePlan_StopsOnAnUnknownTable_AndOnAKeptTablePointingAtADeletedOne()
    {
        var (tables, tenant, keys) = Schema();
        var all = PanelFactoryResetInventory.Groups;
        Assert.Contains("ops.new_log", PanelFactoryResetService.BuildPlan([.. tables, "ops.new_log"], tenant, keys, all).Problem);
        Assert.Contains("iam.tenant_settings → sales.orders", PanelFactoryResetService.BuildPlan(tables, tenant, [.. keys, new("iam.tenant_settings", "sales.orders", false)], all).Problem);
        Assert.Contains("sales.orders", PanelFactoryResetService.BuildPlan(tables, tenant.Where(x => x != "sales.orders").ToHashSet(), keys, all).Problem);
        // A key between categories with no way to detach it asks for the other category.
        Assert.Contains("Müşteri Soruları", PanelFactoryResetService.BuildPlan(tables, tenant, [.. keys, new("sales.marketplace_questions", "catalog.brands", false)], PanelFactoryResetInventory.Close(["BRANDS"])!).Problem);
    }

    [Fact]
    public void EveryStoredFileReference_IsKnown()
    {
        var model = Model();
        var references = model.GetEntityTypes().Where(x => TableOf(x) is not null)
            .SelectMany(x => x.GetProperties().Where(p => p.Name.EndsWith("AssetId", StringComparison.Ordinal)).Select(p => (TableOf(x)!, p.GetColumnName())))
            .Distinct().Order().ToList();
        Assert.Equal(references, PanelFactoryResetService.FileReferences.Select(x => (x.Table, x.Column)).Order().ToList());
    }

    [Fact]
    public void TheConnectionsAndTheirCredentialsStay_TheirDataAndCursorsGo()
    {
        foreach (var kept in new[] { "integration.platform_connections", "integration.platform_credentials", "integration.connection_sync_policies", "iam.users", "iam.tenants", "iam.tenant_memberships" })
            Assert.NotEqual(PanelResetAction.Delete, PanelFactoryResetInventory.Find(kept)!.Action);
        foreach (var removed in new[] { "integration.sync_cursors", "integration.jobs", "ops.audit_logs", "ops.ai_usage_logs", "ops.api_idempotency_records", "ops.operational_issues", "catalog.product_change_entries", "integration.price_stock_send_log", "sales.orders", "catalog.products", "integration.reference_items", "dashboard.revenue_daily" })
            Assert.Equal(PanelResetAction.Delete, PanelFactoryResetInventory.Find(removed)!.Action);
        Assert.Equal(PanelFactoryResetInventory.Groups.Count + 3, PanelFactoryResetInventory.Steps.Count);
        Assert.Equal("Tamamlandı", PanelFactoryResetInventory.Steps[^1]);
    }

    [Theory]
    [InlineData("PANELİ SIFIRLA", true)]
    [InlineData("  paneli   sıfırla ", true)]
    [InlineData("Paneli Sıfırla", true)]
    [InlineData("PANELI SIFIRLA", false)]
    [InlineData("paneli sifirla", false)]
    [InlineData(null, false)]
    public void TheConfirmation_IsComparedInTurkishCapitals(string? typed, bool accepted) =>
        Assert.Equal(accepted, PanelFactoryResetService.NormalizeConfirmation(typed) == PanelFactoryResetService.ConfirmationWord);

    [Fact]
    public void TheCategoryConfirmation_VerileriSil_IsComparedTheSameWay() =>
        Assert.Equal(PanelFactoryResetService.PartialConfirmationWord, PanelFactoryResetService.NormalizeConfirmation(" verileri  sil"));
}
