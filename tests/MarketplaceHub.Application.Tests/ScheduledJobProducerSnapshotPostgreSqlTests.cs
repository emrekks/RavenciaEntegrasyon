using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// KUYRUK-10: the scheduler's per-policy lookups were batched; the jobs a pass queues must stay exactly what they were.
// A seeded multi-connection tenant (fixed ids, so jitter and buckets are fixed) is scheduled twice; the queued set of
// each pass is pinned as the code before the batching queued it.
[Collection("postgres-integration")]
public sealed class ScheduledJobProducerSnapshotPostgreSqlTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    private static readonly DateTimeOffset Now = new(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid TenantId = Guid.Parse("0193a6e0-0000-7000-8000-00000000a100");

    private static readonly (string Label, string Platform, Guid Id)[] Connections =
    [
        ("T1", "TRENDYOL", Guid.Parse("0193a6e0-0000-7000-8000-00000000a101")),
        ("T2", "TRENDYOL", Guid.Parse("0193a6e0-0000-7000-8000-00000000a102")),
        ("HB", "HEPSIBURADA", Guid.Parse("0193a6e0-0000-7000-8000-00000000a103")),
        ("PZ1", "PAZARAMA", Guid.Parse("0193a6e0-0000-7000-8000-00000000a104")),
        ("PZ2", "PAZARAMA", Guid.Parse("0193a6e0-0000-7000-8000-00000000a105")),
        ("N11", "N11", Guid.Parse("0193a6e0-0000-7000-8000-00000000a106")),
        ("PTT", "PTTAVM", Guid.Parse("0193a6e0-0000-7000-8000-00000000a107")),
        ("SH", "SHOPIFY", Guid.Parse("0193a6e0-0000-7000-8000-00000000a108"))
    ];

    [PostgreSqlFact]
    public async Task Scheduler_QueuesTheSameJobsForASeededMultiConnectionTenant()
    {
        Assert.NotNull(fixture);
        await using var db = fixture.CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        await SeedAsync(db);

        await new ScheduledJobProducer(db, new FixedTimeProvider(Now), new ConfigurationBuilder().Build()).EnqueueDueAsync(CancellationToken.None);
        var first = await QueuedAsync(db);

        // T2's jobs ran and finished; its policies forget when they last queued. The next pass, in the same buckets, finds
        // their keys taken (reused, not queued again); the other connections' policies are not due yet.
        var t2 = Connections.Single(x => x.Label == "T2").Id;
        await db.IntegrationJobs.Where(x => x.TenantId == TenantId && x.ConnectionId == t2)
            .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.Status, JobStatus.Succeeded));
        await db.ConnectionSyncPolicies.Where(x => x.TenantId == TenantId && x.ConnectionId == t2)
            .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.LastScheduledAt, (DateTimeOffset?)null));
        db.ChangeTracker.Clear();
        await new ScheduledJobProducer(db, new FixedTimeProvider(Now.AddSeconds(5)), new ConfigurationBuilder().Build()).EnqueueDueAsync(CancellationToken.None);
        var second = (await QueuedAsync(db)).Except(first).ToList();

        await transaction.RollbackAsync();
        Assert.Equal(ExpectedFirstPass.ReplaceLineEndings("\n"), string.Join('\n', first));
        Assert.Equal(ExpectedSecondPass.ReplaceLineEndings("\n"), string.Join('\n', second));
    }

    // Recorded from the code before the batched reads (round 6, e358a5be3), by running this test against it.
    private const string ExpectedFirstPass = """
        HB|HEPSIBURADA_FINANCE_SYNC|scheduled:finance:HB:83094|4
        HB|HEPSIBURADA_ORDER_SYNC|scheduled:orders:HB:14956920|0
        HB|HEPSIBURADA_REFERENCE_SYNC|scheduled:reference:HB:20773|5
        HB|HEPSIBURADA_RETURN_SYNC|scheduled:returns:HB:997128|3
        HB|MARKETPLACE_QUESTION_SYNC|scheduled:questions:HB:2991384|3
        N11|MARKETPLACE_QUESTION_SYNC|scheduled:questions:N11:3739230|3
        N11|N11_ORDER_SYNC|scheduled:orders:N11:14956920|0
        N11|N11_REFERENCE_SYNC|scheduled:reference:N11:20773|5
        N11|N11_SETTLEMENT_SYNC|scheduled:finance:N11:83094|4
        N11|RETURN_SYNC|scheduled:returns:N11:997128|2
        PTT|PTTAVM_ORDER_SYNC|scheduled:orders:PTT:14956920|0
        PTT|PTTAVM_REFERENCE_SYNC|scheduled:reference:PTT:20773|5
        PTT|RETURN_SYNC|scheduled:returns:PTT:997128|2
        PZ1|MARKETPLACE_QUESTION_SYNC|scheduled:questions:PZ1:2991384|3
        PZ1|PAZARAMA_FINANCE_SYNC|scheduled:finance:PZ1:83094|4
        PZ1|PAZARAMA_ORDER_SYNC|scheduled:orders:PZ1:14956920|0
        PZ1|PAZARAMA_REFERENCE_SYNC|scheduled:reference:PZ1:20773|5
        PZ1|PAZARAMA_REFERENCE_SYNC|scheduled:reference:PZ1:brands:20773|5
        PZ1|RETURN_SYNC|scheduled:returns:PZ1:997128|2
        PZ2|MARKETPLACE_QUESTION_SYNC|scheduled:questions:PZ2:2991384|3
        PZ2|PAZARAMA_FINANCE_SYNC|scheduled:finance:PZ2:83094|4
        PZ2|PAZARAMA_ORDER_SYNC|scheduled:orders:PZ2:14956920|0
        PZ2|PAZARAMA_REFERENCE_SYNC|scheduled:reference:PZ2:20773|5
        PZ2|PAZARAMA_REFERENCE_SYNC|scheduled:reference:PZ2:brands:20773|5
        PZ2|RETURN_SYNC|scheduled:returns:PZ2:997128|2
        SH|RETURN_SYNC|scheduled:returns:SH:997128|2
        SH|SHOPIFY_ORDER_SYNC|scheduled:orders:SH:14956920|0
        T1|MARKETPLACE_QUESTION_SYNC|scheduled:questions:T1:2991384|3
        T1|ORDER_INVOICE_RECONCILIATION|scheduled:order-invoice-reconciliation:T1:1994256|3
        T1|RETURN_SYNC|scheduled:returns:T1:2991384|2
        T1|TRENDYOL_FINANCE_SYNC|scheduled:finance:T1:83094|4
        T1|TRENDYOL_ORDER_CARGO_INFO_RECONCILIATION|scheduled:order-cargo-info:T1:997128|3
        T1|TRENDYOL_ORDER_SYNC|scheduled:orders:T1:14956920|0
        T1|TRENDYOL_REFERENCE_SYNC|scheduled:reference:T1:20773|5
        T1|TRENDYOL_REFERENCE_SYNC|scheduled:reference:T1:brands:20773|5
        T2|MARKETPLACE_QUESTION_SYNC|scheduled:questions:T2:2991384|3
        T2|ORDER_INVOICE_RECONCILIATION|scheduled:order-invoice-reconciliation:T2:1994256|3
        T2|RETURN_SYNC|scheduled:returns:T2:997128|2
        T2|TRENDYOL_FINANCE_SYNC|scheduled:finance:T2:83094|4
        T2|TRENDYOL_ORDER_CARGO_INFO_RECONCILIATION|scheduled:order-cargo-info:T2:997128|3
        T2|TRENDYOL_ORDER_SYNC|scheduled:orders:T2:14956920|0
        T2|TRENDYOL_REFERENCE_SYNC|scheduled:reference:T2:20773|5
        T2|TRENDYOL_REFERENCE_SYNC|scheduled:reference:T2:brands:20773|5
        """;
    private const string ExpectedSecondPass = """
        HB|HEPSIBURADA_ORDER_RECOVERY_SYNC|scheduled:order-recovery:HB:1994256|3
        HB|HEPSIBURADA_ORDER_STATUS_SYNC|scheduled:order-lifecycle:HB:14956920|0
        N11|N11_ORDER_RECOVERY_SYNC|scheduled:order-recovery:N11:1994256|3
        PTT|PTTAVM_ORDER_RECOVERY_SYNC|scheduled:order-recovery:PTT:1994256|3
        PZ1|PAZARAMA_ORDER_RECOVERY_SYNC|scheduled:order-recovery:PZ1:1994256|3
        PZ1|PAZARAMA_ORDER_STATUS_SYNC|scheduled:order-lifecycle:PZ1:14956920|0
        PZ2|PAZARAMA_ORDER_RECOVERY_SYNC|scheduled:order-recovery:PZ2:1994256|3
        SH|SHOPIFY_ORDER_RECOVERY_SYNC|scheduled:order-recovery:SH:498564|6
        T1|ORDER_STATUS_SYNC|scheduled:order-lifecycle:T1:997128|0
        T2|RETURN_RECONCILIATION|scheduled:return-reconcile-short:T2:498564|4
        T2|TRENDYOL_ORDER_RECOVERY_SYNC|scheduled:order-recovery:T2:1994256|6
        """;

    private static async Task<List<string>> QueuedAsync(AppDbContext db)
    {
        var jobs = await db.IntegrationJobs.AsNoTracking().Where(x => x.TenantId == TenantId)
            .Select(x => new { x.ConnectionId, x.JobType, x.JobDedupKey, x.Priority, x.Status })
            .ToListAsync();
        string Label(Guid? id) => Connections.FirstOrDefault(x => x.Id == id).Label ?? "-";
        string Key(string key) => Connections.Aggregate(key, (current, connection) => current.Replace(connection.Id.ToString("N"), connection.Label).Replace(connection.Id.ToString(), connection.Label));
        return [.. jobs.Select(x => $"{Label(x.ConnectionId)}|{x.JobType}|{Key(x.JobDedupKey)}|{x.Priority}").Order(StringComparer.Ordinal)];
    }

    private static async Task SeedAsync(AppDbContext db)
    {
        db.Tenants.Add(new Tenant { Id = TenantId, Code = $"snapshot-{Guid.NewGuid():N}", DisplayName = "snapshot", Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = Now.AddDays(-60), UpdatedAt = Now.AddDays(-60), Version = 1 });
        foreach (var (label, platform, id) in Connections)
            db.PlatformConnections.Add(new PlatformConnection
            {
                Id = id,
                TenantId = TenantId,
                PublicId = Guid.CreateVersion7(),
                PlatformCode = platform,
                Environment = "PRODUCTION",
                DisplayName = label,
                ExternalStoreId = $"{label}-{Guid.NewGuid():N}"[..30],
                Status = "ACTIVE",
                ApiVersion = "V1"
            });
        await db.SaveChangesAsync();

        Guid Connection(string label) => Connections.Single(x => x.Label == label).Id;
        Order NewOrder(string label, string number) => new()
        {
            Id = Guid.CreateVersion7(),
            TenantId = TenantId,
            ConnectionId = Connection(label),
            ExternalOrderId = number,
            OrderNumber = number,
            Currency = "TRY",
            GrossAmount = 100m,
            NetAmount = 100m,
            OrderedAt = Now.AddDays(-4),
            LastRemoteModifiedAt = Now.AddDays(-4),
            CustomerSnapshotJson = "{}",
            ShipmentAddressSnapshotJson = "{}",
            InvoiceAddressSnapshotJson = "{}",
            DerivedStatus = "NEW",
            CreatedAt = Now.AddDays(-4),
            UpdatedAt = Now.AddDays(-4),
            Version = 1
        };
        ShipmentPackage OpenPackage(Order order) => new()
        {
            Id = Guid.CreateVersion7(),
            TenantId = TenantId,
            ConnectionId = order.ConnectionId,
            OrderId = order.Id,
            ExternalPackageId = $"p-{order.OrderNumber}",
            GrossAmount = 100m,
            NetAmount = 100m,
            Status = ShipmentPackageStatus.New,
            RawStatus = "open",
            StatusOccurredAt = Now.AddDays(-4),
            MarketplaceInvoiceStatus = MarketplaceInvoiceStatus.Unknown,
            CreatedAt = Now.AddDays(-4),
            UpdatedAt = Now.AddDays(-4),
            Version = 1
        };
        ReturnClaim Claim(Order order, ReturnClaimStatus status, string raw, DateTimeOffset createdAt) => new()
        {
            Id = Guid.CreateVersion7(),
            TenantId = TenantId,
            ConnectionId = order.ConnectionId,
            OrderId = order.Id,
            ExternalClaimId = $"c-{order.OrderNumber}-{raw}",
            Status = status,
            RawStatus = raw,
            LastRemoteModifiedAt = createdAt,
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
            Version = 1
        };
        MarketplaceQuestion Question(string label, DateTimeOffset createdAt) => new()
        {
            Id = Guid.CreateVersion7(),
            TenantId = TenantId,
            ConnectionId = Connection(label),
            ExternalQuestionId = $"q-{Guid.NewGuid():N}",
            Kind = "PRODUCT",
            Status = "WAITING_FOR_ANSWER",
            QuestionText = "Beden?",
            CreatedAt = createdAt,
            LastRemoteModifiedAt = createdAt,
            LastSyncedAt = createdAt,
            Version = 1
        };

        // T1: orders, an open package, an open claim from three days ago and a question from ten days ago.
        var t1Order = NewOrder("T1", "T1-1");
        var t1Closed = NewOrder("T1", "T1-2");
        // HB: a closed claim from a week ago; PZ1: an open package; PZ2, PTT and SH: nothing; N11: a question an hour ago.
        var hbOrder = NewOrder("HB", "HB-1");
        var pz1Order = NewOrder("PZ1", "PZ1-1");
        db.Orders.AddRange(t1Order, t1Closed, hbOrder, pz1Order);
        db.ShipmentPackages.AddRange(OpenPackage(t1Order), OpenPackage(pz1Order));
        db.ReturnClaims.AddRange(
            Claim(t1Order, ReturnClaimStatus.Requested, "Created", Now.AddDays(-3)),
            Claim(t1Closed, ReturnClaimStatus.Completed, "Accepted", Now.AddDays(-20)),
            Claim(hbOrder, ReturnClaimStatus.Completed, "Completed", Now.AddDays(-7)));
        db.MarketplaceQuestions.AddRange(Question("T1", Now.AddDays(-10)), Question("N11", Now.AddHours(-1)));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
