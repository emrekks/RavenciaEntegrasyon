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

    // Owner, 31 Oct 2026: on a marketplace that does not report the invoice state (n11, PTT AVM, Pazarama) the panel's
    // own record decides. No panel invoice waits for an invoice: it counts as uninvoiced on the invoice page, in its
    // metric and the summary the orders page shows, and the orders filter lists it as waiting.
    [PostgreSqlFact]
    public async Task UnreportedPlatformWithoutAPanelInvoiceWaitsForAnInvoiceOnEveryScreen()
    {
        foreach (var platformCode in new[] { "PAZARAMA", "N11", "PTTAVM" })
        {
            await AssertSameLabelAsync(platformCode, null, "FATURA_BEKLIYOR");
            await AssertWorkspaceCountsAsync(platformCode, seed => { }, uninvoiced: true);
        }
    }

    // An invoice the panel issued and delivered marks the package invoiced: it reads as issued and leaves the waiting work.
    [PostgreSqlFact]
    public async Task PazaramaPackageWithAPanelDeliveredInvoiceIsInvoiced()
    {
        await AssertWorkspaceCountsAsync("PAZARAMA", seed => seed.Package.MarketplaceInvoiceStatus = MarketplaceInvoiceStatus.Invoiced, uninvoiced: false, withInvoice: true);
    }

    private async Task AssertWorkspaceCountsAsync(string platformCode, Action<Seed> arrange, bool uninvoiced, bool withInvoice = false)
    {
        var seed = NewSeed("unreported-counts", platformCode, null);
        arrange(seed);
        var invoice = withInvoice ? NewInvoice(seed, InvoiceStatus.Completed) : null;
        await SeedAsync(seed, invoice is null ? null : db => db.Invoices.Add(invoice));
        try
        {
            await using var db = fixture.CreateContext();
            var billing = NewBillingService(db);
            var all = await billing.WorkspacePageAsync(seed.Tenant.Id, new InvoiceWorkspacePageQuery(PageNumber: 1, PageSize: 20, Tab: "ALL"), CancellationToken.None);
            var waiting = await billing.WorkspacePageAsync(seed.Tenant.Id, new InvoiceWorkspacePageQuery(PageNumber: 1, PageSize: 20, Tab: "UNINVOICED"), CancellationToken.None);
            var invoiced = await billing.WorkspacePageAsync(seed.Tenant.Id, new InvoiceWorkspacePageQuery(PageNumber: 1, PageSize: 20, Tab: "INVOICED"), CancellationToken.None);
            var waitingFilter = await billing.WorkspacePageAsync(seed.Tenant.Id, new InvoiceWorkspacePageQuery(PageNumber: 1, PageSize: 20, Tab: "ALL", InvoiceStatus: "FATURA_BEKLIYOR"), CancellationToken.None);
            var invoicedFilter = await billing.WorkspacePageAsync(seed.Tenant.Id, new InvoiceWorkspacePageQuery(PageNumber: 1, PageSize: 20, Tab: "ALL", InvoiceStatus: "FATURA_KONTROLDE"), CancellationToken.None);
            var summary = await billing.WorkspaceSummaryAsync(seed.Tenant.Id, CancellationToken.None);

            var item = Assert.Single(all.Items);
            Assert.Equal(uninvoiced ? "FATURA_BEKLIYOR" : "FATURA_KONTROLDE", item.InvoiceStatus);
            Assert.Equal(uninvoiced, item.CanCreateInvoice);
            Assert.Equal((uninvoiced ? 1 : 0, uninvoiced ? 0 : 1), (all.UninvoicedCount, all.InvoicedCount));
            Assert.Equal(uninvoiced ? 1 : 0, summary.UninvoicedCount);
            Assert.Equal(uninvoiced ? 1 : 0, waiting.Items.Count);
            Assert.Equal(uninvoiced ? 0 : 1, invoiced.Items.Count);
            Assert.Equal(uninvoiced ? 1 : 0, waitingFilter.Items.Count);
            Assert.Equal(uninvoiced ? 0 : 1, invoicedFilter.Items.Count);
        }
        finally
        {
            await DeleteTenantAsync(seed.Tenant.Id);
        }
    }

    private async Task AssertSameLabelAsync(string platformCode, string? manualStatus, string expected)
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

    // G-4 (29 Oct 2026): an n11 order whose invoice was cancelled kept the cancelled invoice's link as "invoiced" and
    // refused every new invoice. "Fatura Bekliyor" reopens it: the link removal ends, the cancelled invoice leaves the sale
    // slot, and a later order read bringing the old link back no longer counts.
    [PostgreSqlFact]
    public async Task G4_FaturaBekliyorReopensAnN11OrderWhoseInvoiceWasCancelled()
    {
        var seed = NewSeed("g4-n11-reopen", "N11", null);
        seed.Package.MarketplaceInvoiceStatus = MarketplaceInvoiceStatus.Invoiced;
        seed.Package.MarketplaceInvoiceUrl = "https://example.com/g4/old-invoice.pdf";
        var cancelled = NewInvoice(seed, InvoiceStatus.Cancelled);
        var link = NewRevocationState(seed, cancelled);
        await SeedAsync(seed, db => { db.Invoices.Add(cancelled); db.MarketplaceDeliveryStates.Add(link); });
        try
        {
            await using (var db = fixture.CreateContext())
                Assert.False((await NewBillingService(db).CreateDraftAsync(seed.Tenant.Id, new CreateInvoiceCommand(seed.Order.Id, seed.Package.Id, seed.Provider.Id, null), "g4-before", CancellationToken.None)).Succeeded);

            Assert.Equal(1, await SetManualStatusAsync(seed, "PENDING"));
            await using (var verification = fixture.CreateContext())
            {
                var state = await verification.MarketplaceDeliveryStates.SingleAsync(item => item.Id == link.Id);
                Assert.Equal((MarketplaceInvoiceLinkRevocationPolicy.Removed, MarketplaceInvoiceReopenPolicy.ReopenedCode), (state.Status, state.ErrorCode));
                Assert.Equal("REPLACED", (await verification.Invoices.SingleAsync(item => item.Id == cancelled.Id)).SequencePurpose);
                var package = await verification.ShipmentPackages.SingleAsync(item => item.Id == seed.Package.Id);
                Assert.Equal((MarketplaceInvoiceStatus.NotInvoiced, "PENDING", (string?)null), (package.MarketplaceInvoiceStatus, package.ManualInvoiceStatus, package.MarketplaceInvoiceUrl));
            }

            // An order read brings the cancelled invoice's link back: it no longer counts.
            await using (var db = fixture.CreateContext())
                await db.ShipmentPackages.Where(item => item.Id == seed.Package.Id).ExecuteUpdateAsync(setter => setter.SetProperty(item => item.MarketplaceInvoiceStatus, MarketplaceInvoiceStatus.Invoiced));
            Guid draftId;
            await using (var db = fixture.CreateContext())
            {
                var created = await NewBillingService(db).CreateDraftAsync(seed.Tenant.Id, new CreateInvoiceCommand(seed.Order.Id, seed.Package.Id, seed.Provider.Id, null), "g4-after", CancellationToken.None);
                Assert.True(created.Succeeded, created.Error?.Code);
                draftId = created.Value!.Id;
                await db.Invoices.Where(item => item.Id == draftId).ExecuteUpdateAsync(setter => setter.SetProperty(item => item.Status, InvoiceStatus.Submitting));
                // The draft's receiver (with its carrier) was sealed by another test key; without it the order and its
                // package are read directly.
                await db.Invoices.Where(item => item.Id == draftId).ExecuteUpdateAsync(setter => setter.SetProperty(item => item.PackageId, seed.Package.Id));
                await db.InvoicePartySnapshots.Where(item => item.InvoiceId == draftId).ExecuteDeleteAsync();
            }

            // The new invoice passes the fiscal pre-check and reaches the provider.
            var provider = new RefusingProvider();
            await using (var db = fixture.CreateContext())
            {
                var result = await NewProcessor(db, new UnverifiedMarketplace(), provider).ProcessAsync(seed.Tenant.Id, seed.Provider.Id, InvoicingJobTypes.InvoiceSubmit,
                    JsonSerializer.Serialize(new { invoiceId = draftId }), "g4-submit", CancellationToken.None);
                Assert.Equal(RefusingProvider.Code, result.ErrorCode);
            }
            Assert.Equal(1, provider.SubmitCalls);
        }
        finally
        {
            await DeleteTenantAsync(seed.Tenant.Id);
        }
    }

    // G-4: an invoice the marketplace shows without a cancelled invoice of this panel behind it is never set aside.
    [PostgreSqlFact]
    public async Task G4_FaturaBekliyorLeavesAnInvoiceFromElsewhereInPlace()
    {
        var seed = NewSeed("g4-elsewhere", "N11", null);
        seed.Package.MarketplaceInvoiceStatus = MarketplaceInvoiceStatus.Invoiced;
        await SeedAsync(seed);
        try
        {
            Assert.Equal(0, await SetManualStatusAsync(seed, "PENDING"));
            await using var db = fixture.CreateContext();
            Assert.Equal(MarketplaceInvoiceStatus.Invoiced, (await db.ShipmentPackages.SingleAsync(item => item.Id == seed.Package.Id)).MarketplaceInvoiceStatus);
            Assert.Equal("MARKETPLACE_INVOICE_ALREADY_PRESENT", (await NewBillingService(db).CreateDraftAsync(seed.Tenant.Id, new CreateInvoiceCommand(seed.Order.Id, seed.Package.Id, seed.Provider.Id, null), "g4-elsewhere", CancellationToken.None)).Error?.Code);
        }
        finally
        {
            await DeleteTenantAsync(seed.Tenant.Id);
        }
    }

    // G-4: PTT AVM still flags the lines invoiced (IsInvoice) after the cancelled invoice and does not document what a
    // second link does. The new invoice is issued, but its link is not sent: the seller replaces it in the PTT AVM panel.
    [PostgreSqlFact]
    public async Task G4_PttAvmReopenedOrderGetsItsNewLinkReplacedInTheSellerPanel()
    {
        var seed = NewSeed("g4-pttavm-reopen", "PTTAVM", null);
        seed.Package.MarketplaceInvoiceStatus = MarketplaceInvoiceStatus.Invoiced;
        var cancelled = NewInvoice(seed, InvoiceStatus.Cancelled);
        var link = NewRevocationState(seed, cancelled);
        await SeedAsync(seed, db => { db.Invoices.Add(cancelled); db.MarketplaceDeliveryStates.Add(link); });
        try
        {
            Assert.Equal(1, await SetManualStatusAsync(seed, "PENDING"));
            var replacement = NewInvoice(seed, InvoiceStatus.MarketplacePending);
            replacement.InvoiceNumber = "GIB2026000000002";
            var asset = new FileAsset { Id = Guid.CreateVersion7(), TenantId = seed.Tenant.Id, Classification = "INVOICE_DOCUMENT", RelativePath = "g4/invoice.pdf", MimeType = "application/pdf", SizeBytes = 10, Sha256 = new string('A', 64), Status = "ACTIVE", CreatedAt = fixture.Now };
            await using (var db = fixture.CreateContext())
            {
                db.Invoices.Add(replacement);
                db.FileAssets.Add(asset);
                db.InvoiceDocuments.Add(new InvoiceDocument { Id = Guid.CreateVersion7(), TenantId = seed.Tenant.Id, InvoiceId = replacement.Id, DocumentType = "PDF", FileAssetId = asset.Id, Sha256 = asset.Sha256, CreatedAt = fixture.Now });
                await db.SaveChangesAsync();
            }

            var marketplace = new InvoicedMarketplace();
            await using (var db = fixture.CreateContext())
            {
                var result = await NewProcessor(db, marketplace, configuration: new Dictionary<string, string?> { ["Marketplace:PublicBaseUrl"] = "https://panel.example.test" })
                    .ProcessAsync(seed.Tenant.Id, seed.Marketplace.Id, InvoicingJobTypes.MarketplaceDelivery,
                        JsonSerializer.Serialize(new { invoiceId = replacement.Id, packageId = seed.Package.Id }), "g4-ptt-deliver", CancellationToken.None);
                Assert.Equal(MarketplaceInvoiceReopenPolicy.PttAvmManualReplacementCode, result.ErrorCode);
            }
            Assert.Equal(0, marketplace.Deliveries);
            await using var verification = fixture.CreateContext();
            var stored = await verification.Invoices.SingleAsync(item => item.Id == replacement.Id);
            Assert.Equal((InvoiceStatus.ManualReview, MarketplaceInvoiceReopenPolicy.PttAvmManualReplacementCode), (stored.Status, stored.LastErrorCode));
        }
        finally
        {
            await DeleteTenantAsync(seed.Tenant.Id);
        }
    }

    private MarketplaceDeliveryState NewRevocationState(Seed seed, Invoice invoice) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = seed.Tenant.Id,
        InvoiceId = invoice.Id,
        ConnectionId = seed.Marketplace.Id,
        PackageId = seed.Package.Id,
        AttemptNumber = 2,
        ExternalIdempotencyKey = $"delivery:{invoice.Id:N}:{seed.Package.Id:N}",
        RequestHash = "g4-hash",
        DeliveryType = "LINK",
        Status = MarketplaceInvoiceLinkRevocationPolicy.Required,
        ExternalReference = seed.Package.ExternalPackageId,
        CreatedAt = fixture.Now.AddDays(-1),
        UpdatedAt = fixture.Now.AddDays(-1),
        Version = 1
    };

    // The workspace's hand-set status for one package; returns how many packages it reopened.
    private async Task<int> SetManualStatusAsync(Seed seed, string status)
    {
        await using var db = fixture.CreateContext();
        var http = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection()
                .AddSingleton<ITenantContextAccessor>(new TenantAccessor(new(Guid.NewGuid(), seed.Tenant.Id, "OWNER")))
                .BuildServiceProvider()
        };
        http.Request.Headers["Idempotency-Key"] = $"g4:{Guid.NewGuid():N}";
        var result = await InvoicingEndpoints.UpdateInvoiceWorkspaceManualStatusAsync(new([seed.Package.Id], status), http, db, fixture.TimeProvider);
        var value = (result as IValueHttpResult)?.Value;
        Assert.Equal(StatusCodes.Status200OK, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
        return JsonDocument.Parse(JsonSerializer.Serialize(value)).RootElement.GetProperty("reopenedCount").GetInt32();
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

    private InvoicingJobProcessor NewProcessor(AppDbContext db, IInvoiceMarketplacePort marketplace, IInvoiceProviderPort? provider = null, IDictionary<string, string?>? configuration = null) =>
        new(db, provider!, marketplace, null!, fixture.TimeProvider, new ConfigurationBuilder().AddInMemoryCollection(configuration ?? new Dictionary<string, string?>()).Build(), new EphemeralDataProtectionProvider());

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
        await db.FileAssets.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
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

    // PTT AVM's read once every live line says IsInvoice=true; counts the deliveries it is asked for.
    private sealed class InvoicedMarketplace : IInvoiceMarketplacePort
    {
        public int Deliveries { get; private set; }

        public Task<AdapterResult<InvoiceDeliveryResult>> DeliverAsync(AdapterContext context, InvoiceDeliveryCommand command, CancellationToken cancellationToken)
        {
            Deliveries++;
            throw new InvalidOperationException("A delivery must not be sent.");
        }

        public Task<AdapterResult<InvoiceDeliveryStatus>> QueryDeliveryAsync(AdapterContext context, ExternalInvoiceDeliveryReference reference, CancellationToken cancellationToken) =>
            Task.FromResult(AdapterResult<InvoiceDeliveryStatus>.Success(new(reference.ExternalReference, "INVOICED", true)));
    }

    // The provider refuses every invoice it is sent: the test only needs to see that one reached it.
    private sealed class RefusingProvider : IInvoiceProviderPort
    {
        public const string Code = "EFATURAM_REQUEST_REJECTED";
        public int SubmitCalls { get; private set; }

        public Task<AdapterResult<InvoiceSubmissionResult>> SubmitAsync(AdapterContext context, InvoiceSubmission submission, CancellationToken cancellationToken)
        {
            SubmitCalls++;
            return Task.FromResult(AdapterResult<InvoiceSubmissionResult>.Failure(new(AdapterErrorClass.Validation, Code, "E-Faturam isteği reddetti.", 400, null, null)));
        }
        public Task<AdapterResult<ConnectionIdentity>> TestConnectionAsync(AdapterContext context, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<InvoiceRemoteStatus>> QueryStatusAsync(AdapterContext context, ExternalInvoiceReference reference, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<RemoteInvoiceDocument>> GetDocumentAsync(AdapterContext context, ExternalInvoiceReference reference, string documentKind, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<InvoiceCancellationResult>> CancelAsync(AdapterContext context, InvoiceCancellation command, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class TenantAccessor(TenantContext context) : ITenantContextAccessor
    {
        public TenantContext? Current => context;
    }
}
