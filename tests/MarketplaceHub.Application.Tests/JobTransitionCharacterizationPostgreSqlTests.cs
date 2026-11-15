using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// KUYRUK-7 (15 Nov 2026): what each job status transition outside the lease service writes today (status, times,
// lease fields, error fields, counters and the "jobs" outbox event), pinned before the transitions move into
// JobStateMachine.
[Collection("postgres-integration")]
public sealed class JobTransitionCharacterizationPostgreSqlTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    [PostgreSqlFact]
    public async Task AManualRetryReschedulesAStoppedJobAndClearsItsErrorAndLease()
    {
        var tenant = NewTenant("tr-retry");
        var job = NewJob(tenant.Id, "TEST_RETRY");
        job.Status = JobStatus.Dead;
        job.AttemptCount = 3;
        job.MaxAttempts = 3;
        job.CompletedAt = DateTimeOffset.UtcNow.AddHours(-1);
        job.LastErrorCode = "REMOTE_TIMEOUT";
        job.LastErrorSummary = "zaman aşımı";
        job.ConflictCount = -3;
        job.WaitingChecks = 2;
        await SeedAsync(tenant, job);
        try
        {
            var before = DateTimeOffset.UtcNow;
            await using (var db = fixture.CreateContext())
                Assert.True((await new JobOperationsService(db, fixture.TimeProvider).RetryAsync(tenant.Id, job.Id, CancellationToken.None)).Succeeded);

            await using var verification = fixture.CreateContext();
            var stored = await verification.IntegrationJobs.AsNoTracking().SingleAsync(x => x.Id == job.Id);
            Assert.Equal(JobStatus.RetryScheduled, stored.Status);
            Assert.Null(stored.CompletedAt);
            Assert.Null(stored.LastErrorCode);
            Assert.Null(stored.LastErrorSummary);
            Assert.Null(stored.LeaseTokenHash);
            Assert.True(stored.AvailableAt >= before.AddSeconds(-1));
            Assert.NotNull(stored.RetriedAt);
            Assert.Equal(4, stored.MaxAttempts);
            Assert.Equal(3, stored.AttemptCount);
            Assert.Equal(2, stored.Version);
            // The waiting checks stay: MaxAttempts - WaitingChecks is the job's own attempt budget.
            Assert.Equal(2, stored.WaitingChecks);
            // KUYRUK-7 fixes: the retry starts a new run of conflicts, and the panel hears of it.
            Assert.Equal(0, stored.ConflictCount);
            Assert.Equal(1, await JobEventsAsync(verification, job.Id));
        }
        finally
        {
            await CleanupAsync(tenant.Id);
        }
    }

    // A job sent to review for a duplicate insert that kept coming back, and retried by the operator, gets the duplicate
    // retries of a new job: one duplicate after the retry is not "a duplicate that keeps coming back" (JobConflictPolicy).
    [PostgreSqlFact]
    public async Task ARetriedJobGetsItsDuplicateInsertRetriesAgain()
    {
        var tenant = NewTenant("tr-retry-conflict");
        var job = NewJob(tenant.Id, "TEST_RETRY");
        job.Status = JobStatus.ManualReview;
        job.AttemptCount = 3;
        job.MaxAttempts = 6;
        job.CompletedAt = DateTimeOffset.UtcNow.AddHours(-1);
        job.LastErrorCode = Infrastructure.Jobs.JobConflictPolicy.DuplicateInsert;
        job.ConflictCount = -(Infrastructure.Jobs.JobConflictPolicy.DuplicateInsertRetries + 1);
        await SeedAsync(tenant, job);
        try
        {
            await using (var db = fixture.CreateContext())
                Assert.True((await new JobOperationsService(db, fixture.TimeProvider).RetryAsync(tenant.Id, job.Id, CancellationToken.None)).Succeeded);
            await using (var db = fixture.CreateContext())
            {
                var leases = new JobLeaseService(db, fixture.TokenHasher, fixture.TimeProvider);
                var lease = await leases.TryLeaseAsync(TimeSpan.FromMinutes(2), null, null, CancellationToken.None, onlyJobId: job.Id);
                Assert.NotNull(lease);
                var duplicate = JobExecutionResult.Retry(Infrastructure.Jobs.JobConflictPolicy.DuplicateInsert, "aynı kayıt");
                Assert.True(await leases.CompleteAsync(job.Id, lease.LeaseToken, duplicate, CancellationToken.None));
            }

            await using var verification = fixture.CreateContext();
            var stored = await verification.IntegrationJobs.AsNoTracking().SingleAsync(x => x.Id == job.Id);
            Assert.Equal(JobStatus.RetryScheduled, stored.Status);
            Assert.Equal(-1, stored.ConflictCount);
        }
        finally
        {
            await CleanupAsync(tenant.Id);
        }
    }

    [PostgreSqlFact]
    public async Task AnOperatorCancelOfAWaitingJobClosesItAndTellsThePanel()
    {
        var tenant = NewTenant("tr-cancel");
        var job = NewJob(tenant.Id, "TEST_CANCEL");
        await SeedAsync(tenant, job);
        try
        {
            await using (var db = fixture.CreateContext())
                Assert.True((await new JobOperationsService(db, fixture.TimeProvider).CancelAsync(tenant.Id, job.Id, CancellationToken.None)).Succeeded);

            await using var verification = fixture.CreateContext();
            var stored = await verification.IntegrationJobs.AsNoTracking().SingleAsync(x => x.Id == job.Id);
            Assert.Equal(JobStatus.Cancelled, stored.Status);
            Assert.NotNull(stored.CompletedAt);
            Assert.Equal("CANCELLED_BY_OPERATOR", stored.LastErrorCode);
            Assert.Equal("Job kullanıcı tarafından iptal edildi.", stored.LastErrorSummary);
            Assert.Equal(2, stored.Version);
            Assert.Equal(1, await JobEventsAsync(verification, job.Id));
        }
        finally
        {
            await CleanupAsync(tenant.Id);
        }
    }

    [PostgreSqlFact]
    public async Task AnOperatorCancelOfARunningJobClosesItsAttemptAndDropsTheLease()
    {
        var tenant = NewTenant("tr-cancel-leased");
        var job = NewJob(tenant.Id, "TEST_CANCEL");
        job.Status = JobStatus.Leased;
        job.AttemptCount = 1;
        job.LeaseTokenHash = "hash";
        job.LeaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(2);
        job.HeartbeatAt = DateTimeOffset.UtcNow;
        await SeedAsync(tenant, job, new JobAttempt { Id = Guid.NewGuid(), TenantId = tenant.Id, JobId = job.Id, AttemptNumber = 1, StartedAt = DateTimeOffset.UtcNow });
        try
        {
            await using (var db = fixture.CreateContext())
                Assert.True((await new JobOperationsService(db, fixture.TimeProvider).CancelAsync(tenant.Id, job.Id, CancellationToken.None)).Succeeded);

            await using var verification = fixture.CreateContext();
            var stored = await verification.IntegrationJobs.AsNoTracking().SingleAsync(x => x.Id == job.Id);
            Assert.Equal(JobStatus.Cancelled, stored.Status);
            Assert.Equal("Çalışan job kullanıcı tarafından durduruldu.", stored.LastErrorSummary);
            Assert.Null(stored.LeaseTokenHash);
            Assert.Null(stored.LeaseExpiresAt);
            Assert.Null(stored.HeartbeatAt);
            var attempt = await verification.JobAttempts.AsNoTracking().SingleAsync(x => x.JobId == job.Id);
            Assert.NotNull(attempt.CompletedAt);
            Assert.False(attempt.Succeeded);
            Assert.Equal("CANCELLED_BY_OPERATOR", attempt.ErrorCode);
            Assert.Equal(1, await JobEventsAsync(verification, job.Id));
        }
        finally
        {
            await CleanupAsync(tenant.Id);
        }
    }

    [PostgreSqlFact]
    public async Task ACancelledInvoiceStopsItsWaitingDeliveryJobs()
    {
        var tenant = NewTenant("tr-invoice-cancel");
        var invoiceId = Guid.NewGuid();
        var job = NewJob(tenant.Id, InvoicingJobTypes.MarketplaceDelivery);
        job.JobDedupKey = $"{InvoicingJobTypes.MarketplaceDelivery}:{invoiceId}:after-document";
        job.Status = JobStatus.RetryScheduled;
        await SeedAsync(tenant, job);
        try
        {
            await using (var db = fixture.CreateContext())
            {
                Assert.Equal(1, await InvoiceAdjustmentTracking.CancelPendingDeliveryJobsAsync(db, tenant.Id, invoiceId, DateTimeOffset.UtcNow, CancellationToken.None));
                await db.SaveChangesAsync();
            }

            await using var verification = fixture.CreateContext();
            var stored = await verification.IntegrationJobs.AsNoTracking().SingleAsync(x => x.Id == job.Id);
            Assert.Equal(JobStatus.Cancelled, stored.Status);
            Assert.NotNull(stored.CompletedAt);
            Assert.Equal(InvoiceDeliveryGuardPolicy.CancelledJobCode, stored.LastErrorCode);
            Assert.Equal("Fatura iptal sürecine girdiği için pazaryeri teslimi durduruldu.", stored.LastErrorSummary);
            Assert.Equal(2, stored.Version);
            // KUYRUK-7 fix: the job screens hear that the delivery was stopped.
            Assert.Equal(1, await JobEventsAsync(verification, job.Id));
        }
        finally
        {
            await CleanupAsync(tenant.Id);
        }
    }

    // The policy pass cancels the automatic write jobs of a connection whose write switch is off, and the scheduled
    // product reads of Trendyol and Shopify. It runs over every tenant, so it runs inside a transaction rolled back.
    [PostgreSqlFact]
    public async Task ThePolicyPassCancelsWriteJobsOfABlockedConnectionAndScheduledProductReads()
    {
        var tenant = NewTenant("tr-policy-pass");
        var connection = NewConnection(tenant);
        var write = NewJob(tenant.Id, MarketplaceJobTypes.PriceInventorySync);
        write.ConnectionId = connection.Id;
        var products = NewJob(tenant.Id, MarketplaceJobTypes.ProductSync);
        products.ConnectionId = connection.Id;
        products.JobDedupKey = $"scheduled:products:{connection.Id:N}:1";
        await using var db = fixture.CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        db.Tenants.Add(tenant);
        db.PlatformConnections.Add(connection);
        db.IntegrationJobs.AddRange(write, products);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await new ScheduledJobProducer(db, fixture.TimeProvider, new ConfigurationBuilder().Build()).MaintainPoliciesAsync(includeDefaults: false, CancellationToken.None);

        db.ChangeTracker.Clear();
        foreach (var id in new[] { write.Id, products.Id })
        {
            var stored = await db.IntegrationJobs.AsNoTracking().SingleAsync(x => x.Id == id);
            Assert.Equal(JobStatus.Cancelled, stored.Status);
            Assert.NotNull(stored.CompletedAt);
            Assert.Null(stored.LastErrorCode);
            Assert.Equal(2, stored.Version);
            // KUYRUK-7 fix: the job screens hear of the cancellation instead of showing the job waiting until a reload.
            Assert.Equal(1, await JobEventsAsync(db, id));
        }
        await transaction.RollbackAsync();
    }

    private static Task<int> JobEventsAsync(AppDbContext db, Guid jobId) =>
        db.IntegrationOutboxEvents.AsNoTracking().CountAsync(x => x.AggregateId == jobId && x.ResourceType == "jobs");

    private async Task SeedAsync(Tenant tenant, IntegrationJob job, JobAttempt? attempt = null)
    {
        await using var setup = fixture.CreateContext();
        setup.Tenants.Add(tenant);
        setup.IntegrationJobs.Add(job);
        await setup.SaveChangesAsync();
        if (attempt is null) return;
        setup.JobAttempts.Add(attempt);
        await setup.SaveChangesAsync();
    }

    private async Task CleanupAsync(Guid tenantId)
    {
        await using var cleanup = fixture.CreateContext();
        await cleanup.JobAttempts.Where(x => x.TenantId == tenantId).ExecuteDeleteAsync();
        await cleanup.IntegrationJobs.Where(x => x.TenantId == tenantId).ExecuteDeleteAsync();
        await cleanup.IntegrationOutboxEvents.Where(x => x.TenantId == tenantId).ExecuteDeleteAsync();
        await cleanup.Tenants.Where(x => x.Id == tenantId).ExecuteDeleteAsync();
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

    private static PlatformConnection NewConnection(Tenant tenant) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = tenant.Id,
        PublicId = Guid.CreateVersion7(),
        PlatformCode = "TRENDYOL",
        Environment = "PRODUCTION",
        DisplayName = "Geçiş testi",
        ExternalStoreId = $"store-{Guid.NewGuid():N}",
        Status = "ACTIVE",
        ApiVersion = "V1",
        SettingsJson = "{}"
    };

    private static IntegrationJob NewJob(Guid tenantId, string jobType)
    {
        var now = DateTimeOffset.UtcNow;
        var suffix = Guid.NewGuid().ToString("N");
        return new IntegrationJob
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            JobType = jobType,
            PayloadJson = "{}",
            PayloadVersion = 1,
            PayloadHash = "tr-payload-hash",
            JobDedupKey = $"tr-{suffix}",
            EffectIdempotencyKey = $"tr-{suffix}",
            Priority = 1,
            Status = JobStatus.Pending,
            AvailableAt = now.AddMinutes(-1),
            MaxAttempts = 3,
            CorrelationId = $"tr-{suffix}",
            CreatedAt = now,
            Version = 1
        };
    }
}
