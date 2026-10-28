using System.Text.Json;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// The brand picker searches the current brand snapshot (Trendyol's holds 808k brands) instead of downloading it.
public sealed class BrandReferenceSearchTests
{
    [Theory]
    [InlineData("ipek", new[] { "IPEK", "İPEK" })]
    [InlineData("İpek", new[] { "İPEK", "IPEK" })]
    [InlineData("kılıç", new[] { "KıLıÇ", "KILIÇ" })]
    [InlineData("KILIÇ", new[] { "KILIÇ", "KıLıÇ" })]
    [InlineData("  polo  ", new[] { "POLO" })]
    public void SearchKeys_CoverBothWaysATurkishIIsStored(string query, string[] expected)
    {
        Assert.Equal(expected, ReferenceDataService.BrandSearchKeys(query));
    }

    [Fact]
    public void LikeWildcardsInTheQueryAreMatchedLiterally()
    {
        Assert.Equal("100\\% A\\_B \\\\", ReferenceDataService.EscapeLike("100% A_B \\"));
    }

    [Fact]
    public void Ranking_PutsTheIdThenTheWholeNameThenItsStartThenAWordStart()
    {
        var keys = ReferenceDataService.BrandSearchKeys("polo");
        ReferenceDataService.BrandSearchRow Row(string id, string name) => new(name.ToUpperInvariant(), new(id, null, name, name, 0, false, true, null, null, null));

        Assert.Equal(0, ReferenceDataService.BrandSearchRank(Row("polo", "Başka"), "polo", keys));
        Assert.Equal(1, ReferenceDataService.BrandSearchRank(Row("1", "Polo"), "polo", keys));
        Assert.Equal(2, ReferenceDataService.BrandSearchRank(Row("2", "Polo Garage"), "polo", keys));
        Assert.Equal(3, ReferenceDataService.BrandSearchRank(Row("3", "U.S. Polo Assn."), "polo", keys));
        Assert.Equal(4, ReferenceDataService.BrandSearchRank(Row("4", "Apolo"), "polo", keys));
    }

    [Fact]
    public void DailyReferenceRead_KeepsTheBrandListOfTrendyolAndPazaramaOnly()
    {
        Assert.Equal(["CATEGORIES", "BRANDS"], ScheduledJobProducer.ScheduledReferenceResources("TRENDYOL"));
        Assert.Equal(["CATEGORIES", "BRANDS"], ScheduledJobProducer.ScheduledReferenceResources("PAZARAMA"));
        Assert.Equal(["CATEGORIES"], ScheduledJobProducer.ScheduledReferenceResources("HEPSIBURADA"));
        Assert.Equal(["CATEGORIES"], ScheduledJobProducer.ScheduledReferenceResources("N11"));
        Assert.Equal(["CATEGORIES"], ScheduledJobProducer.ScheduledReferenceResources("PTTAVM"));
    }

    [Fact]
    public void DailyReferenceRead_ReadsAListAgainOnceADayAndNeverOneFetchedToday()
    {
        var now = new DateTimeOffset(2026, 10, 29, 14, 5, 0, TimeSpan.FromHours(3));
        string[] resources = ["CATEGORIES", "BRANDS"];
        IReadOnlyList<string> Stale(DateTimeOffset? categories, DateTimeOffset? brands)
        {
            var fetched = new Dictionary<string, DateTimeOffset>();
            if (categories is { } c) fetched["CATEGORIES"] = c;
            if (brands is { } b) fetched["BRANDS"] = b;
            return ScheduledJobProducer.StaleReferenceResources(resources, fetched, 86_400, 60, now);
        }

        // Never fetched: both are read.
        Assert.Equal(resources, Stale(null, null));
        // Fetched an hour ago (by hand or at activation): nothing to read.
        Assert.Empty(Stale(now.AddHours(-1), now.AddHours(-1)));
        // Yesterday's brand read ended late in the window (16:40); today's window still reads it again.
        Assert.Equal(["BRANDS"], Stale(now.AddHours(-1), now.AddDays(-1).AddHours(2.5)));
        Assert.Equal(resources, Stale(now.AddDays(-3), now.AddDays(-3)));
    }
}

