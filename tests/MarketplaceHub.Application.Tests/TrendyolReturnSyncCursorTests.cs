using System.Text.Json;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Adapters.Trendyol;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// A16: the Trendyol claims poll answers with a per-status-bucket cursor; the RETURN_SYNC state keeps it as it came,
// so a bucket that ran out of claims is not asked again on the next page.
public sealed class TrendyolReturnSyncCursorTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 9, 0, 0, TimeSpan.Zero);

    private static ReturnReadService.ReturnSyncState StoreAndRead(ReturnReadService.ReturnSyncState state) =>
        ReturnReadService.ReadReturnSyncState(
            new SyncCursor { ResourceType = "RETURNS", OpaqueCursor = ReturnReadService.SerializeReturnSyncState(state), LastModifiedWatermark = Now.AddHours(-1) },
            Now,
            TimeSpan.FromMinutes(15));

    [Fact]
    public void BucketCursor_RoundTripsThroughTheStoredStateAcrossTwoPages()
    {
        var first = ReturnReadService.ReadReturnSyncState(new SyncCursor { ResourceType = "RETURNS" }, Now, TimeSpan.FromMinutes(15));
        Assert.Equal("0", ReturnReadService.ReturnPageCursor(first));
        Assert.Equal(TrendyolHttpClient.ClaimStatuses.Length, TrendyolHttpClient.ClaimBucketPages(ReturnReadService.ReturnPageCursor(first)).Count);

        // Page 1: only Created and Accepted had more claims.
        var afterFirst = StoreAndRead(ReturnReadService.NextReturnPage(first, TrendyolHttpClient.ClaimBucketCursor([("Created", 1), ("Accepted", 1)])));
        Assert.Equal("s:Created=1,Accepted=1", ReturnReadService.ReturnPageCursor(afterFirst));
        Assert.Equal([("Created", 1), ("Accepted", 1)], TrendyolHttpClient.ClaimBucketPages(ReturnReadService.ReturnPageCursor(afterFirst)));
        Assert.Equal(1, afterFirst.Page);

        // Page 2: Created ran out; only Accepted is asked for its third page.
        var afterSecond = StoreAndRead(ReturnReadService.NextReturnPage(afterFirst, TrendyolHttpClient.ClaimBucketCursor([("Accepted", 2)])));
        Assert.Equal([("Accepted", 2)], TrendyolHttpClient.ClaimBucketPages(ReturnReadService.ReturnPageCursor(afterSecond)));
        Assert.Equal(2, afterSecond.Page);
    }

    // B-8: "İadeleri Çek" (forceFull) continues in slices with the same payload; a slice resumes the baseline the slice
    // before saved instead of starting it again from page 0. An old or non-baseline state starts a new baseline.
    [Fact]
    public void ForcedFullRead_ResumesItsOwnBaselineAcrossSlices()
    {
        var baseline = ReturnReadService.ReadReturnSyncState(new SyncCursor { ResourceType = "RETURNS" }, Now, TimeSpan.FromMinutes(15), forceFull: true);
        Assert.Equal((0, Now.AddMonths(-3), Now), (baseline.Page, baseline.StartAt, baseline.EndAt));
        var afterSlice = ReturnReadService.NextReturnPage(baseline with { Page = 4 }, "5");
        var saved = new SyncCursor { ResourceType = "RETURNS", OpaqueCursor = ReturnReadService.SerializeReturnSyncState(afterSlice), LastModifiedWatermark = Now.AddDays(-2) };

        var resumed = ReturnReadService.ReadReturnSyncState(saved, Now.AddMinutes(1), TimeSpan.FromMinutes(15), forceFull: true);
        Assert.Equal(5, resumed.Page);
        Assert.Equal((Now.AddMonths(-3), Now), (resumed.StartAt, resumed.EndAt));

        // A baseline saved more than a day ago, or an incremental read's state, is not resumed by a forced read.
        Assert.Equal(0, ReturnReadService.ReadReturnSyncState(saved, Now.AddDays(2), TimeSpan.FromMinutes(15), forceFull: true).Page);
        var incremental = ReturnReadService.ReadReturnSyncState(new SyncCursor { ResourceType = "RETURNS", LastModifiedWatermark = Now.AddHours(-1) }, Now, TimeSpan.FromMinutes(15)) with { Page = 3 };
        var forcedOverIncremental = ReturnReadService.ReadReturnSyncState(new SyncCursor { ResourceType = "RETURNS", OpaqueCursor = ReturnReadService.SerializeReturnSyncState(incremental) }, Now, TimeSpan.FromMinutes(15), forceFull: true);
        Assert.Equal((0, Now.AddMonths(-3)), (forcedOverIncremental.Page, forcedOverIncremental.StartAt));
    }

    [Fact]
    public void NumericCursors_StillWork()
    {
        // A plain page number from an adapter is a page, not an opaque cursor.
        var numeric = ReturnReadService.NextReturnPage(ReturnReadService.ReadReturnSyncState(new SyncCursor { ResourceType = "RETURNS" }, Now, TimeSpan.FromMinutes(15)), "4");
        Assert.Null(numeric.PageCursor);
        Assert.Equal("4", ReturnReadService.ReturnPageCursor(numeric));

        // A state stored before PageCursor existed continues from its page number.
        var legacyState = $$"""{"Version":"{{ReturnReadService.ReturnSyncStateVersion}}","StoreFrontIndex":0,"Page":3,"Full":false,"StartAt":"2026-10-20T08:00:00+00:00","EndAt":"2026-10-20T09:00:00+00:00"}""";
        var legacy = ReturnReadService.ReadReturnSyncState(new SyncCursor { ResourceType = "RETURNS", OpaqueCursor = legacyState }, Now, TimeSpan.FromMinutes(15));
        Assert.Equal(3, legacy.Page);
        Assert.Equal("3", ReturnReadService.ReturnPageCursor(legacy));

        // The oldest cursors were a bare page number.
        var bare = ReturnReadService.ReadReturnSyncState(new SyncCursor { ResourceType = "RETURNS", OpaqueCursor = "5" }, Now, TimeSpan.FromMinutes(15));
        Assert.Equal("5", ReturnReadService.ReturnPageCursor(bare));
    }

    [Fact]
    public void MissingOrOversizedCursor_FallsBackToTheNextPageNumber()
    {
        var state = ReturnReadService.ReadReturnSyncState(new SyncCursor { ResourceType = "RETURNS" }, Now, TimeSpan.FromMinutes(15)) with { Page = 2, PageCursor = "s:Created=2" };

        var empty = ReturnReadService.NextReturnPage(state, null);
        Assert.Equal("3", ReturnReadService.ReturnPageCursor(empty));
        var oversized = ReturnReadService.NextReturnPage(state, "s:" + new string('x', 2_000));
        Assert.Equal("3", ReturnReadService.ReturnPageCursor(oversized));

        var stored = ReturnReadService.SerializeReturnSyncState(state with { PageCursor = new string('y', 2_000) });
        var read = ReturnReadService.ReadReturnSyncState(new SyncCursor { ResourceType = "RETURNS", OpaqueCursor = stored }, Now, TimeSpan.FromMinutes(15));
        Assert.Null(read.PageCursor);
        Assert.Equal("2", ReturnReadService.ReturnPageCursor(read));
    }

    [Fact]
    public void OnlyPermanentRefusalsOfTheSavedPageCountTowardsARestart()
    {
        var state = ReturnReadService.ReadReturnSyncState(new SyncCursor { ResourceType = "RETURNS" }, Now, TimeSpan.FromMinutes(15)) with { Page = 3, PageCursor = "s:Created=3" };
        var refused = new AdapterError(AdapterErrorClass.Validation, "TRENDYOL_BAD_REQUEST", "Geçersiz sayfa.", 400, null, null);
        var quota = new AdapterError(AdapterErrorClass.RateLimit, "REMOTE_RATE_LIMITED", "Kota.", 429, TimeSpan.FromMinutes(1), null);

        Assert.Equal((0, false), ReturnReadService.ReturnPageRefusal(state, quota));
        Assert.Equal((1, false), ReturnReadService.ReturnPageRefusal(state, refused));
        Assert.Equal((ReturnReadService.ReturnPagePermanentFailureLimit, true),
            ReturnReadService.ReturnPageRefusal(state with { PermanentFailures = ReturnReadService.ReturnPagePermanentFailureLimit - 1 }, refused));
        // The count survives the stored state.
        Assert.Equal(2, StoreAndRead(state with { PermanentFailures = 2 }).PermanentFailures);
    }
}

