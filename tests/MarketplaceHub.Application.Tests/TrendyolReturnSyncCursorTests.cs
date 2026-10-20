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

    private static MarketplaceJobProcessor.ReturnSyncState StoreAndRead(MarketplaceJobProcessor.ReturnSyncState state) =>
        MarketplaceJobProcessor.ReadReturnSyncState(
            new SyncCursor { ResourceType = "RETURNS", OpaqueCursor = MarketplaceJobProcessor.SerializeReturnSyncState(state), LastModifiedWatermark = Now.AddHours(-1) },
            Now,
            TimeSpan.FromMinutes(15));

    [Fact]
    public void BucketCursor_RoundTripsThroughTheStoredStateAcrossTwoPages()
    {
        var first = MarketplaceJobProcessor.ReadReturnSyncState(new SyncCursor { ResourceType = "RETURNS" }, Now, TimeSpan.FromMinutes(15));
        Assert.Equal("0", MarketplaceJobProcessor.ReturnPageCursor(first));
        Assert.Equal(TrendyolHttpClient.ClaimStatuses.Length, TrendyolHttpClient.ClaimBucketPages(MarketplaceJobProcessor.ReturnPageCursor(first)).Count);

        // Page 1: only Created and Accepted had more claims.
        var afterFirst = StoreAndRead(MarketplaceJobProcessor.NextReturnPage(first, TrendyolHttpClient.ClaimBucketCursor([("Created", 1), ("Accepted", 1)])));
        Assert.Equal("s:Created=1,Accepted=1", MarketplaceJobProcessor.ReturnPageCursor(afterFirst));
        Assert.Equal([("Created", 1), ("Accepted", 1)], TrendyolHttpClient.ClaimBucketPages(MarketplaceJobProcessor.ReturnPageCursor(afterFirst)));
        Assert.Equal(1, afterFirst.Page);

        // Page 2: Created ran out; only Accepted is asked for its third page.
        var afterSecond = StoreAndRead(MarketplaceJobProcessor.NextReturnPage(afterFirst, TrendyolHttpClient.ClaimBucketCursor([("Accepted", 2)])));
        Assert.Equal([("Accepted", 2)], TrendyolHttpClient.ClaimBucketPages(MarketplaceJobProcessor.ReturnPageCursor(afterSecond)));
        Assert.Equal(2, afterSecond.Page);
    }

    [Fact]
    public void NumericCursors_StillWork()
    {
        // A plain page number from an adapter is a page, not an opaque cursor.
        var numeric = MarketplaceJobProcessor.NextReturnPage(MarketplaceJobProcessor.ReadReturnSyncState(new SyncCursor { ResourceType = "RETURNS" }, Now, TimeSpan.FromMinutes(15)), "4");
        Assert.Null(numeric.PageCursor);
        Assert.Equal("4", MarketplaceJobProcessor.ReturnPageCursor(numeric));

        // A state stored before PageCursor existed continues from its page number.
        var legacyState = $$"""{"Version":"{{MarketplaceJobProcessor.ReturnSyncStateVersion}}","StoreFrontIndex":0,"Page":3,"Full":false,"StartAt":"2026-10-20T08:00:00+00:00","EndAt":"2026-10-20T09:00:00+00:00"}""";
        var legacy = MarketplaceJobProcessor.ReadReturnSyncState(new SyncCursor { ResourceType = "RETURNS", OpaqueCursor = legacyState }, Now, TimeSpan.FromMinutes(15));
        Assert.Equal(3, legacy.Page);
        Assert.Equal("3", MarketplaceJobProcessor.ReturnPageCursor(legacy));

        // The oldest cursors were a bare page number.
        var bare = MarketplaceJobProcessor.ReadReturnSyncState(new SyncCursor { ResourceType = "RETURNS", OpaqueCursor = "5" }, Now, TimeSpan.FromMinutes(15));
        Assert.Equal("5", MarketplaceJobProcessor.ReturnPageCursor(bare));
    }

    [Fact]
    public void MissingOrOversizedCursor_FallsBackToTheNextPageNumber()
    {
        var state = MarketplaceJobProcessor.ReadReturnSyncState(new SyncCursor { ResourceType = "RETURNS" }, Now, TimeSpan.FromMinutes(15)) with { Page = 2, PageCursor = "s:Created=2" };

        var empty = MarketplaceJobProcessor.NextReturnPage(state, null);
        Assert.Equal("3", MarketplaceJobProcessor.ReturnPageCursor(empty));
        var oversized = MarketplaceJobProcessor.NextReturnPage(state, "s:" + new string('x', 2_000));
        Assert.Equal("3", MarketplaceJobProcessor.ReturnPageCursor(oversized));

        var stored = MarketplaceJobProcessor.SerializeReturnSyncState(state with { PageCursor = new string('y', 2_000) });
        var read = MarketplaceJobProcessor.ReadReturnSyncState(new SyncCursor { ResourceType = "RETURNS", OpaqueCursor = stored }, Now, TimeSpan.FromMinutes(15));
        Assert.Null(read.PageCursor);
        Assert.Equal("2", MarketplaceJobProcessor.ReturnPageCursor(read));
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
