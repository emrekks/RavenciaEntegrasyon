using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Adapters;
using MarketplaceHub.Infrastructure.Adapters.Hepsiburada;
using MarketplaceHub.Infrastructure.Adapters.N11;
using MarketplaceHub.Infrastructure.Adapters.Pazarama;
using MarketplaceHub.Infrastructure.Adapters.PttAvm;
using MarketplaceHub.Infrastructure.Adapters.Trendyol;
using MarketplaceHub.Infrastructure.Adapters.Trendyol.Mapping;
using MarketplaceHub.Infrastructure.Imports;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace MarketplaceHub.Infrastructure.Persistence;

public sealed partial class MarketplaceJobProcessor(AppDbContext db, IConnectionPort connections, IReferenceDataPort references, IProductPort products, IInventoryPricePort inventoryPrice, IOrderPort orders, IHepsiburadaInvoiceStatusPort hepsiburadaInvoiceStatus, IOrderPackageReadPort orderPackages, IReturnPort returns, IMarketplaceQuestionService questions, IPrivateFileStorage files, IConfiguration configuration, TimeProvider timeProvider, IInvoicingBillingService? invoiceBilling = null, IEnumerable<IJobHandler>? handlers = null, ILogger<MarketplaceJobProcessor>? logger = null) : IMarketplaceJobProcessor
{
    // The payload deadline is the authoritative approval bound. The worker currently
    // applies exponential backoff, but this ceiling also keeps retry accounting from
    // becoming the earlier bound if the polling schedule is made more frequent later.
    private const int ProductApprovalReconcileMaxAttempts = (7 * 24 * 12) + 1;
    private const int MaxReferenceItems = 500_000;
    private const int MaxBrandReferenceItems = 2_000_000;
    // The job types that have their own handler class; built here when the processor is made without DI.
    private readonly IReadOnlyList<IJobHandler> jobHandlers = handlers?.ToArray() ?? [new MarketplaceHub.Infrastructure.Jobs.Handlers.QuestionSyncJobHandler(questions)];
    // Every marketplace write opens, sends and closes its ExternalEffect record through this guard (ISLEYICI-10).
    private readonly ExternalEffectGuard effectGuard = new(db, timeProvider);
    // When the marketplace stock of the model group being finalized was read (K-2); null outside a catalog import.
    private DateTimeOffset? catalogStockReadAt;
    // Trendyol orders an integrity check refused during this job (T2-O8), and orders of any marketplace stored with a
    // product line that could not be read; the order read hands them to its re-read list.
    private readonly HashSet<string> rereadOrderIds = new(StringComparer.Ordinal);
    private bool orderRereadRan;
    private int telemetryRequestCount;
    private int telemetryReceivedCount;
    private int telemetryChangedCount;
    private int telemetryInsertedCount;
    private int telemetryUpdatedCount;
    private int telemetrySkippedCount;
    private int telemetryFailedCount;
    private int telemetryRetryCount;
    private int telemetryRateLimitCount;
    private int telemetryImportProcessedCount;
    private int telemetryImportSkippedCount;
    private int telemetryImportFailedCount;
    // A product import run reports to its cursor what the job reports (received, processed, skipped, failed), counted
    // over the whole import, not each product the pages brought as changed (audit 2026-11-06 O3).
    private bool productImportTelemetry;
    private int telemetryImportReceivedCount;
    // Sizes (barcodes) Trendyol lists on an owner-edited card that a plain pull kept whole and did not add: the run's
    // count for the progress line, the last product's for its read record.
    private int telemetryUnaddedSizeCount;
    private int productUnaddedSizeCount;
    // The result line of a product pull, by mode (owner, 12 Nov 2026): what the focused price/stock path changed, what
    // "Yeni Ürünleri Aktar" added, what "Ürün Eşleme" linked. Counted over this run of the job.
    private ImportSummaryMode importSummaryMode;
    private readonly HashSet<Guid> priceChangedVariantIds = [];
    private readonly HashSet<Guid> stockChangedVariantIds = [];
    private int telemetryUnchangedVariantCount;
    private int telemetryNewProductCount;
    private int telemetryNewSizeCount;
    private int telemetryAlreadyInPanelCount;
    private int productImportedNewVariantCount;
    private int telemetryMappedCount;
    private int telemetryAlreadyMappedCount;
    private int telemetryNoCounterpartCount;
    private int telemetryMappingConflictCount;

    private enum ImportSummaryMode { Standard, FocusedPriceStock, NewOnly, Mapping }

    private enum FocusedPriceStockOutcome { NotInPanel, Conflict, Unchanged, Changed }

