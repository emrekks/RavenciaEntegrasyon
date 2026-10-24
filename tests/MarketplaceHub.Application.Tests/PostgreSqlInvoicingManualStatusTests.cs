using System.Text.Json;
using MarketplaceHub.Api.Invoicing;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// Audit 24 Oct 2026: a hand-set invoice status reads the same on every screen (S2), a package marked "Faturası
// yüklendi" gets no second fiscal invoice and no link (S3), and a manual delivery retried after a failed job is
// queued again instead of answering with the finished job (W2).
[Collection("postgres-integration")]
public sealed class PostgreSqlInvoicingManualStatusTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    // S2: the invoice page, the orders list and the order detail give one label for the same package.
    [PostgreSqlFact]
    public async Task S2_InvoicePageAndOrdersShowTheSameHandSetInvoiceLabel()
    {
        foreach (var (platformCode, manualStatus, expected) in new[]
        {
            ("N11", "UPLOADED", "FATURA_KONTROLDE"),
            ("SHOPIFY", "UPLOADED", "FATURA_KONTROLDE"),
            ("PAZARAMA", "UPLOADED", "FATURA_KONTROLDE"),
            ("PTTAVM", "PENDING", "FATURA_BEKLIYOR")
        })
            await AssertSameLabelAsync(platformCode, manualStatus, expected);
    }

    private async Task AssertSameLabelAsync(string platformCode, string manualStatus, string expected)
    {
        var seed = NewSeed("s2-label", platformCode, manualStatus);
        await SeedAsync(seed);
        try
        {
            await using var db = fixture.CreateContext();
            var workspace = await NewBillingService(db).WorkspacePageAsync(seed.Tenant.Id, new InvoiceWorkspacePageQuery(PageNumber: 1, PageSize: 20, Tab: "ALL"), CancellationToken.None);
            var sales = NewSalesService(db);
            var list = await sales.OrdersAsync(seed.Tenant.Id, 50, 1, null, new OrderListQuery(), CancellationToken.None);
            var detail = await sales.OrderAsync(seed.Tenant.Id, seed.Order.Id, CancellationToken.None);

            var pageLabel = Assert.Single(workspace.Items, item => item.PackageId == seed.Package.Id).InvoiceStatus;
            Assert.Equal(expected, pageLabel);
            Assert.Equal(pageLabel, Assert.Single(list.Items, item => item.Id == seed.Order.Id).InvoiceStatus);
            Assert.True(detail.Succeeded, detail.Error?.Message);
            Assert.Equal(pageLabel, detail.Value!.InvoiceStatus);

            // The orders invoice filter lists the order under the same label, and not under the other one.
            var other = expected == "FATURA_KONTROLDE" ? "FATURA_BEKLIYOR" : "FATURA_KONTROLDE";
            var filtered = await sales.OrdersAsync(seed.Tenant.Id, 50, 1, null, new OrderListQuery(Invoice: expected), CancellationToken.None);
            var otherFiltered = await sales.OrdersAsync(seed.Tenant.Id, 50, 1, null, new OrderListQuery(Invoice: other), CancellationToken.None);
            Assert.Contains(filtered.Items, item => item.Id == seed.Order.Id);
            Assert.DoesNotContain(otherFiltered.Items, item => item.Id == seed.Order.Id);
        }
        finally
        {
            await DeleteTenantAsync(seed.Tenant.Id);
        }
    }

    // S3: the fiscal pre-check refuses a package marked uploaded before any marketplace read or provider call.
    [PostgreSqlFact]
    public async Task S3_FiscalPrecheckRefusesAPackageMarkedUploaded()
    {
        var seed = NewSeed("s3-fiscal", "PAZARAMA", "UPLOADED");
        var invoice = NewInvoice(seed, InvoiceStatus.Submitting);
        await SeedAsync(seed, db => db.Invoices.Add(invoice));
        try
        {
            var marketplace = new UnverifiedMarketplace();
            await using (var db = fixture.CreateContext())
            {
                var result = await NewProcessor(db, marketplace).ProcessAsync(seed.Tenant.Id, seed.Provider.Id, InvoicingJobTypes.InvoiceSubmit,
                    JsonSerializer.Serialize(new { invoiceId = invoice.Id }), "s3-fiscal", CancellationToken.None);
                Assert.Equal(JobCompletionKind.Blocked, result.Kind);
                Assert.Equal("PLATFORM_INVOICE_ALREADY_PRESENT", result.ErrorCode);
            }

            Assert.Equal(0, marketplace.Calls);
            await using var verification = fixture.CreateContext();
            var stored = await verification.Invoices.SingleAsync(item => item.Id == invoice.Id);
            Assert.Equal(InvoiceStatus.ManualReview, stored.Status);
            Assert.False(await verification.InvoiceSubmissionAttempts.AnyAsync(attempt => attempt.InvoiceId == invoice.Id));
        }
        finally
        {
            await DeleteTenantAsync(seed.Tenant.Id);
        }
    }

    // S3: the delivery pre-check sends no link for a package marked uploaded, whatever the platform.
    [PostgreSqlFact]
    public async Task S3_DeliveryPrecheckSendsNoLinkForAPackageMarkedUploaded()
    {
        var seed = NewSeed("s3-deliver", "SHOPIFY", "UPLOADED");
        var invoice = NewInvoice(seed, InvoiceStatus.MarketplacePending);
        await SeedAsync(seed, db => db.Invoices.Add(invoice));
        try
        {
            var marketplace = new UnverifiedMarketplace();
            await using (var db = fixture.CreateContext())
            {
                var result = await NewProcessor(db, marketplace).ProcessAsync(seed.Tenant.Id, seed.Marketplace.Id, InvoicingJobTypes.MarketplaceDelivery,
                    JsonSerializer.Serialize(new { invoiceId = invoice.Id, packageId = seed.Package.Id }), "s3-deliver", CancellationToken.None);
                Assert.Equal("PLATFORM_INVOICE_ALREADY_PRESENT", result.ErrorCode);
            }

            Assert.Equal(0, marketplace.Calls);
            await using var verification = fixture.CreateContext();
            Assert.Equal(InvoiceStatus.ManualReview, (await verification.Invoices.SingleAsync(item => item.Id == invoice.Id)).Status);
            Assert.False(await verification.MarketplaceDeliveryStates.AnyAsync(state => state.InvoiceId == invoice.Id));
        }
        finally
        {
            await DeleteTenantAsync(seed.Tenant.Id);
        }
    }

    // S3: creating an invoice from the order is refused for a package marked uploaded.
    [PostgreSqlFact]
    public async Task S3_CreatingAnInvoiceIsRefusedForAPackageMarkedUploaded()
    {
        var seed = NewSeed("s3-create", "PAZARAMA", "UPLOADED");
        await SeedAsync(seed);
        try
        {
            await using var db = fixture.CreateContext();
            var created = await NewBillingService(db).CreateDraftAsync(seed.Tenant.Id, new CreateInvoiceCommand(seed.Order.Id, seed.Package.Id, seed.Provider.Id, null), "s3-create-key", CancellationToken.None);

            Assert.False(created.Succeeded);
            Assert.Equal("MARKETPLACE_INVOICE_ALREADY_PRESENT", created.Error?.Code);
            await using var verification = fixture.CreateContext();
            Assert.False(await verification.Invoices.AnyAsync(item => item.TenantId == seed.Tenant.Id));
        }
        finally
        {
            await DeleteTenantAsync(seed.Tenant.Id);
        }
    }

    // S3: the workspace preview offers no invoice for a package marked uploaded.
    [PostgreSqlFact]
    public async Task S3_WorkspacePreviewCannotCreateForAPackageMarkedUploaded()
    {
        var seed = NewSeed("s3-preview", "PAZARAMA", "UPLOADED");
        await SeedAsync(seed);
        try
        {
            await using var db = fixture.CreateContext();
            var preview = await NewBillingService(db).PreviewWorkspaceInvoicesAsync(seed.Tenant.Id,
                new InvoiceWorkspacePreviewRequest([new(seed.Order.Id, seed.Package.Id, seed.Provider.Id)]), CancellationToken.None);

            Assert.True(preview.Succeeded, preview.Error?.Message);
            var item = Assert.Single(preview.Value!);
            Assert.False(item.CanConfirm);
            Assert.Contains("fatura yüklendi", item.BlockedReason, StringComparison.Ordinal);
        }
        finally
        {
            await DeleteTenantAsync(seed.Tenant.Id);
        }
    }

    // W2: the same key after a finished (failed) job queues a new job; while a job waits, it is answered with that job.
    [PostgreSqlFact]
    public async Task W2_ManualDeliveryRetriedAfterAFailedJobIsQueuedAgain()
    {
        var seed = NewSeed("w2-retry", "TRENDYOL", null);
        var document = new ManualOrderInvoiceDocument
        {
            Id = Guid.CreateVersion7(),
            TenantId = seed.Tenant.Id,
            OrderId = seed.Order.Id,
            ConnectionId = seed.Marketplace.Id,
            InvoiceNumber = "W2A2026000000001",
            IssuedAt = fixture.Now.AddDays(-1),
            IdempotencyKey = $"w2:{Guid.NewGuid():N}",
            DocumentType = "URL",
            DocumentUrl = "https://example.com/w2/invoice.pdf",
            Status = "ACTIVE",
            CreatedAt = fixture.Now,
            UpdatedAt = fixture.Now
        };
        await SeedAsync(seed, db => db.ManualOrderInvoiceDocuments.Add(document));
        try
        {
            const string key = "manual-invoice-delivery:w2:same-key";
            var first = await QueueManualDeliveryAsync(seed, document.Id, key);
            Assert.Equal(StatusCodes.Status202Accepted, first);
            // A second click while the job waits reaches the same job.
            Assert.Equal(StatusCodes.Status202Accepted, await QueueManualDeliveryAsync(seed, document.Id, key));
            Assert.Equal(1, await CountDeliveryJobsAsync(seed.Tenant.Id));

            // The job failed for good, twice in a row (FAILED -> FAILED).
            for (var attempt = 2; attempt <= 3; attempt++)
            {
                await FailDeliveryAsync(seed.Tenant.Id, document.Id);
                Assert.Equal(StatusCodes.Status202Accepted, await QueueManualDeliveryAsync(seed, document.Id, key));
                Assert.Equal(attempt, await CountDeliveryJobsAsync(seed.Tenant.Id));
                await using var db = fixture.CreateContext();
                Assert.Equal(1, await db.IntegrationJobs.CountAsync(job => job.TenantId == seed.Tenant.Id
                    && job.JobType == InvoicingJobTypes.ManualOrderInvoiceDelivery && job.Status == JobStatus.Pending));
                Assert.Equal("QUEUED", (await db.ManualOrderInvoiceDeliveryStates.SingleAsync(state => state.DocumentId == document.Id)).Status);
            }
        }
        finally
        {
            await DeleteTenantAsync(seed.Tenant.Id);
        }
    }

    private async Task<int> QueueManualDeliveryAsync(Seed seed, Guid documentId, string key)
    {
        await using var db = fixture.CreateContext();
        var http = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection()
                .AddSingleton<ITenantContextAccessor>(new TenantAccessor(new(Guid.NewGuid(), seed.Tenant.Id, "OWNER")))
                .BuildServiceProvider()
        };
        http.Request.Headers["Idempotency-Key"] = key;
        var result = await InvoicingEndpoints.QueueManualOrderInvoiceDeliveryAsync(documentId, http, db, fixture.TimeProvider);
        var status = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode ?? 0;
        Assert.True(status == StatusCodes.Status202Accepted, $"{status}: {JsonSerializer.Serialize((result as IValueHttpResult)?.Value)}");
        return status;
    }

    private async Task<int> CountDeliveryJobsAsync(Guid tenantId)
    {
        await using var db = fixture.CreateContext();
        return await db.IntegrationJobs.CountAsync(job => job.TenantId == tenantId && job.JobType == InvoicingJobTypes.ManualOrderInvoiceDelivery);
    }

    private async Task FailDeliveryAsync(Guid tenantId, Guid documentId)
    {
        await using var db = fixture.CreateContext();
        await db.IntegrationJobs.Where(job => job.TenantId == tenantId && job.JobType == InvoicingJobTypes.ManualOrderInvoiceDelivery && job.Status == JobStatus.Pending)
            .ExecuteUpdateAsync(update => update.SetProperty(job => job.Status, JobStatus.Dead));
        await db.ManualOrderInvoiceDeliveryStates.Where(state => state.TenantId == tenantId && state.DocumentId == documentId)
            .ExecuteUpdateAsync(update => update.SetProperty(state => state.Status, "FAILED"));
    }

    private InvoicingBillingService NewBillingService(AppDbContext db)
    {
        var dataProtection = new EphemeralDataProtectionProvider();
        return new InvoicingBillingService(db, new CursorCodec(dataProtection, fixture.TimeProvider), dataProtection, null!,
            new ConfigurationBuilder().Build(), fixture.TimeProvider, null!);
    }

    private MarketplaceSalesService NewSalesService(AppDbContext db) =>
        new(db, new CursorCodec(new EphemeralDataProtectionProvider(), fixture.TimeProvider), new ConfigurationBuilder().Build(), null!, null!, null!, null!, fixture.TimeProvider);

    private InvoicingJobProcessor NewProcessor(AppDbContext db, IInvoiceMarketplacePort marketplace) =>
        new(db, null!, marketplace, null!, fixture.TimeProvider, new ConfigurationBuilder().Build(), new EphemeralDataProtectionProvider());

    private sealed record Seed(Tenant Tenant, PlatformConnection Marketplace, PlatformConnection Provider, Order Order, ShipmentPackage Package, LegalEntityProfile Profile, InvoicePolicy Policy);

    private Seed NewSeed(string code, string platformCode, string? manualInvoiceStatus)
    {
        var tenant = new Tenant { Id = Guid.CreateVersion7(), Code = $"{code}-{Guid.NewGuid():N}"[..40], DisplayName = code, Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = fixture.Now, UpdatedAt = fixture.Now, Version = 1 };
        var marketplace = NewConnection(tenant.Id, platformCode, $"{code}-store");
        var provider = NewConnection(tenant.Id, "TRENDYOL_EFATURAM", $"{code}-efaturam");
        var order = new Order
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenant.Id,
            ConnectionId = marketplace.Id,
            ExternalOrderId = $"{code}-order",
            OrderNumber = $"{code}-order",
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
            MarketplaceInvoiceStatus = MarketplaceInvoiceStatus.Unknown,
            ManualInvoiceStatus = manualInvoiceStatus,
            CreatedAt = fixture.Now,
            UpdatedAt = fixture.Now,
            Version = 1
        };
        var profile = new LegalEntityProfile { Id = Guid.CreateVersion7(), TenantId = tenant.Id, Title = "profile", ProtectedTaxId = "protected", MaskedTaxId = "E-Faturam", AddressSnapshotJson = "{}", ContactSnapshotJson = "{}", Status = "ACTIVE", CreatedAt = fixture.Now, UpdatedAt = fixture.Now };
        var policy = new InvoicePolicy { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ProviderConnectionId = provider.Id, TriggerState = "MANUAL_CONFIRMED", PackageScope = "SHIPMENT_PACKAGE", DueRule = "IMMEDIATE", RoundingRule = "LINE_HALF_AWAY_FROM_ZERO", AdjustmentRule = "REJECT_OVER_ONE_KURUS", CreatedAt = fixture.Now, UpdatedAt = fixture.Now };
        return new(tenant, marketplace, provider, order, package, profile, policy);
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

    private Invoice NewInvoice(Seed seed, InvoiceStatus status) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = seed.Tenant.Id,
        OrderId = seed.Order.Id,
        PackageId = seed.Package.Id,
        OrderFiscalGuarded = true,
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
        IdempotencyKey = $"manual-status:{Guid.NewGuid():N}",
        ExternalReference = status == InvoiceStatus.Submitting ? null : $"remote-{Guid.NewGuid():N}",
        InvoiceNumber = status == InvoiceStatus.Submitting ? null : "GIB2026000000001",
        IssuedAt = fixture.Now.AddDays(-1),
        CreatedAt = fixture.Now.AddDays(-1),
        UpdatedAt = fixture.Now.AddDays(-1),
        Version = 1
    };

    private async Task SeedAsync(Seed seed, Action<AppDbContext>? extra = null)
    {
        await using var db = fixture.CreateContext();
        db.Tenants.Add(seed.Tenant);
        db.PlatformConnections.AddRange(seed.Marketplace, seed.Provider);
        db.Orders.Add(seed.Order);
        db.OrderLines.Add(new OrderLine
        {
            Id = Guid.CreateVersion7(),
            TenantId = seed.Tenant.Id,
            OrderId = seed.Order.Id,
            ExternalLineId = $"{seed.Order.ExternalOrderId}-line",
            Sku = "SKU-1",
            Barcode = "BARCODE-1",
            TitleSnapshot = "Ürün",
            OrderedQuantity = 1,
            UnitPrice = 120m,
            VatRate = 20m,
            RawStatus = "Delivered",
            Version = 1
        });
        db.ShipmentPackages.Add(seed.Package);
        db.LegalEntityProfiles.Add(seed.Profile);
        db.InvoicePolicies.Add(seed.Policy);
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
        await db.ManualOrderInvoiceDeliveryStates.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.ManualOrderInvoiceDocuments.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.MarketplaceInvoiceReferenceHistories.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.MarketplaceDeliveries.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.MarketplaceDeliveryStates.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.InvoiceDocuments.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.InvoiceSubmissionAttempts.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.InvoicePartySnapshots.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.InvoiceLines.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.Invoices.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.InvoicePolicies.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.LegalEntityProfiles.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.ShipmentPackages.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.OrderLines.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.Orders.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.PlatformConnections.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.Tenants.Where(row => row.Id == tenantId).ExecuteDeleteAsync();
    }

    // Answers every read as Pazarama now does before a delivery (UNVERIFIED) and refuses any write.
    private sealed class UnverifiedMarketplace : IInvoiceMarketplacePort
    {
        public int Calls { get; private set; }

        public Task<AdapterResult<InvoiceDeliveryResult>> DeliverAsync(AdapterContext context, InvoiceDeliveryCommand command, CancellationToken cancellationToken)
        {
            Calls++;
            throw new InvalidOperationException("A delivery must not be sent.");
        }

        public Task<AdapterResult<InvoiceDeliveryStatus>> QueryDeliveryAsync(AdapterContext context, ExternalInvoiceDeliveryReference reference, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(AdapterResult<InvoiceDeliveryStatus>.Success(new(reference.ExternalReference, MarketplaceInvoiceStatePolicy.Unverified, true)));
        }
    }

    private sealed class TenantAccessor(TenantContext context) : ITenantContextAccessor
    {
        public TenantContext? Current => context;
    }
}
