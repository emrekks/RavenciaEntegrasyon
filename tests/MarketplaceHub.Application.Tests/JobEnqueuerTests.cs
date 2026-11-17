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

    // Every new job row is built by IntegrationJobFactory: nothing else in src constructs an IntegrationJob.
    [Fact]
    public void OnlyTheFactoryConstructsAJob()
    {
        string? root = null;
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null && root is null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "MarketplaceHub.sln"))) root = directory.FullName;
        Assert.NotNull(root);
        var construction = new System.Text.RegularExpressions.Regex(@"new\s+IntegrationJob\b(?!\w)|IntegrationJob\s+\w+\s*=\s*new\s*\(|=>\s*new\s*\(\)\s*\{\s*Id\s*=.*JobDedupKey");
        var offenders = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.EndsWith("JobEnqueueContracts.cs", StringComparison.Ordinal))
            .SelectMany(path => File.ReadLines(path).Select((line, index) => (path, line, index)))
            .Where(item => construction.IsMatch(item.line))
            .Select(item => $"{Path.GetRelativePath(root, item.path)}:{item.index + 1}")
            .ToList();
        Assert.Empty(offenders);
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

    // ReuseHeldElseRetire, the rule the panel's price and stock sends kept in InventoryService.StandingPriceInventoryJobAsync
    // (Q-1/K-1): a job that still holds its key (queued, running, retry waiting, succeeded) is the answer, nothing is added
    // and nothing changes; a failed one gives up its key ("{key}:retired:{id}", one version up) in the same save as the
    // new job; no job at all adds one.
    [PostgreSqlFact]
    public async Task ReuseHeldElseRetireReusesAHoldingJobAndRetiresAFailedOne()
    {
        var tenantId = await NewTenantAsync();
        try
        {
            foreach (var status in Enum.GetValues<JobStatus>())
            {
                var key = $"held-{status}-{Guid.NewGuid():N}";
                var request = new IntegrationJobRequest(tenantId, null, "TEST_HELD", "{}", key, "corr", DateTimeOffset.UtcNow);
                Guid firstId;
                await using (var db = fixture.CreateContext())
                {
                    var first = await JobEnqueuer.EnqueueAsync(db, request, JobDedupPolicy.ReuseHeldElseRetire, CancellationToken.None);
                    Assert.True(first.Created);
                    first.Job!.Status = status;
                    await db.SaveChangesAsync();
                    firstId = first.JobId;
                }
                var holds = status is JobStatus.Pending or JobStatus.Leased or JobStatus.RetryScheduled or JobStatus.Succeeded;
                Assert.Equal(holds, PriceInventoryOutboxPolicy.HoldsDedupKey(status));
                await using (var db = fixture.CreateContext())
                {
                    var again = await JobEnqueuer.EnqueueAsync(db, request with { Id = Guid.CreateVersion7() }, JobDedupPolicy.ReuseHeldElseRetire, CancellationToken.None);
                    Assert.Equal(!holds, again.Created);
                    if (holds)
                    {
                        Assert.Equal(firstId, again.JobId);
                        Assert.DoesNotContain(db.ChangeTracker.Entries<IntegrationJob>(), entry => entry.State != EntityState.Unchanged);
                    }
                    else
                    {
                        Assert.NotEqual(firstId, again.JobId);
                        var retired = Assert.Single(db.ChangeTracker.Entries<IntegrationJob>(), entry => entry.State == EntityState.Modified).Entity;
                        Assert.Equal(firstId, retired.Id);
                        Assert.Equal($"{key}:retired:{firstId:N}", retired.JobDedupKey);
                        Assert.Equal(2, retired.Version);
                    }
                    await db.SaveChangesAsync();
                }
                await using (var db = fixture.CreateContext())
                {
                    var rows = await db.IntegrationJobs.AsNoTracking().Where(x => x.TenantId == tenantId && x.JobDedupKey.StartsWith(key)).ToListAsync();
                    Assert.Equal(holds ? 1 : 2, rows.Count);
                    Assert.Single(rows, x => x.JobDedupKey == key);
                }
            }
        }
        finally
        {
            await DeleteTenantAsync(tenantId);
        }
    }

    // KeyTakenAsync, the first check of the sites that run a check of their own before they add a job
    // (InvoicingJobProcessor.EnqueueAutomaticJob, ReferenceSnapshotBackfill): the same tenant, type and key in any status.
    [PostgreSqlFact]
    public async Task KeyTakenFindsTheKeyInEveryStatusAndNothingElse()
    {
        var tenantId = await NewTenantAsync();
        try
        {
            foreach (var status in Enum.GetValues<JobStatus>())
            {
                var key = $"taken-{status}-{Guid.NewGuid():N}";
                await using var db = fixture.CreateContext();
                var job = JobEnqueuer.Add(db, new IntegrationJobRequest(tenantId, null, "TEST_TAKEN", "{}", key, "corr", DateTimeOffset.UtcNow));
                job.Status = status;
                await db.SaveChangesAsync();
                Assert.True(await JobEnqueuer.KeyTakenAsync(db, tenantId, "TEST_TAKEN", key, CancellationToken.None));
                Assert.False(await JobEnqueuer.KeyTakenAsync(db, tenantId, "TEST_TAKEN_OTHER", key, CancellationToken.None));
                Assert.False(await JobEnqueuer.KeyTakenAsync(db, tenantId, "TEST_TAKEN", key + ":2", CancellationToken.None));
                Assert.False(await JobEnqueuer.KeyTakenAsync(db, Guid.CreateVersion7(), "TEST_TAKEN", key, CancellationToken.None));
            }
        }
        finally
        {
            await DeleteTenantAsync(tenantId);
        }
    }

    private async Task<Guid> NewTenantAsync()
    {
        var tenantId = Guid.CreateVersion7();
        await using var setup = fixture.CreateContext();
        setup.Tenants.Add(new Tenant { Id = tenantId, Code = $"enq-{Guid.NewGuid():N}", DisplayName = "enq", Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow, Version = 1 });
        await setup.SaveChangesAsync();
        return tenantId;
    }

    private async Task DeleteTenantAsync(Guid tenantId)
    {
        await using var cleanup = fixture.CreateContext();
        await cleanup.IntegrationJobs.Where(x => x.TenantId == tenantId).ExecuteDeleteAsync();
        await cleanup.Tenants.Where(x => x.Id == tenantId).ExecuteDeleteAsync();
    }
}
