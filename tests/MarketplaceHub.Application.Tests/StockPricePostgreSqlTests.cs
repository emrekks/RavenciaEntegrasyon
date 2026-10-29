using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Adapters.PttAvm;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// Stock, price and publishing behaviour that needs the real schema (audit 2026-10-19, section 4.3).
[Collection("postgres-integration")]
public sealed class StockPricePostgreSqlTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>, IAsyncLifetime
{
    // Jobs these tests queue (dispatches, price/stock pushes) stay PENDING; the lease tests in the same
    // database take any pending job, so every test removes the jobs of the tenants it created.
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

    // W-3: a channel offer with a sale price of zero is refused, on create and on update; a sale price equal to the
    // list price is a valid offer (every marketplace takes listPrice >= salePrice).
    [PostgreSqlFact]
    public async Task ChannelOffer_RefusesAZeroSalePriceAndTakesOneEqualToTheListPrice()
    {
        var tenant = NewTenant("offer-zero-sale");
        var trendyol = NewConnection(tenant, "TRENDYOL");
        var (product, variant) = NewVariant(tenant, "ZERO-1", "8690000000801");
        await using (var seed = fixture.CreateContext())
        {
            seed.Tenants.Add(tenant);
            seed.PlatformConnections.Add(trendyol);
            seed.Products.Add(product);
            seed.ProductVariants.Add(variant);
            await seed.SaveChangesAsync();
        }

        await using var db = fixture.CreateContext();
        var service = new InventoryService(db, new CursorCodec(new EphemeralDataProtectionProvider(), TimeProvider.System), fixture.TimeProvider, new ConfigurationBuilder().Build());
        UpsertChannelOfferCommand Create(decimal list, decimal sale) => new(trendyol.Id, variant.Id, list, sale, "TRY", 20m, "INCLUDED", "HALF_EVEN", 0m, "ACTIVE", "Toplu ürün fiyat düzenleme");

        var zero = await service.UpsertOfferAsync(tenant.Id, Guid.CreateVersion7(), Create(799.90m, 0m), CancellationToken.None);
        Assert.False(zero.Succeeded);
        Assert.True(zero.Error!.FieldErrors!.ContainsKey("salePrice"));
        Assert.False(await db.ChannelOffers.AsNoTracking().AnyAsync(x => x.TenantId == tenant.Id));

        var equal = await service.UpsertOfferAsync(tenant.Id, Guid.CreateVersion7(), Create(799.90m, 799.90m), CancellationToken.None);
        Assert.True(equal.Succeeded, equal.Error?.Message);
        Assert.Equal(799.90m, equal.Value!.SalePrice);

        var zeroUpdate = await service.UpdateOfferAsync(tenant.Id, Guid.CreateVersion7(), equal.Value.Id, equal.Value.Version, new(799.90m, 0m, "TRY", 20m, "INCLUDED", "HALF_EVEN", 0m, "ACTIVE", "Toplu ürün fiyat düzenleme"), CancellationToken.None);
        Assert.False(zeroUpdate.Succeeded);
        Assert.Equal(799.90m, (await db.ChannelOffers.AsNoTracking().SingleAsync(x => x.Id == equal.Value.Id)).SalePrice);
    }

    // K1: a manual adjustment reaches every connection with an ACTIVE offer for the variant.
    [PostgreSqlFact]
    public async Task ManualStockAdjustment_QueuesADispatchForEveryConnectionSellingTheVariant()
    {
        var tenant = NewTenant("stock-fan-out");
        var trendyol = NewConnection(tenant, "TRENDYOL", writesEnabled: true);
        var n11 = NewConnection(tenant, "N11", writesEnabled: true);
        var pazarama = NewConnection(tenant, "PAZARAMA", writesEnabled: true);
        var passive = NewConnection(tenant, "HEPSIBURADA", writesEnabled: true);
        passive.Status = "PASSIVE";
        // Writes off: the dispatch would send nothing, so none is queued (audit 2026-10-20 Q7).
        var writesOff = NewConnection(tenant, "HEPSIBURADA");
        var (product, variant) = NewVariant(tenant, "FAN-1", "8690000000011");
        await using (var seed = fixture.CreateContext())
        {
            seed.Tenants.Add(tenant);
            seed.PlatformConnections.AddRange(trendyol, n11, pazarama, passive, writesOff);
            seed.Products.Add(product);
            seed.ProductVariants.Add(variant);
            seed.InventoryItems.Add(NewInventory(tenant, variant, 5m));
            seed.ChannelOffers.AddRange(NewOffer(tenant, trendyol, variant), NewOffer(tenant, n11, variant), NewOffer(tenant, pazarama, variant, status: "INACTIVE"), NewOffer(tenant, passive, variant), NewOffer(tenant, writesOff, variant));
            await seed.SaveChangesAsync();
        }

        await using var db = fixture.CreateContext();
        var service = new InventoryService(db, new CursorCodec(new EphemeralDataProtectionProvider(), TimeProvider.System), fixture.TimeProvider, new ConfigurationBuilder().Build());
        var result = await service.AdjustAsync(tenant.Id, Guid.CreateVersion7(), variant.Id, new StockAdjustmentCommand(3m, "Sayım farkı", "count-1"), $"adjust-{Guid.NewGuid():N}", "stock-fan-out", CancellationToken.None);

        Assert.True(result.Succeeded, result.Error?.Message);
        var dispatchConnections = await db.IntegrationJobs.AsNoTracking()
            .Where(x => x.TenantId == tenant.Id && x.JobType == MarketplaceJobTypes.StockProjectionDispatch)
            .Select(x => x.ConnectionId)
            .ToListAsync();
        Assert.Equal(new Guid?[] { trendyol.Id, n11.Id }.OrderBy(x => x), dispatchConnections.OrderBy(x => x));
    }

    // K2: a candidate whose offer cannot be sent no longer stops the reconcile scan.
    [PostgreSqlFact]
    public async Task StockReconcile_ContinuesPastACandidateWithAnInvalidOffer()
    {
        var tenant = NewTenant("stock-reconcile-continue");
        var n11 = NewConnection(tenant, "N11", writesEnabled: true);
        var (product, invalid) = NewVariant(tenant, "REC-1", "8690000000028");
        var valid = NewVariant(tenant, "REC-2", "8690000000035", product.Id).Variant;
        var now = fixture.Now;
        await using (var seed = fixture.CreateContext())
        {
            seed.Tenants.Add(tenant);
            seed.PlatformConnections.Add(n11);
            seed.Products.Add(product);
            seed.ProductVariants.AddRange(invalid, valid);
            var invalidInventory = NewInventory(tenant, invalid, 4m);
            invalidInventory.ReconciledAt = now.AddHours(-10);
            var validInventory = NewInventory(tenant, valid, 6m);
            validInventory.ReconciledAt = now.AddHours(-5);
            seed.InventoryItems.AddRange(invalidInventory, validInventory);
            seed.ChannelOffers.AddRange(NewOffer(tenant, n11, invalid, currency: "USD"), NewOffer(tenant, n11, valid));
            seed.MarketplaceVariantLinks.AddRange(
                new MarketplaceVariantLink { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = n11.Id, VariantId = invalid.Id, ExternalId = "REC-1" },
                new MarketplaceVariantLink { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = n11.Id, VariantId = valid.Id, ExternalId = "REC-2" });
            await seed.SaveChangesAsync();
        }

        await using var db = fixture.CreateContext();
        var processor = new MarketplaceJobProcessor(db, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, WritesOnConfiguration(), fixture.TimeProvider);
        var result = await processor.ProcessAsync(tenant.Id, n11.Id, MarketplaceJobTypes.StockReconciliation, "{\"lookbackHours\":1}", "stock-reconcile-continue", CancellationToken.None);

        Assert.True(result.Succeeded, result.ErrorSummary);
        var pushes = await db.IntegrationJobs.AsNoTracking().Where(x => x.TenantId == tenant.Id && x.JobType == MarketplaceJobTypes.PriceInventorySync).Select(x => x.PayloadJson).ToListAsync();
        Assert.Single(pushes);
        Assert.Contains(valid.Id.ToString(), pushes[0], StringComparison.OrdinalIgnoreCase);
        Assert.True(await db.OperationalIssues.AsNoTracking().AnyAsync(x => x.TenantId == tenant.Id && x.Code == "CHANNEL_OFFER_INVALID"));
    }

