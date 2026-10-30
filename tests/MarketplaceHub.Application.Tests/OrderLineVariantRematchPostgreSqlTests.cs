using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// Order lines read before their panel variant existed (live 30 Oct 2026: 104 of 111 unmatched lines) are matched by
// every order sync afterwards, without taking stock a second time for orders that already shipped.
[Collection("postgres-integration")]
public sealed class OrderLineVariantRematchPostgreSqlTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>, IAsyncLifetime
{
    private static readonly System.Collections.Concurrent.ConcurrentBag<Guid> CreatedTenants = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        var tenantIds = CreatedTenants.ToArray();
        if (tenantIds.Length == 0) return;
        await using var db = fixture.CreateContext();
        await db.JobAttempts.Where(x => tenantIds.Contains(x.TenantId)).ExecuteDeleteAsync();
        await db.IntegrationJobs.Where(x => tenantIds.Contains(x.TenantId)).ExecuteDeleteAsync();
    }

    [PostgreSqlFact]
    public async Task Rematch_MatchesLateLinesTakesStockOnlyForOpenOrdersAndRaisesAnIssueForCodesWithoutAVariant()
    {
        var tenant = NewTenant("order-line-rematch");
        var hepsiburada = NewConnection(tenant, "HEPSIBURADA");
        var (deliveredProduct, deliveredVariant) = NewVariant(tenant, "KRL01501K04", "KRL01501K04");
        var (openProduct, openVariant) = NewVariant(tenant, "RYP00121", "RYP00121");
        var deliveredOrder = NewOrder(tenant, hepsiburada, "4219346609", "DELIVERED");
        var openOrder = NewOrder(tenant, hepsiburada, "4552478925", "NEW");
        var missingOrder = NewOrder(tenant, hepsiburada, "4091991027", "DELIVERED");
        // Hepsiburada reports no shipped quantity on the line: only the order's status says it was delivered.
        var deliveredLine = NewLine(tenant, deliveredOrder, "KRL01501K04", "00KRL01501K04", """{"sku":"HBCV00008VUNWZ","merchantSku":"KRL01501K04"}""");
        var openLine = NewLine(tenant, openOrder, "RYP00121", "00000RYP00121", """{"sku":"HBCV0000AKL370"}""");
        var missingLine = NewLine(tenant, missingOrder, "KRL44118", "00000KRL44118", """{"sku":"HBCV00006HMJNA"}""");
        await using (var seed = fixture.CreateContext())
        {
            seed.Tenants.Add(tenant);
            seed.PlatformConnections.Add(hepsiburada);
            seed.Products.AddRange(deliveredProduct, openProduct);
            seed.ProductVariants.AddRange(deliveredVariant, openVariant);
            seed.InventoryItems.AddRange(NewInventory(tenant, deliveredVariant, 10m), NewInventory(tenant, openVariant, 10m));
            seed.Orders.AddRange(deliveredOrder, openOrder, missingOrder);
            seed.OrderLines.AddRange(deliveredLine, openLine, missingLine);
            await seed.SaveChangesAsync();
        }

        await using (var db = fixture.CreateContext())
        {
            var outcome = await Processor(db).RematchUnmatchedOrderLines(tenant.Id, hepsiburada.Id, CancellationToken.None);
            Assert.Equal(new MarketplaceJobProcessor.OrderLineRematchOutcome(3, 2, 1), outcome);
        }

        await using (var db = fixture.CreateContext())
        {
            Assert.Equal(deliveredVariant.Id, (await db.OrderLines.AsNoTracking().SingleAsync(x => x.Id == deliveredLine.Id)).VariantId);
            Assert.Equal(openVariant.Id, (await db.OrderLines.AsNoTracking().SingleAsync(x => x.Id == openLine.Id)).VariantId);
            Assert.Null((await db.OrderLines.AsNoTracking().SingleAsync(x => x.Id == missingLine.Id)).VariantId);
            // The delivered order's unit left before the panel tracked it: stock stays, the baseline records it.
            Assert.Equal(10m, (await db.InventoryItems.AsNoTracking().SingleAsync(x => x.VariantId == deliveredVariant.Id)).OnHand);
            var deliveredLedger = await db.StockLedgerEntries.AsNoTracking().Where(x => x.SourceId == deliveredLine.Id.ToString("D")).ToListAsync();
            Assert.Equal(1m, Assert.Single(deliveredLedger, x => x.MovementType == "ORDER_STOCK_BASELINE").QuantityDelta);
            Assert.DoesNotContain(deliveredLedger, x => x.MovementType == "ORDER_RECEIVED");
            // The open order's unit is still to ship: it leaves stock like a new order's.
            Assert.Equal(9m, (await db.InventoryItems.AsNoTracking().SingleAsync(x => x.VariantId == openVariant.Id)).OnHand);
            var issue = await db.OperationalIssues.AsNoTracking().SingleAsync(x => x.TenantId == tenant.Id && x.Code == MarketplaceJobProcessor.UnmatchedOrderLineIssueCode);
            Assert.Equal(IssueStatus.Open, issue.Status);
            Assert.Contains("4091991027", issue.Summary);
            Assert.Contains("KRL44118", issue.Summary);
        }

        // Idempotent: a second pass changes neither stock nor the issue.
        await using (var db = fixture.CreateContext())
        {
            var outcome = await Processor(db).RematchUnmatchedOrderLines(tenant.Id, hepsiburada.Id, CancellationToken.None);
            Assert.Equal(new MarketplaceJobProcessor.OrderLineRematchOutcome(1, 0, 1), outcome);
            Assert.Equal(10m, (await db.InventoryItems.AsNoTracking().SingleAsync(x => x.VariantId == deliveredVariant.Id)).OnHand);
            Assert.Equal(9m, (await db.InventoryItems.AsNoTracking().SingleAsync(x => x.VariantId == openVariant.Id)).OnHand);
            Assert.Equal(1, (await db.OperationalIssues.AsNoTracking().SingleAsync(x => x.TenantId == tenant.Id)).OccurrenceCount);
        }

        // The owner maps the code: the next pass matches the line and resolves the issue.
        var (missingProduct, missingVariant) = NewVariant(tenant, "KRL44118", "KRL44118");
        await using (var seed = fixture.CreateContext())
        {
            seed.Products.Add(missingProduct);
            seed.ProductVariants.Add(missingVariant);
            seed.InventoryItems.Add(NewInventory(tenant, missingVariant, 4m));
            await seed.SaveChangesAsync();
        }
        await using (var db = fixture.CreateContext())
        {
            var outcome = await Processor(db).RematchUnmatchedOrderLines(tenant.Id, hepsiburada.Id, CancellationToken.None);
            Assert.Equal(new MarketplaceJobProcessor.OrderLineRematchOutcome(1, 1, 0), outcome);
            Assert.Equal(missingVariant.Id, (await db.OrderLines.AsNoTracking().SingleAsync(x => x.Id == missingLine.Id)).VariantId);
            Assert.Equal(4m, (await db.InventoryItems.AsNoTracking().SingleAsync(x => x.VariantId == missingVariant.Id)).OnHand);
            Assert.Equal(IssueStatus.Resolved, (await db.OperationalIssues.AsNoTracking().SingleAsync(x => x.TenantId == tenant.Id)).Status);
        }
    }

    // A line already holding stock movements (a reset's carry rows, or rows of its own) is not given a second baseline.
    [PostgreSqlFact]
    public async Task Rematch_LeavesALineThatAlreadyHasStockRowsToTheProjection()
    {
        var tenant = NewTenant("order-line-rematch-carry");
        var trendyol = NewConnection(tenant, "TRENDYOL");
        var (product, variant) = NewVariant(tenant, "RYP00103", "RYP00103");
        var order = NewOrder(tenant, trendyol, "11509706338", "DELIVERED");
        var line = NewLine(tenant, order, "RYP00103", "RYP00103", "{}");
        line.ShippedQuantity = 1m;
        line.DeliveredQuantity = 1m;
        var item = NewInventory(tenant, variant, 7m);
        var carryKey = OrderInventoryStockPolicy.ResetCarryKey(trendyol.Id, order.ExternalOrderId, line.ExternalLineId);
        await using (var seed = fixture.CreateContext())
        {
            seed.Tenants.Add(tenant);
            seed.PlatformConnections.Add(trendyol);
            seed.Products.Add(product);
            seed.ProductVariants.Add(variant);
            seed.InventoryItems.Add(item);
            seed.Orders.Add(order);
            seed.OrderLines.Add(line);
            // The deleted line's movements a reset kept under the carry key: it took the unit when it was imported.
            seed.StockLedgerEntries.AddRange(
                new StockLedgerEntry { Id = Guid.CreateVersion7(), TenantId = tenant.Id, InventoryItemId = item.Id, MovementType = "ORDER_STOCK_BASELINE", QuantityDelta = 0m, SourceType = "ORDER_LINE", SourceId = carryKey, SourceEventId = "carried-baseline", IdempotencyKey = $"carried:{Guid.NewGuid():N}", OccurredAt = fixture.Now, RecordedAt = fixture.Now, CorrelationId = "carried" },
                new StockLedgerEntry { Id = Guid.CreateVersion7(), TenantId = tenant.Id, InventoryItemId = item.Id, MovementType = "ORDER_RECEIVED", QuantityDelta = -1m, SourceType = "ORDER_LINE", SourceId = carryKey, SourceEventId = "carried", IdempotencyKey = $"carried:{Guid.NewGuid():N}", OccurredAt = fixture.Now, RecordedAt = fixture.Now, CorrelationId = "carried" });
            await seed.SaveChangesAsync();
        }

        await using (var db = fixture.CreateContext())
            Assert.Equal(1, (await Processor(db).RematchUnmatchedOrderLines(tenant.Id, trendyol.Id, CancellationToken.None)).Matched);

        await using (var verify = fixture.CreateContext())
        {
            // The carried movements became the line's own and no second baseline was written: the unit is not taken again.
            var ledger = await verify.StockLedgerEntries.AsNoTracking().Where(x => x.SourceId == line.Id.ToString("D")).ToListAsync();
            Assert.Contains(ledger, x => x.MovementType == "ORDER_RECEIVED" && x.SourceEventId == "carried");
            Assert.Equal(0m, Assert.Single(ledger, x => x.MovementType == "ORDER_STOCK_BASELINE").QuantityDelta);
            Assert.Equal(7m, (await verify.InventoryItems.AsNoTracking().SingleAsync(x => x.Id == item.Id)).OnHand);
        }
    }

    private MarketplaceJobProcessor Processor(AppDbContext db) =>
        new(db, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, new ConfigurationBuilder().Build(), fixture.TimeProvider);

    private static Tenant NewTenant(string code)
    {
        var tenant = new Tenant { Id = Guid.CreateVersion7(), Code = $"{code}-{Guid.NewGuid():N}", DisplayName = code, Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow, Version = 1 };
        CreatedTenants.Add(tenant.Id);
        return tenant;
    }

    private static PlatformConnection NewConnection(Tenant tenant, string platformCode) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = tenant.Id,
        PublicId = Guid.CreateVersion7(),
        PlatformCode = platformCode,
        Environment = "PRODUCTION",
        DisplayName = $"{platformCode} eşleştirme testi",
        ExternalStoreId = $"store-{Guid.NewGuid():N}",
        Status = "ACTIVE",
        ApiVersion = "V1",
        SettingsJson = "{}"
    };

    private static (Product Product, ProductVariant Variant) NewVariant(Tenant tenant, string sku, string barcode)
    {
        var now = DateTimeOffset.UtcNow;
        var product = new Product { Id = Guid.CreateVersion7(), TenantId = tenant.Id, Title = $"Ürün {sku}", Description = "Eşleştirme testi", Status = ProductStatus.Active, CreatedAt = now, UpdatedAt = now };
        var variant = new ProductVariant { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ProductId = product.Id, Sku = sku, SkuNormalized = sku.ToUpperInvariant(), Barcode = barcode, BarcodeNormalized = barcode.ToUpperInvariant(), ModelCode = "MODEL-1", OptionSignature = sku, CreatedAt = now, UpdatedAt = now };
        return (product, variant);
    }

    private static InventoryItem NewInventory(Tenant tenant, ProductVariant variant, decimal onHand) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = tenant.Id,
        VariantId = variant.Id,
        LocationCode = "MAIN",
        OnHand = onHand,
        Available = onHand
    };

    private Order NewOrder(Tenant tenant, PlatformConnection connection, string number, string status) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = tenant.Id,
        ConnectionId = connection.Id,
        ExternalOrderId = $"ext-{number}",
        OrderNumber = number,
        Currency = "TRY",
        OrderedAt = fixture.Now.AddDays(-10),
        LastRemoteModifiedAt = fixture.Now.AddDays(-5),
        CustomerSnapshotJson = "{}",
        ShipmentAddressSnapshotJson = "{}",
        InvoiceAddressSnapshotJson = "{}",
        DerivedStatus = status,
        CreatedAt = fixture.Now.AddDays(-10),
        UpdatedAt = fixture.Now.AddDays(-5)
    };

    private static OrderLine NewLine(Tenant tenant, Order order, string sku, string barcode, string snapshot) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = tenant.Id,
        OrderId = order.Id,
        ExternalLineId = $"line-{Guid.NewGuid():N}",
        Sku = sku,
        Barcode = barcode,
        TitleSnapshot = $"Ürün {sku}",
        SourceSnapshotJson = snapshot,
        OrderedQuantity = 1m,
        UnitPrice = 100m,
        VatRate = 10m,
        RawStatus = order.DerivedStatus
    };
}
