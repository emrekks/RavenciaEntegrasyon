using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Jobs;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// KUYRUK-8: the one job factory and the enqueuer's dedup policies.
public sealed class JobEnqueuerTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AMinimalRequestKeepsTheEntityDefaults()
    {
        var job = IntegrationJobFactory.New(new IntegrationJobRequest(Guid.Empty, null, "TEST", "{\"a\":1}", "key", "corr", Now));
        var plain = new IntegrationJob { JobType = "TEST", PayloadJson = "{}", PayloadHash = "", JobDedupKey = "", EffectIdempotencyKey = "", CorrelationId = "" };
        Assert.NotEqual(Guid.Empty, job.Id);
        Assert.Equal(1, job.PayloadVersion);
        Assert.Equal(1, job.Version);
        Assert.Equal("key", job.EffectIdempotencyKey);
        Assert.Equal(plain.Priority, job.Priority);
        Assert.Equal(plain.TriggerType, job.TriggerType);
        Assert.Equal(plain.MaxAttempts, job.MaxAttempts);
        Assert.Equal(plain.CreatedAt, job.CreatedAt);
        Assert.Equal(plain.ResourceType, job.ResourceType);
        Assert.Equal(plain.OperationType, job.OperationType);
        Assert.Equal(JobStatus.Pending, job.Status);
        Assert.Null(job.LockGroup);
        Assert.Equal(Now, job.AvailableAt);
        // The hash every writer stored: SHA-256 of the UTF-8 payload in upper-case hex.
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData("{\"a\":1}"u8.ToArray())), job.PayloadHash);
    }

    [Fact]
    public void EveryRequestedFieldIsWritten()
    {
        var id = Guid.CreateVersion7();
        var connection = Guid.CreateVersion7();
        var job = IntegrationJobFactory.New(new IntegrationJobRequest(Guid.CreateVersion7(), connection, "TEST", "{}", "key", "corr", Now)
        {
            Id = id,
            EffectIdempotencyKey = "effect",
            Priority = -3,
            TriggerType = "user",
            MaxAttempts = 1,
            CreatedAt = Now.AddSeconds(-1),
            LockGroup = "orders",
            ResourceType = "invoices",
            OperationType = "op"
        });
        Assert.Equal(id, job.Id);
        Assert.Equal(connection, job.ConnectionId);
        Assert.Equal("effect", job.EffectIdempotencyKey);
        Assert.Equal(-3, job.Priority);
        Assert.Equal("user", job.TriggerType);
        Assert.Equal(1, job.MaxAttempts);
        Assert.Equal(Now.AddSeconds(-1), job.CreatedAt);
        Assert.Equal("orders", job.LockGroup);
        Assert.Equal("invoices", job.ResourceType);
        Assert.Equal("op", job.OperationType);
    }

    [Fact]
    public void ACallerCheckedJobIsAddedToTheUnitOfWorkUnsaved()
    {
        using var db = new MarketplaceHub.Infrastructure.Persistence.AppDbContext(new DbContextOptionsBuilder<MarketplaceHub.Infrastructure.Persistence.AppDbContext>().UseNpgsql("Host=localhost;Database=unused").Options);
        var job = JobEnqueuer.Add(db, new IntegrationJobRequest(Guid.CreateVersion7(), null, "TEST", "{}", "key", "corr", Now));
        Assert.Equal(EntityState.Added, db.Entry(job).State);
    }
}

[Collection("postgres-integration")]
public sealed class JobEnqueuerPostgreSqlTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    // ReuseAnyWithKey: a row with the same tenant, type and key in any status is the job; another type or key is not.
    [PostgreSqlFact]
    public async Task ReuseAnyWithKeyReturnsTheExistingJobInEveryStatus()
    {
        var tenantId = Guid.CreateVersion7();
        await using (var setup = fixture.CreateContext())
        {
            setup.Tenants.Add(new Tenant { Id = tenantId, Code = $"enq-{Guid.NewGuid():N}", DisplayName = "enq", Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow, Version = 1 });
            await setup.SaveChangesAsync();
        }
        try
        {
            foreach (var status in new[] { JobStatus.Pending, JobStatus.Succeeded, JobStatus.Dead, JobStatus.Cancelled })
            {
                var key = $"enq-{status}-{Guid.NewGuid():N}";
                var request = new IntegrationJobRequest(tenantId, null, "TEST_ENQUEUE", "{}", key, "corr", DateTimeOffset.UtcNow);
                Guid firstId;
                await using (var db = fixture.CreateContext())
                {
                    var first = await JobEnqueuer.EnqueueAsync(db, request, JobDedupPolicy.ReuseAnyWithKey, CancellationToken.None);
                    Assert.True(first.Created);
                    first.Job!.Status = status;
                    await db.SaveChangesAsync();
                    firstId = first.JobId;
                }
                await using (var db = fixture.CreateContext())
                {
                    var again = await JobEnqueuer.EnqueueAsync(db, request, JobDedupPolicy.ReuseAnyWithKey, CancellationToken.None);
                    Assert.False(again.Created);
                    Assert.Equal(firstId, again.JobId);
                    Assert.Empty(db.ChangeTracker.Entries<IntegrationJob>());
                    var otherType = await JobEnqueuer.EnqueueAsync(db, request with { JobType = "TEST_ENQUEUE_OTHER" }, JobDedupPolicy.ReuseAnyWithKey, CancellationToken.None);
                    Assert.True(otherType.Created);
                    var callerChecked = await JobEnqueuer.EnqueueAsync(db, request with { DedupKey = key + ":2" }, JobDedupPolicy.CallerChecked, CancellationToken.None);
                    Assert.True(callerChecked.Created);
                    await db.SaveChangesAsync();
                }
            }
        }
        finally
        {
            await using var cleanup = fixture.CreateContext();
            await cleanup.IntegrationJobs.Where(x => x.TenantId == tenantId).ExecuteDeleteAsync();
            await cleanup.Tenants.Where(x => x.Id == tenantId).ExecuteDeleteAsync();
        }
    }
}