    // Q-3: the scan walks every ACTIVE offer over its passes instead of the same first batch. Offers whose stock was
    // sent and whose stock row was observed a moment ago (but whose price never went out) were never candidates; a
    // stock-lagging offer goes first; the walk keeps its place and starts over at the end.
    [PostgreSqlFact]
    public async Task StockReconcile_WalksEveryActiveOfferAcrossPasses()
    {
        var tenant = NewTenant("stock-reconcile-walk");
        var n11 = NewConnection(tenant, "N11", writesEnabled: true);
        var product = NewVariant(tenant, "WALK-0", "8690000000400").Product;
        var variants = Enumerable.Range(1, 5).Select(index => NewVariant(tenant, $"WALK-{index}", $"869000000040{index}", product.Id).Variant).ToArray();
        var lagging = variants[^1];
        await using (var seed = fixture.CreateContext())
        {
            seed.Tenants.Add(tenant);
            seed.PlatformConnections.Add(n11);
            seed.Products.Add(product);
            seed.ProductVariants.AddRange(variants);
            foreach (var variant in variants)
            {
                var inventory = NewInventory(tenant, variant, 3m);
                inventory.ProjectionVersion = 4;
                inventory.ReconciledAt = fixture.Now;
                seed.InventoryItems.Add(inventory);
                var offer = NewOffer(tenant, n11, variant);
                offer.LastStockProjectionVersion = variant == lagging ? 3 : 4;
                seed.ChannelOffers.Add(offer);
                seed.MarketplaceVariantLinks.Add(new MarketplaceVariantLink { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = n11.Id, VariantId = variant.Id, ExternalId = variant.Sku });
            }
            await seed.SaveChangesAsync();
        }

        async Task<List<string>> ScanAsync()
        {
            await using var db = fixture.CreateContext();
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["FeatureFlags:ExternalWrites"] = "true",
                ["MarketplaceSync:StockReconciliation:BatchSize"] = "2"
            }).Build();
            var processor = new MarketplaceJobProcessor(db, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, configuration, fixture.TimeProvider);
            var result = await processor.ProcessAsync(tenant.Id, n11.Id, MarketplaceJobTypes.StockReconciliation, "{\"lookbackHours\":1}", "stock-reconcile-walk", CancellationToken.None);
            Assert.True(result.Succeeded, result.ErrorSummary);
            return await db.IntegrationJobs.AsNoTracking().Where(x => x.TenantId == tenant.Id && x.JobType == MarketplaceJobTypes.PriceInventorySync).OrderBy(x => x.CreatedAt).Select(x => x.PayloadJson).ToListAsync();
        }
        bool Queued(IEnumerable<string> payloads, ProductVariant variant) => payloads.Any(payload => payload.Contains(variant.Id.ToString(), StringComparison.OrdinalIgnoreCase));

        // A budget of two: one slot for the stock-lagging offer (still lagging while its job waits), one for the walk.
        var first = await ScanAsync();
        Assert.Equal(2, first.Count);
        Assert.True(Queued(first, lagging));
        Assert.Equal(3, (await ScanAsync()).Count);
        Assert.Equal(4, (await ScanAsync()).Count);
        var fourth = await ScanAsync();
        Assert.Equal(5, fourth.Count);
        Assert.All(variants, variant => Assert.True(Queued(fourth, variant), variant.Sku));
        // Everything queued: a further pass wraps around and opens nothing beside the queued jobs.
        Assert.Equal(5, (await ScanAsync()).Count);
        await using var check = fixture.CreateContext();
        var cursor = await check.SyncCursors.AsNoTracking().SingleAsync(x => x.TenantId == tenant.Id && x.ConnectionId == n11.Id && x.ResourceType == "STOCK_RECONCILIATION");
        Assert.Contains("stock-reconcile-v1", cursor.OpaqueCursor);
        Assert.True(await check.InventoryItems.AsNoTracking().Where(x => x.TenantId == tenant.Id).AllAsync(x => x.ReconciledAt != null));
    }

    // K-2: "update prices and stock" takes the marketplace number read at the page read. An order the panel took from
    // stock after that read is not in the number, so it comes off again; an order held as a reservation from before
    // the read is already off the number, so it is added back (its reservation takes it later). The change is a
    // stock ledger row and a dispatch to the connections selling the variant.
    [PostgreSqlFact]
    public async Task CatalogStockUpdate_CountsOrdersAfterTheReadAndReservationsBeforeIt()
    {
        var tenant = NewTenant("catalog-stock-orders");
        var shopify = NewConnection(tenant, "SHOPIFY", writesEnabled: true);
        var (product, ordered) = NewVariant(tenant, "K2-ORDERED", "8690000000501");
        var reserved = NewVariant(tenant, "K2-RESERVED", "8690000000502", product.Id).Variant;
        var orderedInventory = NewInventory(tenant, ordered, 10m);
        orderedInventory.ProjectionVersion = 2;
        orderedInventory.Version = 2;
        var reservedInventory = NewInventory(tenant, reserved, 0m);
        reservedInventory.Reserved = 2m;
        reservedInventory.ProjectionVersion = 3;
        reservedInventory.Version = 3;
        var reservationSource = Guid.CreateVersion7().ToString("D");
        await using (var seed = fixture.CreateContext())
        {
            seed.Tenants.Add(tenant);
            seed.PlatformConnections.Add(shopify);
            seed.Products.Add(product);
            seed.ProductVariants.AddRange(ordered, reserved);
            seed.InventoryItems.AddRange(orderedInventory, reservedInventory);
            seed.ChannelOffers.AddRange(NewOffer(tenant, shopify, ordered), NewOffer(tenant, shopify, reserved));
            seed.MarketplaceProductLinks.Add(new MarketplaceProductLink { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = shopify.Id, ProductId = product.Id, ExternalId = "k2-product", LastImportedProductVersion = product.Version, SyncStatus = "SYNCED", Version = 1 });
            seed.MarketplaceVariantLinks.AddRange(
                new MarketplaceVariantLink { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = shopify.Id, VariantId = ordered.Id, ExternalId = "k2-ordered" },
                new MarketplaceVariantLink { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = shopify.Id, VariantId = reserved.Id, ExternalId = "k2-reserved" });
            // Two units of an order the panel could not take from stock an hour ago: held as a reservation.
            seed.StockReservations.Add(new StockReservation { Id = Guid.CreateVersion7(), TenantId = tenant.Id, InventoryItemId = reservedInventory.Id, SourceType = "ORDER_LINE", SourceId = reservationSource, Quantity = 2m, Status = ReservationStatus.Active, Version = 1 });
            seed.StockLedgerEntries.Add(new StockLedgerEntry { Id = Guid.CreateVersion7(), TenantId = tenant.Id, InventoryItemId = reservedInventory.Id, MovementType = "ORDER_RESERVED", QuantityDelta = -2m, SourceType = "ORDER_LINE", SourceId = reservationSource, SourceEventId = $"k2:{reservationSource}", IdempotencyKey = $"k2-reserved:{reservationSource}", OccurredAt = DateTimeOffset.UtcNow.AddHours(-1), RecordedAt = DateTimeOffset.UtcNow.AddHours(-1), CorrelationId = "k2" });
            await seed.SaveChangesAsync();
        }

        RemoteCatalogVariant Remote(string id, string sku, string barcode, decimal stock) => new(id, sku, barcode, null, new Dictionary<string, string>(), false, 120m, 150m, 20m, stock, "TRY", "{}");
        var port = new CatalogReadPort(
            new RemoteCatalogProduct("k2-product", null, "Ürün", "", null, null, null, null, [], [Remote("k2-ordered", "K2-ORDERED", "8690000000501", 12m), Remote("k2-reserved", "K2-RESERVED", "8690000000502", 8m)], "{}"),
            async () =>
            {
                // While the page is being read, an order takes two units of the first variant from stock in the panel.
                await using var orders = fixture.CreateContext();
                var item = await orders.InventoryItems.SingleAsync(x => x.Id == orderedInventory.Id);
                item.OnHand -= 2m;
                item.Available = item.OnHand;
                item.ProjectionVersion++;
                item.Version++;
                var source = Guid.CreateVersion7().ToString("D");
                orders.StockLedgerEntries.Add(new StockLedgerEntry { Id = Guid.CreateVersion7(), TenantId = tenant.Id, InventoryItemId = item.Id, MovementType = "ORDER_RECEIVED", QuantityDelta = -2m, SourceType = "ORDER_LINE", SourceId = source, SourceEventId = $"k2:{source}", IdempotencyKey = $"k2-received:{source}", OccurredAt = DateTimeOffset.UtcNow, RecordedAt = DateTimeOffset.UtcNow, CorrelationId = "k2" });
                await orders.SaveChangesAsync();
            });

        await using (var db = fixture.CreateContext())
        {
            var processor = new MarketplaceJobProcessor(db, null!, null!, port, null!, null!, null!, null!, null!, null!, null!, new ConfigurationBuilder().Build(), fixture.TimeProvider);
            var result = await processor.ProcessAsync(tenant.Id, shopify.Id, MarketplaceJobTypes.ShopifyProductSync, "{\"full\":true,\"updateExistingProducts\":false,\"updateExistingPricesAndStock\":true}", "catalog-stock-orders", CancellationToken.None);
            Assert.True(result.Succeeded, result.ErrorSummary);
        }

        await using var check = fixture.CreateContext();
        var orderedAfter = await check.InventoryItems.AsNoTracking().SingleAsync(x => x.Id == orderedInventory.Id);
        var reservedAfter = await check.InventoryItems.AsNoTracking().SingleAsync(x => x.Id == reservedInventory.Id);
        Assert.Equal(10m, orderedAfter.OnHand);
        Assert.Equal(10m, orderedAfter.Available);
        Assert.Equal(10m, reservedAfter.OnHand);
        Assert.Equal(8m, reservedAfter.Available);
        var imports = await check.StockLedgerEntries.AsNoTracking()
            .Where(x => x.TenantId == tenant.Id && x.MovementType == MarketplaceStockImportPolicy.MovementType)
            .ToDictionaryAsync(x => x.InventoryItemId, x => x.QuantityDelta);
        Assert.Equal(2m, imports[orderedInventory.Id]);
        Assert.Equal(10m, imports[reservedInventory.Id]);
        var dispatched = await check.IntegrationJobs.AsNoTracking()
            .Where(x => x.TenantId == tenant.Id && x.JobType == MarketplaceJobTypes.StockProjectionDispatch)
            .Select(x => x.PayloadJson)
            .ToListAsync();
        Assert.Contains(dispatched, payload => payload.Contains(ordered.Id.ToString(), StringComparison.OrdinalIgnoreCase));
        Assert.Contains(dispatched, payload => payload.Contains(reserved.Id.ToString(), StringComparison.OrdinalIgnoreCase));
    }

    private sealed class CatalogReadPort(RemoteCatalogProduct snapshot, Func<Task> duringRead) : IProductPort
    {
        public async Task<AdapterResult<AdapterPageResult<RemoteCatalogProduct>>> ListCatalogAsync(AdapterContext context, AdapterPageRequest page, ProductReadFilter filter, CancellationToken cancellationToken)
        {
            await duringRead();
            return AdapterResult<AdapterPageResult<RemoteCatalogProduct>>.Success(new([snapshot], null, false, 1));
        }

        public Task<AdapterResult<AdapterPageResult<RemoteProduct>>> ListAsync(AdapterContext context, AdapterPageRequest page, ProductReadFilter filter, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<RemoteOperationRef>> CreateAsync(AdapterContext context, ProductPublication publication, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<RemoteOperationRef>> UpdateUnapprovedAsync(AdapterContext context, ProductUpdatePublication publication, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<RemoteOperationRef>> UpdateApprovedContentAsync(AdapterContext context, ProductUpdatePublication publication, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<RemoteOperationRef>> UpdateApprovedVariantsAsync(AdapterContext context, ProductUpdatePublication publication, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<RemoteOperationRef>> UpdateApprovedDeliveryAsync(AdapterContext context, ProductUpdatePublication publication, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<RemoteOperationStatus>> GetOperationAsync(AdapterContext context, string externalOperationId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<RemotePublicationStatus>> GetPublicationStatusAsync(AdapterContext context, string barcode, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<RemoteOperationRef>> ArchiveAsync(AdapterContext context, string payloadJson, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    // Q-1/K-1: a price/stock job of the same values that ended without getting them through no longer counts as
    // sent. The reconcile scan retires its key and queues them again (at once after a pre-send stop, an hour after
    // any other failure), while a job still queued is left alone and no second one is opened beside it.
    [PostgreSqlFact]
    public async Task StockReconcile_SendsTheValuesAgainAfterTheirJobFailed()
    {
        var tenant = NewTenant("stock-reconcile-failed-push");
        var n11 = NewConnection(tenant, "N11", writesEnabled: true);
        var (product, variant) = NewVariant(tenant, "RET-1", "8690000000042");
        await using (var seed = fixture.CreateContext())
        {
            seed.Tenants.Add(tenant);
            seed.PlatformConnections.Add(n11);
            seed.Products.Add(product);
            seed.ProductVariants.Add(variant);
            seed.InventoryItems.Add(NewInventory(tenant, variant, 2m));
            seed.ChannelOffers.Add(NewOffer(tenant, n11, variant));
            seed.MarketplaceVariantLinks.Add(new MarketplaceVariantLink { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = n11.Id, VariantId = variant.Id, ExternalId = "RET-1" });
            await seed.SaveChangesAsync();
        }

        async Task<List<IntegrationJob>> ScanAsync()
        {
            await using var db = fixture.CreateContext();
            var processor = new MarketplaceJobProcessor(db, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, WritesOnConfiguration(), fixture.TimeProvider);
            var result = await processor.ProcessAsync(tenant.Id, n11.Id, MarketplaceJobTypes.StockReconciliation, "{\"lookbackHours\":1}", "stock-reconcile-failed-push", CancellationToken.None);
            Assert.True(result.Succeeded, result.ErrorSummary);
            return await db.IntegrationJobs.AsNoTracking().Where(x => x.TenantId == tenant.Id && x.JobType == MarketplaceJobTypes.PriceInventorySync).OrderBy(x => x.CreatedAt).ToListAsync();
        }
        async Task EndAsync(Guid jobId, JobStatus status, string code, TimeSpan ago)
        {
            await using var db = fixture.CreateContext();
            await db.IntegrationJobs.Where(x => x.Id == jobId).ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.Status, status).SetProperty(x => x.LastErrorCode, code).SetProperty(x => x.CompletedAt, DateTimeOffset.UtcNow - ago));
        }

        var first = Assert.Single(await ScanAsync());
        // Queued: the next scan finds it and opens nothing beside it.
        Assert.Equal(first.Id, Assert.Single(await ScanAsync()).Id);

        // Blocked before the send because the stock policy was off; the policy is on again.
        await EndAsync(first.Id, JobStatus.Blocked, "EXTERNAL_WRITE_POLICY_DISABLED", TimeSpan.FromMinutes(5));
        var afterBlock = await ScanAsync();
        Assert.Equal(2, afterBlock.Count);
        Assert.Equal($"{first.JobDedupKey}:retired:{first.Id:N}", afterBlock.Single(x => x.Id == first.Id).JobDedupKey);
        var second = afterBlock.Single(x => x.Id != first.Id);
        Assert.Equal(first.JobDedupKey, second.JobDedupKey);
        Assert.Equal(JobStatus.Pending, second.Status);

        // Dead after its retries ten minutes ago: the scan waits out the hour before it sends again.
        await EndAsync(second.Id, JobStatus.Dead, "REMOTE_5XX", TimeSpan.FromMinutes(10));
        Assert.Equal(2, (await ScanAsync()).Count);
        await EndAsync(second.Id, JobStatus.Dead, "REMOTE_5XX", TimeSpan.FromHours(2));
        var afterDead = await ScanAsync();
        Assert.Equal(3, afterDead.Count);
        Assert.Single(afterDead, x => x.JobDedupKey == first.JobDedupKey && x.Status == JobStatus.Pending);
    }

    // Q-1/K-1: the panel's send returns a queued job of the same values, but queues them again when their job failed.
    [PostgreSqlFact]
    public async Task PanelPriceStockSend_QueuesAgainAfterAFailedJobOfTheSameValues()
    {
        var tenant = NewTenant("panel-send-failed-push");
        var n11 = NewConnection(tenant, "N11", writesEnabled: true);
        var (product, variant) = NewVariant(tenant, "PAN-1", "8690000000059");
        await using (var seed = fixture.CreateContext())
        {
            seed.Tenants.Add(tenant);
            seed.PlatformConnections.Add(n11);
            seed.Products.Add(product);
            seed.ProductVariants.Add(variant);
            seed.InventoryItems.Add(NewInventory(tenant, variant, 7m));
            seed.ChannelOffers.Add(NewOffer(tenant, n11, variant));
            seed.MarketplaceVariantLinks.Add(new MarketplaceVariantLink { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = n11.Id, VariantId = variant.Id, ExternalId = "PAN-1" });
            await seed.SaveChangesAsync();
        }

        async Task<Guid> SendAsync()
        {
            await using var db = fixture.CreateContext();
            var service = new InventoryService(db, new CursorCodec(new EphemeralDataProtectionProvider(), TimeProvider.System), fixture.TimeProvider, WritesOnConfiguration());
            var result = await service.EnqueuePriceInventorySyncAsync(tenant.Id, n11.Id, $"send-{Guid.NewGuid():N}", "panel-send-failed-push", CancellationToken.None);
            Assert.True(result.Succeeded, result.Error?.Message);
            return result.Value;
        }

        var first = await SendAsync();
        Assert.Equal(first, await SendAsync());

        await using (var db = fixture.CreateContext())
            await db.IntegrationJobs.Where(x => x.Id == first).ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.Status, JobStatus.Dead).SetProperty(x => x.LastErrorCode, "REMOTE_5XX").SetProperty(x => x.CompletedAt, DateTimeOffset.UtcNow));
        var second = await SendAsync();

        Assert.NotEqual(first, second);
        await using var check = fixture.CreateContext();
        var jobs = await check.IntegrationJobs.AsNoTracking().Where(x => x.TenantId == tenant.Id && x.JobType == MarketplaceJobTypes.PriceInventorySync).ToDictionaryAsync(x => x.Id);
        Assert.Equal($"{jobs[second].JobDedupKey}:retired:{first:N}", jobs[first].JobDedupKey);
        Assert.Equal(JobStatus.Pending, jobs[second].Status);
    }

    // PTT AVM keeps a product with variants by its model code and names each variant by its colour and size: the send
    // is one product with the changed variants, priced from the lowest variant with each variant's difference. A
    // product without variants goes by its own code with its stock, and an old option-id link is not sent.
    [PostgreSqlFact]
    public async Task PttAvmPriceStock_SendsAProductWithItsVariantsByColourAndSize()
    {
        var tenant = NewTenant("pttavm-model-variant");
        var pttAvm = NewConnection(tenant, "PTTAVM", writesEnabled: true);
        var (product, small) = NewVariant(tenant, "FK4530-S", "8690000000097");
        var medium = NewVariant(tenant, "FK4530-M", "8690000000103", product.Id).Variant;
        small.OptionSignature = "Renk: Lacivert | Beden: S";
        medium.OptionSignature = "Renk: Lacivert | Beden: M";
        var (single, singleVariant) = NewVariant(tenant, "QU2410121", "QU2410121");
        var (stale, staleVariant) = NewVariant(tenant, "OLD-1", "8690000000110");
        var mediumOffer = NewOffer(tenant, pttAvm, medium);
        mediumOffer.SalePrice = 130m;
        await using (var seed = fixture.CreateContext())
        {
            seed.Tenants.Add(tenant);
            seed.PlatformConnections.Add(pttAvm);
            seed.Products.AddRange(product, single, stale);
            seed.ProductVariants.AddRange(small, medium, singleVariant, staleVariant);
            seed.InventoryItems.AddRange(NewInventory(tenant, small, 4m), NewInventory(tenant, medium, 2m), NewInventory(tenant, singleVariant, 3m), NewInventory(tenant, staleVariant, 1m));
            seed.ChannelOffers.AddRange(NewOffer(tenant, pttAvm, small), mediumOffer, NewOffer(tenant, pttAvm, singleVariant), NewOffer(tenant, pttAvm, staleVariant));
            seed.MarketplaceVariantLinks.AddRange(
                new MarketplaceVariantLink { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = pttAvm.Id, VariantId = small.Id, ExternalId = "FK4530-S" },
                new MarketplaceVariantLink { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = pttAvm.Id, VariantId = medium.Id, ExternalId = "FK4530-M" },
                new MarketplaceVariantLink { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = pttAvm.Id, VariantId = singleVariant.Id, ExternalId = "QU2410121" },
                new MarketplaceVariantLink { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = pttAvm.Id, VariantId = staleVariant.Id, ExternalId = "OLD/7101" });
            seed.MarketplaceProductLinks.AddRange(
                new MarketplaceProductLink { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = pttAvm.Id, ProductId = product.Id, ExternalId = "1523598789", ExternalCode = "FK4530" },
                new MarketplaceProductLink { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = pttAvm.Id, ProductId = single.Id, ExternalId = "1523598790", ExternalCode = "QU2410121" });
            await seed.SaveChangesAsync();
        }

        await using var db = fixture.CreateContext();
        var draft = await new PriceInventoryComposer(db).BuildAsync(tenant.Id, pttAvm.Id, CancellationToken.None);

        Assert.True(draft.Succeeded, draft.Error?.Message);
        var items = PttAvmPriceInventoryRules.Items(draft.Value!.PayloadJson)!;
        Assert.Equal(2, items.Count);
        var model = items.Single(x => x.Barcode == "FK4530");
        Assert.Equal(120.00m, model.PriceWithVat);
        Assert.Null(model.Quantity);
        Assert.Equal(2, model.Variants!.Count);
        Assert.Contains(new PttAvmPriceInventoryRules.VariantItem([new("Renk", "Lacivert"), new("Beden", "S")], 4, 0m), model.Variants);
        Assert.Contains(new PttAvmPriceInventoryRules.VariantItem([new("Renk", "Lacivert"), new("Beden", "M")], 2, 10m), model.Variants);
        Assert.Equal(new PttAvmPriceInventoryRules.Item("QU2410121", 120.00m, 20m, 3), items.Single(x => x.Barcode == "QU2410121"));
        // Each line is one variant (its own code) and answers under the product's PTT AVM barcode.
        var lines = draft.Value.Lines.ToDictionary(x => x.VariantId);
        Assert.Equal(("FK4530-S", "FK4530"), (lines[small.Id].Barcode, lines[small.Id].ExternalVariantId));
        Assert.Equal(("QU2410121", "QU2410121"), (lines[singleVariant.Id].Barcode, lines[singleVariant.Id].ExternalVariantId));
        Assert.False(lines.ContainsKey(staleVariant.Id));
        Assert.DoesNotContain("/7101", draft.Value.PayloadJson, StringComparison.Ordinal);
    }

    // An imported Trendyol variant has no listing rows; its price and stock go by the barcode Trendyol holds,
    // which differs from the panel's when the seller keyed it differently there.
    [PostgreSqlFact]
    public async Task ImportedTrendyolVariant_IsSentByTheMarketplaceBarcode()
    {
        var tenant = NewTenant("imported-trendyol-codes");
        var trendyol = NewConnection(tenant, "TRENDYOL", writesEnabled: true);
        var (product, variant) = NewVariant(tenant, "IMP-1", "8690000000110");
        var withoutCodes = NewVariant(tenant, "IMP-2", "8690000000127", product.Id).Variant;
        await using (var seed = fixture.CreateContext())
        {
            seed.Tenants.Add(tenant);
            seed.PlatformConnections.Add(trendyol);
            seed.Products.Add(product);
            seed.ProductVariants.AddRange(variant, withoutCodes);
            seed.InventoryItems.AddRange(NewInventory(tenant, variant, 3m), NewInventory(tenant, withoutCodes, 3m));
            seed.ChannelOffers.AddRange(NewOffer(tenant, trendyol, variant), NewOffer(tenant, trendyol, withoutCodes));
            seed.MarketplaceVariantLinks.AddRange(
                new MarketplaceVariantLink { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = trendyol.Id, VariantId = variant.Id, ExternalId = "777001", ExternalBarcode = "TY-BARKOD-1", ExternalSku = "TY-STOK-1" },
                // Linked before the codes were kept: nothing to address it by until the next import.
                new MarketplaceVariantLink { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = trendyol.Id, VariantId = withoutCodes.Id, ExternalId = "777002" });
            await seed.SaveChangesAsync();
        }

        await using var db = fixture.CreateContext();
        var draft = await new PriceInventoryComposer(db).BuildAsync(tenant.Id, trendyol.Id, CancellationToken.None);

        Assert.True(draft.Succeeded, draft.Error?.Message);
        var line = Assert.Single(draft.Value!.Lines);
        Assert.Equal(variant.Id, line.VariantId);
        Assert.Equal("TY-BARKOD-1", line.Barcode);
    }

    // Shopify addresses a variant by its own id: an imported variant without a barcode is still sent, keyed by it.
    [PostgreSqlFact]
    public async Task ImportedShopifyVariant_IsSentByItsVariantId()
    {
        var tenant = NewTenant("imported-shopify-variant-id");
        var shopify = NewConnection(tenant, "SHOPIFY", writesEnabled: true);
        var (product, withoutBarcode) = NewVariant(tenant, "SHP-1", "8690000000165");
        var withBarcode = NewVariant(tenant, "SHP-2", "8690000000172", product.Id).Variant;
        await using (var seed = fixture.CreateContext())
        {
            seed.Tenants.Add(tenant);
            seed.PlatformConnections.Add(shopify);
            seed.Products.Add(product);
            seed.ProductVariants.AddRange(withoutBarcode, withBarcode);
            seed.InventoryItems.AddRange(NewInventory(tenant, withoutBarcode, 2m), NewInventory(tenant, withBarcode, 5m));
            seed.ChannelOffers.AddRange(NewOffer(tenant, shopify, withoutBarcode), NewOffer(tenant, shopify, withBarcode));
            seed.MarketplaceVariantLinks.AddRange(
                new MarketplaceVariantLink { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = shopify.Id, VariantId = withoutBarcode.Id, ExternalId = "44791234567890", ExternalSku = "SHP-1" },
                new MarketplaceVariantLink { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = shopify.Id, VariantId = withBarcode.Id, ExternalId = "44791234567891", ExternalBarcode = "SH-BARKOD-2", ExternalSku = "SHP-2" });
            await seed.SaveChangesAsync();
        }

        await using var db = fixture.CreateContext();
        var draft = await new PriceInventoryComposer(db).BuildAsync(tenant.Id, shopify.Id, CancellationToken.None);

        Assert.True(draft.Succeeded, draft.Error?.Message);
        var lines = draft.Value!.Lines.ToDictionary(x => x.VariantId);
        Assert.Equal("44791234567890", lines[withoutBarcode.Id].Barcode);
        Assert.Equal("44791234567890", lines[withoutBarcode.Id].ExternalVariantId);
        Assert.Equal("SH-BARKOD-2", lines[withBarcode.Id].Barcode);
        Assert.Equal("44791234567891", lines[withBarcode.Id].ExternalVariantId);
        Assert.Contains("\"variantId\":\"44791234567890\"", draft.Value.PayloadJson, StringComparison.Ordinal);
    }

    // The product editor lists the Hepsiburada and Shopify codes of each variant.
    [PostgreSqlFact]
    public async Task ProductMarketplaceCodes_ListTheHepsiburadaAndShopifyCodesOfEachVariant()
    {
        var tenant = NewTenant("product-marketplace-codes");
        var hepsiburada = NewConnection(tenant, "HEPSIBURADA");
        var shopify = NewConnection(tenant, "SHOPIFY");
        var trendyol = NewConnection(tenant, "TRENDYOL");
        var (product, small) = NewVariant(tenant, "CODE-S", "8690000000189");
        var medium = NewVariant(tenant, "CODE-M", "8690000000196", product.Id).Variant;
        await using (var seed = fixture.CreateContext())
        {
            seed.Tenants.Add(tenant);
            seed.PlatformConnections.AddRange(hepsiburada, shopify, trendyol);
            seed.Products.Add(product);
            seed.ProductVariants.AddRange(small, medium);
            seed.MarketplaceVariantLinks.AddRange(
                new MarketplaceVariantLink { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = hepsiburada.Id, VariantId = small.Id, ExternalId = "HBCV00001ABCD", ExternalSku = "CODE-S" },
                new MarketplaceVariantLink { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = shopify.Id, VariantId = small.Id, ExternalId = "44791234567892" },
                new MarketplaceVariantLink { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = trendyol.Id, VariantId = medium.Id, ExternalId = "999" });
            await seed.SaveChangesAsync();
        }

        await using var db = fixture.CreateContext();
        var service = new CatalogService(db, new CursorCodec(new EphemeralDataProtectionProvider(), TimeProvider.System), new ConfigurationBuilder().Build(), fixture.TimeProvider, new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions()));
        var result = await service.GetProductMarketplaceCodesAsync(tenant.Id, product.Id, CancellationToken.None);

        Assert.True(result.Succeeded, result.Error?.Message);
        Assert.Equal(new[] { "HEPSIBURADA", "SHOPIFY" }, result.Value!.Connections.Select(x => x.PlatformCode));
        var smallCodes = result.Value.Variants.Single(x => x.VariantId == small.Id).Codes;
        Assert.Equal("HBCV00001ABCD", smallCodes.Single(x => x.ConnectionId == hepsiburada.Id).ExternalId);
        Assert.Equal("44791234567892", smallCodes.Single(x => x.ConnectionId == shopify.Id).ExternalId);
        // A Trendyol link is not a Hepsiburada or Shopify code.
        Assert.Empty(result.Value.Variants.Single(x => x.VariantId == medium.Id).Codes);
    }

    // Codes typed in the product editor: a changed code starts without the marketplace's barcode and stock code (the
    // next import reads them), two variants may hand a code over in one save, a code another product's variant holds is
    // refused with that variant's stock code, and an empty code removes the link.
    // An uploaded tariff is stored once per file and period, its rows match panel variants by barcode, and the
    // lookup answers the band rate only inside the period.
    [PostgreSqlFact]
    public async Task CommissionTariff_UploadListRowsLookupAndDelete()
    {
        var tenant = NewTenant("commission-tariff");
        var trendyol = NewConnection(tenant, "TRENDYOL");
        var shopify = NewConnection(tenant, "SHOPIFY");
        var (product, variant) = NewVariant(tenant, "RYP00101", "RYP00101");
        await using (var seed = fixture.CreateContext())
        {
            seed.Tenants.Add(tenant);
            seed.PlatformConnections.AddRange(trendyol, shopify);
            seed.Products.Add(product);
            seed.ProductVariants.Add(variant);
            await seed.SaveChangesAsync();
        }
        string[] headers = ["ÜRÜN İSMİ", "BARKOD", "SATICI STOK KODU", "1.Fiyat Alt Limit", "2.Fiyat Üst Limiti", "2.Fiyat Alt Limit", "3.Fiyat Üst Limiti", "3.Fiyat Alt Limit", "4.Fiyat Üst Limiti", "Tarih aralığı (7 Gün)", "1.KOMİSYON", "2.KOMİSYON", "3.KOMİSYON", "4.KOMİSYON", "KOMİSYONA ESAS FİYAT", "GÜNCEL KOMİSYON", "GÜNCEL TSF"];
        var file = CommissionTariffTests.Xlsx(headers,
        [
            ["Bluz", "RYP00101", "RYP00101", "470.64", "470.63", "435.98", "435.97", "388.64", "388.63", "22 Ekim 08.00-29 Ekim 07.59", "21.5", "20.6", "19.4", "17.5", "519.75", "21.5", "525.0"],
            ["Etek", "NOTINPANEL", "NOTINPANEL", "300", "299.99", "250", "249.99", "200", "199.99", "22 Ekim 08.00-29 Ekim 07.59", "20", "19", "18", "17", "260", "19", "270"]
        ]);
        var clock = new FixedClock(new DateTimeOffset(2026, 10, 23, 9, 0, 0, TimeSpan.Zero));

        Guid tariffId;
        await using (var db = fixture.CreateContext())
        {
            var service = new CommissionTariffService(db, clock);
            var uploaded = await service.UploadAsync(tenant.Id, null, trendyol.Id, "tarife.xlsx", new MemoryStream(file), "test", CancellationToken.None);
            Assert.True(uploaded.Succeeded, uploaded.Error?.Message);
            var tariff = Assert.Single(uploaded.Value!.Tariffs);
            Assert.Equal(2, uploaded.Value.ImportedRows);
            Assert.False(uploaded.Value.AlreadyUploaded);
            Assert.Equal(1, tariff.MatchedRowCount);
            Assert.True(tariff.IsCurrent);
            tariffId = tariff.Id;

            var refused = await service.UploadAsync(tenant.Id, null, shopify.Id, "tarife.xlsx", new MemoryStream(file), "test", CancellationToken.None);
            Assert.Equal("COMMISSION_TARIFF_PLATFORM_UNSUPPORTED", refused.Error?.Code);
        }
        await using (var db = fixture.CreateContext())
        {
            var service = new CommissionTariffService(db, clock);
            var again = await service.UploadAsync(tenant.Id, null, trendyol.Id, "kopya.xlsx", new MemoryStream(file), "test", CancellationToken.None);
            Assert.True(again.Value!.AlreadyUploaded);
            Assert.Equal(tariffId, Assert.Single(again.Value.Tariffs).Id);

            var rows = await service.RowsAsync(tenant.Id, trendyol.Id, tariffId, "ryp", 0, 50, new CommissionTariffRowFilter(null, null), CancellationToken.None);
            var row = Assert.Single(rows.Value!.Items);
            Assert.Equal(1, row.CurrentTier);
            Assert.Equal(21.5m, row.CurrentBandRate);
            Assert.Equal(470.63m, row.NextLowerBand?.Max);
            Assert.True(row.MatchesPanel);
            Assert.Equal(4, row.Bands.Count);
        }
        await using (var db = fixture.CreateContext())
        {
            var lookup = new CommissionTariffLookup(db);
            var inPeriod = new DateTimeOffset(2026, 10, 25, 12, 0, 0, TimeSpan.FromHours(3));
            Assert.Equal(20.6m, (await lookup.RateAsync(tenant.Id, trendyol.Id, "RYP00101", null, inPeriod, 450m, CancellationToken.None))?.Rate);
            Assert.Equal(17.5m, (await lookup.RateAsync(tenant.Id, trendyol.Id, "UNKNOWN", "RYP00101", inPeriod, 300m, CancellationToken.None))?.Rate);
            Assert.Null(await lookup.RateAsync(tenant.Id, trendyol.Id, "RYP00101", null, new DateTimeOffset(2026, 10, 29, 8, 0, 0, TimeSpan.FromHours(3)), 450m, CancellationToken.None));
        }
        await using (var db = fixture.CreateContext())
        {
            var deleted = await new CommissionTariffService(db, clock).DeleteAsync(tenant.Id, null, trendyol.Id, tariffId, "test", CancellationToken.None);
            Assert.True(deleted.Succeeded);
            Assert.False(await db.CommissionTariffRows.AnyAsync(x => x.TariffId == tariffId));
        }
    }

    // The tariff list is filtered by the file's name, barcode, model code, stock range, category and brand; the same file
    // uploaded again fills the category, brand and stock of rows stored before they were read.
    [PostgreSqlFact]
    public async Task CommissionTariff_FiltersByTheFilesFactsAndFillsThemOnReupload()
    {
        var tenant = NewTenant("commission-filter");
        var trendyol = NewConnection(tenant, "TRENDYOL");
        var (product, variant) = NewVariant(tenant, "RYP00101", "RYP00101");
        variant.CostPrice = 300m;
        var billedAt = new DateTimeOffset(2026, 10, 20, 9, 0, 0, TimeSpan.Zero);
        await using (var seed = fixture.CreateContext())
        {
            seed.Tenants.Add(tenant);
            seed.PlatformConnections.Add(trendyol);
            seed.Products.Add(product);
            seed.ProductVariants.Add(variant);
            // Its offer's VAT rate and a cargo invoice of the store billing a parcel at 98,34 (the store's cargo tariff).
            seed.ChannelOffers.Add(new ChannelOffer { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = trendyol.Id, VariantId = variant.Id, ListPrice = 500m, SalePrice = 465.92m, Currency = "TRY", VatRate = 10m, VatInclusion = "INCLUDED", RoundingMode = "HALF_EVEN", Status = "ACTIVE" });
            seed.MarketplaceFinanceTransactions.Add(new MarketplaceFinanceTransaction { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = trendyol.Id, PlatformCode = "TRENDYOL", Source = "OTHER_FINANCIALS", ExternalId = "KRG-1", RequestedType = "DeductionInvoices", Category = MarketplaceFinanceCategories.Cargo, TransactionDate = billedAt, RawJson = "{}", FirstSeenAt = billedAt, UpdatedAt = billedAt });
            seed.MarketplaceCargoInvoiceItems.Add(new MarketplaceCargoInvoiceItem { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = trendyol.Id, InvoiceSerialNumber = "KRG-1", LineNo = 1, ShipmentPackageType = "Gönderi Kargo Bedeli", OrderNumber = "o-1", Amount = 98.34m, RawJson = "{}", FetchedAt = billedAt });
            await seed.SaveChangesAsync();
        }
        string[] headers = ["ÜRÜN İSMİ", "BARKOD", "MODEL KODU", "KATEGORİ", "MARKA", "STOK", "1.Fiyat Alt Limit", "Tarih aralığı (7 Gün)", "1.KOMİSYON", "KOMİSYONA ESAS FİYAT"];
        var file = CommissionTariffTests.Xlsx(headers,
        [
            ["Anne Penye Bluz", "RYP00101", "RYP001", "Bluz", "Ravencia", "11.0", "470.64", "22 Ekim 08.00-29 Ekim 07.59", "21.5", "519.75"],
            ["Viskon Bluz", "MZ005S10", "MZ005S", "Bluz", "Ravencia", "0.0", "470.64", "22 Ekim 08.00-29 Ekim 07.59", "21.5", "791.01"],
            ["Uzun Etek", "ETK001", "ETK", "Etek", "Başka", "40.0", "300", "22 Ekim 08.00-29 Ekim 07.59", "20", "320"]
        ]);
        var clock = new FixedClock(new DateTimeOffset(2026, 10, 23, 9, 0, 0, TimeSpan.Zero));
        Guid tariffId;
        await using (var db = fixture.CreateContext())
        {
            tariffId = Assert.Single((await new CommissionTariffService(db, clock).UploadAsync(tenant.Id, null, trendyol.Id, "tarife.xlsx", new MemoryStream(file), "test", CancellationToken.None)).Value!.Tariffs).Id;
            // As a tariff stored before the facts were read.
            await db.CommissionTariffRows.Where(x => x.TariffId == tariffId).ExecuteUpdateAsync(set => set.SetProperty(x => x.Category, (string?)null).SetProperty(x => x.Brand, (string?)null).SetProperty(x => x.Stock, (decimal?)null));
        }
        await using (var db = fixture.CreateContext())
        {
            var service = new CommissionTariffService(db, clock);
            Assert.True((await service.UploadAsync(tenant.Id, null, trendyol.Id, "tarife.xlsx", new MemoryStream(file), "test", CancellationToken.None)).Value!.AlreadyUploaded);
            var facets = (await service.FacetsAsync(tenant.Id, trendyol.Id, tariffId, CancellationToken.None)).Value!;
            Assert.Equal(["Bluz", "Etek"], facets.Categories);
            Assert.Equal(["Başka", "Ravencia"], facets.Brands);

            async Task<string[]> Barcodes(CommissionTariffRowFilter filter) =>
                (await service.RowsAsync(tenant.Id, trendyol.Id, tariffId, null, 0, 50, filter, CancellationToken.None)).Value!.Items.Select(x => x.Barcode).Order().ToArray();
            Assert.Equal(["MZ005S10", "RYP00101"], await Barcodes(new(null, null, Category: "Bluz")));
            Assert.Equal(["ETK001"], await Barcodes(new(null, null, Brand: "Başka")));
            Assert.Equal(["RYP00101"], await Barcodes(new(null, null, Name: "penye")));
            Assert.Equal(["MZ005S10"], await Barcodes(new(null, null, ModelCode: "mz005")));
            Assert.Equal(["RYP00101"], await Barcodes(new(null, null, Barcode: "ryp001")));
            Assert.Equal(["ETK001", "RYP00101"], await Barcodes(new(null, null, MinStock: 1)));
            Assert.Equal(["MZ005S10", "RYP00101"], await Barcodes(new(null, null, MaxStock: 11)));
            // Fiyat Aralığı: RYP00101 in band 1 is 519,75 at %21,5 (111,75) over a 300 cost, 98,34 cargo, 13,19 fee, 4,73
            // withholding tax and −17,24 net VAT, leaving 8,98 (%2,99 of the cost); the rows without a cost never meet a profit range.
            Assert.Equal(["RYP00101"], await Barcodes(new(null, null, ProfitTier: 1, MinProfit: 5)));
            Assert.Empty(await Barcodes(new(null, null, ProfitTier: 1, MinProfit: 10)));
            Assert.Equal(["RYP00101"], await Barcodes(new(null, null, ProfitTier: 1, MinShare: 2.5m, MaxShare: 3)));
            Assert.Empty(await Barcodes(new(null, null, ProfitTier: 1, MaxShare: 2.5m)));
            // Models in the order a header asks for; the same order reversed, and by price.
            async Task<string[]> Models(string sort, bool descending) =>
                (await service.RowsAsync(tenant.Id, trendyol.Id, tariffId, null, 0, 50, new(null, null, Sort: sort, Descending: descending), CancellationToken.None)).Value!.Items.Select(x => x.ModelCode!).ToArray();
            Assert.Equal(["RYP001", "ETK", "MZ005S"], await Models("name", false));
            Assert.Equal(["MZ005S", "ETK", "RYP001"], await Models("name", true));
            Assert.Equal(["MZ005S", "RYP001", "ETK"], await Models("price", true));
            Assert.Equal(11m, (await service.RowsAsync(tenant.Id, trendyol.Id, tariffId, "RYP00101", 0, 50, new(null, null), CancellationToken.None)).Value!.Items.Single().Stock);

            // The panel's own stock of the variant comes before the file's, in the list and in the stock range.
            db.InventoryItems.Add(new InventoryItem { Id = Guid.CreateVersion7(), TenantId = tenant.Id, VariantId = variant.Id, LocationCode = "MAIN", OnHand = 25m, Available = 25m });
            await db.SaveChangesAsync();
            Assert.Equal(25m, (await service.RowsAsync(tenant.Id, trendyol.Id, tariffId, "RYP00101", 0, 50, new(null, null), CancellationToken.None)).Value!.Items.Single().Stock);
            Assert.Equal(["RYP00101"], await Barcodes(new(null, null, MinStock: 20, MaxStock: 30)));
        }
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    [PostgreSqlFact]
    public async Task ProductMarketplaceCodes_SaveTypedCodes()
    {
        var tenant = NewTenant("product-marketplace-codes-save");
        var hepsiburada = NewConnection(tenant, "HEPSIBURADA");
        var shopify = NewConnection(tenant, "SHOPIFY");
        var (product, small) = NewVariant(tenant, "SAVE-S", "8690000000302");
        var medium = NewVariant(tenant, "SAVE-M", "8690000000319", product.Id).Variant;
        var (otherProduct, other) = NewVariant(tenant, "OTHER-S", "8690000000326");
        await using (var seed = fixture.CreateContext())
        {
            seed.Tenants.Add(tenant);
            seed.PlatformConnections.AddRange(hepsiburada, shopify);
            seed.Products.AddRange(product, otherProduct);
            seed.ProductVariants.AddRange(small, medium, other);
            seed.MarketplaceVariantLinks.AddRange(
                new MarketplaceVariantLink { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = hepsiburada.Id, VariantId = small.Id, ExternalId = "HBCV00001ABCD", ExternalSku = "SAVE-S", ExternalBarcode = "8690000000302" },
                new MarketplaceVariantLink { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = hepsiburada.Id, VariantId = other.Id, ExternalId = "HBCV0000OTHER", ExternalSku = "OTHER-S" });
            await seed.SaveChangesAsync();
        }
        CatalogService Service(AppDbContext context) => new(context, new CursorCodec(new EphemeralDataProtectionProvider(), TimeProvider.System), new ConfigurationBuilder().Build(), fixture.TimeProvider, new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions()));

        await using (var db = fixture.CreateContext())
        {
            var saved = await Service(db).UpdateProductMarketplaceCodesAsync(tenant.Id, product.Id, new([
                new(small.Id, hepsiburada.Id, " HBCV00001NEW1 "),
                new(medium.Id, hepsiburada.Id, "HBCV00001ABCD"),
                new(small.Id, shopify.Id, "gid://shopify/ProductVariant/44791234567899"),
                new(medium.Id, shopify.Id, "")]), CancellationToken.None);

            Assert.True(saved.Succeeded, saved.Error?.Message);
            var codes = saved.Value!.Variants.ToDictionary(x => x.VariantId, x => x.Codes.ToDictionary(code => code.ConnectionId));
            Assert.Equal("HBCV00001NEW1", codes[small.Id][hepsiburada.Id].ExternalId);
            Assert.Null(codes[small.Id][hepsiburada.Id].ExternalSku);
            Assert.Equal("HBCV00001ABCD", codes[medium.Id][hepsiburada.Id].ExternalId);
            Assert.Equal("44791234567899", codes[small.Id][shopify.Id].ExternalId);
            Assert.False(codes[medium.Id].ContainsKey(shopify.Id));
        }

        await using (var db = fixture.CreateContext())
        {
            var refused = await Service(db).UpdateProductMarketplaceCodesAsync(tenant.Id, product.Id, new([new(medium.Id, hepsiburada.Id, "HBCV0000OTHER")]), CancellationToken.None);

            Assert.Equal("MARKETPLACE_CODE_IN_USE", refused.Error?.Code);
            Assert.Contains("OTHER-S", refused.Error!.Message, StringComparison.Ordinal);
            Assert.True(refused.Error.FieldErrors!.ContainsKey(CatalogService.MarketplaceCodeField(medium.Id, hepsiburada.Id)));
        }

        await using (var db = fixture.CreateContext())
        {
            var cleared = await Service(db).UpdateProductMarketplaceCodesAsync(tenant.Id, product.Id, new([new(small.Id, hepsiburada.Id, "  ")]), CancellationToken.None);

            Assert.True(cleared.Succeeded, cleared.Error?.Message);
            Assert.DoesNotContain(cleared.Value!.Variants.Single(x => x.VariantId == small.Id).Codes, code => code.ConnectionId == hepsiburada.Id);
            Assert.Equal("HBCV00001ABCD", cleared.Value.Variants.Single(x => x.VariantId == medium.Id).Codes.Single(code => code.ConnectionId == hepsiburada.Id).ExternalId);
        }
    }

    // K-3: a variant's marketplace code typed anew (a changed Shopify variant id, a first code where none was) makes
    // its offer on that connection unsent; the offer of a code left as it was keeps its marks.
    [PostgreSqlFact]
    public async Task ProductMarketplaceCodes_ChangedCodeMakesTheOfferUnsent()
    {
        var tenant = NewTenant("marketplace-code-unsent");
        var shopify = NewConnection(tenant, "SHOPIFY");
        var (product, changed) = NewVariant(tenant, "MOVE-S", "8690000000601");
        var added = NewVariant(tenant, "MOVE-M", "8690000000602", product.Id).Variant;
        var kept = NewVariant(tenant, "MOVE-L", "8690000000603", product.Id).Variant;
        ChannelOffer SentOffer(ProductVariant variant)
        {
            var offer = NewOffer(tenant, shopify, variant);
            offer.LastPriceHash = "SENT";
            offer.LastStockProjectionVersion = 1;
            return offer;
        }
        await using (var seed = fixture.CreateContext())
        {
            seed.Tenants.Add(tenant);
            seed.PlatformConnections.Add(shopify);
            seed.Products.Add(product);
            seed.ProductVariants.AddRange(changed, added, kept);
            seed.ChannelOffers.AddRange(SentOffer(changed), SentOffer(added), SentOffer(kept));
            seed.MarketplaceVariantLinks.AddRange(
                new MarketplaceVariantLink { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = shopify.Id, VariantId = changed.Id, ExternalId = "44791234560001" },
                new MarketplaceVariantLink { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = shopify.Id, VariantId = kept.Id, ExternalId = "44791234560003" });
            await seed.SaveChangesAsync();
        }

        await using (var db = fixture.CreateContext())
        {
            var service = new CatalogService(db, new CursorCodec(new EphemeralDataProtectionProvider(), TimeProvider.System), new ConfigurationBuilder().Build(), fixture.TimeProvider, new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions()));
            var saved = await service.UpdateProductMarketplaceCodesAsync(tenant.Id, product.Id, new([
                new(changed.Id, shopify.Id, "44791234560009"),
                new(added.Id, shopify.Id, "44791234560002"),
                new(kept.Id, shopify.Id, "44791234560003")]), CancellationToken.None);
            Assert.True(saved.Succeeded, saved.Error?.Message);
        }

        await using var check = fixture.CreateContext();
        var offers = await check.ChannelOffers.AsNoTracking().Where(x => x.TenantId == tenant.Id).ToDictionaryAsync(x => x.VariantId);
        Assert.Null(offers[changed.Id].LastPriceHash);
        Assert.Null(offers[changed.Id].LastStockProjectionVersion);
        Assert.Null(offers[added.Id].LastPriceHash);
        Assert.Null(offers[added.Id].LastStockProjectionVersion);
        Assert.Equal("SENT", offers[kept.Id].LastPriceHash);
        Assert.Equal(1, offers[kept.Id].LastStockProjectionVersion);
    }

    // K-3: any save that points a link elsewhere (an import or rekey changing its id or a held code, or moving it to
    // another variant) makes the offers of the old and new variant unsent. A health update, a placeholder taking the
    // marketplace's own id, a first code recorded, and marks the same save writes itself all leave the marks.
    [PostgreSqlFact]
    public async Task VariantLinkReaddressed_MakesTheOfferUnsentInTheSameSave()
    {
        var tenant = NewTenant("link-readdressed");
        var hepsiburada = NewConnection(tenant, "HEPSIBURADA");
        var product = NewVariant(tenant, "LINK-0", "8690000000700").Product;
        var variants = Enumerable.Range(1, 7).Select(index => NewVariant(tenant, $"LINK-{index}", $"869000000070{index}", product.Id).Variant).ToArray();
        var (rekeyed, moved, movedTo, barcodeChanged, healthOnly, placeholder, seeded) = (variants[0], variants[1], variants[2], variants[3], variants[4], variants[5], variants[6]);
        MarketplaceVariantLink Link(ProductVariant variant, string externalId, string? barcode = null) =>
            new() { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = hepsiburada.Id, VariantId = variant.Id, ExternalId = externalId, ExternalBarcode = barcode };
        await using (var seed = fixture.CreateContext())
        {
            seed.Tenants.Add(tenant);
            seed.PlatformConnections.Add(hepsiburada);
            seed.Products.Add(product);
            seed.ProductVariants.AddRange(variants);
            foreach (var variant in variants)
            {
                var offer = NewOffer(tenant, hepsiburada, variant);
                offer.LastPriceHash = "SENT";
                offer.LastStockProjectionVersion = 4;
                seed.ChannelOffers.Add(offer);
            }
            seed.MarketplaceVariantLinks.AddRange(
                Link(rekeyed, "LINK-1"), Link(moved, "HBCV0000MOVED"), Link(barcodeChanged, "HBCV0000BARC", "8690000000704"),
                Link(healthOnly, "HBCV0000HEALTH"), Link(placeholder, MarketplaceVariantLinkHealth.ListingPlaceholderPrefix + "LINK-6"), Link(seeded, "HBCV0000SEED"));
            await seed.SaveChangesAsync();
        }

        await using (var db = fixture.CreateContext())
        {
            var links = await db.MarketplaceVariantLinks.Where(x => x.TenantId == tenant.Id).ToDictionaryAsync(x => x.VariantId);
            links[rekeyed.Id].ExternalId = "HBCV0000REKEY";
            links[moved.Id].VariantId = movedTo.Id;
            links[barcodeChanged.Id].ExternalBarcode = "8690000000799";
            links[healthOnly.Id].LastSeenAt = DateTimeOffset.UtcNow;
            links[healthOnly.Id].ExternalBarcode = "8690000000705";
            links[placeholder.Id].ExternalId = "HBCV0000PLACE";
            links[seeded.Id].ExternalId = "HBCV0000SEED2";
            var seededOffer = await db.ChannelOffers.SingleAsync(x => x.TenantId == tenant.Id && x.VariantId == seeded.Id);
            seededOffer.LastPriceHash = "SEEDED";
            seededOffer.LastStockProjectionVersion = 5;
            await db.SaveChangesAsync();
        }

        await using var check = fixture.CreateContext();
        var offers = await check.ChannelOffers.AsNoTracking().Where(x => x.TenantId == tenant.Id).ToDictionaryAsync(x => x.VariantId);
        foreach (var unsent in new[] { rekeyed, moved, movedTo, barcodeChanged })
        {
            Assert.Null(offers[unsent.Id].LastPriceHash);
            Assert.Null(offers[unsent.Id].LastStockProjectionVersion);
        }
        Assert.Equal("SENT", offers[healthOnly.Id].LastPriceHash);
        Assert.Equal("SENT", offers[placeholder.Id].LastPriceHash);
        Assert.Equal(4, offers[placeholder.Id].LastStockProjectionVersion);
        Assert.Equal("SEEDED", offers[seeded.Id].LastPriceHash);
        Assert.Equal(5, offers[seeded.Id].LastStockProjectionVersion);

        // Removing a link leaves its offer without the address the marks named.
        await using (var db = fixture.CreateContext())
        {
            db.MarketplaceVariantLinks.Remove(await db.MarketplaceVariantLinks.SingleAsync(x => x.TenantId == tenant.Id && x.VariantId == healthOnly.Id));
            await db.SaveChangesAsync();
        }
        Assert.Null((await check.ChannelOffers.AsNoTracking().SingleAsync(x => x.TenantId == tenant.Id && x.VariantId == healthOnly.Id)).LastPriceHash);
    }

    // A PTT AVM product with variants is kept by its model code and lists only its colour and size values: the
    // panel variants of the model are linked by their own stock codes (PTT AVM's variant stock codes), an earlier
    // option-id link is rewritten, and a variant whose size PTT AVM does not list stays unlinked and is reported.
    [PostgreSqlFact]
    public async Task PttAvmModelMapping_LinksTheModelsPanelVariantsByTheirStockCodes()
    {
        var tenant = NewTenant("pttavm-model-codes");
        var pttAvm = NewConnection(tenant, "PTTAVM");
        var (product, small) = NewVariant(tenant, "M1-S", "8690000000203");
        var medium = NewVariant(tenant, "M1-M", "8690000000210", product.Id).Variant;
        var large = NewVariant(tenant, "M1-L", "8690000000227", product.Id).Variant;
        small.OptionSignature = "Renk: Lacivert | Beden: S";
        medium.OptionSignature = "Renk: Lacivert | Beden: M";
        large.OptionSignature = "Renk: Lacivert | Beden: L";
        await using (var seed = fixture.CreateContext())
        {
            seed.Tenants.Add(tenant);
            seed.PlatformConnections.Add(pttAvm);
            seed.Products.Add(product);
            seed.ProductVariants.AddRange(small, medium, large);
            seed.MarketplaceVariantLinks.AddRange(
                new MarketplaceVariantLink { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = pttAvm.Id, VariantId = medium.Id, ExternalId = "MODEL-1/7102" },
                new MarketplaceVariantLink { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = pttAvm.Id, VariantId = large.Id, ExternalId = "MODEL-1/7103" });
            await seed.SaveChangesAsync();
        }
        RemoteCatalogVariant Option(string id, string value) => new($"MODEL-1/{id}", $"MODEL-1-{value}", null, "MODEL-1", new Dictionary<string, string> { ["Varyant"] = value }, false, 100m, null, 20m, 1m, "TRY", "{}");
        var snapshot = new RemoteCatalogProduct("900001", "MODEL-1", "Mini Etek", "", null, null, null, null, [], [Option("7100", "Lacivert"), Option("7101", "S"), Option("7102", "M")], "{}");

        await using var db = fixture.CreateContext();
        var processor = new MarketplaceJobProcessor(db, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, new ConfigurationBuilder().Build(), fixture.TimeProvider);
        var map = typeof(MarketplaceJobProcessor).GetMethod("MapExistingCatalogProduct", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var mapped = await (Task<bool>)map.Invoke(processor, [tenant.Id, pttAvm.Id, snapshot, false, CancellationToken.None, true, true, false, null, "PTTAVM", true])!;
        await db.SaveChangesAsync();

        Assert.True(mapped);
        var links = await db.MarketplaceVariantLinks.AsNoTracking().Where(x => x.TenantId == tenant.Id && x.ConnectionId == pttAvm.Id).ToDictionaryAsync(x => x.VariantId);
        Assert.Equal("M1-S", links[small.Id].ExternalId);
        Assert.Equal("8690000000203", links[small.Id].ExternalBarcode);
        Assert.Equal("M1-M", links[medium.Id].ExternalId);
        Assert.Equal("M1-M", links[medium.Id].ExternalSku);
        // PTT AVM lists no size L for the model: the old option-id link is removed and the variant is reported.
        Assert.False(links.ContainsKey(large.Id));
        Assert.True(await db.OperationalIssues.AsNoTracking().AnyAsync(x => x.TenantId == tenant.Id && x.Code == "PRODUCT_MAPPING_UNMATCHED" && x.Summary.Contains("bu renk/beden yok: L")));
        var productLink = await db.MarketplaceProductLinks.AsNoTracking().SingleAsync(x => x.TenantId == tenant.Id && x.ConnectionId == pttAvm.Id);
        Assert.Equal("900001", productLink.ExternalId);
        Assert.Equal("MODEL-1", productLink.ExternalCode);
    }

    // The panel keeps one model as a product per colour (as Trendyol lists it) while PTT AVM keeps it as one product:
    // every variant is linked, and price and stock go as one product with all of the model's variants.
    [PostgreSqlFact]
    public async Task PttAvmModelSplitByColour_IsMappedAndSentAsOneProduct()
    {
        var tenant = NewTenant("pttavm-colour-products");
        var pttAvm = NewConnection(tenant, "PTTAVM", writesEnabled: true);
        var (navy, navyXl) = NewVariant(tenant, "MZDT002ECD05", "MZDT002ECD05");
        var (black, blackXl) = NewVariant(tenant, "MZDT002ECD09", "MZDT002ECD09");
        navyXl.ModelCode = blackXl.ModelCode = "MZDT002ECD";
        navyXl.OptionSignature = "Renk: Lacivert | Beden: XL";
        blackXl.OptionSignature = "Renk: Siyah | Beden: XL";
        await using (var seed = fixture.CreateContext())
        {
            seed.Tenants.Add(tenant);
            seed.PlatformConnections.Add(pttAvm);
            seed.Products.AddRange(navy, black);
            seed.ProductVariants.AddRange(navyXl, blackXl);
            seed.InventoryItems.AddRange(NewInventory(tenant, navyXl, 2m), NewInventory(tenant, blackXl, 3m));
            seed.ChannelOffers.AddRange(NewOffer(tenant, pttAvm, navyXl), NewOffer(tenant, pttAvm, blackXl));
            await seed.SaveChangesAsync();
        }
        RemoteCatalogVariant Option(string id, string value) => new($"MZDT002ECD/{id}", $"MZDT002ECD-{value}", null, "MZDT002ECD", new Dictionary<string, string> { ["Varyant"] = value }, false, 120m, null, 20m, 0m, "TRY", "{}");
        var snapshot = new RemoteCatalogProduct("1533250134", "MZDT002ECD", "Tunik", "", null, null, null, null, [], [Option("1", "Lacivert"), Option("2", "Siyah"), Option("3", "XL")], "{}");

        await using (var db = fixture.CreateContext())
        {
            var processor = new MarketplaceJobProcessor(db, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, new ConfigurationBuilder().Build(), fixture.TimeProvider);
            var map = typeof(MarketplaceJobProcessor).GetMethod("MapExistingCatalogProduct", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            Assert.True(await (Task<bool>)map.Invoke(processor, [tenant.Id, pttAvm.Id, snapshot, false, CancellationToken.None, true, true, false, null, "PTTAVM", true])!);
            await db.SaveChangesAsync();
        }

        await using var check = fixture.CreateContext();
        var links = await check.MarketplaceVariantLinks.AsNoTracking().Where(x => x.TenantId == tenant.Id && x.ConnectionId == pttAvm.Id).ToDictionaryAsync(x => x.VariantId, x => x.ExternalId);
        Assert.Equal("MZDT002ECD05", links[navyXl.Id]);
        Assert.Equal("MZDT002ECD09", links[blackXl.Id]);
        var draft = await new PriceInventoryComposer(check).BuildAsync(tenant.Id, pttAvm.Id, CancellationToken.None);
        Assert.True(draft.Succeeded, draft.Error?.Message);
        var item = Assert.Single(PttAvmPriceInventoryRules.Items(draft.Value!.PayloadJson)!);
        Assert.Equal("MZDT002ECD", item.Barcode);
        Assert.Equal(2, item.Variants!.Count);
        // A send started from one colour's product covers the whole model.
        var single = await new PriceInventoryComposer(check).BuildAsync(tenant.Id, pttAvm.Id, CancellationToken.None, blackXl.Id);
        Assert.Equal(2, Assert.Single(PttAvmPriceInventoryRules.Items(single.Value!.PayloadJson)!).Variants!.Count);
    }

    // A PTT AVM product without variants is kept by the product's own code, which may differ from its panel model code.
    [PostgreSqlFact]
    public async Task PttAvmMapping_LinksAProductWithoutVariantsByItsOwnCode()
    {
        var tenant = NewTenant("pttavm-single-code");
        var pttAvm = NewConnection(tenant, "PTTAVM");
        var (product, variant) = NewVariant(tenant, "QU2410121", "QU2410121");
        variant.ModelCode = "QUAC241012";
        await using (var seed = fixture.CreateContext())
        {
            seed.Tenants.Add(tenant);
            seed.PlatformConnections.Add(pttAvm);
            seed.Products.Add(product);
            seed.ProductVariants.Add(variant);
            await seed.SaveChangesAsync();
        }
        var remote = new RemoteCatalogVariant("QU2410121", "QU2410121", "QU2410121", null, new Dictionary<string, string>(), false, 499m, null, 10m, 3m, "TRY", "{}");
        var snapshot = new RemoteCatalogProduct("900002", "QU2410121", "Bluz", "", null, null, null, null, [], [remote], "{}");

        await using var db = fixture.CreateContext();
        var processor = new MarketplaceJobProcessor(db, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, new ConfigurationBuilder().Build(), fixture.TimeProvider);
        var map = typeof(MarketplaceJobProcessor).GetMethod("MapExistingCatalogProduct", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var mapped = await (Task<bool>)map.Invoke(processor, [tenant.Id, pttAvm.Id, snapshot, false, CancellationToken.None, true, true, false, null, "PTTAVM", true])!;
        await db.SaveChangesAsync();

        Assert.True(mapped);
        Assert.Equal("QU2410121", (await db.MarketplaceVariantLinks.AsNoTracking().SingleAsync(x => x.TenantId == tenant.Id && x.ConnectionId == pttAvm.Id)).ExternalId);
        Assert.Equal("QU2410121", (await db.MarketplaceProductLinks.AsNoTracking().SingleAsync(x => x.TenantId == tenant.Id && x.ConnectionId == pttAvm.Id)).ExternalCode);
    }

    // An import keeps the marketplace's barcode and stock code on the links it read, whatever code paired them.
    [PostgreSqlFact]
    public async Task ProductImport_RecordsTheMarketplaceCodesOnItsLinks()
    {
        var tenant = NewTenant("import-link-codes");
        var trendyol = NewConnection(tenant, "TRENDYOL");
        var (product, variant) = NewVariant(tenant, "PANEL-STOK", "8690000000158");
        await using (var seed = fixture.CreateContext())
        {
            seed.Tenants.Add(tenant);
            seed.PlatformConnections.Add(trendyol);
            seed.Products.Add(product);
            seed.ProductVariants.Add(variant);
            seed.MarketplaceVariantLinks.Add(new MarketplaceVariantLink { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = trendyol.Id, VariantId = variant.Id, ExternalId = "888001" });
            await seed.SaveChangesAsync();
        }

        await using var db = fixture.CreateContext();
        var processor = new MarketplaceJobProcessor(db, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, new ConfigurationBuilder().Build(), fixture.TimeProvider);
        var record = typeof(MarketplaceJobProcessor).GetMethod("RecordVariantLinkCodes", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var remote = new RemoteCatalogVariant("888001", " TY-STOK ", "TY-BARKOD", null, new Dictionary<string, string>(), false, 100m, 120m, 20m, 2m, "TRY", "{}");
        var snapshots = new List<RemoteCatalogProduct> { new("p-1", null, "Ürün", "", null, null, null, null, [], [remote], "{}") };
        await (Task)record.Invoke(processor, [tenant.Id, trendyol.Id, snapshots, CancellationToken.None])!;
        await db.SaveChangesAsync();

        var link = await db.MarketplaceVariantLinks.AsNoTracking().SingleAsync(x => x.TenantId == tenant.Id && x.ConnectionId == trendyol.Id);
        Assert.Equal("TY-BARKOD", link.ExternalBarcode);
        Assert.Equal("TY-STOK", link.ExternalSku);
        Assert.Equal(2, link.Version);
    }

    // A link the mapping re-keyed in memory (old id → variant code) is still found by its stored id; recording the
    // read codes passes over it instead of failing the whole product.
    [PostgreSqlFact]
    public async Task ProductImport_LinkCodesSkipALinkReKeyedInTheSameBatch()
    {
        var tenant = NewTenant("import-link-rekeyed");
        var pttAvm = NewConnection(tenant, "PTTAVM");
        var (product, variant) = NewVariant(tenant, "M1-S", "8690000000203");
        await using (var seed = fixture.CreateContext())
        {
            seed.Tenants.Add(tenant);
            seed.PlatformConnections.Add(pttAvm);
            seed.Products.Add(product);
            seed.ProductVariants.Add(variant);
            seed.MarketplaceVariantLinks.Add(new MarketplaceVariantLink { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = pttAvm.Id, VariantId = variant.Id, ExternalId = "MODEL-1/7101" });
            await seed.SaveChangesAsync();
        }

        await using var db = fixture.CreateContext();
        var link = await db.MarketplaceVariantLinks.SingleAsync(x => x.TenantId == tenant.Id && x.ConnectionId == pttAvm.Id);
        link.ExternalId = "M1-S";
        var processor = new MarketplaceJobProcessor(db, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, new ConfigurationBuilder().Build(), fixture.TimeProvider);
        var record = typeof(MarketplaceJobProcessor).GetMethod("RecordVariantLinkCodes", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var remote = new RemoteCatalogVariant("MODEL-1/7101", "MODEL-1-S", null, "MODEL-1", new Dictionary<string, string> { ["Varyant"] = "S" }, false, 100m, null, 20m, 1m, "TRY", "{}");
        var snapshots = new List<RemoteCatalogProduct> { new("900001", "MODEL-1", "Etek", "", null, null, null, null, [], [remote], "{}") };

        await (Task)record.Invoke(processor, [tenant.Id, pttAvm.Id, snapshots, CancellationToken.None])!;

        Assert.Equal("M1-S", link.ExternalId);
        Assert.Null(link.ExternalSku);
    }

    // An order line names the variant by the marketplace's codes; a link holding them finds the panel variant
    // even when the panel's barcode and stock code differ.
    [PostgreSqlFact]
    public async Task OrderLine_ResolvesThroughTheConnectionsVariantLinkCodes()
    {
        var tenant = NewTenant("order-line-link-codes");
        var n11 = NewConnection(tenant, "N11");
        var other = NewConnection(tenant, "PAZARAMA");
        var (product, linked) = NewVariant(tenant, "PANEL-1", "8690000000134");
        var panelMatch = NewVariant(tenant, "N11-STOK-9", "8690000000141", product.Id).Variant;
        await using (var seed = fixture.CreateContext())
        {
            seed.Tenants.Add(tenant);
            seed.PlatformConnections.AddRange(n11, other);
            seed.Products.Add(product);
            seed.ProductVariants.AddRange(linked, panelMatch);
            seed.MarketplaceVariantLinks.AddRange(
                new MarketplaceVariantLink { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = n11.Id, VariantId = linked.Id, ExternalId = "N11-STOK-9", ExternalBarcode = "N11-BARKOD-9", ExternalSku = "N11-STOK-9" },
                new MarketplaceVariantLink { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = other.Id, VariantId = panelMatch.Id, ExternalId = "N11-BARKOD-9" });
            await seed.SaveChangesAsync();
        }

        await using var db = fixture.CreateContext();
        var processor = new MarketplaceJobProcessor(db, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, new ConfigurationBuilder().Build(), fixture.TimeProvider);
        var resolve = typeof(MarketplaceJobProcessor).GetMethod("ResolveOrderLineVariantIds", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var lines = new List<RemoteOrderLine> { new("line-1", "N11-STOK-9", "N11-BARKOD-9", "Ürün", 1m, 100m, 20m, "Created"), new("line-2", "PANEL-ONLY", "8690000000141", "Ürün", 1m, 100m, 20m, "Created") };
        var keys = await (Task<Dictionary<string, Guid>>)resolve.Invoke(processor, [tenant.Id, n11.Id, lines, CancellationToken.None])!;

        // The n11 stock code is also a panel SKU of another variant; the n11 link wins on its own connection.
        Assert.Equal(linked.Id, keys["s:N11-STOK-9"]);
        Assert.Equal(linked.Id, keys["b:N11-BARKOD-9"]);
        // A code no link of this connection names still resolves by the panel's codes.
        Assert.Equal(panelMatch.Id, keys["b:8690000000141"]);
    }

    // K6: an order line that cannot take its units from physical stock keeps them reserved, also under a
    // DISABLED or missing connection inventory policy (the policy fields are not applied).
    [PostgreSqlFact]
    public async Task OrderShortfall_IsReservedUnderADisabledOrMissingInventoryPolicy()
    {
        var tenant = NewTenant("reservation-unconditional");
        var withDisabledPolicy = NewConnection(tenant, "TRENDYOL");
        var withoutPolicy = NewConnection(tenant, "N11");
        var (product, disabledVariant) = NewVariant(tenant, "RES-1", "8690000000073");
        var missingVariant = NewVariant(tenant, "RES-2", "8690000000080", product.Id).Variant;
        var disabledInventory = NewInventory(tenant, disabledVariant, 0m);
        var missingInventory = NewInventory(tenant, missingVariant, 0m);
        await using (var seed = fixture.CreateContext())
        {
            seed.Tenants.Add(tenant);
            seed.PlatformConnections.AddRange(withDisabledPolicy, withoutPolicy);
            seed.ConnectionInventoryPolicies.Add(new ConnectionInventoryPolicy { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = withDisabledPolicy.Id, AuthorityMode = InventoryAuthorityPolicy.Central, ReservationMode = "DISABLED", ReserveOnStatuses = "", ReleaseOnStatuses = "" });
            seed.Products.Add(product);
            seed.ProductVariants.AddRange(disabledVariant, missingVariant);
            seed.InventoryItems.AddRange(disabledInventory, missingInventory);
            await seed.SaveChangesAsync();
        }

        await using var db = fixture.CreateContext();
        var processor = new MarketplaceJobProcessor(db, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, new ConfigurationBuilder().Build(), fixture.TimeProvider);
        var project = typeof(MarketplaceJobProcessor).GetMethod("ProjectOrderReservations", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        foreach (var (connection, variant) in new[] { (withDisabledPolicy, disabledVariant), (withoutPolicy, missingVariant) })
        {
            var line = new OrderLine { Id = Guid.CreateVersion7(), TenantId = tenant.Id, OrderId = Guid.CreateVersion7(), VariantId = variant.Id, ExternalLineId = $"line-{variant.Sku}", Sku = variant.Sku, TitleSnapshot = variant.Sku, OrderedQuantity = 2m, RawStatus = "Created" };
            await (Task)project.Invoke(processor, [tenant.Id, connection.Id, new List<(OrderLine, DateTimeOffset)> { (line, fixture.Now) }, CancellationToken.None])!;
        }
        await db.SaveChangesAsync();

        foreach (var inventory in new[] { disabledInventory, missingInventory })
        {
            var reservation = await db.StockReservations.AsNoTracking().SingleAsync(x => x.TenantId == tenant.Id && x.InventoryItemId == inventory.Id);
            Assert.Equal(ReservationStatus.Active, reservation.Status);
            Assert.Equal(2m, reservation.Quantity);
            Assert.NotNull(reservation.ExpiresAt);
            Assert.Equal(2m, (await db.InventoryItems.AsNoTracking().SingleAsync(x => x.Id == inventory.Id)).Reserved);
        }
    }

    // K10: a barcode another product already lists on the same connection is refused before create.
    [PostgreSqlFact]
    public async Task Publication_RefusesABarcodeAnotherProductUsesOnTheSameConnection()
    {
        var tenant = NewTenant("barcode-duplicate");
        var trendyol = NewConnection(tenant, "TRENDYOL");
        var otherStore = NewConnection(tenant, "TRENDYOL");
        var (listedProduct, listedVariant) = NewVariant(tenant, "DUP-OLD", "8690000000042");
        var (newProduct, newVariant) = NewVariant(tenant, "DUP-NEW", "8690000000042-B");
        var profile = new ChannelListingProfile { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = trendyol.Id, ProductId = listedProduct.Id, Enabled = true, DesiredStatus = "LIVE", ActualStatus = "LIVE" };
        await using (var seed = fixture.CreateContext())
        {
            seed.Tenants.Add(tenant);
            seed.PlatformConnections.AddRange(trendyol, otherStore);
            seed.Products.AddRange(listedProduct, newProduct);
            seed.ProductVariants.AddRange(listedVariant, newVariant);
            seed.ChannelListingProfiles.Add(profile);
            seed.ChannelListingVariants.Add(new ChannelListingVariant { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ProfileId = profile.Id, VariantId = listedVariant.Id, ExternalBarcode = "8690000000042", DesiredStatus = "LIVE", ActualStatus = "LIVE" });
            await seed.SaveChangesAsync();
        }

        await using var db = fixture.CreateContext();
        Assert.Equal("8690000000042", await ProductPublicationComposer.ConnectionDuplicateAsync(db, tenant.Id, trendyol.Id, [newVariant.Id], ["8690000000042"], [], CancellationToken.None));
        // The product's own listing, and another store of the same seller, are not duplicates.
        Assert.Null(await ProductPublicationComposer.ConnectionDuplicateAsync(db, tenant.Id, trendyol.Id, [listedVariant.Id], ["8690000000042"], [], CancellationToken.None));
        Assert.Null(await ProductPublicationComposer.ConnectionDuplicateAsync(db, tenant.Id, otherStore.Id, [newVariant.Id], ["8690000000042"], [], CancellationToken.None));
    }

    // K11: an active offer without a MAIN stock row is reported instead of silently skipped.
    [PostgreSqlFact]
    public async Task PriceInventoryBuild_ReportsAnOfferWithoutInventory()
    {
        var tenant = NewTenant("inventory-required");
        var n11 = NewConnection(tenant, "N11");
        var (product, withoutInventory) = NewVariant(tenant, "INV-0", "8690000000059");
        var withInventory = NewVariant(tenant, "INV-1", "8690000000066", product.Id).Variant;
        await using (var seed = fixture.CreateContext())
        {
            seed.Tenants.Add(tenant);
            seed.PlatformConnections.Add(n11);
            seed.Products.Add(product);
            seed.ProductVariants.AddRange(withoutInventory, withInventory);
            seed.InventoryItems.Add(NewInventory(tenant, withInventory, 2m));
            seed.ChannelOffers.AddRange(NewOffer(tenant, n11, withoutInventory), NewOffer(tenant, n11, withInventory));
            seed.MarketplaceVariantLinks.AddRange(
                new MarketplaceVariantLink { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = n11.Id, VariantId = withoutInventory.Id, ExternalId = "INV-0" },
                new MarketplaceVariantLink { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = n11.Id, VariantId = withInventory.Id, ExternalId = "INV-1" });
            await seed.SaveChangesAsync();
        }

        await using var db = fixture.CreateContext();
        var composer = new PriceInventoryComposer(db);
        var single = await composer.BuildAsync(tenant.Id, n11.Id, CancellationToken.None, withoutInventory.Id);
        Assert.False(single.Succeeded);
        Assert.Equal("INVENTORY_REQUIRED", single.Error!.Code);

        var all = await composer.BuildAsync(tenant.Id, n11.Id, CancellationToken.None);
        Assert.True(all.Succeeded, all.Error?.Message);
        Assert.Equal([withInventory.Id], all.Value!.Lines.Select(x => x.VariantId));
        Assert.Equal(["INV-0"], all.Value.MissingInventorySkus);
    }

    // A model family is one Shopify product: publishing the brown card sends the black card's variants too, under
    // one handle with Renk and Beden, each colour's variants showing its card's first image; the brown card's job
    // records only its own variants.
    [PostgreSqlFact]
    public async Task ShopifyPublication_SendsTheWholeModelFamilyAsOneProduct()
    {
        var tenant = NewTenant("shopify-family");
        var shopify = NewConnection(tenant, "SHOPIFY", writesEnabled: true);
        var (black, blackL) = NewVariant(tenant, "FAM-S-L", "8690000000301");
        var blackXl = NewVariant(tenant, "FAM-S-XL", "8690000000302", black.Id).Variant;
        var (brown, brownL) = NewVariant(tenant, "FAM-K-L", "8690000000303");
        var brownXl = NewVariant(tenant, "FAM-K-XL", "8690000000304", brown.Id).Variant;
        black.Title = brown.Title = "Yonca Bluz";
        var now = DateTimeOffset.UtcNow;
        black.CreatedAt = now.AddMinutes(-5);
        brown.CreatedAt = now;
        blackXl.SortOrder = brownXl.SortOrder = 1;
        var variants = new[] { blackL, blackXl, brownL, brownXl };
        var rows = new List<object>();
        foreach (var (product, colour, cardVariants) in new[] { (black, "Siyah", new[] { blackL, blackXl }), (brown, "Kahverengi", new[] { brownL, brownXl }) })
        {
            var size = new ProductOption { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ProductId = product.Id, Label = "Beden", NormalizedKey = "BEDEN", SortOrder = 0 };
            var colourOption = new ProductOption { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ProductId = product.Id, Label = "Renk", NormalizedKey = "RENK", SortOrder = 1 };
            var colourValue = new ProductOptionValue { Id = Guid.CreateVersion7(), TenantId = tenant.Id, OptionId = colourOption.Id, Label = colour, NormalizedKey = colour.ToUpperInvariant(), SortOrder = 0 };
            rows.AddRange([size, colourOption, colourValue]);
            for (var index = 0; index < cardVariants.Length; index++)
            {
                var sizeLabel = index == 0 ? "L" : "XL";
                var sizeValue = new ProductOptionValue { Id = Guid.CreateVersion7(), TenantId = tenant.Id, OptionId = size.Id, Label = sizeLabel, NormalizedKey = sizeLabel, SortOrder = index };
                rows.AddRange([sizeValue,
                    new VariantOptionValue { Id = Guid.CreateVersion7(), TenantId = tenant.Id, VariantId = cardVariants[index].Id, OptionId = size.Id, OptionValueId = sizeValue.Id },
                    new VariantOptionValue { Id = Guid.CreateVersion7(), TenantId = tenant.Id, VariantId = cardVariants[index].Id, OptionId = colourOption.Id, OptionValueId = colourValue.Id }]);
            }
            var asset = new FileAsset { Id = Guid.CreateVersion7(), TenantId = tenant.Id, Classification = "PRODUCT_MEDIA_URL", RelativePath = $"https://cdn.example.com/{colour}.jpg", MimeType = "image/jpeg", Sha256 = Guid.NewGuid().ToString("N"), Status = "ACTIVE", CreatedAt = now };
            rows.AddRange([asset, new ProductMedia { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ProductId = product.Id, FileAssetId = asset.Id, MediaRole = "GALLERY", SortOrder = 0, Status = "ACTIVE" }]);
        }
        var profile = new ChannelListingProfile { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = shopify.Id, ProductId = brown.Id, Enabled = true, DesiredStatus = "DRAFT", ActualStatus = "DRAFT" };
        await using (var seed = fixture.CreateContext())
        {
            seed.Tenants.Add(tenant);
            seed.PlatformConnections.Add(shopify);
            seed.Products.AddRange(black, brown);
            seed.ProductVariants.AddRange(variants);
            seed.AddRange(rows);
            seed.InventoryItems.AddRange(variants.Select(variant => NewInventory(tenant, variant, 3m)));
            seed.ChannelOffers.AddRange(variants.Select(variant => NewOffer(tenant, shopify, variant)));
            seed.ChannelListingProfiles.Add(profile);
            await seed.SaveChangesAsync();
        }

        await using var db = fixture.CreateContext();
        var draft = await new ShopifyProductPublicationComposer(db, new ConfigurationBuilder().Build()).BuildAsync(tenant.Id, brown.Id, shopify.Id, CancellationToken.None);

        Assert.True(draft.Succeeded, draft.Error?.Message);
        Assert.Equal([brownL.Id, brownXl.Id], draft.Value!.Variants.Select(variant => variant.VariantId));
        using var payload = System.Text.Json.JsonDocument.Parse(draft.Value.PayloadJson);
        var root = payload.RootElement;
        Assert.StartsWith("ravencia-model-", root.GetProperty("handle").GetString(), StringComparison.Ordinal);
        Assert.Equal(["FAM-S-L", "FAM-S-XL", "FAM-K-L", "FAM-K-XL"], root.GetProperty("variants").EnumerateArray().Select(variant => variant.GetProperty("sku").GetString()));
        var options = root.GetProperty("productOptions").EnumerateArray().ToArray();
        Assert.Equal(["Beden", "Renk"], options.Select(option => option.GetProperty("name").GetString()));
        Assert.Equal(["Siyah", "Kahverengi"], options[1].GetProperty("values").EnumerateArray().Select(value => value.GetProperty("name").GetString()));
        Assert.Equal("https://cdn.example.com/Kahverengi.jpg", root.GetProperty("variants")[2].GetProperty("file").GetProperty("originalSource").GetString());
        Assert.Equal(2, root.GetProperty("files").GetArrayLength());
        var metafields = root.GetProperty("metafields").EnumerateArray().ToDictionary(item => item.GetProperty("key").GetString()!, item => item.GetProperty("value").GetString());
        Assert.Equal(black.Id.ToString("D"), metafields["ravencia_product_id"]);
        Assert.StartsWith("model:", metafields["ravencia_family_key"], StringComparison.Ordinal);
        Assert.Equal(draft.Value.PayloadHash, metafields["payload_hash"]);
    }

    // A PTT AVM model family of two colour cards (Siyah, Kahverengi) with two sizes each, ready to send but without a
    // PTT AVM price: the price comes from the cards' Trendyol offers, as in a bulk publication.
    // The connection's "Manuel Dış Yazmalar" switch (MANUAL_WRITE) is on unless the test says otherwise.
    private async Task<(Tenant Tenant, PlatformConnection PttAvm, Product Black, Product Brown, ProductVariant[] Variants)> SeedPttAvmFamilyAsync(string code, bool manualWritesEnabled = true)
    {
        var tenant = NewTenant(code);
        var pttAvm = NewConnection(tenant, "PTTAVM", writesEnabled: true);
        var trendyol = NewConnection(tenant, "TRENDYOL");
        var now = DateTimeOffset.UtcNow;
        var category = new Category { Id = Guid.CreateVersion7(), TenantId = tenant.Id, Name = "Bluz", NormalizedName = "BLUZ", Path = "Bluz", IsLeaf = true, IsActive = true, CreatedAt = now, UpdatedAt = now, Version = 1 };
        var snapshot = new ReferenceSnapshot { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = pttAvm.Id, ResourceType = "CATEGORIES", ScopeExternalId = "", SourceVersion = "categories-v1", ContentHash = "hash-categories-v1", FetchedAt = now, IsCurrent = true, ItemCount = 1 };
        var leaf = new ReferenceItem { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = pttAvm.Id, SnapshotId = snapshot.Id, ResourceType = "CATEGORIES", ExternalId = "1632", Name = "Bluz", NormalizedName = "BLUZ", Path = "Bluz", PayloadHash = "h", IsActive = true, IsLeaf = true };
        var mapping = new CategoryMapping { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = pttAvm.Id, SnapshotId = snapshot.Id, LocalId = category.Id, ExternalId = "1632", Status = "VERIFIED", VerifiedAt = now, Version = 1 };
        var rows = new List<object>();
        var variants = new List<ProductVariant>();
        Product Card(string colour, string prefix, DateTimeOffset createdAt)
        {
            var product = new Product { Id = Guid.CreateVersion7(), TenantId = tenant.Id, Title = "Yonca Bluz", Description = "Penye bluz", CategoryId = category.Id, Status = ProductStatus.Active, CreatedAt = createdAt, UpdatedAt = createdAt };
            foreach (var (size, index) in new[] { ("L", 0), ("XL", 1) })
            {
                var sku = $"{prefix}-{size}-{Guid.NewGuid():N}"[..20];
                var barcode = $"mz053{prefix.ToLowerInvariant()}{size.ToLowerInvariant()}";
                variants.Add(new ProductVariant { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ProductId = product.Id, Sku = sku, SkuNormalized = sku.ToUpperInvariant(), Barcode = barcode, BarcodeNormalized = barcode.ToUpperInvariant(), ModelCode = "MZ053TCD", OptionSignature = $"Renk: {colour} | Beden: {size}", Desi = 1m, SortOrder = index, CreatedAt = createdAt, UpdatedAt = createdAt });
            }
            var asset = new FileAsset { Id = Guid.CreateVersion7(), TenantId = tenant.Id, Classification = "PRODUCT_MEDIA_URL", RelativePath = $"https://cdn.example.com/{colour}.jpg", MimeType = "image/jpeg", Sha256 = Guid.NewGuid().ToString("N"), Status = "ACTIVE", CreatedAt = createdAt };
            rows.AddRange([asset, new ProductMedia { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ProductId = product.Id, FileAssetId = asset.Id, MediaRole = "GALLERY", SortOrder = 0, Status = "ACTIVE" }]);
            return product;
        }
        var black = Card("Siyah", "SYH", now.AddMinutes(-5));
        var brown = Card("Kahverengi", "KHV", now);
        await using var seed = fixture.CreateContext();
        seed.Tenants.Add(tenant);
        seed.PlatformConnections.AddRange(pttAvm, trendyol);
        seed.ConnectionSyncPolicies.Add(new ConnectionSyncPolicy { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = pttAvm.Id, ResourceType = MarketplaceExternalWritePolicies.Manual, IntervalSeconds = 0, Enabled = manualWritesEnabled, Version = 1 });
        seed.Categories.Add(category);
        seed.ReferenceSnapshots.Add(snapshot);
        seed.ReferenceItems.Add(leaf);
        seed.CategoryMappings.Add(mapping);
        seed.Products.AddRange(black, brown);
        seed.ProductVariants.AddRange(variants);
        seed.AddRange(rows);
        seed.InventoryItems.AddRange(variants.Select(variant => NewInventory(tenant, variant, 3m)));
        seed.ChannelOffers.AddRange(variants.Select(variant => NewOffer(tenant, trendyol, variant)));
        await seed.SaveChangesAsync();
        return (tenant, pttAvm, black, brown, [.. variants]);
    }

    private CatalogService NewCatalogService(AppDbContext db) =>
        new(db, null!, WritesOnConfiguration(), fixture.TimeProvider, new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions()));

    // Live 2026-10-29: a bulk publication of MZ053TCD's eight colour cards to PTT AVM priced and queued one card at a
    // time; the family request needs every card's PTT AVM price, so seven cards were refused (CHANNEL_OFFER_REQUIRED)
    // and only the last went out, carrying all 24 variants. Every card is now priced first, and every card is queued.
    [PostgreSqlFact]
    public async Task BulkPublication_QueuesEveryColourCardOfAPttAvmFamily()
    {
        var (tenant, pttAvm, black, brown, variants) = await SeedPttAvmFamilyAsync("pttavm-bulk-family");

        await using var db = fixture.CreateContext();
        var result = await NewCatalogService(db).EnqueueBulkPublicationAsync(tenant.Id, Guid.CreateVersion7(), new BulkPublicationCommand([black.Id, brown.Id], [pttAvm.Id]), $"bulk-{Guid.NewGuid():N}", "trace", CancellationToken.None);

        Assert.True(result.Succeeded, result.Error?.Message);
        Assert.Empty(result.Value!.Failed);
        Assert.Equal(2, result.Value.Queued);
        var jobs = await db.IntegrationJobs.AsNoTracking().Where(x => x.TenantId == tenant.Id && x.JobType == MarketplaceJobTypes.ProductCreate).ToListAsync();
        Assert.Equal(2, jobs.Count);
        Assert.Contains(jobs, job => job.JobDedupKey.StartsWith($"product-create:{pttAvm.Id:N}:{black.Id:N}:", StringComparison.Ordinal));
        Assert.Contains(jobs, job => job.JobDedupKey.StartsWith($"product-create:{pttAvm.Id:N}:{brown.Id:N}:", StringComparison.Ordinal));
        // Every card records its own variants on PTT AVM; the request of each carries the whole family.
        var listed = await db.ChannelListingVariants.AsNoTracking().Where(x => x.TenantId == tenant.Id).Select(x => x.VariantId).ToListAsync();
        Assert.Equal(variants.Select(x => x.Id).Order(), listed.Order());
        Assert.All(jobs, job => Assert.Equal(4, PttAvmProductPayloadRules.Read(PttAvmFamilySend.Read(job.PayloadJson)!.PayloadJson, out _)!.Variants!.Count));
    }

    // With PTT AVM's "Manuel Dış Yazmalar" switch off, a bulk publication queues nothing for PTT AVM and says why.
    [PostgreSqlFact]
    public async Task BulkPublication_ToPttAvmIsRefusedWhileItsManualWriteSwitchIsOff()
    {
        var (tenant, pttAvm, black, brown, _) = await SeedPttAvmFamilyAsync("pttavm-bulk-manual-off", manualWritesEnabled: false);

        await using var db = fixture.CreateContext();
        var result = await NewCatalogService(db).EnqueueBulkPublicationAsync(tenant.Id, Guid.CreateVersion7(), new BulkPublicationCommand([black.Id, brown.Id], [pttAvm.Id]), $"bulk-{Guid.NewGuid():N}", "trace", CancellationToken.None);

        Assert.True(result.Succeeded, result.Error?.Message);
        Assert.Equal(0, result.Value!.Queued);
        Assert.NotEmpty(result.Value.Failed);
        Assert.All(result.Value.Failed, failure =>
        {
            Assert.Equal(PttAvmManualWritePolicy.DisabledCode, failure.Code);
            Assert.Equal(PttAvmManualWritePolicy.DisabledMessage, failure.Message);
        });
        Assert.False(await db.IntegrationJobs.AsNoTracking().AnyAsync(x => x.TenantId == tenant.Id && x.JobType == MarketplaceJobTypes.ProductCreate));
    }

    // PTT AVM sends the family as one product, so a selected card goes with its family's other cards, as in the editor.
    [PostgreSqlFact]
    public async Task BulkPublication_OfOneCardOfAPttAvmFamilyQueuesTheWholeFamily()
    {
        var (tenant, pttAvm, black, brown, _) = await SeedPttAvmFamilyAsync("pttavm-bulk-one-card");

        await using var db = fixture.CreateContext();
        var result = await NewCatalogService(db).EnqueueBulkPublicationAsync(tenant.Id, Guid.CreateVersion7(), new BulkPublicationCommand([brown.Id], [pttAvm.Id]), $"bulk-{Guid.NewGuid():N}", "trace", CancellationToken.None);

        Assert.True(result.Succeeded, result.Error?.Message);
        Assert.Empty(result.Value!.Failed);
        Assert.Equal(2, result.Value.Queued);
        var profiles = await db.ChannelListingProfiles.AsNoTracking().Where(x => x.TenantId == tenant.Id && x.ConnectionId == pttAvm.Id).ToListAsync();
        Assert.Equal(new[] { black.Id, brown.Id }.Order(), profiles.Select(x => x.ProductId).Order());
        Assert.All(profiles, profile => Assert.Equal("QUEUED", profile.ActualStatus));
    }

    // A card the family's finished send carried without a job of its own (the MZ053TCD state on live) is taken into
    // that send by the status refresh, which then reads its variants from PTT AVM; a card the latest send left out is not.
    [PostgreSqlFact]
    public async Task StatusRefresh_TakesAPttAvmCardTheFamilysSendCarriedIntoThatSend()
    {
        var (tenant, pttAvm, black, brown, variants) = await SeedPttAvmFamilyAsync("pttavm-refresh-family");
        var now = DateTimeOffset.UtcNow;
        const string hash = "149B5C8920DCEF01D4691A5AD5E3827D9DAE4EE7898208968EEC266F175D372A";
        var blackProfile = new ChannelListingProfile { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = pttAvm.Id, ProductId = black.Id, Enabled = true, DesiredStatus = "LIVE", ActualStatus = "LIVE", Version = 3 };
        var brownProfile = new ChannelListingProfile { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = pttAvm.Id, ProductId = brown.Id, Enabled = true, DesiredStatus = "DRAFT", ActualStatus = "UNKNOWN", Version = 1 };
        var blackVariants = variants.Where(x => x.ProductId == black.Id).ToArray();
        var familyPayload = PttAvmProductPayloadRules.Payload(new PttAvmProductPayloadRules.ProductDraft("MZ053TCD", "Yonca Bluz", "Penye bluz", "1632", "MZ053TCD", 120m, 20m, 12, 1m, ["https://cdn.example.com/Siyah.jpg"],
            [.. variants.Select(x => new PttAvmProductPayloadRules.VariantDraft(x.Barcode!, x.OptionSignature!.Contains("Siyah") ? "Siyah" : "Kahverengi", x.OptionSignature.EndsWith("XL", StringComparison.Ordinal) ? "XL" : "L", 3))]));
        var sendId = Guid.CreateVersion7();
        var sendPayload = System.Text.Json.JsonSerializer.Serialize(new ProductPublicationJobPayload(sendId, black.Id, blackProfile.Id, "POLL", hash, familyPayload, "t:6ae35b6f", now.AddMinutes(-30)));
        await using (var seed = fixture.CreateContext())
        {
            seed.ChannelListingProfiles.AddRange(blackProfile, brownProfile);
            foreach (var variant in blackVariants)
            {
                seed.ChannelListingVariants.Add(new ChannelListingVariant { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ProfileId = blackProfile.Id, VariantId = variant.Id, ExternalSku = variant.Sku, ExternalBarcode = variant.Barcode, DesiredStatus = "LIVE", ActualStatus = "LIVE" });
                seed.MarketplaceListingStates.Add(new MarketplaceListingState { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = pttAvm.Id, VariantId = variant.Id, DesiredStatus = "LIVE", ActualStatus = "LIVE", PayloadHash = hash, Version = 2 });
            }
            var dedup = $"product-create:{pttAvm.Id:N}:{black.Id:N}:{hash}";
            seed.IntegrationJobs.Add(new IntegrationJob { Id = sendId, TenantId = tenant.Id, ConnectionId = pttAvm.Id, JobType = MarketplaceJobTypes.ProductCreate, PayloadJson = sendPayload, PayloadVersion = 1, PayloadHash = "job-hash", JobDedupKey = dedup, EffectIdempotencyKey = $"{dedup}:key", Priority = 10, Status = JobStatus.Succeeded, AvailableAt = now.AddMinutes(-31), MaxAttempts = 10, CorrelationId = "bulk", CreatedAt = now.AddMinutes(-31), CompletedAt = now.AddMinutes(-29), Version = 3 });
            await seed.SaveChangesAsync();
        }

        await using var db = fixture.CreateContext();
        var refreshed = await NewCatalogService(db).RequestPublicationStatusRefreshAsync(tenant.Id, black.Id, pttAvm.Id, CancellationToken.None);

        Assert.True(refreshed.Succeeded, refreshed.Error?.Message);
        db.ChangeTracker.Clear();
        var brownVariantIds = variants.Where(x => x.ProductId == brown.Id).Select(x => x.Id).ToArray();
        var listings = await db.ChannelListingVariants.AsNoTracking().Where(x => x.TenantId == tenant.Id && x.ProfileId == brownProfile.Id).ToListAsync();
        Assert.Equal(brownVariantIds.Order(), listings.Select(x => x.VariantId).Order());
        Assert.All(listings, listing => Assert.Equal("BATCH_SUBMITTED", listing.ActualStatus));
        var states = await db.MarketplaceListingStates.AsNoTracking().Where(x => x.TenantId == tenant.Id && x.ConnectionId == pttAvm.Id && brownVariantIds.Contains(x.VariantId)).ToListAsync();
        Assert.Equal(2, states.Count);
        Assert.All(states, state => Assert.Equal((hash, "BATCH_SUBMITTED"), (state.PayloadHash, state.ActualStatus)));
        Assert.Equal("BATCH_SUBMITTED", (await db.ChannelListingProfiles.AsNoTracking().SingleAsync(x => x.Id == brownProfile.Id)).ActualStatus);
        // The check that reads PTT AVM is queued for both cards; the send's result answers nothing by itself.
        var checks = await db.IntegrationJobs.AsNoTracking().Where(x => x.TenantId == tenant.Id && x.JobType == MarketplaceJobTypes.ProductApprovalReconcile).Select(x => x.JobDedupKey).ToListAsync();
        Assert.Contains($"product-approval:{pttAvm.Id:N}:{brownProfile.Id:N}:{hash}", checks);
        Assert.Contains(checks, key => key.StartsWith($"product-approval:{pttAvm.Id:N}:{blackProfile.Id:N}:{hash}", StringComparison.Ordinal));
    }

    [PostgreSqlFact]
    public async Task StatusRefresh_LeavesAPttAvmCardTheFamilysLatestSendDidNotCarry()
    {
        var (tenant, pttAvm, black, brown, variants) = await SeedPttAvmFamilyAsync("pttavm-refresh-uncarried");
        var now = DateTimeOffset.UtcNow;
        const string hash = "AB12";
        var blackVariants = variants.Where(x => x.ProductId == black.Id).ToArray();
        var blackOnly = PttAvmProductPayloadRules.Payload(new PttAvmProductPayloadRules.ProductDraft("MZ053TCD", "Yonca Bluz", "Penye bluz", "1632", "MZ053TCD", 120m, 20m, 6, 1m, ["https://cdn.example.com/Siyah.jpg"],
            [.. blackVariants.Select(x => new PttAvmProductPayloadRules.VariantDraft(x.Barcode!, "Siyah", x.OptionSignature!.EndsWith("XL", StringComparison.Ordinal) ? "XL" : "L", 3))]));
        var sendId = Guid.CreateVersion7();
        var dedup = $"product-create:{pttAvm.Id:N}:{black.Id:N}:{hash}";
        await using (var seed = fixture.CreateContext())
        {
            seed.ChannelListingProfiles.Add(new ChannelListingProfile { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = pttAvm.Id, ProductId = brown.Id, Enabled = true, DesiredStatus = "DRAFT", ActualStatus = "UNKNOWN", Version = 1 });
            seed.IntegrationJobs.Add(new IntegrationJob { Id = sendId, TenantId = tenant.Id, ConnectionId = pttAvm.Id, JobType = MarketplaceJobTypes.ProductCreate, PayloadJson = System.Text.Json.JsonSerializer.Serialize(new ProductPublicationJobPayload(sendId, black.Id, Guid.CreateVersion7(), "POLL", hash, blackOnly, "t:only-black", now.AddMinutes(-30))), PayloadVersion = 1, PayloadHash = "job-hash", JobDedupKey = dedup, EffectIdempotencyKey = $"{dedup}:key", Priority = 10, Status = JobStatus.Succeeded, AvailableAt = now.AddMinutes(-31), MaxAttempts = 10, CorrelationId = "bulk", CreatedAt = now.AddMinutes(-31), CompletedAt = now.AddMinutes(-29), Version = 3 });
            await seed.SaveChangesAsync();
        }

        await using var db = fixture.CreateContext();
        var refreshed = await NewCatalogService(db).RequestPublicationStatusRefreshAsync(tenant.Id, brown.Id, pttAvm.Id, CancellationToken.None);

        Assert.Equal("PUBLICATION_STATE_REQUIRED", refreshed.Error?.Code);
        Assert.False(await db.ChannelListingVariants.AsNoTracking().AnyAsync(x => x.TenantId == tenant.Id));
        Assert.False(await db.IntegrationJobs.AsNoTracking().AnyAsync(x => x.TenantId == tenant.Id && x.JobType == MarketplaceJobTypes.ProductApprovalReconcile));
    }

    private static IConfiguration WritesOnConfiguration() =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["FeatureFlags:ExternalWrites"] = "true" }).Build();

    private static Tenant NewTenant(string code)
    {
        var tenant = NewTenantRow(code);
        CreatedTenants.Add(tenant.Id);
        return tenant;
    }

    private static Tenant NewTenantRow(string code) => new()
    {
        Id = Guid.CreateVersion7(),
        Code = $"{code}-{Guid.NewGuid():N}",
        DisplayName = code,
        Status = RecordStatus.Active,
        Timezone = "Europe/Istanbul",
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
        Version = 1
    };

    private static PlatformConnection NewConnection(Tenant tenant, string platformCode, bool writesEnabled = false) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = tenant.Id,
        PublicId = Guid.CreateVersion7(),
        PlatformCode = platformCode,
        Environment = "PRODUCTION",
        DisplayName = $"{platformCode} stok testi",
        ExternalStoreId = $"store-{Guid.NewGuid():N}",
        Status = "ACTIVE",
        ApiVersion = "V1",
        SettingsJson = writesEnabled ? "{\"ExternalWritesEnabled\":true}" : "{}"
    };

    private static (Product Product, ProductVariant Variant) NewVariant(Tenant tenant, string sku, string barcode, Guid? productId = null)
    {
        var now = DateTimeOffset.UtcNow;
        var product = new Product { Id = productId ?? Guid.CreateVersion7(), TenantId = tenant.Id, Title = $"Ürün {sku}", Description = "Stok testi", Status = ProductStatus.Active, CreatedAt = now, UpdatedAt = now };
        var variant = new ProductVariant
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenant.Id,
            ProductId = product.Id,
            Sku = sku,
            SkuNormalized = sku.ToUpperInvariant(),
            Barcode = barcode,
            BarcodeNormalized = barcode.ToUpperInvariant(),
            ModelCode = "MODEL-1",
            OptionSignature = sku,
            CreatedAt = now,
            UpdatedAt = now
        };
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

    private static ChannelOffer NewOffer(Tenant tenant, PlatformConnection connection, ProductVariant variant, string status = "ACTIVE", string currency = "TRY") => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = tenant.Id,
        ConnectionId = connection.Id,
        VariantId = variant.Id,
        ListPrice = 150m,
        SalePrice = 120m,
        Currency = currency,
        VatRate = 20m,
        VatInclusion = "INCLUDED",
        RoundingMode = "HALF_EVEN",
        Status = status
    };
}
