using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// Live 4 Nov 2026: 24 "PRODUCT_CREATE işleminin sonucu kesinleşmeden kaldı" notices for two Shopify products whose
// ambiguous publishes a later publish had superseded and put live. The superseded publish's effect record is settled with
// its job, and a record the sweep already closed as unresolved gets its notice resolved; a publish no later success
// answers keeps its open record.
[Collection("postgres-integration")]
public sealed class PublicationEffectSettlementPostgreSqlTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>, IAsyncLifetime
{
    private static readonly System.Collections.Concurrent.ConcurrentBag<Guid> CreatedTenants = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        var tenantIds = CreatedTenants.ToArray();
        if (tenantIds.Length == 0) return;
        await using var db = fixture.CreateContext();
        await db.IntegrationJobs.Where(x => tenantIds.Contains(x.TenantId)).ExecuteDeleteAsync();
        await db.ExternalEffectRecords.Where(x => tenantIds.Contains(x.TenantId)).ExecuteDeleteAsync();
        await db.OperationalIssues.Where(x => x.TenantId != null && tenantIds.Contains(x.TenantId.Value)).ExecuteDeleteAsync();
    }

    [PostgreSqlFact]
    public async Task A_later_publish_success_settles_the_superseded_publishes_effect_records_and_their_notices()
    {
        var now = fixture.Now;
        var tenant = new Tenant { Id = Guid.CreateVersion7(), Code = $"effect-settle-{Guid.NewGuid():N}", DisplayName = "effect-settle", Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = now, UpdatedAt = now, Version = 1 };
        CreatedTenants.Add(tenant.Id);
        var connection = new PlatformConnection { Id = Guid.CreateVersion7(), TenantId = tenant.Id, PublicId = Guid.CreateVersion7(), PlatformCode = "SHOPIFY", Environment = "PRODUCTION", DisplayName = "Shopify", ExternalStoreId = $"store-{Guid.NewGuid():N}", Status = "ACTIVE", ApiVersion = "V1", SettingsJson = "{}" };
        var profile = Guid.CreateVersion7();
        var otherProfile = Guid.CreateVersion7();

        // Open: the ambiguous publish still in review, its record still open.
        var reviewKey = $"product-create:{connection.Id:N}:review";
        // Closed before: a job superseded earlier, its record already closed by the sweep with an open notice.
        var sweptKey = $"product-create:{connection.Id:N}:swept";
        // Unanswered: another product's ambiguous publish, with no later success.
        var unansweredKey = $"product-create:{connection.Id:N}:unanswered";
        var swept = Effect(tenant, sweptKey, now.AddDays(-9), completedAt: now.AddDays(-2), ExternalEffectReconcilePolicy.UnresolvedOutcome);
        var review = Effect(tenant, reviewKey, now.AddDays(-3), completedAt: null, outcome: null);
        var unanswered = Effect(tenant, unansweredKey, now.AddDays(-3), completedAt: null, outcome: null);
        await using (var seed = fixture.CreateContext())
        {
            seed.Tenants.Add(tenant);
            seed.PlatformConnections.Add(connection);
            seed.IntegrationJobs.AddRange(
                Job(tenant, connection, profile, reviewKey, JobStatus.ManualReview, "EXTERNAL_EFFECT_AMBIGUOUS", now.AddDays(-3)),
                Job(tenant, connection, profile, sweptKey, JobStatus.Cancelled, PublicationJobSupersession.Code, now.AddDays(-9)),
                Job(tenant, connection, otherProfile, unansweredKey, JobStatus.ManualReview, "EXTERNAL_EFFECT_AMBIGUOUS", now.AddDays(-3)),
                Job(tenant, connection, profile, $"product-create:{connection.Id:N}:later", JobStatus.Succeeded, null, now.AddDays(-1)));
            seed.ExternalEffectRecords.AddRange(swept, review, unanswered);
            seed.OperationalIssues.AddRange(Notice(tenant, swept, now), Notice(tenant, unanswered, now));
            await seed.SaveChangesAsync();
        }

        await using (var db = fixture.CreateContext())
            await PublicationJobSupersession.CloseAsync(db, now, CancellationToken.None);

        await using (var verify = fixture.CreateContext())
        {
            var reviewJob = await verify.IntegrationJobs.AsNoTracking().SingleAsync(x => x.TenantId == tenant.Id && x.EffectIdempotencyKey == reviewKey);
            Assert.Equal(JobStatus.Cancelled, reviewJob.Status);
            var settledReview = await verify.ExternalEffectRecords.AsNoTracking().SingleAsync(x => x.Id == review.Id);
            Assert.Equal(ExternalEffectReconcilePolicy.SupersededOutcome, settledReview.Outcome);
            Assert.NotNull(settledReview.CompletedAt);
            var settledSwept = await verify.ExternalEffectRecords.AsNoTracking().SingleAsync(x => x.Id == swept.Id);
            Assert.Equal(ExternalEffectReconcilePolicy.SupersededOutcome, settledSwept.Outcome);
            // The sweep's closing time stays.
            Assert.True((settledSwept.CompletedAt!.Value - swept.CompletedAt!.Value).Duration() < TimeSpan.FromMilliseconds(1));
            Assert.Equal(IssueStatus.Resolved, (await verify.OperationalIssues.AsNoTracking().SingleAsync(x => x.TenantId == tenant.Id && x.DedupeKey == $"external-effect:{swept.Id}")).Status);
            // No later success answers the other product: its record stays open and its notice stays.
            var open = await verify.ExternalEffectRecords.AsNoTracking().SingleAsync(x => x.Id == unanswered.Id);
            Assert.Null(open.CompletedAt);
            Assert.Null(open.Outcome);
            Assert.Equal(IssueStatus.Open, (await verify.OperationalIssues.AsNoTracking().SingleAsync(x => x.TenantId == tenant.Id && x.DedupeKey == $"external-effect:{unanswered.Id}")).Status);
        }

        // Idempotent: nothing of this tenant is left to settle.
        await using (var again = fixture.CreateContext())
        {
            await PublicationJobSupersession.SettleCreateEffectsAsync(again, now.AddMinutes(5), CancellationToken.None);
            Assert.Equal(2, await again.ExternalEffectRecords.CountAsync(x => x.TenantId == tenant.Id && x.Outcome == ExternalEffectReconcilePolicy.SupersededOutcome));
            Assert.Equal(unanswered.Id, (await again.ExternalEffectRecords.SingleAsync(x => x.TenantId == tenant.Id && x.CompletedAt == null)).Id);
        }
    }

    private static ExternalEffectRecord Effect(Tenant tenant, string key, DateTimeOffset createdAt, DateTimeOffset? completedAt, string? outcome) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = tenant.Id,
        EffectType = MarketplaceJobTypes.ProductCreate,
        IdempotencyKey = key,
        CreatedAt = createdAt,
        CompletedAt = completedAt,
        Outcome = outcome
    };

    private static IntegrationJob Job(Tenant tenant, PlatformConnection connection, Guid profileId, string key, JobStatus status, string? errorCode, DateTimeOffset createdAt)
    {
        var id = Guid.CreateVersion7();
        return new IntegrationJob
        {
            Id = id,
            TenantId = tenant.Id,
            ConnectionId = connection.Id,
            JobType = MarketplaceJobTypes.ProductCreate,
            PayloadJson = $$"""{"ProductId":"{{Guid.CreateVersion7()}}","ProfileId":"{{profileId}}"}""",
            PayloadVersion = 1,
            PayloadHash = "h",
            JobDedupKey = key,
            EffectIdempotencyKey = key,
            Status = status,
            LastErrorCode = errorCode,
            AvailableAt = createdAt,
            CorrelationId = "publish",
            CreatedAt = createdAt,
            Version = 1
        };
    }

    private static OperationalIssue Notice(Tenant tenant, ExternalEffectRecord effect, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = tenant.Id,
        DedupeKey = $"external-effect:{effect.Id}",
        Code = "EXTERNAL_EFFECT_UNRESOLVED",
        Summary = "PRODUCT_CREATE işleminin sonucu kesinleşmeden kaldı ve kapatıldı; pazaryeri panelinden kontrol edin.",
        Status = IssueStatus.Open,
        FirstSeenAt = now.AddDays(-2),
        LastSeenAt = now.AddDays(-2),
        OccurrenceCount = 1
    };
}
