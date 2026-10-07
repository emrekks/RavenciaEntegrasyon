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
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
using Xunit.Abstractions;

namespace MarketplaceHub.Application.Tests;

public sealed class PostgreSqlTenantIsolationTests(PostgreSqlTenantIsolationFixture fixture, ITestOutputHelper output) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
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
            await cleanup.Tenants.Where(x => x.Id == tenant.Id).ExecuteDeleteAsync();
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
        var orderPort = new ReadOnlyInvoiceTestOrderPort(new RemotePackage(
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

            var persistedCursor = await processorDb.SyncCursors.SingleAsync(cursor => cursor.TenantId == tenant.Id
                && cursor.ConnectionId == connection.Id
                && cursor.ResourceType == "ORDER_INVOICE_RECONCILIATION");
            Assert.Equal(OrderInvoiceReconciliationBatchPolicy.WriteCursor(regularOrderId), persistedCursor.OpaqueCursor);

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
            ResourceType = "ORDER_RECONCILE_DAILY",
            LastAttemptAt = now.AddHours(-24).AddMinutes(-18),
            LastSuccessAt = now.AddHours(-24).AddMinutes(-18),
            LastModifiedWatermark = now.AddDays(-2),
            LastCursorAdvancedAt = now.AddDays(-2),
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
            seedDb.ConnectionSyncPolicies.AddRange(policy, dailyPolicy);
            seedDb.SyncCursors.AddRange(cursor, dailyCursor);
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
    public async Task InvoiceWorkspace_ShowsOpenMarketplaceInvoiceReadFailureForPackage()
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
            Assert.Equal(items.Count(row => row.IsDueSoon), summary.DueSoonCount);
            Assert.Equal(1, summary.DueSoonCount);
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
        var seededRows = Enumerable.Range(0, 21).Select(index =>
        {
            var orderId = Guid.CreateVersion7();
            var order = NewInvoiceTestOrder(tenant.Id, connection.Id, orderId, $"page-order-{index:D2}", isReturnClaim: false, fixture.Now);
            var package = NewInvoiceTestPackage(tenant.Id, connection.Id, orderId, Guid.CreateVersion7(), $"page-package-{index:D2}", fixture.Now);
            package.CreatedBy = "MARKETPLACE_DETAIL";
            package.StatusOccurredAt = fixture.Now.AddDays(-(6 + index));
            package.MarketplaceInvoiceStatus = MarketplaceInvoiceStatus.NotInvoiced;
            return (order, package);
        }).ToArray();

        await using (var seedDb = fixture.CreateContext())
        {
            seedDb.Tenants.Add(tenant);
            seedDb.PlatformConnections.Add(connection);
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

            Assert.Equal(21, firstPage.TotalCount);
            Assert.Equal(21, firstPage.DueSoonCount);
            Assert.Equal(21, firstPage.UninvoicedCount);
            Assert.Equal(21, firstPage.TotalPackageCount);
            Assert.True(firstPage.HasPendingMarketplaceInvoices);
            Assert.Equal(2, firstPage.TotalPages);
            Assert.Equal(20, firstPage.Items.Count);
            Assert.Single(secondPage.Items);
            Assert.Equal(2, clampedPage.PageNumber);
            Assert.Equal(secondPage.Items.Single().PackageId, Assert.Single(clampedPage.Items).PackageId);
            Assert.Equal(21, secondPage.TotalCount);
            Assert.Equal(seededRows[0].package.Id, firstPage.Items[0].PackageId);
            Assert.Empty(firstPage.Items[0].Lines!);
            Assert.Equal(1, searchedPage.TotalCount);
            Assert.Equal(seededRows[20].package.Id, Assert.Single(searchedPage.Items).PackageId);
        }
        finally
        {
            await DeleteInvoiceTestTenantAsync(tenant.Id);
        }
    }

    [PostgreSqlFact]
    public async Task InvoiceWorkspacePage_ProcessesOneHundredThousandCandidatesAndReturnsOnlyOnePage()
    {
        const int candidateCount = 100_000;
        const int batchSize = 500;
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
                    package.CreatedBy = "MARKETPLACE_DETAIL";
                    package.StatusOccurredAt = fixture.Now.AddDays(-6);
                    package.MarketplaceInvoiceStatus = MarketplaceInvoiceStatus.NotInvoiced;
                    return (order, package);
                }).ToArray();
                seedDb.Orders.AddRange(batch.Select(row => row.order));
                seedDb.ShipmentPackages.AddRange(batch.Select(row => row.package));
                await seedDb.SaveChangesAsync();
            }

            var testConnectionString = Environment.GetEnvironmentVariable("MARKETPLACEHUB_TEST_CONNECTION")
                ?? throw new InvalidOperationException("PostgreSQL load test requires the isolated test connection.");
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseNpgsql(testConnectionString)
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

            await db.Database.OpenConnectionAsync();
            try
            {
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
        var returnsPort = new ReadOnlyReturnTestPort(fixture.Now, rawStatus: "Accepted", externalOrderId: order.ExternalOrderId);

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
                Assert.Equal(["accepted-claim"], returnsPort.ReadCalls);
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

            Assert.Equal(1, approved.TotalCount);
            Assert.Equal("APPROVED", Assert.Single(approved.Items).Status);
            Assert.Equal(0, actionRequired.TotalCount);
            Assert.Empty(actionRequired.Items);
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
    public async Task TrendyolOrderPoll_UsesThirteenDayWindowsAndStopsAtThirtyDayHistoryFloor()
    {
        var tenant = NewTenant("trendyol-order-range-contract");
        var anchor = DateTimeOffset.Parse("2026-10-02T00:00:00Z");
        var connection = new PlatformConnection
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenant.Id,
            PublicId = Guid.CreateVersion7(),
            PlatformCode = "TRENDYOL",
            Environment = "STAGE",
            DisplayName = "Trendyol order contract test",
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
            Content = new StringContent("{\"content\":[],\"page\":0,\"totalPages\":0}", Encoding.UTF8, "application/json")
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
            var context = new AdapterContext(tenant.Id, connection.Id, "trendyol-order-range-contract", "contract-test", anchor.AddMinutes(1), Operation: IntegrationOperation.Manual);
            var window = new OrderPollWindow(null, null, PackageItemStatuses: "Delivered", StoreFrontCode: "TR");

            var first = await client.PollAsync(context, window, new(null, 200), CancellationToken.None);
            Assert.True(first.IsSuccess, first.Error?.SafeMessage);
            Assert.True(first.Value!.HasMore);
            Assert.Equal(anchor.AddDays(-13).ToUnixTimeMilliseconds(), QueryLong(requests.Requests[0], "startDate"));
            Assert.Equal(anchor.ToUnixTimeMilliseconds(), QueryLong(requests.Requests[0], "endDate"));

            var second = await client.PollAsync(context, window, new(first.Value.NextCursor, 200), CancellationToken.None);
            Assert.True(second.IsSuccess, second.Error?.SafeMessage);
            Assert.True(second.Value!.HasMore);
            Assert.Equal(anchor.AddDays(-26).AddMilliseconds(-1).ToUnixTimeMilliseconds(), QueryLong(requests.Requests[1], "startDate"));
            Assert.Equal(anchor.AddDays(-13).AddMilliseconds(-1).ToUnixTimeMilliseconds(), QueryLong(requests.Requests[1], "endDate"));

            var third = await client.PollAsync(context, window, new(second.Value.NextCursor, 200), CancellationToken.None);
            Assert.True(third.IsSuccess, third.Error?.SafeMessage);
            Assert.False(third.Value!.HasMore);
            Assert.Equal(anchor.AddDays(-30).ToUnixTimeMilliseconds(), QueryLong(requests.Requests[2], "startDate"));
            Assert.Equal(anchor.AddDays(-26).AddMilliseconds(-2).ToUnixTimeMilliseconds(), QueryLong(requests.Requests[2], "endDate"));
            Assert.All(requests.Requests, uri => Assert.Equal("/integration/order/sellers/seller-contract-test/v2/orders", uri.AbsolutePath));
            Assert.All(requests.Requests, uri => Assert.Equal("Delivered", QueryValue(uri, "status")));
        }
        finally
        {
            await using var cleanupDb = fixture.CreateContext();
            await cleanupDb.PlatformCredentials.Where(row => row.TenantId == tenant.Id).ExecuteDeleteAsync();
            await DeleteInvoiceTestTenantAsync(tenant.Id);
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
        await db.ShipmentPackages.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.Orders.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
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

    private sealed class ReadOnlyInvoiceTestOrderPort(RemotePackage package) : IOrderPort
    {
        public int OrderReadCalls { get; private set; }
        public List<string> PackageReadCalls { get; } = [];

        public Task<AdapterResult<AdapterPageResult<RemoteOrder>>> PollAsync(AdapterContext context, OrderPollWindow window, AdapterPageRequest page, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Invoice reconciliation test must not poll order pages.");

        public Task<AdapterResult<RemoteOrder>> GetAsync(AdapterContext context, string externalOrderId, CancellationToken cancellationToken)
        {
            OrderReadCalls++;
            throw new NotSupportedException("Return hydration must defer while the order lane is locked.");
        }

        public Task<AdapterResult<RemoteOrderPackage>> GetShipmentPackageAsync(AdapterContext context, string externalPackageId, DateTimeOffset? packageStatusOccurredAt, CancellationToken cancellationToken)
        {
            PackageReadCalls.Add(externalPackageId);
            return Task.FromResult(AdapterResult<RemoteOrderPackage>.Success(new("regular-order", package)));
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

    private sealed class ReadOnlyReturnTestPort(DateTimeOffset lastModifiedAt, string rawStatus = "NewRequest", string externalOrderId = "return-lifecycle-order") : IReturnPort
    {
        public List<string> ReadCalls { get; } = [];

        public Task<AdapterResult<AdapterPageResult<RemoteReturnClaim>>> PollAsync(AdapterContext context, ReturnPollWindow window, AdapterPageRequest page, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Return lifecycle test must use individual claim reads.");

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
