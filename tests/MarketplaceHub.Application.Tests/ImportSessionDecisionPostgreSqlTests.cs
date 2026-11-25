using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Imports;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// Scan 25 Nov 2026 (E-4): a decision changed after every candidate was decided answered 500 (a READY_TO_APPLY ->
// READY_TO_APPLY transition), and one file column mapped twice broke the profile's unique index or the preview.
[Collection("postgres-integration")]
public sealed class ImportSessionDecisionPostgreSqlTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    private ImportService Service(AppDbContext db) =>
        new(db, null!, new CursorCodec(new EphemeralDataProtectionProvider(), TimeProvider.System), fixture.TimeProvider, new ConfigurationBuilder().Build());

    private async Task<(Guid TenantId, ImportSession Session, ImportMatchCandidate Candidate)> SeedAsync(string code, ImportSessionStatus status)
    {
        var now = fixture.Now;
        var tenant = new Tenant { Id = Guid.CreateVersion7(), Code = $"{code}-{Guid.NewGuid():N}", DisplayName = code, Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = now, UpdatedAt = now, Version = 1 };
        var asset = new FileAsset { Id = Guid.CreateVersion7(), TenantId = tenant.Id, Classification = "IMPORT_SOURCE", RelativePath = $"{Guid.NewGuid():N}.csv", OriginalNameSafe = "urunler.csv", MimeType = "text/csv", SizeBytes = 10, Sha256 = Guid.NewGuid().ToString("N"), Status = "ACTIVE", CreatedAt = now };
        var session = new ImportSession { Id = Guid.CreateVersion7(), TenantId = tenant.Id, SourceType = ImportSourceType.Csv, SourceAssetId = asset.Id, Status = status, CreatedAt = now, UpdatedAt = now };
        var staging = new ImportStagingRecord { Id = Guid.CreateVersion7(), TenantId = tenant.Id, SessionId = session.Id, RowNumber = 2, RawJson = "{}", SafeValuesJson = "{}", ValidationErrorsJson = "[]", RowHash = $"hash-{Guid.NewGuid():N}", ReviewStatus = "PENDING" };
        var candidate = new ImportMatchCandidate { Id = Guid.CreateVersion7(), TenantId = tenant.Id, SessionId = session.Id, StagingRecordId = staging.Id, MatchRule = "NEW", Status = "PENDING", SafeSummary = "{}" };
        await using var db = fixture.CreateContext();
        db.Tenants.Add(tenant);
        db.FileAssets.Add(asset);
        db.ImportSessions.Add(session);
        db.ImportStagingRecords.Add(staging);
        db.ImportMatchCandidates.Add(candidate);
        await db.SaveChangesAsync();
        return (tenant.Id, session, candidate);
    }

    [PostgreSqlFact]
    public async Task ADecision_CanBeChangedWhileReadyToApply_AndIsRefusedOnceApplying()
    {
        var (tenantId, session, candidate) = await SeedAsync("import-decide", ImportSessionStatus.ReviewRequired);
        var userId = Guid.CreateVersion7();

        await using (var db = fixture.CreateContext())
        {
            var first = await Service(db).DecideAsync(tenantId, userId, session.Id, candidate.Id, candidate.Version, new ImportDecisionCommand("CREATE", null, null), CancellationToken.None);
            Assert.True(first.Succeeded);
        }
        await using (var db = fixture.CreateContext())
            Assert.Equal(ImportSessionStatus.ReadyToApply, (await db.ImportSessions.AsNoTracking().SingleAsync(x => x.Id == session.Id)).Status);

        // Changing the only decision while the session is ready to apply keeps it ready instead of a 500.
        await using (var db = fixture.CreateContext())
        {
            var version = (await db.ImportMatchCandidates.AsNoTracking().SingleAsync(x => x.Id == candidate.Id)).Version;
            var changed = await Service(db).DecideAsync(tenantId, userId, session.Id, candidate.Id, version, new ImportDecisionCommand("SKIP", null, null), CancellationToken.None);
            Assert.True(changed.Succeeded, changed.Error?.Message);
        }
        await using (var db = fixture.CreateContext())
        {
            Assert.Equal(ImportSessionStatus.ReadyToApply, (await db.ImportSessions.AsNoTracking().SingleAsync(x => x.Id == session.Id)).Status);
            Assert.Equal(ImportDecisionKind.Skip, (await db.ImportDecisions.AsNoTracking().SingleAsync(x => x.CandidateId == candidate.Id)).Decision);
            await db.ImportSessions.Where(x => x.Id == session.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, ImportSessionStatus.Applying));
        }

        // Once applying has started a decision is a 409, never a state-machine exception.
        await using (var db = fixture.CreateContext())
        {
            var version = (await db.ImportMatchCandidates.AsNoTracking().SingleAsync(x => x.Id == candidate.Id)).Version;
            var refused = await Service(db).DecideAsync(tenantId, userId, session.Id, candidate.Id, version, new ImportDecisionCommand("CREATE", null, null), CancellationToken.None);
            Assert.False(refused.Succeeded);
            Assert.Equal((409, "IMPORT_DECISIONS_CLOSED"), (refused.Error!.Status, refused.Error.Code));
        }
    }

    // A changed decision while Uygula's job waits in the queue or runs would raise the session's version, and the next
    // Uygula would open a second IMPORT_APPLY job: it is a 409 until that job has ended.
    [PostgreSqlFact]
    public async Task ADecision_IsRefusedWhileTheApplyJobIsQueuedOrLeased_AndOpenAgainOnceItEnded()
    {
        var (tenantId, session, candidate) = await SeedAsync("import-apply-queued", ImportSessionStatus.ReviewRequired);
        var userId = Guid.CreateVersion7();
        await using (var db = fixture.CreateContext())
            Assert.True((await Service(db).DecideAsync(tenantId, userId, session.Id, candidate.Id, candidate.Version, new ImportDecisionCommand("CREATE", null, null), CancellationToken.None)).Succeeded);
        Guid jobId;
        await using (var db = fixture.CreateContext())
        {
            var enqueued = await Service(db).EnqueueApplyAsync(tenantId, session.Id, "corr-e4", CancellationToken.None);
            Assert.True(enqueued.Succeeded, enqueued.Error?.Message);
            jobId = enqueued.Value;
        }

        foreach (var status in new[] { JobStatus.Pending, JobStatus.Leased })
        {
            await using (var db = fixture.CreateContext())
                await db.IntegrationJobs.Where(x => x.Id == jobId).ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, status));
            await using (var db = fixture.CreateContext())
            {
                var version = (await db.ImportMatchCandidates.AsNoTracking().SingleAsync(x => x.Id == candidate.Id)).Version;
                var refused = await Service(db).DecideAsync(tenantId, userId, session.Id, candidate.Id, version, new ImportDecisionCommand("SKIP", null, null), CancellationToken.None);
                Assert.False(refused.Succeeded);
                Assert.Equal((409, "IMPORT_APPLY_QUEUED"), (refused.Error!.Status, refused.Error.Code));
            }
        }
        await using (var db = fixture.CreateContext())
        {
            Assert.Equal(ImportDecisionKind.Create, (await db.ImportDecisions.AsNoTracking().SingleAsync(x => x.CandidateId == candidate.Id)).Decision);
            Assert.Equal(1, await db.IntegrationJobs.CountAsync(x => x.TenantId == tenantId && x.JobType == "IMPORT_APPLY"));
            await db.IntegrationJobs.Where(x => x.Id == jobId).ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, JobStatus.Dead));
        }

        // The job ended without applying: the decision can be changed again.
        await using (var db = fixture.CreateContext())
        {
            var version = (await db.ImportMatchCandidates.AsNoTracking().SingleAsync(x => x.Id == candidate.Id)).Version;
            var changed = await Service(db).DecideAsync(tenantId, userId, session.Id, candidate.Id, version, new ImportDecisionCommand("SKIP", null, null), CancellationToken.None);
            Assert.True(changed.Succeeded, changed.Error?.Message);
        }
    }

    [PostgreSqlFact]
    public async Task OneFileColumn_MappedToTwoFields_IsRefusedWith422()
    {
        var (tenantId, session, _) = await SeedAsync("import-columns", ImportSessionStatus.Created);
        await using var db = fixture.CreateContext();
        var version = (await db.ImportSessions.AsNoTracking().SingleAsync(x => x.Id == session.Id)).Version;

        var result = await Service(db).ConfigureColumnsAsync(tenantId, session.Id, version,
            new UpdateColumnMappingCommand("Profil", null, [new ColumnMappingEntry("Ürün Adı", "title", 0), new ColumnMappingEntry(" ürün adı ", "sku", 1)]), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal((422, "VALIDATION_FAILED"), (result.Error!.Status, result.Error.Code));
    }
}