[Collection("postgres-integration")]
public sealed class BrandReferenceSearchPostgreSqlTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    [PostgreSqlFact]
    public async Task Search_RanksTheIdTheNameItsStartAWordStartAndAFragmentAndSkipsInactiveBrands()
    {
        var seed = await SeedAsync(fixture);
        await using var db = seed.Db;
        var service = new ReferenceDataService(db, fixture.TimeProvider);

        async Task<string[]> Search(string query, int? limit = null)
        {
            var result = await service.SearchBrandsAsync(seed.Tenant.Id, seed.Trendyol.Id, query, limit, CancellationToken.None);
            Assert.True(result.Succeeded, result.Error?.Message);
            Assert.Equal(seed.Current.Id, result.Value!.SnapshotId);
            return result.Value.Items.Select(x => x.ExternalId).ToArray();
        }

        Assert.Equal(["1001", "1009", "1002", "1003", "1004"], await Search("polo"));
        Assert.Equal(["1001", "1009", "1002", "1003", "1004"], await Search(" POLO "));
        Assert.Equal(["1005"], await Search("ipek"));
        Assert.Equal(["1005"], await Search("İpek"));
        Assert.Equal(["1006"], await Search("ipa"));
        Assert.Equal(["1003"], await Search("1003"));
        // LIKE wildcards are matched as written.
        Assert.Equal(["1008"], await Search("100%"));
        Assert.Empty(await Search("_"));
        // An empty query lists the first names in byte order.
        Assert.Equal(["1008", "1004", "1006"], await Search("", 3));
        await seed.Transaction.RollbackAsync();
    }

    [PostgreSqlFact]
    public async Task SummaryAndLookup_ReadOnlyTheTenantsCurrentSnapshot()
    {
        var seed = await SeedAsync(fixture);
        await using var db = seed.Db;
        var service = new ReferenceDataService(db, fixture.TimeProvider);

        var summary = await service.BrandSummaryAsync(seed.Tenant.Id, seed.Trendyol.Id, CancellationToken.None);
        Assert.True(summary.Succeeded, summary.Error?.Message);
        Assert.Equal(seed.Current.Id, summary.Value!.SnapshotId);
        Assert.Equal(9, summary.Value.ItemCount);
        Assert.Equal(seed.Current.FetchedAt, summary.Value.FetchedAt);
        Assert.Empty(summary.Value.Items);

        // The current snapshot's names, an inactive brand included; an unknown id is left out.
        var lookup = await service.LookupBrandsAsync(seed.Tenant.Id, seed.Trendyol.Id, ["1001", "1007", "9999", "1001"], CancellationToken.None);
        Assert.True(lookup.Succeeded, lookup.Error?.Message);
        Assert.Equal([("1001", "Polo"), ("1007", "Polonez")], lookup.Value!.Items.Select(x => (x.ExternalId, x.Name)).ToArray());

        // Another tenant sees its own brands only and cannot read this tenant's connection.
        var other = await service.SearchBrandsAsync(seed.OtherTenant.Id, seed.OtherConnection.Id, "polo", null, CancellationToken.None);
        Assert.Equal(["Polo B"], other.Value!.Items.Select(x => x.Name).ToArray());
        Assert.Equal("RESOURCE_NOT_FOUND", (await service.SearchBrandsAsync(seed.OtherTenant.Id, seed.Trendyol.Id, "polo", null, CancellationToken.None)).Error?.Code);
        Assert.Equal("RESOURCE_NOT_FOUND", (await service.LookupBrandsAsync(seed.OtherTenant.Id, seed.Trendyol.Id, ["1001"], CancellationToken.None)).Error?.Code);
        Assert.Equal("RESOURCE_NOT_FOUND", (await service.BrandSummaryAsync(seed.OtherTenant.Id, seed.Trendyol.Id, CancellationToken.None)).Error?.Code);

        // A connection without a brand list says so; the page then queues its first read.
        Assert.Equal("REFERENCE_SNAPSHOT_UNAVAILABLE", (await service.BrandSummaryAsync(seed.Tenant.Id, seed.Pazarama.Id, CancellationToken.None)).Error?.Code);
        Assert.Equal("VALIDATION_FAILED", (await service.LookupBrandsAsync(seed.Tenant.Id, seed.Trendyol.Id, Enumerable.Range(0, 501).Select(x => $"{x}").ToArray(), CancellationToken.None)).Error?.Code);
        await seed.Transaction.RollbackAsync();
    }

    [PostgreSqlFact]
    public async Task Scheduler_ReadsAStaleBrandListOnceADayAndLeavesAFreshCategoryTree()
    {
        var istanbul = TimeSpan.FromHours(3);
        var today = DateTimeOffset.UtcNow.ToOffset(istanbul);
        var now = new DateTimeOffset(today.Year, today.Month, today.Day, 15, 0, 0, istanbul);
        var tenant = NewTenant("brand-daily");
        var connection = NewConnection(tenant.Id, "TRENDYOL", "brand-daily");
        await using var db = fixture.CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        db.Tenants.Add(tenant);
        db.PlatformConnections.Add(connection);
        db.ConnectionSyncPolicies.Add(new ConnectionSyncPolicy { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, ResourceType = "REFERENCE_DATA", IntervalSeconds = 86_400, OverlapSeconds = 0, JitterSeconds = 60, Enabled = true, Version = 1 });
        db.ReferenceSnapshots.AddRange(
            NewSnapshot(tenant.Id, connection.Id, "CATEGORIES", now.AddHours(-1), 1),
            NewSnapshot(tenant.Id, connection.Id, "BRANDS", now.AddDays(-2), 1));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var producer = new ScheduledJobProducer(db, new FixedTimeProvider(now), new ConfigurationBuilder().Build());
        await producer.EnqueueDueAsync(CancellationToken.None);
        await producer.EnqueueDueAsync(CancellationToken.None);

        db.ChangeTracker.Clear();
        var references = await db.IntegrationJobs.AsNoTracking().Where(x => x.TenantId == tenant.Id && x.JobType == MarketplaceJobTypes.ReferenceSync).ToListAsync();
        var job = Assert.Single(references);
        Assert.StartsWith($"scheduled:reference:{connection.Id:N}:brands:", job.JobDedupKey);
        using var payload = JsonDocument.Parse(job.PayloadJson);
        Assert.Equal("BRANDS", payload.RootElement.GetProperty("resourceType").GetString());
        await transaction.RollbackAsync();
    }

    private sealed record Seed(AppDbContext Db, Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction Transaction, Tenant Tenant, Tenant OtherTenant, PlatformConnection Trendyol, PlatformConnection Pazarama, PlatformConnection OtherConnection, ReferenceSnapshot Current);

    private static async Task<Seed> SeedAsync(PostgreSqlTenantIsolationFixture fixture)
    {
        var tenant = NewTenant("brand-search");
        var otherTenant = NewTenant("brand-search-other");
        var trendyol = NewConnection(tenant.Id, "TRENDYOL", "brand-search");
        var pazarama = NewConnection(tenant.Id, "PAZARAMA", "brand-search-pz");
        var otherConnection = NewConnection(otherTenant.Id, "TRENDYOL", "brand-search-other");
        var current = NewSnapshot(tenant.Id, trendyol.Id, "BRANDS", fixture.Now.AddHours(-2), 9);
        var stale = NewSnapshot(tenant.Id, trendyol.Id, "BRANDS", fixture.Now.AddDays(-3), 1, isCurrent: false);
        var otherCurrent = NewSnapshot(otherTenant.Id, otherConnection.Id, "BRANDS", fixture.Now.AddHours(-2), 1);
        var db = fixture.CreateContext();
        var transaction = await db.Database.BeginTransactionAsync();
        db.Tenants.AddRange(tenant, otherTenant);
        db.PlatformConnections.AddRange(trendyol, pazarama, otherConnection);
        db.ReferenceSnapshots.AddRange(current, stale, otherCurrent);
        ReferenceItem Brand(ReferenceSnapshot snapshot, string id, string name, bool active = true) => new()
        {
            Id = Guid.CreateVersion7(),
            TenantId = snapshot.TenantId,
            ConnectionId = snapshot.ConnectionId,
            SnapshotId = snapshot.Id,
            ResourceType = "BRANDS",
            ExternalId = id,
            Name = name,
            NormalizedName = name.Trim().ToUpperInvariant(),
            Path = name,
            IsLeaf = true,
            IsActive = active,
            PayloadHash = $"hash-{id}"
        };
        db.ReferenceItems.AddRange(
            Brand(current, "1001", "Polo"),
            Brand(current, "1002", "Polo Garage"),
            Brand(current, "1003", "U.S. Polo Assn."),
            Brand(current, "1004", "Apolo"),
            Brand(current, "1005", "İpek Yolu"),
            Brand(current, "1006", "Ipana"),
            Brand(current, "1007", "Polonez", active: false),
            Brand(current, "1008", "100% Pamuk"),
            Brand(current, "1009", "Polo_X"),
            Brand(stale, "1001", "Polo Eski"),
            Brand(otherCurrent, "1001", "Polo B"));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return new(db, transaction, tenant, otherTenant, trendyol, pazarama, otherConnection, current);
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

    private static PlatformConnection NewConnection(Guid tenantId, string platformCode, string store) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = tenantId,
        PublicId = Guid.CreateVersion7(),
        PlatformCode = platformCode,
        Environment = "PRODUCTION",
        DisplayName = store,
        ExternalStoreId = $"{store}-{Guid.NewGuid():N}"[..40],
        Status = "ACTIVE",
        ApiVersion = "V1"
    };

    private static ReferenceSnapshot NewSnapshot(Guid tenantId, Guid connectionId, string resourceType, DateTimeOffset fetchedAt, int itemCount, bool isCurrent = true) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = tenantId,
        ConnectionId = connectionId,
        ResourceType = resourceType,
        SourceVersion = "V1",
        ContentHash = $"hash-{Guid.NewGuid():N}",
        // PostgreSQL keeps microseconds; the seeded time is cut to them so it reads back equal.
        FetchedAt = new DateTimeOffset(fetchedAt.UtcTicks - fetchedAt.UtcTicks % 10, TimeSpan.Zero),
        IsCurrent = isCurrent,
        ItemCount = itemCount
    };

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now.ToUniversalTime();
    }
}
