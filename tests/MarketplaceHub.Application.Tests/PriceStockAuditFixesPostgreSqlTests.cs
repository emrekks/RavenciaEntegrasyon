using System.Text.Json;
using System.Text.Json.Nodes;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using MarketplaceHub.Infrastructure.Persistence.Migrations;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// The price/stock audit of 30 Oct 2026, against PostgreSQL.
[Collection("postgres-integration")]
public sealed class PriceStockAuditFixesPostgreSqlTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>, IAsyncLifetime
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

    // Items 1, 5 and 7: an offer saved without a price change sends nothing; a safety stock change sends the new
    // quantity alone (no price), as a stock change of the offer.
    [PostgreSqlFact]
    public async Task OfferSave_SendsOnlyWhatChanged()
    {
        var tenant = NewTenant("offer-save");
        var n11 = NewConnection(tenant, "N11");
        var (product, variant) = NewVariant(tenant, "OS-1");
        var inventory = NewInventory(tenant, variant, 3m);
        var offer = NewOffer(tenant, n11, variant);
        await using (var seed = fixture.CreateContext())
        {
            seed.Tenants.Add(tenant);
            seed.PlatformConnections.Add(n11);
            seed.Products.Add(product);
            seed.ProductVariants.Add(variant);
            seed.InventoryItems.Add(inventory);
            seed.ChannelOffers.Add(offer);
            seed.MarketplaceVariantLinks.Add(new MarketplaceVariantLink { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = n11.Id, VariantId = variant.Id, ExternalId = "OS-1" });
            await seed.SaveChangesAsync();
            // The offer's current values were sent.
            offer.LastPriceHash = PriceInventoryComposer.N11PriceHash(offer);
            offer.LastStockProjectionVersion = inventory.ProjectionVersion;
            offer.Version++;
            await seed.SaveChangesAsync();
        }

        await using (var db = fixture.CreateContext())
        {
            var service = new InventoryService(db, new CursorCodec(new EphemeralDataProtectionProvider(), TimeProvider.System), fixture.TimeProvider, new ConfigurationBuilder().Build());
            var unchanged = await service.UpdateOfferAsync(tenant.Id, Guid.CreateVersion7(), offer.Id, offer.Version, new(150m, 120m, "TRY", 20m, "INCLUDED", "HALF_EVEN", 0m, "ACTIVE", "Kaydet"), CancellationToken.None);
            Assert.True(unchanged.Succeeded, unchanged.Error?.Message);
            Assert.Equal(1, unchanged.Value!.PriceVersion);
            Assert.Equal("NO_EXTERNAL_CHANGES", (await new PriceInventoryComposer(db).BuildAsync(tenant.Id, n11.Id, CancellationToken.None)).Error?.Code);

            var safety = await service.UpdateOfferAsync(tenant.Id, Guid.CreateVersion7(), offer.Id, unchanged.Value.Version, new(150m, 120m, "TRY", 20m, "INCLUDED", "HALF_EVEN", 1m, "ACTIVE", "Güvenlik stoğu"), CancellationToken.None);
            Assert.True(safety.Succeeded, safety.Error?.Message);
            Assert.Equal(1, safety.Value!.PriceVersion);
            Assert.False(await db.ChannelPriceHistory.AnyAsync(x => x.OfferId == offer.Id && x.PriceVersion > 1));
        }

        await using (var db = fixture.CreateContext())
        {
            // "güvenlik stoğu 1 ise total stok 3 ise pazaryerlerine 2 adet gidecektir"
            var draft = await new PriceInventoryComposer(db).BuildAsync(tenant.Id, n11.Id, CancellationToken.None);
            Assert.True(draft.Succeeded, draft.Error?.Message);
            var line = Assert.Single(draft.Value!.Lines);
            Assert.Equal(2m, line.Quantity);
            Assert.True(line.PriceOmitted);
            Assert.Equal(1m, line.SafetyStock);
            var sku = JsonNode.Parse(draft.Value.PayloadJson)!["payload"]!["skus"]![0]!.AsObject();
            Assert.Equal(2, sku["quantity"]!.GetValue<int>());
            Assert.False(sku.ContainsKey("salePrice"));
        }
    }

    // Item 2: the emergency stop switches the automatic policies off and cancels the automatic sends not yet submitted;
    // a send started from the panel and a batch already submitted (being polled) carry on.
    [PostgreSqlFact]
    public async Task EmergencyStop_LeavesManualSendsAndSubmittedBatches()
    {
        var tenant = NewTenant("stop-scope");
        var n11 = NewConnection(tenant, "N11");
        var automaticSubmit = PushJob(tenant, n11, "SUBMIT", automatic: true, JobStatus.Pending);
        var automaticPoll = PushJob(tenant, n11, "POLL", automatic: true, JobStatus.RetryScheduled);
        var panelSend = PushJob(tenant, n11, "SUBMIT", automatic: false, JobStatus.Pending);
        await using (var seed = fixture.CreateContext())
        {
            seed.Tenants.Add(tenant);
            seed.PlatformConnections.Add(n11);
            seed.ConnectionSyncPolicies.AddRange(new[] { MarketplaceExternalWritePolicies.Price, MarketplaceExternalWritePolicies.Stock }
                .Select(resourceType => new ConnectionSyncPolicy { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = n11.Id, ResourceType = resourceType, IntervalSeconds = 0, Enabled = true, Version = 1 }));
            seed.IntegrationJobs.AddRange(automaticSubmit, automaticPoll, panelSend);
            await seed.SaveChangesAsync();
        }

        await using (var db = fixture.CreateContext())
        {
            var stop = await new PriceStockMonitorService(db, new ConfigurationBuilder().Build(), fixture.TimeProvider).StopAllAutomaticWritesAsync(tenant.Id, Guid.CreateVersion7(), "stop", CancellationToken.None);
            Assert.Equal((1, 2, 1), (stop.ConnectionsStopped, stop.PoliciesDisabled, stop.JobsCancelled));
        }

        await using var check = fixture.CreateContext();
        var statuses = await check.IntegrationJobs.AsNoTracking().Where(x => x.TenantId == tenant.Id).ToDictionaryAsync(x => x.Id, x => x.Status);
        Assert.Equal(JobStatus.Cancelled, statuses[automaticSubmit.Id]);
        Assert.Equal(JobStatus.RetryScheduled, statuses[automaticPoll.Id]);
        Assert.Equal(JobStatus.Pending, statuses[panelSend.Id]);
    }

    // Items 3 and 6: the data repair takes the missed line's unit from stock once (a second run changes nothing), keeps
    // what stock could not give as the line's shortfall, and releases a settled order's reservation without touching
    // on-hand. The order projection afterwards finds every line settled.
    [PostgreSqlFact]
    public async Task Repair_DeductsTheMissedLinesOnceAndReleasesSettledReservations()
    {
        var tenant = NewTenant("order-stock-repair");
        var trendyol = NewConnection(tenant, "TRENDYOL");
        var (product, missed) = NewVariant(tenant, "REP-1");
        var empty = NewVariant(tenant, "REP-2", product.Id).Variant;
        var reserved = NewVariant(tenant, "REP-3", product.Id).Variant;
        var missedItem = NewInventory(tenant, missed, 3m);
        var emptyItem = NewInventory(tenant, empty, 0m);
        var reservedItem = NewInventory(tenant, reserved, 0m);
        reservedItem.Reserved = 1m;
        var order = NewOrder(tenant, trendyol, "DELIVERED");
        var missedLine = NewLine(tenant, order, missed, shipped: true);
        var emptyLine = NewLine(tenant, order, empty, shipped: true);
        var reservedLine = NewLine(tenant, order, reserved, shipped: true);
        var reservation = new StockReservation { Id = Guid.CreateVersion7(), TenantId = tenant.Id, InventoryItemId = reservedItem.Id, SourceType = "ORDER_LINE", SourceId = reservedLine.Id.ToString("D"), Quantity = 1m, Status = ReservationStatus.Active, ExpiresAt = fixture.Now.AddDays(14), Version = 1 };
        await using (var seed = fixture.CreateContext())
        {
            seed.Tenants.Add(tenant);
            seed.PlatformConnections.Add(trendyol);
            seed.Products.Add(product);
            seed.ProductVariants.AddRange(missed, empty, reserved);
            seed.InventoryItems.AddRange(missedItem, emptyItem, reservedItem);
            seed.Orders.Add(order);
            seed.OrderLines.AddRange(missedLine, emptyLine, reservedLine);
            // What the old projection wrote for a line first read shipped: its unit as the baseline, nothing taken.
            seed.StockLedgerEntries.AddRange(Baseline(tenant, missedItem, missedLine, 1m), Baseline(tenant, emptyItem, emptyLine, 1m), Baseline(tenant, reservedItem, reservedLine, 0m));
            seed.StockReservations.Add(reservation);
            seed.OperationalIssues.Add(new OperationalIssue { Id = Guid.CreateVersion7(), TenantId = tenant.Id, DedupeKey = $"stock-order-consumption:{reservedLine.Id:N}", Code = "STOCK_ORDER_CONSUMPTION_BLOCKED", Summary = "x", Status = IssueStatus.Open, FirstSeenAt = fixture.Now, LastSeenAt = fixture.Now, OccurrenceCount = 1 });
            await seed.SaveChangesAsync();
        }

        var lines = new[] { (missedLine.Id.ToString("D"), missedItem.Id.ToString("D")), (emptyLine.Id.ToString("D"), emptyItem.Id.ToString("D")) };
        for (var run = 0; run < 2; run++)
        {
            await using var db = fixture.CreateContext();
            await db.Database.ExecuteSqlRawAsync(RepairOrderStockAndReleaseReservations.RepairMissedLinesSql(lines));
            await db.Database.ExecuteSqlRawAsync(RepairOrderStockAndReleaseReservations.ReleaseReservationsSql(tenant.Id));
        }

        async Task AssertItems()
        {
            await using var check = fixture.CreateContext();
            var items = await check.InventoryItems.AsNoTracking().Where(x => x.TenantId == tenant.Id).ToDictionaryAsync(x => x.Id);
            Assert.Equal((2m, 0m, 2m, 2L), (items[missedItem.Id].OnHand, items[missedItem.Id].Reserved, items[missedItem.Id].Available, items[missedItem.Id].ProjectionVersion));
            Assert.Equal((0m, 0m, 0m, 1L), (items[emptyItem.Id].OnHand, items[emptyItem.Id].Reserved, items[emptyItem.Id].Available, items[emptyItem.Id].ProjectionVersion));
            Assert.Equal((0m, 0m, 0m, 2L), (items[reservedItem.Id].OnHand, items[reservedItem.Id].Reserved, items[reservedItem.Id].Available, items[reservedItem.Id].ProjectionVersion));
        }
        await AssertItems();
        await using (var check = fixture.CreateContext())
        {
            Assert.Equal(ReservationStatus.Released, (await check.StockReservations.AsNoTracking().SingleAsync(x => x.Id == reservation.Id)).Status);
            Assert.Equal(IssueStatus.Resolved, (await check.OperationalIssues.AsNoTracking().SingleAsync(x => x.TenantId == tenant.Id)).Status);
            var ledger = await check.StockLedgerEntries.AsNoTracking().Where(x => x.TenantId == tenant.Id).ToListAsync();
            Assert.Equal(-1m, ledger.Where(x => x.InventoryItemId == missedItem.Id && x.MovementType == "ORDER_RECEIVED").Sum(x => x.QuantityDelta));
            Assert.Equal(1m, ledger.Where(x => x.InventoryItemId == emptyItem.Id && x.MovementType == OrderInventoryStockPolicy.ShortfallMovementType).Sum(x => x.QuantityDelta));
            Assert.Equal(1m, ledger.Where(x => x.InventoryItemId == reservedItem.Id && x.MovementType == OrderInventoryStockPolicy.ShortfallMovementType).Sum(x => x.QuantityDelta));
        }

        // The projection reading the order again moves nothing.
        await using (var db = fixture.CreateContext())
        {
            var processor = new MarketplaceJobProcessor(db, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, new ConfigurationBuilder().Build(), fixture.TimeProvider);
            var project = typeof(MarketplaceJobProcessor).GetMethod("ProjectOrderReservations", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            await (Task)project.Invoke(processor, [tenant.Id, trendyol.Id, new List<(OrderLine, DateTimeOffset)> { (missedLine, fixture.Now), (emptyLine, fixture.Now), (reservedLine, fixture.Now) }, CancellationToken.None])!;
            await db.SaveChangesAsync();
        }
        await AssertItems();
    }

    private static StockLedgerEntry Baseline(Tenant tenant, InventoryItem item, OrderLine line, decimal quantity) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = tenant.Id,
        InventoryItemId = item.Id,
        MovementType = "ORDER_STOCK_BASELINE",
        QuantityDelta = quantity,
        SourceType = "ORDER_LINE",
        SourceId = line.Id.ToString("D"),
        SourceEventId = $"baseline:{line.Id:D}",
        IdempotencyKey = $"order-stock-baseline:{item.Id:N}:{line.Id:D}",
        OccurredAt = DateTimeOffset.UtcNow,
        RecordedAt = DateTimeOffset.UtcNow,
        CorrelationId = $"order:{line.OrderId:N}"
    };

    private Order NewOrder(Tenant tenant, PlatformConnection connection, string status) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = tenant.Id,
        ConnectionId = connection.Id,
        ExternalOrderId = $"ord-{Guid.NewGuid():N}",
        OrderNumber = $"ord-{Guid.NewGuid():N}"[..20],
        Currency = "TRY",
        GrossAmount = 300m,
        NetAmount = 300m,
        OrderedAt = fixture.Now.AddHours(-2),
        LastRemoteModifiedAt = fixture.Now.AddHours(-1),
        CustomerSnapshotJson = "{}",
        ShipmentAddressSnapshotJson = "{}",
        InvoiceAddressSnapshotJson = "{}",
        DerivedStatus = status,
        CreatedAt = fixture.Now,
        UpdatedAt = fixture.Now,
        Version = 1
    };

    private static OrderLine NewLine(Tenant tenant, Order order, ProductVariant variant, bool shipped) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = tenant.Id,
        OrderId = order.Id,
        VariantId = variant.Id,
        ExternalLineId = $"line-{variant.Sku}",
        Sku = variant.Sku,
        TitleSnapshot = variant.Sku,
        OrderedQuantity = 1m,
        ShippedQuantity = shipped ? 1m : 0m,
        DeliveredQuantity = shipped ? 1m : 0m,
        UnitPrice = 100m,
        VatRate = 20m,
        RawStatus = shipped ? "Delivered" : "Created"
    };

    private static IntegrationJob PushJob(Tenant tenant, PlatformConnection connection, string phase, bool automatic, JobStatus status)
    {
        var id = Guid.CreateVersion7();
        var payload = JsonSerializer.Serialize(new PriceInventoryJobPayload(id, connection.Id, phase, "h", "{}", [], phase == "POLL" ? "op-1" : null, phase == "POLL" ? DateTimeOffset.UtcNow : null, Automatic: automatic));
        return new IntegrationJob
        {
            Id = id,
            TenantId = tenant.Id,
            ConnectionId = connection.Id,
            JobType = MarketplaceJobTypes.PriceInventorySync,
            PayloadJson = payload,
            PayloadVersion = 1,
            PayloadHash = "h",
            JobDedupKey = $"test-push:{id:N}",
            EffectIdempotencyKey = $"test-push:{id:N}",
            TriggerType = automatic ? "system" : "user",
            Status = status,
            AvailableAt = DateTimeOffset.UtcNow,
            CorrelationId = "test",
            CreatedAt = DateTimeOffset.UtcNow,
            Version = 1
        };
    }

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
        DisplayName = $"{platformCode} denetim testi",
        ExternalStoreId = $"store-{Guid.NewGuid():N}",
        Status = "ACTIVE",
        ApiVersion = "V1",
        SettingsJson = "{\"ExternalWritesEnabled\":true}",
        Version = 1
    };

    private static (Product Product, ProductVariant Variant) NewVariant(Tenant tenant, string sku, Guid? productId = null)
    {
        var now = DateTimeOffset.UtcNow;
        var product = new Product { Id = productId ?? Guid.CreateVersion7(), TenantId = tenant.Id, Title = $"Ürün {sku}", Description = "Denetim testi", Status = ProductStatus.Active, CreatedAt = now, UpdatedAt = now };
        var barcode = $"869{Guid.NewGuid().ToString("N")[..10]}";
        var variant = new ProductVariant { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ProductId = product.Id, Sku = sku, SkuNormalized = sku.ToUpperInvariant(), Barcode = barcode, BarcodeNormalized = barcode.ToUpperInvariant(), OptionSignature = sku, CreatedAt = now, UpdatedAt = now };
        return (product, variant);
    }

    private static InventoryItem NewInventory(Tenant tenant, ProductVariant variant, decimal onHand) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = tenant.Id,
        VariantId = variant.Id,
        LocationCode = "MAIN",
        OnHand = onHand,
        Available = onHand,
        ProjectionVersion = 1,
        Version = 1
    };

    private static ChannelOffer NewOffer(Tenant tenant, PlatformConnection connection, ProductVariant variant) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = tenant.Id,
        ConnectionId = connection.Id,
        VariantId = variant.Id,
        ListPrice = 150m,
        SalePrice = 120m,
        Currency = "TRY",
        VatRate = 20m,
        VatInclusion = "INCLUDED",
        RoundingMode = "HALF_EVEN",
        Status = "ACTIVE",
        PriceVersion = 1,
        Version = 1
    };
}
