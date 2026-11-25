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
