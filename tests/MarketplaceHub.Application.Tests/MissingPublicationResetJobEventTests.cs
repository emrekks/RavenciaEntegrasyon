using System.Text.Json;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Jobs;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// J6 (15 Nov 2026): when Trendyol confirms a publication missing, the waiting approval checks of that product are closed
// (PRODUCT_MISSING_RESET). They were closed without a "jobs" event, so an İşlemler screen open in another tab kept them
// queued; now one bulk event goes out for all of them, and none when there was nothing to close.
public sealed class MissingPublicationResetJobEventTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Tenant = Guid.CreateVersion7();

    [Fact]
    public void ClosingTheWaitingApprovalChecksPublishesOneJobsEvent()
    {
        using var db = Context();
        var current = Job(MarketplaceJobTypes.ProductApprovalReconcile, JobStatus.Leased);
        var waiting = Job(MarketplaceJobTypes.ProductApprovalReconcile, JobStatus.Pending);
        var retrying = Job(MarketplaceJobTypes.ProductApprovalReconcile, JobStatus.RetryScheduled);
        var finished = Job(MarketplaceJobTypes.ProductApprovalReconcile, JobStatus.Succeeded);
        var create = Job(MarketplaceJobTypes.ProductCreate, JobStatus.Succeeded);

        var closed = ProductJobService.CloseApprovalChecksOfMissingPublication(db, Tenant, [current, waiting, retrying, finished, create], current.Id, Now);

        Assert.Equal(2, closed);
        Assert.Equal(JobStatus.Cancelled, waiting.Status);
        Assert.Equal(JobStatus.Cancelled, retrying.Status);
        Assert.Equal("PRODUCT_MISSING_RESET", waiting.LastErrorCode);
        Assert.Equal(JobStatus.Leased, current.Status);
        Assert.Equal(JobStatus.Succeeded, finished.Status);
        var events = db.ChangeTracker.Entries<IntegrationOutboxEvent>().Select(entry => entry.Entity).ToList();
        var bulk = Assert.Single(events);
        Assert.Equal(JobStateMachine.JobEventResource, bulk.ResourceType);
        Assert.Equal(JobStateMachine.BulkOperation, bulk.OperationType);
        Assert.Equal(Tenant, bulk.TenantId);
        using var payload = JsonDocument.Parse(bulk.PayloadJson);
        Assert.Equal("CANCELLED", payload.RootElement.GetProperty("status").GetString());
        Assert.Equal(2, payload.RootElement.GetProperty("count").GetInt32());
    }

    [Fact]
    public void NothingToCloseRaisesNoEvent()
    {
        using var db = Context();
        var current = Job(MarketplaceJobTypes.ProductApprovalReconcile, JobStatus.Leased);
        Assert.Equal(0, ProductJobService.CloseApprovalChecksOfMissingPublication(db, Tenant, [current], current.Id, Now));
        Assert.Empty(db.ChangeTracker.Entries<IntegrationOutboxEvent>());
    }

    private static IntegrationJob Job(string jobType, JobStatus status) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = Tenant,
        JobType = jobType,
        PayloadJson = "{}",
        PayloadHash = "hash",
        JobDedupKey = $"key-{Guid.NewGuid():N}",
        EffectIdempotencyKey = $"effect-{Guid.NewGuid():N}",
        Status = status,
        CorrelationId = "j6",
        CreatedAt = Now,
        AvailableAt = Now,
        Version = 1
    };

    private static AppDbContext Context() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql("Host=localhost;Database=unused").Options);
}
