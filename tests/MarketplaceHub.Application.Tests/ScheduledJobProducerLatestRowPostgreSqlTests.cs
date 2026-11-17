using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// KUYRUK-10 follow-up (16 Nov 2026): the batched reads of the latest question and the latest return must read the latest
// row, whatever order the rows were written in. The idle cadence they feed shows in the job key's bucket (now / interval):
// a connection with several questions (or returns) written out of order must get the same bucket as one holding only the
// latest of them, and a different one from a connection holding only the oldest. The context throws on EF's
// "row limit without OrderBy" warning, so an unordered Take cannot come back unnoticed.
[Collection("postgres-integration")]
public sealed class ScheduledJobProducerLatestRowPostgreSqlTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    private static readonly DateTimeOffset Now = new(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);

    [PostgreSqlFact]
    public async Task TheIdleCadenceReadsTheLatestQuestionAndReturnWrittenOutOfOrder()
    {
        var tenant = ScheduledPolicyIsolationTests.NewTenant("latest-row");
        var mixed = NewConnection(tenant.Id, "mixed");
        var latestOnly = NewConnection(tenant.Id, "latest");
        var oldestOnly = NewConnection(tenant.Id, "oldest");

        await using var db = fixture.CreateStrictContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        db.Tenants.Add(tenant);
        db.PlatformConnections.AddRange(mixed, latestOnly, oldestOnly);
        await db.SaveChangesAsync();

        var recentQuestion = Now.AddHours(-1);
        var oldQuestions = new[] { Now.AddDays(-30), Now.AddDays(-10) };
        var recentReturn = Now.AddHours(-2);
        var oldReturns = new[] { Now.AddDays(-20), Now.AddDays(-8) };
        // Written oldest, newest, older: the newest is neither the first nor the last row.
        db.MarketplaceQuestions.AddRange(Question(mixed, oldQuestions[0]), Question(mixed, recentQuestion), Question(mixed, oldQuestions[1]),
            Question(latestOnly, recentQuestion), Question(oldestOnly, oldQuestions[0]));
        await AddReturnsAsync(db, mixed, oldReturns[0], recentReturn, oldReturns[1]);
        await AddReturnsAsync(db, latestOnly, recentReturn);
        await AddReturnsAsync(db, oldestOnly, oldReturns[0]);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await new ScheduledJobProducer(db, new FixedTimeProvider(Now), new ConfigurationBuilder().Build()).EnqueueDueAsync(CancellationToken.None);
        var keys = await db.IntegrationJobs.AsNoTracking().Where(x => x.TenantId == tenant.Id)
            .Select(x => new { x.ConnectionId, x.JobType, x.JobDedupKey }).ToListAsync();
        await transaction.RollbackAsync();

        string Bucket(PlatformConnection connection, string jobType) =>
            keys.Single(x => x.ConnectionId == connection.Id && x.JobType == jobType).JobDedupKey.Split(':')[^1];
        foreach (var jobType in new[] { MarketplaceJobTypes.QuestionSync, MarketplaceJobTypes.ReturnSync })
        {
            Assert.Equal(Bucket(latestOnly, jobType), Bucket(mixed, jobType));
            Assert.NotEqual(Bucket(oldestOnly, jobType), Bucket(mixed, jobType));
        }
    }

    // The other passes a worker runs at start or shortly after read without an unordered row limit either (a strict
    // context throws on EF warning 10102). They run over the shared test database as the worker would.
    [PostgreSqlFact]
    public async Task TheWorkersOtherStartupReadsHaveNoUnorderedRowLimit()
    {
        await using (var db = fixture.CreateStrictContext())
            await new ExternalEffectSweep(db, fixture.TimeProvider).SweepAsync(CancellationToken.None);
        await using (var db = fixture.CreateStrictContext())
            Assert.Null(await new JobLeaseService(db, fixture.TokenHasher, fixture.TimeProvider).TryLeaseAsync(TimeSpan.FromMinutes(2), null, null, CancellationToken.None, onlyJobId: Guid.CreateVersion7()));
        await using (var db = fixture.CreateStrictContext())
            await ReturnCardColumns.StaleTenantsAsync(db, CancellationToken.None);
        await using (var db = fixture.CreateStrictContext())
            await ProductWarranty.RunPassAsync(db, firstPassAfterStart: false, CancellationToken.None);
        await using (var db = fixture.CreateStrictContext())
            await OrderCustomerSearchNames.BackfillAsync(db, CancellationToken.None);
        await using (var db = fixture.CreateStrictContext())
            await ConnectionLeftovers.SweepDeletedConnectionsAsync(db, DateTimeOffset.UtcNow, CancellationToken.None, tenantId: Guid.CreateVersion7());
    }

    private static PlatformConnection NewConnection(Guid tenantId, string store) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = tenantId,
        PublicId = Guid.CreateVersion7(),
        PlatformCode = "TRENDYOL",
        Environment = "PRODUCTION",
        DisplayName = store,
        ExternalStoreId = $"{store}-{Guid.NewGuid():N}"[..30],
        Status = "ACTIVE",
        ApiVersion = "V1"
    };

    private static MarketplaceQuestion Question(PlatformConnection connection, DateTimeOffset createdAt) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = connection.TenantId,
        ConnectionId = connection.Id,
        ExternalQuestionId = $"q-{Guid.NewGuid():N}",
        Kind = "PRODUCT",
        Status = "WAITING_FOR_ANSWER",
        QuestionText = "Beden?",
        CreatedAt = createdAt,
        LastRemoteModifiedAt = createdAt,
        LastSyncedAt = createdAt,
        Version = 1
    };

    // Closed claims (Completed), so only the time of the latest one moves the return cadence.
    private static async Task AddReturnsAsync(AppDbContext db, PlatformConnection connection, params DateTimeOffset[] createdAt)
    {
        foreach (var at in createdAt)
        {
            var order = new Order
            {
                Id = Guid.CreateVersion7(),
                TenantId = connection.TenantId,
                ConnectionId = connection.Id,
                ExternalOrderId = $"o-{Guid.NewGuid():N}",
                OrderNumber = $"o-{Guid.NewGuid():N}",
                Currency = "TRY",
                GrossAmount = 100m,
                NetAmount = 100m,
                OrderedAt = at.AddDays(-2),
                LastRemoteModifiedAt = at,
                CustomerSnapshotJson = "{}",
                ShipmentAddressSnapshotJson = "{}",
                InvoiceAddressSnapshotJson = "{}",
                DerivedStatus = "DELIVERED",
                CreatedAt = at.AddDays(-2),
                UpdatedAt = at,
                Version = 1
            };
            db.Orders.Add(order);
            db.ReturnClaims.Add(new ReturnClaim { Id = Guid.CreateVersion7(), TenantId = connection.TenantId, ConnectionId = connection.Id, OrderId = order.Id, ExternalClaimId = $"c-{Guid.NewGuid():N}", Status = ReturnClaimStatus.Completed, RawStatus = "Accepted", LastRemoteModifiedAt = at, CreatedAt = at, UpdatedAt = at, Version = 1 });
            // One save per claim, so the rows are written in the order given.
            await db.SaveChangesAsync();
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
