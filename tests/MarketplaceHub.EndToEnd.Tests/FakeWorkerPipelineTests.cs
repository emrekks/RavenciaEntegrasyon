using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using MarketplaceHub.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;

namespace MarketplaceHub.EndToEnd.Tests;

public sealed class FakeWorkerPipelineTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-08-02T12:00:00Z");
    private PostgreSqlContainer? postgres;
    private string connectionString = string.Empty;
    private string? externalAdminConnection;
    private string? externalDatabase;

    public async ValueTask InitializeAsync()
    {
        if (Environment.GetEnvironmentVariable("MARKETPLACEHUB_TEST_POSTGRES") is { Length: > 0 } external)
        {
            externalAdminConnection = external;
            externalDatabase = $"marketplacehub_fake_e2e_{Guid.NewGuid():N}";
            await using var connection = new NpgsqlConnection(external);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"CREATE DATABASE \"{externalDatabase}\"", connection);
            await command.ExecuteNonQueryAsync();
            connectionString = new NpgsqlConnectionStringBuilder(external) { Database = externalDatabase, Pooling = false }.ConnectionString;
            return;
        }

        postgres = new PostgreSqlBuilder("postgres:18.4@sha256:3a82e1f56c8f0f5616a11103ac3d47e632c3938698946a7ad26da0df1334744a").Build();
        await postgres.StartAsync();
        connectionString = postgres.GetConnectionString();
    }

    public async ValueTask DisposeAsync()
    {
        if (postgres is not null)
        {
            await postgres.DisposeAsync();
            return;
        }
        if (externalAdminConnection is null || externalDatabase is null) return;
        await using var connection = new NpgsqlConnection(externalAdminConnection);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{externalDatabase}\" WITH (FORCE)", connection);
        await command.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task Worker_kill_retry_with_fake_adapter_does_not_duplicate_the_order_graph()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var tenantId = Guid.Parse("55555555-5555-5555-5555-555555555555");
        var connectionId = Guid.Parse("66666666-6666-6666-6666-666666666666");
        var jobId = Guid.Parse("77777777-7777-7777-7777-777777777777");
        var clock = new MutableTimeProvider(Now);
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connectionString).Options);
        await db.Database.MigrateAsync(cancellationToken);
        db.Tenants.Add(new Tenant { Id = tenantId, Code = "fake-e2e", DisplayName = "Fake E2E", CreatedAt = Now, UpdatedAt = Now });
        db.PlatformConnections.Add(new PlatformConnection { Id = connectionId, PublicId = Guid.Parse("88888888-8888-8888-8888-888888888888"), TenantId = tenantId, PlatformCode = "TRENDYOL", Environment = "STAGE", DisplayName = "Synthetic connection", ExternalStoreId = "synthetic-store", Status = "DRAFT", ApiVersion = "v2", Version = 1 });
        db.IntegrationJobs.Add(new IntegrationJob
        {
            Id = jobId,
            TenantId = tenantId,
            ConnectionId = connectionId,
            JobType = F3JobTypes.OrderSync,
            PayloadJson = "{}",
            PayloadVersion = 1,
            PayloadHash = "synthetic-payload-hash",
            JobDedupKey = "fake-order-sync",
            EffectIdempotencyKey = "fake-order-sync-effect",
            AvailableAt = Now,
            CorrelationId = "fake-e2e-correlation"
        });
        await db.SaveChangesAsync(cancellationToken);

        var leases = new JobLeaseService(db, new TokenHasher(Enumerable.Repeat((byte)17, 32).ToArray()), clock);
        var fake = new DeterministicFakeAdapter(FakeScenario.Success, clock);
        var processor = new F3JobProcessor(db, fake, fake, fake, null!, clock);
        var firstLease = Assert.IsType<LeasedJob>(await leases.TryLeaseAsync(TimeSpan.FromMinutes(2), cancellationToken));
        Assert.True(await processor.ProcessAsync(firstLease.TenantId, firstLease.ConnectionId, firstLease.JobType, firstLease.PayloadJson, firstLease.CorrelationId, cancellationToken));

        clock.Advance(TimeSpan.FromMinutes(3));
        Assert.Equal(1, await leases.ReapExpiredAsync(cancellationToken));
        var job = await db.IntegrationJobs.SingleAsync(x => x.Id == jobId, cancellationToken);
        job.AvailableAt = clock.GetUtcNow().AddSeconds(-1);
        await db.SaveChangesAsync(cancellationToken);
        var retryLease = Assert.IsType<LeasedJob>(await leases.TryLeaseAsync(TimeSpan.FromMinutes(2), cancellationToken));
        Assert.True(await processor.ProcessAsync(retryLease.TenantId, retryLease.ConnectionId, retryLease.JobType, retryLease.PayloadJson, retryLease.CorrelationId, cancellationToken));
        Assert.True(await leases.CompleteAsync(retryLease.Id, retryLease.LeaseToken, true, null, cancellationToken));

        db.ChangeTracker.Clear();
        Assert.Equal(1, await db.Orders.CountAsync(x => x.TenantId == tenantId, cancellationToken));
        Assert.Equal(1, await db.OrderLines.CountAsync(x => x.TenantId == tenantId, cancellationToken));
        Assert.Equal(1, await db.SyncCursors.CountAsync(x => x.TenantId == tenantId && x.ResourceType == "ORDERS", cancellationToken));
        Assert.Equal(2, await db.JobAttempts.CountAsync(x => x.JobId == jobId, cancellationToken));
        Assert.Equal(JobStatus.Succeeded, (await db.IntegrationJobs.SingleAsync(x => x.Id == jobId, cancellationToken)).Status);
    }

    private sealed class MutableTimeProvider(DateTimeOffset value) : TimeProvider
    {
        private DateTimeOffset current = value;
        public override DateTimeOffset GetUtcNow() => current;
        public void Advance(TimeSpan duration) => current = current.Add(duration);
    }
}
