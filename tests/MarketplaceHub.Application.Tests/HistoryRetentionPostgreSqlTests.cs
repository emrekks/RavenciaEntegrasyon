using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// VERITABANI-DAGITIM-13: history tables are purged only when the owner sets a period, rows on both sides of the cutoff,
// and the newest rows an offer or an order needs stay whatever their age. Each test runs in a transaction that is rolled
// back. Price and order history rows are written without their parents (session_replication_role = replica, local to the
// transaction; the test database user is the superuser, as in CI) because only the purge's own conditions are under test.
[Collection("postgres-integration")]
public sealed class HistoryRetentionPostgreSqlTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    private static readonly TimeSpan Year = TimeSpan.FromDays(365);

    [PostgreSqlFact]
    public async Task AuditAiAndProductChangeRowsGoOnlyBeforeTheCutoff()
    {
        var now = DateTimeOffset.UtcNow;
        var tenant = NewTenant("history-plain");
        await using var db = fixture.CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();
        var oldAudit = new AuditLog { TenantId = tenant.Id, Action = "HISTORY_TEST", TargetType = "T", Reason = "old", CorrelationId = "c", CreatedAt = now.AddDays(-400) };
        var newAudit = new AuditLog { TenantId = tenant.Id, Action = "HISTORY_TEST", TargetType = "T", Reason = "new", CorrelationId = "c", CreatedAt = now.AddDays(-300) };
        db.AuditLogs.AddRange(oldAudit, newAudit);
        AiUsageLog Ai(int daysAgo) => new() { Id = Guid.CreateVersion7(), TenantId = tenant.Id, Feature = "TEST", Model = "m", Status = "SUCCEEDED", CreatedAt = now.AddDays(-daysAgo) };
        var oldAi = Ai(400);
        var newAi = Ai(300);
        db.AiUsageLogs.AddRange(oldAi, newAi);
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlRawAsync("SET LOCAL session_replication_role = replica");
        var oldChange = Guid.CreateVersion7();
        var newChange = Guid.CreateVersion7();
        foreach (var (id, daysAgo) in new[] { (oldChange, 400), (newChange, 300) })
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO catalog.product_change_entries ("Id", "TenantId", "ProductId", "Kind", "Source", "CorrelationId", "OccurredAt")
                VALUES ({id}, {tenant.Id}, {Guid.CreateVersion7()}, 'FIELD', 'PANEL', 'c', {now.AddDays(-daysAgo)})
                """);
        db.ChangeTracker.Clear();

        var retention = new JobRetentionService(db, fixture.TimeProvider);
        Assert.True(await retention.PurgeAuditLogsAsync(Year, maximumBatches: 1_000, CancellationToken.None) >= 1);
        Assert.True(await retention.PurgeAiUsageLogsAsync(Year, maximumBatches: 1_000, CancellationToken.None) >= 1);
        Assert.True(await retention.PurgeProductChangeEntriesAsync(Year, maximumBatches: 1_000, CancellationToken.None) >= 1);

        Assert.Equal([newAudit.Id], await db.AuditLogs.AsNoTracking().Where(x => x.TenantId == tenant.Id).Select(x => x.Id).ToListAsync());
        Assert.Equal([newAi.Id], await db.AiUsageLogs.AsNoTracking().Where(x => x.TenantId == tenant.Id).Select(x => x.Id).ToListAsync());
        Assert.Equal([newChange], await db.Database.SqlQuery<Guid>($"SELECT \"Id\" AS \"Value\" FROM catalog.product_change_entries WHERE \"TenantId\" = {tenant.Id}").ToListAsync());
        await transaction.RollbackAsync();
    }

    [PostgreSqlFact]
    public async Task OldPriceVersionsGoOnlyBeyondTheNewestFiftyOfTheirOffer()
    {
        var now = DateTimeOffset.UtcNow;
        var tenant = NewTenant("history-price");
        var busyOffer = Guid.CreateVersion7();
        var quietOffer = Guid.CreateVersion7();
        await using var db = fixture.CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlRawAsync("SET LOCAL session_replication_role = replica");
        // Busy offer: 55 versions, all older than the cutoff -> the five oldest go. Quiet offer: 3 old versions -> all stay.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO inventory.channel_price_history ("Id", "TenantId", "OfferId", "PriceVersion", "ListPrice", "SalePrice", "Currency", "Reason", "ActorSource", "EffectiveAt")
            SELECT gen_random_uuid(), {tenant.Id}, {busyOffer}, version, 10, 9, 'TRY', 'test', 'TEST', {now.AddDays(-500)} + version * interval '1 minute'
            FROM generate_series(1, 55) AS version
            UNION ALL
            SELECT gen_random_uuid(), {tenant.Id}, {quietOffer}, version, 10, 9, 'TRY', 'test', 'TEST', {now.AddDays(-500)}
            FROM generate_series(1, 3) AS version
            """);

        var deleted = await new JobRetentionService(db, fixture.TimeProvider).PurgeChannelPriceHistoryAsync(Year, maximumBatches: 1_000, CancellationToken.None);

        Assert.Equal(5, deleted);
        var busy = await db.ChannelPriceHistory.AsNoTracking().Where(x => x.TenantId == tenant.Id && x.OfferId == busyOffer).Select(x => x.PriceVersion).ToListAsync();
        Assert.Equal(Enumerable.Range(6, 50).Select(x => (long)x), busy.Order());
        Assert.Equal(3, await db.ChannelPriceHistory.AsNoTracking().CountAsync(x => x.TenantId == tenant.Id && x.OfferId == quietOffer));
        await transaction.RollbackAsync();
    }

    [PostgreSqlFact]
    public async Task OldOrderStatusRowsGoButTheNewestOfEveryOrderStays()
    {
        var now = DateTimeOffset.UtcNow;
        var tenant = NewTenant("history-status");
        var order = Guid.CreateVersion7();
        var oldOnlyOrder = Guid.CreateVersion7();
        await using var db = fixture.CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlRawAsync("SET LOCAL session_replication_role = replica");
        var created = Guid.CreateVersion7();
        var shipped = Guid.CreateVersion7();
        var delivered = Guid.CreateVersion7();
        var lonely = Guid.CreateVersion7();
        foreach (var (id, orderId, status, daysAgo) in new[] { (created, order, "CREATED", 1200), (shipped, order, "SHIPPED", 1199), (delivered, order, "DELIVERED", 10), (lonely, oldOnlyOrder, "DELIVERED", 1200) })
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO sales.order_status_history ("Id", "TenantId", "OrderId", "CanonicalStatus", "RawStatus", "SourceEventId", "OccurredAt", "RecordedAt")
                VALUES ({id}, {tenant.Id}, {orderId}, {status}, {status}, {id.ToString("N")}, {now.AddDays(-daysAgo)}, {now.AddDays(-daysAgo)})
                """);

        var deleted = await new JobRetentionService(db, fixture.TimeProvider).PurgeOrderStatusHistoryAsync(TimeSpan.FromDays(1095), maximumBatches: 1_000, CancellationToken.None);

        Assert.Equal(2, deleted);
        var left = await db.OrderStatusHistory.AsNoTracking().Where(x => x.TenantId == tenant.Id).Select(x => x.Id).ToListAsync();
        Assert.Equal(new[] { delivered, lonely }.Order(), left.Order());
        await transaction.RollbackAsync();
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
    };
}

public sealed class HistoryRetentionPolicyTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("0", null)]
    [InlineData("-5", null)]
    [InlineData("30", 730)]
    [InlineData("1000", 1000)]
    [InlineData("99999", 3650)]
    public void AuditLogPeriodIsOffUntilSetAndNeverBelowItsFloor(string? configured, int? expectedDays)
    {
        var settings = new Dictionary<string, string?>();
        if (configured is not null) settings[HistoryRetentionPolicy.SettingName(HistoryRetentionPolicy.AuditLogs)] = configured;
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        var period = HistoryRetentionPolicy.Period(HistoryRetentionPolicy.AuditLogs, configuration.GetValue<int?>(HistoryRetentionPolicy.SettingName(HistoryRetentionPolicy.AuditLogs)));

        Assert.Equal(expectedDays, period is null ? null : (int)period.Value.TotalDays);
    }

    [Fact]
    public void EveryTableIsOffByDefaultAndNamedInTheProposal()
    {
        var configuration = new ConfigurationBuilder().Build();
        var proposal = File.ReadAllText(Path.Combine(TestRepository.Root(), "docs", "SAKLAMA-SURELERI.md"));
        var compose = File.ReadAllText(Path.Combine(TestRepository.Root(), "deploy", "compose", "compose.production.yaml"));
        foreach (var table in HistoryRetentionPolicy.Tables)
        {
            Assert.Null(HistoryRetentionPolicy.Period(table, configuration.GetValue<int?>(HistoryRetentionPolicy.SettingName(table))));
            Assert.Contains(table.Name, proposal, StringComparison.Ordinal);
            Assert.Contains(HistoryRetentionPolicy.SettingName(table).Replace(":", "__", StringComparison.Ordinal), compose, StringComparison.Ordinal);
        }
    }
}
