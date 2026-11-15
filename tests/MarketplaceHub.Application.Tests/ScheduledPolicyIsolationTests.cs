using System.Data.Common;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// KUYRUK-10: one sync policy that throws no longer ends the scheduler pass for every other connection.
public sealed class ScheduledPolicyIsolationTests
{
    [Fact]
    public async Task APolicyThatThrowsIsSkippedWithWhatItAddedAndTheOthersGoOn()
    {
        // The change tracker works without a database: nothing here is saved.
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql("Host=localhost;Database=unused").Options);
        var failures = new List<Exception>();
        var first = NewTenant("first");
        var broken = NewTenant("broken");
        var last = NewTenant("last");

        var results = new List<int?>
        {
            await ScheduledPolicyIsolation.RunAsync(db, () => { db.Tenants.Add(first); return Task.FromResult(1); }, failures.Add),
            await ScheduledPolicyIsolation.RunAsync(db, async () =>
            {
                db.Tenants.Add(broken);
                await Task.Yield();
                throw new InvalidOperationException("an unexpected value in this policy's rows");
            }, failures.Add),
            await ScheduledPolicyIsolation.RunAsync(db, () => { db.Tenants.Add(last); return Task.FromResult(1); }, failures.Add)
        };

        Assert.Equal([1, null, 1], results);
        Assert.IsType<InvalidOperationException>(Assert.Single(failures));
        Assert.Equal(EntityState.Added, db.Entry(first).State);
        Assert.Equal(EntityState.Detached, db.Entry(broken).State);
        Assert.Equal(EntityState.Added, db.Entry(last).State);
    }

    [Fact]
    public async Task ACancellationOrALostDatabaseConnectionStillEndsThePass()
    {
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql("Host=localhost;Database=unused").Options);
        var failures = new List<Exception>();

        await Assert.ThrowsAsync<OperationCanceledException>(() => ScheduledPolicyIsolation.RunAsync(db, () => throw new OperationCanceledException(), failures.Add));
        await Assert.ThrowsAsync<NpgsqlException>(() => ScheduledPolicyIsolation.RunAsync(db, () => throw new NpgsqlException("connection refused"), failures.Add));
        await Assert.ThrowsAsync<InvalidOperationException>(() => ScheduledPolicyIsolation.RunAsync(db, () => throw new InvalidOperationException("transient failure", new TimeoutException()), failures.Add));
        Assert.Empty(failures);

        // An error the server reported about this policy's query is the policy's own.
        Assert.Null(await ScheduledPolicyIsolation.RunAsync(db, () => throw new PostgresException("invalid input", "ERROR", "ERROR", PostgresErrorCodes.InvalidTextRepresentation), failures.Add));
        Assert.Single(failures);
    }

    internal static Tenant NewTenant(string code) => new()
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
}

[Collection("postgres-integration")]
public sealed class ScheduledPolicyIsolationPostgreSqlTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    [PostgreSqlFact]
    public async Task Scheduler_StillQueuesTheOtherConnectionsWhenOnePolicyThrows()
    {
        var istanbul = TimeSpan.FromHours(3);
        var today = fixture.Now.ToOffset(istanbul);
        var now = new DateTimeOffset(today.Year, today.Month, today.Day, 15, 0, 0, istanbul);
        var tenant = ScheduledPolicyIsolationTests.NewTenant("policy-isolation");
        var broken = NewConnection(tenant.Id, "policy-broken");
        var healthy = NewConnection(tenant.Id, "policy-healthy");
        var failing = new FailingReferenceRead(broken.Id);
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(Environment.GetEnvironmentVariable("MARKETPLACEHUB_TEST_CONNECTION")).AddInterceptors(failing).Options);
        await using var transaction = await db.Database.BeginTransactionAsync();
        db.Tenants.Add(tenant);
        db.PlatformConnections.AddRange(broken, healthy);
        foreach (var connection in new[] { broken, healthy })
            db.ConnectionSyncPolicies.Add(new ConnectionSyncPolicy { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, ResourceType = "REFERENCE_DATA", IntervalSeconds = 86_400, OverlapSeconds = 0, JitterSeconds = 60, Enabled = true, Version = 1 });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        failing.Armed = true;
        await new ScheduledJobProducer(db, new FixedTimeProvider(now), new ConfigurationBuilder().Build()).EnqueueDueAsync(CancellationToken.None);
        failing.Armed = false;

        Assert.True(failing.Thrown > 0);
        db.ChangeTracker.Clear();
        var references = await db.IntegrationJobs.AsNoTracking().Where(x => x.TenantId == tenant.Id && x.JobType == MarketplaceJobTypes.ReferenceSync).ToListAsync();
        Assert.NotEmpty(references);
        Assert.All(references, job => Assert.Equal(healthy.Id, job.ConnectionId));
        await transaction.RollbackAsync();
    }

    private static PlatformConnection NewConnection(Guid tenantId, string store) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = tenantId,
        PublicId = Guid.CreateVersion7(),
        PlatformCode = "TRENDYOL",
        Environment = "PRODUCTION",
        DisplayName = store,
        ExternalStoreId = $"{store}-{Guid.NewGuid():N}"[..40],
        Status = "ACTIVE",
        ApiVersion = "V1"
    };

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now.ToUniversalTime();
    }

    // Fails the reference list read of one connection before it reaches the database (the transaction stays usable).
    private sealed class FailingReferenceRead(Guid connectionId) : DbCommandInterceptor
    {
        public bool Armed { get; set; }
        public int Thrown { get; private set; }

        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            Fail(command);
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Fail(command);
            return ValueTask.FromResult(result);
        }

        private void Fail(DbCommand command)
        {
            if (!Armed || !command.CommandText.Contains("reference_snapshots", StringComparison.Ordinal)) return;
            if (!command.Parameters.Cast<DbParameter>().Any(parameter => parameter.Value is Guid id && id == connectionId)) return;
            Thrown++;
            throw new InvalidOperationException("Injected failure of one policy's reference read.");
        }
    }
}
