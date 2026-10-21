using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// Migration AddJobListAndAttemptIndexes creates these indexes by hand (CONCURRENTLY); the names and shapes here
// must stay the ones its SQL uses.
public sealed class JobIndexModelTests
{
    private static AppDbContext MetadataContext() => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseNpgsql("Host=localhost;Port=5432;Database=metadata-only;Username=metadata-only;Password=metadata-only")
        .Options);

    [Fact]
    public void Job_list_index_orders_a_tenants_jobs_newest_first()
    {
        using var db = MetadataContext();
        var entity = db.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(IntegrationJob));
        Assert.NotNull(entity);

        var index = Assert.Single(entity!.GetIndexes(), candidate => candidate.GetDatabaseName() == "IX_jobs_TenantId_CreatedAt_Id");
        Assert.Equal([nameof(IntegrationJob.TenantId), nameof(IntegrationJob.CreatedAt), nameof(IntegrationJob.Id)], index.Properties.Select(property => property.Name));
        Assert.True(index.IsDescending is [false, true, true]);
        Assert.False(index.IsUnique);
    }

    [Fact]
    public void Attempt_history_index_covers_the_tenant_foreign_key()
    {
        using var db = MetadataContext();
        var entity = db.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(JobAttempt));
        Assert.NotNull(entity);

        var index = Assert.Single(entity!.GetIndexes(), candidate => candidate.GetDatabaseName() == "IX_job_attempts_TenantId_StartedAt");
        Assert.Equal([nameof(JobAttempt.TenantId), nameof(JobAttempt.StartedAt)], index.Properties.Select(property => property.Name));
        Assert.True(index.GetIncludeProperties() is [nameof(JobAttempt.JobId)]);
        Assert.DoesNotContain(entity!.GetIndexes(), candidate => candidate.GetDatabaseName() == "IX_job_attempts_TenantId");
    }
}
