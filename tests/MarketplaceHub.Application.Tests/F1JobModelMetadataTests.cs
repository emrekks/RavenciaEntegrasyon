using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace MarketplaceHub.Application.Tests;

public sealed class F1JobModelMetadataTests
{
    [Fact]
    public void Job_model_has_binding_retry_state_attempt_and_schedule_contract()
    {
        using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql("Host=localhost;Database=metadata;Username=none;Password=none").Options);
        var entity = db.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(IntegrationJob))!;
        var converter = entity.FindProperty(nameof(IntegrationJob.Status))!.GetValueConverter()!;

        Assert.Equal("PENDING", converter.ConvertToProvider(JobStatus.Pending));
        Assert.Equal("RETRY_SCHEDULED", converter.ConvertToProvider(JobStatus.RetryScheduled));
        Assert.Equal(JobRetryPolicy.DefaultMaxAttempts, entity.FindProperty(nameof(IntegrationJob.MaxAttempts))!.GetDefaultValue());
        Assert.Contains(entity.GetCheckConstraints(), constraint => constraint.Name == "ck_job_attempt_bounds");
        Assert.Contains(entity.GetIndexes(), index => index.Properties.Select(property => property.Name).SequenceEqual(new[] { "Status", "Priority", "AvailableAt", "CreatedAt" }));
    }
}
