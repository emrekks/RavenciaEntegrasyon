using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// The automatic price/stock pipeline, owner decisions A-J of 30 Oct 2026 (stock push pipeline).
[Collection("postgres-integration")]
public sealed class StockPushPipelinePostgreSqlTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>, IAsyncLifetime
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

    // E + A: a price edit queues a dispatch for its own connection, and only while that connection's automatic writes are open.
    [PostgreSqlFact]
    public async Task PriceEdit_QueuesADispatchOnlyWhereAutomaticWritesAreOpen()
    {
        var tenant = NewTenant("price-edit-dispatch");
        var open = NewConnection(tenant, "N11");
        var closed = NewConnection(tenant, "PAZARAMA");
        var (product, variant) = NewVariant(tenant, "PE-1");
        var openOffer = NewOffer(tenant, open, variant);
        var closedOffer = NewOffer(tenant, closed, variant);
        await using (var seed = fixture.CreateContext())
        {
            seed.Tenants.Add(tenant);
            seed.PlatformConnections.AddRange(open, closed);
            seed.ConnectionSyncPolicies.AddRange(WritePolicies(tenant, open, true));
            seed.ConnectionSyncPolicies.AddRange(WritePolicies(tenant, closed, false));
            seed.Products.Add(product);
            seed.ProductVariants.Add(variant);
            seed.InventoryItems.Add(NewInventory(tenant, variant, 5m));
            seed.ChannelOffers.AddRange(openOffer, closedOffer);
            await seed.SaveChangesAsync();
        }

        await using var db = fixture.CreateContext();
        var service = new InventoryService(db, new CursorCodec(new EphemeralDataProtectionProvider(), TimeProvider.System), fixture.TimeProvider, WritesOn());
        foreach (var offer in new[] { openOffer, closedOffer })
        {
            var result = await service.UpdateOfferAsync(tenant.Id, Guid.CreateVersion7(), offer.Id, offer.Version, new(160m, 130m, "TRY", 20m, "INCLUDED", "HALF_EVEN", 0m, "ACTIVE", "Fiyat düzenleme"), CancellationToken.None);
            Assert.True(result.Succeeded, result.Error?.Message);
        }

        var dispatches = await db.IntegrationJobs.AsNoTracking().Where(x => x.TenantId == tenant.Id && x.JobType == MarketplaceJobTypes.StockProjectionDispatch).ToListAsync();
        var dispatch = Assert.Single(dispatches);
        Assert.Equal(open.Id, dispatch.ConnectionId);
        Assert.Equal(StockProjectionOutboxPolicy.PriceChangeDedupKey(open.Id, variant.Id, 2), dispatch.JobDedupKey);
    }

    // B: switching the automatic price and stock writes on marks every offer of the connection as sent at its current
    // values, on every platform's own price mark, so nothing changed while they were off goes out; a later change does.
    [PostgreSqlFact]
    public async Task EnablingAutomaticWrites_SendsOnlyTheChangesMadeAfterwards()
    {
        var tenant = NewTenant("baseline-on-enable");
        var connections = new[] { NewConnection(tenant, "N11"), NewConnection(tenant, "PAZARAMA"), NewConnection(tenant, "PTTAVM"), NewConnection(tenant, "TRENDYOL") };
        var variants = new List<(PlatformConnection Connection, ProductVariant Variant, InventoryItem Inventory)>();
        await using (var seed = fixture.CreateContext())
        {
            seed.Tenants.Add(tenant);
            seed.PlatformConnections.AddRange(connections);
            foreach (var connection in connections)
            {
                seed.ConnectionSyncPolicies.AddRange(WritePolicies(tenant, connection, false));
                var (product, variant) = NewVariant(tenant, $"BL-{connection.PlatformCode}");
                var inventory = NewInventory(tenant, variant, 7m);
                inventory.ProjectionVersion = 5;
                seed.Products.Add(product);
                seed.ProductVariants.Add(variant);
                seed.InventoryItems.Add(inventory);
                // Changed while the writes were off: neither the price nor the stock was ever sent.
                seed.ChannelOffers.Add(NewOffer(tenant, connection, variant));
                seed.MarketplaceVariantLinks.Add(new MarketplaceVariantLink { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, VariantId = variant.Id, ExternalId = $"code-{variant.Sku}", ExternalBarcode = $"8690{Math.Abs(variant.Sku.GetHashCode()) % 100000000:D8}" });
                variants.Add((connection, variant, inventory));
            }
            await seed.SaveChangesAsync();
        }

        foreach (var connection in connections)
        {
            await using var db = fixture.CreateContext();
            var dataProtection = new EphemeralDataProtectionProvider();
            var service = new MarketplaceConnectionService(db, new CursorCodec(dataProtection, fixture.TimeProvider), dataProtection, fixture.TokenHasher, fixture.TimeProvider, WritesOn());
            foreach (var resourceType in new[] { MarketplaceExternalWritePolicies.Price, MarketplaceExternalWritePolicies.Stock })
            {
                var result = await service.UpsertSyncPolicyAsync(tenant.Id, connection.Id, resourceType, 1, new(0, 0, 0, true), CancellationToken.None);
                Assert.True(result.Succeeded, result.Error?.Message);
            }
        }

        foreach (var (connection, variant, _) in variants)
        {
            await using var db = fixture.CreateContext();
            var offer = await db.ChannelOffers.AsNoTracking().SingleAsync(x => x.TenantId == tenant.Id && x.ConnectionId == connection.Id);
            Assert.True(offer.LastPriceHash == PriceInventoryComposer.CurrentPriceHash(connection.PlatformCode, offer) && offer.LastStockProjectionVersion == 5,
                $"{connection.PlatformCode}: mark {offer.LastPriceHash}/{offer.LastStockProjectionVersion}, current {PriceInventoryComposer.CurrentPriceHash(connection.PlatformCode, offer)}/5");
            var build = await new PriceInventoryComposer(db).BuildAsync(tenant.Id, connection.Id, CancellationToken.None, automatic: true);
            Assert.False(build.Succeeded, $"{connection.PlatformCode}: {string.Join(", ", build.Value?.Lines.Select(line => $"{line.Barcode} {line.PriceHash} v{line.ProjectionVersion}") ?? [])}");
            Assert.Equal("NO_EXTERNAL_CHANGES", build.Error!.Code);
        }

        // A stock change after enabling reaches every connection.
        await using (var db = fixture.CreateContext())
        {
            var service = new InventoryService(db, new CursorCodec(new EphemeralDataProtectionProvider(), TimeProvider.System), fixture.TimeProvider, WritesOn());
            foreach (var (_, variant, _) in variants)
            {
                var result = await service.SetOnHandAsync(tenant.Id, Guid.CreateVersion7(), variant.Id, new StockSetCommand(9m, 7m, "Sayım", $"count-{variant.Sku}"), $"set-{Guid.NewGuid():N}", "baseline", CancellationToken.None);
                Assert.True(result.Succeeded, result.Error?.Message);
            }
            var dispatched = await db.IntegrationJobs.AsNoTracking().Where(x => x.TenantId == tenant.Id && x.JobType == MarketplaceJobTypes.StockProjectionDispatch).Select(x => x.ConnectionId).ToListAsync();
            Assert.Equal(connections.Select(x => (Guid?)x.Id).OrderBy(x => x), dispatched.OrderBy(x => x));
            foreach (var (connection, _, _) in variants)
            {
                var build = await new PriceInventoryComposer(db).BuildAsync(tenant.Id, connection.Id, CancellationToken.None, automatic: true);
                Assert.True(build.Succeeded, $"{connection.PlatformCode}: {build.Error?.Code}");
                Assert.All(build.Value!.Lines, line => Assert.Equal(9m, line.Quantity));
            }
        }
    }

    // C: the user sets 15 while an order of the marketplace is not read yet. The send waits for an order read that
    // started after the change; that read takes the order off (14), and 14 is what goes out.
    [PostgreSqlFact]
    public async Task StockSend_WaitsForTheOrderReadAndSendsTheStockLeftAfterIt()
    {
        var tenant = NewTenant("order-freshness");
        var n11 = NewConnection(tenant, "N11");
        var (product, variant) = NewVariant(tenant, "OF-1");
        var inventory = NewInventory(tenant, variant, 15m);
        inventory.ProjectionVersion = 2;
        var changedAt = DateTimeOffset.UtcNow.AddSeconds(-30);
        var dispatchId = Guid.CreateVersion7();
        await using (var seed = fixture.CreateContext())
        {
            seed.Tenants.Add(tenant);
            seed.PlatformConnections.Add(n11);
            seed.ConnectionSyncPolicies.AddRange(WritePolicies(tenant, n11, true));
            seed.ConnectionSyncPolicies.Add(new ConnectionSyncPolicy { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = n11.Id, ResourceType = "ORDERS", IntervalSeconds = 180, Enabled = true, Version = 1 });
            // The last order read ran before the change.
            seed.SyncCursors.Add(new SyncCursor { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = n11.Id, ResourceType = OrderFreshnessGatePolicy.OrderReadCursor, LastAttemptAt = changedAt.AddSeconds(-60), LastSuccessAt = changedAt.AddSeconds(-55), Version = 1 });
            seed.Products.Add(product);
            seed.ProductVariants.Add(variant);
            seed.InventoryItems.Add(inventory);
            seed.ChannelOffers.Add(NewOffer(tenant, n11, variant));
            seed.MarketplaceVariantLinks.Add(new MarketplaceVariantLink { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = n11.Id, VariantId = variant.Id, ExternalId = "OF-1" });
            seed.IntegrationJobs.Add(DispatchJob(tenant, n11, variant, dispatchId, changedAt));
            await seed.SaveChangesAsync();
        }

        async Task<JobExecutionResult> DispatchAsync()
        {
            await using var db = fixture.CreateContext();
            var processor = new MarketplaceJobProcessor(db, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, WritesOn(), fixture.TimeProvider);
            return await processor.ProcessAsync(tenant.Id, n11.Id, MarketplaceJobTypes.StockProjectionDispatch, JsonSerializer.Serialize(new { variantId = variant.Id }), "order-freshness", CancellationToken.None, dispatchId);
        }

        var waiting = await DispatchAsync();
        Assert.Equal(OrderFreshnessGatePolicy.WaitCode, waiting.ErrorCode);
        Assert.True(waiting.RefundAttempt);
        await using (var check = fixture.CreateContext())
            Assert.False(await check.IntegrationJobs.AnyAsync(x => x.TenantId == tenant.Id && x.JobType == MarketplaceJobTypes.PriceInventorySync));

        // The order read that started after the change takes the order placed before it: 15 - 1 = 14.
        await using (var orders = fixture.CreateContext())
        {
            var item = await orders.InventoryItems.SingleAsync(x => x.Id == inventory.Id);
            item.OnHand = 14m; item.Available = 14m; item.ProjectionVersion++; item.Version++;
            var cursor = await orders.SyncCursors.SingleAsync(x => x.TenantId == tenant.Id && x.ConnectionId == n11.Id && x.ResourceType == OrderFreshnessGatePolicy.OrderReadCursor);
            cursor.LastAttemptAt = changedAt.AddSeconds(10);
            cursor.LastSuccessAt = changedAt.AddSeconds(12);
            cursor.Version++;
            await orders.SaveChangesAsync();
        }

        var sent = await DispatchAsync();
        Assert.True(sent.Succeeded, sent.ErrorSummary);
        await using var db2 = fixture.CreateContext();
        var push = await db2.IntegrationJobs.AsNoTracking().SingleAsync(x => x.TenantId == tenant.Id && x.JobType == MarketplaceJobTypes.PriceInventorySync);
        var payload = JsonSerializer.Deserialize<PriceInventoryJobPayload>(push.PayloadJson, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.True(payload.Automatic);
        Assert.Equal(14m, Assert.Single(payload.Lines).Quantity);
    }

    // C: the wait is bounded; a connection whose order read is late is sent to after the bound.
    [PostgreSqlFact]
    public async Task StockSend_GoesAfterTheWaitBoundWhenTheOrderReadIsLate()
    {
        var tenant = NewTenant("order-freshness-bound");
        var n11 = NewConnection(tenant, "N11");
        var (product, variant) = NewVariant(tenant, "OFB-1");
        var changedAt = DateTimeOffset.UtcNow.AddMinutes(-11);
        var dispatchId = Guid.CreateVersion7();
        await using (var seed = fixture.CreateContext())
        {
            seed.Tenants.Add(tenant);
            seed.PlatformConnections.Add(n11);
            seed.ConnectionSyncPolicies.AddRange(WritePolicies(tenant, n11, true));
            seed.ConnectionSyncPolicies.Add(new ConnectionSyncPolicy { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = n11.Id, ResourceType = "ORDERS", IntervalSeconds = 180, Enabled = true, Version = 1 });
            seed.SyncCursors.Add(new SyncCursor { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = n11.Id, ResourceType = OrderFreshnessGatePolicy.OrderReadCursor, LastAttemptAt = changedAt.AddMinutes(-1), LastSuccessAt = changedAt.AddMinutes(-1), Version = 1 });
            seed.Products.Add(product);
            seed.ProductVariants.Add(variant);
            seed.InventoryItems.Add(NewInventory(tenant, variant, 3m));
            seed.ChannelOffers.Add(NewOffer(tenant, n11, variant));
            seed.MarketplaceVariantLinks.Add(new MarketplaceVariantLink { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = n11.Id, VariantId = variant.Id, ExternalId = "OFB-1" });
            seed.IntegrationJobs.Add(DispatchJob(tenant, n11, variant, dispatchId, changedAt));
            await seed.SaveChangesAsync();
        }

        await using var db = fixture.CreateContext();
        var processor = new MarketplaceJobProcessor(db, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, WritesOn(), fixture.TimeProvider);
        var result = await processor.ProcessAsync(tenant.Id, n11.Id, MarketplaceJobTypes.StockProjectionDispatch, JsonSerializer.Serialize(new { variantId = variant.Id }), "order-freshness-bound", CancellationToken.None, dispatchId);
        Assert.True(result.Succeeded, result.ErrorSummary);
        Assert.True(await db.IntegrationJobs.AnyAsync(x => x.TenantId == tenant.Id && x.JobType == MarketplaceJobTypes.PriceInventorySync));
    }

    // C, webhook first: with healthy order notifications the send does not wait for the (eight-minute) scheduled read;
    // once the notifications have had time to arrive and the received ones are ingested, it goes.
    [PostgreSqlFact]
    public async Task StockSend_WithHealthyNotificationsGoesOnceTheyAreIngested()
    {
        var tenant = NewTenant("order-freshness-webhook");
        var connection = NewConnection(tenant, "N11");
        var (product, variant) = NewVariant(tenant, "OFW-1");
        var now = DateTimeOffset.UtcNow;
        var changedAt = now.AddMinutes(-3);
        var dispatchId = Guid.CreateVersion7();
        var ingestId = Guid.CreateVersion7();
        await using (var seed = fixture.CreateContext())
        {
            seed.Tenants.Add(tenant);
            seed.PlatformConnections.Add(connection);
            seed.ConnectionSyncPolicies.AddRange(WritePolicies(tenant, connection, true));
            seed.ConnectionSyncPolicies.Add(new ConnectionSyncPolicy { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, ResourceType = "ORDERS", IntervalSeconds = 120, Enabled = true, Version = 1 });
            // The last scheduled read ran before the change; the notifications are healthy (delivered, no unnotified order).
            seed.SyncCursors.Add(new SyncCursor { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, ResourceType = OrderFreshnessGatePolicy.OrderReadCursor, LastAttemptAt = changedAt.AddMinutes(-4), LastSuccessAt = changedAt.AddMinutes(-4), Version = 1 });
            seed.WebhookSubscriptions.Add(new WebhookSubscription { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, RouteTokenHash = $"ofw-{Guid.NewGuid():N}", AuthenticationType = "BASIC_AUTH", ProtectedVerifierSecret = "secret", ExternalSubscriptionId = "wh-1", Status = "ACTIVE", VerifiedAt = now.AddDays(-2), LastReceivedAt = now.AddSeconds(-20) });
            seed.Products.Add(product);
            seed.ProductVariants.Add(variant);
            seed.InventoryItems.Add(NewInventory(tenant, variant, 4m));
            seed.ChannelOffers.Add(NewOffer(tenant, connection, variant));
            seed.MarketplaceVariantLinks.Add(new MarketplaceVariantLink { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, VariantId = variant.Id, ExternalId = "OFW-1" });
            seed.IntegrationJobs.Add(DispatchJob(tenant, connection, variant, dispatchId, changedAt));
            // A notification received seconds ago is still waiting to be ingested.
            var ingest = DispatchJob(tenant, connection, variant, ingestId, now.AddSeconds(-20));
            ingest.JobType = MarketplaceJobTypes.WebhookIngest;
            ingest.Status = JobStatus.Pending;
            seed.IntegrationJobs.Add(ingest);
            await seed.SaveChangesAsync();
        }

        async Task<JobExecutionResult> DispatchAsync()
        {
            await using var db = fixture.CreateContext();
            var processor = new MarketplaceJobProcessor(db, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, WritesOn(), fixture.TimeProvider);
            return await processor.ProcessAsync(tenant.Id, connection.Id, MarketplaceJobTypes.StockProjectionDispatch, JsonSerializer.Serialize(new { variantId = variant.Id }), "order-freshness-webhook", CancellationToken.None, dispatchId);
        }

        Assert.Equal(OrderFreshnessGatePolicy.WaitCode, (await DispatchAsync()).ErrorCode);

        await using (var ingested = fixture.CreateContext())
        {
            var job = await ingested.IntegrationJobs.SingleAsync(x => x.Id == ingestId);
            job.Status = JobStatus.Succeeded;
            job.CompletedAt = DateTimeOffset.UtcNow;
            job.Version++;
            await ingested.SaveChangesAsync();
        }

        var sent = await DispatchAsync();
        Assert.True(sent.Succeeded, sent.ErrorSummary);
        await using var check = fixture.CreateContext();
        Assert.True(await check.IntegrationJobs.AnyAsync(x => x.TenantId == tenant.Id && x.JobType == MarketplaceJobTypes.PriceInventorySync));
    }

    // I: an automatic send leaves out a code the marketplace no longer knows (NOT_FOUND) and one it refused twice
    // (REJECTED), also for a single variant; only a send started from the panel tries a REJECTED code again. A link a
    // scan saw again (healed) is sent once more.
    [PostgreSqlFact]
    public async Task AutomaticSend_SkipsBrokenCodesAndResumesAHealedOne()
    {
        var tenant = NewTenant("link-health-push");
        var n11 = NewConnection(tenant, "N11");
        var (product, rejected) = NewVariant(tenant, "LH-REJ");
        var notFound = NewVariant(tenant, "LH-NF", product.Id).Variant;
        var rejectedLink = new MarketplaceVariantLink { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = n11.Id, VariantId = rejected.Id, ExternalId = "LH-REJ", HealthStatus = MarketplaceVariantLinkHealth.Rejected };
        await using (var seed = fixture.CreateContext())
        {
            seed.Tenants.Add(tenant);
            seed.PlatformConnections.Add(n11);
            seed.ConnectionSyncPolicies.AddRange(WritePolicies(tenant, n11, true));
            seed.Products.Add(product);
            seed.ProductVariants.AddRange(rejected, notFound);
            seed.InventoryItems.AddRange(NewInventory(tenant, rejected, 2m), NewInventory(tenant, notFound, 2m));
            seed.ChannelOffers.AddRange(NewOffer(tenant, n11, rejected), NewOffer(tenant, n11, notFound));
            seed.MarketplaceVariantLinks.AddRange(rejectedLink, new MarketplaceVariantLink { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = n11.Id, VariantId = notFound.Id, ExternalId = "LH-NF", HealthStatus = MarketplaceVariantLinkHealth.NotFound });
            await seed.SaveChangesAsync();
        }

        await using (var db = fixture.CreateContext())
        {
            var composer = new PriceInventoryComposer(db);
            Assert.Equal("NO_EXTERNAL_CHANGES", (await composer.BuildAsync(tenant.Id, n11.Id, CancellationToken.None, rejected.Id, automatic: true)).Error?.Code);
            Assert.Equal("NO_EXTERNAL_CHANGES", (await composer.BuildAsync(tenant.Id, n11.Id, CancellationToken.None, notFound.Id, automatic: true)).Error?.Code);
            Assert.Equal("NO_EXTERNAL_CHANGES", (await composer.BuildAsync(tenant.Id, n11.Id, CancellationToken.None, automatic: true)).Error?.Code);
            // The panel's send of that one variant still tries the twice-refused code.
            Assert.True((await composer.BuildAsync(tenant.Id, n11.Id, CancellationToken.None, rejected.Id)).Succeeded);
            Assert.Equal("NO_EXTERNAL_CHANGES", (await composer.BuildAsync(tenant.Id, n11.Id, CancellationToken.None, notFound.Id)).Error?.Code);
        }

        await using (var heal = fixture.CreateContext())
        {
            var link = await heal.MarketplaceVariantLinks.SingleAsync(x => x.Id == rejectedLink.Id);
            MarketplaceVariantLinkHealth.MarkSeen(link, DateTimeOffset.UtcNow);
            await heal.SaveChangesAsync();
        }
        await using var after = fixture.CreateContext();
        var healed = await new PriceInventoryComposer(after).BuildAsync(tenant.Id, n11.Id, CancellationToken.None, automatic: true);
        Assert.True(healed.Succeeded, healed.Error?.Code);
        Assert.Equal(rejected.Id, Assert.Single(healed.Value!.Lines).VariantId);
    }

    // G: an offer the automatic send cannot take (price 0) is left out of the batch and shown as an issue, while the
    // other offers still go; the monitor counts it; the emergency stop turns the writes off and cancels the queue.
    [PostgreSqlFact]
    public async Task InvalidOffer_IsReportedWhileTheBatchGoesAndTheEmergencyStopEmptiesTheQueue()
    {
        var tenant = NewTenant("monitor-and-stop");
        var n11 = NewConnection(tenant, "N11");
        var (product, valid) = NewVariant(tenant, "MS-OK");
        var zero = NewVariant(tenant, "MS-ZERO", product.Id).Variant;
        var zeroOffer = NewOffer(tenant, n11, zero);
        zeroOffer.SalePrice = 0m;
        await using (var seed = fixture.CreateContext())
        {
            seed.Tenants.Add(tenant);
            seed.PlatformConnections.Add(n11);
            seed.ConnectionSyncPolicies.AddRange(WritePolicies(tenant, n11, true));
            seed.Products.Add(product);
            seed.ProductVariants.AddRange(valid, zero);
            seed.InventoryItems.AddRange(NewInventory(tenant, valid, 4m), NewInventory(tenant, zero, 4m));
            seed.ChannelOffers.AddRange(NewOffer(tenant, n11, valid), zeroOffer);
            seed.MarketplaceVariantLinks.AddRange(
                new MarketplaceVariantLink { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = n11.Id, VariantId = valid.Id, ExternalId = "MS-OK" },
                new MarketplaceVariantLink { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = n11.Id, VariantId = zero.Id, ExternalId = "MS-ZERO", HealthStatus = null });
            await seed.SaveChangesAsync();
        }

        await using (var db = fixture.CreateContext())
        {
            var processor = new MarketplaceJobProcessor(db, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, WritesOn(), fixture.TimeProvider);
            var result = await processor.ProcessAsync(tenant.Id, n11.Id, MarketplaceJobTypes.StockReconciliation, "{}", "monitor-and-stop", CancellationToken.None);
            Assert.True(result.Succeeded, result.ErrorSummary);
            var push = await db.IntegrationJobs.AsNoTracking().SingleAsync(x => x.TenantId == tenant.Id && x.JobType == MarketplaceJobTypes.PriceInventorySync);
            Assert.Contains(valid.Id.ToString(), push.PayloadJson, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(zero.Id.ToString(), push.PayloadJson, StringComparison.OrdinalIgnoreCase);
            // A second pass waits for the push still on its way.
            Assert.True((await processor.ProcessAsync(tenant.Id, n11.Id, MarketplaceJobTypes.StockReconciliation, "{}", "monitor-and-stop", CancellationToken.None)).Succeeded);
            Assert.Equal(1, await db.IntegrationJobs.CountAsync(x => x.TenantId == tenant.Id && x.JobType == MarketplaceJobTypes.PriceInventorySync));
        }

        await using (var db = fixture.CreateContext())
        {
            var monitor = new PriceStockMonitorService(db, WritesOn(), fixture.TimeProvider);
            var row = Assert.Single((await monitor.MonitorAsync(tenant.Id, CancellationToken.None)).Connections);
            Assert.True(row.AutomaticWritesOpen);
            Assert.Equal(1, row.InvalidOffers);
            Assert.Equal(1, row.QueuedJobs);

            var stop = await monitor.StopAllAutomaticWritesAsync(tenant.Id, Guid.CreateVersion7(), "emergency-stop", CancellationToken.None);
            Assert.Equal(1, stop.ConnectionsStopped);
            Assert.Equal(2, stop.PoliciesDisabled);
            Assert.Equal(1, stop.JobsCancelled);
        }

        await using var check = fixture.CreateContext();
        Assert.False(await check.ConnectionSyncPolicies.AnyAsync(x => x.TenantId == tenant.Id && x.Enabled));
        Assert.Equal(JobStatus.Cancelled, (await check.IntegrationJobs.SingleAsync(x => x.TenantId == tenant.Id && x.JobType == MarketplaceJobTypes.PriceInventorySync)).Status);
        var after = Assert.Single((await new PriceStockMonitorService(check, WritesOn(), fixture.TimeProvider).MonitorAsync(tenant.Id, CancellationToken.None)).Connections);
        Assert.False(after.AutomaticWritesOpen);
        Assert.Equal(0, after.QueuedJobs);
    }

    private static IntegrationJob DispatchJob(Tenant tenant, PlatformConnection connection, ProductVariant variant, Guid id, DateTimeOffset createdAt) => new()
    {
        Id = id,
        TenantId = tenant.Id,
        ConnectionId = connection.Id,
        JobType = MarketplaceJobTypes.StockProjectionDispatch,
        PayloadJson = JsonSerializer.Serialize(new { variantId = variant.Id }),
        PayloadVersion = 1,
        PayloadHash = "h",
        JobDedupKey = $"test-dispatch:{id:N}",
        EffectIdempotencyKey = $"test-dispatch:{id:N}",
        Status = JobStatus.Leased,
        AvailableAt = createdAt,
        CorrelationId = "test",
        CreatedAt = createdAt,
        Version = 1
    };

    private static IConfiguration WritesOn() =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["FeatureFlags:ExternalWrites"] = "true" }).Build();

    private static IEnumerable<ConnectionSyncPolicy> WritePolicies(Tenant tenant, PlatformConnection connection, bool enabled) =>
        new[] { MarketplaceExternalWritePolicies.Price, MarketplaceExternalWritePolicies.Stock }
            .Select(resourceType => new ConnectionSyncPolicy { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, ResourceType = resourceType, IntervalSeconds = 0, Enabled = enabled, Version = 1 });

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
        DisplayName = $"{platformCode} gönderim testi",
        ExternalStoreId = $"store-{Guid.NewGuid():N}",
        Status = "ACTIVE",
        ApiVersion = "V1",
        SettingsJson = "{\"ExternalWritesEnabled\":true}",
        Version = 1
    };

    private static (Product Product, ProductVariant Variant) NewVariant(Tenant tenant, string sku, Guid? productId = null)
    {
        var now = DateTimeOffset.UtcNow;
        var product = new Product { Id = productId ?? Guid.CreateVersion7(), TenantId = tenant.Id, Title = $"Ürün {sku}", Description = "Gönderim testi", Status = ProductStatus.Active, CreatedAt = now, UpdatedAt = now };
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
