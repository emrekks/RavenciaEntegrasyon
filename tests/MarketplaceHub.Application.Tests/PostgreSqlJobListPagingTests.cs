using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// P17: the operations page reads the job list one cursor page at a time.
[Collection("postgres-integration")]
public sealed class PostgreSqlJobListPagingTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    [PostgreSqlFact]
    public async Task JobPage_WalksEveryMatchingJobOnceInCreatedOrderAndKeepsTheFilters()
    {
        var tenant = NewTenant("job-list-paging");
        var otherTenant = NewTenant("job-list-paging-other");
        // Whole seconds, so the cursor's ticks match the stored microsecond timestamps exactly.
        var baseTime = new DateTimeOffset(DateTimeOffset.UtcNow.UtcTicks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, TimeSpan.Zero).AddHours(-1);
        // Never PENDING: the lease tests in the same database take any pending job.
        var jobs = new List<IntegrationJob>();
        for (var index = 0; index < 5; index++)
            jobs.Add(NewJob(tenant.Id, baseTime.AddSeconds(index), index % 2 == 0 ? JobStatus.Succeeded : JobStatus.Dead));
        // Two jobs created in the same instant: the Id decides their order and neither may be skipped.
        jobs.Add(NewJob(tenant.Id, baseTime.AddSeconds(2), JobStatus.Succeeded));
        jobs.Add(NewJob(tenant.Id, baseTime.AddSeconds(2), JobStatus.Dead));
        var foreign = NewJob(otherTenant.Id, baseTime.AddSeconds(3), JobStatus.Succeeded);

        await using (var seed = fixture.CreateContext())
        {
            seed.Tenants.AddRange(tenant, otherTenant);
            seed.IntegrationJobs.AddRange(jobs);
            seed.IntegrationJobs.Add(foreign);
            await seed.SaveChangesAsync();
        }

        try
        {
            await using var db = fixture.CreateContext();
            var service = new JobOperationsService(db, fixture.TimeProvider);

            var all = await ReadAllPagesAsync(service, tenant.Id, new JobListQuery(Limit: 2));
            Assert.Equal(jobs.Select(job => job.Id).Order(), all.Select(job => job.Id).Order());
            Assert.Equal(all.Count, all.Select(job => job.Id).Distinct().Count());
            Assert.DoesNotContain(all, job => job.Id == foreign.Id);
            Assert.True(all.Zip(all.Skip(1)).All(pair => pair.First.CreatedAt >= pair.Second.CreatedAt));

            // The same order as the unpaged list.
            var unpaged = await service.ListAsync(tenant.Id, new JobListQuery(), CancellationToken.None);
            Assert.Equal(unpaged.Select(job => job.Id), all.Select(job => job.Id));

            var dead = await ReadAllPagesAsync(service, tenant.Id, new JobListQuery(Status: "DEAD", Limit: 1));
            Assert.Equal(jobs.Where(job => job.Status == JobStatus.Dead).Select(job => job.Id).Order(), dead.Select(job => job.Id).Order());

            var recent = await ReadAllPagesAsync(service, tenant.Id, new JobListQuery(Since: baseTime.AddSeconds(2), Limit: 2));
            Assert.Equal(jobs.Where(job => job.CreatedAt >= baseTime.AddSeconds(2)).Select(job => job.Id).Order(), recent.Select(job => job.Id).Order());

            var invalid = await service.PageAsync(tenant.Id, new JobListQuery(After: "not-a-cursor"), CancellationToken.None);
            Assert.False(invalid.Succeeded);
            Assert.Equal(400, invalid.Error!.Status);
            Assert.Equal("JOB_CURSOR_INVALID", invalid.Error.Code);
        }
        finally
        {
            var tenantIds = new[] { tenant.Id, otherTenant.Id };
            await using var cleanup = fixture.CreateContext();
            await cleanup.JobAttempts.Where(x => tenantIds.Contains(x.TenantId)).ExecuteDeleteAsync();
            await cleanup.IntegrationJobs.Where(x => tenantIds.Contains(x.TenantId)).ExecuteDeleteAsync();
            await cleanup.IntegrationOutboxEvents.Where(x => tenantIds.Contains(x.TenantId)).ExecuteDeleteAsync();
            await cleanup.Tenants.Where(x => tenantIds.Contains(x.Id)).ExecuteDeleteAsync();
        }
    }

    [PostgreSqlFact]
    public async Task JobPageAndCounts_KeepOnlyTheChosenPlatformsConnections()
    {
        var tenant = NewTenant("job-list-platform");
        var trendyol = NewConnection(tenant, "TRENDYOL");
        var efaturam = NewConnection(tenant, "TRENDYOL_EFATURAM");
        var n11 = NewConnection(tenant, "N11");
        var baseTime = new DateTimeOffset(DateTimeOffset.UtcNow.UtcTicks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, TimeSpan.Zero).AddHours(-1);
        var trendyolJobs = Enumerable.Range(0, 3).Select(index => NewJob(tenant.Id, baseTime.AddSeconds(index), JobStatus.Succeeded, trendyol.Id)).ToList();
        var efaturamJob = NewJob(tenant.Id, baseTime.AddSeconds(10), JobStatus.Dead, efaturam.Id);
        var n11Job = NewJob(tenant.Id, baseTime.AddSeconds(11), JobStatus.Succeeded, n11.Id);
        var unconnected = NewJob(tenant.Id, baseTime.AddSeconds(12), JobStatus.Succeeded);

        await using (var seed = fixture.CreateContext())
        {
            seed.Tenants.Add(tenant);
            seed.PlatformConnections.AddRange(trendyol, efaturam, n11);
            seed.IntegrationJobs.AddRange(trendyolJobs);
            seed.IntegrationJobs.AddRange(efaturamJob, n11Job, unconnected);
            await seed.SaveChangesAsync();
        }

        try
        {
            await using var db = fixture.CreateContext();
            var service = new JobOperationsService(db, fixture.TimeProvider);

            // Paged across two pages, the platform filter holds on the second page too.
            var trendyolPage = await ReadAllPagesAsync(service, tenant.Id, new JobListQuery(Limit: 2, Platforms: ["trendyol"]));
            Assert.Equal(trendyolJobs.Select(job => job.Id).Order(), trendyolPage.Select(job => job.Id).Order());

            var twoPlatforms = await ReadAllPagesAsync(service, tenant.Id, new JobListQuery(Platforms: ["TRENDYOL_EFATURAM", "N11"]));
            Assert.Equal(new[] { efaturamJob.Id, n11Job.Id }.Order(), twoPlatforms.Select(job => job.Id).Order());

            // A job without a connection shows only without a platform choice.
            var everything = await ReadAllPagesAsync(service, tenant.Id, new JobListQuery());
            Assert.Contains(everything, job => job.Id == unconnected.Id);
            Assert.Equal(6, everything.Count);

            var counts = await service.CountAsync(tenant.Id, baseTime.AddMinutes(-1), ["TRENDYOL_EFATURAM"], CancellationToken.None);
            var count = Assert.Single(counts);
            Assert.Equal("DEAD", count.Status);
            Assert.Equal(1, count.Count);
            var allCounts = await service.CountAsync(tenant.Id, baseTime.AddMinutes(-1), null, CancellationToken.None);
            Assert.Equal(6, allCounts.Sum(row => row.Count));
        }
        finally
        {
            await using var cleanup = fixture.CreateContext();
            await cleanup.JobAttempts.Where(x => x.TenantId == tenant.Id).ExecuteDeleteAsync();
            await cleanup.IntegrationJobs.Where(x => x.TenantId == tenant.Id).ExecuteDeleteAsync();
            await cleanup.PlatformConnections.Where(x => x.TenantId == tenant.Id).ExecuteDeleteAsync();
            await cleanup.IntegrationOutboxEvents.Where(row => row.TenantId == tenant.Id).ExecuteDeleteAsync();
            await cleanup.Tenants.Where(x => x.Id == tenant.Id).ExecuteDeleteAsync();
        }
    }

    private static PlatformConnection NewConnection(Tenant tenant, string platformCode) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = tenant.Id,
        PublicId = Guid.CreateVersion7(),
        PlatformCode = platformCode,
        Environment = "PRODUCTION",
        DisplayName = $"{platformCode} iş listesi testi",
        ExternalStoreId = $"store-{Guid.NewGuid():N}",
        Status = "ACTIVE",
        ApiVersion = "V1",
        SettingsJson = "{}"
    };

    private static async Task<List<JobSummaryView>> ReadAllPagesAsync(JobOperationsService service, Guid tenantId, JobListQuery query)
    {
        var items = new List<JobSummaryView>();
        string? after = null;
        for (var guard = 0; guard < 50; guard++)
        {
            var page = await service.PageAsync(tenantId, query with { After = after }, CancellationToken.None);
            Assert.True(page.Succeeded, page.Error?.Message);
            Assert.True(page.Value!.Items.Count <= query.EffectiveLimit);
            items.AddRange(page.Value.Items);
            if (!page.Value.HasMore)
            {
                Assert.Null(page.Value.NextCursor);
                return items;
            }
            Assert.NotNull(page.Value.NextCursor);
            after = page.Value.NextCursor;
        }
        throw new InvalidOperationException("The job list did not reach its last page.");
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

    private static IntegrationJob NewJob(Guid tenantId, DateTimeOffset createdAt, JobStatus status, Guid? connectionId = null)
    {
        var suffix = Guid.NewGuid().ToString("N");
        return new IntegrationJob
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            ConnectionId = connectionId,
            JobType = "TEST_LIST_PAGE",
            PayloadJson = "{}",
            PayloadVersion = 1,
            PayloadHash = "test-payload-hash",
            JobDedupKey = $"list-page-{suffix}",
            EffectIdempotencyKey = $"effect-{suffix}",
            Priority = 1,
            Status = status,
            AvailableAt = createdAt,
            MaxAttempts = 3,
            CorrelationId = $"corr-{suffix}",
            CreatedAt = createdAt,
            CompletedAt = createdAt.AddSeconds(1),
            Version = 1
        };
    }
}
