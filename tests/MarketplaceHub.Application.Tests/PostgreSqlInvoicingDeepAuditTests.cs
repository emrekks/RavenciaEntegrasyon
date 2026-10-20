using System.Text.Json;
using MarketplaceHub.Api.Invoicing;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Identity;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// Invoicing findings of the 20 Oct 2026 deep audit that need a real database (G1, G18, G19, G36).
[Collection("postgres-integration")]
public sealed class PostgreSqlInvoicingDeepAuditTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    private static readonly Dictionary<string, string?> OpenWrites = new()
    {
        ["FeatureFlags:ExternalWrites"] = "true",
        ["FeatureFlags:InvoiceMarketplaceDeliveryWrites"] = "true"
    };

    [PostgreSqlFact]
    public async Task G1_DeliveryJobOfAnInvoiceBeingCancelledIsBlockedAndSendsNothing()
    {
        var seed = NewSeed("g1-cancel", InvoiceStatus.CancellationPending);
        await SeedAsync(seed);
        try
        {
            var marketplace = new RecordingMarketplace();
            await using (var db = fixture.CreateContext())
            {
                var result = await NewProcessor(db, marketplace).ProcessAsync(seed.Tenant.Id, seed.Marketplace.Id, InvoicingJobTypes.MarketplaceDelivery,
                    DeliveryPayload(seed), "g1-cancel", CancellationToken.None);
                Assert.Equal(JobCompletionKind.Blocked, result.Kind);
                Assert.Equal(InvoiceDeliveryGuardPolicy.InvoiceNotDeliverableCode, result.ErrorCode);
            }

            Assert.Equal(0, marketplace.Calls);
            await using var verification = fixture.CreateContext();
            Assert.Equal(InvoiceStatus.CancellationPending, (await verification.Invoices.SingleAsync(item => item.Id == seed.Invoice.Id)).Status);
            Assert.False(await verification.MarketplaceDeliveryStates.AnyAsync(state => state.InvoiceId == seed.Invoice.Id));
        }
        finally
        {
            await DeleteTenantAsync(seed.Tenant.Id);
        }
    }

    [PostgreSqlFact]
    public async Task G1_APackageWhoseLinkIsInRemovalIsNeverSentAgain()
    {
        var seed = NewSeed("g1-revoke", InvoiceStatus.MarketplacePending);
        var state = NewDeliveryState(seed, MarketplaceInvoiceLinkRevocationPolicy.Removed);
        await SeedAsync(seed, db => db.MarketplaceDeliveryStates.Add(state));
        try
        {
            var marketplace = new RecordingMarketplace();
            await using (var db = fixture.CreateContext())
            {
                var result = await NewProcessor(db, marketplace).ProcessAsync(seed.Tenant.Id, seed.Marketplace.Id, InvoicingJobTypes.MarketplaceDelivery,
                    DeliveryPayload(seed), "g1-revoke", CancellationToken.None);
                Assert.Equal(InvoiceDeliveryGuardPolicy.LinkRevocationOpenCode, result.ErrorCode);
            }

            Assert.Equal(0, marketplace.Calls);
            await using var verification = fixture.CreateContext();
            Assert.Equal(MarketplaceInvoiceLinkRevocationPolicy.Removed, (await verification.MarketplaceDeliveryStates.SingleAsync(item => item.Id == state.Id)).Status);
        }
        finally
        {
            await DeleteTenantAsync(seed.Tenant.Id);
        }
    }

    [PostgreSqlFact]
    public async Task G1_CancellingAnInvoiceCancelsItsQueuedDeliveriesOnly()
    {
        var seed = NewSeed("g1-queue", InvoiceStatus.Accepted);
        seed.Provider.SettingsJson = JsonSerializer.Serialize(new { ExternalWritesEnabled = true });
        var other = NewInvoice(seed, InvoiceStatus.Accepted);
        var otherOrder = NewOrder(seed.Tenant.Id, seed.Marketplace.Id, "g1-queue-other");
        other.OrderId = otherOrder.Id;
        var pending = NewDeliveryJob(seed, seed.Invoice.Id, "k1", JobStatus.Pending, 1);
        var retrying = NewDeliveryJob(seed, seed.Invoice.Id, "k2", JobStatus.RetryScheduled, 2);
        var leased = NewDeliveryJob(seed, seed.Invoice.Id, "k3", JobStatus.Leased, 3);
        var otherInvoiceJob = NewDeliveryJob(seed, other.Id, "k4", JobStatus.Pending, 4);
        await SeedAsync(seed, db =>
        {
            db.Orders.Add(otherOrder);
            db.Invoices.Add(other);
            db.IntegrationJobs.AddRange(pending, retrying, leased, otherInvoiceJob);
        });
        try
        {
            await using (var db = fixture.CreateContext())
            {
                var queued = await NewBillingService(db, OpenWrites).EnqueueCancellationAsync(seed.Tenant.Id, seed.Invoice.Id, seed.Invoice.Version, "g1-cancel-key", "g1-queue", CancellationToken.None);
                Assert.True(queued.Succeeded, queued.Error?.Message);
            }

            await using var verification = fixture.CreateContext();
            var statuses = await verification.IntegrationJobs.Where(job => job.TenantId == seed.Tenant.Id && job.JobType == InvoicingJobTypes.MarketplaceDelivery)
                .ToDictionaryAsync(job => job.Id, job => job.Status);
            Assert.Equal(JobStatus.Cancelled, statuses[pending.Id]);
            Assert.Equal(JobStatus.Cancelled, statuses[retrying.Id]);
            // A leased job is the worker's; the delivery itself refuses the invoice once it is being cancelled.
            Assert.Equal(JobStatus.Leased, statuses[leased.Id]);
            Assert.Equal(JobStatus.Pending, statuses[otherInvoiceJob.Id]);
            Assert.Equal(InvoiceStatus.CancellationPending, (await verification.Invoices.SingleAsync(item => item.Id == seed.Invoice.Id)).Status);
        }
        finally
        {
            await DeleteTenantAsync(seed.Tenant.Id);
        }
    }

    [PostgreSqlFact]
    public async Task CancellingAnInvoiceToleratesADeliveryJobLeasedWhileItWasBeingCancelled()
    {
        var seed = NewSeed("cancel-leased-meanwhile", InvoiceStatus.Accepted);
        seed.Provider.SettingsJson = JsonSerializer.Serialize(new { ExternalWritesEnabled = true });
        var takenMeanwhile = NewDeliveryJob(seed, seed.Invoice.Id, "k1", JobStatus.Pending, 1);
        var waiting = NewDeliveryJob(seed, seed.Invoice.Id, "k2", JobStatus.RetryScheduled, 2);
        await SeedAsync(seed, db => db.IntegrationJobs.AddRange(takenMeanwhile, waiting));
        try
        {
            var connectionString = Environment.GetEnvironmentVariable("MARKETPLACEHUB_TEST_CONNECTION")!;
            var leaseMeanwhile = new LeaseJobBeforeCancellationSave(fixture, takenMeanwhile.Id);
            await using (var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connectionString).AddInterceptors(leaseMeanwhile).Options))
            {
                var queued = await NewBillingService(db, OpenWrites).EnqueueCancellationAsync(seed.Tenant.Id, seed.Invoice.Id, seed.Invoice.Version, "cancel-leased-key", "cancel-leased", CancellationToken.None);
                Assert.True(queued.Succeeded, queued.Error?.Message);
            }

            Assert.True(leaseMeanwhile.Fired);
            await using var verification = fixture.CreateContext();
            Assert.Equal(JobStatus.Leased, (await verification.IntegrationJobs.SingleAsync(job => job.Id == takenMeanwhile.Id)).Status);
            Assert.Equal(JobStatus.Cancelled, (await verification.IntegrationJobs.SingleAsync(job => job.Id == waiting.Id)).Status);
            Assert.Equal(InvoiceStatus.CancellationPending, (await verification.Invoices.SingleAsync(item => item.Id == seed.Invoice.Id)).Status);
            Assert.True(await verification.IntegrationJobs.AnyAsync(job => job.TenantId == seed.Tenant.Id && job.JobType == InvoicingJobTypes.InvoiceCancellation));
        }
        finally
        {
            await DeleteTenantAsync(seed.Tenant.Id);
        }
    }

    // A worker leases the job between the cancellation's read of the waiting deliveries and its save.
    private sealed class LeaseJobBeforeCancellationSave(PostgreSqlTenantIsolationFixture fixture, Guid jobId) : Microsoft.EntityFrameworkCore.Diagnostics.SaveChangesInterceptor
    {
        public bool Fired { get; private set; }

        public override async ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int>> SavingChangesAsync(Microsoft.EntityFrameworkCore.Diagnostics.DbContextEventData eventData, Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!Fired && eventData.Context!.ChangeTracker.Entries<IntegrationJob>().Any(entry => entry.Entity.Id == jobId && entry.Entity.Status == JobStatus.Cancelled))
            {
                Fired = true;
                await using var worker = fixture.CreateContext();
                await worker.IntegrationJobs.Where(job => job.Id == jobId).ExecuteUpdateAsync(setters => setters
                    .SetProperty(job => job.Status, JobStatus.Leased)
                    .SetProperty(job => job.Version, job => job.Version + 1), cancellationToken);
            }
            return await base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    [PostgreSqlFact]
    public async Task G1_ACancellationReadFromTheProviderCancelsQueuedDeliveries()
    {
        var seed = NewSeed("g1-recon", InvoiceStatus.CancellationPending);
        var state = NewDeliveryState(seed, "CONFIRMED");
        var pending = NewDeliveryJob(seed, seed.Invoice.Id, "k1", JobStatus.Pending, 1);
        await SeedAsync(seed, db => { db.MarketplaceDeliveryStates.Add(state); db.IntegrationJobs.Add(pending); });
        try
        {
            await using (var db = fixture.CreateContext())
                Assert.Equal(JobCompletionKind.Succeeded, (await NewProcessor(db, null, provider: new CancelledProvider()).ProcessAsync(seed.Tenant.Id, seed.Provider.Id,
                    InvoicingJobTypes.InvoiceReconcile, $"{{\"invoiceId\":\"{seed.Invoice.Id}\"}}", "g1-recon", CancellationToken.None)).Kind);

            await using var verification = fixture.CreateContext();
            Assert.Equal(MarketplaceInvoiceLinkRevocationPolicy.Required, (await verification.MarketplaceDeliveryStates.SingleAsync(item => item.Id == state.Id)).Status);
            Assert.Equal(JobStatus.Cancelled, (await verification.IntegrationJobs.SingleAsync(job => job.Id == pending.Id)).Status);
        }
        finally
        {
            await DeleteTenantAsync(seed.Tenant.Id);
        }
    }

    [PostgreSqlFact]
    public async Task G19_DueScanSetsTheIssuingDeadlineOfADraftMadeBeforeDelivery()
    {
        var seed = NewSeed("g19-due", InvoiceStatus.Ready);
        seed.Invoice.ExternalReference = null;
        seed.Invoice.InvoiceNumber = null;
        seed.Invoice.DueAt = null;
        seed.Package.StatusOccurredAt = fixture.Now.AddDays(-(DashboardMetricPolicy.InvoiceDueDays + 1));
        await SeedAsync(seed);
        try
        {
            await using (var db = fixture.CreateContext())
                Assert.Equal(JobCompletionKind.Succeeded, (await NewProcessor(db, null).ProcessAsync(seed.Tenant.Id, null, InvoicingJobTypes.InvoiceDueScan, "{}", "g19-due", CancellationToken.None)).Kind);

            await using var verification = fixture.CreateContext();
            var invoice = await verification.Invoices.SingleAsync(item => item.Id == seed.Invoice.Id);
            Assert.NotNull(invoice.DueAt);
            // The database keeps microseconds; the seed's clock has finer ticks.
            Assert.True(Math.Abs((seed.Package.StatusOccurredAt.AddDays(DashboardMetricPolicy.InvoiceDueDays) - invoice.DueAt.Value).TotalMilliseconds) < 1);
            // DueAt is derived: the operator's If-Match stays valid.
            Assert.Equal(seed.Invoice.Version, invoice.Version);
            Assert.True(await verification.OperationalIssues.AnyAsync(issue => issue.TenantId == seed.Tenant.Id && issue.DedupeKey == $"invoice-due:{seed.Invoice.Id:N}"));
        }
        finally
        {
            await DeleteTenantAsync(seed.Tenant.Id);
        }
    }

    [PostgreSqlFact]
    public async Task G36_TwoHepsiburadaChecksAtOnceAnswerConflictInsteadOfServerError()
    {
        var seed = NewSeed("g36-race", InvoiceStatus.Cancelled, "HEPSIBURADA");
        seed.Invoice.SequencePurpose = "REPLACED";
        var state = NewDeliveryState(seed, "CONFIRMED");
        await SeedAsync(seed, db => db.MarketplaceDeliveryStates.Add(state));
        try
        {
            await using (var db = fixture.CreateContext())
            {
                var tracked = await db.Invoices.SingleAsync(item => item.Id == seed.Invoice.Id);
                await InvoiceAdjustmentTracking.MarkLinkRevocationRequiredAsync(db, tracked, fixture.Now, CancellationToken.None);
                await db.SaveChangesAsync();
            }

            var marketplace = new RecordingMarketplace { Status = AdapterResult<InvoiceDeliveryStatus>.Success(new(seed.Package.ExternalPackageId, "NOT_INVOICED", true)) };
            ServiceResult<MarketplaceLinkRevocationView>? concurrent = null;
            // While the first check reads the package, a second check (another tab) finishes the same removal.
            marketplace.OnFirstQuery = async () =>
            {
                await using var other = fixture.CreateContext();
                concurrent = await NewBillingService(other, new Dictionary<string, string?>(), marketplace).RevokeMarketplaceLinkAsync(seed.Tenant.Id, seed.Invoice.Id, state.Id, "tab-2", "g36-race", CancellationToken.None);
            };
            await using (var db = fixture.CreateContext())
            {
                var first = await NewBillingService(db, new Dictionary<string, string?>(), marketplace).RevokeMarketplaceLinkAsync(seed.Tenant.Id, seed.Invoice.Id, state.Id, "tab-1", "g36-race", CancellationToken.None);
                Assert.Equal("INVOICE_LINK_REVOCATION_RUNNING", first.Error?.Code);
                Assert.Equal(409, first.Error?.Status);
            }

            Assert.True(concurrent?.Succeeded, concurrent?.Error?.Message);
            await using var verification = fixture.CreateContext();
            Assert.Equal(MarketplaceInvoiceLinkRevocationPolicy.Removed, (await verification.MarketplaceDeliveryStates.SingleAsync(item => item.Id == state.Id)).Status);
        }
        finally
        {
            await DeleteTenantAsync(seed.Tenant.Id);
        }
    }

    [PostgreSqlFact]
    public async Task G18_LinkRemovalAsksForThePasswordOnALiveProviderOnly()
    {
        var live = NewSeed("g18-live", InvoiceStatus.Cancelled);
        var stage = NewSeed("g18-stage", InvoiceStatus.Cancelled);
        stage.Provider.Environment = "STAGE";
        await SeedAsync(live);
        await SeedAsync(stage);
        try
        {
            await using var db = fixture.CreateContext();
            using var users = new UserManager<ApplicationUser>(new NoUsers(), null!, null!, [], [], null!, null!, null!, NullLogger<UserManager<ApplicationUser>>.Instance);

            var refused = await InvoicingEndpoints.FiscalStepUpAsync(live.Invoice.Id, "wrong", new(Guid.NewGuid(), live.Tenant.Id, "OWNER"), new DefaultHttpContext(), users, db);
            Assert.Equal(401, Assert.IsAssignableFrom<IStatusCodeHttpResult>(refused).StatusCode);
            Assert.Null(await InvoicingEndpoints.FiscalStepUpAsync(stage.Invoice.Id, null, new(Guid.NewGuid(), stage.Tenant.Id, "OWNER"), new DefaultHttpContext(), users, db));
            var missing = await InvoicingEndpoints.FiscalStepUpAsync(Guid.NewGuid(), null, new(Guid.NewGuid(), live.Tenant.Id, "OWNER"), new DefaultHttpContext(), users, db);
            Assert.Equal(404, Assert.IsAssignableFrom<IStatusCodeHttpResult>(missing).StatusCode);
        }
        finally
        {
            await DeleteTenantAsync(live.Tenant.Id);
            await DeleteTenantAsync(stage.Tenant.Id);
        }
    }

    private InvoicingBillingService NewBillingService(AppDbContext db, IDictionary<string, string?> configuration, IInvoiceMarketplacePort? marketplace = null)
    {
        var dataProtection = new EphemeralDataProtectionProvider();
        return new InvoicingBillingService(db, new CursorCodec(dataProtection, fixture.TimeProvider), dataProtection, null!,
            new ConfigurationBuilder().AddInMemoryCollection(configuration).Build(), fixture.TimeProvider, null!, marketplace);
    }

    private InvoicingJobProcessor NewProcessor(AppDbContext db, IInvoiceMarketplacePort? marketplace, IDictionary<string, string?>? configuration = null, IInvoiceProviderPort? provider = null) =>
        new(db, provider!, marketplace!, null!, fixture.TimeProvider,
            new ConfigurationBuilder().AddInMemoryCollection(configuration ?? new Dictionary<string, string?>()).Build(), new EphemeralDataProtectionProvider());

    private static string DeliveryPayload(Seed seed) => JsonSerializer.Serialize(new { invoiceId = seed.Invoice.Id, packageId = seed.Package.Id });

    private sealed record Seed(Tenant Tenant, PlatformConnection Marketplace, PlatformConnection Provider, Order Order, ShipmentPackage Package, LegalEntityProfile Profile, InvoicePolicy Policy, Invoice Invoice);

    private Seed NewSeed(string code, InvoiceStatus status, string platformCode = "TRENDYOL")
    {
        var tenant = new Tenant { Id = Guid.CreateVersion7(), Code = $"{code}-{Guid.NewGuid():N}"[..40], DisplayName = code, Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = fixture.Now, UpdatedAt = fixture.Now, Version = 1 };
        var marketplace = NewConnection(tenant.Id, platformCode, $"{code}-store");
        var provider = NewConnection(tenant.Id, "TRENDYOL_EFATURAM", $"{code}-efaturam");
        var order = NewOrder(tenant.Id, marketplace.Id, $"{code}-order");
        var package = new ShipmentPackage
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenant.Id,
            ConnectionId = marketplace.Id,
            OrderId = order.Id,
            ExternalPackageId = $"{code}-package",
            GrossAmount = 120m,
            NetAmount = 120m,
            CargoProviderExternalId = "YKMP",
            Status = ShipmentPackageStatus.Delivered,
            RawStatus = "Delivered",
            StatusOccurredAt = fixture.Now.AddDays(-1),
            MarketplaceInvoiceStatus = MarketplaceInvoiceStatus.NotInvoiced,
            CreatedAt = fixture.Now,
            UpdatedAt = fixture.Now,
            Version = 1
        };
        var profile = new LegalEntityProfile { Id = Guid.CreateVersion7(), TenantId = tenant.Id, Title = "profile", ProtectedTaxId = "protected", MaskedTaxId = "E-Faturam", AddressSnapshotJson = "{}", ContactSnapshotJson = "{}", Status = "ACTIVE", CreatedAt = fixture.Now, UpdatedAt = fixture.Now };
        var policy = new InvoicePolicy { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ProviderConnectionId = provider.Id, TriggerState = "MANUAL_CONFIRMED", PackageScope = "SHIPMENT_PACKAGE", DueRule = "IMMEDIATE", RoundingRule = "LINE_HALF_AWAY_FROM_ZERO", AdjustmentRule = "REJECT_OVER_ONE_KURUS", CreatedAt = fixture.Now, UpdatedAt = fixture.Now };
        var seed = new Seed(tenant, marketplace, provider, order, package, profile, policy, null!);
        return seed with { Invoice = NewInvoice(seed, status) };
    }

    private static PlatformConnection NewConnection(Guid tenantId, string platformCode, string storeId) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = tenantId,
        PublicId = Guid.CreateVersion7(),
        PlatformCode = platformCode,
        Environment = "PRODUCTION",
        DisplayName = $"{platformCode} test",
        ExternalStoreId = storeId,
        Status = "ACTIVE",
        ApiVersion = platformCode == "TRENDYOL_EFATURAM" ? "1.0.0" : "V1"
    };

    private Order NewOrder(Guid tenantId, Guid connectionId, string externalId) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = tenantId,
        ConnectionId = connectionId,
        ExternalOrderId = externalId,
        OrderNumber = externalId,
        Currency = "TRY",
        GrossAmount = 120m,
        NetAmount = 120m,
        OrderedAt = fixture.Now.AddDays(-3),
        LastRemoteModifiedAt = fixture.Now.AddDays(-3),
        CustomerSnapshotJson = "{}",
        ShipmentAddressSnapshotJson = "{}",
        InvoiceAddressSnapshotJson = "{}",
        DerivedStatus = "DELIVERED",
        CreatedAt = fixture.Now,
        UpdatedAt = fixture.Now,
        Version = 1
    };

    private Invoice NewInvoice(Seed seed, InvoiceStatus status) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = seed.Tenant.Id,
        OrderId = seed.Order.Id,
        OrderFiscalGuarded = status is not (InvoiceStatus.Cancelled or InvoiceStatus.CancelledLocal),
        ProviderConnectionId = seed.Provider.Id,
        LegalEntityProfileId = seed.Profile.Id,
        InvoicePolicyId = seed.Policy.Id,
        InvoiceType = "EARSIVFATURA",
        SequencePurpose = "SALE",
        Status = status,
        Currency = "TRY",
        TaxExclusiveTotal = 100m,
        TaxTotal = 20m,
        PayableTotal = 120m,
        Note = InvoiceAmounts.TurkishInvoiceNote(120m),
        IdempotencyKey = $"deep-audit:{Guid.NewGuid():N}",
        ExternalReference = $"remote-{Guid.NewGuid():N}",
        InvoiceNumber = "GIB2026000000001",
        IssuedAt = fixture.Now.AddDays(-1),
        CreatedAt = fixture.Now.AddDays(-1),
        UpdatedAt = fixture.Now.AddDays(-1),
        Version = 1
    };

    private MarketplaceDeliveryState NewDeliveryState(Seed seed, string status) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = seed.Tenant.Id,
        InvoiceId = seed.Invoice.Id,
        ConnectionId = seed.Marketplace.Id,
        PackageId = seed.Package.Id,
        AttemptNumber = 1,
        ExternalIdempotencyKey = $"delivery:{seed.Invoice.Id:N}:{seed.Package.Id:N}",
        RequestHash = "deep-audit-hash",
        DeliveryType = "LINK",
        Status = status,
        ExternalReference = seed.Package.ExternalPackageId,
        CreatedAt = fixture.Now.AddDays(-1),
        UpdatedAt = fixture.Now.AddDays(-1),
        CompletedAt = fixture.Now.AddDays(-1),
        Version = 1
    };

    // Ids one millisecond apart keep the jobs' order stable.
    private IntegrationJob NewDeliveryJob(Seed seed, Guid invoiceId, string key, JobStatus status, int order)
    {
        var payload = JsonSerializer.Serialize(new { invoiceId });
        return new IntegrationJob
        {
            Id = Guid.CreateVersion7(fixture.Now.AddMilliseconds(order)),
            TenantId = seed.Tenant.Id,
            ConnectionId = seed.Marketplace.Id,
            JobType = InvoicingJobTypes.MarketplaceDelivery,
            PayloadJson = payload,
            PayloadVersion = 1,
            PayloadHash = $"hash-{key}",
            JobDedupKey = $"{InvoicingJobTypes.MarketplaceDelivery}:{invoiceId}:{key}",
            EffectIdempotencyKey = $"{InvoicingJobTypes.MarketplaceDelivery}:{invoiceId}:{key}",
            Status = status,
            // Far in the future, so a lease test sharing the database never takes these jobs.
            AvailableAt = fixture.Now.AddYears(5),
            LeaseExpiresAt = status == JobStatus.Leased ? fixture.Now.AddYears(5) : null,
            CorrelationId = $"deep-audit-{key}",
            CreatedAt = fixture.Now,
            Version = 1
        };
    }

    private async Task SeedAsync(Seed seed, Action<AppDbContext>? extra = null)
    {
        await using var db = fixture.CreateContext();
        db.Tenants.Add(seed.Tenant);
        db.PlatformConnections.AddRange(seed.Marketplace, seed.Provider);
        db.Orders.Add(seed.Order);
        db.ShipmentPackages.Add(seed.Package);
        db.LegalEntityProfiles.Add(seed.Profile);
        db.InvoicePolicies.Add(seed.Policy);
        db.Invoices.Add(seed.Invoice);
        extra?.Invoke(db);
        await db.SaveChangesAsync();
    }

    private async Task DeleteTenantAsync(Guid tenantId)
    {
        await using var db = fixture.CreateContext();
        var jobIds = await db.IntegrationJobs.Where(row => row.TenantId == tenantId).Select(row => row.Id).ToListAsync();
        await db.JobAttempts.Where(row => row.TenantId == tenantId || jobIds.Contains(row.JobId)).ExecuteDeleteAsync();
        await db.IntegrationJobs.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.IntegrationOutboxEvents.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.AuditLogs.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.OperationalIssues.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.MarketplaceInvoiceReferenceHistories.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.MarketplaceDeliveries.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.MarketplaceDeliveryStates.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.InvoiceDocuments.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.FileAssets.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.InvoiceSubmissionAttempts.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.InvoicePartySnapshots.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.InvoiceLines.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.Invoices.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.InvoicePolicies.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.LegalEntityProfiles.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.ShipmentPackages.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.Orders.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.PlatformConnections.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.Tenants.Where(row => row.Id == tenantId).ExecuteDeleteAsync();
    }

    private sealed class RecordingMarketplace : IInvoiceMarketplacePort
    {
        private bool fired;
        public AdapterResult<InvoiceDeliveryStatus>? Status { get; set; }
        public Func<Task>? OnFirstQuery { get; set; }
        public int Calls { get; private set; }

        public Task<AdapterResult<InvoiceDeliveryResult>> DeliverAsync(AdapterContext context, InvoiceDeliveryCommand command, CancellationToken cancellationToken)
        {
            Calls++;
            throw new InvalidOperationException("A delivery must not be sent.");
        }

        public async Task<AdapterResult<InvoiceDeliveryStatus>> QueryDeliveryAsync(AdapterContext context, ExternalInvoiceDeliveryReference reference, CancellationToken cancellationToken)
        {
            Calls++;
            if (!fired && OnFirstQuery is not null)
            {
                fired = true;
                await OnFirstQuery();
            }
            return Status ?? throw new InvalidOperationException("No status scripted.");
        }
    }

    private sealed class CancelledProvider : IInvoiceProviderPort
    {
        public Task<AdapterResult<InvoiceRemoteStatus>> QueryStatusAsync(AdapterContext context, ExternalInvoiceReference reference, CancellationToken cancellationToken) =>
            Task.FromResult(AdapterResult<InvoiceRemoteStatus>.Success(new(reference.ExternalReference, "305", "CANCELLED", null, null, true)));
        public Task<AdapterResult<ConnectionIdentity>> TestConnectionAsync(AdapterContext context, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<InvoiceSubmissionResult>> SubmitAsync(AdapterContext context, InvoiceSubmission submission, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<RemoteInvoiceDocument>> GetDocumentAsync(AdapterContext context, ExternalInvoiceReference reference, string documentKind, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<InvoiceCancellationResult>> CancelAsync(AdapterContext context, InvoiceCancellation command, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class NoUsers : IUserStore<ApplicationUser>
    {
        public Task<ApplicationUser?> FindByIdAsync(string userId, CancellationToken cancellationToken) => Task.FromResult<ApplicationUser?>(null);
        public Task<ApplicationUser?> FindByNameAsync(string normalizedUserName, CancellationToken cancellationToken) => Task.FromResult<ApplicationUser?>(null);
        public Task<IdentityResult> CreateAsync(ApplicationUser user, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IdentityResult> DeleteAsync(ApplicationUser user, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IdentityResult> UpdateAsync(ApplicationUser user, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<string?> GetNormalizedUserNameAsync(ApplicationUser user, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<string> GetUserIdAsync(ApplicationUser user, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<string?> GetUserNameAsync(ApplicationUser user, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task SetNormalizedUserNameAsync(ApplicationUser user, string? normalizedName, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task SetUserNameAsync(ApplicationUser user, string? userName, CancellationToken cancellationToken) => throw new NotSupportedException();
        public void Dispose() { }
    }
}
