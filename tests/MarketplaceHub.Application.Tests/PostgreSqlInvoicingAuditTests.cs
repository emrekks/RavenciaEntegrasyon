using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// Regression tests for the invoicing findings of the 19 Oct 2026 audit that need a real database.
[Collection("postgres-integration")]
public sealed class PostgreSqlInvoicingAuditTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    [PostgreSqlFact]
    public async Task CreateDraft_RefusesEInvoiceRecipientFromFlatTrendyolSnapshotAndExportOrders()
    {
        var seed = NewSeed("audit-draft-gates");
        seed.Order.CustomerSnapshotJson = "{\"commercial\":true}";
        seed.Order.InvoiceAddressSnapshotJson = "{\"company\":\"Örnek A.Ş.\",\"taxNumber\":\"1234567890\",\"eInvoiceAvailable\":true}";
        var exportOrder = NewOrder(seed.Tenant.Id, seed.Marketplace.Id, "audit-export-order");
        exportOrder.Currency = "EUR";
        var exportPackage = NewPackage(seed.Tenant.Id, seed.Marketplace.Id, exportOrder.Id, "audit-export-package");
        await SeedAsync(seed, db => { db.Orders.Add(exportOrder); db.ShipmentPackages.Add(exportPackage); });

        try
        {
            await using var db = fixture.CreateContext();
            var service = NewBillingService(db);

            var eInvoice = await service.CreateDraftAsync(seed.Tenant.Id,
                new CreateInvoiceCommand(seed.Order.Id, seed.Package.Id, seed.Provider.Id, null, true), "audit-einvoice", CancellationToken.None);
            var export = await service.CreateDraftAsync(seed.Tenant.Id,
                new CreateInvoiceCommand(exportOrder.Id, exportPackage.Id, seed.Provider.Id, null, true), "audit-export", CancellationToken.None);

            Assert.Equal(InvoiceIssuePolicy.EInvoiceRecipientCode, eInvoice.Error?.Code);
            Assert.Equal(422, eInvoice.Error?.Status);
            Assert.Equal(InvoiceIssuePolicy.ExportFlowRequiredCode, export.Error?.Code);
            Assert.Equal(422, export.Error?.Status);
            await using var verification = fixture.CreateContext();
            Assert.False(await verification.Invoices.AnyAsync(invoice => invoice.TenantId == seed.Tenant.Id));
        }
        finally
        {
            await DeleteTenantAsync(seed.Tenant.Id);
        }
    }

    [PostgreSqlFact]
    public async Task DueScan_FlagsIssuedSaleInvoicesOfCancelledOrdersAndAcceptedReturnsForAdjustment()
    {
        var seed = NewSeed("audit-adjustment");
        var untouched = NewInvoice(seed, seed.Order.Id, InvoiceStatus.Completed);
        var cancelledOrder = NewOrder(seed.Tenant.Id, seed.Marketplace.Id, "audit-cancelled-order");
        cancelledOrder.DerivedStatus = "CANCELLED";
        var cancelledInvoice = NewInvoice(seed, cancelledOrder.Id, InvoiceStatus.Completed);
        var returnedOrder = NewOrder(seed.Tenant.Id, seed.Marketplace.Id, "audit-returned-order");
        var returnedInvoice = NewInvoice(seed, returnedOrder.Id, InvoiceStatus.Accepted);
        var claim = new ReturnClaim
        {
            Id = Guid.CreateVersion7(),
            TenantId = seed.Tenant.Id,
            ConnectionId = seed.Marketplace.Id,
            OrderId = returnedOrder.Id,
            ExternalClaimId = "audit-claim",
            Status = ReturnClaimStatus.Approved,
            RawStatus = "Accepted",
            LastRemoteModifiedAt = fixture.Now,
            CreatedAt = fixture.Now,
            UpdatedAt = fixture.Now
        };
        await SeedAsync(seed, db =>
        {
            db.Orders.AddRange(cancelledOrder, returnedOrder);
            db.Invoices.AddRange(untouched, cancelledInvoice, returnedInvoice);
            db.ReturnClaims.Add(claim);
        });

        try
        {
            await using (var db = fixture.CreateContext())
            {
                var result = await NewProcessor(db, null).ProcessAsync(seed.Tenant.Id, null, InvoicingJobTypes.InvoiceDueScan, "{}", "audit-adjustment", CancellationToken.None);
                Assert.True(result.Succeeded);
            }

            await using var verification = fixture.CreateContext();
            var statuses = await verification.Invoices.Where(invoice => invoice.TenantId == seed.Tenant.Id).ToDictionaryAsync(invoice => invoice.Id, invoice => invoice.Status);
            Assert.Equal(InvoiceStatus.Completed, statuses[untouched.Id]);
            Assert.Equal(InvoiceStatus.AdjustmentRequired, statuses[cancelledInvoice.Id]);
            Assert.Equal(InvoiceStatus.AdjustmentRequired, statuses[returnedInvoice.Id]);
            var issueKeys = await verification.OperationalIssues.Where(issue => issue.TenantId == seed.Tenant.Id && issue.Code == "INVOICE_ADJUSTMENT_REQUIRED")
                .Select(issue => issue.DedupeKey).ToListAsync();
            Assert.Equal(
                new[] { $"invoice-adjustment:{cancelledInvoice.Id:N}", $"invoice-adjustment:{returnedInvoice.Id:N}" }.Order(),
                issueKeys.Order());
        }
        finally
        {
            await DeleteTenantAsync(seed.Tenant.Id);
        }
    }

    [PostgreSqlFact]
    public async Task DueScan_ReviewsOnlyUnissuedInvoicesPastTheirIssuingDeadline()
    {
        var seed = NewSeed("audit-due");
        var unissued = NewInvoice(seed, seed.Order.Id, InvoiceStatus.Ready);
        unissued.ExternalReference = null;
        unissued.DueAt = fixture.Now.AddDays(-1);
        var issuedOrder = NewOrder(seed.Tenant.Id, seed.Marketplace.Id, "audit-due-issued");
        var issued = NewInvoice(seed, issuedOrder.Id, InvoiceStatus.Completed);
        issued.DueAt = fixture.Now.AddDays(-1);
        await SeedAsync(seed, db => { db.Orders.Add(issuedOrder); db.Invoices.AddRange(unissued, issued); });

        try
        {
            await using (var db = fixture.CreateContext())
                Assert.True((await NewProcessor(db, null).ProcessAsync(seed.Tenant.Id, null, InvoicingJobTypes.InvoiceDueScan, "{}", "audit-due", CancellationToken.None)).Succeeded);

            await using var verification = fixture.CreateContext();
            var keys = await verification.OperationalIssues.Where(issue => issue.TenantId == seed.Tenant.Id && issue.Code == "INVOICE_DUE_REVIEW").Select(issue => issue.DedupeKey).ToListAsync();
            Assert.Equal([$"invoice-due:{unissued.Id:N}"], keys);
        }
        finally
        {
            await DeleteTenantAsync(seed.Tenant.Id);
        }
    }

    [PostgreSqlFact]
    public async Task DueScan_QueuesDeliveryForOrderLevelSaleInvoiceThatWasNeverQueued()
    {
        var seed = NewSeed("audit-never-queued");
        var invoice = NewInvoice(seed, seed.Order.Id, InvoiceStatus.Accepted);
        invoice.PackageId = null;
        invoice.UpdatedAt = fixture.Now.AddMinutes(-10);
        var asset = NewFileAsset(seed.Tenant.Id);
        var document = new InvoiceDocument
        {
            Id = Guid.CreateVersion7(),
            TenantId = seed.Tenant.Id,
            InvoiceId = invoice.Id,
            DocumentType = "PDF",
            FileAssetId = asset.Id,
            Sha256 = asset.Sha256,
            PermanentUrl = "https://documents.example.test/invoice.pdf",
            CreatedAt = fixture.Now
        };
        await SeedAsync(seed, db => { db.Invoices.Add(invoice); db.FileAssets.Add(asset); db.InvoiceDocuments.Add(document); });

        try
        {
            await using (var db = fixture.CreateContext())
            {
                var processor = NewProcessor(db, null, new Dictionary<string, string?>
                {
                    ["FeatureFlags:ExternalWrites"] = "true",
                    ["FeatureFlags:InvoiceMarketplaceDeliveryWrites"] = "true"
                });
                Assert.True((await processor.ProcessAsync(seed.Tenant.Id, null, InvoicingJobTypes.InvoiceDueScan, "{}", "audit-never-queued", CancellationToken.None)).Succeeded);
            }

            await using var verification = fixture.CreateContext();
            var job = Assert.Single(await verification.IntegrationJobs.Where(job => job.TenantId == seed.Tenant.Id && job.JobType == InvoicingJobTypes.MarketplaceDelivery).ToListAsync());
            Assert.Equal(seed.Marketplace.Id, job.ConnectionId);
            Assert.Contains(invoice.Id.ToString(), job.PayloadJson, StringComparison.Ordinal);
        }
        finally
        {
            await DeleteTenantAsync(seed.Tenant.Id);
        }
    }

    // Live 20 Oct 2026: a recovery job was queued beside the retry of an invoice-level delivery job ({"invoiceId"}
    // only), both took the same attempt number and the second stopped for review on the unique key.
    [PostgreSqlFact]
    public async Task DueScan_DoesNotQueueARecoveryBesideAnInvoiceLevelDeliveryJobOfTheSameInvoice()
    {
        var seed = NewSeed("audit-recovery-beside-retry");
        var invoice = NewInvoice(seed, seed.Order.Id, InvoiceStatus.MarketplacePending);
        var state = NewConfirmedDeliveryState(seed, invoice);
        state.Status = "UNKNOWN";
        state.ExternalReference = null;
        state.CompletedAt = null;
        state.UpdatedAt = fixture.Now.AddMinutes(-3);
        var asset = NewFileAsset(seed.Tenant.Id);
        var document = new InvoiceDocument
        {
            Id = Guid.CreateVersion7(),
            TenantId = seed.Tenant.Id,
            InvoiceId = invoice.Id,
            DocumentType = "PDF",
            FileAssetId = asset.Id,
            Sha256 = asset.Sha256,
            PermanentUrl = "https://documents.example.test/invoice.pdf",
            CreatedAt = fixture.Now
        };
        var retrying = NewDeliveryJob(seed, $"{InvoicingJobTypes.MarketplaceDelivery}:{invoice.Id}:after-document", $$"""{"invoiceId":"{{invoice.Id}}"}""", JobStatus.RetryScheduled);
        await SeedAsync(seed, db => { db.Invoices.Add(invoice); db.MarketplaceDeliveryStates.Add(state); db.FileAssets.Add(asset); db.InvoiceDocuments.Add(document); db.IntegrationJobs.Add(retrying); });

        try
        {
            await using (var db = fixture.CreateContext())
            {
                var processor = NewProcessor(db, null, new Dictionary<string, string?>
                {
                    ["FeatureFlags:ExternalWrites"] = "true",
                    ["FeatureFlags:InvoiceMarketplaceDeliveryWrites"] = "true"
                });
                Assert.True((await processor.ProcessAsync(seed.Tenant.Id, null, InvoicingJobTypes.InvoiceDueScan, "{}", "audit-recovery-beside-retry", CancellationToken.None)).Succeeded);
            }

            await using var verification = fixture.CreateContext();
            var job = Assert.Single(await verification.IntegrationJobs.Where(job => job.TenantId == seed.Tenant.Id && job.JobType == InvoicingJobTypes.MarketplaceDelivery).ToListAsync());
            Assert.Equal(retrying.Id, job.Id);
        }
        finally
        {
            await DeleteTenantAsync(seed.Tenant.Id);
        }
    }

    [PostgreSqlFact]
    public async Task DueScan_ClosesAStoppedDeliveryJobWhoseDeliveryAnotherJobConfirmed()
    {
        var seed = NewSeed("audit-settled-elsewhere");
        var invoice = NewInvoice(seed, seed.Order.Id, InvoiceStatus.Completed);
        var state = NewConfirmedDeliveryState(seed, invoice);
        var otherOrder = NewOrder(seed.Tenant.Id, seed.Marketplace.Id, "audit-settled-elsewhere-other");
        var otherInvoice = NewInvoice(seed, otherOrder.Id, InvoiceStatus.MarketplaceFailed);
        var stopped = NewDeliveryJob(seed, $"{InvoicingJobTypes.MarketplaceDelivery}:{invoice.Id}:{seed.Package.Id}:recovery:3:1", $$"""{"invoiceId":"{{invoice.Id}}","packageId":"{{seed.Package.Id}}"}""", JobStatus.ManualReview);
        var unsettled = NewDeliveryJob(seed, $"{InvoicingJobTypes.MarketplaceDelivery}:{otherInvoice.Id}:after-document", $$"""{"invoiceId":"{{otherInvoice.Id}}"}""", JobStatus.ManualReview);
        // A backlog of older stopped jobs that stay unsettled must not keep the newer settled one open.
        var backlog = Enumerable.Range(0, 250).Select(index =>
        {
            var job = NewDeliveryJob(seed, $"{InvoicingJobTypes.MarketplaceDelivery}:{otherInvoice.Id}:backlog-{index}", $$"""{"invoiceId":"{{otherInvoice.Id}}"}""", JobStatus.Dead);
            job.CreatedAt = fixture.Now.AddDays(-2).AddMinutes(index);
            return job;
        }).ToList();
        await SeedAsync(seed, db => { db.Orders.Add(otherOrder); db.Invoices.AddRange(invoice, otherInvoice); db.MarketplaceDeliveryStates.Add(state); db.IntegrationJobs.AddRange(stopped, unsettled); db.IntegrationJobs.AddRange(backlog); });

        try
        {
            await using (var db = fixture.CreateContext())
                Assert.True((await NewProcessor(db, null).ProcessAsync(seed.Tenant.Id, null, InvoicingJobTypes.InvoiceDueScan, "{}", "audit-settled-elsewhere", CancellationToken.None)).Succeeded);

            await using var verification = fixture.CreateContext();
            var closed = await verification.IntegrationJobs.SingleAsync(job => job.Id == stopped.Id);
            Assert.Equal(JobStatus.Cancelled, closed.Status);
            Assert.Equal(InvoicingJobProcessor.DeliverySettledElsewhereCode, closed.LastErrorCode);
            Assert.Equal(JobStatus.ManualReview, (await verification.IntegrationJobs.SingleAsync(job => job.Id == unsettled.Id)).Status);
            // The job screens learn that the job closed.
            Assert.True(await verification.IntegrationOutboxEvents.AnyAsync(outbox => outbox.TenantId == seed.Tenant.Id
                && outbox.ResourceType == JobLeaseService.JobEventResource && outbox.AggregateId == stopped.Id && outbox.AggregateVersion == closed.Version));
        }
        finally
        {
            await DeleteTenantAsync(seed.Tenant.Id);
        }
    }

    private IntegrationJob NewDeliveryJob(Seed seed, string key, string payload, JobStatus status) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = seed.Tenant.Id,
        ConnectionId = seed.Marketplace.Id,
        JobType = InvoicingJobTypes.MarketplaceDelivery,
        PayloadJson = payload,
        PayloadHash = "audit-hash",
        JobDedupKey = key,
        EffectIdempotencyKey = key,
        Status = status,
        AvailableAt = fixture.Now,
        CorrelationId = key,
        CreatedAt = fixture.Now.AddMinutes(-5),
        Version = 1
    };

    [PostgreSqlFact]
    public async Task ReplaceCancelledInvoice_RequiresTheProviderToReportTheCancellationAndMarksTheLinkForRemoval()
    {
        var seed = NewSeed("audit-replace");
        var invoice = NewInvoice(seed, seed.Order.Id, InvoiceStatus.Completed);
        var state = NewConfirmedDeliveryState(seed, invoice);
        await SeedAsync(seed, db => { db.Invoices.Add(invoice); db.MarketplaceDeliveryStates.Add(state); });

        try
        {
            await using (var db = fixture.CreateContext())
            {
                var refused = await NewBillingService(db, new StatusProvider("ACCEPTED")).ReplaceCancelledInvoiceAsync(seed.Tenant.Id, seed.Order.Id, "audit-replace", CancellationToken.None);
                Assert.Equal("INVOICE_NOT_CANCELLED_AT_PROVIDER", refused.Error?.Code);
                Assert.Equal(409, refused.Error?.Status);
            }
            await using (var db = fixture.CreateContext())
                Assert.Equal(InvoiceStatus.Completed, (await db.Invoices.SingleAsync(item => item.Id == invoice.Id)).Status);

            await using (var db = fixture.CreateContext())
                Assert.True((await NewBillingService(db, new StatusProvider("CANCELLED")).ReplaceCancelledInvoiceAsync(seed.Tenant.Id, seed.Order.Id, "audit-replace", CancellationToken.None)).Succeeded);

            await using var verification = fixture.CreateContext();
            var replaced = await verification.Invoices.SingleAsync(item => item.Id == invoice.Id);
            Assert.Equal("REPLACED", replaced.SequencePurpose);
            Assert.Equal(InvoiceStatus.Cancelled, replaced.Status);
            Assert.Equal("REVOCATION_REQUIRED", (await verification.MarketplaceDeliveryStates.SingleAsync(item => item.Id == state.Id)).Status);
            Assert.True(await verification.OperationalIssues.AnyAsync(issue => issue.TenantId == seed.Tenant.Id && issue.DedupeKey == $"invoice-link-revocation:{invoice.Id:N}"));
        }
        finally
        {
            await DeleteTenantAsync(seed.Tenant.Id);
        }
    }

    [PostgreSqlFact]
    public async Task Reconcile_InvoiceCancelledAtProvider_MarksTheDeliveredLinkForRemoval()
    {
        var seed = NewSeed("audit-provider-cancel");
        var invoice = NewInvoice(seed, seed.Order.Id, InvoiceStatus.CancellationPending);
        var state = NewConfirmedDeliveryState(seed, invoice);
        await SeedAsync(seed, db => { db.Invoices.Add(invoice); db.MarketplaceDeliveryStates.Add(state); });

        try
        {
            await using (var db = fixture.CreateContext())
            {
                var result = await NewProcessor(db, new StatusProvider("CANCELLED")).ProcessAsync(seed.Tenant.Id, seed.Provider.Id, InvoicingJobTypes.InvoiceReconcile,
                    $"{{\"invoiceId\":\"{invoice.Id}\"}}", "audit-provider-cancel", CancellationToken.None);
                Assert.True(result.Succeeded);
            }

            await using var verification = fixture.CreateContext();
            Assert.Equal(InvoiceStatus.Cancelled, (await verification.Invoices.SingleAsync(item => item.Id == invoice.Id)).Status);
            Assert.Equal("REVOCATION_REQUIRED", (await verification.MarketplaceDeliveryStates.SingleAsync(item => item.Id == state.Id)).Status);
            Assert.True(await verification.MarketplaceDeliveries.AnyAsync(entry => entry.InvoiceId == invoice.Id && entry.Status == "REVOCATION_REQUIRED"));
            Assert.True(await verification.OperationalIssues.AnyAsync(issue => issue.TenantId == seed.Tenant.Id && issue.Code == "INVOICE_MARKETPLACE_LINK_REVOCATION_REQUIRED"));
        }
        finally
        {
            await DeleteTenantAsync(seed.Tenant.Id);
        }
    }

    [PostgreSqlFact]
    public async Task ConfirmWorkspace_ContinuesWithTheNextOrderAfterAnItemFailsToSave()
    {
        var seed = NewSeed("audit-confirm-batch");
        seed.Marketplace.Environment = "STAGE";
        seed.Provider.Environment = "STAGE";
        var secondOrder = NewOrder(seed.Tenant.Id, seed.Marketplace.Id, "audit-confirm-second");
        var secondPackage = NewPackage(seed.Tenant.Id, seed.Marketplace.Id, secondOrder.Id, "audit-confirm-second-package");
        var credential = new PlatformCredential
        {
            Id = Guid.CreateVersion7(),
            TenantId = seed.Tenant.Id,
            ConnectionId = seed.Provider.Id,
            CredentialType = "EMAIL_PASSWORD",
            ProtectedPayload = "test-only",
            MaskedHint = "f***@example.test",
            CreatedAt = fixture.Now
        };
        await SeedAsync(seed, db =>
        {
            db.Orders.Add(secondOrder);
            db.ShipmentPackages.Add(secondPackage);
            db.OrderLines.AddRange(NewLine(seed.Tenant.Id, seed.Order.Id), NewLine(seed.Tenant.Id, secondOrder.Id));
            db.PlatformCredentials.Add(credential);
        });

        try
        {
            var configuration = new Dictionary<string, string?> { ["FeatureFlags:InvoiceMarketplaceDeliveryWrites"] = "true" };
            IReadOnlyList<InvoiceWorkspacePreviewItem> preview;
            await using (var db = fixture.CreateContext())
            {
                var previewed = await NewBillingService(db, null, configuration).PreviewWorkspaceInvoicesAsync(seed.Tenant.Id,
                    new InvoiceWorkspacePreviewRequest([new(seed.Order.Id, seed.Package.Id, seed.Provider.Id), new(secondOrder.Id, secondPackage.Id, seed.Provider.Id)], true),
                    CancellationToken.None);
                Assert.True(previewed.Succeeded, previewed.Error?.Message);
                preview = previewed.Value!;
                Assert.All(preview, item => Assert.True(item.CanConfirm, item.BlockedReason));
            }

            var failFirstInvoice = new FailFirstInvoiceSave();
            var connectionString = Environment.GetEnvironmentVariable("MARKETPLACEHUB_TEST_CONNECTION")!;
            await using (var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connectionString).AddInterceptors(failFirstInvoice).Options))
            {
                var confirmed = await NewBillingService(db, null, configuration).ConfirmWorkspaceInvoicesAsync(seed.Tenant.Id,
                    new InvoiceWorkspacePreviewConfirmRequest(preview.Select(item => new InvoiceWorkspacePreviewConfirmation(item.OrderId, item.PackageId, item.ProviderConnectionId, item.PreviewDigest)).ToArray(), true),
                    "audit-confirm", "audit-confirm", CancellationToken.None);
                Assert.True(confirmed.Succeeded);
                Assert.Equal(["CONFLICT", "QUEUED"], confirmed.Value!.Items.Select(item => item.Status));
            }

            await using var verification = fixture.CreateContext();
            Assert.False(await verification.Invoices.AnyAsync(invoice => invoice.TenantId == seed.Tenant.Id && invoice.OrderId == seed.Order.Id));
            Assert.Equal(1, await verification.Invoices.CountAsync(invoice => invoice.TenantId == seed.Tenant.Id && invoice.OrderId == secondOrder.Id));
        }
        finally
        {
            await DeleteTenantAsync(seed.Tenant.Id);
        }
    }

    [PostgreSqlFact]
    public async Task ALineWhoseVatRateTrendyolDidNotReportBlocksThePreviewAndTheDraft()
    {
        var seed = NewSeed("audit-vat-unknown");
        var line = NewLine(seed.Tenant.Id, seed.Order.Id);
        line.VatRate = 0m;
        line.SourceSnapshotJson = "{\"lineId\":\"1\",\"stockCode\":\"AUDIT-SKU\",\"quantity\":1,\"lineItemPrice\":120}";
        await SeedAsync(seed, db => db.OrderLines.Add(line));

        try
        {
            await using var db = fixture.CreateContext();
            var service = NewBillingService(db);
            var preview = await service.PreviewWorkspaceInvoicesAsync(seed.Tenant.Id,
                new InvoiceWorkspacePreviewRequest([new(seed.Order.Id, seed.Package.Id, seed.Provider.Id)], true), CancellationToken.None);
            var draft = await service.CreateDraftAsync(seed.Tenant.Id,
                new CreateInvoiceCommand(seed.Order.Id, seed.Package.Id, seed.Provider.Id, null, true), "audit-vat-unknown", CancellationToken.None);

            var item = Assert.Single(preview.Value ?? []);
            Assert.False(item.CanConfirm);
            Assert.Equal(InvoiceIssuePolicy.LineVatRateUnknown.Message, item.BlockedReason);
            Assert.Equal(InvoiceIssuePolicy.LineVatRateUnknownCode, draft.Error?.Code);
            await using var verification = fixture.CreateContext();
            Assert.False(await verification.Invoices.AnyAsync(invoice => invoice.TenantId == seed.Tenant.Id));
        }
        finally
        {
            await DeleteTenantAsync(seed.Tenant.Id);
        }
    }

    // Audit 29 Oct 2026 (G-1): a draft the provider refused before creating anything was queued again with the lines and
    // total it froze on its first day, while the preview showed the order as it is now (a line cancelled since).
    [PostgreSqlFact]
    public async Task ConfirmWorkspace_RetryOfARefusedDraftIsRedraftedFromTheOrderAfterALineWasCancelled()
    {
        var protection = new EphemeralDataProtectionProvider();
        var seed = NewRetrySeed("audit-retry-cancelled-line", out var kept, out var cancelled, out var invoice);
        kept.UnitPrice = 100m;
        cancelled.UnitPrice = 50m;
        cancelled.CancelledQuantity = 1;
        seed.Order.GrossAmount = seed.Order.NetAmount = 100m;
        seed.Package.GrossAmount = seed.Package.NetAmount = 100m;
        invoice.TaxExclusiveTotal = 125m;
        invoice.TaxTotal = 25m;
        invoice.PayableTotal = 150m;
        invoice.Note = InvoiceAmounts.TurkishInvoiceNote(150m);
        var waitingJob = NewSubmitJob(seed, invoice, JobStatus.RetryScheduled);
        await SeedRetryAsync(seed, kept, cancelled, invoice, protection, frozenLines: [(kept, 100m), (cancelled, 50m)],
            attemptOutcome: "FAILED", extra: db => db.IntegrationJobs.Add(waitingJob));

        try
        {
            var item = await PreviewAndConfirmRetryAsync(seed, invoice, protection, expectedPayable: 100m);
            Assert.Equal(("QUEUED", "SUBMIT_RETRY"), (item.Status, item.Action));
            Assert.NotEqual(invoice.Id, item.InvoiceId);

            await using var verification = fixture.CreateContext();
            // The refused draft keeps its frozen lines for audit but leaves the order's sale slot and is never sent.
            var retired = await verification.Invoices.SingleAsync(row => row.Id == invoice.Id);
            Assert.Equal(("REPLACED", InvoiceStatus.CancelledLocal, false), (retired.SequencePurpose, retired.Status, retired.OrderFiscalGuarded));
            Assert.Equal(InvoicingBillingService.RedraftedFromOrderCode, retired.LastErrorCode);
            Assert.Equal(150m, await verification.InvoiceLines.Where(row => row.InvoiceId == invoice.Id).SumAsync(row => row.LineTotal));
            Assert.Equal(JobStatus.Cancelled, (await verification.IntegrationJobs.SingleAsync(row => row.Id == waitingJob.Id)).Status);
            Assert.True(await verification.AuditLogs.AnyAsync(log => log.TenantId == seed.Tenant.Id && log.Action == "INVOICE_UNSENT_DRAFT_REDRAFTED"
                && log.TargetId == invoice.Id.ToString("D")));

            // The one sale invoice of the order carries what the preview showed, and it is the one queued.
            var sale = await verification.Invoices.SingleAsync(row => row.TenantId == seed.Tenant.Id && row.OrderId == seed.Order.Id && row.SequencePurpose == "SALE");
            Assert.Equal(item.InvoiceId, sale.Id);
            Assert.Equal((InvoiceStatus.Submitting, 100m), (sale.Status, sale.PayableTotal));
            var line = Assert.Single(await verification.InvoiceLines.Where(row => row.InvoiceId == sale.Id).ToListAsync());
            Assert.Equal((kept.Id, 1m, 100m), (line.OrderLineId!.Value, line.Quantity, line.LineTotal));
            Assert.True(await verification.InvoicePartySnapshots.AnyAsync(row => row.InvoiceId == sale.Id && row.Role == "RECEIVER"));
            var job = await verification.IntegrationJobs.SingleAsync(row => row.Id == item.JobId);
            Assert.Equal((InvoicingJobTypes.InvoiceSubmit, JobStatus.Pending), (job.JobType, job.Status));
            Assert.Contains(sale.Id.ToString(), job.PayloadJson, StringComparison.Ordinal);
        }
        finally
        {
            await DeleteTenantAsync(seed.Tenant.Id);
        }
    }

    // A refusal for the recipient's data (a missing district, a wrong tax number) is fixed on the marketplace and synced;
    // the retry must carry the corrected receiver, not the one frozen with the refused draft.
    [PostgreSqlFact]
    public async Task ConfirmWorkspace_RetryOfARefusedDraftTakesTheReceiverCorrectedSince()
    {
        var protection = new EphemeralDataProtectionProvider();
        var seed = NewRetrySeed("audit-retry-receiver", out var first, out var second, out var invoice);
        invoice.Status = InvoiceStatus.Rejected;
        invoice.LastErrorCode = "EFATURAM_FISCAL_PAYLOAD_INVALID";
        await SeedRetryAsync(seed, first, second, invoice, protection, frozenLines: [(first, 60m), (second, 60m)], attemptOutcome: "FAILED");
        await using (var db = fixture.CreateContext())
            await db.Orders.Where(order => order.Id == seed.Order.Id)
                .ExecuteUpdateAsync(update => update.SetProperty(order => order.InvoiceAddressSnapshotJson, "{\"city\":\"Istanbul\",\"district\":\"Kadikoy\"}"));

        try
        {
            var item = await PreviewAndConfirmRetryAsync(seed, invoice, protection, expectedPayable: 120m);
            Assert.Equal(("QUEUED", "SUBMIT_RETRY"), (item.Status, item.Action));
            Assert.NotEqual(invoice.Id, item.InvoiceId);

            await using var verification = fixture.CreateContext();
            Assert.Equal(InvoiceStatus.CancelledLocal, (await verification.Invoices.SingleAsync(row => row.Id == invoice.Id)).Status);
            var sale = await verification.Invoices.SingleAsync(row => row.TenantId == seed.Tenant.Id && row.SequencePurpose == "SALE");
            Assert.Equal((item.InvoiceId, InvoiceStatus.Submitting, 120m), ((Guid?)sale.Id, sale.Status, sale.PayableTotal));
            var receiver = await verification.InvoicePartySnapshots.SingleAsync(row => row.InvoiceId == sale.Id && row.Role == "RECEIVER");
            Assert.Contains("Kadikoy", protection.CreateProtector("MarketplaceHub.InvoicePartySnapshot.v1").Unprotect(receiver.ProtectedContent), StringComparison.Ordinal);
        }
        finally
        {
            await DeleteTenantAsync(seed.Tenant.Id);
        }
    }

    // The operator's word after an unknown result is not proof that the provider holds nothing: such a draft is not
    // redrafted under a new local reference, and its old amount is not sent either.
    [PostgreSqlFact]
    public async Task ConfirmWorkspace_RetryAfterAnUnknownResultStopsWhenTheAmountsNoLongerMatchThePreview()
    {
        var protection = new EphemeralDataProtectionProvider();
        var seed = NewRetrySeed("audit-retry-uncertain", out var kept, out var cancelled, out var invoice);
        cancelled.CancelledQuantity = 1;
        seed.Order.GrossAmount = seed.Order.NetAmount = 60m;
        seed.Package.GrossAmount = seed.Package.NetAmount = 60m;
        invoice.Status = InvoiceStatus.ManualReview;
        invoice.LastErrorCode = InvoicingBillingService.OperatorConfirmedNoRemoteInvoiceCode;
        await SeedRetryAsync(seed, kept, cancelled, invoice, protection, frozenLines: [(kept, 60m), (cancelled, 60m)], attemptOutcome: "UNKNOWN");

        try
        {
            var item = await PreviewAndConfirmRetryAsync(seed, invoice, protection, expectedPayable: 60m);
            Assert.Equal(("BLOCKED", "REVIEW"), (item.Status, item.Action));

            await using var verification = fixture.CreateContext();
            var untouched = await verification.Invoices.SingleAsync(row => row.TenantId == seed.Tenant.Id);
            Assert.Equal((invoice.Id, InvoiceStatus.ManualReview, invoice.Version, 120m), (untouched.Id, untouched.Status, untouched.Version, untouched.PayableTotal));
            Assert.False(await verification.IntegrationJobs.AnyAsync(row => row.TenantId == seed.Tenant.Id));
        }
        finally
        {
            await DeleteTenantAsync(seed.Tenant.Id);
        }
    }

    [PostgreSqlFact]
    public async Task ConfirmWorkspace_RetryOfARefusedDraftWhoseOrderDidNotChangeIsQueuedAsItIs()
    {
        var protection = new EphemeralDataProtectionProvider();
        var seed = NewRetrySeed("audit-retry-unchanged", out var first, out var second, out var invoice);
        await SeedRetryAsync(seed, first, second, invoice, protection, frozenLines: [(first, 60m), (second, 60m)], attemptOutcome: "FAILED");

        try
        {
            var item = await PreviewAndConfirmRetryAsync(seed, invoice, protection, expectedPayable: 120m);
            Assert.Equal(("QUEUED", "SUBMIT_RETRY", invoice.Id), (item.Status, item.Action, item.InvoiceId));

            await using var verification = fixture.CreateContext();
            var queued = await verification.Invoices.SingleAsync(row => row.TenantId == seed.Tenant.Id);
            Assert.Equal((invoice.Id, "SALE", InvoiceStatus.Submitting, 120m), (queued.Id, queued.SequencePurpose, queued.Status, queued.PayableTotal));
            Assert.Null(queued.LastErrorCode);
            Assert.Equal([60m, 60m], await verification.InvoiceLines.Where(row => row.InvoiceId == invoice.Id).OrderBy(row => row.LineSequence).Select(row => row.LineTotal).ToListAsync());
            var job = Assert.Single(await verification.IntegrationJobs.Where(row => row.TenantId == seed.Tenant.Id).ToListAsync());
            Assert.Equal(item.JobId, job.Id);
        }
        finally
        {
            await DeleteTenantAsync(seed.Tenant.Id);
        }
    }

    [PostgreSqlFact]
    public async Task ConfirmWorkspace_RetryNeverResendsADraftThatCarriesAProviderNumber()
    {
        var protection = new EphemeralDataProtectionProvider();
        var seed = NewRetrySeed("audit-retry-numbered", out var first, out var second, out var invoice);
        invoice.InvoiceNumber = "GIB2026000000009";
        await SeedRetryAsync(seed, first, second, invoice, protection, frozenLines: [(first, 60m), (second, 60m)], attemptOutcome: "FAILED");

        try
        {
            var item = await PreviewAndConfirmRetryAsync(seed, invoice, protection, expectedPayable: 120m);
            Assert.Equal(("BLOCKED", "REVIEW"), (item.Status, item.Action));

            await using var verification = fixture.CreateContext();
            var untouched = await verification.Invoices.SingleAsync(row => row.TenantId == seed.Tenant.Id);
            Assert.Equal((invoice.Id, InvoiceStatus.Submitting, "EFATURAM_RATE_LIMITED", invoice.Version), (untouched.Id, untouched.Status, untouched.LastErrorCode, untouched.Version));
            Assert.False(await verification.IntegrationJobs.AnyAsync(row => row.TenantId == seed.Tenant.Id));
        }
        finally
        {
            await DeleteTenantAsync(seed.Tenant.Id);
        }
    }

    // A Stage order of two lines at 60 TL (120 TL) whose order-level draft the provider refused before creating
    // anything (rate limit): Submitting with a pre-provider code and no provider reference.
    private Seed NewRetrySeed(string code, out OrderLine first, out OrderLine second, out Invoice invoice)
    {
        var seed = NewSeed(code);
        seed.Marketplace.Environment = "STAGE";
        seed.Provider.Environment = "STAGE";
        first = NewLine(seed.Tenant.Id, seed.Order.Id);
        first.ExternalLineId = $"{code}-line-1";
        first.UnitPrice = 60m;
        second = NewLine(seed.Tenant.Id, seed.Order.Id);
        second.ExternalLineId = $"{code}-line-2";
        second.UnitPrice = 60m;
        invoice = NewInvoice(seed, seed.Order.Id, InvoiceStatus.Submitting);
        invoice.LastErrorCode = "EFATURAM_RATE_LIMITED";
        invoice.ExternalReference = null;
        invoice.InvoiceNumber = null;
        invoice.IdempotencyKey = $"invoice-workspace-order:{seed.Order.Id:N}";
        invoice.Version = 3;
        return seed;
    }

    // The draft as CreateDraftAsync froze it: lines, one submission attempt and the receiver read from the order.
    private async Task SeedRetryAsync(Seed seed, OrderLine first, OrderLine second, Invoice invoice, IDataProtectionProvider protection,
        (OrderLine Line, decimal Total)[] frozenLines, string attemptOutcome, Action<AppDbContext>? extra = null)
    {
        var credential = new PlatformCredential
        {
            Id = Guid.CreateVersion7(),
            TenantId = seed.Tenant.Id,
            ConnectionId = seed.Provider.Id,
            CredentialType = "EMAIL_PASSWORD",
            ProtectedPayload = "test-only",
            MaskedHint = "f***@example.test",
            CreatedAt = fixture.Now
        };
        var lines = frozenLines.Select((frozen, index) => new InvoiceLine
        {
            Id = Guid.CreateVersion7(),
            TenantId = seed.Tenant.Id,
            InvoiceId = invoice.Id,
            OrderLineId = frozen.Line.Id,
            LineSequence = index + 1,
            DescriptionSnapshot = frozen.Line.TitleSnapshot,
            SkuSnapshot = frozen.Line.Sku,
            UnitSnapshot = "ADET",
            Quantity = 1,
            UnitPrice = frozen.Total / 1.2m,
            VatRate = 20m,
            VatAmount = frozen.Total - frozen.Total / 1.2m,
            LineTotal = frozen.Total
        }).ToArray();
        var receiverJson = System.Text.Json.JsonSerializer.Serialize(new
        {
            seed.Order.OrderNumber,
            seed.Order.CustomerSnapshotJson,
            seed.Order.InvoiceAddressSnapshotJson,
            seed.Order.ShipmentAddressSnapshotJson
        });
        var receiver = new InvoicePartySnapshot
        {
            Id = Guid.CreateVersion7(),
            TenantId = seed.Tenant.Id,
            InvoiceId = invoice.Id,
            Role = "RECEIVER",
            ProtectedContent = protection.CreateProtector("MarketplaceHub.InvoicePartySnapshot.v1").Protect(receiverJson),
            ContentHash = "frozen-receiver",
            CreatedAt = fixture.Now.AddDays(-1)
        };
        var attempt = new InvoiceSubmissionAttempt
        {
            Id = Guid.CreateVersion7(),
            TenantId = seed.Tenant.Id,
            InvoiceId = invoice.Id,
            AttemptNumber = 1,
            RequestHash = "frozen-request",
            Outcome = attemptOutcome,
            ErrorCode = attemptOutcome == "FAILED" ? "EFATURAM_RATE_LIMITED" : "INVOICE_SUBMISSION_OUTCOME_UNKNOWN",
            StartedAt = fixture.Now.AddDays(-1),
            CompletedAt = fixture.Now.AddDays(-1)
        };
        await SeedAsync(seed, db =>
        {
            db.OrderLines.AddRange(first, second);
            db.PlatformCredentials.Add(credential);
            db.Invoices.Add(invoice);
            db.InvoiceLines.AddRange(lines);
            db.InvoicePartySnapshots.Add(receiver);
            db.InvoiceSubmissionAttempts.Add(attempt);
            extra?.Invoke(db);
        });
    }

    private IntegrationJob NewSubmitJob(Seed seed, Invoice invoice, JobStatus status) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = seed.Tenant.Id,
        ConnectionId = seed.Provider.Id,
        JobType = InvoicingJobTypes.InvoiceSubmit,
        PayloadJson = $$"""{"invoiceId":"{{invoice.Id}}"}""",
        PayloadHash = "audit-hash",
        JobDedupKey = $"{InvoicingJobTypes.InvoiceSubmit}:{invoice.Id}:frozen",
        EffectIdempotencyKey = $"{InvoicingJobTypes.InvoiceSubmit}:{invoice.Id}:frozen",
        Status = status,
        AvailableAt = fixture.Now.AddMinutes(5),
        CorrelationId = "audit-retry-frozen",
        CreatedAt = fixture.Now.AddDays(-1),
        Version = 1
    };

    private async Task<InvoiceWorkspaceConfirmItemResult> PreviewAndConfirmRetryAsync(Seed seed, Invoice invoice, IDataProtectionProvider protection, decimal expectedPayable)
    {
        var configuration = new Dictionary<string, string?> { ["FeatureFlags:InvoiceMarketplaceDeliveryWrites"] = "true" };
        InvoiceWorkspacePreviewItem preview;
        await using (var db = fixture.CreateContext())
        {
            var previewed = await NewBillingService(db, null, configuration, protection).PreviewWorkspaceInvoicesAsync(seed.Tenant.Id,
                new InvoiceWorkspacePreviewRequest([new(seed.Order.Id, seed.Package.Id, seed.Provider.Id)], true), CancellationToken.None);
            Assert.True(previewed.Succeeded, previewed.Error?.Message);
            preview = Assert.Single(previewed.Value!);
            Assert.Equal(invoice.Id, preview.ExistingInvoiceId);
            Assert.Equal("SUBMIT_RETRY", preview.NextAction);
            Assert.True(preview.CanConfirm, preview.BlockedReason);
            Assert.Equal(expectedPayable, preview.PayableTotal);
        }

        await using (var db = fixture.CreateContext())
        {
            var confirmed = await NewBillingService(db, null, configuration, protection).ConfirmWorkspaceInvoicesAsync(seed.Tenant.Id,
                new InvoiceWorkspacePreviewConfirmRequest([new(preview.OrderId, preview.PackageId, preview.ProviderConnectionId, preview.PreviewDigest)], true),
                "audit-retry", "audit-retry", CancellationToken.None);
            Assert.True(confirmed.Succeeded);
            return Assert.Single(confirmed.Value!.Items);
        }
    }

    private InvoicingBillingService NewBillingService(AppDbContext db, IInvoiceProviderPort? provider = null, IDictionary<string, string?>? configuration = null, IDataProtectionProvider? protection = null)
    {
        var dataProtection = protection ?? new EphemeralDataProtectionProvider();
        return new InvoicingBillingService(db, new CursorCodec(dataProtection, fixture.TimeProvider), dataProtection, null!,
            new ConfigurationBuilder().AddInMemoryCollection(configuration ?? new Dictionary<string, string?>()).Build(), fixture.TimeProvider, provider!);
    }

    private InvoicingJobProcessor NewProcessor(AppDbContext db, IInvoiceProviderPort? provider, IDictionary<string, string?>? configuration = null) =>
        new(db, provider!, null!, null!, fixture.TimeProvider,
            new ConfigurationBuilder().AddInMemoryCollection(configuration ?? new Dictionary<string, string?>()).Build(), new EphemeralDataProtectionProvider());

    private sealed record Seed(Tenant Tenant, PlatformConnection Marketplace, PlatformConnection Provider, Order Order, ShipmentPackage Package, LegalEntityProfile Profile, InvoicePolicy Policy);

    private Seed NewSeed(string code)
    {
        var tenant = new Tenant
        {
            Id = Guid.CreateVersion7(),
            Code = $"{code}-{Guid.NewGuid():N}",
            DisplayName = code,
            Status = RecordStatus.Active,
            Timezone = "Europe/Istanbul",
            CreatedAt = fixture.Now,
            UpdatedAt = fixture.Now,
            Version = 1
        };
        var marketplace = NewConnection(tenant.Id, "TRENDYOL", "seller-audit");
        var provider = NewConnection(tenant.Id, "TRENDYOL_EFATURAM", "efaturam-audit");
        var order = NewOrder(tenant.Id, marketplace.Id, $"{code}-order");
        var profile = new LegalEntityProfile
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenant.Id,
            Title = "audit-profile",
            ProtectedTaxId = "protected",
            MaskedTaxId = "E-Faturam",
            AddressSnapshotJson = "{}",
            ContactSnapshotJson = "{}",
            Status = "ACTIVE",
            CreatedAt = fixture.Now,
            UpdatedAt = fixture.Now
        };
        var policy = new InvoicePolicy
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenant.Id,
            ProviderConnectionId = provider.Id,
            TriggerState = "MANUAL_CONFIRMED",
            PackageScope = "SHIPMENT_PACKAGE",
            DueRule = "IMMEDIATE",
            RoundingRule = "LINE_HALF_AWAY_FROM_ZERO",
            AdjustmentRule = "REJECT_OVER_ONE_KURUS",
            CreatedAt = fixture.Now,
            UpdatedAt = fixture.Now
        };
        return new(tenant, marketplace, provider, order, NewPackage(tenant.Id, marketplace.Id, order.Id, $"{code}-package"), profile, policy);
    }

    private static PlatformConnection NewConnection(Guid tenantId, string platformCode, string storeId) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = tenantId,
        PublicId = Guid.CreateVersion7(),
        PlatformCode = platformCode,
        Environment = "PRODUCTION",
        DisplayName = $"{platformCode} denetim testi",
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
        CustomerSnapshotJson = "{\"customerFirstName\":\"Test\",\"customerLastName\":\"Müşteri\"}",
        ShipmentAddressSnapshotJson = "{}",
        InvoiceAddressSnapshotJson = "{}",
        DerivedStatus = "DELIVERED",
        CreatedAt = fixture.Now,
        UpdatedAt = fixture.Now,
        Version = 1
    };

    private ShipmentPackage NewPackage(Guid tenantId, Guid connectionId, Guid orderId, string externalId) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = tenantId,
        ConnectionId = connectionId,
        OrderId = orderId,
        ExternalPackageId = externalId,
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

    private static OrderLine NewLine(Guid tenantId, Guid orderId) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = tenantId,
        OrderId = orderId,
        ExternalLineId = $"line-{orderId:N}",
        Sku = "AUDIT-SKU",
        TitleSnapshot = "Denetim ürünü",
        OrderedQuantity = 1,
        UnitPrice = 120m,
        VatRate = 20m,
        RawStatus = "Delivered"
    };

    private Invoice NewInvoice(Seed seed, Guid orderId, InvoiceStatus status) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = seed.Tenant.Id,
        OrderId = orderId,
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
        IdempotencyKey = $"audit-invoice:{Guid.NewGuid():N}",
        ExternalReference = $"remote-{Guid.NewGuid():N}",
        InvoiceNumber = "GIB2026000000001",
        IssuedAt = fixture.Now.AddDays(-1),
        CreatedAt = fixture.Now.AddDays(-1),
        UpdatedAt = fixture.Now.AddDays(-1)
    };

    private MarketplaceDeliveryState NewConfirmedDeliveryState(Seed seed, Invoice invoice) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = seed.Tenant.Id,
        InvoiceId = invoice.Id,
        ConnectionId = seed.Marketplace.Id,
        PackageId = seed.Package.Id,
        AttemptNumber = 1,
        ExternalIdempotencyKey = $"delivery:{invoice.Id:N}:{seed.Package.Id:N}",
        RequestHash = "audit-hash",
        DeliveryType = "LINK",
        Status = "CONFIRMED",
        ExternalReference = seed.Package.ExternalPackageId,
        CreatedAt = fixture.Now.AddDays(-1),
        UpdatedAt = fixture.Now.AddDays(-1),
        CompletedAt = fixture.Now.AddDays(-1)
    };

    private FileAsset NewFileAsset(Guid tenantId) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = tenantId,
        Classification = "INVOICE_DOCUMENT",
        RelativePath = "audit/invoice.pdf",
        MimeType = "application/pdf",
        SizeBytes = 10,
        Sha256 = new string('A', 64),
        Status = "ACTIVE",
        CreatedAt = fixture.Now
    };

    private async Task SeedAsync(Seed seed, Action<AppDbContext>? extra = null)
    {
        await using var db = fixture.CreateContext();
        db.Tenants.Add(seed.Tenant);
        db.PlatformConnections.AddRange(seed.Marketplace, seed.Provider);
        db.Orders.Add(seed.Order);
        db.ShipmentPackages.Add(seed.Package);
        db.LegalEntityProfiles.Add(seed.Profile);
        db.InvoicePolicies.Add(seed.Policy);
        extra?.Invoke(db);
        await db.SaveChangesAsync();
    }

    private async Task DeleteTenantAsync(Guid tenantId)
    {
        await using var db = fixture.CreateContext();
        await db.IntegrationOutboxEvents.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.IntegrationJobs.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.AuditLogs.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.OperationalIssues.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
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
        await db.ReturnClaims.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.PlatformCredentials.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.ShipmentPackages.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.OrderLines.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.Orders.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.PlatformConnections.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.Tenants.Where(row => row.Id == tenantId).ExecuteDeleteAsync();
    }

    // Simulates a concurrent fiscal write: the first save that inserts an invoice fails.
    private sealed class FailFirstInvoiceSave : SaveChangesInterceptor
    {
        private bool fired;

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!fired && eventData.Context!.ChangeTracker.Entries<Invoice>().Any(entry => entry.State == EntityState.Added))
            {
                fired = true;
                throw new DbUpdateException("Simulated concurrent fiscal write.");
            }
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    private sealed class StatusProvider(string canonicalStatus) : IInvoiceProviderPort
    {
        public Task<AdapterResult<InvoiceRemoteStatus>> QueryStatusAsync(AdapterContext context, ExternalInvoiceReference reference, CancellationToken cancellationToken) =>
            Task.FromResult(AdapterResult<InvoiceRemoteStatus>.Success(new(reference.ExternalReference, canonicalStatus, canonicalStatus, null, null, true)));
        public Task<AdapterResult<ConnectionIdentity>> TestConnectionAsync(AdapterContext context, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<InvoiceSubmissionResult>> SubmitAsync(AdapterContext context, InvoiceSubmission submission, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<RemoteInvoiceDocument>> GetDocumentAsync(AdapterContext context, ExternalInvoiceReference reference, string documentKind, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<InvoiceCancellationResult>> CancelAsync(AdapterContext context, InvoiceCancellation command, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
