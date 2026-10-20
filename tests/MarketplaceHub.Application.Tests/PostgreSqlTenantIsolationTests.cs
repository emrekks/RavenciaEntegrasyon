using System.Data.Common;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MarketplaceHub.Api.Catalog;
using MarketplaceHub.Api.Realtime;
using MarketplaceHub.Api.Security;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Adapters.Trendyol;
using MarketplaceHub.Infrastructure.Identity;
using MarketplaceHub.Infrastructure.Persistence;
using MarketplaceHub.Infrastructure.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
using Xunit.Abstractions;

namespace MarketplaceHub.Application.Tests;

[Collection("postgres-integration")]
public sealed class PostgreSqlTenantIsolationTests(PostgreSqlTenantIsolationFixture fixture, ITestOutputHelper output) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    [PostgreSqlFact]
    public async Task TrendyolOptionRepair_UpdatesOnlyMappedVariantOptionsWithoutWritingRemotely()
    {
        var tenant = NewTenant("trendyol-options-only");
        var connection = NewQuestionConnection(tenant);
        var productId = Guid.CreateVersion7();
        var variantId = Guid.CreateVersion7();
        var colorOptionId = Guid.CreateVersion7();
        var legacyWebColorOptionId = Guid.CreateVersion7();
        var oldColorValueId = Guid.CreateVersion7();
        var legacyWebColorValueId = Guid.CreateVersion7();
        var importFailureIssue = new OperationalIssue
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenant.Id,
            DedupeKey = $"product-sync-import:{connection.Id}:remote-product-1",
            Code = "PRODUCT_IMPORT_FAILED",
            Summary = "The previous option signature exceeded the database limit.",
            Status = IssueStatus.Open,
            FirstSeenAt = fixture.Now.AddMinutes(-10),
            LastSeenAt = fixture.Now.AddMinutes(-10),
            OccurrenceCount = 1
        };
        var variant = new ProductVariant
        {
            Id = variantId,
            TenantId = tenant.Id,
            ProductId = productId,
            SortOrder = 0,
            Sku = "MZ005S26",
            SkuNormalized = "MZ005S26",
            ModelCode = "MZ005S26",
            OptionSignature = "Renk: Lacivert | Beden: XL",
            CreatedAt = fixture.Now.AddDays(-1),
            UpdatedAt = fixture.Now.AddDays(-1),
            Version = 4
        };
        var remoteOptions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Renk"] = "İndigo",
            ["Web Color"] = "Lacivert",
            ["Beden"] = "XL"
        };
        for (var index = 1; index <= 5; index++)
            remoteOptions[$"Additional option {index}"] = new string((char)('A' + index), 120);

        var remoteProduct = new RemoteCatalogProduct(
            "remote-product-1", "MZ005", "Remote title", "Remote description", null, null, null, null,
            ["https://example.invalid/remote.jpg"],
            [new RemoteCatalogVariant(
                "remote-variant-1", "MZ005S26", "869000000526", "MZ005S26",
                remoteOptions,
                Archived: false,
                SalePrice: 499m,
                ListPrice: 799m,
                VatRate: 10m,
                StockQuantity: 17m,
                Currency: "TRY",
                RawJson: "{}")],
            "{}");
        var productPort = new ReadOnlyCatalogProductTestPort(remoteProduct);

        await using (var seedDb = fixture.CreateContext())
        {
            seedDb.Tenants.Add(tenant);
            seedDb.PlatformConnections.Add(connection);
            seedDb.Products.Add(new Product
            {
                Id = productId,
                TenantId = tenant.Id,
                Title = "Locally managed title",
                Description = "Locally managed description",
                Status = ProductStatus.Active,
                CreatedAt = fixture.Now.AddDays(-2),
                UpdatedAt = fixture.Now.AddDays(-1),
                Version = 7
            });
            seedDb.ProductVariants.Add(variant);
            seedDb.MarketplaceProductLinks.Add(new MarketplaceProductLink
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenant.Id,
                ConnectionId = connection.Id,
                ProductId = productId,
                ExternalId = "remote-product-1",
                LastImportedProductVersion = 7,
                SyncStatus = "SYNCED",
                Version = 2
            });
            seedDb.MarketplaceVariantLinks.Add(new MarketplaceVariantLink
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenant.Id,
                ConnectionId = connection.Id,
                VariantId = variantId,
                ExternalId = "remote-variant-1",
                Version = 1
            });
            seedDb.ProductOptions.AddRange(
                new ProductOption { Id = colorOptionId, TenantId = tenant.Id, ProductId = productId, Label = "Renk", NormalizedKey = "RENK", SortOrder = 0 },
                new ProductOption { Id = legacyWebColorOptionId, TenantId = tenant.Id, ProductId = productId, Label = "Web Color", NormalizedKey = "WEB-COLOR", SortOrder = 1 });
            seedDb.ProductOptionValues.AddRange(
                new ProductOptionValue { Id = oldColorValueId, TenantId = tenant.Id, OptionId = colorOptionId, Label = "Lacivert", NormalizedKey = "LACIVERT", SortOrder = 0 },
                new ProductOptionValue { Id = legacyWebColorValueId, TenantId = tenant.Id, OptionId = legacyWebColorOptionId, Label = "Lacivert", NormalizedKey = "LACIVERT", SortOrder = 0 });
            seedDb.OperationalIssues.Add(importFailureIssue);
            seedDb.VariantOptionValues.AddRange(
                new VariantOptionValue { Id = Guid.CreateVersion7(), TenantId = tenant.Id, VariantId = variantId, OptionId = colorOptionId, OptionValueId = oldColorValueId },
                new VariantOptionValue { Id = Guid.CreateVersion7(), TenantId = tenant.Id, VariantId = variantId, OptionId = legacyWebColorOptionId, OptionValueId = legacyWebColorValueId });
            await seedDb.SaveChangesAsync();
        }

        try
        {
            await using (var processorDb = fixture.CreateContext())
            {
                var processor = new MarketplaceJobProcessor(
                    processorDb, null!, null!, productPort, null!, null!, null!, null!, null!, null!, null!,
                    new ConfigurationBuilder().Build(), fixture.TimeProvider);
                var result = await processor.ProcessAsync(
                    tenant.Id,
                    connection.Id,
                    MarketplaceJobTypes.ProductSync,
                    "{\"full\":true,\"existingOnly\":true,\"optionsOnly\":true,\"includeArchived\":true,\"includePendingApproval\":true,\"updateExistingProducts\":false}",
                    "trendyol-options-only",
                    CancellationToken.None);

                Assert.True(result.Succeeded, result.ErrorSummary);
            }

            await using var verificationDb = fixture.CreateContext();
            var persistedProduct = await verificationDb.Products.AsNoTracking().SingleAsync(row => row.TenantId == tenant.Id && row.Id == productId);
            var persistedVariant = await verificationDb.ProductVariants.AsNoTracking().SingleAsync(row => row.TenantId == tenant.Id && row.Id == variantId);
            Assert.Equal("Locally managed title", persistedProduct.Title);
            Assert.Equal("Locally managed description", persistedProduct.Description);
            Assert.Equal(7, persistedProduct.Version);
            Assert.Equal("MZ005S26", persistedVariant.Sku);
            Assert.StartsWith("Renk: İndigo | Beden: XL", persistedVariant.OptionSignature, StringComparison.Ordinal);
            Assert.True(persistedVariant.OptionSignature.Length <= 512);
            Assert.Equal(5, persistedVariant.Version);
            Assert.Equal(0, await verificationDb.InventoryItems.CountAsync(row => row.TenantId == tenant.Id && row.VariantId == variantId));
            Assert.Equal(0, await verificationDb.ChannelOffers.CountAsync(row => row.TenantId == tenant.Id && row.VariantId == variantId));
            Assert.Equal(0, await verificationDb.ProductMedia.CountAsync(row => row.TenantId == tenant.Id && row.ProductId == productId));
            Assert.False(await verificationDb.ProductOptions.AnyAsync(row => row.TenantId == tenant.Id && row.ProductId == productId && row.NormalizedKey == "WEB-COLOR"));
            Assert.Equal(5, await verificationDb.ProductOptions.CountAsync(row => row.TenantId == tenant.Id && row.ProductId == productId && row.Label.StartsWith("Additional option ")));
            Assert.Equal(IssueStatus.Resolved, (await verificationDb.OperationalIssues.AsNoTracking().SingleAsync(row => row.Id == importFailureIssue.Id)).Status);
            var assignedColor = await (from assignment in verificationDb.VariantOptionValues.AsNoTracking()
                                       join option in verificationDb.ProductOptions.AsNoTracking() on new { assignment.TenantId, assignment.OptionId } equals new { option.TenantId, OptionId = option.Id }
                                       join value in verificationDb.ProductOptionValues.AsNoTracking() on new { assignment.TenantId, assignment.OptionValueId } equals new { value.TenantId, OptionValueId = value.Id }
                                       where assignment.TenantId == tenant.Id && assignment.VariantId == variantId && option.NormalizedKey == "RENK"
                                       select value.Label).SingleAsync();
            Assert.Equal("İndigo", assignedColor);
            Assert.Equal(1, productPort.ReadCalls);
            Assert.Equal(0, productPort.WriteAttempts);
        }
        finally
        {
            await DeleteCatalogOptionRepairTestTenantAsync(tenant.Id);
        }
    }

    [PostgreSqlFact]
    public async Task OperationsRealtimeDispatchLease_IsExclusiveAcrossDatabaseConnections()
    {
        await using var firstDb = fixture.CreateContext();
        await using var secondDb = fixture.CreateContext();
        await firstDb.Database.OpenConnectionAsync();
        await secondDb.Database.OpenConnectionAsync();
        var firstConnection = firstDb.Database.GetDbConnection();
        var secondConnection = secondDb.Database.GetDbConnection();
        var firstOwnsLease = false;
        var secondOwnsLease = false;

        try
        {
            firstOwnsLease = await OperationsRealtimeDispatchLease.TryAcquireAsync(firstConnection, CancellationToken.None);
            Assert.True(firstOwnsLease);
            Assert.False(await OperationsRealtimeDispatchLease.TryAcquireAsync(secondConnection, CancellationToken.None));

            await OperationsRealtimeDispatchLease.ReleaseAsync(firstConnection, CancellationToken.None);
            firstOwnsLease = false;

            secondOwnsLease = await OperationsRealtimeDispatchLease.TryAcquireAsync(secondConnection, CancellationToken.None);
            Assert.True(secondOwnsLease);
        }
        finally
        {
            if (secondOwnsLease)
                await OperationsRealtimeDispatchLease.ReleaseAsync(secondConnection, CancellationToken.None);
            if (firstOwnsLease)
                await OperationsRealtimeDispatchLease.ReleaseAsync(firstConnection, CancellationToken.None);
        }
    }

    [PostgreSqlFact]
    public async Task OperationsRealtimeOutboxRetention_DeletesOnlyExpiredPublishedEventsInBoundedBatches()
    {
        var tenant = NewTenant("outbox-retention");
        var now = fixture.Now;
        await using (var db = fixture.CreateContext())
        {
            db.Tenants.Add(tenant);
            await db.SaveChangesAsync();

            var oldPublished = Enumerable.Range(0, OperationsRealtimeOutboxRetention.BatchSize + 2)
                .Select(_ => NewOutboxEvent(tenant.Id, now, now.AddDays(-31)))
                .ToArray();
            db.IntegrationOutboxEvents.AddRange(oldPublished);
            db.IntegrationOutboxEvents.Add(NewOutboxEvent(tenant.Id, now, now.AddDays(-29)));
            db.IntegrationOutboxEvents.Add(NewOutboxEvent(tenant.Id, now, publishedAt: null));
            await db.SaveChangesAsync();
        }

        try
        {
            await using var db = fixture.CreateContext();
            var deletedFirstBatch = await OperationsRealtimeOutboxRetention.DeleteExpiredPublishedBatchAsync(db, now, CancellationToken.None);
            Assert.Equal(OperationsRealtimeOutboxRetention.BatchSize, deletedFirstBatch);

            var remainingOldPublished = await db.IntegrationOutboxEvents.CountAsync(row => row.TenantId == tenant.Id && row.PublishedAt < now.AddDays(-30));
            var recentPublished = await db.IntegrationOutboxEvents.CountAsync(row => row.TenantId == tenant.Id && row.PublishedAt == now.AddDays(-29));
            var unpublished = await db.IntegrationOutboxEvents.CountAsync(row => row.TenantId == tenant.Id && row.PublishedAt == null);
            Assert.Equal(2, remainingOldPublished);
            Assert.Equal(1, recentPublished);
            Assert.Equal(1, unpublished);

            var deletedSecondBatch = await OperationsRealtimeOutboxRetention.DeleteExpiredPublishedBatchAsync(db, now, CancellationToken.None);
            Assert.Equal(2, deletedSecondBatch);
            Assert.Equal(2, await db.IntegrationOutboxEvents.CountAsync(row => row.TenantId == tenant.Id));
        }
        finally
        {
            await DeleteInvoiceTestTenantAsync(tenant.Id);
        }
    }

    [PostgreSqlFact]
    public async Task OperationsRealtimeBroadcaster_RedeliversStableEventId_WhenStoppedAfterSendBeforePublishMark()
    {
        var tenant = NewTenant("outbox-send-before-mark");
        var now = fixture.Now;
        var outboxEvent = NewOutboxEvent(tenant.Id, now, publishedAt: null);
        await using (var db = fixture.CreateContext())
        {
            db.Tenants.Add(tenant);
            db.IntegrationOutboxEvents.Add(outboxEvent);
            await db.SaveChangesAsync();
        }

        var deliveredIds = new System.Collections.Concurrent.ConcurrentQueue<Guid>();
        using var firstStop = new CancellationTokenSource();
        var firstHub = new RecordingOperationsHubContext((arguments, _) =>
        {
            deliveredIds.Enqueue(ReadFirstOutboxEventId(arguments));
            firstStop.Cancel();
        });

        try
        {
            await using (var firstServices = CreateOutboxDispatcherServices())
            {
                var firstDispatcher = CreateOutboxDispatcher(firstServices, firstHub);
                await firstDispatcher.StartAsync(firstStop.Token);
                await WaitForCountAsync(deliveredIds, 1);
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                    firstDispatcher.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(10)));
            }

            await using (var db = fixture.CreateContext())
            {
                var afterInterruptedAck = await db.IntegrationOutboxEvents.AsNoTracking()
                    .SingleAsync(row => row.Id == outboxEvent.Id);
                Assert.Null(afterInterruptedAck.PublishedAt);
            }

            using var secondStop = new CancellationTokenSource();
            var secondHub = new RecordingOperationsHubContext((arguments, _) =>
                deliveredIds.Enqueue(ReadFirstOutboxEventId(arguments)));
            await using (var secondServices = CreateOutboxDispatcherServices())
            {
                var secondDispatcher = CreateOutboxDispatcher(secondServices, secondHub);
                await secondDispatcher.StartAsync(secondStop.Token);
                await WaitForCountAsync(deliveredIds, 2);

                var publishDeadline = DateTimeOffset.UtcNow.AddSeconds(10);
                while (DateTimeOffset.UtcNow < publishDeadline)
                {
                    await using var db = fixture.CreateContext();
                    var publishedAt = await db.IntegrationOutboxEvents.AsNoTracking()
                        .Where(row => row.Id == outboxEvent.Id)
                        .Select(row => row.PublishedAt)
                        .SingleAsync();
                    if (publishedAt.HasValue) break;
                    await Task.Delay(20);
                }

                await secondStop.CancelAsync();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                    secondDispatcher.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(10)));
            }

            Assert.Equal([outboxEvent.Id, outboxEvent.Id], deliveredIds.ToArray());
            await using var finalDb = fixture.CreateContext();
            var afterRetry = await finalDb.IntegrationOutboxEvents.AsNoTracking()
                .SingleAsync(row => row.Id == outboxEvent.Id);
            Assert.NotNull(afterRetry.PublishedAt);
            Assert.Equal(1, afterRetry.DispatchAttempts);
        }
        finally
        {
            await DeleteInvoiceTestTenantAsync(tenant.Id);
        }
    }

    [PostgreSqlFact]
    public async Task ProductVariantAttributesAndDefaultPrices_RoundTripThroughProductUpdateAndReload()
    {
        var tenant = NewTenant("product-price-defaults");
        var attributeId = Guid.CreateVersion7();
        var valueId = Guid.CreateVersion7();
        var product = new Product
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenant.Id,
            Title = $"Price defaults {Guid.NewGuid():N}",
            Description = "Persistence round-trip test",
            DefaultListPrice = 0m,
            DefaultSalePrice = 0m,
            CreatedAt = fixture.Now,
            UpdatedAt = fixture.Now,
            Version = 1
        };
        var sku = $"PRICE-{Guid.NewGuid():N}";
        var variant = new ProductVariant
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenant.Id,
            ProductId = product.Id,
            SortOrder = 0,
            Sku = sku,
            SkuNormalized = sku,
            OptionSignature = "Renk=Test",
            CreatedAt = fixture.Now,
            UpdatedAt = fixture.Now,
            Version = 1
        };
        var attribute = new AttributeDefinition
        {
            Id = attributeId,
            TenantId = tenant.Id,
            Code = $"TEST-{Guid.NewGuid():N}",
            Name = "Test varyant özelliği",
            DataType = AttributeDataType.SingleSelect,
            CreatedAt = fixture.Now,
            UpdatedAt = fixture.Now,
            Version = 1
        };
        var attributeValue = new AttributeValue
        {
            Id = valueId,
            TenantId = tenant.Id,
            AttributeId = attributeId,
            Value = "Değer",
            NormalizedValue = "DEGER",
            SortOrder = 0,
            Version = 1
        };
        await using var db = fixture.CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        db.Tenants.Add(tenant);
        db.Products.Add(product);
        db.ProductVariants.Add(variant);
        db.AttributeDefinitions.Add(attribute);
        db.AttributeValues.Add(attributeValue);
        db.ProductAttributeAssignments.Add(new ProductAttributeAssignment
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenant.Id,
            ProductId = product.Id,
            VariantId = variant.Id,
            AttributeId = attributeId,
            ValueId = valueId,
            SortOrder = 0,
            Version = 1
        });
        await db.SaveChangesAsync();

        var service = new CatalogService(
            db,
            null!,
            new ConfigurationBuilder().Build(),
            fixture.TimeProvider,
            new MemoryCache(new MemoryCacheOptions()));
        var updated = await service.UpdateProductAsync(
            tenant.Id,
            product.Id,
            product.Version,
            new UpdateProductCommand(
                product.Title,
                product.Description,
                null,
                null,
                VariantUpdates: [new UpdateVariantCommand(variant.Id, variant.Sku, null, null, DefaultListPrice: 899m, DefaultSalePrice: 799m)],
                DefaultListPrice: 699m,
                DefaultSalePrice: 0m),
            CancellationToken.None);

        Assert.True(updated.Succeeded, updated.Error?.Message);
        Assert.Equal(699m, updated.Value?.DefaultListPrice);
        Assert.Equal(0m, updated.Value?.DefaultSalePrice);
        Assert.Equal(899m, updated.Value?.Variants.Single().DefaultListPrice);
        Assert.Equal(799m, updated.Value?.Variants.Single().DefaultSalePrice);
        Assert.Equal(valueId, Assert.Single(updated.Value!.Variants.Single().Attributes!).ValueId);

        var reloadedView = await service.GetProductAsync(tenant.Id, product.Id, CancellationToken.None);
        Assert.Equal(699m, reloadedView.Value?.DefaultListPrice);
        Assert.Equal(0m, reloadedView.Value?.DefaultSalePrice);
        Assert.Equal(799m, reloadedView.Value?.StartingPrice);
        Assert.Equal(899m, reloadedView.Value?.Variants.Single().DefaultListPrice);
        Assert.Equal(799m, reloadedView.Value?.Variants.Single().DefaultSalePrice);
        Assert.Equal(valueId, Assert.Single(reloadedView.Value!.Variants.Single().Attributes!).ValueId);

        var listedProducts = await service.ListProductsAsync(tenant.Id, 20, null, null, null, null, null, CancellationToken.None);
        Assert.Equal(799m, Assert.Single(listedProducts.Items).StartingPrice);

        db.ChangeTracker.Clear();
        var reloaded = await db.Products.AsNoTracking().SingleAsync(x => x.TenantId == tenant.Id && x.Id == product.Id);
        Assert.Equal(699m, reloaded.DefaultListPrice);
        Assert.Equal(0m, reloaded.DefaultSalePrice);
        var reloadedVariant = await db.ProductVariants.AsNoTracking().SingleAsync(x => x.TenantId == tenant.Id && x.Id == variant.Id);
        Assert.Equal(899m, reloadedVariant.DefaultListPrice);
        Assert.Equal(799m, reloadedVariant.DefaultSalePrice);

        await transaction.RollbackAsync();
    }

    [PostgreSqlFact]
    public async Task UpdateProductAsync_WhenVariantIdsToDeleteAreSent_RemovesThemWithTheirStockAndKeepsTheRest()
    {
        var tenant = NewTenant("variant-delete");
        var product = new Product
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenant.Id,
            Title = $"Variant delete {Guid.NewGuid():N}",
            Description = "Variant delete test",
            DefaultListPrice = 0m,
            DefaultSalePrice = 0m,
            CreatedAt = fixture.Now,
            UpdatedAt = fixture.Now,
            Version = 1
        };
        ProductVariant NewVariant(int order)
        {
            var code = $"DEL-{order}-{Guid.NewGuid():N}";
            return new ProductVariant
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenant.Id,
                ProductId = product.Id,
                SortOrder = order,
                Sku = code,
                SkuNormalized = code,
                OptionSignature = $"Beden={order}",
                CreatedAt = fixture.Now,
                UpdatedAt = fixture.Now,
                Version = 1
            };
        }
        var kept = NewVariant(0);
        var removed = NewVariant(1);
        await using var db = fixture.CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        db.Tenants.Add(tenant);
        db.Products.Add(product);
        db.ProductVariants.AddRange(kept, removed);
        db.InventoryItems.AddRange(
            new InventoryItem { Id = Guid.CreateVersion7(), TenantId = tenant.Id, VariantId = kept.Id, LocationCode = "MAIN", OnHand = 5, Available = 5 },
            new InventoryItem { Id = Guid.CreateVersion7(), TenantId = tenant.Id, VariantId = removed.Id, LocationCode = "MAIN", OnHand = 3, Available = 3 });
        await db.SaveChangesAsync();

        var service = new CatalogService(
            db,
            null!,
            new ConfigurationBuilder().Build(),
            fixture.TimeProvider,
            new MemoryCache(new MemoryCacheOptions()));

        var blocked = await service.UpdateProductAsync(
            tenant.Id,
            product.Id,
            product.Version,
            new UpdateProductCommand(product.Title, product.Description, null, null, VariantIdsToDelete: [kept.Id, removed.Id]),
            CancellationToken.None);
        Assert.False(blocked.Succeeded);

        var updated = await service.UpdateProductAsync(
            tenant.Id,
            product.Id,
            product.Version,
            new UpdateProductCommand(product.Title, product.Description, null, null, VariantIdsToDelete: [removed.Id]),
            CancellationToken.None);
        Assert.True(updated.Succeeded, updated.Error?.Message);
        Assert.Equal(kept.Id, Assert.Single(updated.Value!.Variants).Id);

        db.ChangeTracker.Clear();
        Assert.False(await db.ProductVariants.AsNoTracking().AnyAsync(x => x.TenantId == tenant.Id && x.Id == removed.Id));
        Assert.False(await db.InventoryItems.AsNoTracking().AnyAsync(x => x.TenantId == tenant.Id && x.VariantId == removed.Id));
        Assert.True(await db.InventoryItems.AsNoTracking().AnyAsync(x => x.TenantId == tenant.Id && x.VariantId == kept.Id));

        await transaction.RollbackAsync();
    }

    [PostgreSqlFact]
    public async Task ReceiveAsync_WhenSameWebhookArrivesConcurrently_CreatesOneInboxMessageAndOneJob()
    {
        var tenant = NewTenant("webhook");
        var connection = new PlatformConnection
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenant.Id,
            PublicId = Guid.CreateVersion7(),
            PlatformCode = "TRENDYOL",
            Environment = "STAGE",
            DisplayName = "Concurrent webhook test",
            ExternalStoreId = $"store-{Guid.NewGuid():N}",
            Status = "ACTIVE",
            ApiVersion = "V2",
            Version = 1
        };
        const string routeToken = "concurrent-webhook-route-token";
        var subscription = new WebhookSubscription
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenant.Id,
            ConnectionId = connection.Id,
            RouteTokenHash = fixture.TokenHasher.Hash(routeToken),
            AuthenticationType = "API_KEY",
            ProtectedVerifierSecret = "test-secret",
            Status = "ACTIVE",
            Version = 1
        };
        const string externalMessageId = "content:concurrent-event";
        const string firstRawJson = "{\"content\":[{\"id\":\"concurrent-event\"}],\"page\":0}";
        const string secondRawJson = "{ \"page\": 0, \"content\": [ { \"id\": \"concurrent-event\" } ] }";
        var firstVerifier = new FixedWebhookVerifier(new(externalMessageId, "payload-hash-a", "ORDERS", firstRawJson));
        var secondVerifier = new FixedWebhookVerifier(new(externalMessageId, "payload-hash-b", "ORDERS", secondRawJson));

        try
        {
            await using (var setupDb = fixture.CreateContext())
            {
                setupDb.Tenants.Add(tenant);
                setupDb.PlatformConnections.Add(connection);
                setupDb.WebhookSubscriptions.Add(subscription);
                await setupDb.SaveChangesAsync();
            }

            var requests = Enumerable.Range(0, 16)
                .Select(index => index % 2 == 0
                    ? ReceiveWebhookAsync(connection.PublicId, routeToken, firstRawJson, $"concurrent-{index}", firstVerifier)
                    : ReceiveWebhookAsync(connection.PublicId, routeToken, secondRawJson, $"concurrent-{index}", secondVerifier))
                .ToArray();
            var results = await Task.WhenAll(requests);

            Assert.All(results, result => Assert.True(result.Succeeded, result.Error?.Code));

            await using var verifyDb = fixture.CreateContext();
            var inboxCount = await verifyDb.InboxMessages.CountAsync(x =>
                x.TenantId == tenant.Id &&
                x.Source == "TRENDYOL_WEBHOOK" &&
                x.ExternalMessageId == externalMessageId);
            var jobDedupKey = $"webhook:{connection.Id}:{externalMessageId}";
            var jobCount = await verifyDb.IntegrationJobs.CountAsync(x =>
                x.TenantId == tenant.Id &&
                x.JobType == MarketplaceJobTypes.WebhookIngest &&
                x.JobDedupKey == jobDedupKey);
            var persistedSubscription = await verifyDb.WebhookSubscriptions.AsNoTracking().SingleAsync(x => x.Id == subscription.Id);

            Assert.Equal(1, inboxCount);
            Assert.Equal(1, jobCount);
            Assert.NotNull(persistedSubscription.LastReceivedAt);
        }
        finally
        {
            await using var cleanupDb = fixture.CreateContext();
            await cleanupDb.IntegrationJobs.Where(x => x.TenantId == tenant.Id).ExecuteDeleteAsync();
            await cleanupDb.InboxMessages.Where(x => x.TenantId == tenant.Id).ExecuteDeleteAsync();
            await cleanupDb.IntegrationOutboxEvents.Where(x => x.TenantId == tenant.Id).ExecuteDeleteAsync();
            await cleanupDb.WebhookSubscriptions.Where(x => x.Id == subscription.Id).ExecuteDeleteAsync();
            await cleanupDb.PlatformConnections.Where(x => x.Id == connection.Id).ExecuteDeleteAsync();
            await cleanupDb.Tenants.Where(x => x.Id == tenant.Id).ExecuteDeleteAsync();
        }
    }

    [PostgreSqlFact]
    public async Task ReceiveAsync_HepsiburadaClaimPackageQueuesPlatformSpecificIngestJob()
    {
        var tenant = NewTenant("hb-claim-package");
        var connection = new PlatformConnection
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenant.Id,
            PublicId = Guid.CreateVersion7(),
            PlatformCode = "HEPSIBURADA",
            Environment = "STAGE",
            DisplayName = "Hepsiburada claim package webhook test",
            ExternalStoreId = $"merchant-{Guid.NewGuid():N}",
            Status = "ACTIVE",
            ApiVersion = "V1.0",
            Version = 1
        };
        const string routeToken = "hepsiburada-claim-package-route-token";
        var subscription = new WebhookSubscription
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenant.Id,
            ConnectionId = connection.Id,
            RouteTokenHash = fixture.TokenHasher.Hash(routeToken),
            AuthenticationType = "API_KEY",
            ProtectedVerifierSecret = "test-secret",
            Status = "ACTIVE",
            Version = 1
        };
        const string externalMessageId = "claim_package:replacement-17:stable-hash";
        const string rawJson = """
        {
          "packageNumber":"replacement-17",
          "status":"Open",
          "claims":[{
            "number":"claim-17",
            "status":"Accepted",
            "claimType":"RenewProduct",
            "claimDate":"2026-09-28T12:15:00Z",
            "orderNumber":"order-17",
            "orderDate":"2026-09-20T10:00:00Z",
            "quantity":1,
            "line":{"lineItemId":"line-17","quantity":1,"merchantSku":"sku-17","price":9.99}
          }]
        }
        """;
        var verifier = new FixedWebhookVerifier(new(externalMessageId, "payload-hash", "CLAIM_PACKAGE", rawJson));

        try
        {
            await using (var setupDb = fixture.CreateContext())
            {
                setupDb.Tenants.Add(tenant);
                setupDb.PlatformConnections.Add(connection);
                setupDb.WebhookSubscriptions.Add(subscription);
                await setupDb.SaveChangesAsync();
            }

            var received = await ReceiveWebhookAsync(connection.PublicId, routeToken, rawJson, "claim-package-webhook-test", verifier);

            Assert.True(received.Succeeded, received.Error?.Code);
            await using var verifyDb = fixture.CreateContext();
            var inbox = await verifyDb.InboxMessages.AsNoTracking().SingleAsync(x => x.TenantId == tenant.Id);
            var job = await verifyDb.IntegrationJobs.AsNoTracking().SingleAsync(x => x.TenantId == tenant.Id);
            using var jobPayload = System.Text.Json.JsonDocument.Parse(job.PayloadJson);
            Assert.Equal("HEPSIBURADA_WEBHOOK", inbox.Source);
            Assert.Equal($"{connection.Id:N}:{externalMessageId}", inbox.ExternalMessageId);
            Assert.Equal(MarketplaceJobTypes.HepsiburadaWebhookIngest, job.JobType);
            Assert.Equal("CLAIM_PACKAGE", jobPayload.RootElement.GetProperty("resourceType").GetString());
            Assert.Equal($"{connection.Id:N}:{externalMessageId}", jobPayload.RootElement.GetProperty("externalMessageId").GetString());
        }
        finally
        {
            await using var cleanupDb = fixture.CreateContext();
            await cleanupDb.IntegrationJobs.Where(x => x.TenantId == tenant.Id).ExecuteDeleteAsync();
            await cleanupDb.InboxMessages.Where(x => x.TenantId == tenant.Id).ExecuteDeleteAsync();
            await cleanupDb.IntegrationOutboxEvents.Where(x => x.TenantId == tenant.Id).ExecuteDeleteAsync();
            await cleanupDb.WebhookSubscriptions.Where(x => x.Id == subscription.Id).ExecuteDeleteAsync();
            await cleanupDb.PlatformConnections.Where(x => x.Id == connection.Id).ExecuteDeleteAsync();
            await cleanupDb.Tenants.Where(x => x.Id == tenant.Id).ExecuteDeleteAsync();
        }
    }

    private async Task<ServiceResult<bool>> ReceiveWebhookAsync(Guid connectionPublicId, string routeToken, string rawJson, string correlationId, IWebhookVerifier verifier)
    {
        await using var db = fixture.CreateContext();
        var service = new MarketplaceWebhookService(db, fixture.TokenHasher, verifier, fixture.TimeProvider);
        return await service.ReceiveAsync(
            connectionPublicId,
            routeToken,
            Encoding.UTF8.GetBytes(rawJson),
            new Dictionary<string, string>(),
            correlationId,
            CancellationToken.None);
    }

    [PostgreSqlFact]
    public async Task OperationalIssueDedupeKey_AllowsSameKeyAcrossTenants_ButRejectsDuplicateWithinTenant()
    {
        var firstTenant = NewTenant("tenant-a");
        var secondTenant = NewTenant("tenant-b");
        await using var db = fixture.CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        {
            db.Tenants.AddRange(firstTenant, secondTenant);
            db.OperationalIssues.AddRange(
                NewIssue(firstTenant.Id, "same-key"),
                NewIssue(secondTenant.Id, "same-key"));
            await db.SaveChangesAsync();

            db.ChangeTracker.Clear();
            db.OperationalIssues.Add(NewIssue(firstTenant.Id, "same-key"));
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }
        await transaction.RollbackAsync();
    }

    [PostgreSqlFact]
    public async Task ADraftConnectionIsRetestedAfterAPassingFaultOrARefusalOfUnchangedCredentials()
    {
        var tenant = NewTenant("connection-retest");
        var connection = NewQuestionConnection(tenant);
        connection.PlatformCode = "PAZARAMA";
        connection.Environment = "PRODUCTION";
        connection.ApiVersion = "V1";
        connection.Status = "DRAFT";
        connection.LastTestedAt = fixture.Now.AddMinutes(-20);
        connection.LastErrorCode = "PAZARAMA_GATEWAY_BLOCKED";
        var refused = NewQuestionConnection(tenant);
        refused.PlatformCode = "PAZARAMA";
        refused.Environment = "PRODUCTION";
        refused.ApiVersion = "V1";
        refused.ExternalStoreId = "seller-18";
        refused.Status = "DRAFT";
        refused.LastTestedAt = fixture.Now.AddHours(-2);
        refused.LastErrorCode = "PAZARAMA_CREDENTIALS_REJECTED";
        // The identity service refused credentials saved three hours ago (an outage on its side): retried hourly.
        var refusedDuringOutage = NewQuestionConnection(tenant);
        refusedDuringOutage.PlatformCode = "PAZARAMA";
        refusedDuringOutage.Environment = "PRODUCTION";
        refusedDuringOutage.ApiVersion = "V1";
        refusedDuringOutage.ExternalStoreId = "seller-19";
        refusedDuringOutage.Status = "DRAFT";
        refusedDuringOutage.LastTestedAt = fixture.Now.AddHours(-2);
        refusedDuringOutage.LastErrorCode = "PAZARAMA_CREDENTIALS_REJECTED";
        // Refused for more than a day: the seller has to look at the credentials.
        var refusedLongAgo = NewQuestionConnection(tenant);
        refusedLongAgo.PlatformCode = "PAZARAMA";
        refusedLongAgo.Environment = "PRODUCTION";
        refusedLongAgo.ApiVersion = "V1";
        refusedLongAgo.ExternalStoreId = "seller-20";
        refusedLongAgo.Status = "DRAFT";
        refusedLongAgo.LastTestedAt = fixture.Now.AddHours(-2);
        refusedLongAgo.LastErrorCode = "PAZARAMA_CREDENTIALS_REJECTED";
        await using var db = fixture.CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        db.Tenants.Add(tenant);
        db.PlatformConnections.AddRange(connection, refused, refusedDuringOutage, refusedLongAgo);
        PlatformCredential Credential(PlatformConnection owner, DateTimeOffset savedAt) => new() { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = owner.Id, CredentialType = "PAZARAMA_CLIENT", ProtectedPayload = "protected", MaskedHint = "test", CreatedAt = savedAt, Version = 1 };
        db.PlatformCredentials.AddRange(
            Credential(connection, fixture.Now),
            // Saved after the refused test: the new credentials get their own test, not a retry.
            Credential(refused, fixture.Now),
            Credential(refusedDuringOutage, fixture.Now.AddHours(-3)),
            Credential(refusedLongAgo, fixture.Now.AddHours(-30)));
        await db.SaveChangesAsync();
        var producer = new ScheduledJobProducer(db, fixture.TimeProvider, new ConfigurationBuilder().Build());

        var first = await producer.RetestTransientDraftsAsync(fixture.Now, CancellationToken.None);
        var second = await producer.RetestTransientDraftsAsync(fixture.Now, CancellationToken.None);

        Assert.Equal(2, first);
        Assert.Equal(0, second);
        var jobs = await db.IntegrationJobs.Where(x => x.TenantId == tenant.Id).ToListAsync();
        Assert.Equal(new[] { connection.Id, refusedDuringOutage.Id }.Order(), jobs.Select(job => job.ConnectionId!.Value).Order());
        Assert.All(jobs, job => Assert.Equal(MarketplaceJobTypes.PazaramaConnectionTest, job.JobType));
        Assert.All(jobs, job => Assert.StartsWith($"scheduled:connection-retest:{job.ConnectionId:N}:", job.JobDedupKey));
        await transaction.RollbackAsync();
    }

    [PostgreSqlFact]
    public async Task OperationalIssueRetention_ResolvesIssuesNotSeenForAMonthAndKeepsTheRest()
    {
        var tenant = NewTenant("issue-retention");
        await using var db = fixture.CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        db.Tenants.Add(tenant);
        var stale = NewIssue(tenant.Id, "stale");
        stale.FirstSeenAt = stale.LastSeenAt = fixture.Now.AddDays(-45);
        var fresh = NewIssue(tenant.Id, "fresh");
        fresh.LastSeenAt = fixture.Now.AddDays(-3);
        var alreadyResolved = NewIssue(tenant.Id, "resolved");
        alreadyResolved.Status = IssueStatus.Resolved;
        alreadyResolved.LastSeenAt = fixture.Now.AddDays(-60);
        db.OperationalIssues.AddRange(stale, fresh, alreadyResolved);
        await db.SaveChangesAsync();

        var resolved = await new OperationalIssueRetentionService(db, fixture.TimeProvider).ResolveStaleAsync(TimeSpan.FromDays(30), CancellationToken.None);

        Assert.Equal(1, resolved);
        db.ChangeTracker.Clear();
        Assert.Equal(IssueStatus.Resolved, (await db.OperationalIssues.SingleAsync(x => x.Id == stale.Id)).Status);
        Assert.Equal(IssueStatus.Open, (await db.OperationalIssues.SingleAsync(x => x.Id == fresh.Id)).Status);
        await transaction.RollbackAsync();
    }

    [PostgreSqlFact]
    public async Task SessionForDisabledTenant_DoesNotReceiveTenantClaims()
    {
        var tenant = NewTenant("disabled-tenant");
        tenant.Status = RecordStatus.Disabled;
        var user = NewUser();
        var membership = NewMembership(tenant.Id, user.Id, MembershipRole.Administrator);
        var rawToken = TokenHasher.NewToken();
        var session = NewSession(user.Id, tenant.Id, rawToken, fixture.Now);

        await using var db = fixture.CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        {
            db.Tenants.Add(tenant);
            db.Users.Add(user);
            db.TenantMemberships.Add(membership);
            db.UserSessions.Add(session);
            await db.SaveChangesAsync();

            var context = NewHttpContext(rawToken);
            var called = false;
            var middleware = new SessionAuthMiddleware(_ =>
            {
                called = true;
                return Task.CompletedTask;
            });

            await middleware.InvokeAsync(context, db, fixture.TokenHasher, fixture.TimeProvider);

            Assert.True(called);
            Assert.True(context.User.Identity?.IsAuthenticated);
            Assert.Null(context.User.FindFirstValue("tenant_id"));
            Assert.Null(context.User.FindFirstValue(ClaimTypes.Role));
        }
        await transaction.RollbackAsync();
    }

    [PostgreSqlFact]
    public async Task SessionForActiveTenant_ReceivesTenantAndRoleClaims()
    {
        var tenant = NewTenant("active-tenant");
        var user = NewUser();
        var membership = NewMembership(tenant.Id, user.Id, MembershipRole.Operations);
        var rawToken = TokenHasher.NewToken();
        var session = NewSession(user.Id, tenant.Id, rawToken, fixture.Now);

        await using var db = fixture.CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        {
            db.Tenants.Add(tenant);
            db.Users.Add(user);
            db.TenantMemberships.Add(membership);
            db.UserSessions.Add(session);
            await db.SaveChangesAsync();

            var context = NewHttpContext(rawToken);
            var middleware = new SessionAuthMiddleware(_ => Task.CompletedTask);

            await middleware.InvokeAsync(context, db, fixture.TokenHasher, fixture.TimeProvider);

            Assert.Equal(tenant.Id.ToString(), context.User.FindFirstValue("tenant_id"));
            Assert.Equal("OPERATIONS", context.User.FindFirstValue(ClaimTypes.Role));
        }
        await transaction.RollbackAsync();
    }

    [PostgreSqlFact]
    public async Task JobLease_IsExclusive_WhenTwoWorkersRace()
    {
        var tenant = NewTenant("lease-tenant");
        var job = NewJob(tenant.Id);

        await using (var setup = fixture.CreateContext())
        {
            setup.Tenants.Add(tenant);
            setup.IntegrationJobs.Add(job);
            await setup.SaveChangesAsync();
        }

        try
        {
            LeasedJob?[] leases;
            await using (var firstDb = fixture.CreateContext())
            await using (var secondDb = fixture.CreateContext())
            {
                var firstWorker = new JobLeaseService(firstDb, fixture.TokenHasher, fixture.TimeProvider);
                var secondWorker = new JobLeaseService(secondDb, fixture.TokenHasher, fixture.TimeProvider);
                leases = await Task.WhenAll(
                    firstWorker.TryLeaseAsync(TimeSpan.FromMinutes(2), null, null, CancellationToken.None),
                    secondWorker.TryLeaseAsync(TimeSpan.FromMinutes(2), null, null, CancellationToken.None));
            }

            Assert.Single(leases, lease => lease is not null);
            Assert.Single(leases, lease => lease is null);

            await using var verification = fixture.CreateContext();
            var persisted = await verification.IntegrationJobs.SingleAsync(x => x.Id == job.Id);
            Assert.Equal(JobStatus.Leased, persisted.Status);
            Assert.Equal(1, persisted.AttemptCount);
            Assert.Equal(1, await verification.JobAttempts.CountAsync(x => x.JobId == job.Id));
        }
        finally
        {
            await DeleteJobAndTenantAsync(job.Id, tenant.Id);
        }
    }

    [PostgreSqlFact]
    public async Task JobLease_AllPriorityLanes_HandleNullOptionalFilters()
    {
        var tenant = NewTenant("lease-priority-lanes");
        var initialSyncJob = NewJob(tenant.Id);
        initialSyncJob.Priority = -1;
        var unboundedJob = NewJob(tenant.Id);
        unboundedJob.Priority = 0;
        var hotJob = NewJob(tenant.Id);
        hotJob.Priority = 1;
        var backgroundJob = NewJob(tenant.Id);
        backgroundJob.Priority = 3;
        var boundedJob = NewJob(tenant.Id);
        boundedJob.Priority = 5;

        await using (var setup = fixture.CreateContext())
        {
            setup.Tenants.Add(tenant);
            setup.IntegrationJobs.AddRange(initialSyncJob, unboundedJob, hotJob, backgroundJob, boundedJob);
            await setup.SaveChangesAsync();
        }

        try
        {
            await using var db = fixture.CreateContext();
            var leaseService = new JobLeaseService(db, fixture.TokenHasher, fixture.TimeProvider);

            var initialSyncLease = await leaseService.TryLeaseAsync(TimeSpan.FromMinutes(2), -1, -1, CancellationToken.None);
            var unboundedLease = await leaseService.TryLeaseAsync(TimeSpan.FromMinutes(2), null, null, CancellationToken.None);
            var backgroundLease = await leaseService.TryLeaseAsync(TimeSpan.FromMinutes(2), null, 3, CancellationToken.None);
            var hotLease = await leaseService.TryLeaseAsync(TimeSpan.FromMinutes(2), 2, null, CancellationToken.None);
            var boundedLease = await leaseService.TryLeaseAsync(TimeSpan.FromMinutes(2), 6, 4, CancellationToken.None);

            Assert.Equal(initialSyncJob.Id, initialSyncLease?.Id);
            Assert.Equal(unboundedJob.Id, unboundedLease?.Id);
            Assert.Equal(backgroundJob.Id, backgroundLease?.Id);
            Assert.Equal(hotJob.Id, hotLease?.Id);
            Assert.Equal(boundedJob.Id, boundedLease?.Id);
        }
        finally
        {
            await using var cleanup = fixture.CreateContext();
            var jobIds = new[] { initialSyncJob.Id, unboundedJob.Id, hotJob.Id, backgroundJob.Id, boundedJob.Id };
            await cleanup.IntegrationJobs.Where(x => jobIds.Contains(x.Id)).ExecuteDeleteAsync();
            await cleanup.IntegrationOutboxEvents.Where(x => x.TenantId == tenant.Id).ExecuteDeleteAsync();
            await cleanup.Tenants.Where(x => x.Id == tenant.Id).ExecuteDeleteAsync();
        }
    }

    [PostgreSqlFact]
    public async Task JobLease_HotLaneSelectsOldestSamePriorityHepsiburadaLifecycleBeforeTrendyolOrders()
    {
        var tenant = NewTenant("lease-fifo-same-priority");
        var hepsiburadaLifecycle = NewJob(tenant.Id);
        hepsiburadaLifecycle.JobType = MarketplaceJobTypes.HepsiburadaOrderStatusSync;
        hepsiburadaLifecycle.Priority = 0;
        var trendyolOrders = NewJob(tenant.Id);
        trendyolOrders.JobType = MarketplaceJobTypes.OrderSync;
        trendyolOrders.Priority = 0;
        trendyolOrders.CreatedAt = hepsiburadaLifecycle.CreatedAt.AddSeconds(1);
        trendyolOrders.AvailableAt = hepsiburadaLifecycle.AvailableAt.AddSeconds(1);

        await using (var setup = fixture.CreateContext())
        {
            setup.Tenants.Add(tenant);
            setup.IntegrationJobs.AddRange(hepsiburadaLifecycle, trendyolOrders);
            await setup.SaveChangesAsync();
        }

        try
        {
            await using var db = fixture.CreateContext();
            var leaseService = new JobLeaseService(db, fixture.TokenHasher, fixture.TimeProvider);
            var lease = await leaseService.TryLeaseAsync(TimeSpan.FromMinutes(2), 2, null, CancellationToken.None);

            Assert.Equal(hepsiburadaLifecycle.Id, lease?.Id);
        }
        finally
        {
            var jobIds = new[] { hepsiburadaLifecycle.Id, trendyolOrders.Id };
            await using var cleanup = fixture.CreateContext();
            await cleanup.IntegrationJobs.Where(x => jobIds.Contains(x.Id)).ExecuteDeleteAsync();
            await cleanup.IntegrationOutboxEvents.Where(x => x.TenantId == tenant.Id).ExecuteDeleteAsync();
            await cleanup.Tenants.Where(x => x.Id == tenant.Id).ExecuteDeleteAsync();
        }
    }

    [PostgreSqlFact]
    public async Task JobLease_SkipsJobsWhoseConnectionLockGroupIsAlreadyRunning()
    {
        var tenant = NewTenant("lease-lock-group");
        var busyConnectionId = Guid.CreateVersion7();
        var freeConnectionId = Guid.CreateVersion7();
        var running = NewJob(tenant.Id);
        running.JobType = MarketplaceJobTypes.OrderSync;
        running.ConnectionId = busyConnectionId;
        running.Status = JobStatus.Leased;
        running.AttemptCount = 1;
        running.LeaseTokenHash = "running-lease";
        running.LeaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(2);
        // Same connection and the same "orders" lock group as the running job: must wait.
        var sameGroup = NewJob(tenant.Id);
        sameGroup.JobType = MarketplaceJobTypes.OrderStatusSync;
        sameGroup.ConnectionId = busyConnectionId;
        sameGroup.Priority = 0;
        // Same connection, different lock group: may run next to it.
        var otherGroup = NewJob(tenant.Id);
        otherGroup.JobType = MarketplaceJobTypes.ReturnSync;
        otherGroup.ConnectionId = busyConnectionId;
        otherGroup.Priority = 1;
        // Same lock group on another connection: may run.
        var otherConnection = NewJob(tenant.Id);
        otherConnection.JobType = MarketplaceJobTypes.OrderStatusSync;
        otherConnection.ConnectionId = freeConnectionId;
        otherConnection.Priority = 2;

        await using (var setup = fixture.CreateContext())
        {
            setup.Tenants.Add(tenant);
            setup.IntegrationJobs.AddRange(running, sameGroup, otherGroup, otherConnection);
            await setup.SaveChangesAsync();
        }

        var jobIds = new[] { running.Id, sameGroup.Id, otherGroup.Id, otherConnection.Id };
        try
        {
            await using var db = fixture.CreateContext();
            var leaseService = new JobLeaseService(db, fixture.TokenHasher, fixture.TimeProvider);

            var first = await leaseService.TryLeaseAsync(TimeSpan.FromMinutes(2), null, null, CancellationToken.None);
            var second = await leaseService.TryLeaseAsync(TimeSpan.FromMinutes(2), null, null, CancellationToken.None);
            var third = await leaseService.TryLeaseAsync(TimeSpan.FromMinutes(2), null, null, CancellationToken.None);

            Assert.Equal(otherGroup.Id, first?.Id);
            Assert.Equal(otherConnection.Id, second?.Id);
            Assert.Null(third);

            // Once the running job's lease has expired it no longer holds the group back.
            await db.IntegrationJobs.Where(x => x.Id == running.Id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.LeaseExpiresAt, DateTimeOffset.UtcNow.AddMinutes(-1)));
            var afterExpiry = await leaseService.TryLeaseAsync(TimeSpan.FromMinutes(2), null, null, CancellationToken.None);
            Assert.Equal(sameGroup.Id, afterExpiry?.Id);
        }
        finally
        {
            await using var cleanup = fixture.CreateContext();
            await cleanup.JobAttempts.Where(x => jobIds.Contains(x.JobId)).ExecuteDeleteAsync();
            await cleanup.IntegrationJobs.Where(x => jobIds.Contains(x.Id)).ExecuteDeleteAsync();
            await cleanup.IntegrationOutboxEvents.Where(x => x.TenantId == tenant.Id).ExecuteDeleteAsync();
            await cleanup.Tenants.Where(x => x.Id == tenant.Id).ExecuteDeleteAsync();
        }
    }

    [PostgreSqlFact]
    public async Task JobMetrics_ReportWaitDurationFailuresAndQueueDepthPerJobType()
    {
        var tenant = NewTenant("job-metrics");
        var now = DateTimeOffset.UtcNow;
        var done = NewJob(tenant.Id);
        done.JobType = MarketplaceJobTypes.OrderSync;
        done.Status = JobStatus.Succeeded;
        done.AttemptCount = 1;
        done.AvailableAt = now.AddMinutes(-10);
        var failed = NewJob(tenant.Id);
        failed.JobType = MarketplaceJobTypes.OrderSync;
        failed.Status = JobStatus.RetryScheduled;
        failed.AttemptCount = 1;
        failed.AvailableAt = now.AddHours(1);
        var queued = NewJob(tenant.Id);
        queued.JobType = MarketplaceJobTypes.OrderSync;
        queued.AvailableAt = now.AddMinutes(-1);

        await using (var setup = fixture.CreateContext())
        {
            setup.Tenants.Add(tenant);
            setup.IntegrationJobs.AddRange(done, failed, queued);
            setup.JobAttempts.AddRange(
                // Started 4 s after it became runnable and ran for 20 s.
                new JobAttempt { Id = Guid.NewGuid(), TenantId = tenant.Id, JobId = done.Id, AttemptNumber = 1, StartedAt = done.AvailableAt.AddSeconds(4), CompletedAt = done.AvailableAt.AddSeconds(24), Succeeded = true },
                new JobAttempt { Id = Guid.NewGuid(), TenantId = tenant.Id, JobId = failed.Id, AttemptNumber = 1, StartedAt = now.AddMinutes(-5), CompletedAt = now.AddMinutes(-4), Succeeded = false, ErrorCode = "REMOTE_TIMEOUT" });
            await setup.SaveChangesAsync();
        }

        try
        {
            await using var db = fixture.CreateContext();
            var metrics = await new JobOperationsService(db, fixture.TimeProvider).MetricsAsync(tenant.Id, TimeSpan.FromHours(24), CancellationToken.None);

            var orderSync = Assert.Single(metrics, row => row.JobType == MarketplaceJobTypes.OrderSync);
            Assert.Equal(2, orderSync.Attempts);
            Assert.Equal(1, orderSync.Failed);
            Assert.Equal(1, orderSync.Queued);
            Assert.Equal(0, orderSync.Running);
            Assert.NotNull(orderSync.AverageDurationSeconds);
            Assert.InRange(orderSync.AverageWaitSeconds!.Value, 3.9, 4.1);
        }
        finally
        {
            var ids = new[] { done.Id, failed.Id, queued.Id };
            await using var cleanup = fixture.CreateContext();
            await cleanup.IntegrationJobs.Where(job => ids.Contains(job.Id)).ExecuteDeleteAsync();
            await cleanup.Tenants.Where(x => x.Id == tenant.Id).ExecuteDeleteAsync();
        }
    }

    [PostgreSqlFact]
    public async Task QueueHealth_ReportsTheTrendyolOrderQuotaOfTheLastHour()
    {
        var tenant = NewTenant("quota-usage");
        var connection = NewQuestionConnection(tenant);
        connection.ExternalStoreId = $"seller-{Guid.NewGuid():N}";
        var foregroundKey = $"trendyol:orders:apigw.trendyol.com:{connection.ExternalStoreId}:fg";
        var backgroundKey = $"trendyol:orders:apigw.trendyol.com:{connection.ExternalStoreId}:bg";
        var now = fixture.Now;
        await using (var seedDb = fixture.CreateContext())
        {
            seedDb.Tenants.Add(tenant);
            seedDb.PlatformConnections.Add(connection);
            await seedDb.SaveChangesAsync();
            await seedDb.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO integration.rate_limit_usage ("Key", "Minute", "Requests") VALUES
                ({foregroundKey}, {now.AddMinutes(-5)}, 4), ({foregroundKey}, {now.AddMinutes(-2)}, 8),
                ({backgroundKey}, {now.AddMinutes(-3)}, 12), ({backgroundKey}, {now.AddHours(-2)}, 18)
                """);
        }

        try
        {
            await using var db = fixture.CreateContext();
            var service = new QueueHealthService(db, Options.Create(new MarketplaceHub.Infrastructure.Adapters.Trendyol.TrendyolOptions()), fixture.TimeProvider);
            var quota = await service.QuotaAsync(tenant.Id, CancellationToken.None);

            var foreground = Assert.Single(quota, row => row.Lane == "FOREGROUND");
            Assert.Equal(12, foreground.LimitPerMinute);
            Assert.Equal(12, foreground.LastHourRequests);
            Assert.Equal(8, foreground.PeakMinuteRequests);
            var background = Assert.Single(quota, row => row.Lane == "BACKGROUND");
            Assert.Equal(18, background.LimitPerMinute);
            Assert.Equal(12, background.LastHourRequests);
            Assert.Equal(4, (await service.LanesAsync(tenant.Id, TimeSpan.FromHours(1), CancellationToken.None)).Count);
        }
        finally
        {
            await using var cleanup = fixture.CreateContext();
            await cleanup.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM integration.rate_limit_usage WHERE \"Key\" IN ({foregroundKey}, {backgroundKey})");
            await DeleteInvoiceTestTenantAsync(tenant.Id);
        }
    }

    [PostgreSqlFact]
    public async Task Orders_SortByWhenTheyWereHandedToTheCarrier()
    {
        var tenant = NewTenant("ship-sort");
        var connection = NewQuestionConnection(tenant);
        var now = DateTimeOffset.FromUnixTimeSeconds(fixture.Now.ToUnixTimeSeconds());
        var early = NewInvoiceTestOrder(tenant.Id, connection.Id, Guid.CreateVersion7(), "ship-early", isReturnClaim: false, now);
        var late = NewInvoiceTestOrder(tenant.Id, connection.Id, Guid.CreateVersion7(), "ship-late", isReturnClaim: false, now);
        var never = NewInvoiceTestOrder(tenant.Id, connection.Id, Guid.CreateVersion7(), "ship-never", isReturnClaim: false, now);
        never.DerivedStatus = "NEW";
        OrderStatusHistory Shipped(Order order, DateTimeOffset at, string eventId) => new()
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenant.Id,
            OrderId = order.Id,
            CanonicalStatus = "SHIPPED",
            RawStatus = "Shipped",
            SourceEventId = eventId,
            OccurredAt = at,
            RecordedAt = at
        };
        await using (var seedDb = fixture.CreateContext())
        {
            seedDb.Tenants.Add(tenant);
            seedDb.PlatformConnections.Add(connection);
            seedDb.Orders.AddRange(early, late, never);
            // The first shipped status counts: a later repeat must not move the order.
            seedDb.OrderStatusHistory.AddRange(Shipped(early, now.AddDays(-3), "early-1"), Shipped(early, now.AddHours(-1), "early-2"), Shipped(late, now.AddDays(-1), "late-1"));
            await seedDb.SaveChangesAsync();
        }

        try
        {
            await using var db = fixture.CreateContext();
            var sales = new MarketplaceSalesService(db,
                new CursorCodec(new EphemeralDataProtectionProvider(), fixture.TimeProvider),
                new ConfigurationBuilder().Build(), null!, null!, null!, null!, fixture.TimeProvider);

            var newest = await sales.OrdersAsync(tenant.Id, 20, 1, null, new OrderListQuery(Sort: "SHIP_DESC"), CancellationToken.None);
            Assert.Equal(["ship-late", "ship-early", "ship-never"], newest.Items.Select(order => order.OrderNumber));
            Assert.Equal(now.AddDays(-3), newest.Items.Single(order => order.OrderNumber == "ship-early").ShippedAt);
            Assert.Null(newest.Items.Single(order => order.OrderNumber == "ship-never").ShippedAt);

            var oldest = await sales.OrdersAsync(tenant.Id, 20, 1, null, new OrderListQuery(Sort: "SHIP_ASC"), CancellationToken.None);
            Assert.Equal(["ship-early", "ship-late", "ship-never"], oldest.Items.Select(order => order.OrderNumber));

            var first = await sales.OrdersAsync(tenant.Id, 1, 1, null, new OrderListQuery(Sort: "SHIP_DESC"), CancellationToken.None);
            var second = await sales.OrdersAsync(tenant.Id, 1, 1, first.NextCursor, new OrderListQuery(Sort: "SHIP_DESC"), CancellationToken.None);
            var third = await sales.OrdersAsync(tenant.Id, 1, 1, second.NextCursor, new OrderListQuery(Sort: "SHIP_DESC"), CancellationToken.None);
            Assert.Equal(["ship-late", "ship-early", "ship-never"], new[] { first, second, third }.Select(page => Assert.Single(page.Items).OrderNumber));
        }
        finally
        {
            await DeleteInvoiceTestTenantAsync(tenant.Id);
        }
    }

    [PostgreSqlFact]
    public async Task EffectSweep_ReachesANewRecordBehindAHundredAlreadyQueued()
    {
        var tenant = NewTenant("effect-sweep");
        var connection = NewQuestionConnection(tenant);
        var now = fixture.TimeProvider.GetUtcNow();
        ExternalEffectRecord Effect(int minutesAgo, string key) => new() { Id = Guid.CreateVersion7(), TenantId = tenant.Id, EffectType = "TRENDYOL_INSTANT_PICKING", IdempotencyKey = key, CreatedAt = now.AddMinutes(-minutesAgo), ConnectionId = connection.Id, TargetId = Guid.NewGuid(), Action = "PICKING" };
        var queued = Enumerable.Range(0, 105).Select(index => Effect(60 + index, $"sweep-queued-{index}")).ToArray();
        var fresh = Effect(5, "sweep-fresh");
        await using (var seedDb = fixture.CreateContext())
        {
            seedDb.Tenants.Add(tenant);
            seedDb.PlatformConnections.Add(connection);
            seedDb.ExternalEffectRecords.AddRange(queued);
            seedDb.ExternalEffectRecords.Add(fresh);
            seedDb.IntegrationJobs.AddRange(queued.Select(effect => ExternalEffectReconcilePolicy.NewJob(effect, now, now)));
            await seedDb.SaveChangesAsync();
        }

        try
        {
            await using (var db = fixture.CreateContext())
                await new ExternalEffectSweep(db, fixture.TimeProvider).SweepAsync(CancellationToken.None);

            await using var check = fixture.CreateContext();
            var freshKey = ExternalEffectReconcilePolicy.JobDedupKey(fresh.Id);
            Assert.True(await check.IntegrationJobs.AnyAsync(x => x.TenantId == tenant.Id && x.JobDedupKey == freshKey));
            Assert.Equal(106, await check.IntegrationJobs.CountAsync(x => x.TenantId == tenant.Id));
        }
        finally
        {
            await using var cleanupDb = fixture.CreateContext();
            await cleanupDb.IntegrationJobs.Where(row => row.TenantId == tenant.Id).ExecuteDeleteAsync();
            await cleanupDb.ExternalEffectRecords.Where(row => row.TenantId == tenant.Id).ExecuteDeleteAsync();
            await DeleteInvoiceTestTenantAsync(tenant.Id);
        }
    }

    [PostgreSqlFact]
    public async Task RunNow_StartsAFlowOnceAndRefusesStockWithoutWrites()
    {
        var tenant = NewTenant("run-now");
        var connection = NewQuestionConnection(tenant);
        await using (var seedDb = fixture.CreateContext())
        {
            seedDb.Tenants.Add(tenant);
            seedDb.PlatformConnections.Add(connection);
            await seedDb.SaveChangesAsync();
        }

        try
        {
            await using var db = fixture.CreateContext();
            var producer = new ScheduledJobProducer(db, fixture.TimeProvider, new ConfigurationBuilder().Build());

            var first = await producer.RunNowAsync(tenant.Id, connection.Id, "orders", "run-now-test", CancellationToken.None);
            var second = await producer.RunNowAsync(tenant.Id, connection.Id, "ORDERS", "run-now-test", CancellationToken.None);
            Assert.Null(first.Error);
            Assert.Equal(first.Value, second.Value);
            var job = await db.IntegrationJobs.AsNoTracking().SingleAsync(x => x.Id == first.Value);
            Assert.Equal("user", job.TriggerType);
            Assert.Equal(MarketplaceJobTypes.OrderSync, job.JobType);

            Assert.Equal("EXTERNAL_WRITES_DISABLED", (await producer.RunNowAsync(tenant.Id, connection.Id, "STOCK_RECONCILE_SHORT", "run-now-test", CancellationToken.None)).Error!.Code);
            Assert.Equal("SYNC_FLOW_NOT_RUNNABLE", (await producer.RunNowAsync(tenant.Id, connection.Id, "UNKNOWN_FLOW", "run-now-test", CancellationToken.None)).Error!.Code);
        }
        finally
        {
            await using var cleanupDb = fixture.CreateContext();
            await cleanupDb.IntegrationJobs.Where(row => row.TenantId == tenant.Id).ExecuteDeleteAsync();
            await DeleteInvoiceTestTenantAsync(tenant.Id);
        }
    }

    [PostgreSqlFact]
    public async Task SharedRateLimiter_TakesTokensUntilTheBucketIsEmptyThenReportsTheWait()
    {
        var key = $"test:rate:{Guid.NewGuid():N}";
        await using var connectionSource = fixture.CreateContext();
        await using var dataSource = Npgsql.NpgsqlDataSource.Create(connectionSource.Database.GetConnectionString()!);
        var limiter = new MarketplaceHub.Infrastructure.Adapters.PostgresSharedRateLimiter(dataSource);
        try
        {
            // Capacity 2 and a slow refill: two immediate tokens, then a wait of about 10 s.
            Assert.Equal(TimeSpan.Zero, await limiter.TryTakeAsync(key, 2, 0.1, CancellationToken.None));
            Assert.Equal(TimeSpan.Zero, await limiter.TryTakeAsync(key, 2, 0.1, CancellationToken.None));
            var wait = await limiter.TryTakeAsync(key, 2, 0.1, CancellationToken.None);
            Assert.InRange(wait.TotalSeconds, 8, 10.5);
            // Only taken tokens are counted, per minute.
            var counted = await connectionSource.Database.SqlQuery<int>($"SELECT coalesce(sum(\"Requests\"), 0)::int AS \"Value\" FROM integration.rate_limit_usage WHERE \"Key\" = {key}").SingleAsync();
            Assert.Equal(2, counted);
        }
        finally
        {
            await using var cleanup = fixture.CreateContext();
            await cleanup.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM integration.rate_limit_buckets WHERE \"Key\" = {key}");
            await cleanup.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM integration.rate_limit_usage WHERE \"Key\" = {key}");
        }
    }

    [PostgreSqlFact]
    public async Task JobRetention_RemovesOnlyOldFinishedScheduledJobs()
    {
        var tenant = NewTenant("job-retention");
        var now = DateTimeOffset.UtcNow;
        IntegrationJob Finished(string dedupPrefix, JobStatus status, int ageDays, string jobType = MarketplaceJobTypes.OrderSync)
        {
            var job = NewJob(tenant.Id);
            job.JobType = jobType;
            job.JobDedupKey = $"{dedupPrefix}:{Guid.NewGuid():N}";
            job.Status = status;
            job.CreatedAt = now.AddDays(-ageDays);
            job.CompletedAt = now.AddDays(-ageDays);
            return job;
        }
        var oldScheduledSuccess = Finished("scheduled", JobStatus.Succeeded, 20);
        var recentScheduledSuccess = Finished("scheduled", JobStatus.Succeeded, 3);
        var oldScheduledDead = Finished("scheduled", JobStatus.Dead, 40);
        var youngerScheduledDead = Finished("scheduled", JobStatus.Dead, 20);
        // Effect-guarding dedup keys and product syncs are never purged.
        var oldEffectJob = Finished("product-create", JobStatus.Succeeded, 400, MarketplaceJobTypes.ProductCreate);
        var oldProductSync = Finished("scheduled", JobStatus.Succeeded, 400, MarketplaceJobTypes.ProductSync);
        var oldPending = Finished("scheduled", JobStatus.Pending, 400);
        oldPending.CompletedAt = null;
        var all = new[] { oldScheduledSuccess, recentScheduledSuccess, oldScheduledDead, youngerScheduledDead, oldEffectJob, oldProductSync, oldPending };

        await using (var setup = fixture.CreateContext())
        {
            setup.Tenants.Add(tenant);
            setup.IntegrationJobs.AddRange(all);
            setup.JobAttempts.Add(new JobAttempt { Id = Guid.NewGuid(), TenantId = tenant.Id, JobId = oldScheduledSuccess.Id, AttemptNumber = 1, StartedAt = now.AddDays(-20) });
            await setup.SaveChangesAsync();
        }

        try
        {
            await using var db = fixture.CreateContext();
            var service = new JobRetentionService(db, fixture.TimeProvider);
            await service.PurgeAsync(TimeSpan.FromDays(14), TimeSpan.FromDays(30), maximumBatches: 5, CancellationToken.None);

            var ids = all.Select(job => job.Id).ToArray();
            var remaining = await db.IntegrationJobs.AsNoTracking().Where(job => ids.Contains(job.Id)).Select(job => job.Id).ToListAsync();
            Assert.DoesNotContain(oldScheduledSuccess.Id, remaining);
            Assert.DoesNotContain(oldScheduledDead.Id, remaining);
            Assert.Contains(recentScheduledSuccess.Id, remaining);
            Assert.Contains(youngerScheduledDead.Id, remaining);
            Assert.Contains(oldEffectJob.Id, remaining);
            Assert.Contains(oldProductSync.Id, remaining);
            Assert.Contains(oldPending.Id, remaining);
            Assert.False(await db.JobAttempts.AsNoTracking().AnyAsync(attempt => attempt.JobId == oldScheduledSuccess.Id));
        }
        finally
        {
            var ids = all.Select(job => job.Id).ToArray();
            await using var cleanup = fixture.CreateContext();
            await cleanup.IntegrationJobs.Where(job => ids.Contains(job.Id)).ExecuteDeleteAsync();
            await cleanup.Tenants.Where(x => x.Id == tenant.Id).ExecuteDeleteAsync();
        }
    }

    [PostgreSqlFact]
    public async Task JobRetention_RemovesFinishedReadsAndProcessedInboxRowsButKeepsEffectGuards()
    {
        var tenant = NewTenant("job-retention-reads");
        var now = DateTimeOffset.UtcNow;
        IntegrationJob Finished(string dedupPrefix, JobStatus status, int ageDays, string jobType)
        {
            var job = NewJob(tenant.Id);
            job.JobType = jobType;
            job.JobDedupKey = $"{dedupPrefix}:{Guid.NewGuid():N}";
            job.Status = status;
            job.CreatedAt = now.AddDays(-ageDays);
            job.CompletedAt = now.AddDays(-ageDays);
            return job;
        }
        var oldWebhook = Finished("webhook", JobStatus.Succeeded, 20, MarketplaceJobTypes.WebhookIngest);
        var recentWebhook = Finished("webhook", JobStatus.Succeeded, 3, MarketplaceJobTypes.WebhookIngest);
        var oldVerification = Finished("effect-reconcile", JobStatus.Succeeded, 20, MarketplaceJobTypes.ExternalEffectReconcile);
        var deadManual = Finished("manual:scheduled:orders", JobStatus.Dead, 20, MarketplaceJobTypes.OrderSync);
        var olderDeadManual = Finished("manual:scheduled:orders", JobStatus.Dead, 40, MarketplaceJobTypes.OrderSync);
        var oldTargetedRead = Finished("targeted-order", JobStatus.Succeeded, 40, MarketplaceJobTypes.OrderSync);
        var youngerTargetedRead = Finished("targeted-order", JobStatus.Succeeded, 20, MarketplaceJobTypes.OrderSync);
        // Effect guards and activation bootstraps stay whatever their age or dedup key.
        var oldManualWrite = Finished("manual", JobStatus.Succeeded, 400, MarketplaceJobTypes.ShipmentAction);
        var oldInvoiceSubmit = Finished("invoice-submit", JobStatus.Succeeded, 400, InvoicingJobTypes.InvoiceSubmit);
        var oldBootstrap = Finished("activation-bootstrap:connection", JobStatus.Succeeded, 400, MarketplaceJobTypes.OrderRecoverySync);
        var all = new[] { oldWebhook, recentWebhook, oldVerification, deadManual, olderDeadManual, oldTargetedRead, youngerTargetedRead, oldManualWrite, oldInvoiceSubmit, oldBootstrap };
        InboxMessage Inbox(int? processedDaysAgo) => new()
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenant.Id,
            Source = "TRENDYOL_WEBHOOK",
            ExternalMessageId = Guid.NewGuid().ToString("N"),
            PayloadHash = "hash",
            ReceivedAt = now.AddDays(-(processedDaysAgo ?? 60)),
            ProcessedAt = processedDaysAgo is { } days ? now.AddDays(-days) : null
        };
        var oldProcessed = Inbox(40);
        var recentProcessed = Inbox(10);
        var neverProcessed = Inbox(null);

        await using (var setup = fixture.CreateContext())
        {
            setup.Tenants.Add(tenant);
            setup.IntegrationJobs.AddRange(all);
            setup.InboxMessages.AddRange(oldProcessed, recentProcessed, neverProcessed);
            await setup.SaveChangesAsync();
        }

        try
        {
            await using var db = fixture.CreateContext();
            var service = new JobRetentionService(db, fixture.TimeProvider);
            await service.PurgeAsync(TimeSpan.FromDays(14), TimeSpan.FromDays(30), maximumBatches: 5, CancellationToken.None);
            await service.PurgeProcessedInboxAsync(TimeSpan.FromDays(30), maximumBatches: 5, CancellationToken.None);

            var ids = all.Select(job => job.Id).ToArray();
            var remaining = await db.IntegrationJobs.AsNoTracking().Where(job => ids.Contains(job.Id)).Select(job => job.Id).ToListAsync();
            Assert.DoesNotContain(oldWebhook.Id, remaining);
            Assert.DoesNotContain(oldVerification.Id, remaining);
            Assert.DoesNotContain(olderDeadManual.Id, remaining);
            Assert.DoesNotContain(oldTargetedRead.Id, remaining);
            Assert.Contains(recentWebhook.Id, remaining);
            Assert.Contains(deadManual.Id, remaining);
            Assert.Contains(youngerTargetedRead.Id, remaining);
            Assert.Contains(oldManualWrite.Id, remaining);
            Assert.Contains(oldInvoiceSubmit.Id, remaining);
            Assert.Contains(oldBootstrap.Id, remaining);
            var inbox = await db.InboxMessages.AsNoTracking().Where(row => row.TenantId == tenant.Id).Select(row => row.Id).ToListAsync();
            Assert.DoesNotContain(oldProcessed.Id, inbox);
            Assert.Contains(recentProcessed.Id, inbox);
            Assert.Contains(neverProcessed.Id, inbox);
        }
        finally
        {
            await using var cleanup = fixture.CreateContext();
            await cleanup.IntegrationJobs.Where(job => job.TenantId == tenant.Id).ExecuteDeleteAsync();
            await cleanup.InboxMessages.Where(row => row.TenantId == tenant.Id).ExecuteDeleteAsync();
            await cleanup.Tenants.Where(x => x.Id == tenant.Id).ExecuteDeleteAsync();
        }
    }

    [PostgreSqlFact]
    public async Task OperationalIssueRetention_DeletesResolvedIssuesNotSeenForAMonth()
    {
        var tenant = NewTenant("issue-retention-delete");
        await using var db = fixture.CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        db.Tenants.Add(tenant);
        var oldResolved = NewIssue(tenant.Id, "old-resolved");
        oldResolved.Status = IssueStatus.Resolved;
        oldResolved.FirstSeenAt = oldResolved.LastSeenAt = fixture.Now.AddDays(-45);
        var recentResolved = NewIssue(tenant.Id, "recent-resolved");
        recentResolved.Status = IssueStatus.Resolved;
        recentResolved.LastSeenAt = fixture.Now.AddDays(-5);
        var oldOpen = NewIssue(tenant.Id, "old-open");
        oldOpen.FirstSeenAt = oldOpen.LastSeenAt = fixture.Now.AddDays(-45);
        db.OperationalIssues.AddRange(oldResolved, recentResolved, oldOpen);
        await db.SaveChangesAsync();

        var deleted = await new OperationalIssueRetentionService(db, fixture.TimeProvider).DeleteResolvedAsync(TimeSpan.FromDays(30), CancellationToken.None);

        Assert.Equal(1, deleted);
        db.ChangeTracker.Clear();
        var remaining = await db.OperationalIssues.AsNoTracking().Where(x => x.TenantId == tenant.Id).Select(x => x.Id).ToListAsync();
        Assert.DoesNotContain(oldResolved.Id, remaining);
        Assert.Contains(recentResolved.Id, remaining);
        Assert.Contains(oldOpen.Id, remaining);
        await transaction.RollbackAsync();
    }

    [PostgreSqlFact]
    public async Task EffectSweep_RequeuesAVerificationWhoseJobEndedAndClosesTheRecordAfterTheCap()
    {
        var tenant = NewTenant("effect-sweep-requeue");
        var connection = NewQuestionConnection(tenant);
        var now = fixture.TimeProvider.GetUtcNow();
        ExternalEffectRecord Effect(string key) => new() { Id = Guid.CreateVersion7(), TenantId = tenant.Id, EffectType = "TRENDYOL_INSTANT_PICKING", IdempotencyKey = key, CreatedAt = now.AddMinutes(-40), ConnectionId = connection.Id, TargetId = Guid.NewGuid(), Action = "PICKING" };
        IntegrationJob Ended(ExternalEffectRecord effect, string key, JobStatus status)
        {
            var job = ExternalEffectReconcilePolicy.NewJob(effect, now.AddMinutes(-30), now.AddMinutes(-30));
            job.JobDedupKey = key;
            job.EffectIdempotencyKey = key;
            job.Status = status;
            job.CompletedAt = now.AddMinutes(-20);
            return job;
        }
        var deadOnce = Effect("sweep-dead-once");
        var exhausted = Effect("sweep-exhausted");
        var exhaustedKeys = ExternalEffectSweep.VerificationKeys(exhausted.Id);
        await using (var seedDb = fixture.CreateContext())
        {
            seedDb.Tenants.Add(tenant);
            seedDb.PlatformConnections.Add(connection);
            seedDb.ExternalEffectRecords.AddRange(deadOnce, exhausted);
            seedDb.IntegrationJobs.Add(Ended(deadOnce, ExternalEffectReconcilePolicy.JobDedupKey(deadOnce.Id), JobStatus.Dead));
            seedDb.IntegrationJobs.AddRange(exhaustedKeys.Select(key => Ended(exhausted, key, JobStatus.Blocked)));
            await seedDb.SaveChangesAsync();
        }

        try
        {
            await using (var db = fixture.CreateContext())
                Assert.Equal(2, await new ExternalEffectSweep(db, fixture.TimeProvider).SweepAsync(CancellationToken.None));

            await using var check = fixture.CreateContext();
            var retry = await check.IntegrationJobs.AsNoTracking().SingleAsync(x => x.TenantId == tenant.Id && x.JobDedupKey == $"{ExternalEffectReconcilePolicy.JobDedupKey(deadOnce.Id)}:2");
            Assert.Equal(JobStatus.Pending, retry.Status);
            Assert.Null((await check.ExternalEffectRecords.AsNoTracking().SingleAsync(x => x.Id == deadOnce.Id)).CompletedAt);
            var closed = await check.ExternalEffectRecords.AsNoTracking().SingleAsync(x => x.Id == exhausted.Id);
            Assert.Equal("UNRESOLVED", closed.Outcome);
            Assert.NotNull(closed.CompletedAt);
            Assert.True(await check.OperationalIssues.AnyAsync(x => x.TenantId == tenant.Id && x.DedupeKey == $"external-effect:{exhausted.Id}" && x.Status == IssueStatus.Open));
        }
        finally
        {
            await using var cleanupDb = fixture.CreateContext();
            await cleanupDb.IntegrationJobs.Where(row => row.TenantId == tenant.Id).ExecuteDeleteAsync();
            await cleanupDb.ExternalEffectRecords.Where(row => row.TenantId == tenant.Id).ExecuteDeleteAsync();
            await DeleteInvoiceTestTenantAsync(tenant.Id);
        }
    }

    [PostgreSqlFact]
    public async Task JobLease_BumpsTheVersionAndTellsThePanelTheJobStarted()
    {
        var tenant = NewTenant("lease-outbox");
        var job = NewJob(tenant.Id);
        await using (var setup = fixture.CreateContext())
        {
            setup.Tenants.Add(tenant);
            setup.IntegrationJobs.Add(job);
            await setup.SaveChangesAsync();
        }

        try
        {
            await using var db = fixture.CreateContext();
            var lease = await new JobLeaseService(db, fixture.TokenHasher, fixture.TimeProvider)
                .TryLeaseAsync(TimeSpan.FromMinutes(2), null, null, CancellationToken.None, onlyJobId: job.Id);
            Assert.Equal(job.Id, lease?.Id);

            await using var verification = fixture.CreateContext();
            var leased = await verification.IntegrationJobs.AsNoTracking().SingleAsync(x => x.Id == job.Id);
            Assert.Equal(JobStatus.Leased, leased.Status);
            Assert.Equal(2, leased.Version);
            var started = await verification.IntegrationOutboxEvents.AsNoTracking().SingleAsync(x => x.TenantId == tenant.Id && x.AggregateId == job.Id);
            Assert.Equal(2, started.AggregateVersion);
            Assert.Contains("LEASED", started.PayloadJson);
        }
        finally
        {
            await using var cleanup = fixture.CreateContext();
            await cleanup.JobAttempts.Where(x => x.JobId == job.Id).ExecuteDeleteAsync();
            await DeleteJobAndTenantAsync(job.Id, tenant.Id);
        }
    }

    [PostgreSqlFact]
    public async Task LeaseReaper_RecoversExhaustedAndExpiredJobsInOnePass()
    {
        var tenant = NewTenant("lease-reaper");
        var exhausted = NewJob(tenant.Id);
        exhausted.Status = JobStatus.RetryScheduled;
        exhausted.AttemptCount = exhausted.MaxAttempts;
        var expired = NewJob(tenant.Id);
        expired.Status = JobStatus.Leased;
        expired.AttemptCount = 1;
        expired.LeaseTokenHash = "expired-lease";
        expired.LeaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        await using (var setup = fixture.CreateContext())
        {
            setup.Tenants.Add(tenant);
            setup.IntegrationJobs.AddRange(exhausted, expired);
            setup.JobAttempts.Add(new JobAttempt { Id = Guid.NewGuid(), TenantId = tenant.Id, JobId = expired.Id, AttemptNumber = 1, StartedAt = DateTimeOffset.UtcNow.AddMinutes(-10) });
            await setup.SaveChangesAsync();
        }

        var jobIds = new[] { exhausted.Id, expired.Id };
        try
        {
            await using var db = fixture.CreateContext();
            var reaped = await new JobLeaseService(db, fixture.TokenHasher, fixture.TimeProvider).ReapExpiredAsync(CancellationToken.None);

            Assert.True(reaped >= 2);
            await using var verification = fixture.CreateContext();
            Assert.Equal(JobStatus.Dead, (await verification.IntegrationJobs.AsNoTracking().SingleAsync(x => x.Id == exhausted.Id)).Status);
            var recovered = await verification.IntegrationJobs.AsNoTracking().SingleAsync(x => x.Id == expired.Id);
            Assert.Equal(JobStatus.RetryScheduled, recovered.Status);
            Assert.Equal("LEASE_EXPIRED", recovered.LastErrorCode);
            Assert.Equal(2, await verification.IntegrationOutboxEvents.CountAsync(x => x.TenantId == tenant.Id));
        }
        finally
        {
            await using var cleanup = fixture.CreateContext();
            await cleanup.JobAttempts.Where(x => jobIds.Contains(x.JobId)).ExecuteDeleteAsync();
            await cleanup.IntegrationJobs.Where(x => jobIds.Contains(x.Id)).ExecuteDeleteAsync();
            await cleanup.IntegrationOutboxEvents.Where(x => x.TenantId == tenant.Id).ExecuteDeleteAsync();
            await cleanup.Tenants.Where(x => x.Id == tenant.Id).ExecuteDeleteAsync();
        }
    }

    [PostgreSqlFact]
    public async Task Scheduler_RemembersWhenItQueuedAPolicyAndLeavesUserPoliciesAlone()
    {
        var tenant = NewTenant("scheduler-policies");
        var connection = NewQuestionConnection(tenant);
        // A user chose a historical default for the order poll; it must survive the defaults pass.
        var userOrders = new ConnectionSyncPolicy { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, ResourceType = "ORDERS", IntervalSeconds = 180, OverlapSeconds = 600, JitterSeconds = 5, Enabled = true, UserDefined = true, Version = 1 };
        // An untouched historical default is upgraded.
        var defaultReturns = new ConnectionSyncPolicy { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, ResourceType = "RETURNS", IntervalSeconds = 60, OverlapSeconds = 900, JitterSeconds = 5, Enabled = true, Version = 1 };
        await using (var seedDb = fixture.CreateContext())
        {
            seedDb.Tenants.Add(tenant);
            seedDb.PlatformConnections.Add(connection);
            seedDb.ConnectionSyncPolicies.AddRange(userOrders, defaultReturns);
            await seedDb.SaveChangesAsync();
        }

        try
        {
            await using (var db = fixture.CreateContext())
            {
                var producer = new ScheduledJobProducer(db, fixture.TimeProvider, new ConfigurationBuilder().Build());
                await producer.MaintainPoliciesAsync(includeDefaults: true, CancellationToken.None);
                await producer.EnqueueDueAsync(CancellationToken.None);
            }

            await using var check = fixture.CreateContext();
            var orders = await check.ConnectionSyncPolicies.AsNoTracking().SingleAsync(x => x.Id == userOrders.Id);
            Assert.Equal(180, orders.IntervalSeconds);
            Assert.Equal(1, orders.Version);
            Assert.NotEqual(60, (await check.ConnectionSyncPolicies.AsNoTracking().SingleAsync(x => x.Id == defaultReturns.Id)).IntervalSeconds);
            var queued = await check.IntegrationJobs.AsNoTracking().SingleAsync(x => x.TenantId == tenant.Id && x.JobType == MarketplaceJobTypes.OrderSync);
            Assert.Equal(queued.AvailableAt, orders.LastScheduledAt);
        }
        finally
        {
            await using var cleanupDb = fixture.CreateContext();
            await cleanupDb.IntegrationJobs.Where(row => row.TenantId == tenant.Id).ExecuteDeleteAsync();
            await cleanupDb.ConnectionSyncPolicies.Where(row => row.TenantId == tenant.Id).ExecuteDeleteAsync();
            await DeleteInvoiceTestTenantAsync(tenant.Id);
        }
    }

    [PostgreSqlFact]
    public async Task TrendyolOrderSync_WritesNoHistoryRowWhenOnlyTheModificationDateChanged()
    {
        var tenant = NewTenant("ty-history-unchanged");
        var connection = NewQuestionConnection(tenant);
        var orderId = Guid.CreateVersion7();
        var lineId = Guid.CreateVersion7();
        var packageId = Guid.CreateVersion7();
        const string externalOrderId = "11697863999";
        const string packageNumber = "4234395999";
        var createdAt = DateTimeOffset.FromUnixTimeSeconds(fixture.Now.AddMinutes(-30).ToUnixTimeSeconds());
        var modifiedAt = createdAt.AddMinutes(5);
        var order = NewInvoiceTestOrder(tenant.Id, connection.Id, orderId, externalOrderId, isReturnClaim: false, fixture.Now);
        order.DerivedStatus = "NEW";
        order.LastRemoteModifiedAt = createdAt;
        await using (var seedDb = fixture.CreateContext())
        {
            seedDb.Tenants.Add(tenant);
            seedDb.PlatformConnections.Add(connection);
            seedDb.Orders.Add(order);
            seedDb.OrderLines.Add(new OrderLine { Id = lineId, TenantId = tenant.Id, OrderId = orderId, ExternalLineId = "line-1", Sku = "SKU-1", TitleSnapshot = "Test ürün", OrderedQuantity = 1m, UnitPrice = 100m, VatRate = 10m, RawStatus = "Created", Version = 1 });
            seedDb.ShipmentPackages.Add(new ShipmentPackage { Id = packageId, TenantId = tenant.Id, ConnectionId = connection.Id, OrderId = orderId, ExternalPackageId = packageNumber, GrossAmount = 100m, NetAmount = 100m, Status = ShipmentPackageStatus.New, RawStatus = "Created", StatusOccurredAt = createdAt, CreatedAt = createdAt, UpdatedAt = createdAt, Version = 1 });
            seedDb.PackageLineAllocations.Add(new PackageLineAllocation { Id = Guid.CreateVersion7(), TenantId = tenant.Id, PackageId = packageId, OrderLineId = lineId, AllocatedQuantity = 1m, SourceEventId = PackageIngestionSafety.EventId(packageNumber, createdAt) });
            seedDb.OrderStatusHistory.Add(new OrderStatusHistory { Id = Guid.CreateVersion7(), TenantId = tenant.Id, OrderId = orderId, PackageId = packageId, CanonicalStatus = "NEW", RawStatus = "Created", SourceEventId = PackageIngestionSafety.EventId(packageNumber, createdAt), OccurredAt = createdAt, RecordedAt = createdAt });
            await seedDb.SaveChangesAsync();
        }

        async Task SyncAsync(string rawStatus, DateTimeOffset occurredAt)
        {
            var remotePackage = new RemotePackage(packageNumber, null, rawStatus, occurredAt, null, null,
                [new RemotePackageAllocation("line-1", 1m, 0m, 0m, 0m, 0m)], GrossAmount: 100m, NetAmount: 100m);
            var remoteOrder = new RemoteOrder(externalOrderId, externalOrderId, createdAt.AddMinutes(-1), occurredAt, "TRY", 100m, 0m, 100m, "{}", "{}", "{}",
                [new RemoteOrderLine("line-1", "SKU-1", null, "Test ürün", 1m, 100m, 10m, rawStatus)], [remotePackage], "{}");
            await using var processorDb = fixture.CreateContext();
            var processor = new MarketplaceJobProcessor(
                processorDb,
                null!, null!, null!, null!, new ReadOnlyOrderTestPort(remotePackage, externalOrderId: externalOrderId, directOrderResult: remoteOrder), null!, null!, null!, null!, null!,
                new ConfigurationBuilder().Build(), fixture.TimeProvider);
            var result = await processor.ProcessAsync(tenant.Id, connection.Id, MarketplaceJobTypes.OrderSync,
                $$"""{"externalOrderId":"{{externalOrderId}}","full":false,"packageNumber":"{{packageNumber}}"}""", "history-unchanged", CancellationToken.None);
            Assert.True(result.Succeeded, result.ErrorSummary);
        }

        try
        {
            // Same status, newer modification date: the package moves on, the history does not grow.
            await SyncAsync("Created", modifiedAt);
            long versionAfterChange;
            await using (var check = fixture.CreateContext())
            {
                Assert.Equal(1, await check.OrderStatusHistory.CountAsync(x => x.TenantId == tenant.Id));
                var package = await check.ShipmentPackages.AsNoTracking().SingleAsync(x => x.Id == packageId);
                Assert.Equal(modifiedAt, package.StatusOccurredAt);
                versionAfterChange = package.Version;
            }

            // The overlap window reads the same observation again: nothing changes.
            await SyncAsync("Created", modifiedAt);
            await using (var check = fixture.CreateContext())
            {
                Assert.Equal(1, await check.OrderStatusHistory.CountAsync(x => x.TenantId == tenant.Id));
                Assert.Equal(versionAfterChange, (await check.ShipmentPackages.AsNoTracking().SingleAsync(x => x.Id == packageId)).Version);
            }

            // A real status change is recorded.
            await SyncAsync("Picking", modifiedAt.AddMinutes(5));
            await using (var check = fixture.CreateContext())
                Assert.Equal(2, await check.OrderStatusHistory.CountAsync(x => x.TenantId == tenant.Id));
        }
        finally
        {
            await DeleteInvoiceTestTenantAsync(tenant.Id);
        }
    }

    [PostgreSqlFact]
    public async Task JobsTrigger_NotifiesListenersWhenAJobBecomesRunnable()
    {
        var tenant = NewTenant("job-notify");
        var job = NewJob(tenant.Id);
        await using (var setup = fixture.CreateContext())
        {
            setup.Tenants.Add(tenant);
            await setup.SaveChangesAsync();
        }

        try
        {
            await using var listenerDb = fixture.CreateContext();
            var listener = (Npgsql.NpgsqlConnection)listenerDb.Database.GetDbConnection();
            await listener.OpenAsync();
            var notifications = 0;
            listener.Notification += (_, args) => { if (args.Channel == IntegrationJobNotifications.Channel) notifications++; };
            await using (var listen = new Npgsql.NpgsqlCommand($"LISTEN {IntegrationJobNotifications.Channel}", listener))
                await listen.ExecuteNonQueryAsync();

            await using (var writer = fixture.CreateContext())
            {
                writer.IntegrationJobs.Add(job);
                await writer.SaveChangesAsync();
            }

            Assert.True(await listener.WaitAsync(TimeSpan.FromSeconds(5)));
            // Other test classes may insert jobs concurrently, so at least one notification is expected.
            Assert.True(notifications >= 1);
        }
        finally
        {
            await DeleteJobAndTenantAsync(job.Id, tenant.Id);
        }
    }

    [PostgreSqlFact]
    public async Task JobLease_SkipsAConnectionLockGroupThatHitAQuotaUntilItsBackoffEnds()
    {
        var tenant = NewTenant("lease-quota-backoff");
        var connectionId = Guid.CreateVersion7();
        var limitedRead = NewJob(tenant.Id);
        limitedRead.JobType = MarketplaceJobTypes.OrderSync;
        limitedRead.ConnectionId = connectionId;
        limitedRead.Priority = 0;
        var sameGroupRead = NewJob(tenant.Id);
        sameGroupRead.JobType = MarketplaceJobTypes.OrderStatusSync;
        sameGroupRead.ConnectionId = connectionId;
        sameGroupRead.Priority = 1;
        var otherGroup = NewJob(tenant.Id);
        otherGroup.JobType = MarketplaceJobTypes.ReturnSync;
        otherGroup.ConnectionId = connectionId;
        otherGroup.Priority = 2;

        await using (var setup = fixture.CreateContext())
        {
            setup.Tenants.Add(tenant);
            setup.IntegrationJobs.AddRange(limitedRead, sameGroupRead, otherGroup);
            await setup.SaveChangesAsync();
        }

        var jobIds = new[] { limitedRead.Id, sameGroupRead.Id, otherGroup.Id };
        try
        {
            await using var db = fixture.CreateContext();
            var leaseService = new JobLeaseService(db, fixture.TokenHasher, fixture.TimeProvider);
            var first = await leaseService.TryLeaseAsync(TimeSpan.FromMinutes(2), null, null, CancellationToken.None);
            Assert.Equal(limitedRead.Id, first?.Id);

            var quota = JobExecutionResult.FromAdapterError(new AdapterError(AdapterErrorClass.RateLimit, "REMOTE_RATE_LIMITED", "Platform hız sınırı yanıtı verdi.", 429, TimeSpan.FromMinutes(2), null));
            Assert.True(await leaseService.CompleteAsync(first!.Id, first.LeaseToken, quota, CancellationToken.None));

            // The orders group of this connection is backing off; the returns group is not.
            var second = await leaseService.TryLeaseAsync(TimeSpan.FromMinutes(2), null, null, CancellationToken.None);
            var third = await leaseService.TryLeaseAsync(TimeSpan.FromMinutes(2), null, null, CancellationToken.None);
            Assert.Equal(otherGroup.Id, second?.Id);
            Assert.Null(third);

            await using var verification = fixture.CreateContext();
            var limited = await verification.IntegrationJobs.AsNoTracking().SingleAsync(x => x.Id == limitedRead.Id);
            Assert.Equal(0, limited.AttemptCount);
            Assert.Equal(JobStatus.RetryScheduled, limited.Status);
        }
        finally
        {
            await using var cleanup = fixture.CreateContext();
            await cleanup.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM integration.connection_backoffs WHERE \"ConnectionId\" = {connectionId}");
            await cleanup.IntegrationOutboxEvents.Where(x => x.TenantId == tenant.Id).ExecuteDeleteAsync();
            await cleanup.JobAttempts.Where(x => jobIds.Contains(x.JobId)).ExecuteDeleteAsync();
            await cleanup.IntegrationJobs.Where(x => jobIds.Contains(x.Id)).ExecuteDeleteAsync();
            await cleanup.Tenants.Where(x => x.Id == tenant.Id).ExecuteDeleteAsync();
        }
    }

    [PostgreSqlFact]
    public async Task JobLease_PrefersTheTenantWithFewerRunningJobsWithinOnePriority()
    {
        var busyTenant = NewTenant("lease-fair-busy");
        var quietTenant = NewTenant("lease-fair-quiet");
        var running = NewJob(busyTenant.Id);
        running.Status = JobStatus.Leased;
        running.AttemptCount = 1;
        running.LeaseTokenHash = "running-lease";
        running.LeaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(2);
        var busyQueued = NewJob(busyTenant.Id);
        var quietQueued = NewJob(quietTenant.Id);
        // The busy tenant's job is older; without fairness it would be leased first.
        quietQueued.AvailableAt = busyQueued.AvailableAt.AddSeconds(5);
        quietQueued.CreatedAt = busyQueued.CreatedAt.AddSeconds(5);
        // A higher-priority job still wins regardless of fairness.
        var urgentBusy = NewJob(busyTenant.Id);
        urgentBusy.Priority = 0;

        await using (var setup = fixture.CreateContext())
        {
            setup.Tenants.AddRange(busyTenant, quietTenant);
            setup.IntegrationJobs.AddRange(running, busyQueued, quietQueued, urgentBusy);
            await setup.SaveChangesAsync();
        }

        var jobIds = new[] { running.Id, busyQueued.Id, quietQueued.Id, urgentBusy.Id };
        try
        {
            await using var db = fixture.CreateContext();
            var leaseService = new JobLeaseService(db, fixture.TokenHasher, fixture.TimeProvider);

            var first = await leaseService.TryLeaseAsync(TimeSpan.FromMinutes(2), null, null, CancellationToken.None);
            var second = await leaseService.TryLeaseAsync(TimeSpan.FromMinutes(2), null, null, CancellationToken.None);
            var third = await leaseService.TryLeaseAsync(TimeSpan.FromMinutes(2), null, null, CancellationToken.None);

            Assert.Equal(urgentBusy.Id, first?.Id);
            Assert.Equal(quietQueued.Id, second?.Id);
            Assert.Equal(busyQueued.Id, third?.Id);
        }
        finally
        {
            await using var cleanup = fixture.CreateContext();
            await cleanup.JobAttempts.Where(x => jobIds.Contains(x.JobId)).ExecuteDeleteAsync();
            await cleanup.IntegrationJobs.Where(x => jobIds.Contains(x.Id)).ExecuteDeleteAsync();
            await cleanup.IntegrationOutboxEvents.Where(x => x.TenantId == busyTenant.Id || x.TenantId == quietTenant.Id).ExecuteDeleteAsync();
            await cleanup.Tenants.Where(x => x.Id == busyTenant.Id || x.Id == quietTenant.Id).ExecuteDeleteAsync();
        }
    }

    [PostgreSqlFact]
    public async Task JobLease_HepsiburadaStatusLaneSkipsOtherMarketplaceJobs()
    {
        var tenant = NewTenant("lease-hepsiburada-status-lane");
        var unrelatedJob = NewJob(tenant.Id);
        unrelatedJob.JobType = MarketplaceJobTypes.OrderStatusSync;
        unrelatedJob.Priority = 0;
        var hepsiburadaLifecycle = NewJob(tenant.Id);
        hepsiburadaLifecycle.JobType = MarketplaceJobTypes.HepsiburadaOrderStatusSync;
        hepsiburadaLifecycle.Priority = 0;
        hepsiburadaLifecycle.CreatedAt = unrelatedJob.CreatedAt.AddSeconds(1);
        hepsiburadaLifecycle.AvailableAt = unrelatedJob.AvailableAt.AddSeconds(1);

        await using (var setup = fixture.CreateContext())
        {
            setup.Tenants.Add(tenant);
            setup.IntegrationJobs.AddRange(unrelatedJob, hepsiburadaLifecycle);
            await setup.SaveChangesAsync();
        }

        try
        {
            await using var db = fixture.CreateContext();
            var leaseService = new JobLeaseService(db, fixture.TokenHasher, fixture.TimeProvider);
            var lease = await leaseService.TryLeaseAsync(
                TimeSpan.FromMinutes(2),
                2,
                null,
                CancellationToken.None,
                MarketplaceJobTypes.HepsiburadaOrderStatusSync);

            Assert.Equal(hepsiburadaLifecycle.Id, lease?.Id);
            await using var verification = fixture.CreateContext();
            Assert.Equal(JobStatus.Pending, (await verification.IntegrationJobs.SingleAsync(x => x.Id == unrelatedJob.Id)).Status);
        }
        finally
        {
            var jobIds = new[] { unrelatedJob.Id, hepsiburadaLifecycle.Id };
            await using var cleanup = fixture.CreateContext();
            await cleanup.IntegrationJobs.Where(x => jobIds.Contains(x.Id)).ExecuteDeleteAsync();
            await cleanup.IntegrationOutboxEvents.Where(x => x.TenantId == tenant.Id).ExecuteDeleteAsync();
            await cleanup.Tenants.Where(x => x.Id == tenant.Id).ExecuteDeleteAsync();
        }
    }

    [PostgreSqlFact]
    public async Task ApiIdempotencyKey_IsScopedToTenant_ButDuplicateWithinTenantIsRejected()
    {
        var firstTenant = NewTenant("idempotency-a");
        var secondTenant = NewTenant("idempotency-b");
        const string route = "/api/v1/catalog/products";
        const string key = "same-idempotency-key";

        await using var db = fixture.CreateContext();
        try
        {
            db.Tenants.AddRange(firstTenant, secondTenant);
            db.ApiIdempotencyRecords.AddRange(
                NewIdempotencyRecord(firstTenant.Id, route, key),
                NewIdempotencyRecord(secondTenant.Id, route, key));
            await db.SaveChangesAsync();

            db.ChangeTracker.Clear();
            db.ApiIdempotencyRecords.Add(NewIdempotencyRecord(firstTenant.Id, route, key));
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }
        finally
        {
            await DeleteIdempotencyRecordsAndTenantsAsync(firstTenant.Id, secondTenant.Id);
        }
    }

    [PostgreSqlFact]
    public async Task IdempotencyMiddleware_ReplaysCompletedResponseFromPostgreSql()
    {
        var tenant = NewTenant("middleware-replay");
        const string path = "/api/v1/catalog/products";
        const string key = "middleware-replay-key";
        const string requestBody = "{}";
        const string responseBody = "{\"id\":\"replayed\"}";

        await using var db = fixture.CreateContext();
        try
        {
            db.Tenants.Add(tenant);
            var record = NewIdempotencyRecord(tenant.Id, path, key);
            record.RequestHash = ComputeRequestHash("POST", path, string.Empty, requestBody);
            record.State = "COMPLETED";
            record.ResponseStatus = StatusCodes.Status201Created;
            record.ResponseBody = responseBody;
            db.ApiIdempotencyRecords.Add(record);
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var context = NewIdempotencyHttpContext(path, key, requestBody);
            var nextCalled = false;
            var middleware = new IdempotencyMiddleware(_ =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            });

            await middleware.InvokeAsync(context, db, new FixedTenantContextAccessor(tenant.Id), fixture.TimeProvider);

            Assert.False(nextCalled);
            Assert.Equal(StatusCodes.Status201Created, context.Response.StatusCode);
            Assert.Equal("true", context.Response.Headers["Idempotency-Replayed"].ToString());
            context.Response.Body.Position = 0;
            using var reader = new StreamReader(context.Response.Body, Encoding.UTF8, leaveOpen: true);
            Assert.Equal(responseBody, await reader.ReadToEndAsync());
        }
        finally
        {
            await DeleteIdempotencyRecordsAndTenantsAsync(tenant.Id);
        }
    }

    [PostgreSqlFact]
    public async Task IdempotencyMiddleware_PersistsUnknownAfterEndpointFailure()
    {
        var tenant = NewTenant("middleware-unknown");
        const string path = "/api/v1/catalog/products";
        const string key = "middleware-unknown-key";

        await using var db = fixture.CreateContext();
        try
        {
            db.Tenants.Add(tenant);
            await db.SaveChangesAsync();

            var context = NewIdempotencyHttpContext(path, key, "{\"title\":\"test\"}");
            var middleware = new IdempotencyMiddleware(_ => throw new InvalidOperationException("simulated endpoint failure"));

            await Assert.ThrowsAsync<InvalidOperationException>(() => middleware.InvokeAsync(
                context,
                db,
                new FixedTenantContextAccessor(tenant.Id),
                fixture.TimeProvider));

            db.ChangeTracker.Clear();
            var persisted = await db.ApiIdempotencyRecords.SingleAsync(x => x.TenantId == tenant.Id && x.RouteTemplate == path && x.IdempotencyKey == key);
            Assert.Equal("UNKNOWN", persisted.State);
            Assert.Null(persisted.ResponseStatus);
            Assert.Null(persisted.ResponseBody);
            Assert.True(persisted.ExpiresAt > fixture.Now.AddDays(6));
        }
        finally
        {
            await DeleteIdempotencyRecordsAndTenantsAsync(tenant.Id);
        }
    }

    [PostgreSqlFact]
    public async Task AnswerAsync_ReconcilesMatchingRemoteMerchantMessageWithoutSendingAnotherAnswer()
    {
        var tenant = NewTenant("question-answer-reconciled");
        var connection = NewQuestionConnection(tenant);
        var question = NewUncertainQuestion(tenant.Id, connection.Id, fixture.Now);
        var remote = NewRemoteQuestion("WAITING_FOR_ANSWER", [new("Merchant", "Önceki cevap", fixture.Now)], fixture.Now);
        var port = new FixedQuestionPort(remote);

        await using var db = fixture.CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        db.Tenants.Add(tenant);
        db.PlatformConnections.Add(connection);
        db.MarketplaceQuestions.Add(question);
        await db.SaveChangesAsync();

        var service = new MarketplaceQuestionService(db, port, fixture.TimeProvider, NullLogger<MarketplaceQuestionService>.Instance);
        var result = await service.AnswerAsync(tenant.Id, Guid.CreateVersion7(), question.Id, question.Version, "yeni cevap metni", Guid.NewGuid().ToString("N"), "question-answer-reconciled", CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("QUESTION_ANSWER_RECONCILED", result.Error?.Code);
        Assert.Equal(0, port.AnswerCalls);
        Assert.Equal("SUBMITTED", question.AnswerSubmissionStatus);
        Assert.Equal("ANSWER_SUBMITTED", question.Status);
        Assert.Null(question.PendingAnswerText);
    }

    [PostgreSqlFact]
    public async Task AnswerAsync_LeavesUncertainSubmissionBlockedWhenRemoteHasNoMatchingMerchantMessage()
    {
        var tenant = NewTenant("question-answer-unresolved");
        var connection = NewQuestionConnection(tenant);
        var question = NewUncertainQuestion(tenant.Id, connection.Id, fixture.Now);
        var remote = NewRemoteQuestion("WAITING_FOR_ANSWER", [new("Customer", "Önceki cevap", fixture.Now)], fixture.Now);
        var port = new FixedQuestionPort(remote);

        await using var db = fixture.CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        db.Tenants.Add(tenant);
        db.PlatformConnections.Add(connection);
        db.MarketplaceQuestions.Add(question);
        await db.SaveChangesAsync();

        var service = new MarketplaceQuestionService(db, port, fixture.TimeProvider, NullLogger<MarketplaceQuestionService>.Instance);
        var result = await service.AnswerAsync(tenant.Id, Guid.CreateVersion7(), question.Id, question.Version, "başka cevap", Guid.NewGuid().ToString("N"), "question-answer-unresolved", CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("QUESTION_ANSWER_RESULT_UNKNOWN", result.Error?.Code);
        Assert.Equal(0, port.AnswerCalls);
        Assert.Equal("UNKNOWN", question.AnswerSubmissionStatus);
        Assert.Equal("ANSWER_SUBMITTED", question.Status);
        Assert.Equal("Önceki cevap", question.PendingAnswerText);
    }

    [PostgreSqlFact]
    public async Task SyncConnectionAsync_ReconcilesOldUncertainSubmissionByQuestionDetailRead()
    {
        var tenant = NewTenant("question-answer-sync-reconcile");
        var connection = NewQuestionConnection(tenant);
        var question = NewUncertainQuestion(tenant.Id, connection.Id, fixture.Now);
        var remote = NewRemoteQuestion("WAITING_FOR_ANSWER", [new("Merchant", "Önceki cevap", fixture.Now)], fixture.Now);
        var port = new FixedQuestionPort(remote);

        await using var db = fixture.CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        db.Tenants.Add(tenant);
        db.PlatformConnections.Add(connection);
        db.MarketplaceQuestions.Add(question);
        db.MarketplaceQuestionSyncStates.Add(new MarketplaceQuestionSyncState
        {
            TenantId = tenant.Id,
            ConnectionId = connection.Id,
            HistoryImported = true,
            HistoryStartedAt = fixture.Now.AddYears(-1),
            ProgressStatus = "IDLE",
            LastSuccessAt = fixture.Now.AddMinutes(-1),
            Version = 1
        });
        await db.SaveChangesAsync();

        var service = new MarketplaceQuestionService(db, port, fixture.TimeProvider, NullLogger<MarketplaceQuestionService>.Instance);
        var sync = await service.SyncConnectionAsync(tenant.Id, connection.Id, null, CancellationToken.None);

        Assert.Null(sync.Error);
        Assert.Equal(1, port.DetailCalls);
        Assert.Equal(0, port.AnswerCalls);
        Assert.Equal("SUBMITTED", question.AnswerSubmissionStatus);
        Assert.Equal("ANSWER_SUBMITTED", question.Status);
        Assert.Null(question.PendingAnswerText);
    }

    [PostgreSqlFact]
    public async Task InvoiceReconciliation_AdvancesPastBusyReturnOrderAndReadsNextPackage()
    {
        var tenant = NewTenant("invoice-return-lock-fairness");
        var connection = NewQuestionConnection(tenant);
        connection.PlatformCode = "TRENDYOL";
        var partialOrderId = Guid.Parse("0199a5a1-1c00-7000-8000-000000000001");
        var regularOrderId = Guid.Parse("0199a5a1-1c00-7000-8000-000000000002");
        var partialPackageId = Guid.CreateVersion7();
        var regularPackageId = Guid.CreateVersion7();
        var partialOrder = NewInvoiceTestOrder(tenant.Id, connection.Id, partialOrderId, "partial-order", isReturnClaim: true, fixture.Now);
        var regularOrder = NewInvoiceTestOrder(tenant.Id, connection.Id, regularOrderId, "regular-order", isReturnClaim: false, fixture.Now);
        var partialPackage = NewInvoiceTestPackage(tenant.Id, connection.Id, partialOrderId, partialPackageId, "partial-package", fixture.Now);
        var regularPackage = NewInvoiceTestPackage(tenant.Id, connection.Id, regularOrderId, regularPackageId, "regular-package", fixture.Now);
        var orderPort = new ReadOnlyOrderTestPort(new RemotePackage(
            "regular-package", null, "Delivered", fixture.Now, null, null, [],
            GrossAmount: 100m, NetAmount: 100m,
            Invoice: new RemotePackageInvoiceObservation("NotInvoiced", null, null, fixture.Now)));

        await using (var seedDb = fixture.CreateContext())
        {
            seedDb.Tenants.Add(tenant);
            seedDb.PlatformConnections.Add(connection);
            seedDb.Orders.AddRange(partialOrder, regularOrder);
            seedDb.ShipmentPackages.AddRange(partialPackage, regularPackage);
            await seedDb.SaveChangesAsync();
        }

        try
        {
            await using var blockingDb = fixture.CreateContext();
            await using var orderLock = await MarketplaceSyncExecutionLock.TryAcquireAsync(
                blockingDb, connection.Id, MarketplaceJobTypes.OrderSync, CancellationToken.None);
            Assert.NotNull(orderLock);

            await using var processorDb = fixture.CreateContext();
            var processor = new MarketplaceJobProcessor(
                processorDb,
                null!, null!, null!, null!, orderPort, null!, null!, null!, null!, null!,
                new ConfigurationBuilder().Build(), fixture.TimeProvider);
            var result = await processor.ProcessAsync(
                tenant.Id,
                connection.Id,
                MarketplaceJobTypes.OrderInvoiceReconciliation,
                "{}",
                "invoice-return-lock-fairness",
                CancellationToken.None);

            Assert.True(result.Succeeded);
            Assert.Equal(0, orderPort.OrderReadCalls);
            Assert.Equal(["regular-package"], orderPort.PackageReadCalls);
            Assert.Contains(processorDb.OperationalIssues.Local, issue => issue.Code == "RETURN_ORDER_HYDRATION_BUSY");

            // Both sweep orders were taken, so the sweep is complete and the next starts six hours later.
            var persistedCursor = await processorDb.SyncCursors.SingleAsync(cursor => cursor.TenantId == tenant.Id
                && cursor.ConnectionId == connection.Id
                && cursor.ResourceType == TrendyolInvoiceReconciliationPolicy.SweepCursorResource);
            Assert.Null(persistedCursor.OpaqueCursor);
            Assert.NotNull(persistedCursor.LastSuccessAt);

            var persistedPackages = await processorDb.ShipmentPackages.AsNoTracking()
                .Where(package => package.TenantId == tenant.Id && package.ConnectionId == connection.Id)
                .ToDictionaryAsync(package => package.ExternalPackageId);
            Assert.Equal(MarketplaceInvoiceStatus.Unknown, persistedPackages["partial-package"].MarketplaceInvoiceStatus);
            Assert.Equal(MarketplaceInvoiceStatus.NotInvoiced, persistedPackages["regular-package"].MarketplaceInvoiceStatus);
        }
        finally
        {
            await DeleteInvoiceTestTenantAsync(tenant.Id);
        }
    }

    [PostgreSqlFact]
    public async Task InvoiceReconciliation_StopsWithinTimeBudgetAndCheckpointsFinishedOrders()
    {
        var tenant = NewTenant("invoice-time-budget");
        var connection = NewQuestionConnection(tenant);
        connection.PlatformCode = "TRENDYOL";
        var orderIds = new[]
        {
            Guid.Parse("0199a5a1-1c00-7000-8000-000000000011"),
            Guid.Parse("0199a5a1-1c00-7000-8000-000000000012"),
            Guid.Parse("0199a5a1-1c00-7000-8000-000000000013")
        };
        var orders = orderIds.Select((id, index) => NewInvoiceTestOrder(tenant.Id, connection.Id, id, $"budget-order-{index}", isReturnClaim: false, fixture.Now)).ToArray();
        var packages = orderIds.Select((id, index) => NewInvoiceTestPackage(tenant.Id, connection.Id, id, Guid.CreateVersion7(), $"budget-package-{index}", fixture.Now)).ToArray();
        var clock = new SteppingTimeProvider(fixture.TimeProvider.GetUtcNow());
        // Every package read takes 400 seconds; with a 600 second budget only two orders fit.
        var orderPort = new ReadOnlyOrderTestPort(
            new RemotePackage("budget-package", null, "Delivered", fixture.Now, null, null, [],
                GrossAmount: 100m, NetAmount: 100m,
                Invoice: new RemotePackageInvoiceObservation("NotInvoiced", null, null, fixture.Now)),
            onPackageRead: () => clock.Advance(TimeSpan.FromSeconds(400)));

        await using (var seedDb = fixture.CreateContext())
        {
            seedDb.Tenants.Add(tenant);
            seedDb.PlatformConnections.Add(connection);
            seedDb.Orders.AddRange(orders);
            seedDb.ShipmentPackages.AddRange(packages);
            await seedDb.SaveChangesAsync();
        }

        try
        {
            await using var processorDb = fixture.CreateContext();
            var processor = new MarketplaceJobProcessor(
                processorDb,
                null!, null!, null!, null!, orderPort, null!, null!, null!, null!, null!,
                new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["MarketplaceSync:OrderInvoiceReconciliation:TrendyolSliceSeconds"] = "600"
                }).Build(),
                clock);
            var result = await processor.ProcessAsync(
                tenant.Id,
                connection.Id,
                MarketplaceJobTypes.OrderInvoiceReconciliation,
                "{}",
                "invoice-time-budget",
                CancellationToken.None);

            // The slice ran out with an order left, so the job continues shortly from its checkpoint.
            Assert.True(result.Continues);
            Assert.Equal(["budget-package-0", "budget-package-1"], orderPort.PackageReadCalls);

            await using var verifyDb = fixture.CreateContext();
            var persistedCursor = await verifyDb.SyncCursors.AsNoTracking().SingleAsync(cursor => cursor.TenantId == tenant.Id
                && cursor.ConnectionId == connection.Id
                && cursor.ResourceType == TrendyolInvoiceReconciliationPolicy.SweepCursorResource);
            Assert.Equal(OrderInvoiceReconciliationBatchPolicy.WriteCursor(orderIds[1]), persistedCursor.OpaqueCursor);
            Assert.Null(persistedCursor.LastSuccessAt);
            var persistedPackages = await verifyDb.ShipmentPackages.AsNoTracking()
                .Where(package => package.TenantId == tenant.Id && package.ConnectionId == connection.Id)
                .ToDictionaryAsync(package => package.ExternalPackageId);
            Assert.Equal(MarketplaceInvoiceStatus.NotInvoiced, persistedPackages["budget-package-0"].MarketplaceInvoiceStatus);
            Assert.Equal(MarketplaceInvoiceStatus.NotInvoiced, persistedPackages["budget-package-1"].MarketplaceInvoiceStatus);
            Assert.Equal(MarketplaceInvoiceStatus.Unknown, persistedPackages["budget-package-2"].MarketplaceInvoiceStatus);
        }
        finally
        {
            await DeleteInvoiceTestTenantAsync(tenant.Id);
        }
    }

    [PostgreSqlFact]
    public async Task TrendyolInvoiceReconciliation_SweepsOnlyRecentUninvoicedShippedPackages()
    {
        var tenant = NewTenant("trendyol-invoice-sweep-scope");
        var connection = NewQuestionConnection(tenant);
        connection.PlatformCode = "TRENDYOL";
        var ids = Enumerable.Range(1, 5).Select(index => Guid.Parse($"0199a5a1-1c00-7000-8000-00000000002{index}")).ToArray();
        var orders = ids.Select((id, index) => NewInvoiceTestOrder(tenant.Id, connection.Id, id, $"sweep-order-{index}", isReturnClaim: false, fixture.Now)).ToArray();
        var packages = ids.Select((id, index) => NewInvoiceTestPackage(tenant.Id, connection.Id, id, Guid.CreateVersion7(), $"sweep-package-{index}", fixture.Now)).ToArray();
        // 0: delivered, uninvoiced, recent → read. 1: already invoiced → skipped.
        // 2: new (not picked yet) → skipped. 3: ordered 120 days ago → skipped. 4: shipped, uninvoiced → read.
        packages[0].MarketplaceInvoiceStatus = MarketplaceInvoiceStatus.NotInvoiced;
        packages[1].MarketplaceInvoiceStatus = MarketplaceInvoiceStatus.Invoiced;
        packages[2].Status = ShipmentPackageStatus.New;
        packages[2].RawStatus = "Created";
        orders[3].OrderedAt = fixture.Now.AddDays(-120);
        packages[4].Status = ShipmentPackageStatus.Shipped;
        packages[4].RawStatus = "Shipped";
        var orderPort = new ReadOnlyOrderTestPort(new RemotePackage(
            "sweep-package", null, "Delivered", fixture.Now, null, null, [],
            GrossAmount: 100m, NetAmount: 100m,
            Invoice: new RemotePackageInvoiceObservation("NotInvoiced", null, null, fixture.Now)));

        await using (var seedDb = fixture.CreateContext())
        {
            seedDb.Tenants.Add(tenant);
            seedDb.PlatformConnections.Add(connection);
            seedDb.Orders.AddRange(orders);
            seedDb.ShipmentPackages.AddRange(packages);
            await seedDb.SaveChangesAsync();
        }

        try
        {
            async Task RunAsync()
            {
                await using var processorDb = fixture.CreateContext();
                var processor = new MarketplaceJobProcessor(
                    processorDb,
                    null!, null!, null!, null!, orderPort, null!, null!, null!, null!, null!,
                    new ConfigurationBuilder().Build(), fixture.TimeProvider);
                var result = await processor.ProcessAsync(
                    tenant.Id,
                    connection.Id,
                    MarketplaceJobTypes.OrderInvoiceReconciliation,
                    "{}",
                    "trendyol-invoice-sweep-scope",
                    CancellationToken.None);
                Assert.True(result.Succeeded, result.ErrorSummary);
            }

            await RunAsync();
            Assert.Equal(["sweep-package-0", "sweep-package-4"], orderPort.PackageReadCalls);

            // The sweep just finished: a run within six hours reads nothing.
            await RunAsync();
            Assert.Equal(2, orderPort.PackageReadCalls.Count);
        }
        finally
        {
            await DeleteInvoiceTestTenantAsync(tenant.Id);
        }
    }

    [PostgreSqlFact]
    public async Task TrendyolOrderSync_ReadsActiveStorefrontsAndProbesOneInactiveStorefrontPerRun()
    {
        var tenant = NewTenant("trendyol-active-storefronts");
        var connection = NewQuestionConnection(tenant);
        var orderPort = new StorefrontRecordingOrderPort();
        var now = fixture.Now;

        await using (var seedDb = fixture.CreateContext())
        {
            seedDb.Tenants.Add(tenant);
            seedDb.PlatformConnections.Add(connection);
            seedDb.SyncCursors.Add(new SyncCursor
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenant.Id,
                ConnectionId = connection.Id,
                ResourceType = "ORDERS_HOT",
                LastModifiedWatermark = now.AddMinutes(-1),
                LastSuccessAt = now.AddMinutes(-1),
                Version = 1
            });
            // DE sold two days ago; every other international storefront has never been probed.
            seedDb.SyncCursors.Add(new SyncCursor
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenant.Id,
                ConnectionId = connection.Id,
                ResourceType = TrendyolStorefrontPolicy.CursorResource,
                OpaqueCursor = JsonSerializer.Serialize(new Dictionary<string, TrendyolStorefrontActivity>
                {
                    ["DE"] = new(now.AddDays(-2), now.AddHours(-1))
                }),
                Version = 1
            });
            await seedDb.SaveChangesAsync();
        }

        try
        {
            async Task RunAsync()
            {
                await using var processorDb = fixture.CreateContext();
                var processor = new MarketplaceJobProcessor(
                    processorDb,
                    null!, null!, null!, null!, orderPort, null!, null!, null!, null!, null!,
                    new ConfigurationBuilder().Build(), fixture.TimeProvider);
                var result = await processor.ProcessAsync(
                    tenant.Id,
                    connection.Id,
                    MarketplaceJobTypes.OrderSync,
                    "{}",
                    "trendyol-active-storefronts",
                    CancellationToken.None);
                Assert.True(result.Succeeded, result.ErrorSummary);
            }

            await RunAsync();
            Assert.Equal(["TR", "DE", "AE"], orderPort.PolledStorefronts);

            orderPort.PolledStorefronts.Clear();
            await RunAsync();
            Assert.Equal(["TR", "DE", "SA"], orderPort.PolledStorefronts);
        }
        finally
        {
            await DeleteInvoiceTestTenantAsync(tenant.Id);
        }
    }

    [PostgreSqlFact]
    public async Task TrendyolOpenPackageScan_ReadsRecentPackagesInOneBatchAndOlderOnesOnceADay()
    {
        var tenant = NewTenant("trendyol-open-package-scan");
        var connection = NewQuestionConnection(tenant);
        var orderPort = new StorefrontRecordingOrderPort();
        var now = fixture.Now;
        var ids = Enumerable.Range(1, 4).Select(index => Guid.Parse($"0199a5a1-1c00-7000-8000-00000000003{index}")).ToArray();
        var orders = ids.Select((id, index) => NewInvoiceTestOrder(tenant.Id, connection.Id, id, $"open-order-{index}", isReturnClaim: false, now)).ToArray();
        var packages = ids.Select((id, index) => NewInvoiceTestPackage(tenant.Id, connection.Id, id, Guid.CreateVersion7(), $"open-package-{index}", now)).ToArray();
        // 0 and 1: shipped in Türkiye this week. 2: shipped, ordered 40 days ago. 3: delivered (not open).
        foreach (var index in new[] { 0, 1, 2 }) { packages[index].Status = ShipmentPackageStatus.Shipped; packages[index].RawStatus = "Shipped"; orders[index].StoreFrontCode = "TR"; }
        orders[2].OrderedAt = now.AddDays(-40);
        orders[2].LastRemoteModifiedAt = now.AddDays(-35);

        await using (var seedDb = fixture.CreateContext())
        {
            seedDb.Tenants.Add(tenant);
            seedDb.PlatformConnections.Add(connection);
            seedDb.Orders.AddRange(orders);
            seedDb.ShipmentPackages.AddRange(packages);
            await seedDb.SaveChangesAsync();
        }

        try
        {
            async Task RunAsync()
            {
                await using var processorDb = fixture.CreateContext();
                var processor = new MarketplaceJobProcessor(
                    processorDb,
                    null!, null!, null!, null!, orderPort, null!, null!, null!, null!, null!,
                    new ConfigurationBuilder().Build(), fixture.TimeProvider);
                var result = await processor.ProcessAsync(tenant.Id, connection.Id, MarketplaceJobTypes.OrderStatusSync, "{}", "trendyol-open-package-scan", CancellationToken.None);
                Assert.True(result.Succeeded, result.ErrorSummary);
            }

            await RunAsync();
            var batch = Assert.Single(orderPort.PackageBatches);
            Assert.Equal(["open-package-0", "open-package-1"], batch.PackageIds.Order());
            Assert.Equal("TR", batch.Hint?.StoreFrontCode);
            var olderRead = Assert.Single(orderPort.OrderReads);
            Assert.Equal("open-order-2", olderRead.OrderId);
            // PostgreSQL keeps microseconds, so compare at that precision.
            Assert.True(Math.Abs((now.AddDays(-35) - olderRead.Hint!.LastModifiedAt!.Value).TotalMilliseconds) < 1);

            // Within a day the older order is not read again; the recent batch is.
            await RunAsync();
            Assert.Equal(2, orderPort.PackageBatches.Count);
            Assert.Single(orderPort.OrderReads);
        }
        finally
        {
            await DeleteInvoiceTestTenantAsync(tenant.Id);
        }
    }

    [PostgreSqlFact]
    public async Task TrendyolOrderReconciliation_ReadsStreamWindowsInSlicesAndResumes()
    {
        var tenant = NewTenant("trendyol-reconcile-windows");
        var connection = NewQuestionConnection(tenant);
        var orderPort = new StorefrontRecordingOrderPort();
        var clock = new SteppingTimeProvider(fixture.Now);
        // Every stream page takes 20 seconds, so a 30-second slice reads two pages.
        orderPort.OnPoll = () => clock.Advance(TimeSpan.FromSeconds(20));

        await using (var seedDb = fixture.CreateContext())
        {
            seedDb.Tenants.Add(tenant);
            seedDb.PlatformConnections.Add(connection);
            await seedDb.SaveChangesAsync();
        }

        try
        {
            async Task<JobExecutionResult> RunAsync()
            {
                await using var processorDb = fixture.CreateContext();
                var processor = new MarketplaceJobProcessor(
                    processorDb,
                    null!, null!, null!, null!, orderPort, null!, null!, null!, null!, null!,
                    new ConfigurationBuilder().Build(), clock);
                return await processor.ProcessAsync(tenant.Id, connection.Id, MarketplaceJobTypes.OrderReconciliation, """{"lookbackDays":90}""", "trendyol-reconcile-windows", CancellationToken.None);
            }

            // 90 days = seven 14-day windows of the Türkiye storefront (the only active one).
            var first = await RunAsync();
            Assert.True(first.Continues);
            Assert.Equal(2, orderPort.PolledWindows.Count);
            var second = await RunAsync();
            Assert.True(second.Continues);
            Assert.Equal(4, orderPort.PolledWindows.Count);
            await RunAsync();
            var last = await RunAsync();
            Assert.True(last.Succeeded, last.ErrorSummary);
            Assert.Equal(7, orderPort.PolledWindows.Count);
            Assert.All(orderPort.PolledStorefronts, storefront => Assert.Equal("TR", storefront));
            Assert.Equal(fixture.Now.AddDays(-90), orderPort.PolledWindows[^1].ModifiedAfter);
            Assert.Equal(orderPort.PolledWindows.Count, orderPort.PolledWindows.Select(window => window.ModifiedBefore).Distinct().Count());
        }
        finally
        {
            await DeleteInvoiceTestTenantAsync(tenant.Id);
        }
    }

    [PostgreSqlFact]
    public async Task TrendyolInvoiceSweep_ReadsRecentPackagesInBatchesAndOnlyTheRestOneByOne()
    {
        var tenant = NewTenant("trendyol-invoice-sweep-batch");
        var connection = NewQuestionConnection(tenant);
        connection.PlatformCode = "TRENDYOL";
        var ids = Enumerable.Range(1, 2).Select(index => Guid.Parse($"0199a5a1-1c00-7000-8000-00000000004{index}")).ToArray();
        var orders = ids.Select((id, index) => NewInvoiceTestOrder(tenant.Id, connection.Id, id, $"batch-order-{index}", isReturnClaim: false, fixture.Now)).ToArray();
        var packages = ids.Select((id, index) => NewInvoiceTestPackage(tenant.Id, connection.Id, id, Guid.CreateVersion7(), $"batch-package-{index}", fixture.Now)).ToArray();
        var orderPort = new ReadOnlyOrderTestPort(new RemotePackage(
            "batch-package", null, "Delivered", fixture.Now, null, null, [],
            GrossAmount: 100m, NetAmount: 100m,
            Invoice: new RemotePackageInvoiceObservation("Invoiced", "INV-1", "https://example.test/invoice.pdf", fixture.Now)))
        {
            // Trendyol returns only the first package for the batch; the second is read on its own.
            BatchReturns = new HashSet<string> { "batch-package-0" }
        };

        await using (var seedDb = fixture.CreateContext())
        {
            seedDb.Tenants.Add(tenant);
            seedDb.PlatformConnections.Add(connection);
            seedDb.Orders.AddRange(orders);
            seedDb.ShipmentPackages.AddRange(packages);
            await seedDb.SaveChangesAsync();
        }

        try
        {
            await using (var processorDb = fixture.CreateContext())
            {
                var processor = new MarketplaceJobProcessor(
                    processorDb,
                    null!, null!, null!, null!, orderPort, null!, null!, null!, null!, null!,
                    new ConfigurationBuilder().Build(), fixture.TimeProvider);
                var result = await processor.ProcessAsync(tenant.Id, connection.Id, MarketplaceJobTypes.OrderInvoiceReconciliation, "{}", "trendyol-invoice-sweep-batch", CancellationToken.None);
                Assert.True(result.Succeeded, result.ErrorSummary);
            }

            var batch = Assert.Single(orderPort.BatchReadCalls);
            Assert.Equal(["batch-package-0", "batch-package-1"], batch.Order());
            Assert.Equal(["batch-package-1"], orderPort.PackageReadCalls);
            await using var verifyDb = fixture.CreateContext();
            var invoiced = await verifyDb.ShipmentPackages.AsNoTracking()
                .Where(package => package.TenantId == tenant.Id)
                .ToDictionaryAsync(package => package.ExternalPackageId, package => package.MarketplaceInvoiceStatus);
            Assert.Equal(MarketplaceInvoiceStatus.Invoiced, invoiced["batch-package-0"]);
            Assert.Equal(MarketplaceInvoiceStatus.Invoiced, invoiced["batch-package-1"]);
            // Both invoices were first seen by the reconciliation, not the order stream.
            var discovery = await verifyDb.SyncCursors.AsNoTracking().SingleAsync(cursor => cursor.TenantId == tenant.Id && cursor.ResourceType == "TRENDYOL_INVOICE_DISCOVERY");
            using var counts = JsonDocument.Parse(discovery.OpaqueCursor!);
            Assert.Equal(2, counts.RootElement.GetProperty("Reconciliation").GetInt32());
            Assert.Equal(0, counts.RootElement.GetProperty("Stream").GetInt32());
        }
        finally
        {
            await DeleteInvoiceTestTenantAsync(tenant.Id);
        }
    }

    [PostgreSqlFact]
    public async Task InstantNotifications_RegisterABasicAuthWebhookAndRemoveIt()
    {
        var tenant = NewTenant("trendyol-instant-notifications");
        var connection = NewQuestionConnection(tenant);
        var port = new RecordingWebhookPort();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Marketplace:PublicBaseUrl"] = "https://panel.ravencia.com" }).Build();
        var dataProtection = new EphemeralDataProtectionProvider();
        var hasher = new TokenHasher(new byte[32]);

        await using (var seedDb = fixture.CreateContext())
        {
            seedDb.Tenants.Add(tenant);
            seedDb.PlatformConnections.Add(connection);
            await seedDb.SaveChangesAsync();
        }

        try
        {
            InstantNotificationService Service(AppDbContext db) => new(db, port, hasher, dataProtection, configuration, fixture.TimeProvider, NullLogger<InstantNotificationService>.Instance);

            await using (var db = fixture.CreateContext())
            {
                var enabled = await Service(db).EnableAsync(tenant.Id, connection.Id, "enable", CancellationToken.None);
                Assert.Null(enabled.Error);
                Assert.Equal("ACTIVE", enabled.Value!.State);
                Assert.DoesNotContain(port.CreatedUrl!.Split('/').Last(), enabled.Value.Endpoint);
            }
            Assert.StartsWith($"https://panel.ravencia.com/api/v1/hooks/{connection.PublicId:D}/", port.CreatedUrl);
            Assert.StartsWith(InstantNotificationService.ManagedUserPrefix, port.CreatedUsername);

            // The address Trendyol was given is accepted by the receiver with the same Basic Auth.
            await using (var db = fixture.CreateContext())
            {
                var receiver = new MarketplaceWebhookService(db, hasher, new TrendyolWebhookVerifier(db, dataProtection), fixture.TimeProvider);
                var routeToken = port.CreatedUrl!.Split('/').Last();
                var basic = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{port.CreatedUsername}:{port.CreatedPassword}"));
                var received = await receiver.ReceiveAsync(connection.PublicId, routeToken, Encoding.UTF8.GetBytes("""{"content":[{"orderNumber":"1","shipmentPackageId":1}]}"""), new Dictionary<string, string> { ["Authorization"] = basic }, "webhook-test", CancellationToken.None);
                Assert.Null(received.Error);
            }

            await using (var db = fixture.CreateContext())
            {
                var disabled = await Service(db).DisableAsync(tenant.Id, connection.Id, "disable", CancellationToken.None);
                Assert.Equal("OFF", disabled.Value!.State);
            }
            Assert.Equal("remote-webhook-1", port.DeletedId);
        }
        finally
        {
            await using var cleanupDb = fixture.CreateContext();
            await cleanupDb.InboxMessages.Where(row => row.TenantId == tenant.Id).ExecuteDeleteAsync();
            await cleanupDb.IntegrationJobs.Where(row => row.TenantId == tenant.Id).ExecuteDeleteAsync();
            await cleanupDb.WebhookSubscriptions.Where(row => row.TenantId == tenant.Id).ExecuteDeleteAsync();
            await DeleteInvoiceTestTenantAsync(tenant.Id);
        }
    }

    [PostgreSqlFact]
    public async Task InstantNotifications_PrepareHepsiburadaWithoutCallingHepsiburada()
    {
        var tenant = NewTenant("hb-notifications");
        var connection = NewQuestionConnection(tenant);
        connection.PlatformCode = "HEPSIBURADA";
        var port = new RecordingWebhookPort();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Marketplace:PublicBaseUrl"] = "https://panel.ravencia.com" }).Build();
        var dataProtection = new EphemeralDataProtectionProvider();
        var hasher = new TokenHasher(new byte[32]);

        await using (var seedDb = fixture.CreateContext())
        {
            seedDb.Tenants.Add(tenant);
            seedDb.PlatformConnections.Add(connection);
            await seedDb.SaveChangesAsync();
        }

        try
        {
            InstantNotificationService Service(AppDbContext db) => new(db, port, hasher, dataProtection, configuration, fixture.TimeProvider, NullLogger<InstantNotificationService>.Instance);

            WebhookSetupInstructions setup;
            await using (var db = fixture.CreateContext())
            {
                var prepared = await Service(db).EnableAsync(tenant.Id, connection.Id, "prepare", CancellationToken.None);
                Assert.Null(prepared.Error);
                Assert.Equal("WAITING", prepared.Value!.State);
                setup = prepared.Value.Setup!;
            }
            Assert.Null(port.CreatedUrl);
            Assert.StartsWith($"https://panel.ravencia.com/api/v1/hooks/{connection.PublicId:D}/", setup.BaseUrl);
            Assert.Contains("PUT /lineitems/{lineitemid}/cancel", setup.Paths);

            // Hepsiburada calls a path below the base address with the prepared Basic Auth.
            await using (var db = fixture.CreateContext())
            {
                var receiver = new MarketplaceWebhookService(db, hasher, new MarketplaceHub.Infrastructure.Adapters.Hepsiburada.HepsiburadaWebhookVerifier(db, dataProtection), fixture.TimeProvider);
                var basic = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{setup.Username}:{setup.Password}"));
                var headers = new Dictionary<string, string> { ["Authorization"] = basic, [WebhookEventPath.Header] = "lineitems/b17553de/cancel" };
                var received = await receiver.ReceiveAsync(connection.PublicId, setup.BaseUrl.Split('/').Last(), Encoding.UTF8.GetBytes("""{"orderNumber":"0123456789","id":"b17553de","cancelledBy":"Customer"}"""), headers, "webhook-test", CancellationToken.None);
                Assert.Null(received.Error);
                Assert.True(await db.IntegrationJobs.AnyAsync(job => job.TenantId == tenant.Id && job.PayloadJson.Contains("ORDER_EVENT")));
            }

            await using (var db = fixture.CreateContext())
            {
                Assert.Equal("ACTIVE", (await Service(db).StatusAsync(tenant.Id, connection.Id, "status", CancellationToken.None)).Value!.State);
                Assert.Equal("OFF", (await Service(db).DisableAsync(tenant.Id, connection.Id, "disable", CancellationToken.None)).Value!.State);
            }
            Assert.Null(port.DeletedId);
        }
        finally
        {
            await using var cleanupDb = fixture.CreateContext();
            await cleanupDb.InboxMessages.Where(row => row.TenantId == tenant.Id).ExecuteDeleteAsync();
            await cleanupDb.IntegrationJobs.Where(row => row.TenantId == tenant.Id).ExecuteDeleteAsync();
            await cleanupDb.WebhookSubscriptions.Where(row => row.TenantId == tenant.Id).ExecuteDeleteAsync();
            await DeleteInvoiceTestTenantAsync(tenant.Id);
        }
    }

    [PostgreSqlFact]
    public async Task InstantNotifications_ShopifyVerifiesWithAGivenClientSecret()
    {
        var tenant = NewTenant("shopify-notifications");
        var connection = NewQuestionConnection(tenant);
        connection.PlatformCode = "SHOPIFY";
        var port = new RecordingWebhookPort();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Marketplace:PublicBaseUrl"] = "https://panel.ravencia.com" }).Build();
        var dataProtection = new EphemeralDataProtectionProvider();
        var hasher = new TokenHasher(new byte[32]);

        await using (var seedDb = fixture.CreateContext())
        {
            seedDb.Tenants.Add(tenant);
            seedDb.PlatformConnections.Add(connection);
            await seedDb.SaveChangesAsync();
        }

        try
        {
            InstantNotificationService Service(AppDbContext db) => new(db, port, hasher, dataProtection, configuration, fixture.TimeProvider, NullLogger<InstantNotificationService>.Instance);

            await using (var db = fixture.CreateContext())
            {
                var enabled = await Service(db).EnableAsync(tenant.Id, connection.Id, "enable", CancellationToken.None, "shpss_test_secret");
                Assert.Null(enabled.Error);
                Assert.Equal("ACTIVE", enabled.Value!.State);
            }

            // A refund delivery signed with the client secret is accepted and names its order by order_id.
            await using (var db = fixture.CreateContext())
            {
                var receiver = new MarketplaceWebhookService(db, hasher, new MarketplaceHub.Infrastructure.Adapters.Shopify.ShopifyWebhookVerifier(db, dataProtection), fixture.TimeProvider);
                var body = Encoding.UTF8.GetBytes("""{"id":987,"order_id":123}""");
                var signature = Convert.ToBase64String(System.Security.Cryptography.HMACSHA256.HashData(Encoding.UTF8.GetBytes("shpss_test_secret"), body));
                var headers = new Dictionary<string, string> { ["X-Shopify-Hmac-Sha256"] = signature, ["X-Shopify-Topic"] = "refunds/create", ["X-Shopify-Webhook-Id"] = "delivery-1" };
                var received = await receiver.ReceiveAsync(connection.PublicId, port.CreatedUrl!.Split('/').Last(), body, headers, "webhook-test", CancellationToken.None);
                Assert.Null(received.Error);
                Assert.True(await db.IntegrationJobs.AnyAsync(job => job.TenantId == tenant.Id && job.PayloadJson.Contains("REFUNDS")));
            }
        }
        finally
        {
            await using var cleanupDb = fixture.CreateContext();
            await cleanupDb.InboxMessages.Where(row => row.TenantId == tenant.Id).ExecuteDeleteAsync();
            await cleanupDb.IntegrationJobs.Where(row => row.TenantId == tenant.Id).ExecuteDeleteAsync();
            await cleanupDb.WebhookSubscriptions.Where(row => row.TenantId == tenant.Id).ExecuteDeleteAsync();
            await DeleteInvoiceTestTenantAsync(tenant.Id);
        }
    }

    [PostgreSqlFact]
    public async Task InstantNotifications_ShopifyWithoutSecretAdmitsDeliveriesByTheirSecretRoute()
    {
        var tenant = NewTenant("shopify-route");
        var connection = NewQuestionConnection(tenant);
        connection.PlatformCode = "SHOPIFY";
        var port = new RecordingWebhookPort();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Marketplace:PublicBaseUrl"] = "https://panel.ravencia.com" }).Build();
        var dataProtection = new EphemeralDataProtectionProvider();
        var hasher = new TokenHasher(new byte[32]);

        await using (var seedDb = fixture.CreateContext())
        {
            seedDb.Tenants.Add(tenant);
            seedDb.PlatformConnections.Add(connection);
            await seedDb.SaveChangesAsync();
        }

        try
        {
            await using (var db = fixture.CreateContext())
            {
                var enabled = await new InstantNotificationService(db, port, hasher, dataProtection, configuration, fixture.TimeProvider, NullLogger<InstantNotificationService>.Instance)
                    .EnableAsync(tenant.Id, connection.Id, "enable", CancellationToken.None);
                Assert.Null(enabled.Error);
                Assert.Equal("ROUTE_TOKEN", (await db.WebhookSubscriptions.SingleAsync(x => x.TenantId == tenant.Id)).AuthenticationType);
            }

            await using (var db = fixture.CreateContext())
            {
                var receiver = new MarketplaceWebhookService(db, hasher, new MarketplaceHub.Infrastructure.Adapters.Shopify.ShopifyWebhookVerifier(db, dataProtection), fixture.TimeProvider);
                var headers = new Dictionary<string, string> { ["X-Shopify-Hmac-Sha256"] = "unverifiable", ["X-Shopify-Topic"] = "orders/updated", ["X-Shopify-Webhook-Id"] = "delivery-2" };
                var received = await receiver.ReceiveAsync(connection.PublicId, port.CreatedUrl!.Split('/').Last(), Encoding.UTF8.GetBytes("""{"id":123}"""), headers, "webhook-test", CancellationToken.None);
                Assert.Null(received.Error);
                var wrongRoute = await receiver.ReceiveAsync(connection.PublicId, "wrong-route", Encoding.UTF8.GetBytes("""{"id":123}"""), headers, "webhook-test", CancellationToken.None);
                Assert.NotNull(wrongRoute.Error);
            }
        }
        finally
        {
            await using var cleanupDb = fixture.CreateContext();
            await cleanupDb.InboxMessages.Where(row => row.TenantId == tenant.Id).ExecuteDeleteAsync();
            await cleanupDb.IntegrationJobs.Where(row => row.TenantId == tenant.Id).ExecuteDeleteAsync();
            await cleanupDb.WebhookSubscriptions.Where(row => row.TenantId == tenant.Id).ExecuteDeleteAsync();
            await DeleteInvoiceTestTenantAsync(tenant.Id);
        }
    }

    private sealed class RecordingWebhookPort : IWebhookRegistrationPort
    {
        public string? CreatedUrl { get; private set; }
        public string? CreatedUsername { get; private set; }
        public string? CreatedPassword { get; private set; }
        public string? DeletedId { get; private set; }

        public Task<AdapterResult<IReadOnlyList<RemoteWebhook>>> ListWebhooksAsync(AdapterContext context, CancellationToken cancellationToken) =>
            Task.FromResult(AdapterResult<IReadOnlyList<RemoteWebhook>>.Success(CreatedUrl is null || DeletedId is not null ? [] : [new RemoteWebhook("remote-webhook-1", CreatedUrl, "ACTIVE", CreatedUsername)]));

        public Task<AdapterResult<string>> CreateWebhookAsync(AdapterContext context, string url, string username, string password, CancellationToken cancellationToken)
        {
            (CreatedUrl, CreatedUsername, CreatedPassword) = (url, username, password);
            return Task.FromResult(AdapterResult<string>.Success("remote-webhook-1"));
        }

        public Task<AdapterResult<bool>> DeleteWebhookAsync(AdapterContext context, string externalId, CancellationToken cancellationToken)
        {
            DeletedId = externalId;
            return Task.FromResult(AdapterResult<bool>.Success(true));
        }

        public Task<AdapterResult<bool>> ActivateWebhookAsync(AdapterContext context, string externalId, CancellationToken cancellationToken) =>
            Task.FromResult(AdapterResult<bool>.Success(true));
    }

    [PostgreSqlFact]
    public async Task ExternalEffectReconcile_ReadsTheMarketplaceAndCompletesOrReleasesThePickingEffect()
    {
        var tenant = NewTenant("external-effect-reconcile");
        var connection = NewQuestionConnection(tenant);
        RemoteOrder RemoteWith(string rawStatus, int minutesAgo)
        {
            var package = new RemotePackage("effect-package", null, rawStatus, fixture.Now.AddMinutes(-minutesAgo), null, null,
                [new RemotePackageAllocation("effect-line", 1m, 0m, 0m, 0m, 0m)], GrossAmount: 100m, NetAmount: 100m);
            return new RemoteOrder("effect-order", "effect-order", fixture.Now.AddDays(-1), fixture.Now.AddMinutes(-minutesAgo), "TRY", 100m, 0m, 100m, "{}", "{}", "{}",
                [new RemoteOrderLine("effect-line", "SKU-1", "SKU-1", "Test ürün", 1m, 100m, 10m, rawStatus)], [package], "{}");
        }
        var port = new SwitchableOrderPort(RemoteWith("Created", 10));

        await using (var seedDb = fixture.CreateContext())
        {
            seedDb.Tenants.Add(tenant);
            seedDb.PlatformConnections.Add(connection);
            await seedDb.SaveChangesAsync();
        }

        try
        {
            async Task<JobExecutionResult> RunAsync(string jobType, string payload)
            {
                await using var processorDb = fixture.CreateContext();
                var processor = new MarketplaceJobProcessor(
                    processorDb,
                    null!, null!, null!, null!, port, null!, null!, null!, null!, null!,
                    new ConfigurationBuilder().Build(), fixture.TimeProvider);
                return await processor.ProcessAsync(tenant.Id, connection.Id, jobType, payload, "external-effect-reconcile", CancellationToken.None);
            }
            async Task<ExternalEffectRecord> EffectAsync(string key)
            {
                await using var db = fixture.CreateContext();
                var packageId = await db.ShipmentPackages.Where(x => x.TenantId == tenant.Id && x.ExternalPackageId == "effect-package").Select(x => x.Id).SingleAsync();
                var effect = new ExternalEffectRecord { Id = Guid.CreateVersion7(), TenantId = tenant.Id, EffectType = "TRENDYOL_INSTANT_PICKING", IdempotencyKey = key, CreatedAt = fixture.Now.AddMinutes(-5), ConnectionId = connection.Id, TargetId = packageId, Action = "PICKING" };
                db.ExternalEffectRecords.Add(effect);
                await db.SaveChangesAsync();
                return effect;
            }

            Assert.True((await RunAsync(MarketplaceJobTypes.OrderSync, """{"externalOrderId":"effect-order","full":false}""")).Succeeded);

            // Trendyol still shows the package as new five minutes later: the effect is released.
            var notApplied = await EffectAsync("not-applied");
            Assert.True((await RunAsync(MarketplaceJobTypes.ExternalEffectReconcile, $$"""{"effectId":"{{notApplied.Id}}"}""")).Succeeded);
            await using (var db = fixture.CreateContext())
                Assert.False(await db.ExternalEffectRecords.AnyAsync(x => x.Id == notApplied.Id));

            // Trendyol shows the package being picked: the effect is completed and the package updated.
            var applied = await EffectAsync("applied");
            port.Order = RemoteWith("Picking", 1);
            Assert.True((await RunAsync(MarketplaceJobTypes.ExternalEffectReconcile, $$"""{"effectId":"{{applied.Id}}"}""")).Succeeded);
            await using (var db = fixture.CreateContext())
            {
                var settled = await db.ExternalEffectRecords.AsNoTracking().SingleAsync(x => x.Id == applied.Id);
                Assert.Equal("APPLIED", settled.Outcome);
                Assert.NotNull(settled.CompletedAt);
                Assert.Equal(ShipmentPackageStatus.Processing, (await db.ShipmentPackages.AsNoTracking().SingleAsync(x => x.TenantId == tenant.Id && x.ExternalPackageId == "effect-package")).Status);
            }
        }
        finally
        {
            await using var cleanupDb = fixture.CreateContext();
            await cleanupDb.ExternalEffectRecords.Where(row => row.TenantId == tenant.Id).ExecuteDeleteAsync();
            await DeleteInvoiceTestTenantAsync(tenant.Id);
        }
    }

    private sealed class SwitchableOrderPort(RemoteOrder order) : IOrderPort
    {
        public RemoteOrder Order { get; set; } = order;

        public Task<AdapterResult<AdapterPageResult<RemoteOrder>>> PollAsync(AdapterContext context, OrderPollWindow window, AdapterPageRequest page, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<AdapterResult<RemoteOrder>> GetAsync(AdapterContext context, string externalOrderId, CancellationToken cancellationToken) =>
            Task.FromResult(AdapterResult<RemoteOrder>.Success(Order));
        public Task<AdapterResult<PackageActionResult>> ExecutePackageActionAsync(AdapterContext context, PackageActionCommand command, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Verification must never send the action again.");
        public Task<AdapterResult<IReadOnlyList<RemoteCargoCompany>>> GetChangeableCargoCompaniesAsync(AdapterContext context, string externalPackageId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<IReadOnlyList<RemotePackageableLine>>> GetPackageableLineItemsAsync(AdapterContext context, string externalLineItemId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<CreateOrderPackageResult>> CreateOrderPackageAsync(AdapterContext context, CreateOrderPackageCommand command, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<bool>> CreateCommonLabelAsync(AdapterContext context, CommonLabelRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<CommonLabelDocument>> GetCommonLabelAsync(AdapterContext context, string cargoTrackingNumber, CancellationToken cancellationToken, string format = "ZPL") => throw new NotSupportedException();
        public Task<AdapterResult<StageTestOrderResult>> CreateStageTestOrderAsync(AdapterContext context, string barcode, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    [PostgreSqlFact]
    public async Task InstantPicking_SendsPazaramaGuidLineIds()
    {
        var tenant = NewTenant("pazarama-instant-picking");
        var connection = NewQuestionConnection(tenant);
        connection.PlatformCode = "PAZARAMA";
        var orderId = Guid.CreateVersion7();
        var order = NewInvoiceTestOrder(tenant.Id, connection.Id, orderId, "pazarama-order", isReturnClaim: false, fixture.Now);
        order.DerivedStatus = "NEW";
        var package = NewInvoiceTestPackage(tenant.Id, connection.Id, orderId, Guid.CreateVersion7(), "pazarama-package", fixture.Now);
        package.Status = ShipmentPackageStatus.New;
        package.RawStatus = "Created";
        var lineId = "3f2b1c4d-5e6f-4a7b-8c9d-0e1f2a3b4c5d";
        var line = new OrderLine { Id = Guid.CreateVersion7(), TenantId = tenant.Id, OrderId = orderId, ExternalLineId = lineId, Sku = "SKU-1", TitleSnapshot = "Test ürün", OrderedQuantity = 1, UnitPrice = 100, VatRate = 20, RawStatus = "Created" };
        var port = new RecordingActionPort();

        await using (var seedDb = fixture.CreateContext())
        {
            seedDb.Tenants.Add(tenant);
            seedDb.PlatformConnections.Add(connection);
            seedDb.ConnectionSyncPolicies.Add(new ConnectionSyncPolicy { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, ResourceType = MarketplaceExternalWritePolicies.Shipment, IntervalSeconds = 0, Enabled = true, Version = 1 });
            seedDb.Orders.Add(order);
            seedDb.OrderLines.Add(line);
            seedDb.ShipmentPackages.Add(package);
            seedDb.PackageLineAllocations.Add(new PackageLineAllocation { Id = Guid.CreateVersion7(), TenantId = tenant.Id, PackageId = package.Id, OrderLineId = line.Id, AllocatedQuantity = 1, SourceEventId = "seed" });
            await seedDb.SaveChangesAsync();
        }

        try
        {
            await using var db = fixture.CreateContext();
            var service = new MarketplaceSalesService(db, null!, new ConfigurationBuilder().Build(), null!, port, null!, null!, fixture.TimeProvider);
            var result = await service.ProcessShipmentInstantAsync(tenant.Id, package.Id, package.Version, "instant-picking:pazarama:v1", "pazarama-instant-picking", CancellationToken.None);

            Assert.Null(result.Error);
            using var sent = JsonDocument.Parse(port.Payload!);
            Assert.Equal(lineId, sent.RootElement.GetProperty("lines")[0].GetProperty("lineId").GetString());
            Assert.True(await db.ExternalEffectRecords.AnyAsync(x => x.TenantId == tenant.Id && x.EffectType == "PAZARAMA_INSTANT_PICKING" && x.CompletedAt != null));
        }
        finally
        {
            await using var cleanupDb = fixture.CreateContext();
            await cleanupDb.ExternalEffectRecords.Where(row => row.TenantId == tenant.Id).ExecuteDeleteAsync();
            await cleanupDb.ConnectionSyncPolicies.Where(row => row.TenantId == tenant.Id).ExecuteDeleteAsync();
            await DeleteInvoiceTestTenantAsync(tenant.Id);
        }
    }

    private sealed class RecordingActionPort : IOrderPort
    {
        public string? Payload { get; private set; }
        public Task<AdapterResult<PackageActionResult>> ExecutePackageActionAsync(AdapterContext context, PackageActionCommand command, CancellationToken cancellationToken)
        {
            Payload = command.PayloadJson;
            return Task.FromResult(AdapterResult<PackageActionResult>.Success(new(command.ExternalPackageId, "Processing", null)));
        }
        public Task<AdapterResult<AdapterPageResult<RemoteOrder>>> PollAsync(AdapterContext context, OrderPollWindow window, AdapterPageRequest page, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<RemoteOrder>> GetAsync(AdapterContext context, string externalOrderId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<IReadOnlyList<RemoteCargoCompany>>> GetChangeableCargoCompaniesAsync(AdapterContext context, string externalPackageId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<IReadOnlyList<RemotePackageableLine>>> GetPackageableLineItemsAsync(AdapterContext context, string externalLineItemId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<CreateOrderPackageResult>> CreateOrderPackageAsync(AdapterContext context, CreateOrderPackageCommand command, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<bool>> CreateCommonLabelAsync(AdapterContext context, CommonLabelRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<CommonLabelDocument>> GetCommonLabelAsync(AdapterContext context, string cargoTrackingNumber, CancellationToken cancellationToken, string format = "ZPL") => throw new NotSupportedException();
        public Task<AdapterResult<StageTestOrderResult>> CreateStageTestOrderAsync(AdapterContext context, string barcode, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    [PostgreSqlFact]
    public async Task TargetedTrendyolOrderSync_UsesOrderNumberBeforeSuppliedPackage()
    {
        var tenant = NewTenant("trendyol-package-first-sync");
        var connection = NewQuestionConnection(tenant);
        connection.ApiVersion = "V2";
        const string externalOrderId = "116483573775";
        const string packageNumber = "419428742";
        var package = new RemotePackage(
            packageNumber,
            null,
            "Shipped",
            fixture.Now.AddDays(-2),
            "TRENDYOL_EXPRESS",
            "7330037500176178",
            [new RemotePackageAllocation("line-1", 1m, 0m, 1m, 0m, 0m)],
            GrossAmount: 454.31m,
            NetAmount: 454.31m);
        var order = new RemoteOrder(
            externalOrderId,
            externalOrderId,
            fixture.Now.AddDays(-2),
            fixture.Now.AddDays(-1),
            "TRY",
            454.31m,
            0m,
            454.31m,
            "{}",
            "{}",
            "{}",
            [new RemoteOrderLine("line-1", "RY-P001", "RY-P001", "Test ürün", 1m, 454.31m, 10m, "Shipped")],
            [package],
            "{}");
        var orderPort = new ReadOnlyOrderTestPort(package, order, externalOrderId, directOrderResult: order);

        await using (var seedDb = fixture.CreateContext())
        {
            seedDb.Tenants.Add(tenant);
            seedDb.PlatformConnections.Add(connection);
            await seedDb.SaveChangesAsync();
        }

        try
        {
            await using (var processorDb = fixture.CreateContext())
            {
                var processor = new MarketplaceJobProcessor(
                    processorDb,
                    null!, null!, null!, null!, orderPort, null!, null!, null!, null!, null!,
                    new ConfigurationBuilder().Build(), fixture.TimeProvider);
                var result = await processor.ProcessAsync(
                    tenant.Id,
                    connection.Id,
                    MarketplaceJobTypes.OrderSync,
                    $$"""{"externalOrderId":"{{externalOrderId}}","full":false,"packageNumber":"{{packageNumber}}"}""",
                    "targeted-trendyol-package-first-sync",
                    CancellationToken.None);

                Assert.True(result.Succeeded, result.ErrorSummary);
                Assert.Equal(1, orderPort.OrderReadCalls);
                Assert.Empty(orderPort.PackageReadCalls);
            }

            await using var verifyDb = fixture.CreateContext();
            var persistedOrder = await verifyDb.Orders.AsNoTracking().SingleAsync(row => row.TenantId == tenant.Id && row.ExternalOrderId == externalOrderId);
            Assert.Equal(externalOrderId, persistedOrder.OrderNumber);
            var persistedPackage = await verifyDb.ShipmentPackages.AsNoTracking().SingleAsync(row => row.TenantId == tenant.Id && row.ExternalPackageId == packageNumber);
            Assert.Equal(persistedOrder.Id, persistedPackage.OrderId);
            Assert.Equal("7330037500176178", persistedPackage.CargoTrackingNumber);
        }
        finally
        {
            await DeleteInvoiceTestTenantAsync(tenant.Id);
        }
    }

    [PostgreSqlFact]
    public async Task TrendyolOrderSync_RepairsLegacyZeroPackageHoldWhenRealPackageExists()
    {
        var tenant = NewTenant("ty-zero-hold");
        var connection = NewQuestionConnection(tenant);
        var orderId = Guid.CreateVersion7();
        var lineId = Guid.CreateVersion7();
        var legacyPackageId = Guid.CreateVersion7();
        var currentPackageId = Guid.CreateVersion7();
        const string externalOrderId = "11697863851";
        const string currentPackageNumber = "4234395651";
        var occurredAt = fixture.Now.AddMinutes(-10);
        var order = NewInvoiceTestOrder(tenant.Id, connection.Id, orderId, externalOrderId, isReturnClaim: false, fixture.Now);
        order.DerivedStatus = "ON_HOLD";
        order.LastRemoteModifiedAt = occurredAt;
        var line = new OrderLine
        {
            Id = lineId,
            TenantId = tenant.Id,
            OrderId = orderId,
            ExternalLineId = "line-1",
            Sku = "SKU-1",
            TitleSnapshot = "Test ürün",
            OrderedQuantity = 1m,
            UnitPrice = 100m,
            VatRate = 10m,
            RawStatus = "Created",
            Version = 1
        };
        var legacyPackage = new ShipmentPackage
        {
            Id = legacyPackageId,
            TenantId = tenant.Id,
            ConnectionId = connection.Id,
            OrderId = orderId,
            ExternalPackageId = "0",
            GrossAmount = 100m,
            NetAmount = 100m,
            Status = ShipmentPackageStatus.OnHold,
            RawStatus = "Awaiting",
            StatusOccurredAt = occurredAt,
            CreatedAt = occurredAt,
            UpdatedAt = occurredAt,
            Version = 1
        };
        var currentPackage = new ShipmentPackage
        {
            Id = currentPackageId,
            TenantId = tenant.Id,
            ConnectionId = connection.Id,
            OrderId = orderId,
            ExternalPackageId = currentPackageNumber,
            GrossAmount = 100m,
            NetAmount = 100m,
            Status = ShipmentPackageStatus.New,
            RawStatus = "Created",
            StatusOccurredAt = occurredAt,
            CreatedAt = occurredAt,
            UpdatedAt = occurredAt,
            Version = 1
        };
        var remotePackage = new RemotePackage(
            currentPackageNumber,
            null,
            "Created",
            occurredAt,
            null,
            null,
            [new RemotePackageAllocation("line-1", 1m, 0m, 0m, 0m, 0m)],
            GrossAmount: 100m,
            NetAmount: 100m);
        var remoteOrder = new RemoteOrder(
            externalOrderId,
            externalOrderId,
            occurredAt.AddMinutes(-1),
            occurredAt,
            "TRY",
            100m,
            0m,
            100m,
            "{}",
            "{}",
            "{}",
            [new RemoteOrderLine("line-1", "SKU-1", null, "Test ürün", 1m, 100m, 10m, "Created")],
            [remotePackage],
            "{}");
        var orderPort = new ReadOnlyOrderTestPort(remotePackage, externalOrderId: externalOrderId, directOrderResult: remoteOrder);

        await using (var seedDb = fixture.CreateContext())
        {
            seedDb.Tenants.Add(tenant);
            seedDb.PlatformConnections.Add(connection);
            seedDb.Orders.Add(order);
            seedDb.OrderLines.Add(line);
            seedDb.ShipmentPackages.AddRange(legacyPackage, currentPackage);
            seedDb.PackageLineAllocations.AddRange(
                new PackageLineAllocation
                {
                    Id = Guid.CreateVersion7(),
                    TenantId = tenant.Id,
                    PackageId = legacyPackageId,
                    OrderLineId = lineId,
                    AllocatedQuantity = 1m,
                    SourceEventId = PackageIngestionSafety.EventId("0", occurredAt)
                },
                new PackageLineAllocation
                {
                    Id = Guid.CreateVersion7(),
                    TenantId = tenant.Id,
                    PackageId = currentPackageId,
                    OrderLineId = lineId,
                    AllocatedQuantity = 1m,
                    SourceEventId = PackageIngestionSafety.EventId(currentPackageNumber, occurredAt)
                });
            seedDb.OrderStatusHistory.AddRange(
                new OrderStatusHistory
                {
                    Id = Guid.CreateVersion7(),
                    TenantId = tenant.Id,
                    OrderId = orderId,
                    PackageId = legacyPackageId,
                    CanonicalStatus = "ON_HOLD",
                    RawStatus = "Awaiting",
                    SourceEventId = PackageIngestionSafety.EventId("0", occurredAt),
                    OccurredAt = occurredAt,
                    RecordedAt = occurredAt
                },
                new OrderStatusHistory
                {
                    Id = Guid.CreateVersion7(),
                    TenantId = tenant.Id,
                    OrderId = orderId,
                    PackageId = currentPackageId,
                    CanonicalStatus = "NEW",
                    RawStatus = "Created",
                    SourceEventId = PackageIngestionSafety.EventId(currentPackageNumber, occurredAt),
                    OccurredAt = occurredAt,
                    RecordedAt = occurredAt
                });
            await seedDb.SaveChangesAsync();
        }

        try
        {
            await using (var processorDb = fixture.CreateContext())
            {
                var processor = new MarketplaceJobProcessor(
                    processorDb,
                    null!, null!, null!, null!, orderPort, null!, null!, null!, null!, null!,
                    new ConfigurationBuilder().Build(), fixture.TimeProvider);
                var result = await processor.ProcessAsync(
                    tenant.Id,
                    connection.Id,
                    MarketplaceJobTypes.OrderSync,
                    $$"""{"externalOrderId":"{{externalOrderId}}","full":false,"packageNumber":"{{currentPackageNumber}}"}""",
                    "trendyol-zero-package-hold-repair",
                    CancellationToken.None);

                Assert.True(result.Succeeded, result.ErrorSummary);
                Assert.Equal(1, orderPort.OrderReadCalls);
            }

            await using var verifyDb = fixture.CreateContext();
            var persistedOrder = await verifyDb.Orders.AsNoTracking().SingleAsync(row => row.TenantId == tenant.Id && row.ExternalOrderId == externalOrderId);
            var persistedPackages = await verifyDb.ShipmentPackages.AsNoTracking()
                .Where(row => row.TenantId == tenant.Id && row.OrderId == orderId)
                .ToDictionaryAsync(row => row.ExternalPackageId);
            Assert.Equal("NEW", persistedOrder.DerivedStatus);
            Assert.Equal(ShipmentPackageStatus.PaymentPending, persistedPackages["0"].Status);
            Assert.Equal(ShipmentPackageStatus.New, persistedPackages[currentPackageNumber].Status);
            var sales = new MarketplaceSalesService(verifyDb,
                new CursorCodec(new EphemeralDataProtectionProvider(), fixture.TimeProvider),
                new ConfigurationBuilder().Build(), null!, null!, null!, null!, fixture.TimeProvider);
            Assert.Equal(currentPackageId, Assert.Single(await sales.CurrentShipmentPackages()
                .Where(package => package.TenantId == tenant.Id).ToListAsync()).Id);
            var summary = await sales.OrderSummaryAsync(tenant.Id, "TRENDYOL", CancellationToken.None);
            Assert.Equal(1, summary.All);
            Assert.Equal(1, summary.New);
            Assert.Equal(0, summary.OnHold);
            Assert.Equal(0, summary.PaymentPending);
            var paymentPending = await sales.OrdersAsync(tenant.Id, 20, 1, null,
                new OrderListQuery(Status: "PAYMENT_PENDING"), CancellationToken.None);
            Assert.Equal(0, paymentPending.TotalCount);
            Assert.Empty(paymentPending.Items);
        }
        finally
        {
            await DeleteInvoiceTestTenantAsync(tenant.Id);
        }
    }

    [PostgreSqlFact]
    public async Task SyncPolicies_ReportCursorTelemetryAndConnectionScopedBacklog()
    {
        var tenant = NewTenant("sync-policy-health");
        var connection = NewQuestionConnection(tenant);
        var otherConnection = NewQuestionConnection(tenant);
        otherConnection.ExternalStoreId = "seller-sync-health-other";
        var now = DateTimeOffset.FromUnixTimeSeconds(fixture.Now.ToUnixTimeSeconds());
        var policy = new ConnectionSyncPolicy
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenant.Id,
            ConnectionId = connection.Id,
            ResourceType = "ORDERS",
            IntervalSeconds = 300,
            OverlapSeconds = 120,
            JitterSeconds = 5,
            Enabled = true,
            Version = 1
        };
        var dailyPolicy = new ConnectionSyncPolicy
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenant.Id,
            ConnectionId = connection.Id,
            ResourceType = "ORDER_RECONCILE_DAILY",
            IntervalSeconds = 86_400,
            OverlapSeconds = 0,
            JitterSeconds = 900,
            Enabled = true,
            Version = 1
        };
        var recoveryPolicy = new ConnectionSyncPolicy
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenant.Id,
            ConnectionId = connection.Id,
            ResourceType = "ORDER_RECOVERY",
            IntervalSeconds = 900,
            OverlapSeconds = 600,
            JitterSeconds = 30,
            Enabled = true,
            Version = 1
        };
        var cursor = new SyncCursor
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenant.Id,
            ConnectionId = connection.Id,
            ResourceType = "ORDERS",
            LastAttemptAt = now.AddMinutes(-3),
            LastSuccessAt = now.AddMinutes(-4),
            LastModifiedWatermark = now.AddDays(-3),
            LastCursorAdvancedAt = now.AddMinutes(-20),
            CursorStagnantSince = now.AddMinutes(-20),
            LastDurationMs = 4_500,
            LastRequestCount = 2,
            LastReceivedCount = 20,
            LastChangedCount = 7,
            LastInsertedCount = 3,
            LastUpdatedCount = 4,
            LastSkippedCount = 1,
            LastFailedCount = 2,
            LastRetryCount = 1,
            LastRateLimitCount = 1,
            ConsecutiveFailureCount = 1,
            Version = 1
        };
        var dailyCursor = new SyncCursor
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenant.Id,
            ConnectionId = connection.Id,
            ResourceType = "ORDER_RECONCILIATION",
            LastAttemptAt = now.AddHours(-24).AddMinutes(-18),
            LastSuccessAt = now.AddHours(-24).AddMinutes(-18),
            LastModifiedWatermark = now.AddDays(-2),
            LastCursorAdvancedAt = now.AddDays(-2),
            Version = 1
        };
        var recoveryCursor = new SyncCursor
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenant.Id,
            ConnectionId = connection.Id,
            ResourceType = "ORDERS_RECOVERY",
            LastAttemptAt = now.AddMinutes(-4),
            LastSuccessAt = now.AddMinutes(-5),
            LastModifiedWatermark = now.AddHours(-1),
            LastReceivedCount = 2,
            LastCursorAdvancedAt = now.AddMinutes(-5),
            Version = 1
        };
        IntegrationJob Job(JobStatus status, DateTimeOffset createdAt, DateTimeOffset? completedAt = null) => new()
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenant.Id,
            ConnectionId = connection.Id,
            JobType = MarketplaceJobTypes.OrderSync,
            PayloadJson = "{}",
            PayloadVersion = 1,
            PayloadHash = "sync-policy-health-payload",
            JobDedupKey = $"sync-policy-health:{Guid.NewGuid():N}",
            EffectIdempotencyKey = $"sync-policy-health:{Guid.NewGuid():N}",
            Status = status,
            AvailableAt = createdAt,
            CorrelationId = "sync-policy-health-test",
            CreatedAt = createdAt,
            CompletedAt = completedAt,
            Version = 1
        };

        await using (var seedDb = fixture.CreateContext())
        {
            seedDb.Tenants.Add(tenant);
            seedDb.PlatformConnections.AddRange(connection, otherConnection);
            seedDb.ConnectionSyncPolicies.AddRange(policy, dailyPolicy, recoveryPolicy);
            seedDb.SyncCursors.AddRange(cursor, dailyCursor, recoveryCursor);
            seedDb.IntegrationJobs.AddRange(
                Job(JobStatus.Pending, now.AddMinutes(-12)),
                Job(JobStatus.Leased, now.AddMinutes(-1)),
                Job(JobStatus.RetryScheduled, now.AddMinutes(-5)),
                Job(JobStatus.Blocked, now.AddDays(-3)),
                Job(JobStatus.ManualReview, now.AddHours(-2)),
                Job(JobStatus.Dead, now.AddHours(-3), now.AddHours(-2)),
                Job(JobStatus.Dead, now.AddDays(-2), now.AddHours(-25)),
                new IntegrationJob
                {
                    Id = Guid.CreateVersion7(),
                    TenantId = tenant.Id,
                    ConnectionId = otherConnection.Id,
                    JobType = MarketplaceJobTypes.OrderSync,
                    PayloadJson = "{}",
                    PayloadVersion = 1,
                    PayloadHash = "other-connection-payload",
                    JobDedupKey = "sync-policy-health:other-connection",
                    EffectIdempotencyKey = "sync-policy-health:other-connection",
                    Status = JobStatus.Pending,
                    AvailableAt = now.AddHours(-4),
                    CorrelationId = "sync-policy-health-other-connection",
                    CreatedAt = now.AddHours(-4),
                    Version = 1
                });
            await seedDb.SaveChangesAsync();
        }

        try
        {
            await using var db = fixture.CreateContext();
            var dataProtection = new EphemeralDataProtectionProvider();
            var service = new MarketplaceConnectionService(
                db,
                new CursorCodec(dataProtection, fixture.TimeProvider),
                dataProtection,
                fixture.TokenHasher,
                new FixedTimeProvider(now),
                new ConfigurationBuilder().Build());

            var result = await service.SyncPoliciesAsync(tenant.Id, connection.Id, CancellationToken.None);

            Assert.True(result.Succeeded);
            var view = Assert.Single(result.Value!, item => item.ResourceType == "ORDERS");
            var dailyView = Assert.Single(result.Value!, item => item.ResourceType == "ORDER_RECONCILE_DAILY");
            var recoveryView = Assert.Single(result.Value!, item => item.ResourceType == "ORDER_RECOVERY");
            Assert.Equal(now.AddMinutes(-3), view.LastAttemptAt);
            Assert.Equal(now.AddMinutes(-4), view.LastSuccessAt);
            Assert.Equal(now.AddMinutes(-20), view.LastCursorAdvancedAt);
            Assert.Equal("STALLED", view.CursorProgressStatus);
            Assert.Equal(4_500, view.LastDurationMs);
            Assert.Equal(2, view.LastRequestCount);
            Assert.Equal(20, view.LastReceivedCount);
            Assert.Equal(7, view.LastChangedCount);
            Assert.Equal(3, view.LastInsertedCount);
            Assert.Equal(4, view.ConnectionBacklogCount);
            Assert.Equal(now.AddDays(-3), view.ConnectionOldestBacklogAt);
            Assert.Equal(1, view.ConnectionManualReviewCount);
            Assert.Equal(1, view.ConnectionDeadJobCount24h);
            Assert.Equal("DELAYED", dailyView.HealthStatus);
            Assert.Equal("NODATA", dailyView.CursorProgressStatus);
            Assert.Equal(now.AddMinutes(-4), recoveryView.LastAttemptAt);
            Assert.Equal(now.AddMinutes(-5), recoveryView.LastSuccessAt);
            Assert.Equal("HEALTHY", recoveryView.HealthStatus);
        }
        finally
        {
            await using var cleanupDb = fixture.CreateContext();
            await cleanupDb.IntegrationOutboxEvents.Where(row => row.TenantId == tenant.Id).ExecuteDeleteAsync();
            await cleanupDb.IntegrationJobs.Where(row => row.TenantId == tenant.Id).ExecuteDeleteAsync();
            await cleanupDb.ConnectionSyncPolicies.Where(row => row.TenantId == tenant.Id).ExecuteDeleteAsync();
            await cleanupDb.SyncCursors.Where(row => row.TenantId == tenant.Id).ExecuteDeleteAsync();
            await cleanupDb.PlatformConnections.Where(row => row.TenantId == tenant.Id).ExecuteDeleteAsync();
            await cleanupDb.Tenants.Where(row => row.Id == tenant.Id).ExecuteDeleteAsync();
        }
    }

    [PostgreSqlFact]
    public async Task SyncCursorProgress_ChangesOnlyWhenCursorOrWatermarkAdvances()
    {
        var tenant = NewTenant("sync-cursor-progress");
        var connection = NewQuestionConnection(tenant);
        var cursor = new SyncCursor
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenant.Id,
            ConnectionId = connection.Id,
            ResourceType = "ORDERS",
            OpaqueCursor = "cursor-1",
            LastModifiedWatermark = fixture.Now.AddHours(-1),
            LastSuccessAt = fixture.Now.AddMinutes(-2),
            Version = 1
        };

        try
        {
            await using var db = fixture.CreateContext();
            db.Tenants.Add(tenant);
            db.PlatformConnections.Add(connection);
            db.SyncCursors.Add(cursor);
            await db.SaveChangesAsync();
            var initialProgressAt = Assert.IsType<DateTimeOffset>(cursor.LastCursorAdvancedAt);

            await Task.Delay(5);
            cursor.LastSuccessAt = fixture.Now;
            cursor.LastReceivedCount = 0;
            await db.SaveChangesAsync();
            Assert.Equal(initialProgressAt, cursor.LastCursorAdvancedAt);
            Assert.Null(cursor.CursorStagnantSince);

            cursor.LastSuccessAt = fixture.Now.AddMinutes(1);
            cursor.LastReceivedCount = 3;
            await db.SaveChangesAsync();
            var stagnantSince = Assert.IsType<DateTimeOffset>(cursor.CursorStagnantSince);

            cursor.LastSuccessAt = fixture.Now.AddMinutes(2);
            await db.SaveChangesAsync();
            Assert.Equal(stagnantSince, cursor.CursorStagnantSince);

            cursor.LastSuccessAt = fixture.Now.AddMinutes(3);
            cursor.LastReceivedCount = 0;
            await db.SaveChangesAsync();
            Assert.Null(cursor.CursorStagnantSince);

            cursor.LastSuccessAt = fixture.Now.AddMinutes(4);
            cursor.LastReceivedCount = 3;
            await db.SaveChangesAsync();
            Assert.NotNull(cursor.CursorStagnantSince);

            await Task.Delay(5);
            cursor.OpaqueCursor = "cursor-2";
            cursor.LastSuccessAt = fixture.Now.AddMinutes(5);
            await db.SaveChangesAsync();
            Assert.True(cursor.LastCursorAdvancedAt > initialProgressAt);
            Assert.Null(cursor.CursorStagnantSince);

            var advancedAt = cursor.LastCursorAdvancedAt;
            cursor.OpaqueCursor = null;
            cursor.LastModifiedWatermark = fixture.Now.AddDays(-2);
            await db.SaveChangesAsync();
            Assert.Equal(advancedAt, cursor.LastCursorAdvancedAt);
        }
        finally
        {
            await DeleteInvoiceTestTenantAsync(tenant.Id);
        }
    }

    [PostgreSqlFact]
    public async Task InvoiceWorkspace_ShowsOpenMarketplaceInvoiceReadFailureAndSummaryOmitsHiddenPackage()
    {
        var tenant = NewTenant("invoice-read-issue-workspace");
        var connection = NewQuestionConnection(tenant);
        var orderId = Guid.CreateVersion7();
        var packageId = Guid.CreateVersion7();
        var order = NewInvoiceTestOrder(tenant.Id, connection.Id, orderId, "invoice-read-error-order", isReturnClaim: false, fixture.Now);
        var package = NewInvoiceTestPackage(tenant.Id, connection.Id, orderId, packageId, "invoice-read-error-package", fixture.Now);
        package.CreatedBy = "MARKETPLACE_DETAIL";
        package.StatusOccurredAt = fixture.Now.AddDays(-6);
        package.MarketplaceInvoiceStatus = MarketplaceInvoiceStatus.NotInvoiced;
        var issue = new OperationalIssue
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenant.Id,
            DedupeKey = $"trendyol-invoice-package-read:{connection.Id}:{package.Id}",
            Code = "REMOTE_5XX",
            Summary = "Trendyol fatura durumu okunamadı; tekrar denenecek.",
            Status = IssueStatus.Open,
            FirstSeenAt = fixture.Now,
            LastSeenAt = fixture.Now,
            OccurrenceCount = 1
        };

        await using (var seedDb = fixture.CreateContext())
        {
            seedDb.Tenants.Add(tenant);
            seedDb.PlatformConnections.Add(connection);
            seedDb.Orders.Add(order);
            seedDb.ShipmentPackages.Add(package);
            seedDb.OperationalIssues.Add(issue);
            seedDb.TenantSettings.Add(new TenantSetting
            {
                TenantId = tenant.Id,
                Key = "invoice-workspace-hidden-packages",
                ValueJson = JsonSerializer.Serialize(new[] { packageId }),
                UpdatedAt = fixture.Now
            });
            await seedDb.SaveChangesAsync();
        }

        try
        {
            await using var db = fixture.CreateContext();
            var dataProtection = new EphemeralDataProtectionProvider();
            var service = new InvoicingBillingService(
                db,
                new CursorCodec(dataProtection, fixture.TimeProvider),
                dataProtection,
                null!,
                new ConfigurationBuilder().Build(),
                fixture.TimeProvider);

            var items = await service.WorkspaceAsync(tenant.Id, CancellationToken.None);
            var summary = await service.WorkspaceSummaryAsync(tenant.Id, CancellationToken.None);

            var item = Assert.Single(items);
            Assert.Equal(package.Id, item.PackageId);
            Assert.Equal("FATURA_BEKLIYOR", item.InvoiceStatus);
            Assert.Equal("REMOTE_5XX", item.MarketplaceInvoiceReadErrorCode);
            Assert.Equal(issue.Summary, item.MarketplaceInvoiceReadErrorSummary);
            Assert.True(item.IsDueSoon);
            Assert.Equal(0, summary.DueSoonCount);
        }
        finally
        {
            await DeleteInvoiceTestTenantAsync(tenant.Id);
        }
    }

    [PostgreSqlFact]
    public async Task InvoiceWorkspacePage_FiltersAndPagesStableRowsWithPageOnlyDetails()
    {
        var tenant = NewTenant("invoice-workspace-page");
        var connection = NewQuestionConnection(tenant);
        var hepsiburadaConnection = NewQuestionConnection(tenant);
        hepsiburadaConnection.PlatformCode = "HEPSIBURADA";
        hepsiburadaConnection.DisplayName = "Hepsiburada fatura filtre testi";
        var seededRows = Enumerable.Range(0, 21).Select(index =>
        {
            var orderId = Guid.CreateVersion7();
            var order = NewInvoiceTestOrder(tenant.Id, connection.Id, orderId, $"page-order-{index:D2}", isReturnClaim: false, fixture.Now);
            var package = NewInvoiceTestPackage(tenant.Id, connection.Id, orderId, Guid.CreateVersion7(), $"page-package-{index:D2}", fixture.Now);
            if (index == 0) package.ConnectionId = hepsiburadaConnection.Id;
            package.CreatedBy = "MARKETPLACE_DETAIL";
            package.StatusOccurredAt = fixture.Now.AddDays(-(6 + index));
            package.MarketplaceInvoiceStatus = MarketplaceInvoiceStatus.NotInvoiced;
            return (order, package);
        }).ToArray();

        await using (var seedDb = fixture.CreateContext())
        {
            seedDb.Tenants.Add(tenant);
            seedDb.PlatformConnections.Add(connection);
            seedDb.PlatformConnections.Add(hepsiburadaConnection);
            seedDb.Orders.AddRange(seededRows.Select(row => row.order));
            seedDb.ShipmentPackages.AddRange(seededRows.Select(row => row.package));
            await seedDb.SaveChangesAsync();
        }

        try
        {
            await using var db = fixture.CreateContext();
            var dataProtection = new EphemeralDataProtectionProvider();
            var service = new InvoicingBillingService(
                db,
                new CursorCodec(dataProtection, fixture.TimeProvider),
                dataProtection,
                null!,
                new ConfigurationBuilder().Build(),
                fixture.TimeProvider);

            var firstPage = await service.WorkspacePageAsync(tenant.Id,
                new InvoiceWorkspacePageQuery(PageNumber: 1, PageSize: 20, Tab: "DUE_SOON"), CancellationToken.None);
            var allPage = await service.WorkspacePageAsync(tenant.Id,
                new InvoiceWorkspacePageQuery(PageNumber: 1, PageSize: 20, Tab: "ALL"), CancellationToken.None);
            var secondPage = await service.WorkspacePageAsync(tenant.Id,
                new InvoiceWorkspacePageQuery(PageNumber: 2, PageSize: 20, Tab: "DUE_SOON"), CancellationToken.None);
            var clampedPage = await service.WorkspacePageAsync(tenant.Id,
                new InvoiceWorkspacePageQuery(PageNumber: 8, PageSize: 20, Tab: "DUE_SOON"), CancellationToken.None);
            var searchedPage = await service.WorkspacePageAsync(tenant.Id,
                new InvoiceWorkspacePageQuery(
                    PageNumber: 1,
                    PageSize: 20,
                    Tab: "DUE_SOON",
                    Search: "PAGE-ORDER-20",
                    PlatformCodes: ["TRENDYOL"],
                    InvoiceStatus: "FATURA_BEKLIYOR",
                    InvoiceAction: "CREATABLE",
                    From: fixture.Now.AddDays(-2),
                    To: fixture.Now,
                    ProviderHasCredential: true), CancellationToken.None);
            var customerSearchPage = await service.WorkspacePageAsync(tenant.Id,
                new InvoiceWorkspacePageQuery(PageNumber: 1, PageSize: 20, Tab: "DUE_SOON", Search: "test"), CancellationToken.None);
            var hepsiburadaPage = await service.WorkspacePageAsync(tenant.Id,
                new InvoiceWorkspacePageQuery(PageNumber: 1, PageSize: 20, Tab: "DUE_SOON", PlatformCodes: ["HEPSIBURADA"]), CancellationToken.None);

            Assert.Equal(21, firstPage.TotalCount);
            Assert.Equal(21, allPage.TotalCount);
            Assert.Equal(21, firstPage.DueSoonCount);
            Assert.Equal(21, firstPage.UninvoicedCount);
            Assert.Equal(21, firstPage.TotalPackageCount);
            Assert.True(firstPage.HasPendingMarketplaceInvoices);
            Assert.Equal(2, firstPage.TotalPages);
            Assert.Equal(20, firstPage.Items.Count);
            Assert.Equal("Test", firstPage.Items[0].CustomerName);
            Assert.Equal("{}", firstPage.Items[0].ShipmentAddressJson);
            Assert.Equal("{}", firstPage.Items[0].InvoiceAddressJson);
            Assert.Single(secondPage.Items);
            Assert.Equal(2, clampedPage.PageNumber);
            Assert.Equal(secondPage.Items.Single().PackageId, Assert.Single(clampedPage.Items).PackageId);
            Assert.Equal(21, secondPage.TotalCount);
            Assert.Equal(seededRows[0].package.Id, firstPage.Items[0].PackageId);
            Assert.Empty(firstPage.Items[0].Lines!);
            Assert.Equal(1, searchedPage.TotalCount);
            Assert.Equal(seededRows[20].package.Id, Assert.Single(searchedPage.Items).PackageId);
            Assert.Equal(21, customerSearchPage.TotalCount);
            Assert.Equal("Test", customerSearchPage.Items[0].CustomerName);
            Assert.Equal(1, hepsiburadaPage.TotalCount);
            Assert.Equal("HEPSIBURADA", Assert.Single(hepsiburadaPage.Items).PlatformCode);
        }
        finally
        {
            await DeleteInvoiceTestTenantAsync(tenant.Id);
        }
    }

    [PostgreSqlFact]
    public async Task InvoiceWorkspacePage_KeepsUnverifiedCustomerInvoiceSnapshotUninvoiced()
    {
        var tenant = NewTenant("invoice-unknown");
        var connection = NewQuestionConnection(tenant);
        var orderId = Guid.CreateVersion7();
        var order = NewInvoiceTestOrder(tenant.Id, connection.Id, orderId, "unknown-invoice-status-order", isReturnClaim: false, fixture.Now);
        order.CustomerSnapshotJson = "{\"customerFirstName\":\"Test\",\"invoiceStatus\":\"Invoiced\"}";
        var package = NewInvoiceTestPackage(tenant.Id, connection.Id, orderId, Guid.CreateVersion7(), "unknown-invoice-status-package", fixture.Now);
        package.CreatedBy = "MARKETPLACE_DETAIL";
        package.StatusOccurredAt = fixture.Now.AddDays(-6);
        package.MarketplaceInvoiceStatus = MarketplaceInvoiceStatus.Unknown;

        await using (var seedDb = fixture.CreateContext())
        {
            seedDb.Tenants.Add(tenant);
            seedDb.PlatformConnections.Add(connection);
            seedDb.Orders.Add(order);
            seedDb.ShipmentPackages.Add(package);
            await seedDb.SaveChangesAsync();
        }

        try
        {
            await using var db = fixture.CreateContext();
            var dataProtection = new EphemeralDataProtectionProvider();
            var service = new InvoicingBillingService(
                db,
                new CursorCodec(dataProtection, fixture.TimeProvider),
                dataProtection,
                null!,
                new ConfigurationBuilder().Build(),
                fixture.TimeProvider);

            var page = await service.WorkspacePageAsync(tenant.Id,
                new InvoiceWorkspacePageQuery(PageNumber: 1, PageSize: 20, Tab: "UNINVOICED"),
                CancellationToken.None);

            Assert.Equal(1, page.TotalCount);
            Assert.Equal(package.Id, Assert.Single(page.Items).PackageId);
            Assert.Equal("FATURA_KONTROLDE", page.Items.Single().InvoiceStatus);

            var invoicedPage = await service.WorkspacePageAsync(tenant.Id,
                new InvoiceWorkspacePageQuery(PageNumber: 1, PageSize: 20, Tab: "INVOICED"),
                CancellationToken.None);
            Assert.Equal(0, invoicedPage.TotalCount);
            Assert.Empty(invoicedPage.Items);
        }
        finally
        {
            await DeleteInvoiceTestTenantAsync(tenant.Id);
        }
    }

    [PostgreSqlFact]
    public async Task InvoiceWorkspacePage_ProcessesOneHundredThousandCandidatesWithUnknownStatusSnapshotsAndReturnsOnlyOnePage()
    {
        const int candidateCount = 100_000;
        const int batchSize = 500;
        const int unknownStatusPeriod = 96;
        const int unknownStatusesPerPeriod = 11;
        var tenant = NewTenant("invoice-workspace-load-100k");
        var connection = NewQuestionConnection(tenant);

        await using (var seedDb = fixture.CreateContext())
        {
            seedDb.Tenants.Add(tenant);
            seedDb.PlatformConnections.Add(connection);
            await seedDb.SaveChangesAsync();
        }

        try
        {
            for (var firstIndex = 0; firstIndex < candidateCount; firstIndex += batchSize)
            {
                await using var seedDb = fixture.CreateContext();
                var batch = Enumerable.Range(firstIndex, Math.Min(batchSize, candidateCount - firstIndex)).Select(index =>
                {
                    var orderId = Guid.CreateVersion7();
                    var order = NewInvoiceTestOrder(tenant.Id, connection.Id, orderId, $"load-order-{index:D5}", isReturnClaim: false, fixture.Now);
                    var package = NewInvoiceTestPackage(tenant.Id, connection.Id, orderId, Guid.CreateVersion7(), $"load-package-{index:D5}", fixture.Now);
                    var hasUnknownInvoiceStatus = index % unknownStatusPeriod < unknownStatusesPerPeriod;
                    package.CreatedBy = "MARKETPLACE_DETAIL";
                    package.StatusOccurredAt = fixture.Now.AddDays(-6);
                    package.MarketplaceInvoiceStatus = hasUnknownInvoiceStatus
                        ? MarketplaceInvoiceStatus.Unknown
                        : MarketplaceInvoiceStatus.NotInvoiced;
                    if (hasUnknownInvoiceStatus)
                    {
                        order.CustomerSnapshotJson = $"{{\"customerId\":\"synthetic-customer\",\"name\":\"Synthetic Customer\",\"marketplaceInvoiceStatus\":\"NOT_INVOICED\",\"phone\":\"0000000000\",\"city\":\"Synthetic City\",\"metadata\":\"{new string('x', 110)}\"}}";
                    }
                    return (order, package);
                }).ToArray();
                seedDb.Orders.AddRange(batch.Select(row => row.order));
                seedDb.ShipmentPackages.AddRange(batch.Select(row => row.package));
                await seedDb.SaveChangesAsync();
            }

            var testConnectionString = Environment.GetEnvironmentVariable("MARKETPLACEHUB_TEST_CONNECTION")
                ?? throw new InvalidOperationException("PostgreSQL load test requires the isolated test connection.");
            var queryPlanInterceptor = new ExplainFirstInvoiceWorkspaceScanInterceptor(output);
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseNpgsql(testConnectionString)
                .AddInterceptors(queryPlanInterceptor)
                .LogTo(message =>
                {
                    const string marker = "Executed DbCommand (";
                    var markerStart = message.IndexOf(marker, StringComparison.Ordinal);
                    if (markerStart < 0) return;
                    var durationStart = markerStart + marker.Length;
                    var durationEnd = message.IndexOf("ms)", durationStart, StringComparison.Ordinal);
                    if (durationEnd <= durationStart || !int.TryParse(message.AsSpan(durationStart, durationEnd - durationStart), out var durationMs) || durationMs < 100) return;
                    var commandStart = message.IndexOf("SELECT", durationEnd, StringComparison.OrdinalIgnoreCase);
                    if (commandStart < 0) commandStart = message.IndexOf("WITH", durationEnd, StringComparison.OrdinalIgnoreCase);
                    var command = commandStart < 0 ? message[durationEnd..] : message[commandStart..];
                    command = command.Replace('\r', ' ').Replace('\n', ' ');
                    output.WriteLine($"Fatura tarama SQL'i {durationMs} ms: {command[..Math.Min(command.Length, 220)]}");
                }, Microsoft.Extensions.Logging.LogLevel.Information)
                .Options;
            await using var db = new AppDbContext(options);
            var dataProtection = new EphemeralDataProtectionProvider();
            var service = new InvoicingBillingService(
                db,
                new CursorCodec(dataProtection, fixture.TimeProvider),
                dataProtection,
                null!,
                new ConfigurationBuilder().Build(),
                fixture.TimeProvider);
            var request = new InvoiceWorkspacePageQuery(PageNumber: 1, PageSize: 20, Tab: "DUE_SOON");
            var sampleDurations = new long[5];
            InvoiceWorkspacePageView? page = null;
            for (var sampleIndex = 0; sampleIndex < sampleDurations.Length; sampleIndex++)
            {
                var timer = System.Diagnostics.Stopwatch.StartNew();
                var measuredPage = await service.WorkspacePageAsync(tenant.Id, request, CancellationToken.None);
                timer.Stop();
                sampleDurations[sampleIndex] = timer.ElapsedMilliseconds;
                page ??= measuredPage;
                Assert.Equal(candidateCount, measuredPage.TotalCount);
                Assert.Equal(candidateCount, measuredPage.DueSoonCount);
                Assert.Equal(candidateCount, measuredPage.UninvoicedCount);
                Assert.Equal(20, measuredPage.Items.Count);
                Assert.Equal(5_000, measuredPage.TotalPages);
            }

            Array.Sort(sampleDurations);
            var p95Sample = sampleDurations[(int)Math.Ceiling(sampleDurations.Length * 0.95) - 1];
            output.WriteLine($"100.000 fatura adayı: {sampleDurations.Length} istek, örnek ms=[{string.Join(",", sampleDurations)}], nearest-rank p95={p95Sample} ms; dönen satır: {page!.Items.Count}.");

            queryPlanInterceptor.CaptureNext = true;
            var diagnosticPage = await service.WorkspacePageAsync(tenant.Id, request, CancellationToken.None);
            Assert.Equal(candidateCount, diagnosticPage.TotalCount);

            await db.Database.OpenConnectionAsync();
            try
            {
                // The 100k rows were inserted just now; without fresh statistics the planner sometimes
                // picks a sequential scan, which made this plan assertion fail intermittently in CI.
                await using (var analyze = db.Database.GetDbConnection().CreateCommand())
                {
                    analyze.CommandText = "ANALYZE sales.shipment_packages";
                    await analyze.ExecuteNonQueryAsync(CancellationToken.None);
                }
                await using var explainCommand = db.Database.GetDbConnection().CreateCommand();
                explainCommand.CommandText = "EXPLAIN (ANALYZE, BUFFERS) SELECT \"Id\", \"StatusOccurredAt\" FROM sales.shipment_packages WHERE \"TenantId\" = @tenantId ORDER BY \"StatusOccurredAt\" DESC, \"Id\" DESC LIMIT 2000";
                var tenantParameter = explainCommand.CreateParameter();
                tenantParameter.ParameterName = "tenantId";
                tenantParameter.Value = tenant.Id;
                explainCommand.Parameters.Add(tenantParameter);
                var planLines = new List<string>();
                await using var planReader = await explainCommand.ExecuteReaderAsync(CancellationToken.None);
                while (await planReader.ReadAsync(CancellationToken.None)) planLines.Add(planReader.GetString(0));
                var plan = string.Join(Environment.NewLine, planLines);
                output.WriteLine($"Fatura aday anahtar-imleç EXPLAIN: {plan}");
                Assert.Contains("IX_shipment_packages_TenantId_StatusOccurredAt_Id", plan, StringComparison.Ordinal);
            }
            finally
            {
                await db.Database.CloseConnectionAsync();
            }

            Assert.NotNull(page);
        }
        finally
        {
            await DeleteInvoiceTestTenantAsync(tenant.Id);
        }
    }

    [PostgreSqlFact]
    public async Task ReturnLifecycle_CursorsBothCargoGroupsAcrossRuns()
    {
        var tenant = NewTenant("return-cursor");
        var connection = NewQuestionConnection(tenant);
        var orderId = Guid.CreateVersion7();
        var order = NewInvoiceTestOrder(tenant.Id, connection.Id, orderId, "return-lifecycle-order", isReturnClaim: false, fixture.Now);
        var missingCargoClaims = Enumerable.Range(0, 30)
            .Select(index => NewReturnLifecycleClaim(tenant.Id, connection.Id, orderId, Guid.Parse($"0199a5a1-1c00-7000-8000-{index + 1:D12}"), $"missing-{index:D2}", fixture.Now, hasCargo: false))
            .ToArray();
        var completeCargoClaims = Enumerable.Range(0, 10)
            .Select(index => NewReturnLifecycleClaim(tenant.Id, connection.Id, orderId, Guid.Parse($"0199a5a1-1c00-7000-8000-{index + 31:D12}"), $"complete-{index:D2}", fixture.Now, hasCargo: true))
            .ToArray();
        var returnsPort = new ReadOnlyReturnTestPort(fixture.Now);

        await using (var seedDb = fixture.CreateContext())
        {
            seedDb.Tenants.Add(tenant);
            seedDb.PlatformConnections.Add(connection);
            seedDb.Orders.Add(order);
            seedDb.ReturnClaims.AddRange(missingCargoClaims.Concat(completeCargoClaims));
            await seedDb.SaveChangesAsync();
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["MarketplaceSync:ReturnLifecycle:BatchSize"] = "4"
        }).Build();

        try
        {
            for (var run = 0; run < 2; run++)
            {
                await using var db = fixture.CreateContext();
                var processor = new MarketplaceJobProcessor(
                    db, null!, null!, null!, null!, null!, null!, null!, returnsPort, null!, null!,
                    configuration, fixture.TimeProvider);
                var result = await processor.ProcessAsync(
                    tenant.Id,
                    connection.Id,
                    MarketplaceJobTypes.ReturnStatusSync,
                    "{}",
                    $"return-lifecycle-cursor-{run}",
                    CancellationToken.None);

                Assert.True(result.Succeeded);
            }

            Assert.Equal(8, returnsPort.ReadCalls.Count);
            Assert.Equal(missingCargoClaims.Take(4).Select(claim => claim.ExternalClaimId), returnsPort.ReadCalls.Where(id => id.StartsWith("missing-", StringComparison.Ordinal)));
            Assert.Equal(completeCargoClaims.Take(4).Select(claim => claim.ExternalClaimId), returnsPort.ReadCalls.Where(id => id.StartsWith("complete-", StringComparison.Ordinal)));

            await using var verificationDb = fixture.CreateContext();
            var cursors = await verificationDb.SyncCursors.AsNoTracking()
                .Where(cursor => cursor.TenantId == tenant.Id && cursor.ConnectionId == connection.Id)
                .ToDictionaryAsync(cursor => cursor.ResourceType);
            Assert.Equal(missingCargoClaims[3].Id.ToString("D"), cursors["RETURN_LIFECYCLE_CARGO_MISSING"].OpaqueCursor);
            Assert.Equal(completeCargoClaims[3].Id.ToString("D"), cursors["RETURN_LIFECYCLE_CARGO_COMPLETE"].OpaqueCursor);
        }
        finally
        {
            await DeleteReturnLifecycleTestTenantAsync(tenant.Id);
        }
    }

    [PostgreSqlFact]
    public async Task HepsiburadaAcceptedReturnSync_MovesClaimOutOfActionRequiredIntoApprovedList()
    {
        var tenant = NewTenant("hepsiburada-return-status-sync");
        var connection = NewQuestionConnection(tenant);
        connection.PlatformCode = "HEPSIBURADA";
        connection.ApiVersion = "V1.0";
        var orderId = Guid.CreateVersion7();
        var order = NewInvoiceTestOrder(tenant.Id, connection.Id, orderId, "return-status-order", isReturnClaim: false, fixture.Now);
        var claim = NewReturnLifecycleClaim(tenant.Id, connection.Id, orderId, Guid.CreateVersion7(), "accepted-claim", fixture.Now, hasCargo: false);
        claim.Status = ReturnClaimStatus.ActionRequired;
        claim.RawStatus = "AwaitingAction";
        claim.LastRemoteModifiedAt = fixture.Now.AddMinutes(-10);
        var returnsPort = new ReadOnlyReturnTestPort(fixture.Now, rawStatus: "Accepted", externalOrderId: order.ExternalOrderId, listedClaimId: "accepted-claim");

        await using (var seedDb = fixture.CreateContext())
        {
            seedDb.Tenants.Add(tenant);
            seedDb.PlatformConnections.Add(connection);
            seedDb.Orders.Add(order);
            seedDb.ReturnClaims.Add(claim);
            await seedDb.SaveChangesAsync();
        }

        try
        {
            await using (var processorDb = fixture.CreateContext())
            {
                var processor = new MarketplaceJobProcessor(
                    processorDb, null!, null!, null!, null!, null!, null!, null!, returnsPort, null!, null!,
                    new ConfigurationBuilder().Build(), fixture.TimeProvider);
                var result = await processor.ProcessAsync(
                    tenant.Id,
                    connection.Id,
                    MarketplaceJobTypes.ReturnStatusSync,
                    "{}",
                    "hepsiburada-return-status-sync",
                    CancellationToken.None);

                Assert.True(result.Succeeded);
                // Hepsiburada has no single-claim read: the open claim is found in the status it moved on to.
                Assert.Empty(returnsPort.ReadCalls);
                Assert.Equal("Accepted", returnsPort.ListedStatuses[0]);
            }

            await using var verificationDb = fixture.CreateContext();
            var persistedClaim = await verificationDb.ReturnClaims.AsNoTracking().SingleAsync(row => row.TenantId == tenant.Id && row.Id == claim.Id);
            Assert.Equal(ReturnClaimStatus.Approved, persistedClaim.Status);
            Assert.Equal("Accepted", persistedClaim.RawStatus);

            var listService = new MarketplaceSalesService(
                verificationDb,
                new CursorCodec(new EphemeralDataProtectionProvider(), fixture.TimeProvider),
                new ConfigurationBuilder().Build(),
                null!, null!, null!, null!, fixture.TimeProvider);
            var approved = await listService.ReturnsAsync(tenant.Id, 20, null, new ReturnListQuery(Status: "APPROVED"), latest: true, CancellationToken.None);
            var actionRequired = await listService.ReturnsAsync(tenant.Id, 20, null, new ReturnListQuery(Status: "ACTION_REQUIRED"), latest: true, CancellationToken.None);
            var analysis = await listService.ReturnsAsync(tenant.Id, 20, null, new ReturnListQuery(Status: "REVIEW"), latest: true, CancellationToken.None);

            Assert.Equal(1, approved.TotalCount);
            Assert.Equal("APPROVED", Assert.Single(approved.Items).Status);
            Assert.Equal(0, actionRequired.TotalCount);
            Assert.Empty(actionRequired.Items);
            Assert.Equal(0, analysis.TotalCount);
            Assert.Empty(analysis.Items);
        }
        finally
        {
            await DeleteReturnLifecycleTestTenantAsync(tenant.Id);
        }
    }

    [PostgreSqlFact]
    public async Task ReturnsList_ShowsClaimsForTenantWhoseOnlyMarketplaceIsN11()
    {
        var tenant = NewTenant("n11-only-returns-list");
        var connection = NewQuestionConnection(tenant);
        connection.PlatformCode = "N11";
        var orderId = Guid.CreateVersion7();
        var order = NewInvoiceTestOrder(tenant.Id, connection.Id, orderId, "n11-return-order", isReturnClaim: false, fixture.Now);
        var claim = NewReturnLifecycleClaim(tenant.Id, connection.Id, orderId, Guid.CreateVersion7(), "n11-claim", fixture.Now, hasCargo: false);

        await using (var seedDb = fixture.CreateContext())
        {
            seedDb.Tenants.Add(tenant);
            seedDb.PlatformConnections.Add(connection);
            seedDb.Orders.Add(order);
            seedDb.ReturnClaims.Add(claim);
            await seedDb.SaveChangesAsync();
        }

        try
        {
            await using var db = fixture.CreateContext();
            var listService = new MarketplaceSalesService(
                db,
                new CursorCodec(new EphemeralDataProtectionProvider(), fixture.TimeProvider),
                new ConfigurationBuilder().Build(),
                null!, null!, null!, null!, fixture.TimeProvider);
            var returns = await listService.ReturnsAsync(tenant.Id, 20, null, new ReturnListQuery(), latest: true, CancellationToken.None);

            Assert.Equal(1, returns.TotalCount);
            var item = Assert.Single(returns.Items);
            Assert.Equal(claim.Id, item.Id);
            Assert.Equal("N11", item.PlatformCode);
        }
        finally
        {
            await DeleteReturnLifecycleTestTenantAsync(tenant.Id);
        }
    }

    [PostgreSqlFact]
    public async Task RequiredAttributeMappingSummary_MatchesPublicationReadinessForCurrentSnapshot()
    {
        var tenant = NewTenant("required-attribute-snapshot");
        var connection = NewQuestionConnection(tenant);
        var productId = Guid.CreateVersion7();
        var categoryId = Guid.CreateVersion7();
        var brandId = Guid.CreateVersion7();
        var attributeId = Guid.CreateVersion7();
        var categorySnapshot = NewReferenceSnapshot(tenant.Id, connection.Id, "CATEGORIES", "", fixture.Now, "categories-v1");
        var brandSnapshot = NewReferenceSnapshot(tenant.Id, connection.Id, "BRANDS", "", fixture.Now, "brands-v1");
        var oldAttributeSnapshot = NewReferenceSnapshot(tenant.Id, connection.Id, "CATEGORY_ATTRIBUTES", "123", fixture.Now.AddDays(-1), "attributes-v1", isCurrent: false);
        var currentAttributeSnapshot = NewReferenceSnapshot(tenant.Id, connection.Id, "CATEGORY_ATTRIBUTES", "123", fixture.Now, "attributes-v2");
        var category = new Category
        {
            Id = categoryId,
            TenantId = tenant.Id,
            Name = "Test kategori",
            NormalizedName = "TEST KATEGORI",
            Path = "Test kategori",
            IsLeaf = true,
            IsActive = true,
            CreatedAt = fixture.Now,
            UpdatedAt = fixture.Now,
            Version = 1
        };
        var brand = new Brand
        {
            Id = brandId,
            TenantId = tenant.Id,
            Name = "Test marka",
            NormalizedName = "TEST MARKA",
            IsActive = true,
            CreatedAt = fixture.Now,
            UpdatedAt = fixture.Now,
            Version = 1
        };
        var attribute = new AttributeDefinition
        {
            Id = attributeId,
            TenantId = tenant.Id,
            Code = $"REQUIRED-{Guid.NewGuid():N}",
            Name = "Beden",
            DataType = AttributeDataType.Text,
            CreatedAt = fixture.Now,
            UpdatedAt = fixture.Now,
            Version = 1
        };
        var product = new Product
        {
            Id = productId,
            TenantId = tenant.Id,
            Title = "Test ürün",
            Description = "Snapshot doğrulama testi",
            CategoryId = categoryId,
            BrandId = brandId,
            CreatedAt = fixture.Now,
            UpdatedAt = fixture.Now,
            Version = 1
        };
        var categoryMapping = new CategoryMapping
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenant.Id,
            ConnectionId = connection.Id,
            SnapshotId = categorySnapshot.Id,
            LocalId = categoryId,
            ExternalId = "123",
            Status = "VERIFIED",
            VerifiedAt = fixture.Now,
            Version = 1
        };
        var brandMapping = new BrandMapping
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenant.Id,
            ConnectionId = connection.Id,
            SnapshotId = brandSnapshot.Id,
            LocalId = brandId,
            ExternalId = "456",
            Status = "VERIFIED",
            VerifiedAt = fixture.Now,
            Version = 1
        };
        var attributeMapping = new AttributeMapping
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenant.Id,
            ConnectionId = connection.Id,
            SnapshotId = oldAttributeSnapshot.Id,
            LocalId = attributeId,
            ScopeExternalId = "123",
            ExternalId = "size",
            Status = "VERIFIED",
            VerifiedAt = fixture.Now.AddDays(-1),
            Version = 1
        };
        var profile = new ChannelListingProfile
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenant.Id,
            ConnectionId = connection.Id,
            ProductId = productId,
            Enabled = true,
            DesiredStatus = "ACTIVE",
            ActualStatus = "DRAFT",
            Version = 1
        };

        await using var db = fixture.CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        db.Tenants.Add(tenant);
        db.PlatformConnections.Add(connection);
        db.Categories.Add(category);
        db.Brands.Add(brand);
        db.AttributeDefinitions.Add(attribute);
        db.Products.Add(product);
        db.ReferenceSnapshots.AddRange(categorySnapshot, brandSnapshot, oldAttributeSnapshot, currentAttributeSnapshot);
        db.ReferenceItems.AddRange(
            NewReferenceItem(tenant.Id, connection.Id, categorySnapshot.Id, "CATEGORIES", "123", "Test kategori", isLeaf: true),
            NewReferenceItem(tenant.Id, connection.Id, brandSnapshot.Id, "BRANDS", "456", "Test marka"),
            NewReferenceItem(tenant.Id, connection.Id, oldAttributeSnapshot.Id, "CATEGORY_ATTRIBUTES", "size", "Beden", isRequired: true),
            NewReferenceItem(tenant.Id, connection.Id, currentAttributeSnapshot.Id, "CATEGORY_ATTRIBUTES", "size", "Beden", isRequired: true));
        db.CategoryMappings.Add(categoryMapping);
        db.BrandMappings.Add(brandMapping);
        db.AttributeMappings.Add(attributeMapping);
        db.ChannelListingProfiles.Add(profile);
        await db.SaveChangesAsync();

        var referenceData = new ReferenceDataService(db, fixture.TimeProvider);
        var publicationComposer = new ProductPublicationComposer(db, new ConfigurationBuilder().Build());
        var staleSummary = await referenceData.ListMappingsAsync(tenant.Id, "categories", connection.Id, "*", CancellationToken.None);
        var stalePublication = await publicationComposer.BuildAsync(tenant.Id, productId, connection.Id, CancellationToken.None);

        Assert.True(staleSummary.Succeeded, staleSummary.Error?.Message);
        Assert.Equal(1, Assert.Single(staleSummary.Value!).MissingRequiredAttributeCount);
        Assert.Equal("REQUIRED_ATTRIBUTE_MAPPING_REQUIRED", stalePublication.Error?.Code);

        attributeMapping.SnapshotId = currentAttributeSnapshot.Id;
        attributeMapping.Version++;
        await db.SaveChangesAsync();

        var currentSummary = await referenceData.ListMappingsAsync(tenant.Id, "categories", connection.Id, "*", CancellationToken.None);
        var currentPublication = await publicationComposer.BuildAsync(tenant.Id, productId, connection.Id, CancellationToken.None);

        Assert.True(currentSummary.Succeeded, currentSummary.Error?.Message);
        Assert.Equal(0, Assert.Single(currentSummary.Value!).MissingRequiredAttributeCount);
        Assert.NotEqual("REQUIRED_ATTRIBUTE_MAPPING_REQUIRED", currentPublication.Error?.Code);
        Assert.Equal("PRODUCT_VARIANT_REQUIRED", currentPublication.Error?.Code);
    }

    [PostgreSqlFact]
    public async Task NameHints_BridgeTheNamesUsedOnTheOtherMarketplace()
    {
        var tenant = NewTenant("name-hints");
        var trendyol = NewQuestionConnection(tenant);
        var hepsiburada = NewQuestionConnection(tenant);
        hepsiburada.PlatformCode = "HEPSIBURADA";
        var fabricId = Guid.CreateVersion7();
        var cotton = new AttributeValue { Id = Guid.CreateVersion7(), TenantId = tenant.Id, AttributeId = fabricId, Value = "Pamuklu", NormalizedValue = "PAMUKLU", SortOrder = 0, IsActive = true, Version = 1 };
        var attributes = NewReferenceSnapshot(tenant.Id, trendyol.Id, "CATEGORY_ATTRIBUTES", "100", fixture.Now, "attributes");
        var fabrics = NewReferenceSnapshot(tenant.Id, trendyol.Id, "ATTRIBUTE_VALUES", "100/21", fixture.Now, "fabrics");

        await using var db = fixture.CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        db.Tenants.Add(tenant);
        db.PlatformConnections.AddRange(trendyol, hepsiburada);
        db.AttributeDefinitions.Add(new AttributeDefinition { Id = fabricId, TenantId = tenant.Id, Code = $"FABRIC-{Guid.NewGuid():N}", Name = "Kumaş", DataType = AttributeDataType.Text, CreatedAt = fixture.Now, UpdatedAt = fixture.Now, Version = 1 });
        db.AttributeValues.Add(cotton);
        db.ReferenceSnapshots.AddRange(attributes, fabrics);
        db.ReferenceItems.AddRange(
            NewReferenceItem(tenant.Id, trendyol.Id, attributes.Id, "CATEGORY_ATTRIBUTES", "21", "Materyal"),
            NewReferenceItem(tenant.Id, trendyol.Id, fabrics.Id, "ATTRIBUTE_VALUES", "301", "%100 Pamuk"));
        db.AttributeMappingTemplates.Add(new AttributeMappingTemplate { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = trendyol.Id, ExternalAttributeId = "21", LocalAttributeId = fabricId, UpdatedAt = fixture.Now });
        db.AttributeValueMappingTemplates.Add(new AttributeValueMappingTemplate { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = trendyol.Id, ExternalAttributeId = "21", LocalValueId = cotton.Id, ExternalValueId = "301", UpdatedAt = fixture.Now });
        await db.SaveChangesAsync();
        var service = new ReferenceDataService(db, fixture.TimeProvider);

        var valueHints = await service.MarketplaceNameHintsAsync(tenant.Id, fabricId, hepsiburada.Id, CancellationToken.None);
        var attributeHints = await service.AttributeNameHintsAsync(tenant.Id, hepsiburada.Id, CancellationToken.None);
        var ownHints = await service.MarketplaceNameHintsAsync(tenant.Id, fabricId, trendyol.Id, CancellationToken.None);

        Assert.Equal(["Materyal"], valueHints.Value!.AttributeNames);
        Assert.Equal(["%100 Pamuk"], valueHints.Value.ValueNames[cotton.Id]);
        Assert.Equal(["Materyal"], attributeHints.Value![fabricId]);
        // The target connection's own decisions are not offered as hints for itself.
        Assert.Empty(ownHints.Value!.AttributeNames);
        Assert.Empty(ownHints.Value.ValueNames);
        await transaction.RollbackAsync();
    }

    [PostgreSqlFact]
    public async Task MappingConflicts_ListSameCategoryDuplicatesAndCrossCategoryDifferences()
    {
        var tenant = NewTenant("mapping-conflicts");
        var connection = NewQuestionConnection(tenant);
        var colourId = Guid.CreateVersion7();
        var black = new AttributeValue { Id = Guid.CreateVersion7(), TenantId = tenant.Id, AttributeId = colourId, Value = "Siyah", NormalizedValue = "SİYAH", SortOrder = 0, IsActive = true, Version = 1 };
        var tshirtColours = NewReferenceSnapshot(tenant.Id, connection.Id, "ATTRIBUTE_VALUES", "100/12", fixture.Now, "tshirt-colours");
        var blouseColours = NewReferenceSnapshot(tenant.Id, connection.Id, "ATTRIBUTE_VALUES", "200/12", fixture.Now, "blouse-colours");
        AttributeValueMapping Mapping(ReferenceSnapshot snapshot, string externalId) => new() { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, SnapshotId = snapshot.Id, LocalId = black.Id, ScopeExternalId = snapshot.ScopeExternalId, ExternalId = externalId, Status = "VERIFIED", VerifiedAt = fixture.Now, Version = 1 };

        await using var db = fixture.CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        db.Tenants.Add(tenant);
        db.PlatformConnections.Add(connection);
        db.AttributeDefinitions.Add(new AttributeDefinition { Id = colourId, TenantId = tenant.Id, Code = $"COLOUR-{Guid.NewGuid():N}", Name = "Renk", DataType = AttributeDataType.Text, CreatedAt = fixture.Now, UpdatedAt = fixture.Now, Version = 1 });
        db.AttributeValues.Add(black);
        db.ReferenceSnapshots.AddRange(tshirtColours, blouseColours);
        db.ReferenceItems.AddRange(
            NewReferenceItem(tenant.Id, connection.Id, tshirtColours.Id, "ATTRIBUTE_VALUES", "13", "Siyah"),
            NewReferenceItem(tenant.Id, connection.Id, tshirtColours.Id, "ATTRIBUTE_VALUES", "14", "Siyah Melanj"),
            NewReferenceItem(tenant.Id, connection.Id, blouseColours.Id, "ATTRIBUTE_VALUES", "15", "Gece Siyahı"));
        // T-shirt maps Siyah to both 13 and 14; Bluz maps it to 15.
        db.AttributeValueMappings.AddRange(Mapping(tshirtColours, "13"), Mapping(tshirtColours, "14"), Mapping(blouseColours, "15"));
        await db.SaveChangesAsync();

        var conflicts = await new ReferenceDataService(db, fixture.TimeProvider).ListMappingConflictsAsync(tenant.Id, connection.Id, CancellationToken.None);

        Assert.True(conflicts.Succeeded, conflicts.Error?.Message);
        var same = Assert.Single(conflicts.Value!.SameCategory);
        Assert.Equal(("100/12", "Siyah"), (same.ScopeExternalId, same.LocalValueName));
        Assert.Equal(new[] { "Siyah", "Siyah Melanj" }, same.Options.Select(option => option.Name).Order());
        var cross = Assert.Single(conflicts.Value.CrossCategory);
        Assert.Equal(3, cross.Options.Count);
        await transaction.RollbackAsync();
    }

    [PostgreSqlFact]
    public async Task ConnectionDefault_CoversARequiredAttributeTheProductLeavesEmpty()
    {
        var tenant = NewTenant("attribute-default");
        var connection = NewQuestionConnection(tenant);
        var categoryId = Guid.CreateVersion7();
        var brandId = Guid.CreateVersion7();
        var productId = Guid.CreateVersion7();
        var categories = NewReferenceSnapshot(tenant.Id, connection.Id, "CATEGORIES", "", fixture.Now, "categories");
        var brands = NewReferenceSnapshot(tenant.Id, connection.Id, "BRANDS", "", fixture.Now, "brands");
        var attributes = NewReferenceSnapshot(tenant.Id, connection.Id, "CATEGORY_ATTRIBUTES", "123", fixture.Now, "attributes");
        var origins = NewReferenceSnapshot(tenant.Id, connection.Id, "ATTRIBUTE_VALUES", "123/500", fixture.Now, "origins");

        await using var db = fixture.CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        db.Tenants.Add(tenant);
        db.PlatformConnections.Add(connection);
        db.Categories.Add(new Category { Id = categoryId, TenantId = tenant.Id, Name = "Test kategori", NormalizedName = "TEST KATEGORI", Path = "Test kategori", IsLeaf = true, IsActive = true, CreatedAt = fixture.Now, UpdatedAt = fixture.Now, Version = 1 });
        db.Brands.Add(new Brand { Id = brandId, TenantId = tenant.Id, Name = "Test marka", NormalizedName = "TEST MARKA", IsActive = true, CreatedAt = fixture.Now, UpdatedAt = fixture.Now, Version = 1 });
        db.Products.Add(new Product { Id = productId, TenantId = tenant.Id, Title = "Test ürün", Description = "Varsayılan testi", CategoryId = categoryId, BrandId = brandId, CreatedAt = fixture.Now, UpdatedAt = fixture.Now, Version = 1 });
        db.ReferenceSnapshots.AddRange(categories, brands, attributes, origins);
        db.ReferenceItems.AddRange(
            NewReferenceItem(tenant.Id, connection.Id, categories.Id, "CATEGORIES", "123", "Test kategori", isLeaf: true),
            NewReferenceItem(tenant.Id, connection.Id, brands.Id, "BRANDS", "456", "Test marka"),
            NewReferenceItem(tenant.Id, connection.Id, attributes.Id, "CATEGORY_ATTRIBUTES", "500", "Menşei", isRequired: true),
            NewReferenceItem(tenant.Id, connection.Id, origins.Id, "ATTRIBUTE_VALUES", "77", "TR"));
        db.CategoryMappings.Add(new CategoryMapping { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, SnapshotId = categories.Id, LocalId = categoryId, ExternalId = "123", Status = "VERIFIED", VerifiedAt = fixture.Now, Version = 1 });
        db.BrandMappings.Add(new BrandMapping { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, SnapshotId = brands.Id, LocalId = brandId, ExternalId = "456", Status = "VERIFIED", VerifiedAt = fixture.Now, Version = 1 });
        db.ChannelListingProfiles.Add(new ChannelListingProfile { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, ProductId = productId, Enabled = true, DesiredStatus = "ACTIVE", ActualStatus = "DRAFT", Version = 1 });
        await db.SaveChangesAsync();

        var referenceData = new ReferenceDataService(db, fixture.TimeProvider);
        var composer = new ProductPublicationComposer(db, new ConfigurationBuilder().Build());
        Assert.Equal("REQUIRED_ATTRIBUTE_MAPPING_REQUIRED", (await composer.BuildAsync(tenant.Id, productId, connection.Id, CancellationToken.None)).Error?.Code);

        Assert.Equal("ATTRIBUTE_DEFAULT_VALUE_REQUIRED", (await referenceData.UpsertAttributeDefaultAsync(tenant.Id, connection.Id, "500", new UpsertConnectionAttributeDefaultCommand("Menşei", null, null, null), CancellationToken.None)).Error?.Code);
        var saved = await referenceData.UpsertAttributeDefaultAsync(tenant.Id, connection.Id, "500", new UpsertConnectionAttributeDefaultCommand("Menşei", "77", "TR", null), CancellationToken.None);
        Assert.True(saved.Succeeded, saved.Error?.Message);

        // The required attribute no longer blocks: the next check is the missing variant.
        Assert.Equal("PRODUCT_VARIANT_REQUIRED", (await composer.BuildAsync(tenant.Id, productId, connection.Id, CancellationToken.None)).Error?.Code);
        var summary = await referenceData.ListMappingsAsync(tenant.Id, "categories", connection.Id, "*", CancellationToken.None);
        Assert.Equal(0, Assert.Single(summary.Value!).MissingRequiredAttributeCount);
        Assert.Equal("TR", Assert.Single((await referenceData.ListAttributeDefaultsAsync(tenant.Id, connection.Id, CancellationToken.None)).Value!).ValueName);

        await transaction.RollbackAsync();
    }

    [PostgreSqlFact]
    public async Task ConnectionDefault_CoversAHepsiburadaRequiredAttributeOnlyWhileItsValueIsOffered()
    {
        var tenant = NewTenant("hb-attribute-default");
        var connection = NewQuestionConnection(tenant);
        connection.PlatformCode = "HEPSIBURADA";
        var categoryId = Guid.CreateVersion7();
        var productId = Guid.CreateVersion7();
        var categories = NewReferenceSnapshot(tenant.Id, connection.Id, "CATEGORIES", "", fixture.Now, "categories");
        var attributes = NewReferenceSnapshot(tenant.Id, connection.Id, "CATEGORY_ATTRIBUTES", "123", fixture.Now, "attributes");
        var origins = NewReferenceSnapshot(tenant.Id, connection.Id, "ATTRIBUTE_VALUES", "123/mensei", fixture.Now, "origins");

        await using var db = fixture.CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        db.Tenants.Add(tenant);
        db.PlatformConnections.Add(connection);
        db.Categories.Add(new Category { Id = categoryId, TenantId = tenant.Id, Name = "Test kategori", NormalizedName = "TEST KATEGORI", Path = "Test kategori", IsLeaf = true, IsActive = true, CreatedAt = fixture.Now, UpdatedAt = fixture.Now, Version = 1 });
        db.Products.Add(new Product { Id = productId, TenantId = tenant.Id, Title = "Test ürün", Description = "Varsayılan testi", CategoryId = categoryId, CreatedAt = fixture.Now, UpdatedAt = fixture.Now, Version = 1 });
        db.ReferenceSnapshots.AddRange(categories, attributes, origins);
        db.ReferenceItems.AddRange(
            NewReferenceItem(tenant.Id, connection.Id, categories.Id, "CATEGORIES", "123", "Test kategori", isLeaf: true),
            NewReferenceItem(tenant.Id, connection.Id, attributes.Id, "CATEGORY_ATTRIBUTES", "mensei", "Menşei", isRequired: true),
            NewReferenceItem(tenant.Id, connection.Id, origins.Id, "ATTRIBUTE_VALUES", "tr", "Türkiye"));
        db.CategoryMappings.Add(new CategoryMapping { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, SnapshotId = categories.Id, LocalId = categoryId, ExternalId = "123", Status = "VERIFIED", VerifiedAt = fixture.Now, Version = 1 });
        db.ChannelListingProfiles.Add(new ChannelListingProfile { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, ProductId = productId, Enabled = true, DesiredStatus = "ACTIVE", ActualStatus = "DRAFT", Version = 1 });
        await db.SaveChangesAsync();

        var referenceData = new ReferenceDataService(db, fixture.TimeProvider);
        var composer = new HepsiburadaProductPublicationComposer(db, new ConfigurationBuilder().Build());
        Assert.Equal("REQUIRED_ATTRIBUTE_MAPPING_REQUIRED", (await composer.BuildAsync(tenant.Id, productId, connection.Id, CancellationToken.None)).Error?.Code);

        // A default whose value is not in the category's value list does not count.
        Assert.True((await referenceData.UpsertAttributeDefaultAsync(tenant.Id, connection.Id, "mensei", new UpsertConnectionAttributeDefaultCommand("Menşei", "cn", "Çin", null), CancellationToken.None)).Succeeded);
        Assert.Equal("REQUIRED_ATTRIBUTE_MAPPING_REQUIRED", (await composer.BuildAsync(tenant.Id, productId, connection.Id, CancellationToken.None)).Error?.Code);

        Assert.True((await referenceData.UpsertAttributeDefaultAsync(tenant.Id, connection.Id, "mensei", new UpsertConnectionAttributeDefaultCommand("Menşei", "tr", "Türkiye", null), CancellationToken.None)).Succeeded);
        // The required attribute no longer blocks: the next check is the missing brand.
        Assert.Equal("BRAND_REQUIRED", (await composer.BuildAsync(tenant.Id, productId, connection.Id, CancellationToken.None)).Error?.Code);

        await transaction.RollbackAsync();
    }

    [PostgreSqlFact]
    public async Task SavedMappings_KeepTheConnectionWideTemplatesUpToDate()
    {
        var tenant = NewTenant("mapping-templates");
        var connection = NewQuestionConnection(tenant);
        var attributeId = Guid.CreateVersion7();
        var blackId = Guid.CreateVersion7();
        var attribute = new AttributeDefinition { Id = attributeId, TenantId = tenant.Id, Code = $"COLOUR-{Guid.NewGuid():N}", Name = "Renk", DataType = AttributeDataType.Text, CreatedAt = fixture.Now, UpdatedAt = fixture.Now, Version = 1 };
        var black = new AttributeValue { Id = blackId, TenantId = tenant.Id, AttributeId = attributeId, Value = "Siyah", NormalizedValue = "SİYAH", SortOrder = 0, IsActive = true, Version = 1 };
        // T-shirt (100) and Bluz (200) both list Trendyol attribute 12.
        var tshirtAttributes = NewReferenceSnapshot(tenant.Id, connection.Id, "CATEGORY_ATTRIBUTES", "100", fixture.Now, "tshirt-attributes");
        var tshirtColours = NewReferenceSnapshot(tenant.Id, connection.Id, "ATTRIBUTE_VALUES", "100/12", fixture.Now, "tshirt-colours");
        var blouseColours = NewReferenceSnapshot(tenant.Id, connection.Id, "ATTRIBUTE_VALUES", "200/12", fixture.Now, "blouse-colours");

        await using var db = fixture.CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        db.Tenants.Add(tenant);
        db.PlatformConnections.Add(connection);
        db.AttributeDefinitions.Add(attribute);
        db.AttributeValues.Add(black);
        db.ReferenceSnapshots.AddRange(tshirtAttributes, tshirtColours, blouseColours);
        db.ReferenceItems.AddRange(
            NewReferenceItem(tenant.Id, connection.Id, tshirtAttributes.Id, "CATEGORY_ATTRIBUTES", "12", "Renk"),
            NewReferenceItem(tenant.Id, connection.Id, tshirtColours.Id, "ATTRIBUTE_VALUES", "13", "Siyah"),
            NewReferenceItem(tenant.Id, connection.Id, blouseColours.Id, "ATTRIBUTE_VALUES", "13", "Siyah"),
            NewReferenceItem(tenant.Id, connection.Id, blouseColours.Id, "ATTRIBUTE_VALUES", "14", "Siyah Melanj"));
        await db.SaveChangesAsync();
        var service = new ReferenceDataService(db, fixture.TimeProvider);

        var attributeSaved = await service.UpsertMappingAsync(tenant.Id, "attributes", attributeId, null, new UpsertCatalogMappingCommand(connection.Id, tshirtAttributes.Id, "12", "VERIFIED"), CancellationToken.None);
        var tshirtSaved = await service.UpsertMappingAsync(tenant.Id, "attribute-values", blackId, null, new UpsertCatalogMappingCommand(connection.Id, tshirtColours.Id, "13", "VERIFIED"), CancellationToken.None);
        Assert.True(attributeSaved.Succeeded, attributeSaved.Error?.Message);
        Assert.True(tshirtSaved.Succeeded, tshirtSaved.Error?.Message);
        var attributeTemplate = await db.AttributeMappingTemplates.AsNoTracking().SingleAsync(x => x.TenantId == tenant.Id);
        Assert.Equal(("12", attributeId, "ATTRIBUTE"), (attributeTemplate.ExternalAttributeId, attributeTemplate.LocalAttributeId, attributeTemplate.Role));
        Assert.Equal("13", (await db.AttributeValueMappingTemplates.AsNoTracking().SingleAsync(x => x.TenantId == tenant.Id)).ExternalValueId);

        // A later decision in another category becomes the template.
        var blouseSaved = await service.UpsertMappingAsync(tenant.Id, "attribute-values", blackId, null, new UpsertCatalogMappingCommand(connection.Id, blouseColours.Id, "14", "VERIFIED"), CancellationToken.None);
        Assert.True(blouseSaved.Succeeded, blouseSaved.Error?.Message);
        Assert.Equal("14", (await db.AttributeValueMappingTemplates.AsNoTracking().SingleAsync(x => x.TenantId == tenant.Id)).ExternalValueId);

        // Removing it falls back to the T-shirt decision; removing that too clears the template.
        Assert.True((await service.DeleteMappingAsync(tenant.Id, "attribute-values", blackId, connection.Id, "200/12", "14", blouseSaved.Value!.Version, CancellationToken.None)).Succeeded);
        Assert.Equal("13", (await db.AttributeValueMappingTemplates.AsNoTracking().SingleAsync(x => x.TenantId == tenant.Id)).ExternalValueId);
        Assert.True((await service.DeleteMappingAsync(tenant.Id, "attribute-values", blackId, connection.Id, "100/12", "13", tshirtSaved.Value!.Version, CancellationToken.None)).Succeeded);
        Assert.False(await db.AttributeValueMappingTemplates.AsNoTracking().AnyAsync(x => x.TenantId == tenant.Id));

        await transaction.RollbackAsync();
    }

    [PostgreSqlFact]
    public async Task UsedAttributeValues_ComeFromAttributeAssignmentsAndSameNamedVariantOptions()
    {
        var tenant = NewTenant("used-attribute-values");
        var colourId = Guid.CreateVersion7();
        var black = new AttributeValue { Id = Guid.CreateVersion7(), TenantId = tenant.Id, AttributeId = colourId, Value = "Siyah", NormalizedValue = "SİYAH", SortOrder = 0, IsActive = true, Version = 1 };
        var purple = new AttributeValue { Id = Guid.CreateVersion7(), TenantId = tenant.Id, AttributeId = colourId, Value = "Mor", NormalizedValue = "MOR", SortOrder = 1, IsActive = true, Version = 1 };
        var white = new AttributeValue { Id = Guid.CreateVersion7(), TenantId = tenant.Id, AttributeId = colourId, Value = "Beyaz", NormalizedValue = "BEYAZ", SortOrder = 2, IsActive = true, Version = 1 };
        var product = new Product { Id = Guid.CreateVersion7(), TenantId = tenant.Id, Title = $"Used values {Guid.NewGuid():N}", Description = "Used values test", DefaultListPrice = 0m, DefaultSalePrice = 0m, CreatedAt = fixture.Now, UpdatedAt = fixture.Now, Version = 1 };
        var option = new ProductOption { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ProductId = product.Id, Label = "Renk", NormalizedKey = "RENK", SortOrder = 0 };

        await using var db = fixture.CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        db.Tenants.Add(tenant);
        db.AttributeDefinitions.Add(new AttributeDefinition { Id = colourId, TenantId = tenant.Id, Code = $"COLOUR-{Guid.NewGuid():N}", Name = "Renk", DataType = AttributeDataType.Text, CreatedAt = fixture.Now, UpdatedAt = fixture.Now, Version = 1 });
        db.AttributeValues.AddRange(black, purple, white);
        db.Products.Add(product);
        db.ProductOptions.Add(option);
        // Siyah is a variant option value, Mor a product attribute; Beyaz is not used.
        db.ProductOptionValues.Add(new ProductOptionValue { Id = Guid.CreateVersion7(), TenantId = tenant.Id, OptionId = option.Id, Label = "Siyah", NormalizedKey = "SİYAH", SortOrder = 0 });
        db.ProductAttributeAssignments.Add(new ProductAttributeAssignment { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ProductId = product.Id, AttributeId = colourId, ValueId = purple.Id, Version = 1 });
        await db.SaveChangesAsync();

        var service = new CatalogService(db, null!, new ConfigurationBuilder().Build(), fixture.TimeProvider, new MemoryCache(new MemoryCacheOptions()));
        var used = await service.UsedAttributeValueIdsAsync(tenant.Id, colourId, CancellationToken.None);

        Assert.True(used.Succeeded, used.Error?.Message);
        Assert.Equal(new[] { black.Id, purple.Id }.Order(), used.Value!.Order());
        await transaction.RollbackAsync();
    }

    [PostgreSqlFact]
    public async Task MappingANewCategory_FillsItFromTheTemplatesButRespectsTheUser()
    {
        var tenant = NewTenant("mapping-template-fill");
        var connection = NewQuestionConnection(tenant);
        var colourId = Guid.CreateVersion7();
        var blackId = Guid.CreateVersion7();
        var whiteId = Guid.CreateVersion7();
        var blouseId = Guid.CreateVersion7();
        var colour = new AttributeDefinition { Id = colourId, TenantId = tenant.Id, Code = $"COLOUR-{Guid.NewGuid():N}", Name = "Renk", DataType = AttributeDataType.Text, CreatedAt = fixture.Now, UpdatedAt = fixture.Now, Version = 1 };
        var black = new AttributeValue { Id = blackId, TenantId = tenant.Id, AttributeId = colourId, Value = "Siyah", NormalizedValue = "SİYAH", SortOrder = 0, IsActive = true, Version = 1 };
        var white = new AttributeValue { Id = whiteId, TenantId = tenant.Id, AttributeId = colourId, Value = "Beyaz", NormalizedValue = "BEYAZ", SortOrder = 1, IsActive = true, Version = 1 };
        var blouse = new Category { Id = blouseId, TenantId = tenant.Id, Name = "Bluz", NormalizedName = "BLUZ", Path = "Bluz", IsLeaf = true, IsActive = true, CreatedAt = fixture.Now, UpdatedAt = fixture.Now, Version = 1 };
        var categories = NewReferenceSnapshot(tenant.Id, connection.Id, "CATEGORIES", "", fixture.Now, "categories");
        var blouseAttributes = NewReferenceSnapshot(tenant.Id, connection.Id, "CATEGORY_ATTRIBUTES", "200", fixture.Now, "blouse-attributes");
        var blouseColours = NewReferenceSnapshot(tenant.Id, connection.Id, "ATTRIBUTE_VALUES", "200/12", fixture.Now, "blouse-colours");

        await using var db = fixture.CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        db.Tenants.Add(tenant);
        db.PlatformConnections.Add(connection);
        db.AttributeDefinitions.Add(colour);
        db.AttributeValues.AddRange(black, white);
        db.Categories.Add(blouse);
        db.ReferenceSnapshots.AddRange(categories, blouseAttributes, blouseColours);
        db.ReferenceItems.AddRange(
            NewReferenceItem(tenant.Id, connection.Id, categories.Id, "CATEGORIES", "200", "Bluz", isLeaf: true),
            NewReferenceItem(tenant.Id, connection.Id, blouseAttributes.Id, "CATEGORY_ATTRIBUTES", "12", "Renk", isRequired: true),
            // Bluz offers Siyah (13) but not the Beyaz value (15) chosen in T-shirt.
            NewReferenceItem(tenant.Id, connection.Id, blouseColours.Id, "ATTRIBUTE_VALUES", "13", "Siyah"));
        // Decisions made earlier in T-shirt: Panel Renk = Trendyol 12, Siyah = 13, Beyaz = 15.
        db.AttributeMappingTemplates.Add(new AttributeMappingTemplate { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, ExternalAttributeId = "12", LocalAttributeId = colourId, Role = "ATTRIBUTE", UpdatedAt = fixture.Now });
        db.AttributeValueMappingTemplates.AddRange(
            new AttributeValueMappingTemplate { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, ExternalAttributeId = "12", LocalValueId = blackId, ExternalValueId = "13", UpdatedAt = fixture.Now },
            new AttributeValueMappingTemplate { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, ExternalAttributeId = "12", LocalValueId = whiteId, ExternalValueId = "15", UpdatedAt = fixture.Now });
        await db.SaveChangesAsync();
        var service = new ReferenceDataService(db, fixture.TimeProvider);

        var mapped = await service.UpsertMappingAsync(tenant.Id, "categories", blouseId, null, new UpsertCatalogMappingCommand(connection.Id, categories.Id, "200", "VERIFIED"), CancellationToken.None);
        Assert.True(mapped.Succeeded, mapped.Error?.Message);

        var attributeMapping = await db.AttributeMappings.AsNoTracking().SingleAsync(x => x.TenantId == tenant.Id && x.ScopeExternalId == "200");
        Assert.Equal((colourId, "12", MappingSources.Template), (attributeMapping.LocalId, attributeMapping.ExternalId, attributeMapping.Source));
        var requirement = await db.CategoryAttributeRequirements.AsNoTracking().SingleAsync(x => x.TenantId == tenant.Id && x.CategoryId == blouseId);
        Assert.True(requirement.IsRequired);
        var valueMapping = await db.AttributeValueMappings.AsNoTracking().SingleAsync(x => x.TenantId == tenant.Id && x.ScopeExternalId == "200/12");
        Assert.Equal((blackId, "13", MappingSources.Template), (valueMapping.LocalId, valueMapping.ExternalId, valueMapping.Source));

        // The user removes Siyah from Bluz: a later fill (e.g. the next reference sync) leaves it out.
        Assert.True((await service.DeleteMappingAsync(tenant.Id, "attribute-values", blackId, connection.Id, "200/12", "13", valueMapping.Version, CancellationToken.None)).Succeeded);
        Assert.Equal(0, await MappingTemplateFiller.FillValuesAsync(db, tenant.Id, connection.Id, "200/12", fixture.Now, CancellationToken.None));
        var manualFill = await service.FillFromTemplatesAsync(tenant.Id, connection.Id, "200", CancellationToken.None);
        Assert.True(manualFill.Succeeded, manualFill.Error?.Message);
        Assert.Equal(new MappingTemplateFillView(0, 0), manualFill.Value);
        Assert.False(await db.AttributeValueMappings.AsNoTracking().AnyAsync(x => x.TenantId == tenant.Id && x.ScopeExternalId == "200/12"));
        Assert.Equal("CATEGORY_MAPPING_REQUIRED", (await service.FillFromTemplatesAsync(tenant.Id, connection.Id, "999", CancellationToken.None)).Error?.Code);

        await transaction.RollbackAsync();
    }

    [PostgreSqlFact]
    public async Task ReferenceSync_RevalidatesUnchangedMappingsButKeepsChangedContractsStale()
    {
        var tenant = NewTenant("reference-revalidation");
        var connection = NewQuestionConnection(tenant);
        var categoryId = Guid.CreateVersion7();
        var brandId = Guid.CreateVersion7();
        var attributeId = Guid.CreateVersion7();
        var valueId = Guid.CreateVersion7();
        var oldCategories = NewReferenceSnapshot(tenant.Id, connection.Id, "CATEGORIES", "", fixture.Now.AddDays(-1), "categories-old");
        var oldBrands = NewReferenceSnapshot(tenant.Id, connection.Id, "BRANDS", "", fixture.Now.AddDays(-1), "brands-old");
        var oldAttributes = NewReferenceSnapshot(tenant.Id, connection.Id, "CATEGORY_ATTRIBUTES", "123", fixture.Now.AddDays(-1), "attributes-old");
        var oldValues = NewReferenceSnapshot(tenant.Id, connection.Id, "ATTRIBUTE_VALUES", "123/size", fixture.Now.AddDays(-1), "values-old");
        var category = new Category
        {
            Id = categoryId,
            TenantId = tenant.Id,
            Name = "Test kategori",
            NormalizedName = "TEST KATEGORI",
            Path = "Test kategori",
            IsLeaf = true,
            IsActive = true,
            CreatedAt = fixture.Now,
            UpdatedAt = fixture.Now,
            Version = 1
        };
        var attribute = new AttributeDefinition
        {
            Id = attributeId,
            TenantId = tenant.Id,
            Code = $"REFERENCE-{Guid.NewGuid():N}",
            Name = "Beden",
            DataType = AttributeDataType.Text,
            CreatedAt = fixture.Now,
            UpdatedAt = fixture.Now,
            Version = 1
        };
        var brand = new Brand
        {
            Id = brandId,
            TenantId = tenant.Id,
            Name = "Test marka",
            NormalizedName = "TEST MARKA",
            IsActive = true,
            CreatedAt = fixture.Now,
            UpdatedAt = fixture.Now,
            Version = 1
        };
        var attributeValue = new AttributeValue
        {
            Id = valueId,
            TenantId = tenant.Id,
            AttributeId = attributeId,
            Value = "Mürdüm",
            NormalizedValue = "MÜRDÜM",
            SortOrder = 0,
            IsActive = true,
            Version = 1
        };
        var categoryRemote = new RemoteReferenceItem("CATEGORIES", "123", null, "Test kategori", "Test kategori", 0, true, true, "{\"id\":\"123\"}");
        var anotherCategory = new RemoteReferenceItem("CATEGORIES", "999", null, "Başka kategori", "Başka kategori", 0, true, true, "{\"id\":\"999\"}");
        var brandRemote = new RemoteReferenceItem("BRANDS", "456", null, "Test marka", "Test marka", 0, false, true, "{\"id\":\"456\"}");
        var anotherBrand = new RemoteReferenceItem("BRANDS", "789", null, "Başka marka", "Başka marka", 0, false, true, "{\"id\":\"789\"}");
        var requiredSize = new RemoteReferenceItem("CATEGORY_ATTRIBUTES", "size", "123", "Beden", "Beden", 0, true, true, "{\"id\":\"size\"}", true, false, false);
        var newlyAddedColor = new RemoteReferenceItem("CATEGORY_ATTRIBUTES", "color", "123", "Renk", "Renk", 0, true, true, "{\"id\":\"color\"}", false, false, false);
        var changedSizeContract = requiredSize with { AllowsMultipleValues = true, RawJson = "{\"id\":\"size\",\"multiple\":true}" };
        var valueRemote = new RemoteReferenceItem("ATTRIBUTE_VALUES", "mauve", "123/size", "Mürdüm", "Mürdüm", 0, false, true, "{\"id\":\"mauve\"}");
        var anotherValue = new RemoteReferenceItem("ATTRIBUTE_VALUES", "navy", "123/size", "Lacivert", "Lacivert", 0, false, true, "{\"id\":\"navy\"}");
        var referencePort = new ScriptedReferenceDataTestPort();
        referencePort.Enqueue(new("CATEGORIES", null), [categoryRemote, anotherCategory]);
        referencePort.Enqueue(new("BRANDS", null), [brandRemote, anotherBrand]);
        referencePort.Enqueue(new("CATEGORY_ATTRIBUTES", "123"), [requiredSize, newlyAddedColor]);
        referencePort.Enqueue(new("CATEGORY_ATTRIBUTES", "123"), [changedSizeContract, newlyAddedColor]);
        referencePort.Enqueue(new("ATTRIBUTE_VALUES", "123/size"), [valueRemote, anotherValue]);

        await using var db = fixture.CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        db.Tenants.Add(tenant);
        db.PlatformConnections.Add(connection);
        db.Categories.Add(category);
        db.Brands.Add(brand);
        db.AttributeDefinitions.Add(attribute);
        db.AttributeValues.Add(attributeValue);
        db.ReferenceSnapshots.AddRange(oldCategories, oldBrands, oldAttributes, oldValues);
        db.ReferenceItems.AddRange(
            NewReferenceItem(tenant.Id, connection.Id, oldCategories.Id, "CATEGORIES", "123", "Test kategori", isLeaf: true),
            NewReferenceItem(tenant.Id, connection.Id, oldBrands.Id, "BRANDS", "456", "Test marka"),
            NewReferenceItem(tenant.Id, connection.Id, oldAttributes.Id, "CATEGORY_ATTRIBUTES", "size", "Beden", isLeaf: true,
                isRequired: true, parentExternalId: "123", allowsCustomValue: false, allowsMultipleValues: false),
            NewReferenceItem(tenant.Id, connection.Id, oldValues.Id, "ATTRIBUTE_VALUES", "mauve", "Mürdüm", parentExternalId: "123/size"));
        db.CategoryMappings.Add(new CategoryMapping
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenant.Id,
            ConnectionId = connection.Id,
            SnapshotId = oldCategories.Id,
            LocalId = categoryId,
            ExternalId = "123",
            Status = "VERIFIED",
            VerifiedAt = fixture.Now.AddDays(-1),
            Version = 1
        });
        db.BrandMappings.Add(new BrandMapping
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenant.Id,
            ConnectionId = connection.Id,
            SnapshotId = oldBrands.Id,
            LocalId = brandId,
            ExternalId = "456",
            Status = "VERIFIED",
            VerifiedAt = fixture.Now.AddDays(-1),
            Version = 1
        });
        db.AttributeMappings.Add(new AttributeMapping
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenant.Id,
            ConnectionId = connection.Id,
            SnapshotId = oldAttributes.Id,
            LocalId = attributeId,
            ScopeExternalId = "123",
            ExternalId = "size",
            Status = "VERIFIED",
            VerifiedAt = fixture.Now.AddDays(-1),
            Version = 1
        });
        db.AttributeValueMappings.Add(new AttributeValueMapping
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenant.Id,
            ConnectionId = connection.Id,
            SnapshotId = oldValues.Id,
            LocalId = valueId,
            ScopeExternalId = "123/size",
            ExternalId = "mauve",
            Status = "VERIFIED",
            VerifiedAt = fixture.Now.AddDays(-1),
            Version = 1
        });
        await db.SaveChangesAsync();

        var processor = new MarketplaceJobProcessor(
            db, null!, referencePort, null!, null!, null!, null!, null!, null!, null!, null!,
            new ConfigurationBuilder().Build(), fixture.TimeProvider);
        var brandSync = await processor.ProcessAsync(
            tenant.Id, connection.Id, MarketplaceJobTypes.ReferenceSync, "{\"resourceType\":\"BRANDS\"}", "reference-revalidation-brands", CancellationToken.None);
        Assert.True(brandSync.Succeeded, brandSync.ErrorSummary);
        var brandMapping = await db.BrandMappings.SingleAsync(mapping => mapping.TenantId == tenant.Id && mapping.LocalId == brandId);
        var currentBrands = await db.ReferenceSnapshots.SingleAsync(snapshot => snapshot.TenantId == tenant.Id && snapshot.ResourceType == "BRANDS" && snapshot.IsCurrent);
        Assert.Equal(currentBrands.Id, brandMapping.SnapshotId);
        Assert.Equal(2, brandMapping.Version);

        var categorySync = await processor.ProcessAsync(
            tenant.Id, connection.Id, MarketplaceJobTypes.ReferenceSync, "{\"resourceType\":\"CATEGORIES\"}", "reference-revalidation-categories", CancellationToken.None);
        Assert.True(categorySync.Succeeded, categorySync.ErrorSummary);

        var categoryMapping = await db.CategoryMappings.SingleAsync(mapping => mapping.TenantId == tenant.Id && mapping.LocalId == categoryId);
        var currentCategories = await db.ReferenceSnapshots.SingleAsync(snapshot => snapshot.TenantId == tenant.Id && snapshot.ResourceType == "CATEGORIES" && snapshot.IsCurrent);
        Assert.Equal(currentCategories.Id, categoryMapping.SnapshotId);
        Assert.Equal(2, categoryMapping.Version);

        var firstAttributeSync = await processor.ProcessAsync(
            tenant.Id, connection.Id, MarketplaceJobTypes.ReferenceSync,
            "{\"resourceType\":\"CATEGORY_ATTRIBUTES\",\"parentExternalId\":\"123\"}",
            "reference-revalidation-attributes-unchanged", CancellationToken.None);
        Assert.True(firstAttributeSync.Succeeded, firstAttributeSync.ErrorSummary);

        var attributeMapping = await db.AttributeMappings.SingleAsync(mapping => mapping.TenantId == tenant.Id && mapping.LocalId == attributeId);
        var firstCurrentAttributes = await db.ReferenceSnapshots.SingleAsync(snapshot => snapshot.TenantId == tenant.Id && snapshot.ResourceType == "CATEGORY_ATTRIBUTES" && snapshot.IsCurrent);
        Assert.Equal(firstCurrentAttributes.Id, attributeMapping.SnapshotId);
        Assert.Equal(2, attributeMapping.Version);

        var changedAttributeSync = await processor.ProcessAsync(
            tenant.Id, connection.Id, MarketplaceJobTypes.ReferenceSync,
            "{\"resourceType\":\"CATEGORY_ATTRIBUTES\",\"parentExternalId\":\"123\"}",
            "reference-revalidation-attributes-changed", CancellationToken.None);
        Assert.True(changedAttributeSync.Succeeded, changedAttributeSync.ErrorSummary);

        var latestAttributes = await db.ReferenceSnapshots.SingleAsync(snapshot => snapshot.TenantId == tenant.Id && snapshot.ResourceType == "CATEGORY_ATTRIBUTES" && snapshot.IsCurrent);
        Assert.NotEqual(latestAttributes.Id, attributeMapping.SnapshotId);
        Assert.Equal(2, attributeMapping.Version);

        var valueSync = await processor.ProcessAsync(
            tenant.Id, connection.Id, MarketplaceJobTypes.ReferenceSync,
            "{\"resourceType\":\"ATTRIBUTE_VALUES\",\"parentExternalId\":\"123/size\"}",
            "reference-revalidation-values", CancellationToken.None);
        Assert.True(valueSync.Succeeded, valueSync.ErrorSummary);
        var valueMapping = await db.AttributeValueMappings.SingleAsync(mapping => mapping.TenantId == tenant.Id && mapping.LocalId == valueId);
        var currentValues = await db.ReferenceSnapshots.SingleAsync(snapshot => snapshot.TenantId == tenant.Id && snapshot.ResourceType == "ATTRIBUTE_VALUES" && snapshot.IsCurrent);
        Assert.Equal(currentValues.Id, valueMapping.SnapshotId);
        Assert.Equal(2, valueMapping.Version);
        var mappings = await new ReferenceDataService(db, fixture.TimeProvider)
            .ListMappingsAsync(tenant.Id, "categories", connection.Id, "*", CancellationToken.None);
        Assert.True(mappings.Succeeded, mappings.Error?.Message);
        Assert.Equal(1, Assert.Single(mappings.Value!).MissingRequiredAttributeCount);
    }

    [PostgreSqlFact]
    public async Task TrendyolLiveQuestionSync_ResumesTheNextPageAcrossRunsAfterHistoryImport()
    {
        var tenant = NewTenant("question-live-cursor");
        var connection = NewQuestionConnection(tenant);
        var syncState = new MarketplaceQuestionSyncState
        {
            TenantId = tenant.Id,
            ConnectionId = connection.Id,
            HistoryImported = true,
            HistoryStartedAt = fixture.Now.AddYears(-1),
            ProgressStatus = "IDLE",
            HistoryStatusIndex = 0,
            HistoryKindIndex = 0,
            HistoryPageIndex = 0,
            LastSuccessAt = fixture.Now.AddMinutes(-1),
            Version = 1
        };
        var questionPort = new PagedLiveQuestionTestPort(fixture.Now);

        await using (var seedDb = fixture.CreateContext())
        {
            seedDb.Tenants.Add(tenant);
            seedDb.PlatformConnections.Add(connection);
            seedDb.MarketplaceQuestionSyncStates.Add(syncState);
            await seedDb.SaveChangesAsync();
        }

        try
        {
            await using var db = fixture.CreateContext();
            var service = new MarketplaceQuestionService(db, questionPort, fixture.TimeProvider, NullLogger<MarketplaceQuestionService>.Instance);

            var firstRun = await service.SyncConnectionAsync(tenant.Id, connection.Id, "PRODUCT", CancellationToken.None);
            var stateAfterFirstRun = await db.MarketplaceQuestionSyncStates.SingleAsync(state => state.TenantId == tenant.Id && state.ConnectionId == connection.Id);
            Assert.Equal(300, firstRun.Added);
            Assert.Equal(1, stateAfterFirstRun.HistoryStatusIndex);
            Assert.Equal(1, stateAfterFirstRun.HistoryPageIndex);

            var secondRun = await service.SyncConnectionAsync(tenant.Id, connection.Id, "PRODUCT", CancellationToken.None);

            Assert.Equal(200, secondRun.Added);
            Assert.Equal(12, questionPort.ReadCalls.Count);
            Assert.Equal(Enumerable.Range(0, 5).Select(page => ("WAITING_FOR_ANSWER", page)), questionPort.ReadCalls.Take(5));
            Assert.Equal(("ANSWERED", 0), questionPort.ReadCalls[5]);
            Assert.Equal(Enumerable.Range(1, 4).Select(page => ("ANSWERED", page)), questionPort.ReadCalls.Skip(6).Take(4));
            Assert.Equal(("REPORTED", 0), questionPort.ReadCalls[10]);
            Assert.Equal(("REJECTED", 0), questionPort.ReadCalls[11]);

            var persistedQuestionCount = await db.MarketplaceQuestions.CountAsync(question => question.TenantId == tenant.Id && question.ConnectionId == connection.Id);
            Assert.Equal(500, persistedQuestionCount);
        }
        finally
        {
            await DeleteQuestionSyncTestTenantAsync(tenant.Id);
        }
    }

    [PostgreSqlFact]
    public async Task QuestionSync_KeepsTheVersionOfAQuestionThatDidNotChange()
    {
        var tenant = NewTenant("question-version-stable");
        var connection = NewQuestionConnection(tenant);
        var syncState = new MarketplaceQuestionSyncState
        {
            TenantId = tenant.Id,
            ConnectionId = connection.Id,
            HistoryImported = true,
            HistoryStartedAt = fixture.Now.AddYears(-1),
            ProgressStatus = "IDLE",
            LastSuccessAt = fixture.Now.AddMinutes(-1),
            Version = 1
        };
        // Marketplaces send milliseconds; the fixture clock has ticks PostgreSQL would round away.
        var askedAt = DateTimeOffset.FromUnixTimeMilliseconds(fixture.Now.AddMinutes(-5).ToUnixTimeMilliseconds());
        var remote = NewRemoteQuestion("WAITING_FOR_ANSWER", [new("Customer", "Ürün birebir aynı mı?", askedAt)], askedAt);
        var port = new WaitingQuestionListPort(remote);

        await using (var seedDb = fixture.CreateContext())
        {
            seedDb.Tenants.Add(tenant);
            seedDb.PlatformConnections.Add(connection);
            seedDb.MarketplaceQuestionSyncStates.Add(syncState);
            await seedDb.SaveChangesAsync();
        }

        try
        {
            async Task<long> SyncAndReadVersionAsync()
            {
                await using var db = fixture.CreateContext();
                var service = new MarketplaceQuestionService(db, port, fixture.TimeProvider, NullLogger<MarketplaceQuestionService>.Instance);
                var sync = await service.SyncConnectionAsync(tenant.Id, connection.Id, "PRODUCT", CancellationToken.None);
                Assert.Null(sync.Error);
                return await db.MarketplaceQuestions.AsNoTracking().Where(question => question.TenantId == tenant.Id).Select(question => question.Version).SingleAsync();
            }

            var first = await SyncAndReadVersionAsync();
            Assert.Equal(first, await SyncAndReadVersionAsync());
            Assert.Equal(first, await SyncAndReadVersionAsync());
        }
        finally
        {
            await DeleteQuestionSyncTestTenantAsync(tenant.Id);
        }
    }

    [PostgreSqlFact]
    public async Task TrendyolOrderStreamPoll_PreservesIncrementalDatesWithoutLegacyOrderDates()
    {
        var tenant = NewTenant("trendyol-order-stream-contract");
        var anchor = DateTimeOffset.Parse("2026-10-02T00:00:00Z");
        var connection = new PlatformConnection
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenant.Id,
            PublicId = Guid.CreateVersion7(),
            PlatformCode = "TRENDYOL",
            Environment = "STAGE",
            DisplayName = "Trendyol stream contract test",
            ExternalStoreId = "seller-contract-test",
            Status = "ACTIVE",
            ApiVersion = "V2",
            SettingsJson = JsonSerializer.Serialize(new { UserAgentIdentity = "ravencia-contract-test", ExternalWritesEnabled = false })
        };
        var dataProtection = new EphemeralDataProtectionProvider();
        var protector = dataProtection.CreateProtector("MarketplaceHub.PlatformCredential.v1");
        var credential = new PlatformCredential
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenant.Id,
            ConnectionId = connection.Id,
            CredentialType = "TRENDYOL_API_KEY",
            ProtectedPayload = protector.Protect(JsonSerializer.Serialize(new { ApiKey = "test-key", ApiSecret = "test-secret" })),
            MaskedHint = "test",
            CreatedAt = anchor,
            Version = 1
        };
        var requests = new RecordingHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"content\":[],\"nextCursor\":null,\"hasMore\":false}", Encoding.UTF8, "application/json")
        });

        await using (var seedDb = fixture.CreateContext())
        {
            seedDb.Tenants.Add(tenant);
            seedDb.PlatformConnections.Add(connection);
            seedDb.PlatformCredentials.Add(credential);
            await seedDb.SaveChangesAsync();
        }

        try
        {
            await using var db = fixture.CreateContext();
            var authentication = new TrendyolAuthenticationHandler(
                db,
                dataProtection,
                Options.Create(new TrendyolOptions()),
                NullLogger<TrendyolAuthenticationHandler>.Instance);
            var client = new TrendyolHttpClient(
                new RecordingHttpClientFactory(requests),
                authentication,
                new ConfigurationBuilder().Build(),
                Options.Create(new TrendyolOptions()),
                new FixedTimeProvider(anchor),
                NullLogger<TrendyolHttpClient>.Instance);
            var modifiedAfter = anchor.AddMinutes(-10);
            var modifiedBefore = anchor;
            var context = new AdapterContext(tenant.Id, connection.Id, "trendyol-order-stream-contract", "contract-test", anchor.AddMinutes(1));

            var result = await client.PollAsync(context, new OrderPollWindow(modifiedAfter, modifiedBefore), new(null, 200), CancellationToken.None);

            Assert.True(result.IsSuccess, result.Error?.SafeMessage);
            var requestUri = Assert.Single(requests.Requests);
            Assert.Equal("/integration/order/sellers/seller-contract-test/orders/stream", requestUri.AbsolutePath);
            Assert.Equal(modifiedAfter.ToUnixTimeMilliseconds(), QueryLong(requestUri, "lastModifiedStartDate"));
            Assert.Equal(modifiedBefore.ToUnixTimeMilliseconds(), QueryLong(requestUri, "lastModifiedEndDate"));
            Assert.Null(QueryValue(requestUri, "startDate"));
            Assert.Null(QueryValue(requestUri, "endDate"));
            Assert.Null(QueryValue(requestUri, "status"));
        }
        finally
        {
            await using var cleanupDb = fixture.CreateContext();
            await cleanupDb.PlatformCredentials.Where(row => row.TenantId == tenant.Id).ExecuteDeleteAsync();
            await DeleteInvoiceTestTenantAsync(tenant.Id);
        }
    }

    [PostgreSqlFact]
    public async Task TrendyolShipmentPackageReadback_UsesV2AndStreamWithoutLegacyOrdersEndpoint()
    {
        var tenant = NewTenant("trendyol-package-readback");
        var anchor = DateTimeOffset.Parse("2026-10-02T00:00:00Z");
        var occurredAt = anchor.AddDays(-1);
        var connection = new PlatformConnection
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenant.Id,
            PublicId = Guid.CreateVersion7(),
            PlatformCode = "TRENDYOL",
            Environment = "STAGE",
            DisplayName = "Trendyol package readback contract test",
            ExternalStoreId = "seller-contract-test",
            Status = "ACTIVE",
            ApiVersion = "V2",
            SettingsJson = JsonSerializer.Serialize(new { UserAgentIdentity = "ravencia-contract-test", ExternalWritesEnabled = false })
        };
        var dataProtection = new EphemeralDataProtectionProvider();
        var protector = dataProtection.CreateProtector("MarketplaceHub.PlatformCredential.v1");
        var credential = new PlatformCredential
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenant.Id,
            ConnectionId = connection.Id,
            CredentialType = "TRENDYOL_API_KEY",
            ProtectedPayload = protector.Protect(JsonSerializer.Serialize(new { ApiKey = "test-key", ApiSecret = "test-secret" })),
            MaskedHint = "test",
            CreatedAt = anchor,
            Version = 1
        };
        var requests = new RecordingHttpMessageHandler(request =>
        {
            var content = request.RequestUri!.AbsolutePath.EndsWith("/v2/orders", StringComparison.Ordinal)
                ? """
                  {"content":[{"shipmentPackageId":4052072376,"orderNumber":"11476852228","status":"Delivered","lastModifiedDate":1790899200000,"lines":[]}],"page":0,"totalPages":1}
                  """
                : """
                  {"content":[{"shipmentPackageId":4052072376,"orderNumber":"11476852228","status":"Delivered","lastModifiedDate":1790899200000,"cargoTrackingNumber":62755229958101,"cargoProviderName":"hepsiJET","invoiceStatus":"Invoiced","invoiceNumber":"INV-42","invoiceUrl":"https://example.invalid/invoice/42","lines":[]}],"nextCursor":null,"hasMore":false}
                  """;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(content, Encoding.UTF8, "application/json")
            };
        });

        await using (var seedDb = fixture.CreateContext())
        {
            seedDb.Tenants.Add(tenant);
            seedDb.PlatformConnections.Add(connection);
            seedDb.PlatformCredentials.Add(credential);
            await seedDb.SaveChangesAsync();
        }

        try
        {
            await using var db = fixture.CreateContext();
            var authentication = new TrendyolAuthenticationHandler(
                db,
                dataProtection,
                Options.Create(new TrendyolOptions()),
                NullLogger<TrendyolAuthenticationHandler>.Instance);
            var client = new TrendyolHttpClient(
                new RecordingHttpClientFactory(requests),
                authentication,
                new ConfigurationBuilder().Build(),
                Options.Create(new TrendyolOptions()),
                new FixedTimeProvider(anchor),
                NullLogger<TrendyolHttpClient>.Instance);
            var context = new AdapterContext(tenant.Id, connection.Id, "trendyol-package-readback-contract", "contract-test", anchor.AddMinutes(1));

            var result = await client.GetShipmentPackageAsync(context, "4052072376", occurredAt, CancellationToken.None);

            Assert.True(result.IsSuccess, result.Error?.SafeMessage);
            Assert.Equal("hepsiJET", result.Value!.Package.CargoProviderExternalId);
            Assert.Equal("62755229958101", result.Value.Package.CargoTrackingNumber);
            Assert.Equal("Invoiced", result.Value.Package.Invoice?.RawStatus);
            Assert.Equal("INV-42", result.Value.Package.Invoice?.InvoiceNumber);
            Assert.Equal(2, requests.Requests.Count);
            Assert.Equal("/integration/order/sellers/seller-contract-test/v2/orders", requests.Requests[0].AbsolutePath);
            Assert.Equal("4052072376", QueryValue(requests.Requests[0], "shipmentPackageIds"));
            Assert.Equal(anchor.AddDays(-14).ToUnixTimeMilliseconds().ToString(), QueryValue(requests.Requests[0], "startDate"));
            Assert.Equal(anchor.ToUnixTimeMilliseconds().ToString(), QueryValue(requests.Requests[0], "endDate"));
            Assert.Equal("/integration/order/sellers/seller-contract-test/orders/stream", requests.Requests[1].AbsolutePath);
            Assert.DoesNotContain(requests.Requests, uri => uri.AbsolutePath == "/integration/order/sellers/seller-contract-test/orders");
        }
        finally
        {
            await using var cleanupDb = fixture.CreateContext();
            await cleanupDb.PlatformCredentials.Where(row => row.TenantId == tenant.Id).ExecuteDeleteAsync();
            await DeleteInvoiceTestTenantAsync(tenant.Id);
        }
    }

    [PostgreSqlFact]
    public async Task TrendyolTargetedReads_SearchInternationalStorefrontAndUseTwoWeekWindow()
    {
        var tenant = NewTenant("trendyol-intl-package");
        var anchor = DateTimeOffset.Parse("2026-10-02T00:00:00Z");
        var connection = new PlatformConnection
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenant.Id,
            PublicId = Guid.CreateVersion7(),
            PlatformCode = "TRENDYOL",
            Environment = "STAGE",
            DisplayName = "Trendyol international package readback contract test",
            ExternalStoreId = "seller-contract-test",
            Status = "ACTIVE",
            ApiVersion = "V2",
            SettingsJson = JsonSerializer.Serialize(new { UserAgentIdentity = "ravencia-contract-test", ExternalWritesEnabled = false })
        };
        var dataProtection = new EphemeralDataProtectionProvider();
        var protector = dataProtection.CreateProtector("MarketplaceHub.PlatformCredential.v1");
        var credential = new PlatformCredential
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenant.Id,
            ConnectionId = connection.Id,
            CredentialType = "TRENDYOL_API_KEY",
            ProtectedPayload = protector.Protect(JsonSerializer.Serialize(new { ApiKey = "test-key", ApiSecret = "test-secret" })),
            MaskedHint = "test",
            CreatedAt = anchor,
            Version = 1
        };
        var storeFrontCodes = new List<string>();
        var requests = new RecordingHttpMessageHandler(request =>
        {
            var storeFrontCode = request.Headers.GetValues("storeFrontCode").Single();
            storeFrontCodes.Add(storeFrontCode);
            var content = storeFrontCode == "AE"
                ? """
                  {"content":[{"shipmentPackageId":419428742,"orderNumber":"116483573775","status":"Shipped","lastModifiedDate":1790899200000,"lines":[]}],"page":0,"totalPages":1}
                  """
                : """
                  {"content":[],"page":0,"totalPages":0}
                  """;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(content, Encoding.UTF8, "application/json")
            };
        });

        await using (var seedDb = fixture.CreateContext())
        {
            seedDb.Tenants.Add(tenant);
            seedDb.PlatformConnections.Add(connection);
            seedDb.PlatformCredentials.Add(credential);
            await seedDb.SaveChangesAsync();
        }

        try
        {
            await using var db = fixture.CreateContext();
            var authentication = new TrendyolAuthenticationHandler(
                db,
                dataProtection,
                Options.Create(new TrendyolOptions()),
                NullLogger<TrendyolAuthenticationHandler>.Instance);
            var client = new TrendyolHttpClient(
                new RecordingHttpClientFactory(requests),
                authentication,
                new ConfigurationBuilder().Build(),
                Options.Create(new TrendyolOptions()),
                new FixedTimeProvider(anchor),
                NullLogger<TrendyolHttpClient>.Instance);
            var context = new AdapterContext(tenant.Id, connection.Id, "trendyol-international-package-readback-contract", "contract-test", anchor.AddMinutes(1));

            var result = await client.GetShipmentPackageAsync(context, "419428742", CancellationToken.None);

            Assert.True(result.IsSuccess, result.Error?.SafeMessage);
            Assert.Equal("116483573775", result.Value!.OrderSnapshot!.ExternalOrderId);
            Assert.Equal("419428742", result.Value.Package.ExternalPackageId);
            Assert.Equal(["TR", "AE"], storeFrontCodes);
            Assert.Equal(2, requests.Requests.Count);
            Assert.All(requests.Requests, uri => Assert.Equal("/integration/order/sellers/seller-contract-test/v2/orders", uri.AbsolutePath));
            Assert.All(requests.Requests, uri => Assert.Equal("419428742", QueryValue(uri, "shipmentPackageIds")));
            Assert.All(requests.Requests, uri => Assert.Equal(anchor.AddDays(-14).ToUnixTimeMilliseconds().ToString(), QueryValue(uri, "startDate")));
            Assert.All(requests.Requests, uri => Assert.Equal(anchor.ToUnixTimeMilliseconds().ToString(), QueryValue(uri, "endDate")));

            var orderLookupStart = requests.Requests.Count;
            var orderResult = await client.GetAsync(context, "116483573775", CancellationToken.None);

            Assert.True(orderResult.IsSuccess, orderResult.Error?.SafeMessage);
            Assert.Equal("116483573775", orderResult.Value!.ExternalOrderId);
            var orderLookupRequests = requests.Requests.Skip(orderLookupStart).ToArray();
            // The quoted order number is tried only after a 400, so a miss costs one request per storefront.
            Assert.Equal(["TR", "AE"], storeFrontCodes.Skip(2));
            Assert.Equal("AE", orderResult.Value.StoreFrontCode);
            Assert.All(orderLookupRequests, uri => Assert.Equal("116483573775", QueryValue(uri, "orderNumber")));
            Assert.All(orderLookupRequests, uri => Assert.Equal(anchor.AddDays(-14).ToUnixTimeMilliseconds().ToString(), QueryValue(uri, "startDate")));
            Assert.All(orderLookupRequests, uri => Assert.Equal(anchor.ToUnixTimeMilliseconds().ToString(), QueryValue(uri, "endDate")));

            // A known order is asked for in its own storefront only.
            storeFrontCodes.Clear();
            var knownOrder = new OrderLookupHint(anchor.AddDays(-3), anchor.AddDays(-1), "AE");
            Assert.True((await client.GetAsync(context, "116483573775", knownOrder, CancellationToken.None)).IsSuccess);
            Assert.True((await client.GetShipmentPackageAsync(context, "419428742", new PackageLookup(anchor.AddDays(-1), knownOrder, InvoiceOnly: true), CancellationToken.None)).IsSuccess);
            Assert.Equal(["AE", "AE"], storeFrontCodes);

            // An order older than Order V2's month is read from the stream around its last change.
            storeFrontCodes.Clear();
            var streamLookupStart = requests.Requests.Count;
            var oldOrder = new OrderLookupHint(anchor.AddDays(-40), anchor.AddDays(-35), "AE");
            var oldResult = await client.GetAsync(context, "116483573775", oldOrder, CancellationToken.None);
            Assert.True(oldResult.IsSuccess, oldResult.Error?.SafeMessage);
            var streamRequest = Assert.Single(requests.Requests.Skip(streamLookupStart));
            Assert.Equal("/integration/order/sellers/seller-contract-test/orders/stream", streamRequest.AbsolutePath);
            Assert.Equal(anchor.AddDays(-41).ToUnixTimeMilliseconds().ToString(), QueryValue(streamRequest, "lastModifiedStartDate"));
            Assert.Equal(anchor.AddDays(-29).ToUnixTimeMilliseconds().ToString(), QueryValue(streamRequest, "lastModifiedEndDate"));
            Assert.Equal(["AE"], storeFrontCodes);
        }
        finally
        {
            await using var cleanupDb = fixture.CreateContext();
            await cleanupDb.PlatformCredentials.Where(row => row.TenantId == tenant.Id).ExecuteDeleteAsync();
            await DeleteInvoiceTestTenantAsync(tenant.Id);
        }
    }

    private static Order NewInvoiceTestOrder(Guid tenantId, Guid connectionId, Guid id, string externalId, bool isReturnClaim, DateTimeOffset now) => new()
    {
        Id = id,
        TenantId = tenantId,
        ConnectionId = connectionId,
        ExternalOrderId = externalId,
        OrderNumber = externalId,
        Currency = "TRY",
        GrossAmount = 100m,
        NetAmount = 100m,
        OrderedAt = now.AddDays(-1),
        LastRemoteModifiedAt = now.AddDays(-1),
        CustomerSnapshotJson = isReturnClaim ? "{\"_ravenciaReadModelSource\":\"RETURN_CLAIM\"}" : "{\"customerFirstName\":\"Test\"}",
        ShipmentAddressSnapshotJson = "{}",
        InvoiceAddressSnapshotJson = "{}",
        DerivedStatus = "DELIVERED",
        CreatedAt = now,
        UpdatedAt = now,
        Version = 1
    };

    private static ShipmentPackage NewInvoiceTestPackage(Guid tenantId, Guid connectionId, Guid orderId, Guid id, string externalId, DateTimeOffset now) => new()
    {
        Id = id,
        TenantId = tenantId,
        ConnectionId = connectionId,
        OrderId = orderId,
        ExternalPackageId = externalId,
        GrossAmount = 100m,
        NetAmount = 100m,
        Status = ShipmentPackageStatus.Delivered,
        RawStatus = "Delivered",
        StatusOccurredAt = now.AddDays(-1),
        MarketplaceInvoiceStatus = MarketplaceInvoiceStatus.Unknown,
        CreatedAt = now,
        UpdatedAt = now,
        Version = 1
    };

    private static ReferenceSnapshot NewReferenceSnapshot(Guid tenantId, Guid connectionId, string resourceType, string scopeExternalId, DateTimeOffset fetchedAt, string sourceVersion, bool isCurrent = true) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = tenantId,
        ConnectionId = connectionId,
        ResourceType = resourceType,
        ScopeExternalId = scopeExternalId,
        SourceVersion = sourceVersion,
        ContentHash = $"hash-{sourceVersion}",
        FetchedAt = fetchedAt,
        IsCurrent = isCurrent,
        ItemCount = 1
    };

    private static ReferenceItem NewReferenceItem(
        Guid tenantId,
        Guid connectionId,
        Guid snapshotId,
        string resourceType,
        string externalId,
        string name,
        bool isLeaf = false,
        bool? isRequired = null,
        string? parentExternalId = null,
        bool? allowsCustomValue = null,
        bool? allowsMultipleValues = null) => new()
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            ConnectionId = connectionId,
            SnapshotId = snapshotId,
            ResourceType = resourceType,
            ExternalId = externalId,
            ParentExternalId = parentExternalId,
            Name = name,
            NormalizedName = name.ToUpperInvariant(),
            Path = name,
            Depth = 0,
            IsLeaf = isLeaf,
            IsActive = true,
            IsRequired = isRequired,
            AllowsCustomValue = allowsCustomValue,
            AllowsMultipleValues = allowsMultipleValues,
            PayloadHash = $"payload-{resourceType}-{externalId}"
        };

    private async Task DeleteInvoiceTestTenantAsync(Guid tenantId)
    {
        await using var db = fixture.CreateContext();
        await db.IntegrationOutboxEvents.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.OperationalIssues.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.SyncCursors.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.PackageLineAllocations.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.OrderStatusHistory.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.OrderLines.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.ShipmentPackages.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.Orders.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.PlatformConnections.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.Tenants.Where(row => row.Id == tenantId).ExecuteDeleteAsync();
    }

    private async Task DeleteCatalogOptionRepairTestTenantAsync(Guid tenantId)
    {
        await using var db = fixture.CreateContext();
        await db.ProductImportStagingRecords.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.ProductImportSessions.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.VariantOptionValues.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.ProductOptionValues.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.ProductOptions.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.OperationalIssues.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.MarketplaceVariantLinks.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.MarketplaceProductLinks.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.ProductMedia.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.ChannelOffers.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.InventoryItems.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.ProductVariants.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.Products.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.SyncCursors.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.IntegrationOutboxEvents.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.PlatformConnections.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.Tenants.Where(row => row.Id == tenantId).ExecuteDeleteAsync();
    }

    private async Task DeleteReturnLifecycleTestTenantAsync(Guid tenantId)
    {
        await using var db = fixture.CreateContext();
        await db.IntegrationOutboxEvents.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.OperationalIssues.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.SyncCursors.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.ReturnLines.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.ReturnClaims.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.Orders.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.PlatformConnections.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.Tenants.Where(row => row.Id == tenantId).ExecuteDeleteAsync();
    }

    private async Task DeleteQuestionSyncTestTenantAsync(Guid tenantId)
    {
        await using var db = fixture.CreateContext();
        await db.IntegrationOutboxEvents.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.MarketplaceQuestions.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.MarketplaceQuestionSyncStates.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.SyncCursors.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.PlatformConnections.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.Tenants.Where(row => row.Id == tenantId).ExecuteDeleteAsync();
    }

    private static ReturnClaim NewReturnLifecycleClaim(Guid tenantId, Guid connectionId, Guid orderId, Guid id, string externalClaimId, DateTimeOffset now, bool hasCargo) => new()
    {
        Id = id,
        TenantId = tenantId,
        ConnectionId = connectionId,
        OrderId = orderId,
        ExternalClaimId = externalClaimId,
        Status = ReturnClaimStatus.Requested,
        RawStatus = "NEWREQUEST",
        CargoProviderName = hasCargo ? "PTT" : null,
        CargoTrackingNumber = hasCargo ? $"tracking-{externalClaimId}" : null,
        LastRemoteModifiedAt = now.AddDays(-1),
        CreatedAt = now.AddDays(-1),
        UpdatedAt = now.AddDays(-1),
        Version = 1
    };

    private static PlatformConnection NewQuestionConnection(Tenant tenant) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = tenant.Id,
        PublicId = Guid.CreateVersion7(),
        PlatformCode = "TRENDYOL",
        Environment = "STAGE",
        DisplayName = "Soru cevap test mağazası",
        ExternalStoreId = "seller-17",
        Status = "ACTIVE",
        ApiVersion = "V1"
    };

    private static MarketplaceQuestion NewUncertainQuestion(Guid tenantId, Guid connectionId, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = tenantId,
        ConnectionId = connectionId,
        ExternalQuestionId = "external-question",
        Kind = "PRODUCT",
        Status = "ANSWER_SUBMITTED",
        QuestionText = "Ürün sorusu",
        PendingAnswerText = "Önceki cevap",
        AnswerSubmissionStatus = "UNKNOWN",
        AnswerSubmissionKey = Guid.NewGuid().ToString("N"),
        CreatedAt = now.AddHours(-1),
        LastRemoteModifiedAt = now.AddMinutes(-1),
        LastSyncedAt = now.AddMinutes(-10),
        Version = 4
    };

    private static RemoteMarketplaceQuestion NewRemoteQuestion(string status, IReadOnlyList<RemoteQuestionConversation> conversations, DateTimeOffset modifiedAt) =>
        new("external-question", "PRODUCT", status, "Ürün sorusu", null, null, null, null, null, "Müşteri", null, modifiedAt.AddHours(-1), null, modifiedAt, conversations);

    private sealed class ScriptedReferenceDataTestPort : IReferenceDataPort
    {
        private readonly Dictionary<(string ResourceType, string ParentExternalId), Queue<IReadOnlyList<RemoteReferenceItem>>> responses = [];

        public void Enqueue(ReferenceResource resource, IReadOnlyList<RemoteReferenceItem> items)
        {
            var key = (resource.ResourceType, resource.ParentExternalId ?? "");
            if (!responses.TryGetValue(key, out var queue)) responses[key] = queue = new Queue<IReadOnlyList<RemoteReferenceItem>>();
            queue.Enqueue(items);
        }

        public Task<AdapterResult<AdapterPageResult<RemoteReferenceItem>>> ReadAsync(AdapterContext context, ReferenceResource resource, AdapterPageRequest page, CancellationToken cancellationToken)
        {
            var key = (resource.ResourceType, resource.ParentExternalId ?? "");
            if (!responses.TryGetValue(key, out var queue) || queue.Count == 0)
                throw new InvalidOperationException($"Unexpected reference read: {resource.ResourceType}/{resource.ParentExternalId}.");
            var items = queue.Dequeue();
            return Task.FromResult(AdapterResult<AdapterPageResult<RemoteReferenceItem>>.Success(new(items, null, false, items.Count)));
        }
    }

    private sealed class ReadOnlyCatalogProductTestPort(RemoteCatalogProduct snapshot) : IProductPort
    {
        public int ReadCalls { get; private set; }
        public int WriteAttempts { get; private set; }

        public Task<AdapterResult<AdapterPageResult<RemoteCatalogProduct>>> ListCatalogAsync(AdapterContext context, AdapterPageRequest page, ProductReadFilter filter, CancellationToken cancellationToken)
        {
            ReadCalls++;
            return Task.FromResult(AdapterResult<AdapterPageResult<RemoteCatalogProduct>>.Success(new([snapshot], null, false, 1)));
        }

        public Task<AdapterResult<AdapterPageResult<RemoteProduct>>> ListAsync(AdapterContext context, AdapterPageRequest page, ProductReadFilter filter, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<AdapterResult<RemoteOperationRef>> CreateAsync(AdapterContext context, ProductPublication publication, CancellationToken cancellationToken) =>
            RejectWrite<RemoteOperationRef>();

        public Task<AdapterResult<RemoteOperationRef>> UpdateUnapprovedAsync(AdapterContext context, ProductUpdatePublication publication, CancellationToken cancellationToken) =>
            RejectWrite<RemoteOperationRef>();

        public Task<AdapterResult<RemoteOperationRef>> UpdateApprovedContentAsync(AdapterContext context, ProductUpdatePublication publication, CancellationToken cancellationToken) =>
            RejectWrite<RemoteOperationRef>();

        public Task<AdapterResult<RemoteOperationRef>> UpdateApprovedVariantsAsync(AdapterContext context, ProductUpdatePublication publication, CancellationToken cancellationToken) =>
            RejectWrite<RemoteOperationRef>();

        public Task<AdapterResult<RemoteOperationRef>> UpdateApprovedDeliveryAsync(AdapterContext context, ProductUpdatePublication publication, CancellationToken cancellationToken) =>
            RejectWrite<RemoteOperationRef>();

        public Task<AdapterResult<RemoteOperationStatus>> GetOperationAsync(AdapterContext context, string externalOperationId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<AdapterResult<RemotePublicationStatus>> GetPublicationStatusAsync(AdapterContext context, string barcode, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<AdapterResult<RemoteOperationRef>> ArchiveAsync(AdapterContext context, string payloadJson, CancellationToken cancellationToken) =>
            RejectWrite<RemoteOperationRef>();

        private Task<AdapterResult<T>> RejectWrite<T>()
        {
            WriteAttempts++;
            throw new InvalidOperationException("The option-only import test must never write to the marketplace.");
        }
    }

    private sealed class FixedQuestionPort(RemoteMarketplaceQuestion remote) : IQuestionPort
    {
        public int AnswerCalls { get; private set; }
        public int DetailCalls { get; private set; }

        public Task<AdapterResult<MarketplaceQuestionPage>> ListQuestionsAsync(AdapterContext context, QuestionPollRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(AdapterResult<MarketplaceQuestionPage>.Success(new([], request.Page, request.Page, 0)));

        public Task<AdapterResult<RemoteMarketplaceQuestion>> GetQuestionAsync(AdapterContext context, string questionId, string kind, CancellationToken cancellationToken)
        {
            DetailCalls++;
            return Task.FromResult(AdapterResult<RemoteMarketplaceQuestion>.Success(remote));
        }

        public Task<AdapterResult<RemoteQuestionAnswerResult>> AnswerQuestionAsync(AdapterContext context, string questionId, string kind, string answer, CancellationToken cancellationToken)
        {
            AnswerCalls++;
            return Task.FromResult(AdapterResult<RemoteQuestionAnswerResult>.Success(new(true, false, null)));
        }
    }

    private sealed class SteppingTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset now = start;
        public void Advance(TimeSpan delta) => now = now.Add(delta);
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class ReadOnlyOrderTestPort(RemotePackage package, RemoteOrder? orderSnapshot = null, string externalOrderId = "regular-order", RemoteOrder? directOrderResult = null, Action? onPackageRead = null) : IOrderPort
    {
        public int OrderReadCalls { get; private set; }
        public List<string> PackageReadCalls { get; } = [];
        // Package IDs the batch read returns; null keeps batch reads unsupported.
        public IReadOnlySet<string>? BatchReturns { get; init; }
        public List<IReadOnlyList<string>> BatchReadCalls { get; } = [];

        public Task<AdapterResult<IReadOnlyList<RemoteOrder>>> GetShipmentPackagesAsync(AdapterContext context, IReadOnlyList<string> externalPackageIds, OrderLookupHint? hint, CancellationToken cancellationToken)
        {
            if (BatchReturns is null)
                return Task.FromResult(AdapterResult<IReadOnlyList<RemoteOrder>>.Failure(new(AdapterErrorClass.NotSupported, "ORDER_PACKAGE_BATCH_READ_UNSUPPORTED", "unsupported", null, null, null)));
            BatchReadCalls.Add(externalPackageIds);
            IReadOnlyList<RemoteOrder> found = [.. externalPackageIds.Where(BatchReturns.Contains).Select(id => new RemoteOrder(
                id, id, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, "TRY", 0, 0, 0, "{}", "{}", "{}", [], [package with { ExternalPackageId = id }], "{}"))];
            return Task.FromResult(AdapterResult<IReadOnlyList<RemoteOrder>>.Success(found));
        }

        public Task<AdapterResult<AdapterPageResult<RemoteOrder>>> PollAsync(AdapterContext context, OrderPollWindow window, AdapterPageRequest page, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Invoice reconciliation test must not poll order pages.");

        public Task<AdapterResult<RemoteOrder>> GetAsync(AdapterContext context, string externalOrderId, CancellationToken cancellationToken)
        {
            OrderReadCalls++;
            if (directOrderResult is not null)
                return Task.FromResult(AdapterResult<RemoteOrder>.Success(directOrderResult));
            throw new NotSupportedException("Return hydration must defer while the order lane is locked.");
        }

        public Task<AdapterResult<RemoteOrderPackage>> GetShipmentPackageAsync(AdapterContext context, string externalPackageId, DateTimeOffset? packageStatusOccurredAt, CancellationToken cancellationToken)
        {
            PackageReadCalls.Add(externalPackageId);
            onPackageRead?.Invoke();
            return Task.FromResult(AdapterResult<RemoteOrderPackage>.Success(new(externalOrderId, package, orderSnapshot)));
        }

        public Task<AdapterResult<PackageActionResult>> ExecutePackageActionAsync(AdapterContext context, PackageActionCommand command, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<AdapterResult<IReadOnlyList<RemoteCargoCompany>>> GetChangeableCargoCompaniesAsync(AdapterContext context, string externalPackageId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<AdapterResult<IReadOnlyList<RemotePackageableLine>>> GetPackageableLineItemsAsync(AdapterContext context, string externalLineItemId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<AdapterResult<CreateOrderPackageResult>> CreateOrderPackageAsync(AdapterContext context, CreateOrderPackageCommand command, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<AdapterResult<bool>> CreateCommonLabelAsync(AdapterContext context, CommonLabelRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<AdapterResult<CommonLabelDocument>> GetCommonLabelAsync(AdapterContext context, string cargoTrackingNumber, CancellationToken cancellationToken, string format = "ZPL") =>
            throw new NotSupportedException();

        public Task<AdapterResult<StageTestOrderResult>> CreateStageTestOrderAsync(AdapterContext context, string barcode, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class StorefrontRecordingOrderPort : IOrderPort
    {
        public List<string?> PolledStorefronts { get; } = [];
        public List<OrderPollWindow> PolledWindows { get; } = [];
        public Action? OnPoll { get; set; }
        public List<(IReadOnlyList<string> PackageIds, OrderLookupHint? Hint)> PackageBatches { get; } = [];
        public List<(string OrderId, OrderLookupHint? Hint)> OrderReads { get; } = [];

        public Task<AdapterResult<IReadOnlyList<RemoteOrder>>> GetShipmentPackagesAsync(AdapterContext context, IReadOnlyList<string> externalPackageIds, OrderLookupHint? hint, CancellationToken cancellationToken)
        {
            PackageBatches.Add((externalPackageIds, hint));
            return Task.FromResult(AdapterResult<IReadOnlyList<RemoteOrder>>.Success([]));
        }

        public Task<AdapterResult<RemoteOrder>> GetAsync(AdapterContext context, string externalOrderId, OrderLookupHint? hint, CancellationToken cancellationToken)
        {
            OrderReads.Add((externalOrderId, hint));
            return Task.FromResult(AdapterResult<RemoteOrder>.Failure(new(AdapterErrorClass.NotFound, "REMOTE_ORDER_NOT_FOUND", "Platform siparişi bulunamadı.", 404, null, null)));
        }

        public Task<AdapterResult<AdapterPageResult<RemoteOrder>>> PollAsync(AdapterContext context, OrderPollWindow window, AdapterPageRequest page, CancellationToken cancellationToken)
        {
            PolledStorefronts.Add(window.StoreFrontCode);
            PolledWindows.Add(window);
            OnPoll?.Invoke();
            return Task.FromResult(AdapterResult<AdapterPageResult<RemoteOrder>>.Success(new([], null, false)));
        }

        public Task<AdapterResult<RemoteOrder>> GetAsync(AdapterContext context, string externalOrderId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<AdapterResult<PackageActionResult>> ExecutePackageActionAsync(AdapterContext context, PackageActionCommand command, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<AdapterResult<IReadOnlyList<RemoteCargoCompany>>> GetChangeableCargoCompaniesAsync(AdapterContext context, string externalPackageId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<AdapterResult<IReadOnlyList<RemotePackageableLine>>> GetPackageableLineItemsAsync(AdapterContext context, string externalLineItemId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<AdapterResult<CreateOrderPackageResult>> CreateOrderPackageAsync(AdapterContext context, CreateOrderPackageCommand command, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<AdapterResult<bool>> CreateCommonLabelAsync(AdapterContext context, CommonLabelRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<AdapterResult<CommonLabelDocument>> GetCommonLabelAsync(AdapterContext context, string cargoTrackingNumber, CancellationToken cancellationToken, string format = "ZPL") =>
            throw new NotSupportedException();

        public Task<AdapterResult<StageTestOrderResult>> CreateStageTestOrderAsync(AdapterContext context, string barcode, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class ReadOnlyReturnTestPort(DateTimeOffset lastModifiedAt, string rawStatus = "NewRequest", string externalOrderId = "return-lifecycle-order", string? listedClaimId = null) : IReturnPort
    {
        public List<string> ReadCalls { get; } = [];
        public List<string> ListedStatuses { get; } = [];

        public Task<AdapterResult<AdapterPageResult<RemoteReturnClaim>>> PollAsync(AdapterContext context, ReturnPollWindow window, AdapterPageRequest page, CancellationToken cancellationToken)
        {
            if (listedClaimId is null) throw new NotSupportedException("Return lifecycle test must use individual claim reads.");
            ListedStatuses.Add(window.Status ?? "");
            IReadOnlyList<RemoteReturnClaim> items = string.Equals(window.Status, rawStatus, StringComparison.Ordinal)
                ? [new(listedClaimId, externalOrderId, rawStatus, null, null, null, lastModifiedAt, [], "{}")]
                : [];
            return Task.FromResult(AdapterResult<AdapterPageResult<RemoteReturnClaim>>.Success(new(items, null, false)));
        }

        public Task<AdapterResult<RemoteReturnClaim>> GetAsync(AdapterContext context, string externalReturnId, CancellationToken cancellationToken)
        {
            ReadCalls.Add(externalReturnId);
            return Task.FromResult(AdapterResult<RemoteReturnClaim>.Success(new(
                externalReturnId,
                externalOrderId,
                rawStatus,
                null,
                null,
                null,
                lastModifiedAt,
                [],
                "{}")));
        }

        public Task<AdapterResult<IReadOnlyList<ReturnIssueReason>>> IssueReasonsAsync(AdapterContext context, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<AdapterResult<ReturnActionResult>> ExecuteAsync(AdapterContext context, ReturnActionCommand command, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class WaitingQuestionListPort(RemoteMarketplaceQuestion remote) : IQuestionPort
    {
        public Task<AdapterResult<MarketplaceQuestionPage>> ListQuestionsAsync(AdapterContext context, QuestionPollRequest request, CancellationToken cancellationToken)
        {
            IReadOnlyList<RemoteMarketplaceQuestion> items = request.Status == remote.Status && request.Page == 0 ? [remote] : [];
            return Task.FromResult(AdapterResult<MarketplaceQuestionPage>.Success(new(items, request.Page, items.Count == 0 ? 0 : 1, items.Count)));
        }

        public Task<AdapterResult<RemoteMarketplaceQuestion>> GetQuestionAsync(AdapterContext context, string questionId, string kind, CancellationToken cancellationToken) =>
            Task.FromResult(AdapterResult<RemoteMarketplaceQuestion>.Success(remote));

        public Task<AdapterResult<RemoteQuestionAnswerResult>> AnswerQuestionAsync(AdapterContext context, string questionId, string kind, string answer, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class PagedLiveQuestionTestPort(DateTimeOffset lastModifiedAt) : IQuestionPort
    {
        public List<(string Status, int Page)> ReadCalls { get; } = [];

        public Task<AdapterResult<MarketplaceQuestionPage>> ListQuestionsAsync(AdapterContext context, QuestionPollRequest request, CancellationToken cancellationToken)
        {
            var status = request.Status ?? "";
            ReadCalls.Add((status, request.Page));
            var pagedStatus = status is "WAITING_FOR_ANSWER" or "ANSWERED";
            var totalPages = pagedStatus ? 5 : 0;
            IReadOnlyList<RemoteMarketplaceQuestion> items = pagedStatus
                ? Enumerable.Range(0, request.Size)
                    .Select(index => NewRemoteQuestion(
                        $"{status}-{request.Page:D2}-{index:D2}",
                        status,
                        lastModifiedAt))
                    .ToArray()
                : [];
            return Task.FromResult(AdapterResult<MarketplaceQuestionPage>.Success(new(items, request.Page, totalPages, pagedStatus ? (long)totalPages * request.Size : 0)));
        }

        public Task<AdapterResult<RemoteMarketplaceQuestion>> GetQuestionAsync(AdapterContext context, string questionId, string kind, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<AdapterResult<RemoteQuestionAnswerResult>> AnswerQuestionAsync(AdapterContext context, string questionId, string kind, string answer, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        private static RemoteMarketplaceQuestion NewRemoteQuestion(string id, string status, DateTimeOffset modifiedAt) =>
            new(id, "PRODUCT", status, "Ürün sorusu", null, null, null, null, null, "Müşteri", null, modifiedAt.AddDays(-1), null, modifiedAt, []);
    }

    private static DefaultHttpContext NewHttpContext(string rawToken)
    {
        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection()
                .AddSingleton<IConfiguration>(new ConfigurationBuilder()
                    .AddInMemoryCollection(new Dictionary<string, string?> { ["MARKETPLACEHUB_ENVIRONMENT"] = "PILOT_LOCAL" })
                    .Build())
                .BuildServiceProvider()
        };
        context.Request.Scheme = "http";
        context.Request.Headers.Cookie = $"{SessionAuthMiddleware.PilotLocalCookieName}={rawToken}";
        return context;
    }

    private static string? QueryValue(Uri uri, string key)
    {
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            var name = separator < 0 ? pair : pair[..separator];
            if (!string.Equals(Uri.UnescapeDataString(name), key, StringComparison.Ordinal)) continue;
            return separator < 0 ? string.Empty : Uri.UnescapeDataString(pair[(separator + 1)..]);
        }
        return null;
    }

    private static long QueryLong(Uri uri, string key) =>
        long.Parse(QueryValue(uri, key) ?? throw new Xunit.Sdk.XunitException($"Expected query parameter '{key}' in '{uri.Query}'."));

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class RecordingHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class RecordingHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri ?? throw new InvalidOperationException("Request URI was not assigned."));
            return Task.FromResult(responseFactory(request));
        }
    }

    private static Tenant NewTenant(string code) => new()
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

    private static IntegrationOutboxEvent NewOutboxEvent(Guid tenantId, DateTimeOffset createdAt, DateTimeOffset? publishedAt) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = tenantId,
        ResourceType = "orders",
        OperationType = "updated",
        AggregateType = "order",
        AggregateId = Guid.CreateVersion7(),
        AggregateVersion = 1,
        PayloadJson = "{}",
        CreatedAt = createdAt,
        PublishedAt = publishedAt,
        NextAttemptAt = createdAt
    };

    private static ApplicationUser NewUser()
    {
        var id = Guid.CreateVersion7();
        return new ApplicationUser
        {
            Id = id,
            UserName = $"test-{id:N}",
            NormalizedUserName = $"TEST-{id:N}",
            Email = $"test-{id:N}@example.invalid",
            NormalizedEmail = $"TEST-{id:N}@EXAMPLE.INVALID",
            DisplayName = "Tenant isolation test user",
            Status = "ACTIVE",
            ForcePasswordChange = false,
            SessionVersion = 1,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            SecurityStamp = Guid.NewGuid().ToString("N")
        };
    }

    private static TenantMembership NewMembership(Guid tenantId, Guid userId, MembershipRole role) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = tenantId,
        UserId = userId,
        Role = role,
        Status = RecordStatus.Active,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
        Version = 1
    };

    private static UserSession NewSession(Guid userId, Guid tenantId, string rawToken, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(),
        UserId = userId,
        TenantId = tenantId,
        State = SessionState.Active,
        TokenHash = new TokenHasher(Encoding.UTF8.GetBytes("tenant-isolation-test-key-32-bytes!!")).Hash(rawToken),
        SessionVersion = 1,
        IssuedAt = now,
        ReauthenticatedAt = now,
        LastSeenAt = now,
        ExpiresAt = now.AddMinutes(30),
        AbsoluteExpiresAt = now.AddHours(12)
    };

    private static OperationalIssue NewIssue(Guid tenantId, string key) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = tenantId,
        DedupeKey = key,
        Code = "TEST",
        Summary = "Tenant isolation test",
        Status = IssueStatus.Open,
        FirstSeenAt = DateTimeOffset.UtcNow,
        LastSeenAt = DateTimeOffset.UtcNow,
        OccurrenceCount = 1
    };

    private static IntegrationJob NewJob(Guid tenantId)
    {
        var now = DateTimeOffset.UtcNow;
        var suffix = Guid.NewGuid().ToString("N");
        return new IntegrationJob
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            JobType = "TEST_LEASE",
            PayloadJson = "{}",
            PayloadVersion = 1,
            PayloadHash = "test-payload-hash",
            JobDedupKey = $"lease-{suffix}",
            EffectIdempotencyKey = $"effect-{suffix}",
            Priority = 1,
            Status = JobStatus.Pending,
            AvailableAt = now.AddMinutes(-1),
            MaxAttempts = 3,
            CorrelationId = $"corr-{suffix}",
            CreatedAt = now,
            Version = 1
        };
    }

    private static ApiIdempotencyRecord NewIdempotencyRecord(Guid tenantId, string route, string key) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = tenantId,
        RouteTemplate = route,
        IdempotencyKey = key,
        RequestHash = "test-request-hash",
        State = "IN_PROGRESS",
        CreatedAt = DateTimeOffset.UtcNow,
        ExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
    };

    private async Task DeleteJobAndTenantAsync(Guid jobId, Guid tenantId)
    {
        await using var db = fixture.CreateContext();
        await db.IntegrationJobs.Where(x => x.Id == jobId).ExecuteDeleteAsync();
        await db.IntegrationOutboxEvents.Where(x => x.TenantId == tenantId).ExecuteDeleteAsync();
        await db.Tenants.Where(x => x.Id == tenantId).ExecuteDeleteAsync();
    }

    private async Task DeleteIdempotencyRecordsAndTenantsAsync(Guid firstTenantId, Guid? secondTenantId = null)
    {
        await using var db = fixture.CreateContext();
        await db.ApiIdempotencyRecords
            .Where(x => x.TenantId == firstTenantId || (secondTenantId.HasValue && x.TenantId == secondTenantId.Value))
            .ExecuteDeleteAsync();
        await db.Tenants
            .Where(x => x.Id == firstTenantId || (secondTenantId.HasValue && x.Id == secondTenantId.Value))
            .ExecuteDeleteAsync();
    }

    private static DefaultHttpContext NewIdempotencyHttpContext(string path, string key, string body)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = path;
        context.Request.Headers["Idempotency-Key"] = key;
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static string ComputeRequestHash(string method, string path, string query, string body)
    {
        var prefix = Encoding.UTF8.GetBytes($"{method}\n{path}\n{query}\n");
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        var combined = new byte[prefix.Length + bodyBytes.Length];
        prefix.CopyTo(combined, 0);
        bodyBytes.CopyTo(combined, prefix.Length);
        return Convert.ToHexString(SHA256.HashData(combined));
    }

    private ServiceProvider CreateOutboxDispatcherServices() => new ServiceCollection()
        .AddScoped(_ => fixture.CreateContext())
        .AddScoped<IDashboardReadService>(_ => new NoopDashboardReadService())
        .BuildServiceProvider();

    private OperationsRealtimeBroadcaster CreateOutboxDispatcher(IServiceProvider services, IHubContext<OperationsHub> hub) =>
        new(services.GetRequiredService<IServiceScopeFactory>(), hub, fixture.TimeProvider, NullLogger<OperationsRealtimeBroadcaster>.Instance);

    private static Guid ReadFirstOutboxEventId(object?[] arguments)
    {
        Assert.Single(arguments);
        var events = JsonSerializer.SerializeToElement(arguments[0]).GetProperty("events");
        return events[0].GetProperty("eventId").GetGuid();
    }

    private static async Task WaitForCountAsync(System.Collections.Concurrent.ConcurrentQueue<Guid> values, int expectedCount)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < deadline && values.Count < expectedCount)
            await Task.Delay(20);
        Assert.Equal(expectedCount, values.Count);
    }

    private sealed class FixedWebhookVerifier(VerifiedWebhookEnvelope envelope) : IWebhookVerifier
    {
        public ValueTask<AdapterResult<VerifiedWebhookEnvelope>> VerifyAsync(
            ReadOnlyMemory<byte> rawBody,
            IReadOnlyDictionary<string, string> headers,
            Guid connectionId,
            Guid subscriptionId,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(AdapterResult<VerifiedWebhookEnvelope>.Success(envelope));
    }

    private sealed class FixedTenantContextAccessor(Guid tenantId) : ITenantContextAccessor
    {
        public TenantContext? Current { get; } = new(Guid.Empty, tenantId, "ADMIN");
    }

    private sealed class NoopDashboardReadService : IDashboardReadService
    {
        public Task<DashboardBootstrapView> BootstrapAsync(Guid tenantId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<DashboardRevenuePointView>> RevenueSeriesAsync(Guid tenantId, DateTimeOffset from, DateTimeOffset to, string? platform, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task RebuildTenantAsync(Guid tenantId, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class ExplainFirstInvoiceWorkspaceScanInterceptor(ITestOutputHelper output) : DbCommandInterceptor
    {
        private int captureNext;

        public bool CaptureNext
        {
            get => Volatile.Read(ref captureNext) == 1;
            set => Volatile.Write(ref captureNext, value ? 1 : 0);
        }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)
                && command.CommandText.Contains("shipment_packages", StringComparison.OrdinalIgnoreCase)
                && command.CommandText.Contains("orders", StringComparison.OrdinalIgnoreCase)
                && Interlocked.CompareExchange(ref captureNext, 0, 1) == 1)
            {
                await using var explainCommand = command.Connection!.CreateCommand();
                explainCommand.Transaction = command.Transaction;
                explainCommand.CommandTimeout = command.CommandTimeout;
                explainCommand.CommandText = $"EXPLAIN (ANALYZE, BUFFERS, VERBOSE) {command.CommandText}";
                foreach (DbParameter parameter in command.Parameters)
                    explainCommand.Parameters.Add(((ICloneable)parameter).Clone());

                var planLines = new List<string>();
                await using var planReader = await explainCommand.ExecuteReaderAsync(cancellationToken);
                while (await planReader.ReadAsync(cancellationToken)) planLines.Add(planReader.GetString(0));
                output.WriteLine($"Fatura çalışma alanı servis aday sorgusu EXPLAIN: {Environment.NewLine}{string.Join(Environment.NewLine, planLines)}");
            }

            return result;
        }
    }

    private sealed class RecordingOperationsHubContext(Action<object?[], CancellationToken> onSend) : IHubContext<OperationsHub>
    {
        public IHubClients Clients { get; } = new RecordingHubClients(new RecordingClientProxy(onSend));
        public IGroupManager Groups { get; } = new NoopHubGroupManager();
    }

    private sealed class RecordingHubClients(IClientProxy proxy) : IHubClients
    {
        public IClientProxy All => proxy;
        public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => proxy;
        public IClientProxy Client(string connectionId) => proxy;
        public IClientProxy Clients(IReadOnlyList<string> connectionIds) => proxy;
        public IClientProxy Group(string groupName) => proxy;
        public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => proxy;
        public IClientProxy Groups(IReadOnlyList<string> groupNames) => proxy;
        public IClientProxy User(string userId) => proxy;
        public IClientProxy Users(IReadOnlyList<string> userIds) => proxy;
    }

    private sealed class RecordingClientProxy(Action<object?[], CancellationToken> onSend) : IClientProxy
    {
        public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken)
        {
            onSend(args, cancellationToken);
            return Task.CompletedTask;
        }
    }

    private sealed class NoopHubGroupManager : IGroupManager
    {
        public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}

public sealed class PostgreSqlTenantIsolationFixture : IAsyncLifetime
{
    private static readonly SemaphoreSlim MigrationGate = new(1, 1);
    private readonly bool shouldMigrate = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MARKETPLACEHUB_TEST_CONNECTION"));
    private readonly string databaseConnectionString = Environment.GetEnvironmentVariable("MARKETPLACEHUB_TEST_CONNECTION")
        ?? "Host=localhost;Port=5432;Database=marketplacehub;Username=marketplacehub;Password=development-only";

    public DateTimeOffset Now { get; } = DateTimeOffset.UtcNow;
    public TimeProvider TimeProvider { get; } = TimeProvider.System;
    public TokenHasher TokenHasher { get; } = new(Encoding.UTF8.GetBytes("tenant-isolation-test-key-32-bytes!!"));

    public async Task InitializeAsync()
    {
        if (!shouldMigrate) return;
        await MigrationGate.WaitAsync();
        try
        {
            await using var db = CreateContext();
            await db.Database.MigrateAsync();
        }
        finally
        {
            MigrationGate.Release();
        }
    }

    public AppDbContext CreateContext() => new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(databaseConnectionString).Options);

    public Task DisposeAsync() => Task.CompletedTask;
}

public sealed class PostgreSqlFactAttribute : FactAttribute
{
    public PostgreSqlFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MARKETPLACEHUB_TEST_CONNECTION")))
            Skip = "PostgreSQL integration test requires MARKETPLACEHUB_TEST_CONNECTION pointing to a dedicated test database.";
    }
}
