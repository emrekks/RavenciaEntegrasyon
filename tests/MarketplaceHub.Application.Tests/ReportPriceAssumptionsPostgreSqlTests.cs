using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// GET /reports/price-assumptions read from the database: clothing told apart by the marketplace category paths the panel
// categories are mapped to, each store's commission on its clothing orders, and Hepsiburada's service charges from its
// accounting records.
[Collection("postgres-integration")]
public sealed class ReportPriceAssumptionsPostgreSqlTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    [PostgreSqlFact]
    public async Task Each_store_shows_what_it_took_of_the_clothing_orders_and_a_bag_stays_out()
    {
        var now = DateTimeOffset.UtcNow;
        var tenant = new Tenant { Id = Guid.CreateVersion7(), Code = $"pa-{Guid.NewGuid():N}"[..24], DisplayName = "pa", Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = now, UpdatedAt = now, Version = 1 };
        PlatformConnection Connection(string platform) => new() { Id = Guid.CreateVersion7(), TenantId = tenant.Id, PublicId = Guid.CreateVersion7(), PlatformCode = platform, Environment = "PRODUCTION", DisplayName = platform, ExternalStoreId = $"s-{Guid.NewGuid():N}", Status = "ACTIVE", ApiVersion = "V1", SettingsJson = "{}" };
        var trendyol = Connection("TRENDYOL");
        var hepsiburada = Connection("HEPSIBURADA");
        Category Category(string name) => new() { Id = Guid.CreateVersion7(), TenantId = tenant.Id, Name = name, NormalizedName = name.ToUpperInvariant(), Path = name, CreatedAt = now, UpdatedAt = now };
        var blouse = Category("Bluz");
        var bag = Category("Omuz Çantası");
        var tree = new ReferenceSnapshot { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = trendyol.Id, ResourceType = "CATEGORIES", SourceVersion = "v1", ContentHash = $"pa-{Guid.NewGuid():N}", FetchedAt = now, IsCurrent = true, ItemCount = 2 };
        ReferenceItem Item(string id, string path) => new() { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = trendyol.Id, SnapshotId = tree.Id, ResourceType = "CATEGORIES", ExternalId = id, Name = path.Split(" / ")[^1], NormalizedName = path, Path = path, IsLeaf = true, IsActive = true, PayloadHash = "h" };
        CategoryMapping Mapping(Category local, string external) => new() { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = trendyol.Id, SnapshotId = tree.Id, LocalId = local.Id, ExternalId = external, Status = "VERIFIED", VerifiedAt = now };
        Product Product(Category category) => new() { Id = Guid.CreateVersion7(), TenantId = tenant.Id, Title = category.Name, Description = "", Status = ProductStatus.Active, CategoryId = category.Id, CreatedAt = now, UpdatedAt = now };
        var blouseProduct = Product(blouse);
        var bagProduct = Product(bag);
        ProductVariant Variant(Product product) { var sku = $"PA-{Guid.NewGuid():N}"[..16].ToUpperInvariant(); return new() { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ProductId = product.Id, Sku = sku, SkuNormalized = sku, OptionSignature = "", CostPrice = 200m, CreatedAt = now, UpdatedAt = now }; }
        var blouseVariant = Variant(blouseProduct);
        var bagVariant = Variant(bagProduct);

        var orders = new List<Order>();
        var lines = new List<OrderLine>();
        var records = new List<MarketplaceFinanceTransaction>();
        Order Sale(PlatformConnection connection, ProductVariant variant, decimal price, string snapshot)
        {
            var at = now.AddDays(-5);
            var order = new Order { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, ExternalOrderId = $"pa-{orders.Count}-{Guid.NewGuid():N}"[..20], OrderNumber = $"pa-{orders.Count}-{Guid.NewGuid():N}"[..20], Currency = "TRY", GrossAmount = price, NetAmount = price, OrderedAt = at, LastRemoteModifiedAt = at, CustomerSnapshotJson = "{}", ShipmentAddressSnapshotJson = "{}", InvoiceAddressSnapshotJson = "{}", DerivedStatus = "DELIVERED", StoreFrontCode = connection.PlatformCode == "TRENDYOL" ? "TR" : null, CreatedAt = now, UpdatedAt = now, Version = 1 };
            orders.Add(order);
            lines.Add(new OrderLine { Id = Guid.CreateVersion7(), TenantId = tenant.Id, OrderId = order.Id, VariantId = variant.Id, ExternalLineId = $"{order.OrderNumber}-1", Sku = variant.Sku, TitleSnapshot = "Bluz", SourceSnapshotJson = snapshot, OrderedQuantity = 1m, UnitPrice = price, VatRate = 10m, RawStatus = "Delivered" });
            return order;
        }
        MarketplaceFinanceTransaction Record(Order order, string type, string category, decimal debt) => new() { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = hepsiburada.Id, PlatformCode = "HEPSIBURADA", Source = "HEPSIBURADA_TRANSACTIONS", ExternalId = Guid.NewGuid().ToString("N"), RequestedType = "OrderBased", TransactionType = type, Category = category, TransactionDate = now.AddDays(-2), OrderNumber = order.OrderNumber, Debt = debt, RawJson = "{}", FirstSeenAt = now, UpdatedAt = now };

        // Trendyol: five blouses at 20 %, and a bag at 10 % that is not clothing.
        for (var index = 0; index < 5; index++) Sale(trendyol, blouseVariant, 500m, """{"commission":20}""");
        Sale(trendyol, bagVariant, 1000m, """{"commission":10}""");
        // Hepsiburada: five blouses billed at 18 % + 20 % VAT, each with its payment service charge, one a processing fee too.
        for (var index = 0; index < 5; index++)
        {
            var order = Sale(hepsiburada, blouseVariant, 500m, """{"commission":{"amount":90}}""");
            records.Add(Record(order, "Commission", MarketplaceFinanceCategories.Commission, 108m));
            records.Add(Record(order, "PaymentServiceCostReflection", MarketplaceFinanceCategories.Other, 4.31m));
            if (index == 0) records.Add(Record(order, "ProcessingFeeExpense", MarketplaceFinanceCategories.ServiceFee, 13.19m));
        }

        await using (var db = fixture.CreateContext())
        {
            db.Tenants.Add(tenant);
            db.PlatformConnections.AddRange(trendyol, hepsiburada);
            db.Categories.AddRange(blouse, bag);
            db.ReferenceSnapshots.Add(tree);
            db.ReferenceItems.AddRange(Item("590", "Giyim / Üst Giyim / Bluz & Tunik / Bluz"), Item("971", "Aksesuar / Çanta / Omuz Çantası"));
            db.CategoryMappings.AddRange(Mapping(blouse, "590"), Mapping(bag, "971"));
            db.Products.AddRange(blouseProduct, bagProduct);
            db.ProductVariants.AddRange(blouseVariant, bagVariant);
            db.Orders.AddRange(orders);
            db.OrderLines.AddRange(lines);
            db.MarketplaceFinanceTransactions.AddRange(records);
            await db.SaveChangesAsync();
        }

        await using (var db = fixture.CreateContext())
        {
            var result = await new ReportsService(db, TimeProvider.System).PriceAssumptionsAsync(tenant.Id, CancellationToken.None);
            Assert.True(result.Succeeded);
            var view = result.Value!;
            Assert.True(view.ClothingOnly);
            Assert.Equal(60, view.Days);
            Assert.Equal(["TRENDYOL", "HEPSIBURADA"], view.Stores.Select(store => store.PlatformCode));

            var ty = view.Stores[0];
            Assert.Equal((5, 2500m, 20.00m), (ty.Orders, ty.Sales, ty.CommissionRate!.Value));
            // No cargo invoice read: the tariff is unknown, never 0.
            Assert.Null(ty.Cargo);

            var hb = view.Stores[1];
            Assert.Equal((5, 5, 21.60m, 18.00m), (hb.Orders, hb.RealCommissionOrders, hb.CommissionRate!.Value, hb.CommissionRateBeforeVat!.Value));
            // (5 x 4.31 + 13.19) / 5 billed orders.
            Assert.Equal((6.95m, "records", 5), (hb.ServiceFee, hb.ServiceFeeSource, hb.ServiceFeeOrders));
        }
    }
}
