using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure;
using MarketplaceHub.Infrastructure.Bootstrap;
using MarketplaceHub.Infrastructure.Identity;
using MarketplaceHub.Infrastructure.Persistence;
using MarketplaceHub.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;

namespace MarketplaceHub.Persistence.IntegrationTests;

public sealed class PostgresSchemaTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:18.4@sha256:3a82e1f56c8f0f5616a11103ac3d47e632c3938698946a7ad26da0df1334744a").Build();
    public ValueTask InitializeAsync() => new(_postgres.StartAsync());
    public ValueTask DisposeAsync() => _postgres.DisposeAsync();

    [Fact]
    public async Task Migration_creates_three_schemas_without_bootstrap_data()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(_postgres.GetConnectionString()).Options);
        await db.Database.MigrateAsync(cancellationToken);
        Assert.Empty(await db.BootstrapStates.ToListAsync(cancellationToken));
        Assert.Empty(await db.Users.ToListAsync(cancellationToken));
        Assert.Empty(await db.Tenants.ToListAsync(cancellationToken));
    }

    [Fact]
    public async Task Concurrent_and_repeated_bootstrap_creates_exactly_one_owner_graph()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using (var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(_postgres.GetConnectionString()).Options))
            await db.Database.MigrateAsync(cancellationToken);
        var password = $"Aa!9-{Guid.NewGuid():N}";
        await using var providerOne = BuildProvider(password);
        await using var providerTwo = BuildProvider(password);
        await using var scopeOne = providerOne.CreateAsyncScope();
        await using var scopeTwo = providerTwo.CreateAsyncScope();
        await Task.WhenAll(
            scopeOne.ServiceProvider.GetRequiredService<BootstrapService>().RunAsync(cancellationToken),
            scopeTwo.ServiceProvider.GetRequiredService<BootstrapService>().RunAsync(cancellationToken));
        await scopeOne.ServiceProvider.GetRequiredService<BootstrapService>().RunAsync(cancellationToken);

        await using var verification = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(_postgres.GetConnectionString()).Options);
        Assert.Equal(1, await verification.BootstrapStates.CountAsync(cancellationToken));
        Assert.Equal(1, await verification.Tenants.CountAsync(cancellationToken));
        Assert.Equal(1, await verification.Users.CountAsync(cancellationToken));
        Assert.Equal(1, await verification.TenantMemberships.CountAsync(cancellationToken));
        Assert.True((await verification.Users.SingleAsync(cancellationToken)).ForcePasswordChange);
    }

    private ServiceProvider BuildProvider(string password)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:AppDb"] = _postgres.GetConnectionString(),
            ["MARKETPLACEHUB_ENVIRONMENT"] = "PILOT_LOCAL",
            ["Security:CredentialKey"] = Convert.ToBase64String(Enumerable.Repeat((byte)42, 32).ToArray()),
            ["Bootstrap:Enabled"] = "true",
            ["Bootstrap:TenantCode"] = "ravencia",
            ["Bootstrap:TenantDisplayName"] = "Ravencia",
            ["Bootstrap:OwnerEmail"] = "owner@example.invalid",
            ["Bootstrap:OwnerDisplayName"] = "Ravencia Admin",
            ["Bootstrap:OwnerPassword"] = password,
            ["Storage:Root"] = Path.Combine(Path.GetTempPath(), "marketplacehub-test-files"),
            ["DataProtection:KeysRoot"] = Path.Combine(Path.GetTempPath(), "marketplacehub-test-keys")
        }).Build();
        var services = new ServiceCollection(); services.AddLogging(); services.AddMarketplaceInfrastructure(configuration);
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task Job_dedup_lease_heartbeat_and_stale_token_guards_hold()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(_postgres.GetConnectionString()).Options);
        await db.Database.MigrateAsync(cancellationToken);
        var tenant = new Tenant { Id = Guid.NewGuid(), Code = $"job-{Guid.NewGuid():N}", DisplayName = "Job Test", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
        db.Tenants.Add(tenant);
        db.IntegrationJobs.Add(NewJob(tenant.Id));
        await db.SaveChangesAsync(cancellationToken);
        await using (var duplicateDb = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(_postgres.GetConnectionString()).Options))
        {
            duplicateDb.IntegrationJobs.Add(NewJob(tenant.Id));
            await Assert.ThrowsAsync<DbUpdateException>(() => duplicateDb.SaveChangesAsync(cancellationToken));
        }
        var hasher = new TokenHasher(Enumerable.Repeat((byte)11, 32).ToArray());
        var leases = new JobLeaseService(db, hasher, TimeProvider.System);
        var lease = Assert.IsType<MarketplaceHub.Application.LeasedJob>(await leases.TryLeaseAsync(TimeSpan.FromMinutes(2), cancellationToken));
        Assert.False(await leases.HeartbeatAsync(lease.Id, "stale-token", TimeSpan.FromMinutes(1), cancellationToken));
        Assert.True(await leases.HeartbeatAsync(lease.Id, lease.LeaseToken, TimeSpan.FromMinutes(1), cancellationToken));
        Assert.True(await leases.CompleteAsync(lease.Id, lease.LeaseToken, true, null, cancellationToken));
        Assert.False(await leases.CompleteAsync(lease.Id, lease.LeaseToken, true, null, cancellationToken));

        db.IntegrationJobs.Add(NewJob(tenant.Id, "worker-kill")); await db.SaveChangesAsync(cancellationToken);
        var abandoned = Assert.IsType<MarketplaceHub.Application.LeasedJob>(await leases.TryLeaseAsync(TimeSpan.FromMinutes(2), cancellationToken));
        var abandonedEntity = await db.IntegrationJobs.SingleAsync(x => x.Id == abandoned.Id, cancellationToken);
        abandonedEntity.LeaseExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1); await db.SaveChangesAsync(cancellationToken);
        Assert.Equal(1, await leases.ReapExpiredAsync(cancellationToken));
        await db.Entry(abandonedEntity).ReloadAsync(cancellationToken);
        Assert.Equal(JobStatus.Pending, abandonedEntity.Status);
    }

    [Fact]
    public async Task Parallel_recovery_code_consumption_has_one_winner()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var setup = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(_postgres.GetConnectionString()).Options);
        await setup.Database.MigrateAsync(cancellationToken);
        var userId = Guid.NewGuid(); var digest = Guid.NewGuid().ToString("N");
        setup.Users.Add(new ApplicationUser { Id = userId, UserName = $"recovery-{userId:N}", NormalizedUserName = $"RECOVERY-{userId:N}", Email = $"recovery-{userId:N}@example.invalid", NormalizedEmail = $"RECOVERY-{userId:N}@EXAMPLE.INVALID", DisplayName = "Recovery Test", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
        setup.RecoveryCodes.Add(new RecoveryCode { Id = Guid.NewGuid(), UserId = userId, BatchId = Guid.NewGuid(), CodeDigest = digest, CreatedAt = DateTimeOffset.UtcNow });
        await setup.SaveChangesAsync(cancellationToken);
        async Task<int> ConsumeAsync()
        {
            await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(_postgres.GetConnectionString()).Options);
            return await db.RecoveryCodes.Where(x => x.UserId == userId && x.CodeDigest == digest && x.UsedAt == null)
                .ExecuteUpdateAsync(update => update.SetProperty(x => x.UsedAt, DateTimeOffset.UtcNow), cancellationToken);
        }
        var results = await Task.WhenAll(ConsumeAsync(), ConsumeAsync());
        Assert.Equal(1, results.Sum());
    }

    private static IntegrationJob NewJob(Guid tenantId, string dedup = "same") => new()
    {
        Id = Guid.NewGuid(),
        TenantId = tenantId,
        JobType = "F1_GUARD_TEST",
        PayloadJson = "{}",
        PayloadVersion = 1,
        PayloadHash = "hash",
        JobDedupKey = dedup,
        EffectIdempotencyKey = $"effect-{dedup}",
        AvailableAt = DateTimeOffset.UtcNow,
        CorrelationId = Guid.NewGuid().ToString("N")
    };
}
