using System.Collections.Concurrent;
using System.Text;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// Trendyol sends the same package webhook twice about 9 ms apart. Both deliveries are answered 200, one inbox row and one
// ingest job are kept, and no duplicate-key error is raised or logged (the check-then-insert logged an EF Error each time).
[Collection("postgres-integration")]
public sealed class PostgreSqlWebhookInboxConcurrencyTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    private const string RouteToken = "webhook-race-route-token";
    private const string RawJson = """{"shipmentPackageId":3300000001,"orderNumber":"10900000001","status":"Created"}""";

    [PostgreSqlFact]
    public async Task TwoParallelDeliveriesOfTheSamePayloadKeepOneInboxRowAndOneJob()
    {
        var (tenant, connection, subscription) = await SeedAsync("webhook-race");
        var errors = new ConcurrentQueue<string>();
        try
        {
            // Several rounds, each with its own message id, so the two requests meet at the insert at least once.
            for (var round = 0; round < 10; round++)
            {
                var messageId = $"package:3300000001:race-{round}";
                using var start = new ManualResetEventSlim(false);
                var deliveries = Enumerable.Range(0, 2).Select(_ => Task.Run(async () =>
                {
                    start.Wait();
                    return await ReceiveAsync(connection.PublicId, messageId, errors);
                })).ToArray();
                start.Set();
                var results = await Task.WhenAll(deliveries);

                Assert.All(results, result =>
                {
                    Assert.True(result.Succeeded, result.Error?.Code);
                    Assert.True(result.Value);
                });
            }

            await using var verification = fixture.CreateContext();
            var inbox = await verification.InboxMessages.AsNoTracking().Where(row => row.TenantId == tenant.Id).ToListAsync();
            var jobs = await verification.IntegrationJobs.AsNoTracking().Where(row => row.TenantId == tenant.Id).ToListAsync();
            Assert.Equal(10, inbox.Count);
            Assert.Equal(10, inbox.Select(row => row.ExternalMessageId).Distinct(StringComparer.Ordinal).Count());
            Assert.Equal(10, jobs.Count);
            Assert.All(jobs, job =>
            {
                Assert.Equal(MarketplaceJobTypes.WebhookIngest, job.JobType);
                Assert.Equal(JobStatus.Pending, job.Status);
                Assert.Equal("webhook", job.TriggerType);
                Assert.Equal(connection.Id, job.ConnectionId);
                Assert.Equal(JobRetryPolicy.DefaultMaxAttempts, job.MaxAttempts);
                Assert.Equal(0, job.AttemptCount);
                Assert.Equal(job.JobDedupKey, job.EffectIdempotencyKey);
                Assert.StartsWith($"webhook:{connection.Id}:package:3300000001:race-", job.JobDedupKey, StringComparison.Ordinal);
                Assert.True(job.CreatedAt > DateTimeOffset.UtcNow.AddMinutes(-5));
            });
            Assert.Empty(errors);
            var touched = await verification.WebhookSubscriptions.AsNoTracking().SingleAsync(row => row.Id == subscription.Id);
            Assert.NotNull(touched.LastReceivedAt);
        }
        finally
        {
            await CleanupAsync(tenant.Id, connection.Id, subscription.Id);
        }
    }

    // A delivery repeated after the first one is committed is answered the same and adds nothing; a delivery whose job
    // was cleaned away (the inbox row is still there) queues the job again.
    [PostgreSqlFact]
    public async Task ARepeatedDeliveryAddsNothingAndAMissingJobIsQueuedAgain()
    {
        var (tenant, connection, subscription) = await SeedAsync("webhook-repeat");
        var errors = new ConcurrentQueue<string>();
        try
        {
            const string messageId = "package:3300000002:repeat";
            Assert.True((await ReceiveAsync(connection.PublicId, messageId, errors)).Value);
            Assert.True((await ReceiveAsync(connection.PublicId, messageId, errors)).Value);
            await using (var db = fixture.CreateContext())
            {
                Assert.Equal(1, await db.InboxMessages.CountAsync(row => row.TenantId == tenant.Id));
                Assert.Equal(1, await db.IntegrationJobs.CountAsync(row => row.TenantId == tenant.Id));
                await db.IntegrationJobs.Where(row => row.TenantId == tenant.Id).ExecuteDeleteAsync();
            }

            Assert.True((await ReceiveAsync(connection.PublicId, messageId, errors)).Value);
            await using var verification = fixture.CreateContext();
            Assert.Equal(1, await verification.InboxMessages.CountAsync(row => row.TenantId == tenant.Id));
            Assert.Equal(1, await verification.IntegrationJobs.CountAsync(row => row.TenantId == tenant.Id));
            Assert.Empty(errors);
        }
        finally
        {
            await CleanupAsync(tenant.Id, connection.Id, subscription.Id);
        }
    }

    // A Shopify delivery older than five minutes is answered 200 and dropped; a stale product delete (held back during a
    // deploy) still queues the connection's link check at once, so the deleted product does not show as live until night.
    [PostgreSqlFact]
    public async Task AStaleShopifyProductDeleteStillQueuesTheLinkCheckAndAStaleOrderDoesNot()
    {
        var (tenant, connection, subscription) = await SeedAsync("webhook-stale", "SHOPIFY", "my-shop");
        try
        {
            var staleOrder = await ReceiveStaleAsync(connection.PublicId, "orders/updated");
            Assert.True(staleOrder.Succeeded);
            Assert.False(staleOrder.Value);
            await using (var db = fixture.CreateContext())
                Assert.Equal(0, await db.IntegrationJobs.CountAsync(row => row.TenantId == tenant.Id));

            var staleDelete = await ReceiveStaleAsync(connection.PublicId, "products/delete");
            var repeated = await ReceiveStaleAsync(connection.PublicId, "products/delete");

            Assert.True(staleDelete.Succeeded);
            Assert.True(staleDelete.Value);
            Assert.True(repeated.Succeeded);
            await using var verification = fixture.CreateContext();
            var job = await verification.IntegrationJobs.AsNoTracking().SingleAsync(row => row.TenantId == tenant.Id);
            Assert.Equal(MarketplaceJobTypes.ListingVerify, job.JobType);
            Assert.Equal(connection.Id, job.ConnectionId);
            Assert.Equal("webhook", job.TriggerType);
            Assert.Equal(0, await verification.InboxMessages.CountAsync(row => row.TenantId == tenant.Id));
        }
        finally
        {
            await CleanupAsync(tenant.Id, connection.Id, subscription.Id);
        }
    }

    private async Task<ServiceResult<bool>> ReceiveStaleAsync(Guid connectionPublicId, string topic)
    {
        await using var db = fixture.CreateContext();
        var service = new MarketplaceWebhookService(db, fixture.TokenHasher, new StaleVerifier(), fixture.TimeProvider);
        var headers = new Dictionary<string, string> { ["X-Shopify-Topic"] = topic, ["X-Shopify-Shop-Domain"] = "my-shop.myshopify.com" };
        return await service.ReceiveAsync(connectionPublicId, RouteToken, Encoding.UTF8.GetBytes("""{"id":1}"""), headers, $"stale-{Guid.NewGuid():N}", CancellationToken.None);
    }

    private sealed class StaleVerifier : IWebhookVerifier
    {
        public ValueTask<AdapterResult<VerifiedWebhookEnvelope>> VerifyAsync(ReadOnlyMemory<byte> rawBody, IReadOnlyDictionary<string, string> headers,
            Guid connectionId, Guid subscriptionId, CancellationToken cancellationToken) =>
            ValueTask.FromResult(AdapterResult<VerifiedWebhookEnvelope>.Failure(new(AdapterErrorClass.Validation, MarketplaceHub.Infrastructure.Adapters.Shopify.ShopifyWebhookVerifier.StaleCode, "stale", 200, null, null)));
    }

    private async Task<ServiceResult<bool>> ReceiveAsync(Guid connectionPublicId, string messageId, ConcurrentQueue<string> errors)
    {
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(new ErrorCollector(errors)).SetMinimumLevel(LogLevel.Warning));
        var connectionString = Environment.GetEnvironmentVariable("MARKETPLACEHUB_TEST_CONNECTION")!;
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connectionString).UseLoggerFactory(loggerFactory).Options);
        var verifier = new FixedVerifier(new(messageId, "payload-hash", "PACKAGE", RawJson));
        var service = new MarketplaceWebhookService(db, fixture.TokenHasher, verifier, fixture.TimeProvider);
        return await service.ReceiveAsync(connectionPublicId, RouteToken, Encoding.UTF8.GetBytes(RawJson), new Dictionary<string, string>(), $"race-{Guid.NewGuid():N}", CancellationToken.None);
    }

    private async Task<(Tenant Tenant, PlatformConnection Connection, WebhookSubscription Subscription)> SeedAsync(string code, string platformCode = "TRENDYOL", string? externalStoreId = null)
    {
        var now = DateTimeOffset.UtcNow;
        var tenant = new Tenant { Id = Guid.CreateVersion7(), Code = $"{code}-{Guid.NewGuid():N}", DisplayName = code, Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = now, UpdatedAt = now, Version = 1 };
        var connection = new PlatformConnection
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenant.Id,
            PublicId = Guid.CreateVersion7(),
            PlatformCode = platformCode,
            Environment = "STAGE",
            DisplayName = "Trendyol webhook race test",
            ExternalStoreId = externalStoreId ?? $"seller-{Guid.NewGuid():N}",
            Status = "ACTIVE",
            ApiVersion = "V1",
            Version = 1
        };
        var subscription = new WebhookSubscription
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenant.Id,
            ConnectionId = connection.Id,
            RouteTokenHash = fixture.TokenHasher.Hash(RouteToken),
            AuthenticationType = "API_KEY",
            ProtectedVerifierSecret = "test-secret",
            Status = "ACTIVE",
            Version = 1
        };
        await using var db = fixture.CreateContext();
        db.Tenants.Add(tenant);
        db.PlatformConnections.Add(connection);
        db.WebhookSubscriptions.Add(subscription);
        await db.SaveChangesAsync();
        return (tenant, connection, subscription);
    }

    private async Task CleanupAsync(Guid tenantId, Guid connectionId, Guid subscriptionId)
    {
        await using var db = fixture.CreateContext();
        await db.IntegrationJobs.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.InboxMessages.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.IntegrationOutboxEvents.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.OperationalIssues.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.WebhookSubscriptions.Where(row => row.Id == subscriptionId).ExecuteDeleteAsync();
        await db.PlatformConnections.Where(row => row.Id == connectionId).ExecuteDeleteAsync();
        await db.Tenants.Where(row => row.Id == tenantId).ExecuteDeleteAsync();
    }

    private sealed class FixedVerifier(VerifiedWebhookEnvelope envelope) : IWebhookVerifier
    {
        public ValueTask<AdapterResult<VerifiedWebhookEnvelope>> VerifyAsync(ReadOnlyMemory<byte> rawBody, IReadOnlyDictionary<string, string> headers,
            Guid connectionId, Guid subscriptionId, CancellationToken cancellationToken) =>
            ValueTask.FromResult(AdapterResult<VerifiedWebhookEnvelope>.Success(envelope));
    }

    // Collects every Error the database layer logs (EF's "Failed executing DbCommand" / "saving changes" lines).
    private sealed class ErrorCollector(ConcurrentQueue<string> errors) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new Logger(categoryName, errors);
        public void Dispose() { }

        private sealed class Logger(string category, ConcurrentQueue<string> errors) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (logLevel >= LogLevel.Error) errors.Enqueue($"{category}: {formatter(state, exception)}");
            }
        }
    }
}