    public async Task<JobExecutionResult> ProcessAsync(Guid tenantId, Guid? connectionId, string jobType, string payloadJson, string correlationId, CancellationToken cancellationToken, Guid? jobId = null)
    {
        if (connectionId is null) return JobExecutionResult.Blocked("CONNECTION_REQUIRED", "Job requires a platform connection.");
        using var quotaLane = TrendyolQuotaLane.Enter(isBackground: !UsesForegroundQuota(jobType));
        var connectionState = await db.PlatformConnections.AsNoTracking().Where(x => x.TenantId == tenantId && x.Id == connectionId.Value).Select(x => new { x.PlatformCode, x.Status, x.LastErrorCode }).SingleOrDefaultAsync(cancellationToken);
        var platform = connectionState?.PlatformCode;
        if (!ActiveIntegrationScope.Contains(platform)) return JobExecutionResult.Blocked("CONNECTION_OUT_OF_SCOPE", "Connection is not active in the current integration scope.");
        // An automatic recovery test runs only while the connection still qualifies: one the seller disabled, hid or
        // deleted meanwhile, or one a test has already cleared, is not tested on its own.
        if (IsConnectionTestJob(jobType) && ConnectionAutoRecoveryPolicy.IsRecoveryPayload(payloadJson)
            && !ConnectionAutoRecoveryPolicy.IsEligible(connectionState?.Status, connectionState?.LastErrorCode))
            return JobExecutionResult.SuccessWithNote("CONNECTION_RECOVERY_NOT_NEEDED", "Bağlantı artık otomatik yeniden test kapsamında değil; test çalıştırılmadı.");
        var currentJobIsBootstrap = jobId is { } currentJobId
            && await db.IntegrationJobs.AsNoTracking().Where(x => x.Id == currentJobId).Select(x => x.JobDedupKey).AnyAsync(x => x.StartsWith(MarketplaceJobTypes.ActivationBootstrapPrefix), cancellationToken);
        if (!currentJobIsBootstrap && IsBootstrapManagedJob(jobType)
            && await db.IntegrationJobs.AsNoTracking().AnyAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId
                && x.JobDedupKey.StartsWith(MarketplaceJobTypes.ActivationBootstrapPrefix)
                && (x.Status == JobStatus.Pending || x.Status == JobStatus.Leased || x.Status == JobStatus.RetryScheduled), cancellationToken))
            // Waiting for the first full import is not a failure: the job keeps its attempts (a day at most,
            // like any deferral) instead of ending Dead while the bootstrap runs.
            return JobExecutionResult.Deferred("INITIAL_SYNC_PENDING", "İlk kapsamlı veri aktarımı tamamlanmadan artımlı senkronizasyon başlatılmayacak.", TimeSpan.FromSeconds(30));
        // A disabled connection may still be tested so it can be reactivated, but
        // no data sync or marketplace operation may execute while it is passive. A full reset only removes the panel's
        // own records and calls no marketplace, so it also runs on a passive connection.
        if (jobType is not (MarketplaceJobTypes.ConnectionTest or MarketplaceJobTypes.ShopifyConnectionTest or MarketplaceJobTypes.HepsiburadaConnectionTest or MarketplaceJobTypes.N11ConnectionTest or MarketplaceJobTypes.PazaramaConnectionTest or MarketplaceJobTypes.PttAvmConnectionTest or MarketplaceJobTypes.ConnectionFullReset) && connectionState?.Status is not ("ACTIVE" or "VERIFIED"))
            return JobExecutionResult.Blocked("CONNECTION_INACTIVE", "Bağlantı pasif olduğu için işlem çalıştırılmadı.");
        var syncLock = await MarketplaceSyncExecutionLock.AcquireForJobAsync(db, connectionId.Value, jobType, payloadJson, timeProvider, cancellationToken);
        if (syncLock is null)
        {
            // Scheduled duplicates may coalesce, but another job cannot satisfy
            // a request for a specific order/package. Preserve that work for retry.
            var triggerType = jobId is { } leasedJobId
                ? await db.IntegrationJobs.AsNoTracking().Where(x => x.Id == leasedJobId).Select(x => x.TriggerType).FirstOrDefaultAsync(cancellationToken)
                : null;
            // A recurring read is folded into the run holding the lock only when that run reads the same thing
            // (a job of the same type of the connection is running, over a scope that covers this one: a running
            // 1-day reconcile does not stand for a 90-day one); otherwise it waits its turn.
            var sameTypeRunning = MarketplaceSyncExecutionLock.IsCoalescableRead(jobType)
                && (await db.IntegrationJobs.AsNoTracking().Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId
                        && x.JobType == jobType && x.Status == JobStatus.Leased && x.Id != jobId)
                    .Select(x => x.PayloadJson).ToListAsync(cancellationToken))
                .Any(runningPayload => MarketplaceSyncExecutionLock.RunningScopeCovers(jobType, runningPayload, payloadJson));
            var contention = MarketplaceSyncExecutionLock.ContentionResult(jobType, payloadJson, triggerType, sameTypeRunning);
            MarketplaceHub.Infrastructure.Telemetry.MarketplaceHubTelemetry.JobLockContention.Add(1, new System.Diagnostics.TagList
            {
                { "job.type", jobType },
                { "lock.group", MarketplaceSyncExecutionLock.GroupFor(jobType) },
                { "result", contention.Succeeded ? "coalesced" : "deferred" }
            });
            return contention;
        }
        await using (syncLock)
        {
            var telemetryResource = TelemetryResource(jobType, payloadJson);
            var stopwatch = Stopwatch.StartNew();
            ResetTelemetry();
            readRecordingKind = ReadRecordKind(jobType) ?? "ORDER";
            readRecordingEnabled = jobId is not null && ReadRecordKind(jobType) is not null;
            if (telemetryResource is not null) await RecordSyncAttempt(tenantId, connectionId.Value, telemetryResource, cancellationToken);
            try
            {
                JobExecutionResult? directResult = null;
                // Job types moved to their own handler class run here, inside the lock and telemetry.
                if (jobHandlers.FirstOrDefault(candidate => candidate.Handles(jobType)) is { } handler)
                {
                    var outcome = await handler.HandleAsync(new JobRequest(tenantId, connectionId.Value, jobType, payloadJson, correlationId, jobId, readRecordingEnabled ? readRecorder : null), cancellationToken);
                    telemetryRequestCount += outcome.Telemetry.Requests;
                    telemetryReceivedCount += outcome.Telemetry.Received;
                    telemetryInsertedCount += outcome.Telemetry.Inserted;
                    telemetryUpdatedCount += outcome.Telemetry.Updated;
                    telemetryChangedCount += outcome.Telemetry.Changed;
                    telemetryFailedCount += outcome.Telemetry.Failed;
                    directResult = outcome.Result;
                }
                else if (jobType == MarketplaceJobTypes.ProductCreate) directResult = await CreateProduct(tenantId, connectionId.Value, payloadJson, correlationId, cancellationToken);
                else if (jobType == MarketplaceJobTypes.ProductApprovalReconcile) directResult = await ReconcileProductApproval(tenantId, connectionId.Value, payloadJson, correlationId, cancellationToken);
                else if (jobType == MarketplaceJobTypes.ProductUpdate) directResult = await UpdateProduct(tenantId, connectionId.Value, payloadJson, correlationId, cancellationToken);
                else if (jobType == MarketplaceJobTypes.ProductArchive) directResult = await ArchiveProduct(tenantId, connectionId.Value, payloadJson, correlationId, cancellationToken);
                else if (jobType == MarketplaceJobTypes.PriceInventorySync) directResult = await SyncPriceInventory(tenantId, connectionId.Value, payloadJson, correlationId, cancellationToken);
                else if (jobType == MarketplaceJobTypes.StockProjectionDispatch) directResult = await DispatchStockProjection(tenantId, connectionId.Value, payloadJson, correlationId, jobId, cancellationToken);
                else if (jobType == MarketplaceJobTypes.StockReconciliation) directResult = await ReconcileStock(tenantId, connectionId.Value, correlationId, jobId, cancellationToken);
                else if (jobType == MarketplaceJobTypes.CommonLabel) directResult = await CommonLabel(tenantId, connectionId.Value, payloadJson, correlationId, cancellationToken);
                else if (jobType == MarketplaceJobTypes.CapabilityProbe) directResult = await LabelCapabilityProbe(tenantId, connectionId.Value, payloadJson, correlationId, cancellationToken);
                else if (jobType == MarketplaceJobTypes.StageTestOrder) directResult = await CreateStageTestOrder(tenantId, connectionId.Value, payloadJson, correlationId, cancellationToken);
                else if (jobType == MarketplaceJobTypes.OrderReconciliation && platform == "TRENDYOL") directResult = await ReconcileTrendyolOrderWindows(tenantId, connectionId.Value, payloadJson, correlationId, cancellationToken);
                else if (jobType == MarketplaceJobTypes.ExternalEffectReconcile) directResult = await ReconcileExternalEffect(tenantId, connectionId.Value, payloadJson, correlationId, cancellationToken);
                else if (jobType == MarketplaceJobTypes.ListingVerify) directResult = await VerifyListings(tenantId, connectionId.Value, correlationId, jobId, cancellationToken);
                else if (jobType == MarketplaceJobTypes.ListingLookup) directResult = await RunListingLookups(tenantId, connectionId.Value, correlationId, jobId, cancellationToken);
                else if (jobType == MarketplaceJobTypes.OrderInvoiceReconciliation && platform == "TRENDYOL")
                    directResult = (await ReconcileOrderInvoices(tenantId, connectionId.Value, payloadJson, correlationId, jobId, cancellationToken)).Unfinished
                        ? JobExecutionResult.Continue("Fatura kontrolü kaldığı siparişten birkaç saniye içinde devam edecek.")
                        : JobExecutionResult.Success();
                if (directResult is not null)
                {
                    // A finished slice of a long job is progress, not a failure; it is recorded as a slice, and
                    // only the run's last slice moves the last success and resets the failure count.
                    // Waiting for the connection's order read (decision C) is not a failure either, nor is a
                    // marketplace that has not settled an approval or a batch yet.
                    if (directResult.Continues || directResult.ErrorCode == OrderFreshnessGatePolicy.WaitCode || IsRemoteWait(directResult))
                    {
                        if (telemetryResource is not null) await RecordSyncSlice(tenantId, connectionId.Value, telemetryResource, stopwatch.Elapsed, cancellationToken);
                        return directResult;
                    }
                    if (directResult.Succeeded)
                    {
                        if (telemetryResource is not null) await RecordSyncCompletion(tenantId, connectionId.Value, telemetryResource, stopwatch.Elapsed, true, null, cancellationToken);
                        return directResult;
                    }
                    telemetryFailedCount++;
                    if (directResult.Kind == JobCompletionKind.Retry) telemetryRetryCount++;
                    if (telemetryResource is not null) await TryRecordFailedSyncCompletion(tenantId, connectionId.Value, telemetryResource, stopwatch.Elapsed, directResult.ErrorCode, cancellationToken);
                    await RecordConnectionAuthenticationFailureAsync(tenantId, connectionId.Value, jobType, directResult, cancellationToken);
                    return directResult;
                }
                var succeeded = jobType switch
                {
                    MarketplaceJobTypes.ConnectionTest or MarketplaceJobTypes.ShopifyConnectionTest or MarketplaceJobTypes.HepsiburadaConnectionTest or MarketplaceJobTypes.N11ConnectionTest or MarketplaceJobTypes.PazaramaConnectionTest or MarketplaceJobTypes.PttAvmConnectionTest => await TestConnection(tenantId, connectionId.Value, correlationId, cancellationToken),
                    MarketplaceJobTypes.ReferenceSync or MarketplaceJobTypes.HepsiburadaReferenceSync or MarketplaceJobTypes.N11ReferenceSync or MarketplaceJobTypes.PazaramaReferenceSync or MarketplaceJobTypes.PttAvmReferenceSync => await SyncReferenceJob(tenantId, connectionId.Value, payloadJson, correlationId, jobId, cancellationToken),
                    MarketplaceJobTypes.OrderSync or MarketplaceJobTypes.ShopifyOrderSync or MarketplaceJobTypes.HepsiburadaOrderSync or MarketplaceJobTypes.N11OrderSync or MarketplaceJobTypes.PazaramaOrderSync or MarketplaceJobTypes.PttAvmOrderSync => await SyncOrdersAndMatchLines(tenantId, connectionId.Value, payloadJson, correlationId, "ORDERS_HOT", allowBaseline: false, cancellationToken),
                    MarketplaceJobTypes.OrderRecoverySync or MarketplaceJobTypes.ShopifyOrderRecoverySync or MarketplaceJobTypes.HepsiburadaOrderRecoverySync or MarketplaceJobTypes.N11OrderRecoverySync or MarketplaceJobTypes.PazaramaOrderRecoverySync or MarketplaceJobTypes.PttAvmOrderRecoverySync => await SyncOrdersAndMatchLines(tenantId, connectionId.Value, payloadJson, correlationId, "ORDERS_RECOVERY", allowBaseline: true, cancellationToken),
                    MarketplaceJobTypes.OrderStatusSync or MarketplaceJobTypes.ShopifyOrderStatusSync or MarketplaceJobTypes.HepsiburadaOrderStatusSync or MarketplaceJobTypes.PazaramaOrderStatusSync or MarketplaceJobTypes.PttAvmOrderStatusSync => await SyncOpenOrders(tenantId, connectionId.Value, correlationId, cancellationToken),
                    MarketplaceJobTypes.TrendyolOrderCargoInfoReconciliation => connectionState?.PlatformCode == "TRENDYOL"
                        && await ReconcileTrendyolCargoInfo(tenantId, connectionId.Value, correlationId, cancellationToken),
                    MarketplaceJobTypes.OrderReconciliation or MarketplaceJobTypes.ShopifyOrderReconciliation => await ReconcileOrders(tenantId, connectionId.Value, payloadJson, correlationId, cancellationToken),
                    MarketplaceJobTypes.OrderInvoiceReconciliation or MarketplaceJobTypes.ShopifyOrderInvoiceReconciliation or MarketplaceJobTypes.HepsiburadaOrderInvoiceReconciliation => (await ReconcileOrderInvoices(tenantId, connectionId.Value, payloadJson, correlationId, jobId, cancellationToken)).Succeeded,
                    MarketplaceJobTypes.ProductSync or MarketplaceJobTypes.ShopifyProductSync or MarketplaceJobTypes.HepsiburadaProductSync => await SyncProducts(tenantId, connectionId.Value, payloadJson, correlationId, jobId, cancellationToken),
                    MarketplaceJobTypes.ReturnSync or MarketplaceJobTypes.HepsiburadaReturnSync => await SyncReturns(tenantId, connectionId.Value, payloadJson, correlationId, cancellationToken),
                    MarketplaceJobTypes.ReturnStatusSync => await SyncOpenReturns(tenantId, connectionId.Value, correlationId, cancellationToken),
                    MarketplaceJobTypes.ReturnReconciliation => await ReconcileReturns(tenantId, connectionId.Value, payloadJson, correlationId, cancellationToken),
                    MarketplaceJobTypes.CatalogStructureImport => await ImportCatalogStructure(tenantId, connectionId.Value, payloadJson, correlationId, jobId, cancellationToken),
                    MarketplaceJobTypes.WebhookIngest or MarketplaceJobTypes.ShopifyWebhookIngest or MarketplaceJobTypes.HepsiburadaWebhookIngest => await IngestWebhook(tenantId, connectionId.Value, payloadJson, correlationId, cancellationToken),
                    MarketplaceJobTypes.ShipmentAction => await ShipmentAction(tenantId, connectionId.Value, payloadJson, correlationId, cancellationToken),
                    MarketplaceJobTypes.ReturnAction => await ReturnAction(tenantId, connectionId.Value, payloadJson, correlationId, cancellationToken),
                    _ => false
                };
                if (telemetryResource is not null)
                {
                    if (succeeded) await RecordSyncCompletion(tenantId, connectionId.Value, telemetryResource, stopwatch.Elapsed, true, null, cancellationToken);
                    else await TryRecordFailedSyncCompletion(tenantId, connectionId.Value, telemetryResource, stopwatch.Elapsed, "F3_JOB_REJECTED", cancellationToken);
                }
                if (succeeded) await RecordReadProgressAsync(tenantId, jobId, jobType, cancellationToken);
                return succeeded ? JobExecutionResult.Success() : JobExecutionResult.Blocked("F3_JOB_REJECTED", "Job payload, capability or current entity state did not permit the operation.");
            }
            catch (JobProcessingException exception) when (exception.Result.Continues)
            {
                // A finished slice of a long read is progress, not a failure, and not yet a completed run.
                await FlushReadRecordsAsync(tenantId, jobId, cancellationToken);
                if (telemetryResource is not null) await RecordSyncSlice(tenantId, connectionId.Value, telemetryResource, stopwatch.Elapsed, cancellationToken);
                return exception.Result;
            }
            catch (JobProcessingException exception)
            {
                if (exception.Result.Kind == JobCompletionKind.Retry) telemetryRetryCount++;
                else telemetryFailedCount++;
                if (telemetryResource is not null) await TryRecordFailedSyncCompletion(tenantId, connectionId.Value, telemetryResource, stopwatch.Elapsed, exception.Result.ErrorCode, cancellationToken);
                await RecordConnectionAuthenticationFailureAsync(tenantId, connectionId.Value, jobType, exception.Result, cancellationToken);
                return exception.Result;
            }
            catch (Exception exception)
            {
                telemetryFailedCount++;
                telemetryRetryCount++;
                if (telemetryResource is not null) await TryRecordFailedSyncCompletion(tenantId, connectionId.Value, telemetryResource, stopwatch.Elapsed, exception.GetType().Name, cancellationToken);
                throw;
            }
            finally
            {
                // A run that ended in an error, a block or a timeout still shows what it read up to that point.
                await FlushReadRecordsAsync(tenantId, jobId, CancellationToken.None, final: true);
            }
        }
    }

    // New-order reads, webhooks and user actions draw from the foreground share of the order
    // quota; reconciliation, recovery and status scans from the background share.
    internal static bool UsesForegroundQuota(string jobType) => jobType is
        MarketplaceJobTypes.OrderSync or MarketplaceJobTypes.ShopifyOrderSync or MarketplaceJobTypes.HepsiburadaOrderSync or MarketplaceJobTypes.N11OrderSync or MarketplaceJobTypes.PazaramaOrderSync or MarketplaceJobTypes.PttAvmOrderSync
        or MarketplaceJobTypes.WebhookIngest or MarketplaceJobTypes.ShopifyWebhookIngest or MarketplaceJobTypes.HepsiburadaWebhookIngest
        or MarketplaceJobTypes.ShipmentAction or MarketplaceJobTypes.ReturnAction or MarketplaceJobTypes.ExternalEffectReconcile;

    // The cursor a job's run telemetry is written to. Two jobs that may run at once (different lock groups) must not
    // share one: the cursor's Version is a concurrency token, so the second to finish failed its telemetry write and
    // an ingest that had already saved its data ran again. Webhook deliveries that lock with the return reads
    // (MarketplaceSyncExecutionLock.WebhookIngestGroup) therefore keep a cursor of their own (audit 2026-10-24 Q3).
    internal const string WebhookIngestReturnsResource = "WEBHOOK_INGEST_RETURNS";

    internal static string? TelemetryResource(string jobType, string? payloadJson) =>
        jobType is MarketplaceJobTypes.WebhookIngest or MarketplaceJobTypes.ShopifyWebhookIngest or MarketplaceJobTypes.HepsiburadaWebhookIngest
            && MarketplaceSyncExecutionLock.WebhookIngestGroup(MarketplaceSyncExecutionLock.WebhookResourceType(payloadJson)) is not null
            ? WebhookIngestReturnsResource
            : TelemetryResource(jobType);

    private static string? TelemetryResource(string jobType) => jobType switch
    {
        MarketplaceJobTypes.ConnectionTest or MarketplaceJobTypes.ShopifyConnectionTest or MarketplaceJobTypes.HepsiburadaConnectionTest or MarketplaceJobTypes.N11ConnectionTest or MarketplaceJobTypes.PazaramaConnectionTest or MarketplaceJobTypes.PttAvmConnectionTest => "CONNECTION_TEST",
        MarketplaceJobTypes.ReferenceSync or MarketplaceJobTypes.HepsiburadaReferenceSync or MarketplaceJobTypes.N11ReferenceSync or MarketplaceJobTypes.PazaramaReferenceSync or MarketplaceJobTypes.PttAvmReferenceSync => "REFERENCE_DATA",
        MarketplaceJobTypes.OrderSync or MarketplaceJobTypes.ShopifyOrderSync or MarketplaceJobTypes.HepsiburadaOrderSync or MarketplaceJobTypes.N11OrderSync or MarketplaceJobTypes.PazaramaOrderSync or MarketplaceJobTypes.PttAvmOrderSync => "ORDERS_HOT",
        MarketplaceJobTypes.OrderRecoverySync or MarketplaceJobTypes.ShopifyOrderRecoverySync or MarketplaceJobTypes.HepsiburadaOrderRecoverySync or MarketplaceJobTypes.N11OrderRecoverySync or MarketplaceJobTypes.PazaramaOrderRecoverySync or MarketplaceJobTypes.PttAvmOrderRecoverySync => "ORDERS_RECOVERY",
        MarketplaceJobTypes.OrderStatusSync or MarketplaceJobTypes.ShopifyOrderStatusSync or MarketplaceJobTypes.HepsiburadaOrderStatusSync or MarketplaceJobTypes.PazaramaOrderStatusSync or MarketplaceJobTypes.PttAvmOrderStatusSync => "ORDER_LIFECYCLE",
        MarketplaceJobTypes.TrendyolOrderCargoInfoReconciliation => "ORDER_CARGO_INFO_RECONCILIATION",
        MarketplaceJobTypes.OrderReconciliation or MarketplaceJobTypes.ShopifyOrderReconciliation => "ORDER_RECONCILIATION",
        MarketplaceJobTypes.OrderInvoiceReconciliation or MarketplaceJobTypes.ShopifyOrderInvoiceReconciliation or MarketplaceJobTypes.HepsiburadaOrderInvoiceReconciliation => "ORDER_INVOICE_RECONCILIATION",
        MarketplaceJobTypes.ReturnSync or MarketplaceJobTypes.HepsiburadaReturnSync => "RETURNS",
        MarketplaceJobTypes.ReturnStatusSync => "RETURN_LIFECYCLE",
        MarketplaceJobTypes.ReturnReconciliation => "RETURN_RECONCILIATION",
        MarketplaceJobTypes.ProductSync or MarketplaceJobTypes.ShopifyProductSync or MarketplaceJobTypes.HepsiburadaProductSync => "PRODUCTS",
        MarketplaceJobTypes.ProductCreate => "PRODUCT_CREATE",
        MarketplaceJobTypes.ProductApprovalReconcile => "PRODUCT_APPROVAL",
        MarketplaceJobTypes.ProductUpdate => "PRODUCT_UPDATE",
        MarketplaceJobTypes.ProductArchive => "PRODUCT_ARCHIVE",
        MarketplaceJobTypes.PriceInventorySync => "PRICE_INVENTORY",
        MarketplaceJobTypes.StockProjectionDispatch => "STOCK_PROJECTION",
        MarketplaceJobTypes.StockReconciliation => "STOCK_RECONCILIATION",
        MarketplaceJobTypes.QuestionSync => "QUESTIONS",
        MarketplaceJobTypes.TrendyolFinanceSync or MarketplaceJobTypes.PazaramaFinanceSync or MarketplaceJobTypes.N11SettlementSync or MarketplaceJobTypes.HepsiburadaFinanceSync => "FINANCE",
        MarketplaceJobTypes.WebhookIngest or MarketplaceJobTypes.ShopifyWebhookIngest or MarketplaceJobTypes.HepsiburadaWebhookIngest => "WEBHOOK_INGEST",
        MarketplaceJobTypes.ShipmentAction => "SHIPMENT_ACTION",
        MarketplaceJobTypes.ReturnAction => "RETURN_ACTION",
        MarketplaceJobTypes.CommonLabel => "COMMON_LABEL",
        MarketplaceJobTypes.CapabilityProbe => "CAPABILITY_PROBE",
        MarketplaceJobTypes.StageTestOrder => "STAGE_TEST_ORDER",
        MarketplaceJobTypes.ListingVerify => ListingVerificationPolicy.ResourceType,
        MarketplaceJobTypes.ListingLookup => "LISTING_LOOKUP",
        _ => null
    };

    // Reference reads are not held back: they have their own lock lane and no cursor of the first import, and a
    // category mapped during a long first order import gets its fields at once (ConnectionReferencePlan).
    internal static bool IsBootstrapManagedJob(string jobType) => jobType is
        MarketplaceJobTypes.OrderSync
        or MarketplaceJobTypes.ShopifyOrderSync
        or MarketplaceJobTypes.HepsiburadaOrderSync or MarketplaceJobTypes.N11OrderSync or MarketplaceJobTypes.PazaramaOrderSync or MarketplaceJobTypes.PttAvmOrderSync
        or MarketplaceJobTypes.OrderRecoverySync
        or MarketplaceJobTypes.ShopifyOrderRecoverySync
        or MarketplaceJobTypes.HepsiburadaOrderRecoverySync or MarketplaceJobTypes.N11OrderRecoverySync or MarketplaceJobTypes.PazaramaOrderRecoverySync or MarketplaceJobTypes.PttAvmOrderRecoverySync
        or MarketplaceJobTypes.OrderStatusSync
        or MarketplaceJobTypes.TrendyolOrderCargoInfoReconciliation
        or MarketplaceJobTypes.ShopifyOrderStatusSync
        or MarketplaceJobTypes.HepsiburadaOrderStatusSync or MarketplaceJobTypes.PazaramaOrderStatusSync or MarketplaceJobTypes.PttAvmOrderStatusSync
        or MarketplaceJobTypes.OrderReconciliation
        or MarketplaceJobTypes.ShopifyOrderReconciliation
        or MarketplaceJobTypes.OrderInvoiceReconciliation
        or MarketplaceJobTypes.ShopifyOrderInvoiceReconciliation
        or MarketplaceJobTypes.HepsiburadaOrderInvoiceReconciliation
        or MarketplaceJobTypes.ProductSync
        or MarketplaceJobTypes.ShopifyProductSync
        or MarketplaceJobTypes.HepsiburadaProductSync
        or MarketplaceJobTypes.ReturnSync
        or MarketplaceJobTypes.HepsiburadaReturnSync
        or MarketplaceJobTypes.ReturnStatusSync
        or MarketplaceJobTypes.ReturnReconciliation
        or MarketplaceJobTypes.StockReconciliation;

    // Only an Authentication-class adapter error is a blocked result that also
    // backs the connection off (JobExecutionResult.FromAdapterError). The
    // connection test records its own outcome.
    internal static bool IsConnectionTestJob(string jobType) =>
        jobType is MarketplaceJobTypes.ConnectionTest or MarketplaceJobTypes.ShopifyConnectionTest or MarketplaceJobTypes.HepsiburadaConnectionTest or MarketplaceJobTypes.N11ConnectionTest or MarketplaceJobTypes.PazaramaConnectionTest or MarketplaceJobTypes.PttAvmConnectionTest;

    // A marketplace that has not settled yet (an approval, a batch) is a wait, not a failed run: counted as one, the
    // PRODUCT_APPROVAL cursor showed 8 failures in a row for a Pazarama card that was only pending (audit 2026-11-06 Y2).
    internal static bool IsRemoteWait(JobExecutionResult result) => JobWaitingPolicy.IsWaiting(result);

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private static bool IsAmbiguous(AdapterError error) => WriteOutcomePolicy.IsUncertain(error);
    // Hepsiburada has no single-claim read. Claims that are still open locally are followed up by
    // listing the statuses a claim moves on to, over the claim creation dates of those open claims,
    // so a decision taken on Hepsiburada's own panel reaches the panel even when the status-change
    // filter of the regular scan misses it. Only claims open locally are updated here.
    // PREAPPROVAL_CONFIRM_SUBMITTED is the local mark after "send for review": Hepsiburada has no single-claim
    // read, so only this follow-up moves such a claim on.
    internal static readonly string[] HepsiburadaOpenClaimStatuses = ["NewRequest", "AwaitingAction", "InDispute", "AwaitingPreApproval", "PREAPPROVAL_CONFIRM_SUBMITTED"];
    private static readonly string[] HepsiburadaClaimFollowUpStatuses = ["Accepted", "Refunded", "Rejected", "Cancelled", "InDispute", "AwaitingPreApproval", "AwaitingAction"];

    // The claims the open-return scan follows; the scheduler asks the same question before it queues a scan. Hepsiburada
    // follows its own open claim words. Elsewhere only claims still moving on the marketplace are read again (Requested,
    // AwaitingShipment, InTransit, ActionRequired, Disputed): an approved or rejected claim is settled, and a later change
    // of it still comes with the incremental return read, which lists claims by their last change.
    internal static IQueryable<ReturnClaim> ReturnLifecycleCandidates(IQueryable<ReturnClaim> claims, Guid tenantId, Guid connectionId, string? platformCode)
    {
        claims = claims.Where(claim => claim.TenantId == tenantId && claim.ConnectionId == connectionId);
        return string.Equals(platformCode, "HEPSIBURADA", StringComparison.OrdinalIgnoreCase)
            ? claims.Where(claim => claim.Status != ReturnClaimStatus.Completed && claim.Status != ReturnClaimStatus.Cancelled
                && HepsiburadaOpenClaimStatuses.Contains(claim.RawStatus))
            : string.Equals(platformCode, "TRENDYOL", StringComparison.OrdinalIgnoreCase)
                // A rejected Trendyol return stays open until its package back to the customer leaves: the package and
                // its tracking link may come without a new lastModifiedDate, so only a read by id sees them.
                ? claims.Where(claim => claim.Status == ReturnClaimStatus.Requested || claim.Status == ReturnClaimStatus.AwaitingShipment
                    || claim.Status == ReturnClaimStatus.InTransit || claim.Status == ReturnClaimStatus.ActionRequired || claim.Status == ReturnClaimStatus.Disputed
                    || claim.Status == ReturnClaimStatus.Rejected && (claim.RejectedPackageState == null || claim.RejectedPackageState == ReturnRejectedPackageStates.None || claim.RejectedPackageState == ReturnRejectedPackageStates.AwaitingCargo))
                : claims.Where(claim => claim.Status == ReturnClaimStatus.Requested || claim.Status == ReturnClaimStatus.AwaitingShipment
                    || claim.Status == ReturnClaimStatus.InTransit || claim.Status == ReturnClaimStatus.ActionRequired || claim.Status == ReturnClaimStatus.Disputed);
    }

    // How many Trendyol claims one run dates from their item audits, and how far back: a claim changed earlier than
    // that cannot have been decided today (a decision changes the claim's lastModifiedDate).
    internal const int ReturnDecisionDatesPerRun = 25;
    internal static readonly TimeSpan ReturnDecisionDateWindow = TimeSpan.FromHours(36);

    // The decision time of a Trendyol claim: the latest audit entry, over the claim's items, that moved an item to the
    // claim's current outcome (Accepted for approved, Rejected for rejected). None found: unknown (null).
    internal static DateTimeOffset? TrendyolDecisionTime(IEnumerable<RemoteReturnLineAudit> entries, ReturnClaimStatus outcome) =>
        entries.Where(entry => MarketplaceReturnStatus.Canonicalize(entry.NewStatus) == outcome)
            .Select(entry => (DateTimeOffset?)entry.At.ToUniversalTime())
            .Max();

    // Dates the connection's recently decided Trendyol claims from getClaimItemAudits (a read). A failed read leaves
    // the claim undated for the next run; it never fails the sync.
    private async Task DateTrendyolReturnDecisionsAsync(Guid tenantId, Guid connectionId, string correlationId, CancellationToken cancellationToken)
    {
        if (returns is not IReturnAuditPort audits) return;
        var since = timeProvider.GetUtcNow().Subtract(ReturnDecisionDateWindow);
        var claims = await db.ReturnClaims
            .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.DecidedAt == null && x.LastRemoteModifiedAt >= since
                && (x.Status == ReturnClaimStatus.Approved || x.Status == ReturnClaimStatus.Rejected))
            .OrderByDescending(x => x.LastRemoteModifiedAt)
            .Take(ReturnDecisionDatesPerRun)
            .ToListAsync(cancellationToken);
        foreach (var claim in claims)
        {
            var lineIds = await db.ReturnLines.AsNoTracking().Where(x => x.TenantId == tenantId && x.ClaimId == claim.Id)
                .OrderBy(x => x.Id).Select(x => x.ExternalLineId).Take(20).ToListAsync(cancellationToken);
            var entries = new List<RemoteReturnLineAudit>();
            foreach (var lineId in lineIds)
            {
                TrackRequest();
                var result = await audits.ReturnLineAuditsAsync(Context(tenantId, connectionId, correlationId, $"return-decision-audit:{lineId}"), lineId, cancellationToken);
                if (!result.IsSuccess) { TrackResultFailure(result.Error); continue; }
                entries.AddRange(result.Value!);
            }
            if (TrendyolDecisionTime(entries, claim.Status) is not { } decidedAt) continue;
            claim.DecidedAt = decidedAt;
            claim.UpdatedAt = timeProvider.GetUtcNow();
            claim.Version++;
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    private async Task<bool> SyncHepsiburadaOpenReturns(Guid tenantId, Guid connectionId, string correlationId, CancellationToken cancellationToken)
    {
        var cursor = await Cursor(tenantId, connectionId, "RETURN_LIFECYCLE", cancellationToken);
        var open = await ReturnLifecycleCandidates(db.ReturnClaims.AsNoTracking(), tenantId, connectionId, "HEPSIBURADA")
            .Select(claim => new { claim.ExternalClaimId, claim.RawStatus, claim.CreatedAt })
            .ToListAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        cursor.LastAttemptAt = now;
        if (open.Count == 0)
        {
            cursor.LastSuccessAt = now;
            cursor.Version++;
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }

        var pending = open.ToDictionary(claim => claim.ExternalClaimId, claim => claim.RawStatus, StringComparer.Ordinal);
        // The local record date can be days after the claim was opened on Hepsiburada; the whole history window is listed.
        var windowStart = now - HepsiburadaReturnHistoryPolicy.InitialLookback;
        var seen = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var status in HepsiburadaClaimFollowUpStatuses)
        {
            if (pending.Count == 0) break;
            var offset = 0;
            while (!cancellationToken.IsCancellationRequested && pending.Count > 0)
            {
                TrackRequest();
                var result = await returns.PollAsync(
                    Context(tenantId, connectionId, correlationId, $"hepsiburada-return-lifecycle:{status}:{offset}"),
                    new ReturnPollWindow(windowStart, now, Status: status),
                    new(offset.ToString(CultureInfo.InvariantCulture), 100),
                    cancellationToken);
                if (!result.IsSuccess)
                {
                    TrackResultFailure(result.Error);
                    throw JobProcessingException.FromAdapter(result.Error!);
                }
                var page = result.Value!;
                if (!seen.TryGetValue(status, out var seenInStatus)) seen[status] = seenInStatus = [];
                seenInStatus.AddRange(page.Items.Select(item => $"{item.ExternalClaimId}:{item.RawStatus}"));
                foreach (var claim in page.Items)
                {
                    TrackReceived();
                    if (!pending.TryGetValue(claim.ExternalClaimId, out var localStatus)) { TrackSkipped(); continue; }
                    // The same claim can be listed under its old status a moment longer; keep the local one.
                    if (string.Equals(status, localStatus, StringComparison.Ordinal)) { pending.Remove(claim.ExternalClaimId); continue; }
                    await UpsertReturn(tenantId, connectionId, claim, cancellationToken);
                    await db.SaveChangesAsync(cancellationToken);
                    pending.Remove(claim.ExternalClaimId);
                }
                if (!page.HasMore) break;
                offset = int.TryParse(page.NextCursor, NumberStyles.None, CultureInfo.InvariantCulture, out var next) ? Math.Max(offset + 1, next) : offset + Math.Max(1, page.Items.Count);
            }
        }
        // What Hepsiburada listed per status in this pass, kept for diagnosis; the follow-up itself keeps no cursor.
        cursor.OpaqueCursor = JsonSerializer.Serialize(new { at = now, open = open.Select(claim => claim.ExternalClaimId), seen, unresolved = pending.Keys });
        cursor.LastSuccessAt = timeProvider.GetUtcNow();
        cursor.LastError = null;
        cursor.LastErrorAt = null;
        cursor.ConsecutiveFailureCount = 0;
        cursor.Version++;
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    private async Task<bool> SyncHepsiburadaReturns(Guid tenantId, Guid connectionId, string payloadJson, string correlationId, CancellationToken cancellationToken)
    {
        var cursor = await Cursor(tenantId, connectionId, "RETURNS", cancellationToken);
        var forceFull = ReadBoolean(payloadJson, "forceFull");
        var state = forceFull ? null : ReadHepsiburadaReturnSyncState(cursor.OpaqueCursor);
        if (state is null)
        {
            var now = timeProvider.GetUtcNow();
            var configuredOverlapSeconds = await db.ConnectionSyncPolicies.AsNoTracking()
                .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ResourceType == "RETURNS")
                .Select(x => (int?)x.OverlapSeconds)
                .SingleOrDefaultAsync(cancellationToken) ?? 900;
            var overlap = TimeSpan.FromSeconds(Math.Clamp(configuredOverlapSeconds, 60, 86_400));
            var retryAfterFailure = cursor.ConsecutiveFailureCount > 0 || !string.IsNullOrWhiteSpace(cursor.LastError);
            var full = forceFull || retryAfterFailure || HepsiburadaReturnHistoryPolicy.FullPassDue(cursor.LastSuccessAt, now);
            var lastSuccessfulChange = cursor.LastModifiedWatermark ?? cursor.LastSuccessAt ?? now.AddHours(-24);
            state = new(
                HepsiburadaReturnSyncStateVersion,
                0,
                0,
                full,
                full ? null : HepsiburadaReturnHistoryPolicy.IncrementalStatusChangeStart(lastSuccessfulChange, overlap, now),
                now);
            cursor.OpaqueCursor = JsonSerializer.Serialize(state);
            cursor.Version++;
            await db.SaveChangesAsync(cancellationToken);
        }
        while (!cancellationToken.IsCancellationRequested)
        {
            var status = HepsiburadaClaimStatuses[state.StatusIndex];
            TrackRequest();
            var result = await returns.PollAsync(
                Context(tenantId, connectionId, correlationId, $"hepsiburada-return-sync:{status}:{state.Offset}"),
                state.Full
                    ? new ReturnPollWindow(null, null, Status: status, StatusModifiedAfter: HepsiburadaReturnHistoryPolicy.InitialStatusChangeStart(state.AnchorEnd), StatusModifiedBefore: state.AnchorEnd)
                    : new ReturnPollWindow(null, null, Status: status, StatusModifiedAfter: state.StatusChangedAfter, StatusModifiedBefore: state.AnchorEnd),
                new(state.Offset.ToString(CultureInfo.InvariantCulture), 100),
                cancellationToken);
            if (!result.IsSuccess)
            {
                TrackResultFailure(result.Error);
                throw JobProcessingException.FromAdapter(result.Error!);
            }

            var pageResult = result.Value!;
            foreach (var issue in pageResult.Issues ?? [])
            {
                TrackSkipped();
                await RecordIssue(tenantId, $"hepsiburada-return:{connectionId}:{status}:{issue.Identity}", issue.Code, issue.Message, cancellationToken);
            }
            foreach (var claim in pageResult.Items)
            {
                TrackReceived();
                var orderCreatedAt = claim.OrderCreatedAt;
                var orderStored = false;
                if (orderCreatedAt is null || !HepsiburadaReturnHistoryPolicy.IsOrderWithinReturnHistory(orderCreatedAt, state.AnchorEnd))
                {
                    var storedOrderAt = await db.Orders.AsNoTracking()
                        .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId
                            && (x.ExternalOrderId == claim.ExternalOrderId || x.OrderNumber == claim.ExternalOrderId))
                        .Select(x => (DateTimeOffset?)x.OrderedAt)
                        .FirstOrDefaultAsync(cancellationToken);
                    orderStored = storedOrderAt is not null;
                    orderCreatedAt ??= storedOrderAt;
                }
                if (!HepsiburadaReturnHistoryPolicy.ImportsClaim(orderCreatedAt, state.AnchorEnd, orderStored))
                {
                    // An order older than the history the panel reads (or of unknown date) that the panel does not hold:
                    // the claim is named, never dropped silently, so a late return is seen.
                    TrackSkipped();
                    await RecordIssue(tenantId, $"hepsiburada-return-outside-history:{connectionId}:{Short(claim.ExternalClaimId, 120)}", "HEPSIBURADA_RETURN_ORDER_OUTSIDE_HISTORY",
                        Short($"Hepsiburada iade talebi {claim.ExternalClaimId} ({claim.ExternalOrderId} numaralı sipariş) panele alınmadı: sipariş panelde yok ve tarihi {(orderCreatedAt is null ? "bilinmiyor" : "son 30 günün dışında")}. Talebi Hepsiburada satıcı panelinden kontrol edin.", 1000)!, cancellationToken);
                    continue;
                }
                await ResolveIssue(tenantId, $"hepsiburada-return:{connectionId}:{status}:{claim.ExternalClaimId}", cancellationToken);
                if (orderStored) await ResolveIssue(tenantId, $"hepsiburada-return-outside-history:{connectionId}:{Short(claim.ExternalClaimId, 120)}", cancellationToken);
                await UpsertReturn(tenantId, connectionId, claim, cancellationToken);
            }

            if (pageResult.HasMore)
            {
                var nextOffset = int.TryParse(pageResult.NextCursor, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedOffset)
                    ? Math.Max(state.Offset + 1, parsedOffset)
                    : state.Offset + Math.Max(1, pageResult.Items.Count + (pageResult.Issues?.Count ?? 0));
                state = state with { Offset = nextOffset };
            }
            else if (state.StatusIndex + 1 < HepsiburadaClaimStatuses.Length)
            {
                state = state with { StatusIndex = state.StatusIndex + 1, Offset = 0 };
            }
            else
            {
                cursor.OpaqueCursor = null;
                cursor.LastModifiedWatermark = state.AnchorEnd;
                cursor.LastSuccessAt = timeProvider.GetUtcNow();
                cursor.LastError = null;
                cursor.LastErrorAt = null;
                cursor.ConsecutiveFailureCount = 0;
                cursor.Version++;
                await db.SaveChangesAsync(cancellationToken);
                break;
            }

            cursor.OpaqueCursor = JsonSerializer.Serialize(state);
            cursor.Version++;
            await db.SaveChangesAsync(cancellationToken);
        }
        return true;
    }

    private static HepsiburadaReturnSyncState? ReadHepsiburadaReturnSyncState(string? opaqueCursor)
    {
        if (!string.IsNullOrWhiteSpace(opaqueCursor))
        {
            try
            {
                var state = JsonSerializer.Deserialize<HepsiburadaReturnSyncState>(opaqueCursor);
                if (state is { Version: HepsiburadaReturnSyncStateVersion, StatusIndex: >= 0, Offset: >= 0 }
                    && state.StatusIndex < HepsiburadaClaimStatuses.Length
                    && state.AnchorEnd != default
                    && (state.Full || state.StatusChangedAfter is not null))
                    return state;
            }
            catch (JsonException) { }
        }
        return null;
    }

    private async Task<bool> SyncOpenReturns(Guid tenantId, Guid connectionId, string correlationId, CancellationToken cancellationToken)
    {
        var platformCode = await db.PlatformConnections.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.Id == connectionId)
            .Select(x => x.PlatformCode)
            .SingleOrDefaultAsync(cancellationToken);
        if (string.Equals(platformCode, "HEPSIBURADA", StringComparison.Ordinal))
            return await SyncHepsiburadaOpenReturns(tenantId, connectionId, correlationId, cancellationToken);
        if (string.Equals(platformCode, "TRENDYOL", StringComparison.OrdinalIgnoreCase))
            await DateTrendyolReturnRequestsAsync(tenantId, connectionId, correlationId, cancellationToken);
        var batchSize = configuration.GetValue("MarketplaceSync:ReturnLifecycle:BatchSize", 25);
        var (incompleteCargoBudget, completeCargoBudget) = ReturnLifecycleBatchPolicy.SplitBatch(batchSize);
        var incompleteCargoCursor = await Cursor(tenantId, connectionId, "RETURN_LIFECYCLE_CARGO_MISSING", cancellationToken);
        var completeCargoCursor = await Cursor(tenantId, connectionId, "RETURN_LIFECYCLE_CARGO_COMPLETE", cancellationToken);
        var incompleteCargoClaims = await ReadReturnLifecycleBatchAsync(tenantId, connectionId, platformCode, incompleteCargoCursor, missingCargo: true, incompleteCargoBudget, cancellationToken);
        var completeCargoClaims = await ReadReturnLifecycleBatchAsync(tenantId, connectionId, platformCode, completeCargoCursor, missingCargo: false, completeCargoBudget, cancellationToken);
        var openClaims = incompleteCargoClaims.Select(claim => (claim.Id, claim.ExternalClaimId, Cursor: incompleteCargoCursor))
            .Concat(completeCargoClaims.Select(claim => (claim.Id, claim.ExternalClaimId, Cursor: completeCargoCursor)))
            .ToArray();
        if (openClaims.Length == 0) return true;

        // Trendyol answers several claims in one request (claimIds, comma separated). A claim the batch did not bring
        // is asked for on its own below, as before; a batch the marketplace refuses as a request falls back to that too.
        var prefetched = new Dictionary<string, RemoteReturnClaim>(StringComparer.OrdinalIgnoreCase);
        if (string.Equals(platformCode, "TRENDYOL", StringComparison.OrdinalIgnoreCase) && returns is IReturnBatchReadPort batchReader)
        {
            foreach (var ids in ReturnLifecycleBatchPolicy.ClaimIdRequests(openClaims.Select(claim => claim.ExternalClaimId)))
            {
                TrackRequest();
                var many = await batchReader.GetManyAsync(Context(tenantId, connectionId, correlationId, $"return-lifecycle-batch:{ids[0]}:{ids.Length}"), ids, cancellationToken);
                if (!many.IsSuccess)
                {
                    TrackResultFailure(many.Error);
                    if (many.Error?.Class is AdapterErrorClass.Validation or AdapterErrorClass.ContractViolation) break;
                    throw JobProcessingException.FromAdapter(many.Error!);
                }
                foreach (var claim in many.Value!) prefetched.TryAdd(claim.ExternalClaimId, claim);
            }
        }

        foreach (var (claimId, externalClaimId, cursor) in openClaims)
        {
            cursor.OpaqueCursor = claimId.ToString("D");
            cursor.LastAttemptAt = timeProvider.GetUtcNow();
            cursor.Version++;
            await db.SaveChangesAsync(cancellationToken);
            AdapterResult<RemoteReturnClaim> result;
            if (prefetched.TryGetValue(externalClaimId, out var batched)) result = AdapterResult<RemoteReturnClaim>.Success(batched);
            else
            {
                TrackRequest();
                result = await returns.GetAsync(
                    Context(tenantId, connectionId, correlationId, $"return-lifecycle:{externalClaimId}"),
                    externalClaimId,
                    cancellationToken);
            }
            if (!result.IsSuccess)
            {
                if (result.Error?.Class == AdapterErrorClass.NotFound)
                {
                    await RecordIssue(tenantId, $"return-lifecycle:{connectionId}:{externalClaimId}", "REMOTE_RETURN_NOT_FOUND", "Açık iade kaydı Trendyol'da ClaimId ile bulunamadı; yerel durum korunarak incelemeye alındı.", cancellationToken);
                    await db.SaveChangesAsync(cancellationToken);
                    continue;
                }
                TrackResultFailure(result.Error);
                throw JobProcessingException.FromAdapter(result.Error!);
            }
            TrackReceived();

            await UpsertReturn(tenantId, connectionId, result.Value!, cancellationToken);
            await ResolveIssue(tenantId, $"return-lifecycle:{connectionId}:{externalClaimId}", cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
        }
        incompleteCargoCursor.LastSuccessAt = timeProvider.GetUtcNow();
        completeCargoCursor.LastSuccessAt = incompleteCargoCursor.LastSuccessAt;
        await db.SaveChangesAsync(cancellationToken);
        if (string.Equals(platformCode, "TRENDYOL", StringComparison.OrdinalIgnoreCase))
            await DateTrendyolReturnDecisionsAsync(tenantId, connectionId, correlationId, cancellationToken);
        return true;
    }

    // Claims stored before ReturnClaim.RequestedAt existed (11 Nov 2026) are dated from Trendyol: one getClaims request by
    // claimIds per run for the next claims without a request date (cursor RETURN_REQUESTED_AT, by claim id, wrapping),
    // so a claim Trendyol does not return or does not date never holds the others back. Nothing is asked once every
    // claim is dated. A refusal stops this step only; the lifecycle read goes on.
    internal const int ReturnRequestDatesPerRun = ReturnLifecycleBatchPolicy.ClaimIdsPerRequest;

    private async Task DateTrendyolReturnRequestsAsync(Guid tenantId, Guid connectionId, string correlationId, CancellationToken cancellationToken)
    {
        if (returns is not IReturnBatchReadPort batchReader) return;
        var undated = db.ReturnClaims.AsNoTracking().Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.RequestedAt == null);
        if (!await undated.AnyAsync(cancellationToken)) return;
        var cursor = await Cursor(tenantId, connectionId, "RETURN_REQUESTED_AT", cancellationToken);
        var after = Guid.TryParse(cursor.OpaqueCursor, out var cursorId) ? undated.Where(x => x.Id.CompareTo(cursorId) > 0) : undated;
        var batch = await after.OrderBy(x => x.Id).Select(x => new { x.Id, x.ExternalClaimId }).Take(ReturnRequestDatesPerRun).ToListAsync(cancellationToken);
        if (batch.Count == 0)
            batch = await undated.OrderBy(x => x.Id).Select(x => new { x.Id, x.ExternalClaimId }).Take(ReturnRequestDatesPerRun).ToListAsync(cancellationToken);
        if (batch.Count == 0) return;
        cursor.OpaqueCursor = batch[^1].Id.ToString("D");
        cursor.LastAttemptAt = timeProvider.GetUtcNow();
        cursor.Version++;
        await db.SaveChangesAsync(cancellationToken);
        var ids = batch.Select(x => x.ExternalClaimId).Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).ToArray();
        if (ids.Length == 0) return;
        TrackRequest();
        var many = await batchReader.GetManyAsync(Context(tenantId, connectionId, correlationId, $"return-requested-at:{ids[0]}:{ids.Length}"), ids, cancellationToken);
        if (!many.IsSuccess) { TrackResultFailure(many.Error); return; }
        foreach (var claim in many.Value!)
        {
            TrackReceived();
            await UpsertReturn(tenantId, connectionId, claim, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
        }
        cursor.LastSuccessAt = timeProvider.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task<IReadOnlyList<(Guid Id, string ExternalClaimId)>> ReadReturnLifecycleBatchAsync(
        Guid tenantId,
        Guid connectionId,
        string? platformCode,
        SyncCursor cursor,
        bool missingCargo,
        int batchSize,
        CancellationToken cancellationToken)
    {
        var claims = ReturnLifecycleCandidates(db.ReturnClaims.AsNoTracking(), tenantId, connectionId, platformCode);
        claims = missingCargo
            ? claims.Where(claim => claim.CargoProviderName == null || claim.CargoProviderName == "" || claim.CargoTrackingNumber == null || claim.CargoTrackingNumber == "")
            : claims.Where(claim => claim.CargoProviderName != null && claim.CargoProviderName != "" && claim.CargoTrackingNumber != null && claim.CargoTrackingNumber != "");

        var hasCursor = Guid.TryParse(cursor.OpaqueCursor, out var cursorId);
        var afterCursor = claims;
        if (hasCursor) afterCursor = afterCursor.Where(claim => claim.Id.CompareTo(cursorId) > 0);
        var page = await afterCursor.OrderBy(claim => claim.Id)
            .Select(claim => new ValueTuple<Guid, string>(claim.Id, claim.ExternalClaimId))
            .Take(batchSize)
            .ToListAsync(cancellationToken);

        if (!hasCursor || page.Count >= batchSize) return page.Select(item => (item.Item1, item.Item2)).ToArray();
        var wrapped = await claims.Where(claim => claim.Id.CompareTo(cursorId) <= 0)
            .OrderBy(claim => claim.Id)
            .Select(claim => new ValueTuple<Guid, string>(claim.Id, claim.ExternalClaimId))
            .Take(batchSize - page.Count)
            .ToListAsync(cancellationToken);
        return page.Concat(wrapped).Select(item => (item.Item1, item.Item2)).ToArray();
    }

    // The page a return reconciliation reads next: the adapter's own cursor when it gives one (Trendyol's per-bucket
    // "s:Accepted=2", which asks only the buckets that still have claims), else the next page number. Before 6 Nov 2026
    // a cursor that was not a number became "page + 1" for every bucket, so finished buckets were asked for pages past
    // their end, seven needless requests a page and a contract error when such a page came back without its list.
    internal static string NextReconcilePage(string current, string? next) =>
        !string.IsNullOrWhiteSpace(next) ? next
        : int.TryParse(current, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var number) ? (number + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)
        : "1";

    private async Task<bool> ReconcileReturns(Guid tenantId, Guid connectionId, string payloadJson, string correlationId, CancellationToken cancellationToken)
    {
        var lookbackDays = ReadBoundedInt(payloadJson, "lookbackDays", 3, 1, 90);
        var end = timeProvider.GetUtcNow();
        foreach (var storefront in TrendyolReadStorefronts.ReturnCodes)
        {
            var page = "0";
            var step = 0;
            do
            {
                TrackRequest();
                var result = await returns.PollAsync(Context(tenantId, connectionId, correlationId, $"return-reconcile:{lookbackDays}:{storefront}:{step}"), new(end.AddDays(-lookbackDays), end, storefront), new(page, 200), cancellationToken);
                if (!result.IsSuccess) { TrackResultFailure(result.Error); throw JobProcessingException.FromAdapter(result.Error!); }
                foreach (var _ in result.Value!.Items) TrackReceived();
                foreach (var claim in result.Value!.Items) await UpsertReturn(tenantId, connectionId, claim, cancellationToken);
                if (!result.Value.HasMore) break;
                page = NextReconcilePage(page, result.Value.NextCursor);
                step++;
            } while (!cancellationToken.IsCancellationRequested);
        }
        return true;
    }

    private static int ReadBoundedInt(string payloadJson, string propertyName, int fallback, int minimum, int maximum)
    {
        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            return document.RootElement.TryGetProperty(propertyName, out var value) && value.TryGetInt32(out var parsed)
                ? Math.Clamp(parsed, minimum, maximum)
                : fallback;
        }
        catch (JsonException) { return fallback; }
    }

    private static bool ReadBoolean(string payloadJson, string propertyName)
    {
        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            return document.RootElement.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.True;
        }
        catch (JsonException) { return false; }
    }

    private static bool ReadBooleanOrDefault(string payloadJson, string propertyName, bool fallback)
    {
        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            return document.RootElement.TryGetProperty(propertyName, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? value.GetBoolean()
                : fallback;
        }
        catch (JsonException) { return fallback; }
    }

    private static string? ReadText(string payloadJson, string propertyName)
    {
        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            return document.RootElement.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch (JsonException) { return null; }
    }

    internal static ReturnSyncState ReadReturnSyncState(SyncCursor cursor, DateTimeOffset now, TimeSpan overlap, bool forceFull = false)
    {
        if (forceFull) return InitialReturnWindow(now);
        if (!string.IsNullOrWhiteSpace(cursor.OpaqueCursor))
        {
            // The oldest cursors were a bare page number (a number is not a state object, so it is read first).
            if (int.TryParse(cursor.OpaqueCursor, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var oldPage))
                return InitialReturnWindow(now) with { Page = oldPage };
            try
            {
                var state = JsonSerializer.Deserialize<ReturnSyncState>(cursor.OpaqueCursor);
                if (state is { Version: ReturnSyncStateVersion, StoreFrontIndex: >= 0, Page: >= 0 } && state.StoreFrontIndex < TrendyolReadStorefronts.ReturnCodes.Length)
                    return state.PageCursor is { } pageCursor && (string.IsNullOrWhiteSpace(pageCursor) || pageCursor.Length > MaximumReturnPageCursorLength)
                        ? state with { PageCursor = null }
                        : state;
            }
            catch (JsonException) { }
        }
        var watermark = cursor.LastModifiedWatermark ?? cursor.LastSuccessAt;
        return watermark is null
            ? InitialReturnWindow(now)
            : new(ReturnSyncStateVersion, 0, 0, false, watermark.Value.Subtract(overlap), now);
    }

    internal static string SerializeReturnSyncState(ReturnSyncState state) => JsonSerializer.Serialize(state);
    private static ReturnSyncState InitialReturnWindow(DateTimeOffset now) => new(ReturnSyncStateVersion, 0, 0, false, now.AddMonths(-3), now);

    // The claim the seller marked received (MarkReturnReceivedAsync: Aksiyon Bekliyor, raw RECEIVED_BY_SELLER) while the
    // marketplace still reports a status from before the receipt.
    internal static bool AwaitsMarketplaceAfterSellerReceipt(string? storedRawStatus, ReturnClaimStatus target) =>
        string.Equals(storedRawStatus, "RECEIVED_BY_SELLER", StringComparison.Ordinal)
        && target is ReturnClaimStatus.Requested or ReturnClaimStatus.AwaitingShipment or ReturnClaimStatus.InTransit;

    private async Task UpsertReturnCore(Guid tenantId, Guid connectionId, RemoteReturnClaim remote, CancellationToken cancellationToken)
    {
        var remoteLines = remote.Lines.Count > 0 ? remote.Lines : TrendyolJsonMapper.ReturnLines(remote.RawJson);
        var claimLevelTarget = MarketplaceReturnStatus.Canonicalize(remote.RawStatus, remote.CargoTrackingLink);
        var remoteLineStatuses = remoteLines.Select(x => x.RawStatus).ToList();
        // A claim that mixes accepted lines with lines still waiting for the seller stays actionable; one whose lines
        // were decided both ways (accepted and rejected) is approved in part (ReturnClaimPartialApprovalPolicy).
        var target = ReturnLineStatusPolicy.Resolve(claimLevelTarget, remoteLineStatuses);
        var lineStatusOverrides = target != claimLevelTarget;
        var allLinesKnown = remoteLineStatuses.Count > 0 && remoteLineStatuses.All(x => !string.IsNullOrWhiteSpace(x));
        if (lineStatusOverrides && target == ReturnClaimStatus.ActionRequired) remote = remote with { RawStatus = "WaitingInAction" };
        if (!ReturnClaimStoragePolicy.ShouldPersist(target))
        {
            if (!await RemoveCancelledReturnClaim(tenantId, connectionId, remote, cancellationToken)) telemetrySkippedCount++;
            readReturnSkipReason = "Pazaryerinde kapanmış veya iptal edilmiş talep; panelde tutulmaz.";
            return;
        }

        var order = await db.Orders.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && (x.ExternalOrderId == remote.ExternalOrderId || x.OrderNumber == remote.ExternalOrderId), cancellationToken);
        if (order is null)
        {
            // Claims carry order-line identity even when the order falls
            // outside the order API history window. Reconstruct a minimal
            // local read model from the platform's own claim contract rather
            // than issuing a doomed remote lookup per claim.
            var platformCode = await db.PlatformConnections.AsNoTracking()
                .Where(x => x.TenantId == tenantId && x.Id == connectionId)
                .Select(x => x.PlatformCode)
                .SingleOrDefaultAsync(cancellationToken);
            // Only Trendyol and Hepsiburada claims carry the order lines; an
            // n11/Pazarama claim would build a Trendyol-shaped empty order that
            // is never filled, so those are reported below and skipped.
            var claimOrder = platformCode switch
            {
                "HEPSIBURADA" => HepsiburadaJsonMapper.OrderFromReturnClaim(remote.RawJson),
                "TRENDYOL" => TrendyolJsonMapper.OrderFromReturnClaim(remote.RawJson),
                _ => null
            };
            if (claimOrder is not null)
            {
                // The claim already contains the product snapshot. Avoid
                // remote product lookups during a historical return scan.
                await UpsertOrder(tenantId, connectionId, claimOrder, cancellationToken);
                await ResolveIssue(tenantId, $"return-order:{connectionId}:{remote.ExternalOrderId}", cancellationToken);
                order = await db.Orders.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && (x.ExternalOrderId == remote.ExternalOrderId || x.OrderNumber == remote.ExternalOrderId), cancellationToken);
            }
        }
        if (order is null)
        {
            await RecordIssue(tenantId, $"return-order:{connectionId}:{remote.ExternalOrderId}", "RETURN_ORDER_NOT_FOUND", "Return claim yerel order ile eşleşmedi; sessiz kayıt oluşturulmadı.", cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            readReturnFailReason = "Bağlı sipariş panelde bulunamadı; talep kaydedilmedi.";
            return;
        }
        // A previous scan may have reconstructed the order after recording the
        // diagnostic. Resolve that stale diagnostic on the next successful read.
        await ResolveIssue(tenantId, $"return-order:{connectionId}:{remote.ExternalOrderId}", cancellationToken);
        var now = timeProvider.GetUtcNow(); var claim = await db.ReturnClaims.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ExternalClaimId == remote.ExternalClaimId, cancellationToken);
        var storedStatusUnknown = claim is not null && (claim.Status == ReturnClaimStatus.Unknown || MarketplaceReturnStatus.Canonicalize(claim.RawStatus) == ReturnClaimStatus.Unknown);
        var remoteCargoProvider = string.IsNullOrWhiteSpace(remote.CargoProviderName) ? null : remote.CargoProviderName.Trim();
        var remoteCargoTracking = string.IsNullOrWhiteSpace(remote.CargoTrackingNumber) ? null : remote.CargoTrackingNumber.Trim();
        // Freshness is decided once for the whole read: an older snapshot than the one stored changes neither the
        // claim's status, its lines, its open decisions nor a pending "Stoğa Ekle"; only non-empty cargo facts merge.
        var remoteIsFresh = claim is null || !IsStaleReturnObservation(claim.LastRemoteModifiedAt, remote.LastModifiedAt);
        if (claim is null) { claim = NewReturnClaim(tenantId, connectionId, order.Id, remote, target, now); db.ReturnClaims.Add(claim); telemetryInsertedCount++; }
        else
        {
            var claimChanged = false;
            var previousRawStatus = claim.RawStatus;
            var previousClaimStatus = claim.Status;
            // A partial line decision waits for the lines' own statuses, except on a marketplace that decides the whole
            // claim (Shopify, Hepsiburada; ReturnDecisionScope): it never reports a line apart, so the claim follows what
            // the marketplace says on the next read instead of waiting forever (report item 8).
            var partialLineDecisionPending = string.Equals(claim.RawStatus, "PARTIAL_LINE_DECISION_PENDING", StringComparison.Ordinal) && !allLinesKnown
                && !ReturnDecisionScope.IsWholeClaim(await db.PlatformConnections.AsNoTracking().Where(x => x.TenantId == tenantId && x.Id == connectionId).Select(x => x.PlatformCode).SingleOrDefaultAsync(cancellationToken));
            if (remoteIsFresh && !partialLineDecisionPending && (lineStatusOverrides || ReturnClaimStateMachine.CanTransition(claim.Status, target)))
            {
                // CanTransition allows staying in the same status; a read that brings the same status, raw status and
                // change time is not an update (no Version or UpdatedAt bump).
                claimChanged = ReturnStatusObservationChanges(claim, target, remote.RawStatus, remote.LastModifiedAt);
                claim.Status = target;
                claim.RawStatus = remote.RawStatus;
                claim.LastRemoteModifiedAt = remote.LastModifiedAt ?? claim.LastRemoteModifiedAt;
            }
            else if (remoteIsFresh && !partialLineDecisionPending && AwaitsMarketplaceAfterSellerReceipt(claim.RawStatus, target))
            {
                // SIPARIS-STOK-1: the seller marked the return received (Teslim Aldım, a whole-claim marketplace) and the
                // marketplace still reports it as requested or on its way. That is not a refused transition: the claim
                // stays Aksiyon Bekliyor with its RECEIVED_BY_SELLER mark (so the next read does not raise it either),
                // and only the marketplace's change time is kept.
                if (remote.LastModifiedAt is not null && claim.LastRemoteModifiedAt != remote.LastModifiedAt)
                {
                    claim.LastRemoteModifiedAt = remote.LastModifiedAt;
                    claimChanged = true;
                }
            }
            else if (remoteIsFresh && !partialLineDecisionPending
                && (claim.Status != target || !string.Equals(claim.RawStatus, remote.RawStatus, StringComparison.Ordinal) || (remote.LastModifiedAt is not null && claim.LastRemoteModifiedAt != remote.LastModifiedAt)))
            {
                // A refused transition still records what the marketplace
                // reports, so the claim is not re-read as unchanged forever;
                // the marketplace's own outcome (approved, rejected, completed)
                // is authoritative even where the local workflow would refuse it.
                var authoritative = ReturnClaimStateMachine.IsAuthoritativeRemoteOutcome(claim.Status, target);
                var previousStatus = claim.Status;
                if (authoritative) claim.Status = target;
                claim.RawStatus = remote.RawStatus;
                claim.LastRemoteModifiedAt = remote.LastModifiedAt ?? claim.LastRemoteModifiedAt;
                claimChanged = true;
                if (previousStatus != target)
                    await RecordIssue(tenantId, $"return-transition:{claim.Id:N}:{Wire(target)}", "RETURN_STATUS_TRANSITION_REFUSED",
                        authoritative
                            ? $"{claim.ExternalClaimId} numaralı iade pazaryerinde {remote.RawStatus} olarak sonuçlandı; paneldeki {Wire(previousStatus)} durumu pazaryerine göre güncellendi."
                            : $"{claim.ExternalClaimId} numaralı iade için pazaryeri {remote.RawStatus} bildirdi ancak paneldeki {Wire(previousStatus)} durumundan bu geçiş yapılamıyor; ham durum güncellendi.",
                        cancellationToken);
            }
            if (remoteIsFresh)
            {
                if (!string.Equals(claim.ReasonCode, remote.ReasonCode, StringComparison.Ordinal)) { claim.ReasonCode = remote.ReasonCode; claimChanged = true; }
                if (!string.Equals(claim.ReasonText, remote.ReasonText, StringComparison.Ordinal)) { claim.ReasonText = remote.ReasonText; claimChanged = true; }
                var (dueAt, dueEstimated) = ReturnActionDuePolicy.Next(claim.ActionDueAt, claim.ActionDueAtEstimated, previousRawStatus, remote.ActionDueAt, remote.ActionDueAtEstimated, remote.RawStatus);
                if (claim.ActionDueAt != dueAt || claim.ActionDueAtEstimated != dueEstimated) { claim.ActionDueAt = dueAt; claim.ActionDueAtEstimated = dueEstimated; claimChanged = true; }
            }

            // Cargo may be returned by a historical/status replay with an old
            // or missing lastModifiedDate. Merge non-empty cargo independently
            // of the claim timestamp, but never replace known values with null.
            if (remoteCargoProvider is not null && !string.Equals(claim.CargoProviderName, remoteCargoProvider, StringComparison.Ordinal)) { claim.CargoProviderName = remoteCargoProvider; claimChanged = true; }
            if (remoteCargoTracking is not null && !string.Equals(claim.CargoTrackingNumber, remoteCargoTracking, StringComparison.Ordinal)) { claim.CargoTrackingNumber = remoteCargoTracking; claimChanged = true; }
            var remoteCargoTrackingLink = SafeReturnTrackingLink(remote.CargoTrackingLink);
            if (remoteCargoTrackingLink is not null && !string.Equals(claim.CargoTrackingLink, remoteCargoTrackingLink, StringComparison.Ordinal)) { claim.CargoTrackingLink = remoteCargoTrackingLink; claimChanged = true; }
            var (packageState, decidedAt) = NextReturnOutcomeFacts(claim.RejectedPackageState, claim.DecidedAt, previousClaimStatus, claim.Status, remote.RejectedPackageState, remote.DecidedAt);
            if (!string.Equals(claim.RejectedPackageState, packageState, StringComparison.Ordinal)) { claim.RejectedPackageState = packageState; claimChanged = true; }
            if (claim.DecidedAt != decidedAt) { claim.DecidedAt = decidedAt; claimChanged = true; }
            // The request date does not change with the claim's state, so any read that brings it fills or corrects it
            // (also an older snapshot); a read without it leaves the stored date.
            if (remote.RequestedAt?.ToUniversalTime() is { } requestedAt && claim.RequestedAt != requestedAt) { claim.RequestedAt = requestedAt; claimChanged = true; }
            // What the customer asked for is kept once known; a read that does not say it leaves it as it is.
            if (remote.CustomerRequest is { } customerRequest && !string.Equals(claim.CustomerRequest, customerRequest, StringComparison.Ordinal)) { claim.CustomerRequest = customerRequest; claimChanged = true; }
            if (remote.ResolvedWith is { } resolvedWith && !string.Equals(claim.ResolvedWith, resolvedWith, StringComparison.Ordinal)) { claim.ResolvedWith = resolvedWith; claimChanged = true; }
            if (claimChanged) { claim.UpdatedAt = now; claim.Version++; telemetryUpdatedCount++; }
        }
        if (!remoteIsFresh)
        {
            // An older snapshot: its lines, its status word and its outcome are history the stored read already
            // replaced, so they neither rewrite a line nor settle a decision or a pending stock choice.
            await db.SaveChangesAsync(cancellationToken);
            return;
        }
        // A status word the panel does not know leaves the claim Unknown, without Onayla/Reddet (report item 9); the
        // issue tells the owner which word came, and the next read with a known word closes it.
        var unknownStatusIssueKey = $"return-status-unknown:{connectionId:N}:{remote.ExternalClaimId}";
        if (target == ReturnClaimStatus.Unknown)
            await RecordIssue(tenantId, unknownStatusIssueKey, "RETURN_STATUS_UNKNOWN",
                $"{remote.ExternalClaimId} numaralı iade için pazaryeri tanınmayan bir durum bildirdi ({(string.IsNullOrWhiteSpace(remote.RawStatus) ? "boş" : remote.RawStatus)}); iade \"Bilinmiyor\" olarak bırakıldı ve panelden işlem açılmadı. Kararı pazaryeri panelinden kontrol edin.", cancellationToken);
        else if (storedStatusUnknown)
            await ResolveIssue(tenantId, unknownStatusIssueKey, cancellationToken);
        var lineStatuses = new Dictionary<Guid, string?>();
        // The claim's open "line not linked" notices, read once per claim.
        var unlinkedPrefix = ReturnLineUnlinkedIssueKey(connectionId, claim.ExternalClaimId, "");
        var openUnlinkedIssues = (await db.OperationalIssues.AsNoTracking()
                .Where(x => x.TenantId == tenantId && x.Status != IssueStatus.Resolved && x.DedupeKey.StartsWith(unlinkedPrefix))
                .Select(x => x.DedupeKey).ToListAsync(cancellationToken))
            .ToHashSet(StringComparer.Ordinal);
        foreach (var remoteLine in remoteLines)
        {
            var candidateIds = new[] { remoteLine.ExternalOrderLineId }
                .Concat(remoteLine.AlternateExternalOrderLineIds ?? [])
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            OrderLine? orderLine = null;
            foreach (var candidateId in candidateIds)
            {
                orderLine = await db.OrderLines.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.OrderId == order.Id && x.ExternalLineId == candidateId, cancellationToken);
                if (orderLine is not null) break;
            }
            // A claimed line the order does not have is reported instead of dropped without a trace.
            var unlinkedIssueKey = ReturnLineUnlinkedIssueKey(connectionId, claim.ExternalClaimId, remoteLine.ExternalLineId);
            if (orderLine is null)
            {
                await RecordIssue(tenantId, unlinkedIssueKey, "RETURN_LINE_ORDER_LINE_NOT_FOUND",
                    candidateIds.Length == 0
                        ? $"{claim.ExternalClaimId} numaralı iadenin bir kaleminin siparişteki hangi kaleme ait olduğu bildirilmedi (doğrulanmamış iade kalemi); iade bu kalem olmadan kaydedildi."
                        : $"{claim.ExternalClaimId} numaralı iadenin bir kalemi siparişteki kalemlerle eşleşmedi; iade bu kalem olmadan kaydedildi.", cancellationToken);
                continue;
            }
            // The line now matches an order line (the order was read after the claim): the earlier "not linked" notice
            // no longer holds (audit 14 Nov 2026: five stayed open after their lines were linked).
            if (openUnlinkedIssues.Contains(unlinkedIssueKey)) await ResolveIssue(tenantId, unlinkedIssueKey, cancellationToken);
            var line = await db.ReturnLines.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ClaimId == claim.Id && x.ExternalLineId == remoteLine.ExternalLineId, cancellationToken);
            // A line whose returned quantity the marketplace did not say is not stored with a guessed quantity: an issue
            // is recorded, and a quantity read earlier is kept (report item 10).
            var quantityIssueKey = $"return-line-quantity:{connectionId:N}:{claim.ExternalClaimId}:{remoteLine.ExternalLineId}";
            if (remoteLine.Quantity <= 0)
            {
                await RecordIssue(tenantId, quantityIssueKey, "RETURN_LINE_QUANTITY_UNKNOWN",
                    $"{claim.ExternalClaimId} numaralı iadenin bir kaleminde adet bilinmiyor; pazaryeri iade adedini göndermedi. Kalem adetsiz kaydedilmedi, iade kararını pazaryeri panelinden kontrol edin.", cancellationToken);
                if (line is not null) lineStatuses[line.Id] = line.RawStatus;
                continue;
            }
            // The issue only stands for a line that was never stored; resolving it on the line's first store keeps the
            // lookup off every later read.
            if (line is null) await ResolveIssue(tenantId, quantityIssueKey, cancellationToken);
            if (line is null) db.ReturnLines.Add(line = new ReturnLine { Id = Guid.CreateVersion7(), TenantId = tenantId, ClaimId = claim.Id, OrderLineId = orderLine.Id, ExternalLineId = remoteLine.ExternalLineId, Quantity = remoteLine.Quantity, RawStatus = remoteLine.RawStatus, AcceptDetail = remoteLine.AcceptDetail, MarketplaceNote = remoteLine.Note, Resolved = remoteLine.Resolved, AcceptedBySeller = remoteLine.AcceptedBySeller }); else { line.Quantity = remoteLine.Quantity; if (!string.IsNullOrWhiteSpace(remoteLine.RawStatus)) line.RawStatus = remoteLine.RawStatus; line.AcceptDetail = remoteLine.AcceptDetail ?? line.AcceptDetail; line.MarketplaceNote = remoteLine.Note ?? line.MarketplaceNote; line.Resolved = remoteLine.Resolved ?? line.Resolved; line.AcceptedBySeller = remoteLine.AcceptedBySeller ?? line.AcceptedBySeller; }
            lineStatuses[line.Id] = line.RawStatus;
        }
        await ReconcileReturnDecisionAsync(tenantId, claim.Id, target, remote.RawStatus, lineStatuses, now, cancellationToken);
        // A "Stoğa Ekle" kept while the approval was unclear is applied once this read shows the lines approved, or
        // dropped when it shows them rejected; committed with the read, under the lines' locks.
        var claimId = claim.Id;
        await ReturnPendingStock.SettleAndSaveAsync(db, () => ReturnPendingStock.SettleClaimAsync(db, tenantId, claimId, $"return-stock:{claimId:N}", now, cancellationToken), cancellationToken);
        // Owner decision (6 Nov 2026): a Hepsiburada claim closed as an exchange sends the order's delivered invoice link again.
        if (await HepsiburadaExchangeInvoiceResend.QueueAsync(db, tenantId, claim,
                configuration.GetValue<bool>("FeatureFlags:ExternalWrites"),
                configuration.GetValue<bool>("FeatureFlags:InvoiceMarketplaceDeliveryWrites"),
                now, cancellationToken))
            await db.SaveChangesAsync(cancellationToken);
    }

    internal static string ReturnLineUnlinkedIssueKey(Guid connectionId, string externalClaimId, string externalLineId) =>
        $"return-line-unlinked:{connectionId}:{externalClaimId}:{externalLineId}";

    // A read whose marketplace change time is older than the one stored is stale. A read without a change time (or a
    // claim stored without one) cannot be called stale: unknown stays unknown, so it is taken as fresh.
    internal static bool IsStaleReturnObservation(DateTimeOffset? storedModifiedAt, DateTimeOffset? remoteModifiedAt) =>
        remoteModifiedAt is { } remoteAt && storedModifiedAt is { } storedAt && remoteAt < storedAt;

    // A decision whose outcome is still open: sent (PENDING/SUBMITTED),
    // waiting on an unclear marketplace answer (MANUAL_REVIEW with an
    // EXTERNAL_EFFECT_* code) or closed as unresolved, is settled by what the
    // marketplace reports. A decision that names its lines is settled by those
    // lines (the claim's other lines may still wait for their own decision);
    // one recorded before its lines were kept is settled by the claim status,
    // and only when it is the latest, as before.
    private async Task ReconcileReturnDecisionAsync(Guid tenantId, Guid claimId, ReturnClaimStatus target, string remoteRawStatus, IReadOnlyDictionary<Guid, string?> lineStatuses, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var decisions = await db.ReturnDecisions
            .Where(x => x.TenantId == tenantId && x.ClaimId == claimId
                && (x.Status == "PENDING" || x.Status == "SUBMITTED"
                    || x.Status == "MANUAL_REVIEW" && x.ErrorCode != null && x.ErrorCode.StartsWith("EXTERNAL_EFFECT_")
                    || x.Status == "FAILED" && x.ErrorCode == "EXTERNAL_EFFECT_UNRESOLVED"))
            .OrderByDescending(x => x.CreatedAt)
            .ToListAsync(cancellationToken);
        for (var index = 0; index < decisions.Count; index++)
        {
            var decision = decisions[index];
            if (decision.ReturnLineIds is not { Length: > 0 } && index > 0) continue;
            var decisionTarget = decision.ReturnLineIds is { Length: > 0 } decisionLineIds
                ? ReturnDecisionLinePolicy.Outcome(target, decisionLineIds.Select(lineId => lineStatuses.GetValueOrDefault(lineId)).ToList())
                : target;
            if (ReconciledReturnDecision(decision.Action, decision.Status, decisionTarget, remoteRawStatus) is not { } outcome) continue;
            decision.Status = outcome.Status;
            decision.ErrorCode = outcome.ErrorCode;
            if (outcome.Status != "SUBMITTED") decision.CompletedAt = now;
        }
    }

    internal static (string Status, string? ErrorCode)? ReconciledReturnDecision(string action, string currentStatus, ReturnClaimStatus target, string remoteRawStatus)
    {
        var confirmed = action == "APPROVE" && target is ReturnClaimStatus.Approved or ReturnClaimStatus.Completed
            || action == "REJECT" && target is ReturnClaimStatus.Rejected or ReturnClaimStatus.Disputed
            || action == "PREAPPROVAL_CONFIRM" && (target is ReturnClaimStatus.AwaitingShipment or ReturnClaimStatus.InTransit
                || target == ReturnClaimStatus.ActionRequired && !HepsiburadaReturnActionPolicy.IsAwaitingPreApproval(remoteRawStatus));
        var conflictingTerminal = action == "APPROVE" && target is ReturnClaimStatus.Rejected or ReturnClaimStatus.Cancelled
            || action == "REJECT" && target is ReturnClaimStatus.Approved or ReturnClaimStatus.Completed
            || action == "PREAPPROVAL_CONFIRM" && target is ReturnClaimStatus.Approved or ReturnClaimStatus.Rejected or ReturnClaimStatus.Cancelled;
        if (confirmed) return ("SUCCEEDED", null);
        // An unresolved decision only ever moves on to a confirmed outcome.
        if (currentStatus == "FAILED") return null;
        if (conflictingTerminal) return ("MANUAL_REVIEW", "RETURN_ACTION_READBACK_CONFLICT");
        return currentStatus is "PENDING" or "SUBMITTED" ? ("SUBMITTED", null) : null;
    }

    // Returns true when the claim was kept as Cancelled because it has history.
    private async Task<bool> RemoveCancelledReturnClaim(Guid tenantId, Guid connectionId, RemoteReturnClaim remote, CancellationToken cancellationToken)
    {
        var stored = await db.ReturnClaims.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ExternalClaimId == remote.ExternalClaimId)
            .Select(x => new { x.Id, x.LastRemoteModifiedAt })
            .SingleOrDefaultAsync(cancellationToken);
        if (stored is null) return false;
        // An older "cancelled" read than the stored one neither deletes the claim nor cancels it (RET-01).
        if (IsStaleReturnObservation(stored.LastRemoteModifiedAt, remote.LastModifiedAt)) return false;
        var claimId = stored.Id;
        var now = timeProvider.GetUtcNow();
        if (!await CancelledReturnClaimCleanup.RemoveOrRetainAsync(db, tenantId, claimId, remote.RawStatus, remote.LastModifiedAt, now, cancellationToken)) return false;
        await ReconcileReturnDecisionAsync(tenantId, claimId, ReturnClaimStatus.Cancelled, remote.RawStatus, new Dictionary<Guid, string?>(), now, cancellationToken);
        await ReturnPendingStock.SettleAndSaveAsync(db, () => ReturnPendingStock.SettleClaimAsync(db, tenantId, claimId, $"return-stock:{claimId:N}", now, cancellationToken), cancellationToken);
        return true;
    }

    // Whether a read of a claim says anything new about its status: another status, raw status or marketplace change time.
    internal static bool ReturnStatusObservationChanges(ReturnClaim claim, ReturnClaimStatus target, string remoteRawStatus, DateTimeOffset? remoteLastModifiedAt) =>
        claim.Status != target
        || !string.Equals(claim.RawStatus, remoteRawStatus, StringComparison.Ordinal)
        || (remoteLastModifiedAt is not null && claim.LastRemoteModifiedAt != remoteLastModifiedAt);

    internal static ReturnClaim NewReturnClaim(Guid tenantId, Guid connectionId, Guid orderId, RemoteReturnClaim remote, ReturnClaimStatus status, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = tenantId,
        ConnectionId = connectionId,
        OrderId = orderId,
        ExternalClaimId = remote.ExternalClaimId,
        Status = status,
        RawStatus = remote.RawStatus,
        CargoProviderName = string.IsNullOrWhiteSpace(remote.CargoProviderName) ? null : remote.CargoProviderName.Trim(),
        CargoTrackingNumber = string.IsNullOrWhiteSpace(remote.CargoTrackingNumber) ? null : remote.CargoTrackingNumber.Trim(),
        CargoTrackingLink = SafeReturnTrackingLink(remote.CargoTrackingLink),
        ReasonCode = remote.ReasonCode,
        ReasonText = remote.ReasonText,
        ActionDueAt = remote.ActionDueAt,
        ActionDueAtEstimated = remote.ActionDueAtEstimated,
        LastRemoteModifiedAt = remote.LastModifiedAt,
        RejectedPackageState = status == ReturnClaimStatus.Rejected ? remote.RejectedPackageState : null,
        DecidedAt = status is ReturnClaimStatus.Approved or ReturnClaimStatus.Rejected ? remote.DecidedAt?.ToUniversalTime() : null,
        CustomerRequest = remote.CustomerRequest,
        ResolvedWith = remote.ResolvedWith,
        RequestedAt = remote.RequestedAt?.ToUniversalTime(),
        CreatedAt = now,
        UpdatedAt = now,
        Version = 1
    };

    // The rejected package state and the decision date after a read. A new outcome drops the old outcome's date (a
    // Trendyol claim is dated again from its audits); a date the read brings is kept only for an approved or rejected
    // claim. A package that left (or is never sent back) does not go back to waiting on an older read.
    internal static (string? RejectedPackageState, DateTimeOffset? DecidedAt) NextReturnOutcomeFacts(
        string? currentPackageState, DateTimeOffset? currentDecidedAt, ReturnClaimStatus previousStatus, ReturnClaimStatus status, string? remotePackageState, DateTimeOffset? remoteDecidedAt)
    {
        var packageState = status != ReturnClaimStatus.Rejected ? null
            : remotePackageState is null || !ReturnRejectedPackageStates.IsOpen(currentPackageState) && ReturnRejectedPackageStates.IsOpen(remotePackageState) ? currentPackageState
            : remotePackageState;
        var decidedAt = status is not (ReturnClaimStatus.Approved or ReturnClaimStatus.Rejected) ? null
            : remoteDecidedAt is { } remoteDate ? remoteDate.ToUniversalTime()
            : previousStatus == status ? currentDecidedAt
            : null;
        return (packageState, decidedAt);
    }

    private static string? SafeReturnTrackingLink(string? value) =>
        Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
            ? uri.ToString()
            : null;

    private async Task<bool> ShipmentAction(Guid tenantId, Guid connectionId, string payloadJson, string correlationId, CancellationToken cancellationToken)
    {
        ShipmentActionJobPayload? payload;
        try { payload = JsonSerializer.Deserialize<ShipmentActionJobPayload>(payloadJson, JsonOptions); }
        catch (JsonException) { return false; }
        if (payload is null || payload.JobId == Guid.Empty || payload.PackageId == Guid.Empty || string.IsNullOrWhiteSpace(payload.Action)) return false;
        var job = await db.IntegrationJobs.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == payload.JobId && x.ConnectionId == connectionId && x.JobType == MarketplaceJobTypes.ShipmentAction, cancellationToken);
        var package = await db.ShipmentPackages.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == payload.PackageId && x.ConnectionId == connectionId, cancellationToken);
        if (job is null || package is null) return false;
        if (!await ExternalWriteAllowedAsync(tenantId, connectionId, MarketplaceExternalWritePolicies.Shipment, cancellationToken)) throw new JobProcessingException(JobExecutionResult.Blocked("EXTERNAL_WRITE_POLICY_DISABLED", "Kargo dış yazma akışı kapalı; pazar yeri isteği gönderilmedi."));
        var effect = await effectGuard.FindAsync(tenantId, MarketplaceJobTypes.ShipmentAction, job.EffectIdempotencyKey, cancellationToken);
        switch (ExternalEffectGuard.Gate(effect))
        {
            case ExternalEffectGate.Open: throw new JobProcessingException(JobExecutionResult.ManualReview("EXTERNAL_EFFECT_AMBIGUOUS", "Önceki paket aksiyonunun sonucu kesinleştirilemedi; tekrar gönderim engellendi."));
            case ExternalEffectGate.Unresolved: return await VerifyUnresolvedShipmentEffect(tenantId, connectionId, effect!, correlationId, cancellationToken);
            case ExternalEffectGate.Done: return true;
        }
        // The record carries what the action changes, like the instant actions do, so an unclear outcome is
        // verified by reading the marketplace instead of waiting a week to be closed as unresolved.
        var (effectAction, expectedValue) = ShipmentEffectContext(payload.Action, payload.PayloadJson);
        // ISLEYICI-6 (15 Nov 2026): every database read comes before the effect record; after it only the marketplace
        // call runs, so a failed read can no longer leave a request that was never sent as an unclear effect.
        var storeFrontCode = await db.Orders.AsNoTracking().Where(x => x.TenantId == tenantId && x.Id == package.OrderId).Select(x => x.StoreFrontCode).SingleOrDefaultAsync(cancellationToken);
        effect = effectGuard.New(tenantId, MarketplaceJobTypes.ShipmentAction, job.EffectIdempotencyKey, connectionId, package.Id, effectAction, expectedValue);
        var send = await effectGuard.SendAsync(effect, () =>
        {
            TrackRequest();
            return orders.ExecutePackageActionAsync(Context(tenantId, connectionId, correlationId, job.EffectIdempotencyKey), new(package.ExternalPackageId, payload.Action, payload.PayloadJson, storeFrontCode), cancellationToken);
        }, cancellationToken);
        if (!send.Accepted)
        {
            TrackResultFailure(send.Error);
            if (send.Outcome == ExternalEffectSendOutcome.Uncertain) throw new JobProcessingException(JobExecutionResult.ManualReview("EXTERNAL_EFFECT_AMBIGUOUS", "Paket aksiyonunun uzak tarafta uygulanıp uygulanmadığı kesinleştirilemedi.", send.Error.RemoteRequestId));
            await effectGuard.ReleaseAsync(effect, cancellationToken); throw JobProcessingException.FromAdapter(send.Error);
        }
        await db.SaveChangesAsync(cancellationToken);
        if (string.Equals(payload.Action, "UNPACK", StringComparison.OrdinalIgnoreCase))
            await MarkHepsiburadaPackageUnpackedAsync(tenantId, connectionId, package.Id, effect.CompletedAt!.Value, cancellationToken);
        await RecordN11ShipmentWriteEvidenceAsync(tenantId, connectionId, payload.Action, cancellationToken);
        var readbackOrder = await db.Orders.AsNoTracking().Where(x => x.TenantId == tenantId && x.Id == package.OrderId).Select(x => new { x.OrderNumber, Hint = new OrderLookupHint(x.OrderedAt, x.LastRemoteModifiedAt, x.StoreFrontCode) }).SingleAsync(cancellationToken);
        TrackRequest();
        var readback = await orders.GetAsync(Context(tenantId, connectionId, correlationId, $"{job.EffectIdempotencyKey}:readback"), readbackOrder.OrderNumber, readbackOrder.Hint, cancellationToken);
        if (readback.IsSuccess) TrackReceived();
        if (readback.IsSuccess) await UpsertOrder(tenantId, connectionId, readback.Value!, cancellationToken);
        else { await RecordIssue(tenantId, $"shipment-readback:{connectionId}:{package.ExternalPackageId}:{payload.Action}", "SHIPMENT_ACTION_READBACK_PENDING", "Paket aksiyonu kabul edildi ancak anlık order read-back tamamlanamadı; planlı sync kesinleştirecek.", cancellationToken); await db.SaveChangesAsync(cancellationToken); }
        return true;
    }

    // Q-2: a record closed as unresolved was sent once with an outcome no read could confirm. A retry must not
    // send it again (a second split or picking) nor take it as done: where an order read can judge the action
    // (picking, a carrier change) it reads once and succeeds only when the read shows the action applied;
    // everything else stays in manual review for a look at the marketplace panel.
    private async Task<bool> VerifyUnresolvedShipmentEffect(Guid tenantId, Guid connectionId, ExternalEffectRecord effect, string correlationId, CancellationToken cancellationToken)
    {
        if (effect.Action is "PICKING" or "CHANGE_CARGO_PROVIDER" && await PackageEffectVerdictAsync(tenantId, connectionId, effect, correlationId, cancellationToken) == EffectVerdict.Applied)
        {
            effect.Outcome = ExternalEffectReconcilePolicy.AppliedOutcome;
            await ResolveIssue(tenantId, $"external-effect:{effect.Id}", cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        throw new JobProcessingException(JobExecutionResult.ManualReview("EXTERNAL_EFFECT_UNRESOLVED", "Önceki paket aksiyonunun sonucu doğrulanamadan kapatıldı; tekrar gönderilmedi. Pazaryeri panelinden kontrol edin."));
    }

    // The action an open shipment effect is verified by, only where the verification job can judge it: picking
    // (the package leaves New) and a Trendyol carrier change (the package shows the requested carrier). Other
    // actions, and a Hepsiburada carrier change whose short name is not the carrier the order read shows, carry
    // no action: a read could not tell, so they keep the old close-after-a-week path.
    internal static (string? Action, string? ExpectedValue) ShipmentEffectContext(string action, string? payloadJson)
    {
        var normalized = action.Trim().ToUpperInvariant();
        if (normalized == "PICKING") return (normalized, null);
        if (normalized != "CHANGE_CARGO_PROVIDER" || string.IsNullOrWhiteSpace(payloadJson)) return (null, null);
        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            var provider = document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("cargoProvider", out var value) && value.ValueKind == JsonValueKind.String
                    ? value.GetString()?.Trim()
                    : null;
            return string.IsNullOrEmpty(provider) || provider.Length > 256 ? (null, null) : (normalized, provider);
        }
        catch (JsonException) { return (null, null); }
    }

    // Hepsiburada dissolves an unpacked package: its lines return to the order and later reads no
    // longer list it. The local row stays as superseded history (no tab, count, amount or line
    // allocation), so the order is not shown with a package that no longer exists.
    private async Task MarkHepsiburadaPackageUnpackedAsync(Guid tenantId, Guid connectionId, Guid packageId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var platform = await db.PlatformConnections.AsNoTracking().Where(x => x.TenantId == tenantId && x.Id == connectionId).Select(x => x.PlatformCode).SingleOrDefaultAsync(cancellationToken);
        if (platform != "HEPSIBURADA") return;
        var package = await db.ShipmentPackages.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == packageId, cancellationToken);
        if (package is null || ShipmentPackageSupersession.IsLocallyUnpacked(package.RawStatus)) return;
        package.Status = ShipmentPackageStatus.Cancelled;
        package.RawStatus = ShipmentPackageSupersession.LocallyUnpackedRawStatus;
        package.UpdatedAt = now;
        package.Version++;
        db.OrderStatusHistory.Add(new OrderStatusHistory { Id = Guid.CreateVersion7(), TenantId = tenantId, OrderId = package.OrderId, PackageId = package.Id, CanonicalStatus = "CANCELLED", RawStatus = ShipmentPackageSupersession.LocallyUnpackedRawStatus, SourceEventId = $"panel-unpack:{package.Id:N}:{now.ToUnixTimeMilliseconds()}", OccurredAt = now, RecordedAt = now });
        await db.SaveChangesAsync(cancellationToken);
    }

    // n11 has no sandbox and its probe is read-only, so SHIPMENT_WRITE is proven by the first
    // package action the live store accepts. Once recorded, the shipment detail offers the
    // n11 package actions (approve and splits) like the other marketplaces.
    internal static readonly string[] N11ShipmentActions = ["PICKING", "SPLIT", "MULTI_SPLIT", "SPLIT_BY_QUANTITY", "LABOR_COST", "COLLECTION_REQUEST"];

    private async Task RecordN11ShipmentWriteEvidenceAsync(Guid tenantId, Guid connectionId, string action, CancellationToken cancellationToken)
    {
        if (!N11ShipmentActions.Contains(action, StringComparer.OrdinalIgnoreCase)) return;
        var connection = await db.PlatformConnections.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == connectionId && x.PlatformCode == "N11", cancellationToken);
        if (connection is null) return;
        var capability = await db.PlatformCapabilities.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.Code == MarketplaceCapabilities.ShipmentWrite, cancellationToken);
        if (capability is null || capability.SupportLevel == CapabilitySupportLevel.Supported && capability.VerifiedAt is not null) return;
        capability.SupportLevel = CapabilitySupportLevel.Supported;
        capability.SourceUrl = "https://api.n11.com/rest/order/v1/update";
        capability.SourceVersion = "V1";
        capability.Environment = connection.Environment;
        capability.StoreScope = connection.ExternalStoreId;
        capability.ConstraintsJson = JsonSerializer.Serialize(new { allowedActions = N11ShipmentActions });
        capability.EvidenceNote = $"SHIPMENT_WRITE, N11 canlı mağazasının kabul ettiği ilk paket aksiyonuyla ({action.ToUpperInvariant()}) doğrulandı.";
        capability.VerifiedAt = timeProvider.GetUtcNow();
        capability.Version++;
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task<bool> ReturnAction(Guid tenantId, Guid connectionId, string payloadJson, string correlationId, CancellationToken cancellationToken)
    {
        // Only a payload that cannot be read blocks the job for good, and a missing decision or claim is checked
        // below. Any other failure (a dropped connection, a database fault) reaches the worker's retry handling
        // instead of being taken for a bad payload and blocking the return decision.
        Guid decisionId;
        Guid[]? selectedReturnLineIds;
        try
        {
            using var payload = JsonDocument.Parse(payloadJson);
            decisionId = payload.RootElement.GetProperty("decisionId").GetGuid();
            selectedReturnLineIds = payload.RootElement.TryGetProperty("returnLineIds", out var selectedLinesElement) && selectedLinesElement.ValueKind == JsonValueKind.Array
                ? selectedLinesElement.EnumerateArray().Select(value => value.GetGuid()).Distinct().ToArray()
                : null;
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or FormatException or InvalidOperationException) { return false; }
        var decision = await db.ReturnDecisions.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == decisionId, cancellationToken);
        if (decision is null) return false;
        if (decision.Status == "SUCCEEDED") return true;
        if (decision.Status == "MANUAL_REVIEW") throw new JobProcessingException(JobExecutionResult.ManualReview(decision.ErrorCode ?? "RETURN_ACTION_REVIEW_REQUIRED", "İade kararı manuel inceleme bekliyor.", decision.ExternalOperationId));
        var claim = await db.ReturnClaims.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == decision.ClaimId && x.ConnectionId == connectionId, cancellationToken);
        if (claim is null) return false;
        if (!await ExternalWriteAllowedAsync(tenantId, connectionId, MarketplaceExternalWritePolicies.Return, cancellationToken)) throw new JobProcessingException(JobExecutionResult.Blocked("EXTERNAL_WRITE_POLICY_DISABLED", "İade dış yazma akışı kapalı; pazar yeri isteği gönderilmedi."));
        var returnLines = db.ReturnLines.AsNoTracking().Where(x => x.TenantId == tenantId && x.ClaimId == claim.Id);
        if (selectedReturnLineIds is not null) returnLines = returnLines.Where(x => selectedReturnLineIds.Contains(x.Id));
        var lineIds = await returnLines.OrderBy(x => x.Id).Select(x => x.ExternalLineId).ToListAsync(cancellationToken);
        if (lineIds.Count == 0) { decision.Status = "FAILED"; decision.ErrorCode = "RETURN_LINES_REQUIRED"; decision.CompletedAt = timeProvider.GetUtcNow(); await db.SaveChangesAsync(cancellationToken); return false; }
        var evidenceRows = await (from evidence in db.ReturnEvidence.AsNoTracking() where evidence.TenantId == tenantId && evidence.DecisionId == decisionId join asset in db.FileAssets.AsNoTracking() on new { evidence.TenantId, Id = evidence.FileAssetId } equals new { asset.TenantId, asset.Id } select asset).ToListAsync(cancellationToken);
        var evidenceFiles = new List<ReturnEvidenceFile>(); long totalBytes = 0;
        foreach (var asset in evidenceRows)
        {
            if (asset.SizeBytes <= 0 || asset.SizeBytes > 10 * 1024 * 1024 || totalBytes + asset.SizeBytes > 25 * 1024 * 1024) { decision.Status = "FAILED"; decision.ErrorCode = "RETURN_EVIDENCE_LIMIT_EXCEEDED"; decision.CompletedAt = timeProvider.GetUtcNow(); await db.SaveChangesAsync(cancellationToken); return false; }
            await using var source = await files.OpenReadAsync(tenantId, asset.RelativePath, cancellationToken); await using var buffer = new MemoryStream(); await source.CopyToAsync(buffer, cancellationToken); if (buffer.Length != asset.SizeBytes) { decision.Status = "MANUAL_REVIEW"; decision.ErrorCode = "RETURN_EVIDENCE_SIZE_MISMATCH"; decision.CompletedAt = timeProvider.GetUtcNow(); await db.SaveChangesAsync(cancellationToken); throw new JobProcessingException(JobExecutionResult.ManualReview(decision.ErrorCode, "İade kanıt dosyasının kayıtlı boyutu ile okunan içerik eşleşmedi.")); }
            var bytes = buffer.ToArray(); var checksum = Convert.ToHexString(SHA256.HashData(bytes)); if (!string.Equals(checksum, asset.Sha256, StringComparison.OrdinalIgnoreCase)) { decision.Status = "MANUAL_REVIEW"; decision.ErrorCode = "RETURN_EVIDENCE_CHECKSUM_MISMATCH"; decision.CompletedAt = timeProvider.GetUtcNow(); await db.SaveChangesAsync(cancellationToken); throw new JobProcessingException(JobExecutionResult.ManualReview(decision.ErrorCode, "İade kanıt dosyasının checksum doğrulaması başarısız oldu.")); }
            evidenceFiles.Add(new(asset.OriginalNameSafe ?? $"evidence-{asset.Id:N}", asset.MimeType, bytes)); totalBytes += bytes.LongLength;
        }
        var existingEffect = await effectGuard.FindAsync(tenantId, MarketplaceJobTypes.ReturnAction, decision.IdempotencyKey, cancellationToken);
        var gate = ExternalEffectGuard.Gate(existingEffect);
        if (gate == ExternalEffectGate.Open) { decision.Status = "MANUAL_REVIEW"; decision.ErrorCode = "EXTERNAL_EFFECT_AMBIGUOUS"; decision.CompletedAt = timeProvider.GetUtcNow(); await db.SaveChangesAsync(cancellationToken); throw new JobProcessingException(JobExecutionResult.ManualReview(decision.ErrorCode, "Önceki iade aksiyonunun sonucu kesinleştirilemedi; tekrar gönderim engellendi.")); }
        // Q-2: a record closed as unresolved does not make the decision submitted. The claim is read once, and
        // that read settles the decision only when it shows the action's outcome; otherwise the job stays in
        // manual review and nothing is sent again.
        if (gate == ExternalEffectGate.Unresolved)
        {
            TrackRequest();
            var check = await returns.GetAsync(Context(tenantId, connectionId, correlationId, $"{decision.IdempotencyKey}:verify"), claim.ExternalClaimId, cancellationToken);
            if (check.IsSuccess) { TrackReceived(); await UpsertReturn(tenantId, connectionId, check.Value!, cancellationToken); }
            else TrackResultFailure(check.Error);
            if (decision.Status == "SUCCEEDED") return true;
            throw new JobProcessingException(JobExecutionResult.ManualReview("EXTERNAL_EFFECT_UNRESOLVED", "Önceki iade aksiyonunun sonucu doğrulanamadan kapatıldı; tekrar gönderilmedi. Pazaryeri panelinden kontrol edin."));
        }
        if (gate == ExternalEffectGate.Done)
        {
            decision.Status = "SUBMITTED"; decision.CompletedAt = null; await db.SaveChangesAsync(cancellationToken);
            TrackRequest();
            var readback = await returns.GetAsync(Context(tenantId, connectionId, correlationId, $"{decision.IdempotencyKey}:readback"), claim.ExternalClaimId, cancellationToken);
            if (readback.IsSuccess) TrackReceived();
            if (readback.IsSuccess) await UpsertReturn(tenantId, connectionId, readback.Value!, cancellationToken);
            else { await RecordIssue(tenantId, $"return-readback:{connectionId}:{claim.ExternalClaimId}:{decision.Action}", "RETURN_ACTION_READBACK_PENDING", "İade aksiyonu daha önce kabul edildi ancak read-back tamamlanamadı; planlı return sync kesinleştirecek.", cancellationToken); await db.SaveChangesAsync(cancellationToken); }
            return true;
        }
        // The claim and action are recorded as the instant return decision does, so the sweep can verify an unclear outcome.
        var effect = effectGuard.New(tenantId, MarketplaceJobTypes.ReturnAction, decision.IdempotencyKey, connectionId, claim.Id, $"RETURN_{decision.Action}");
        var send = await effectGuard.SendAsync(effect, () =>
        {
            TrackRequest();
            return returns.ExecuteAsync(Context(tenantId, connectionId, correlationId, decision.IdempotencyKey), new(claim.ExternalClaimId, lineIds, decision.Action, decision.ReasonCode, decision.Explanation, evidenceFiles), cancellationToken);
        }, cancellationToken);
        var result = send.Result;
        var now = timeProvider.GetUtcNow();
        if (send.Accepted)
        {
            TrackReceived();
            decision.Status = "SUBMITTED"; decision.ExternalOperationId = result.Value!.ExternalOperationId; decision.ErrorCode = null; decision.CompletedAt = null; await db.SaveChangesAsync(cancellationToken);
            TrackRequest();
            var readback = await returns.GetAsync(Context(tenantId, connectionId, correlationId, $"{decision.IdempotencyKey}:readback"), claim.ExternalClaimId, cancellationToken);
            if (readback.IsSuccess) TrackReceived();
            if (readback.IsSuccess) await UpsertReturn(tenantId, connectionId, readback.Value!, cancellationToken);
            else { await RecordIssue(tenantId, $"return-readback:{connectionId}:{claim.ExternalClaimId}:{decision.Action}", "RETURN_ACTION_READBACK_PENDING", "İade aksiyonu kabul edildi ancak anlık read-back tamamlanamadı; planlı return sync kesinleştirecek.", cancellationToken); await db.SaveChangesAsync(cancellationToken); }
            return true;
        }
        var error = send.Error; decision.ErrorCode = error.Code; decision.ExternalOperationId ??= error.RemoteRequestId;
        TrackResultFailure(error);
        switch (send.Outcome)
        {
            case ExternalEffectSendOutcome.Uncertain:
                decision.Status = "MANUAL_REVIEW"; decision.CompletedAt = now; await db.SaveChangesAsync(cancellationToken);
                throw new JobProcessingException(JobExecutionResult.ManualReview("EXTERNAL_EFFECT_AMBIGUOUS", "İade aksiyonunun uzak tarafta uygulanıp uygulanmadığı kesinleştirilemedi.", error.RemoteRequestId));
            // Never sent (the panel's own circuit is open): the decision stays waiting and the job sends it again later.
            case ExternalEffectSendOutcome.LocalCircuit:
                await effectGuard.ReleaseAsync(effect, cancellationToken);
                throw JobProcessingException.FromAdapter(error);
            // ISLEYICI-4 (15 Nov 2026): a refusal the job retries (a quota answer) was not applied and is sent again, so the
            // decision stays waiting, as an open decision, instead of showing "Başarısız" while the job still sends it (the
            // owner could open a second decision for the same lines meanwhile). Only a final refusal fails the decision.
            case ExternalEffectSendOutcome.Retryable:
                effectGuard.Release(effect); decision.Status = "PENDING"; decision.CompletedAt = null; await db.SaveChangesAsync(cancellationToken);
                throw new JobProcessingException(JobExecutionResult.FromAdapterError(error));
            default:
                effectGuard.Release(effect); decision.Status = "FAILED"; decision.CompletedAt = now; await db.SaveChangesAsync(cancellationToken);
                throw JobProcessingException.FromAdapter(error);
        }
    }

    private async Task<SyncCursor> Cursor(Guid tenantId, Guid connectionId, string resource, CancellationToken cancellationToken) { var cursor = await db.SyncCursors.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ResourceType == resource, cancellationToken); if (cursor is not null) return cursor; cursor = new SyncCursor { Id = Guid.CreateVersion7(), TenantId = tenantId, ConnectionId = connectionId, ResourceType = resource, Version = 1 }; db.SyncCursors.Add(cursor); return cursor; }
    private async Task RecordIssue(Guid tenantId, string key, string code, string summary, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var issue = db.OperationalIssues.Local.FirstOrDefault(x => x.TenantId == tenantId && x.DedupeKey == key)
            ?? await db.OperationalIssues.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.DedupeKey == key, cancellationToken);
        if (issue is null)
        {
            db.OperationalIssues.Add(new OperationalIssue { Id = Guid.CreateVersion7(), TenantId = tenantId, DedupeKey = key, Code = code, Summary = summary, Status = IssueStatus.Open, FirstSeenAt = now, LastSeenAt = now, OccurrenceCount = 1 });
            return;
        }

        issue.Status = IssueStatus.Open;
        issue.Code = code;
        issue.Summary = summary;
        issue.LastSeenAt = now;
        issue.OccurrenceCount++;
    }
    // Looks at the issues this unit of work already holds first, as RecordIssue does (ISLEYICI-8): an issue opened and
    // resolved before the next save is resolved, not left open.
    private async Task ResolveIssue(Guid tenantId, string key, CancellationToken cancellationToken) { var issue = db.OperationalIssues.Local.FirstOrDefault(x => x.TenantId == tenantId && x.DedupeKey == key) ?? await db.OperationalIssues.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.DedupeKey == key, cancellationToken); if (issue is not null && issue.Status != IssueStatus.Resolved) { issue.Status = IssueStatus.Resolved; issue.ResolvedAt = timeProvider.GetUtcNow(); } }
    private AdapterContext Context(Guid tenantId, Guid connectionId, string correlationId, string idempotency) => new(tenantId, connectionId, correlationId, idempotency, timeProvider.GetUtcNow().AddMinutes(2));
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static string Wire<T>(T value) where T : Enum => string.Concat(value.ToString().Select((ch, index) => char.IsUpper(ch) && index > 0 ? "_" + ch : ch.ToString())).ToUpperInvariant();
}