[Collection("postgres-integration")]
public sealed class TrendyolReturnSyncCursorPostgreSqlTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    [PostgreSqlFact]
    public async Task ReturnSync_SendsBackTheBucketCursorAndStoresItBetweenPages()
    {
        var tenant = new Tenant
        {
            Id = Guid.CreateVersion7(),
            Code = $"trendyol-return-cursor-{Guid.NewGuid():N}",
            DisplayName = "trendyol-return-cursor",
            Status = RecordStatus.Active,
            Timezone = "Europe/Istanbul",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            Version = 1
        };
        var connection = new PlatformConnection
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenant.Id,
            PublicId = Guid.CreateVersion7(),
            PlatformCode = "TRENDYOL",
            Environment = "PRODUCTION",
            DisplayName = "Trendyol iade imleci testi",
            ExternalStoreId = $"seller-{Guid.NewGuid():N}",
            Status = "ACTIVE",
            ApiVersion = "V2",
            SettingsJson = "{}"
        };
        await using (var seed = fixture.CreateContext())
        {
            seed.Tenants.Add(tenant);
            seed.PlatformConnections.Add(connection);
            await seed.SaveChangesAsync();
        }

        var port = new BucketCursorReturnPort(async () =>
        {
            // Read with a separate connection: what the processor committed after the second page.
            await using var check = fixture.CreateContext();
            return await check.SyncCursors.AsNoTracking()
                .Where(x => x.TenantId == tenant.Id && x.ConnectionId == connection.Id && x.ResourceType == "RETURNS")
                .Select(x => x.OpaqueCursor)
                .SingleAsync();
        });
        try
        {
            await using var db = fixture.CreateContext();
            var processor = new MarketplaceJobProcessor(
                db, null!, null!, null!, null!, null!, null!, null!, port, null!, null!,
                new ConfigurationBuilder().Build(), fixture.TimeProvider);

            var result = await processor.ProcessAsync(tenant.Id, connection.Id, MarketplaceJobTypes.ReturnSync, "{}", "trendyol-return-cursor", CancellationToken.None);

            Assert.True(result.Succeeded, result.ErrorSummary);
            Assert.Equal(["0", "s:Created=1,Accepted=1", "s:Accepted=2"], port.Cursors);
            var stored = JsonDocument.Parse(port.StoredBeforeThirdPage!).RootElement;
            Assert.Equal("s:Accepted=2", stored.GetProperty("PageCursor").GetString());
            Assert.Equal(2, stored.GetProperty("Page").GetInt32());

            await using var verification = fixture.CreateContext();
            var finished = await verification.SyncCursors.AsNoTracking().SingleAsync(x => x.TenantId == tenant.Id && x.ConnectionId == connection.Id && x.ResourceType == "RETURNS");
            Assert.Null(finished.OpaqueCursor);
            Assert.NotNull(finished.LastModifiedWatermark);
        }
        finally
        {
            await using var cleanup = fixture.CreateContext();
            await cleanup.IntegrationOutboxEvents.Where(x => x.TenantId == tenant.Id).ExecuteDeleteAsync();
            await cleanup.OperationalIssues.Where(x => x.TenantId == tenant.Id).ExecuteDeleteAsync();
            await cleanup.SyncCursors.Where(x => x.TenantId == tenant.Id).ExecuteDeleteAsync();
            await cleanup.JobAttempts.Where(x => x.TenantId == tenant.Id).ExecuteDeleteAsync();
            await cleanup.IntegrationJobs.Where(x => x.TenantId == tenant.Id).ExecuteDeleteAsync();
            await cleanup.PlatformConnections.Where(x => x.TenantId == tenant.Id).ExecuteDeleteAsync();
            await cleanup.Tenants.Where(x => x.Id == tenant.Id).ExecuteDeleteAsync();
        }
    }

    [PostgreSqlFact]
    public async Task ReturnSync_RestartsFromItsBaselineWhenTheSavedPageIsRefusedForGood()
    {
        var tenant = new Tenant
        {
            Id = Guid.CreateVersion7(),
            Code = $"trendyol-return-refused-{Guid.NewGuid():N}",
            DisplayName = "trendyol-return-refused",
            Status = RecordStatus.Active,
            Timezone = "Europe/Istanbul",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            Version = 1
        };
        var connection = new PlatformConnection
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenant.Id,
            PublicId = Guid.CreateVersion7(),
            PlatformCode = "TRENDYOL",
            Environment = "PRODUCTION",
            DisplayName = "Trendyol iade sayfa reddi testi",
            ExternalStoreId = $"seller-{Guid.NewGuid():N}",
            Status = "ACTIVE",
            ApiVersion = "V2",
            SettingsJson = "{}"
        };
        var saved = new ReturnReadService.ReturnSyncState(ReturnReadService.ReturnSyncStateVersion, 0, 4, false, fixture.Now.AddHours(-2), fixture.Now.AddHours(-1), "s:Created=4");
        await using (var seed = fixture.CreateContext())
        {
            seed.Tenants.Add(tenant);
            seed.PlatformConnections.Add(connection);
            seed.SyncCursors.Add(new SyncCursor
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenant.Id,
                ConnectionId = connection.Id,
                ResourceType = "RETURNS",
                OpaqueCursor = ReturnReadService.SerializeReturnSyncState(saved),
                LastModifiedWatermark = fixture.Now.AddHours(-2)
            });
            await seed.SaveChangesAsync();
        }

        try
        {
            async Task<ReturnReadService.ReturnSyncState?> RunAndReadAsync()
            {
                await using (var db = fixture.CreateContext())
                {
                    var processor = new MarketplaceJobProcessor(
                        db, null!, null!, null!, null!, null!, null!, null!, new RefusingReturnPort(), null!, null!,
                        new ConfigurationBuilder().Build(), fixture.TimeProvider);
                    var result = await processor.ProcessAsync(tenant.Id, connection.Id, MarketplaceJobTypes.ReturnSync, "{}", "trendyol-return-refused", CancellationToken.None);
                    Assert.False(result.Succeeded);
                }
                await using var check = fixture.CreateContext();
                var stored = await check.SyncCursors.AsNoTracking()
                    .Where(x => x.TenantId == tenant.Id && x.ConnectionId == connection.Id && x.ResourceType == "RETURNS")
                    .Select(x => x.OpaqueCursor).SingleAsync();
                return stored is null ? null : JsonSerializer.Deserialize<ReturnReadService.ReturnSyncState>(stored);
            }

            var afterOne = await RunAndReadAsync();
            Assert.Equal(1, afterOne?.PermanentFailures);
            Assert.Equal("s:Created=4", afterOne?.PageCursor);
            Assert.Equal(2, (await RunAndReadAsync())?.PermanentFailures);
            // The third refusal in a row drops the saved page: the next read starts from its baseline.
            Assert.Null(await RunAndReadAsync());
        }
        finally
        {
            await using var cleanup = fixture.CreateContext();
            await cleanup.IntegrationOutboxEvents.Where(x => x.TenantId == tenant.Id).ExecuteDeleteAsync();
            await cleanup.OperationalIssues.Where(x => x.TenantId == tenant.Id).ExecuteDeleteAsync();
            await cleanup.SyncCursors.Where(x => x.TenantId == tenant.Id).ExecuteDeleteAsync();
            await cleanup.JobAttempts.Where(x => x.TenantId == tenant.Id).ExecuteDeleteAsync();
            await cleanup.IntegrationJobs.Where(x => x.TenantId == tenant.Id).ExecuteDeleteAsync();
            await cleanup.PlatformConnections.Where(x => x.TenantId == tenant.Id).ExecuteDeleteAsync();
            await cleanup.Tenants.Where(x => x.Id == tenant.Id).ExecuteDeleteAsync();
        }
    }

    // Every page read is refused with a permanent 400.
    private sealed class RefusingReturnPort : IReturnPort
    {
        public Task<AdapterResult<AdapterPageResult<RemoteReturnClaim>>> PollAsync(AdapterContext context, ReturnPollWindow window, AdapterPageRequest page, CancellationToken cancellationToken) =>
            Task.FromResult(AdapterResult<AdapterPageResult<RemoteReturnClaim>>.Failure(new(AdapterErrorClass.Validation, "TRENDYOL_BAD_REQUEST", "Geçersiz sayfa.", 400, null, null)));

        public Task<AdapterResult<RemoteReturnClaim>> GetAsync(AdapterContext context, string externalReturnId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<IReadOnlyList<ReturnIssueReason>>> IssueReasonsAsync(AdapterContext context, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<ReturnActionResult>> ExecuteAsync(AdapterContext context, ReturnActionCommand command, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    // Three pages with no claims: the first two say which buckets still have more, the third ends the read.
    private sealed class BucketCursorReturnPort(Func<Task<string?>> readStoredCursor) : IReturnPort
    {
        public List<string?> Cursors { get; } = [];
        public string? StoredBeforeThirdPage { get; private set; }

        public async Task<AdapterResult<AdapterPageResult<RemoteReturnClaim>>> PollAsync(AdapterContext context, ReturnPollWindow window, AdapterPageRequest page, CancellationToken cancellationToken)
        {
            Cursors.Add(page.Cursor);
            if (Cursors.Count == 3) StoredBeforeThirdPage = await readStoredCursor();
            var next = Cursors.Count switch
            {
                1 => TrendyolHttpClient.ClaimBucketCursor([("Created", 1), ("Accepted", 1)]),
                2 => TrendyolHttpClient.ClaimBucketCursor([("Accepted", 2)]),
                _ => null
            };
            return AdapterResult<AdapterPageResult<RemoteReturnClaim>>.Success(new([], next, next is not null));
        }

        public Task<AdapterResult<RemoteReturnClaim>> GetAsync(AdapterContext context, string externalReturnId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<IReadOnlyList<ReturnIssueReason>>> IssueReasonsAsync(AdapterContext context, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<ReturnActionResult>> ExecuteAsync(AdapterContext context, ReturnActionCommand command, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
