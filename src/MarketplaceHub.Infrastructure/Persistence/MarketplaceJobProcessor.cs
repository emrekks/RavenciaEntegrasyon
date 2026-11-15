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
    private async Task<ReferenceSnapshot?> EnsureReferenceSnapshot(Guid tenantId, Guid connectionId, string resourceType, string? parentExternalId, string correlationId, CancellationToken cancellationToken)
    {
        var scope = string.IsNullOrWhiteSpace(parentExternalId) ? "" : parentExternalId.Trim();
        var current = await db.ReferenceSnapshots
            .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ResourceType == resourceType && x.ScopeExternalId == scope && x.IsCurrent)
            .OrderByDescending(x => x.FetchedAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (current is not null && timeProvider.GetUtcNow() - current.FetchedAt < TimeSpan.FromHours(24)) return current;

        var payload = JsonSerializer.Serialize(new { resourceType, parentExternalId });
        try
        {
            if (!await SyncReferences(tenantId, connectionId, payload, correlationId, cancellationToken)) return current;
        }
        catch (JobProcessingException exception)
        {
            await RecordIssue(tenantId, $"product-reference-sync:{connectionId}:{resourceType}:{scope}", exception.Result.ErrorCode ?? "REFERENCE_SYNC_FAILED", exception.Result.ErrorSummary ?? "Pazar yeri kategori/özellik referansı eşitlenemedi; mevcut panel kayıtları korundu.", cancellationToken);
            return current;
        }

        return await db.ReferenceSnapshots
            .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ResourceType == resourceType && x.ScopeExternalId == scope && x.IsCurrent)
            .OrderByDescending(x => x.FetchedAt)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private async Task<CategoryAttributeContext?> EnsureCategoryAttributeContext(
        Guid tenantId,
        Guid connectionId,
        ReferenceSnapshot categorySnapshot,
        RemoteCatalogProduct snapshot,
        IReadOnlyList<ReferenceItem> categoryItems,
        IDictionary<string, AttributeDefinition> importedAttributeLibrary,
        IDictionary<string, CategoryAttributeContext> cache,
        string correlationId,
        CancellationToken cancellationToken)
    {
        var externalCategoryId = Short(snapshot.CategoryExternalId, 256);
        if (string.IsNullOrWhiteSpace(externalCategoryId)) return null;
        if (cache.TryGetValue(externalCategoryId, out var cached))
        {
            await EnsureObservedVariantAttributeValues(tenantId, cached, snapshot, cancellationToken);
            await EnsureExactWebColorValueMappings(tenantId, connectionId, cached, cancellationToken);
            return cached;
        }

        var categoryItem = categoryItems.FirstOrDefault(x => string.Equals(x.ExternalId, externalCategoryId, StringComparison.Ordinal));
        if (categoryItem is null)
        {
            await RecordIssue(tenantId, $"product-category-reference:{connectionId}:{externalCategoryId}", "PRODUCT_CATEGORY_REFERENCE_MISSING", "Ürünün kategori kimliği güncel pazar yeri snapshot'ında bulunamadı; kategori özellikleri eşlenmedi.", cancellationToken);
            return null;
        }

        var attributeSnapshot = await EnsureReferenceSnapshot(tenantId, connectionId, "CATEGORY_ATTRIBUTES", externalCategoryId, correlationId, cancellationToken);
        if (attributeSnapshot is null) return null;
        var remoteAttributes = await db.ReferenceItems.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.SnapshotId == attributeSnapshot.Id && x.ResourceType == "CATEGORY_ATTRIBUTES" && x.IsActive)
            .OrderBy(x => x.SortOrder).ThenBy(x => x.Name)
            .ToListAsync(cancellationToken);

        var observedNames = snapshot.Variants
            .SelectMany(x => x.Options.Keys)
            .Select(x => NormalizeCatalogKey(x, 320))
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToHashSet(StringComparer.Ordinal);
        var observedOptionValues = snapshot.Variants
            .SelectMany(x => x.Options)
            .Select(pair => (Axis: ObservedVariantValueAxis(pair.Key), Value: NormalizeCatalogKey(pair.Value, 320)))
            .Where(item => item.Axis is not null && !string.IsNullOrWhiteSpace(item.Value))
            .GroupBy(item => item.Axis!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Select(item => item.Value).ToHashSet(StringComparer.Ordinal), StringComparer.Ordinal);
        var candidateRemoteAttributes = remoteAttributes
            .Where(x => x.IsRequired == true || observedNames.Contains(NormalizeCatalogKey(x.Name, 320)) || VariantOptionAxis(x.Name) is not null)
            .ToList();
        var valuesByAttribute = new Dictionary<string, IReadOnlyList<ReferenceItem>>(StringComparer.Ordinal);
        foreach (var remoteAttribute in candidateRemoteAttributes)
        {
            var valueSnapshot = await EnsureReferenceSnapshot(tenantId, connectionId, "ATTRIBUTE_VALUES", $"{externalCategoryId}/{remoteAttribute.ExternalId}", correlationId, cancellationToken);
            if (valueSnapshot is null) continue;
            var values = await db.ReferenceItems.AsNoTracking()
                .Where(x => x.TenantId == tenantId && x.SnapshotId == valueSnapshot.Id && x.ResourceType == "ATTRIBUTE_VALUES" && x.IsActive)
                .OrderBy(x => x.SortOrder).ThenBy(x => x.Name)
                .ToListAsync(cancellationToken);
            if (ObservedVariantValueAxis(remoteAttribute.Name) is { } axis && observedOptionValues.TryGetValue(axis, out var usedValues))
                values = values.Where(value => usedValues.Contains(NormalizeCatalogKey(value.Name, 320))).ToList();
            // A category definition without active values is not a usable
            // product attribute. The exception is the real Renk slicer: it
            // can legitimately contain seller-defined values even when
            // Trendyol's category value endpoint returns an empty list.
            // Keep that field in the context so observed variant values can
            // populate the local Renk option instead of falling back to Web
            // Color.
            if (values.Count > 0 || IsRealColorOptionKey(remoteAttribute.Name))
                valuesByAttribute[remoteAttribute.ExternalId] = values;
        }

        var localCategory = await EnsureMappedCategory(tenantId, connectionId, categorySnapshot, categoryItem, cancellationToken);
        var attributes = new Dictionary<string, LocalCategoryAttribute>(StringComparer.Ordinal);
        foreach (var remoteAttribute in candidateRemoteAttributes.Where(x => valuesByAttribute.ContainsKey(x.ExternalId)))
        {
            var values = valuesByAttribute[remoteAttribute.ExternalId];
            var localAttribute = await EnsureMappedAttribute(tenantId, connectionId, localCategory, categoryItem, attributeSnapshot, remoteAttribute, values, importedAttributeLibrary, cancellationToken);
            attributes[NormalizeCatalogKey(remoteAttribute.Name, 320)] = localAttribute;
            attributes.TryAdd(NormalizeCatalogKey(localAttribute.Definition.Name, 320), localAttribute);
        }

        // Hide stale requirements created by older importer versions when the
        // marketplace no longer provides any values for the mapped attribute.
        // Keep the row for audit/recovery, but remove it from the product
        // editor and required-attribute validation until values return.
        var importedAttributeIds = await db.AttributeMappings.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ScopeExternalId == externalCategoryId && x.Status == "VERIFIED")
            .Select(x => x.LocalId)
            .ToListAsync(cancellationToken);
        var visibleAttributeIds = attributes.Values.Select(x => x.Definition.Id).ToHashSet();
        if (importedAttributeIds.Count > 0)
        {
            var staleRequirements = await db.CategoryAttributeRequirements
                .Where(x => x.TenantId == tenantId && x.CategoryId == localCategory.Id && importedAttributeIds.Contains(x.AttributeId) && !visibleAttributeIds.Contains(x.AttributeId))
                .ToListAsync(cancellationToken);
            foreach (var stale in staleRequirements)
            {
                if (!stale.IsPanelScoped && stale.Role == "ATTRIBUTE") continue;
                stale.IsPanelScoped = false;
                stale.Role = "ATTRIBUTE";
                stale.IsRequired = false;
                stale.AllowsCustomValue = false;
                stale.Version++;
            }
        }

        var result = new CategoryAttributeContext(localCategory, externalCategoryId, attributes);
        await EnsureObservedVariantAttributeValues(tenantId, result, snapshot, cancellationToken);
        await EnsureExactWebColorValueMappings(tenantId, connectionId, result, cancellationToken);
        cache[externalCategoryId] = result;
        return result;
    }

    private async Task EnsureObservedVariantAttributeValues(
        Guid tenantId,
        CategoryAttributeContext categoryContext,
        RemoteCatalogProduct snapshot,
        CancellationToken cancellationToken)
    {
        foreach (var mapped in categoryContext.Attributes.Values
            .GroupBy(item => item.Definition.Id)
            .Select(group => group
                .OrderBy(item => IsWebColorOptionKey(item.Remote.Name) ? 1 : 0)
                .First()))
        {
            // Web Color is a presentation field. It may map to the same local
            // Renk attribute, but its marketplace values must never be added as
            // local option values. Only collect values from the real slicer
            // field (normally Trendyol's Renk/Color field).
            if (IsWebColorOptionKey(mapped.Remote.Name)) continue;
            var remoteName = NormalizeCatalogKey(mapped.Remote.Name, 320);
            var axis = VariantOptionAxis(mapped.Remote.Name);
            var values = snapshot.Variants
                .SelectMany(x => x.Options)
                .Where(pair => NormalizeCatalogKey(pair.Key, 320) == remoteName
                    || (axis is not null && VariantOptionAxis(pair.Key) == axis && !IsWebColorOptionKey(pair.Key)))
                .Select(pair => pair.Value.Trim())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (values.Count == 0) continue;
            mapped.Definition.DataType = AttributeDataType.SingleSelect;
            mapped.Definition.SelectionMode = "SINGLE";
            foreach (var valueText in values)
            {
                var normalized = NormalizeCatalogKey(valueText, 320);
                if (!categoryContext.ObservedVariantValueKeys.Add($"{mapped.Definition.Id:D}:{normalized}"))
                    continue;
                var valueKeys = CatalogLookupKeys(valueText, 320);
                var value = db.AttributeValues.Local.FirstOrDefault(x => x.TenantId == tenantId && x.AttributeId == mapped.Definition.Id && CatalogKeyNormalizer.SameKey(x.NormalizedValue, normalized, 320))
                    ?? await db.AttributeValues.Where(x => x.TenantId == tenantId && x.AttributeId == mapped.Definition.Id && valueKeys.Contains(x.NormalizedValue)).OrderBy(x => x.NormalizedValue == normalized ? 0 : 1).ThenBy(x => x.Id).FirstOrDefaultAsync(cancellationToken);
                if (value is not null)
                {
                    value.IsActive = true;
                    continue;
                }
                db.AttributeValues.Add(new AttributeValue
                {
                    Id = Guid.CreateVersion7(),
                    TenantId = tenantId,
                    AttributeId = mapped.Definition.Id,
                    Value = Short(valueText, 320),
                    NormalizedValue = normalized,
                    SortOrder = values.IndexOf(valueText),
                    IsActive = true,
                    Version = 1
                });
            }
        }
    }

    private async Task<Category> EnsureMappedCategory(Guid tenantId, Guid connectionId, ReferenceSnapshot categorySnapshot, ReferenceItem remoteCategory, CancellationToken cancellationToken)
    {
        var mapping = await db.CategoryMappings.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ScopeExternalId == categorySnapshot.ScopeExternalId && x.ExternalId == remoteCategory.ExternalId, cancellationToken);
        Category? category = mapping is null ? null : await db.Categories.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == mapping.LocalId, cancellationToken);
        var leafName = CategoryLeafName(remoteCategory.Name, remoteCategory.Path);
        var normalized = NormalizeCatalogKey(remoteCategory.Path, 160);
        var legacyNormalized = NormalizeCatalogKey(remoteCategory.Name, 160);
        if (string.IsNullOrWhiteSpace(normalized)) normalized = NormalizeCatalogKey(leafName, 160);
        var categoryKeys = CatalogLookupKeys(remoteCategory.Path).Concat(CatalogLookupKeys(remoteCategory.Name)).Concat(CatalogLookupKeys(leafName)).Distinct(StringComparer.Ordinal).ToArray();
        category ??= db.Categories.Local.FirstOrDefault(x => x.TenantId == tenantId && x.ParentId is null && (CatalogKeyNormalizer.SameKey(x.NormalizedName, normalized) || CatalogKeyNormalizer.SameKey(x.NormalizedName, legacyNormalized)))
            ?? await db.Categories.Where(x => x.TenantId == tenantId && x.ParentId == null && categoryKeys.Contains(x.NormalizedName)).OrderBy(x => x.NormalizedName == normalized ? 0 : x.NormalizedName == legacyNormalized ? 1 : 2).ThenBy(x => x.Id).FirstOrDefaultAsync(cancellationToken);
        if (category is null)
        {
            var now = timeProvider.GetUtcNow();
            category = new Category { Id = Guid.CreateVersion7(), TenantId = tenantId, Name = leafName, NormalizedName = normalized, Path = leafName, Depth = 0, IsLeaf = true, IsActive = true, CreatedAt = now, UpdatedAt = now, Version = 1 };
            db.Categories.Add(category);
        }
        else if (db.Entry(category).State != EntityState.Added)
        {
            // K13: an existing panel category keeps its name, path and active state; only its leaf flag
            // follows its children (a category the user split is no longer a leaf).
            var isLeaf = !await db.Categories.AnyAsync(x => x.TenantId == tenantId && x.ParentId == category.Id && x.IsActive, cancellationToken);
            if (category.IsLeaf != isLeaf)
            {
                category.IsLeaf = isLeaf;
                category.UpdatedAt = timeProvider.GetUtcNow();
                category.Version++;
            }
        }

        if (mapping is null)
        {
            mapping = new CategoryMapping { Id = Guid.CreateVersion7(), TenantId = tenantId, ConnectionId = connectionId, SnapshotId = categorySnapshot.Id, LocalId = category.Id, ScopeExternalId = categorySnapshot.ScopeExternalId, ExternalId = remoteCategory.ExternalId, Status = "VERIFIED", VerifiedAt = timeProvider.GetUtcNow(), Version = 1 };
            db.CategoryMappings.Add(mapping);
        }
        else
        {
            mapping.SnapshotId = categorySnapshot.Id;
            mapping.Status = "VERIFIED";
            mapping.VerifiedAt = timeProvider.GetUtcNow();
            mapping.Version++;
        }
        return category;
    }

    private async Task<LocalCategoryAttribute> EnsureMappedAttribute(
        Guid tenantId,
        Guid connectionId,
        Category category,
        ReferenceItem remoteCategory,
        ReferenceSnapshot attributeSnapshot,
        ReferenceItem remoteAttribute,
        IReadOnlyList<ReferenceItem> values,
        IDictionary<string, AttributeDefinition> importedAttributeLibrary,
        CancellationToken cancellationToken,
        string codePrefix = "TRD")
    {
        var optionAxis = VariantOptionAxis(remoteAttribute.Name);
        var canonicalName = IsWebColorOptionKey(remoteAttribute.Name) ? "Renk" : optionAxis switch
        {
            "COLOR" => "Renk",
            "SIZE" => "Beden",
            _ => Short(remoteAttribute.Name, 160)
        };
        var code = Short($"{codePrefix}_{remoteCategory.ExternalId}_{remoteAttribute.ExternalId}", 96);
        var dataType = values.Count > 0
            ? (remoteAttribute.AllowsMultipleValues == true ? AttributeDataType.MultiSelect : AttributeDataType.SingleSelect)
            : AttributeDataType.Text;
        var attributeKey = NormalizeCatalogKey(canonicalName, 160);
        var attribute = importedAttributeLibrary.TryGetValue(attributeKey, out var sharedAttribute)
            ? sharedAttribute
            : db.AttributeDefinitions.Local.FirstOrDefault(x => x.TenantId == tenantId && x.IsActive && NormalizeCatalogKey(x.Name, 160) == attributeKey)
                ?? (await db.AttributeDefinitions
                    .Where(x => x.TenantId == tenantId && x.IsActive)
                    .OrderBy(x => x.CreatedAt)
                    .ThenBy(x => x.Id)
                    .ToListAsync(cancellationToken))
                    .FirstOrDefault(x => NormalizeCatalogKey(x.Name, 160) == attributeKey)
                ?? db.AttributeDefinitions.Local.FirstOrDefault(x => x.TenantId == tenantId && x.Code == code)
                ?? await db.AttributeDefinitions.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Code == code, cancellationToken);
        if (values.Count == 0 && attribute is not null && (attribute.DataType is AttributeDataType.SingleSelect or AttributeDataType.MultiSelect))
            dataType = attribute.DataType;
        if (attribute is null && optionAxis is not null)
        {
            var existingAttributes = await db.AttributeDefinitions
                .Where(x => x.TenantId == tenantId && x.IsActive)
                .OrderBy(x => x.CreatedAt)
                .ToListAsync(cancellationToken);
            attribute = db.AttributeDefinitions.Local.FirstOrDefault(x => x.TenantId == tenantId && x.IsActive && VariantOptionAxis(x.Name) == optionAxis)
                ?? existingAttributes.FirstOrDefault(x => VariantOptionAxis(x.Name) == optionAxis);
        }
        if (attribute is null)
        {
            attribute = new AttributeDefinition { Id = Guid.CreateVersion7(), TenantId = tenantId, Code = code, Name = canonicalName, DataType = dataType, SelectionMode = dataType == AttributeDataType.MultiSelect ? "MULTI" : dataType == AttributeDataType.SingleSelect ? "SINGLE" : null, IsActive = true, CreatedAt = timeProvider.GetUtcNow(), UpdatedAt = timeProvider.GetUtcNow(), Version = 1 };
            db.AttributeDefinitions.Add(attribute);
        }
        else
        {
            attribute.Name = canonicalName;
            attribute.DataType = dataType;
            attribute.SelectionMode = dataType == AttributeDataType.MultiSelect ? "MULTI" : dataType == AttributeDataType.SingleSelect ? "SINGLE" : null;
            attribute.IsActive = true;
            attribute.UpdatedAt = timeProvider.GetUtcNow();
            attribute.Version++;
        }
        importedAttributeLibrary[attributeKey] = attribute;

        if (IsWebColorOptionKey(remoteAttribute.Name))
        {
            var legacyColorAttributes = await db.AttributeDefinitions
                .Where(x => x.TenantId == tenantId && x.IsActive && x.Id != attribute.Id)
                .ToListAsync(cancellationToken);
            foreach (var legacyColorAttribute in legacyColorAttributes.Where(x => IsWebColorOptionKey(x.Name)))
            {
                legacyColorAttribute.IsActive = false;
                legacyColorAttribute.UpdatedAt = timeProvider.GetUtcNow();
                legacyColorAttribute.Version++;
            }
        }

        // The same canonical option can be represented by multiple marketplace
        // fields. Reuse a requirement already added in this import context
        // before querying the database, otherwise two pending inserts can
        // violate the category/attribute uniqueness constraint on SaveChanges.
        var requirement = db.CategoryAttributeRequirements.Local.FirstOrDefault(x =>
                x.TenantId == tenantId
                && x.CategoryId == category.Id
                && x.AttributeId == attribute.Id)
            ?? await db.CategoryAttributeRequirements.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.CategoryId == category.Id && x.AttributeId == attribute.Id, cancellationToken);
        // Web Color may share the panel Renk source with the real Renk
        // option. Never let that presentation field demote an existing
        // variant-option requirement to a plain attribute.
        var role = requirement?.Role == "OPTION" || IsVariantOptionName(remoteAttribute.Name) ? "OPTION" : "ATTRIBUTE";
        if (requirement is null)
            db.CategoryAttributeRequirements.Add(new CategoryAttributeRequirement { Id = Guid.CreateVersion7(), TenantId = tenantId, CategoryId = category.Id, AttributeId = attribute.Id, IsRequired = remoteAttribute.IsRequired == true, AllowsCustomValue = remoteAttribute.AllowsCustomValue == true, IsPanelScoped = true, DisplayOrder = remoteAttribute.SortOrder ?? 0, Role = role, Version = 1 });
        else
        {
            requirement.IsRequired = remoteAttribute.IsRequired == true;
            requirement.AllowsCustomValue = remoteAttribute.AllowsCustomValue == true;
            requirement.DisplayOrder = remoteAttribute.SortOrder ?? requirement.DisplayOrder;
            requirement.Role = role;
            requirement.IsPanelScoped = true;
            requirement.Version++;
        }

        // Several marketplace fields (for example the real color slicer and
        // Web Color) can intentionally share the same panel attribute. Their
        // external field is part of the mapping identity, so both mappings
        // must be retained even when they are pending in this save batch.
        var attributeMapping = db.AttributeMappings.Local.FirstOrDefault(x =>
                x.TenantId == tenantId
                && x.ConnectionId == connectionId
                && x.LocalId == attribute.Id
                && x.ScopeExternalId == remoteCategory.ExternalId
                && x.ExternalId == remoteAttribute.ExternalId)
            ?? await db.AttributeMappings.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.LocalId == attribute.Id && x.ScopeExternalId == remoteCategory.ExternalId && x.ExternalId == remoteAttribute.ExternalId, cancellationToken);
        if (attributeMapping is null)
            db.AttributeMappings.Add(new AttributeMapping { Id = Guid.CreateVersion7(), TenantId = tenantId, ConnectionId = connectionId, SnapshotId = attributeSnapshot.Id, LocalId = attribute.Id, ScopeExternalId = remoteCategory.ExternalId, ExternalId = remoteAttribute.ExternalId, Status = "VERIFIED", VerifiedAt = timeProvider.GetUtcNow(), Version = 1 });
        else
        {
            attributeMapping.SnapshotId = attributeSnapshot.Id;
            attributeMapping.ExternalId = remoteAttribute.ExternalId;
            attributeMapping.Status = "VERIFIED";
            attributeMapping.VerifiedAt = timeProvider.GetUtcNow();
            attributeMapping.Version++;
        }

        // Web Color is a marketplace presentation field, not a second local
        // option group. Its many-to-one value mapping is maintained explicitly
        // in the mapping workspace (for example Mürdüm -> Mor), so importing
        // the remote list must not create marketplace-named values under the
        // panel's Renk attribute or overwrite those custom mappings.
        if (IsWebColorOptionKey(remoteAttribute.Name))
            return new LocalCategoryAttribute(attribute, remoteAttribute, values, role);

        if (values.Count == 0) return new LocalCategoryAttribute(attribute, remoteAttribute, values, role);
        // The attribute's stored values, its value scope's current snapshot and the scope's stored value mappings are read
        // once for the whole value list (a Trendyol attribute can list hundreds of values and the import rebuilds every
        // category context per model, live 12 Nov 2026: ~4,000 queries per model). Each value below picks from them what
        // its own queries used to return (StoredAttributeValues); nothing in this loop saves, so the rows stay as read.
        var valueScope = $"{remoteCategory.ExternalId}/{remoteAttribute.ExternalId}";
        var storedValues = await StoredAttributeValues.ReadAsync(db, tenantId, attribute.Id, values.SelectMany(remoteValue => CatalogLookupKeys(remoteValue.Name, 320)), cancellationToken);
        var valueSnapshot = await db.ReferenceSnapshots.AsNoTracking().Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ResourceType == "ATTRIBUTE_VALUES" && x.ScopeExternalId == valueScope && x.IsCurrent).OrderByDescending(x => x.FetchedAt).FirstOrDefaultAsync(cancellationToken);
        var storedMappings = valueSnapshot is null
            ? null
            : await StoredAttributeValueMappings.ReadAsync(db, tenantId, connectionId, valueScope, cancellationToken);
        foreach (var remoteValue in values)
        {
            var normalizedValue = NormalizeCatalogKey(remoteValue.Name, 320);
            var valueKeys = CatalogLookupKeys(remoteValue.Name, 320);
            var value = db.AttributeValues.Local.FirstOrDefault(x => x.TenantId == tenantId && x.AttributeId == attribute.Id && CatalogKeyNormalizer.SameKey(x.NormalizedValue, normalizedValue, 320))
                ?? storedValues.Track(storedValues.First(valueKeys, normalizedValue));
            if (value is null)
            {
                value = new AttributeValue { Id = Guid.CreateVersion7(), TenantId = tenantId, AttributeId = attribute.Id, Value = Short(remoteValue.Name, 320), NormalizedValue = normalizedValue, SortOrder = remoteValue.SortOrder ?? 0, IsActive = true, Version = 1 };
                db.AttributeValues.Add(value);
            }
            else
            {
                value.Value = Short(remoteValue.Name, 320);
                value.SortOrder = remoteValue.SortOrder ?? value.SortOrder;
                value.IsActive = true;
                value.Version++;
            }
            if (valueSnapshot is null || storedMappings is null) continue;
            var valueMapping = db.AttributeValueMappings.Local.FirstOrDefault(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.LocalId == value.Id && x.ScopeExternalId == valueScope && x.ExternalId == remoteValue.ExternalId)
                ?? storedMappings.Track(storedMappings.Exact(value.Id, remoteValue.ExternalId));
            valueMapping ??= db.AttributeValueMappings.Local.FirstOrDefault(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ScopeExternalId == valueScope && x.ExternalId == remoteValue.ExternalId)
                ?? storedMappings.Track(storedMappings.FirstByLocalId(remoteValue.ExternalId));
            if (valueMapping is null)
                db.AttributeValueMappings.Add(new AttributeValueMapping { Id = Guid.CreateVersion7(), TenantId = tenantId, ConnectionId = connectionId, SnapshotId = valueSnapshot.Id, LocalId = value.Id, ScopeExternalId = valueScope, ExternalId = remoteValue.ExternalId, Status = "VERIFIED", VerifiedAt = timeProvider.GetUtcNow(), Version = 1 });
            else
            {
                valueMapping.SnapshotId = valueSnapshot.Id;
                valueMapping.ExternalId = remoteValue.ExternalId;
                valueMapping.Status = "VERIFIED";
                valueMapping.VerifiedAt = timeProvider.GetUtcNow();
                valueMapping.Version++;
            }
        }
        return new LocalCategoryAttribute(attribute, remoteAttribute, values, role);
    }

    private async Task EnsureExactWebColorValueMappings(
        Guid tenantId,
        Guid connectionId,
        string categoryExternalId,
        AttributeDefinition localAttribute,
        ReferenceItem remoteAttribute,
        IReadOnlyList<ReferenceItem> remoteValues,
        CategoryAttributeContext categoryContext,
        CancellationToken cancellationToken)
    {
        var hasNewTrackedValue = db.AttributeValues.Local.Any(x =>
            x.TenantId == tenantId
            && x.AttributeId == localAttribute.Id
            && x.IsActive
            && !categoryContext.ExactWebColorValueIds.Contains(x.Id));
        if (categoryContext.ExactWebColorInitializedAttributeIds.Contains(localAttribute.Id) && !hasNewTrackedValue)
            return;

        var valueScope = $"{categoryExternalId}/{remoteAttribute.ExternalId}";
        var valueSnapshot = await db.ReferenceSnapshots.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ResourceType == "ATTRIBUTE_VALUES" && x.ScopeExternalId == valueScope && x.IsCurrent)
            .OrderByDescending(x => x.FetchedAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (valueSnapshot is null) return;

        var trackedLocalValues = db.AttributeValues.Local
            .Where(x => x.TenantId == tenantId && x.AttributeId == localAttribute.Id && x.IsActive)
            .ToList();
        var trackedLocalValueIds = trackedLocalValues.Select(x => x.Id).ToHashSet();
        var localValues = trackedLocalValues
            .Concat(await db.AttributeValues
                .Where(x => x.TenantId == tenantId && x.AttributeId == localAttribute.Id && x.IsActive && !trackedLocalValueIds.Contains(x.Id))
                .ToListAsync(cancellationToken))
            .ToList();
        var remoteByValue = remoteValues
            .GroupBy(x => NormalizeCatalogKey(x.Name, 320), StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.First(), StringComparer.Ordinal);

        categoryContext.ExactWebColorInitializedAttributeIds.Add(localAttribute.Id);
        // The scope's stored mappings, read once for every value below (nothing here saves, so they stay as read).
        var storedMappedValueIds = (await db.AttributeValueMappings.AsNoTracking()
                .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ScopeExternalId == valueScope)
                .Select(x => x.LocalId)
                .ToListAsync(cancellationToken))
            .ToHashSet();
        foreach (var localValue in localValues.Where(x => categoryContext.ExactWebColorValueIds.Add(x.Id)))
        {
            if (!remoteByValue.TryGetValue(localValue.NormalizedValue, out var remoteValue)) continue;

            // A saved custom mapping takes precedence over the exact-name
            // default and is intentionally never overwritten by a sync. A value
            // may have several mappings in a scope, so this is an existence check.
            if (db.AttributeValueMappings.Local.Any(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.LocalId == localValue.Id && x.ScopeExternalId == valueScope)
                || storedMappedValueIds.Contains(localValue.Id)) continue;

            db.AttributeValueMappings.Add(new AttributeValueMapping
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenantId,
                ConnectionId = connectionId,
                SnapshotId = valueSnapshot.Id,
                LocalId = localValue.Id,
                ScopeExternalId = valueScope,
                ExternalId = remoteValue.ExternalId,
                Status = "VERIFIED",
                VerifiedAt = timeProvider.GetUtcNow(),
                Version = 1
            });
        }
    }

    private async Task EnsureExactWebColorValueMappings(
        Guid tenantId,
        Guid connectionId,
        CategoryAttributeContext categoryContext,
        CancellationToken cancellationToken)
    {
        foreach (var mapped in categoryContext.Attributes.Values
            .Where(x => IsWebColorOptionKey(x.Remote.Name))
            .GroupBy(x => x.Definition.Id)
            .Select(x => x.First()))
        {
            // Exact matches are safe defaults for the special mapping. They
            // make values such as “Çok Renkli” publishable immediately while
            // leaving non-identical, user-defined mappings (for example
            // Mürdüm -> Mor) untouched.
            await EnsureExactWebColorValueMappings(tenantId, connectionId, categoryContext.ExternalCategoryId, mapped.Definition, mapped.Remote, mapped.Values, categoryContext, cancellationToken);
        }
    }

    private async Task UpsertProductAttributeAssignments(Guid tenantId, Guid connectionId, Product product, ProductVariant variant, IReadOnlyDictionary<string, string> options, CategoryAttributeContext categoryContext, CancellationToken cancellationToken)
    {
        var sortOrder = 0;
        foreach (var pair in options)
        {
            if (IsWebColorOptionKey(pair.Key))
            {
                sortOrder++;
                continue;
            }
            if (!TryGetMappedAttribute(categoryContext.Attributes, pair.Key, out var mapped))
            {
                sortOrder++;
                continue;
            }
            if (IsCatalogProductOption(mapped, pair.Key))
            {
                sortOrder++;
                continue;
            }

            var valueKey = NormalizeCatalogKey(pair.Value, 320);
            var remoteValue = mapped.Values.FirstOrDefault(x => NormalizeCatalogKey(x.Name, 320) == valueKey);
            Guid? valueId = null;
            string? textValue = null;
            decimal? numberValue = null;
            bool? booleanValue = null;
            var localValue = await ResolveMappedAttributeValue(tenantId, connectionId, categoryContext, mapped, remoteValue, pair.Value, cancellationToken);
            if (localValue is not null)
            {
                valueId = localValue.Id;
            }
            else if (mapped.Remote.AllowsCustomValue == true)
            {
                if (mapped.Definition.DataType == AttributeDataType.Number && decimal.TryParse(pair.Value, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var number)) numberValue = number;
                else if (mapped.Definition.DataType == AttributeDataType.Boolean && bool.TryParse(pair.Value, out var boolean)) booleanValue = boolean;
                else textValue = Short(pair.Value, 320);
            }
            else
            {
                await RecordIssue(tenantId, $"product-attribute-value:{product.Id}:{variant.Id}:{mapped.Definition.Id}:{valueKey}", "PRODUCT_ATTRIBUTE_VALUE_UNMAPPED", $"Pazar yeri ürün özelliği '{pair.Key}: {pair.Value}' için güncel panel değeri bulunamadı; atama yapılmadı.", cancellationToken);
                sortOrder++;
                continue;
            }
            if (valueId is null && textValue is null && numberValue is null && booleanValue is null)
            {
                await RecordIssue(tenantId, $"product-attribute-value:{product.Id}:{variant.Id}:{mapped.Definition.Id}:{valueKey}", "PRODUCT_ATTRIBUTE_VALUE_UNMAPPED", $"Pazar yeri ürün özelliği '{pair.Key}: {pair.Value}' için panel değeri eşlenemedi; atama yapılmadı.", cancellationToken);
                sortOrder++;
                continue;
            }

            var assignment = db.ProductAttributeAssignments.Local.FirstOrDefault(x => x.TenantId == tenantId && x.ProductId == product.Id && x.VariantId == variant.Id && x.AttributeId == mapped.Definition.Id)
                ?? await db.ProductAttributeAssignments.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ProductId == product.Id && x.VariantId == variant.Id && x.AttributeId == mapped.Definition.Id, cancellationToken);
            if (assignment is null)
            {
                assignment = new ProductAttributeAssignment { Id = Guid.CreateVersion7(), TenantId = tenantId, ProductId = product.Id, VariantId = variant.Id, AttributeId = mapped.Definition.Id, ValueId = valueId, TextValue = textValue, NumberValue = numberValue, BooleanValue = booleanValue, SortOrder = sortOrder, Version = 1 };
                db.ProductAttributeAssignments.Add(assignment);
            }
            else
            {
                assignment.ValueId = valueId;
                assignment.TextValue = textValue;
                assignment.NumberValue = numberValue;
                assignment.BooleanValue = booleanValue;
                assignment.SortOrder = sortOrder;
                assignment.Version++;
            }
            sortOrder++;
        }

        await UpsertDerivedWebColorAttributeAssignment(tenantId, connectionId, product, variant, options, categoryContext, cancellationToken);
    }

    private async Task UpsertDerivedWebColorAttributeAssignment(
        Guid tenantId,
        Guid connectionId,
        Product product,
        ProductVariant variant,
        IReadOnlyDictionary<string, string> options,
        CategoryAttributeContext categoryContext,
        CancellationToken cancellationToken)
    {
        var webColor = categoryContext.Attributes.Values
            .Where(item => IsWebColorOptionKey(item.Remote.Name))
            .GroupBy(item => item.Definition.Id)
            .Select(group => group.First())
            .FirstOrDefault();
        var realColor = options.FirstOrDefault(pair => IsRealColorOptionKey(pair.Key));
        if (webColor is null || string.IsNullOrWhiteSpace(realColor.Key) || string.IsNullOrWhiteSpace(realColor.Value)) return;
        if (!TryGetMappedAttribute(categoryContext.Attributes, realColor.Key, out var colorAttribute)) return;

        var remoteColorValue = colorAttribute.Values.FirstOrDefault(value => NormalizeCatalogKey(value.Name, 320) == NormalizeCatalogKey(realColor.Value, 320));
        var localColorValue = await ResolveMappedAttributeValue(tenantId, connectionId, categoryContext, colorAttribute, remoteColorValue, realColor.Value, cancellationToken);
        if (localColorValue is null)
        {
            await RecordIssue(tenantId, $"product-web-color-value:{product.Id}:{variant.Id}:{NormalizeCatalogKey(realColor.Value, 320)}", "PRODUCT_WEBCOLOR_VALUE_UNMAPPED", $"Pazar yeri Renk değeri '{realColor.Value}' için panel Renk değeri bulunamadı; Web Color atanmadı.", cancellationToken);
            return;
        }

        var webColorExternalId = await ResolveWebColorExternalIdAsync(tenantId, connectionId, categoryContext, webColor, localColorValue, cancellationToken);
        if (string.IsNullOrWhiteSpace(webColorExternalId))
        {
            await RecordIssue(tenantId, $"product-web-color-mapping:{product.Id}:{variant.Id}:{localColorValue.Id}", "PRODUCT_WEBCOLOR_MAPPING_REQUIRED", $"Panel Renk değeri '{localColorValue.Value}' için Web Color eşlemesi bulunamadı; Web Color atanmadı.", cancellationToken);
            return;
        }

        var assignment = db.ProductAttributeAssignments.Local.FirstOrDefault(x => x.TenantId == tenantId && x.ProductId == product.Id && x.VariantId == variant.Id && x.AttributeId == webColor.Definition.Id)
            ?? await db.ProductAttributeAssignments.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ProductId == product.Id && x.VariantId == variant.Id && x.AttributeId == webColor.Definition.Id, cancellationToken);
        if (assignment is null)
        {
            db.ProductAttributeAssignments.Add(new ProductAttributeAssignment
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenantId,
                ProductId = product.Id,
                VariantId = variant.Id,
                AttributeId = webColor.Definition.Id,
                ValueId = localColorValue.Id,
                SortOrder = webColor.Remote.SortOrder ?? 0,
                Version = 1
            });
        }
        else
        {
            assignment.ValueId = localColorValue.Id;
            assignment.TextValue = null;
            assignment.NumberValue = null;
            assignment.BooleanValue = null;
            assignment.SortOrder = webColor.Remote.SortOrder ?? assignment.SortOrder;
            assignment.Version++;
        }
    }

    private async Task<string?> ResolveWebColorExternalIdAsync(
        Guid tenantId,
        Guid connectionId,
        CategoryAttributeContext categoryContext,
        LocalCategoryAttribute webColor,
        AttributeValue localColorValue,
        CancellationToken cancellationToken)
    {
        var valueScope = $"{categoryContext.ExternalCategoryId}/{webColor.Remote.ExternalId}";
        // Several mappings may exist for one colour; the latest verified one that is still a valid
        // Web Color value wins (the same rule the publication composer uses).
        var mapping = db.AttributeValueMappings.Local
            .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.LocalId == localColorValue.Id && x.ScopeExternalId == valueScope && x.Status == "VERIFIED")
            .Concat(await db.AttributeValueMappings.AsNoTracking()
                .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.LocalId == localColorValue.Id && x.ScopeExternalId == valueScope && x.Status == "VERIFIED")
                .ToListAsync(cancellationToken))
            .Where(candidate => webColor.Values.Any(value => value.ExternalId == candidate.ExternalId))
            .OrderByDescending(candidate => candidate.VerifiedAt ?? DateTimeOffset.MinValue)
            .ThenByDescending(candidate => candidate.Id)
            .FirstOrDefault();
        if (mapping is not null) return mapping.ExternalId;

        var exact = webColor.Values.FirstOrDefault(value => NormalizeCatalogKey(value.Name, 320) == NormalizeCatalogKey(localColorValue.Value, 320));
        if (exact is not null) return exact.ExternalId;
        foreach (var fallbackKey in CatalogColorMappingPolicy.FallbackKeys(localColorValue.Value))
        {
            var fallback = webColor.Values.FirstOrDefault(value => NormalizeCatalogKey(value.Name, 320).Replace("-", "", StringComparison.Ordinal) == fallbackKey);
            if (fallback is not null) return fallback.ExternalId;
        }
        return null;
    }

    private async Task PromoteCommonImportedAttributes(Guid tenantId, Product product, IReadOnlyList<ProductVariant> importedVariants, CategoryAttributeContext categoryContext, CancellationToken cancellationToken)
    {
        var variantIds = importedVariants.Select(x => x.Id).Distinct().ToArray();
        if (variantIds.Length == 0) return;

        var importedAttributeIds = categoryContext.Attributes.Values
            .Where(x => x.Role != "OPTION")
            .Select(x => x.Definition.Id)
            .ToHashSet();
        if (importedAttributeIds.Count == 0) return;

        var trackedAssignments = db.ProductAttributeAssignments.Local
            .Where(x => x.TenantId == tenantId && x.ProductId == product.Id && x.VariantId.HasValue && variantIds.Contains(x.VariantId.Value))
            .ToList();
        var storedAssignments = await db.ProductAttributeAssignments
            .Where(x => x.TenantId == tenantId && x.ProductId == product.Id && x.VariantId.HasValue && variantIds.Contains(x.VariantId.Value))
            .ToListAsync(cancellationToken);
        var assignments = trackedAssignments
            .Concat(storedAssignments)
            .GroupBy(x => x.Id)
            .Select(x => x.First())
            .Where(x => importedAttributeIds.Contains(x.AttributeId))
            .ToList();

        foreach (var attributeId in importedAttributeIds)
        {
            var byVariant = assignments
                .Where(x => x.AttributeId == attributeId && x.VariantId is not null)
                .GroupBy(x => x.VariantId!.Value)
                .ToDictionary(x => x.Key, x => x.ToList());
            var common = byVariant.Count == variantIds.Length && byVariant.Values.All(x => x.Count == 1)
                ? byVariant.Values.Select(x => x[0]).ToList()
                : [];
            var first = common.FirstOrDefault();
            var isCommon = first is not null && common.Skip(1).All(x => SameImportedAttributeValue(first, x));
            var global = db.ProductAttributeAssignments.Local.FirstOrDefault(x => x.TenantId == tenantId && x.ProductId == product.Id && x.VariantId == null && x.AttributeId == attributeId)
                ?? await db.ProductAttributeAssignments.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ProductId == product.Id && x.VariantId == null && x.AttributeId == attributeId, cancellationToken);

            if (!isCommon)
            {
                if (global is not null) db.ProductAttributeAssignments.Remove(global);
                continue;
            }

            // One panel attribute can intentionally serve multiple marketplace fields
            // (for example, the product color and its Web Color presentation field).
            var mapped = categoryContext.Attributes.Values.First(x => x.Definition.Id == attributeId);
            if (global is null)
            {
                global = new ProductAttributeAssignment
                {
                    Id = Guid.CreateVersion7(),
                    TenantId = tenantId,
                    ProductId = product.Id,
                    VariantId = null,
                    AttributeId = attributeId,
                    ValueId = first!.ValueId,
                    TextValue = first.TextValue,
                    NumberValue = first.NumberValue,
                    BooleanValue = first.BooleanValue,
                    SortOrder = mapped.Remote.SortOrder ?? first.SortOrder,
                    Version = 1
                };
                db.ProductAttributeAssignments.Add(global);
            }
            else
            {
                global.ValueId = first!.ValueId;
                global.TextValue = first.TextValue;
                global.NumberValue = first.NumberValue;
                global.BooleanValue = first.BooleanValue;
                global.SortOrder = mapped.Remote.SortOrder ?? first.SortOrder;
                global.Version++;
            }

            db.ProductAttributeAssignments.RemoveRange(common);
        }
    }

    private static bool SameImportedAttributeValue(ProductAttributeAssignment left, ProductAttributeAssignment right) =>
        left.ValueId == right.ValueId
        && string.Equals(left.TextValue, right.TextValue, StringComparison.Ordinal)
        && left.NumberValue == right.NumberValue
        && left.BooleanValue == right.BooleanValue;

    private async Task<AttributeValue?> ResolveMappedAttributeValue(
        Guid tenantId,
        Guid connectionId,
        CategoryAttributeContext categoryContext,
        LocalCategoryAttribute mapped,
        ReferenceItem? remoteValue,
        string remoteValueText,
        CancellationToken cancellationToken)
    {
        var valueScope = $"{categoryContext.ExternalCategoryId}/{mapped.Remote.ExternalId}";
        if (remoteValue is not null)
        {
            // One Trendyol value may be mapped to several panel values. The import picks one
            // deterministically: the panel value named like the Trendyol value, otherwise the most
            // recently verified mapping (before, an unordered query picked any of them).
            var mappings = db.AttributeValueMappings.Local
                .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ScopeExternalId == valueScope && x.ExternalId == remoteValue.ExternalId && x.Status == "VERIFIED")
                .Concat(await db.AttributeValueMappings.AsNoTracking()
                    .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ScopeExternalId == valueScope && x.ExternalId == remoteValue.ExternalId && x.Status == "VERIFIED")
                    .ToListAsync(cancellationToken))
                .GroupBy(mapping => mapping.LocalId)
                .Select(group => group.OrderByDescending(mapping => mapping.VerifiedAt ?? DateTimeOffset.MinValue).ThenByDescending(mapping => mapping.Id).First())
                .ToList();
            if (mappings.Count > 0)
            {
                var localIds = mappings.Select(mapping => mapping.LocalId).ToArray();
                var mappedValues = db.AttributeValues.Local
                    .Where(x => x.TenantId == tenantId && localIds.Contains(x.Id) && x.AttributeId == mapped.Definition.Id && x.IsActive)
                    .ToList();
                var trackedIds = mappedValues.Select(x => x.Id).ToHashSet();
                mappedValues.AddRange(await db.AttributeValues.AsNoTracking()
                    .Where(x => x.TenantId == tenantId && localIds.Contains(x.Id) && !trackedIds.Contains(x.Id) && x.AttributeId == mapped.Definition.Id && x.IsActive)
                    .ToListAsync(cancellationToken));
                var remoteKey = NormalizeCatalogKey(remoteValue.Name, 320);
                var chosen = mappedValues.FirstOrDefault(value => CatalogKeyNormalizer.SameKey(value.NormalizedValue, remoteKey, 320))
                    ?? mappings
                        .OrderByDescending(mapping => mapping.VerifiedAt ?? DateTimeOffset.MinValue)
                        .ThenByDescending(mapping => mapping.Id)
                        .Select(mapping => mappedValues.FirstOrDefault(value => value.Id == mapping.LocalId))
                        .FirstOrDefault(value => value is not null);
                if (chosen is not null) return chosen;
            }
        }

        var normalized = NormalizeCatalogKey(remoteValueText, 320);
        if (IsWebColorOptionKey(mapped.Remote.Name))
        {
            var localValues = await db.AttributeValues.AsNoTracking()
                .Where(x => x.TenantId == tenantId && x.AttributeId == mapped.Definition.Id && x.IsActive)
                .ToListAsync(cancellationToken);
            foreach (var fallbackKey in CatalogColorMappingPolicy.FallbackKeys(remoteValueText))
            {
                var fallback = localValues.FirstOrDefault(value => NormalizeCatalogKey(value.Value, 320).Replace("-", "", StringComparison.Ordinal) == fallbackKey);
                if (fallback is not null) return fallback;
            }
        }
        var normalizedKeys = CatalogLookupKeys(remoteValueText, 320);
        return db.AttributeValues.Local.FirstOrDefault(x => x.TenantId == tenantId && x.AttributeId == mapped.Definition.Id && x.IsActive && CatalogKeyNormalizer.SameKey(x.NormalizedValue, normalized, 320))
            ?? await db.AttributeValues.AsNoTracking().Where(x => x.TenantId == tenantId && x.AttributeId == mapped.Definition.Id && x.IsActive && normalizedKeys.Contains(x.NormalizedValue)).OrderBy(x => x.NormalizedValue == normalized ? 0 : 1).ThenBy(x => x.Id).FirstOrDefaultAsync(cancellationToken);
    }

    // crossMatchCodes (n11): the seller stock code may be the panel barcode
    // and the n11 barcode the panel SKU. Model-code candidates are narrowed
    // by colour and size on every marketplace (Y5). reportUnmatched records every remote
    // variant that found no local variant so it can be fixed by hand. seedOffers (n11)
    // opens the channel offer of a linked variant from the remote price when
    // the variant has none yet; existing offers are never changed.
    private async Task<bool> MapExistingCatalogProduct(Guid tenantId, Guid connectionId, RemoteCatalogProduct snapshot, bool preferBarcode, CancellationToken cancellationToken, bool crossMatchCodes = false, bool reportUnmatched = false, bool seedOffers = false, ConnectionInventoryPolicy? inventoryPolicy = null, string seedSource = "N11", bool pttAvm = false)
    {
        if (pttAvm) return await MapPttAvmProduct(tenantId, connectionId, snapshot, reportUnmatched, seedOffers, inventoryPolicy, cancellationToken);
        var externalProductId = Short(snapshot.ExternalProductId, 256);
        if (string.IsNullOrWhiteSpace(externalProductId)) return false;
        var remoteVariants = snapshot.Variants
            .Where(variant => !string.IsNullOrWhiteSpace(variant.ExternalVariantId))
            .ToList();
        if (remoteVariants.Count == 0) return false;
        // The run's result line counts variants: Eşlenen (a link made now), Zaten eşli, Panelde karşılığı yok, Çakışma.
        bool NoCounterpart() { telemetryNoCounterpartCount += remoteVariants.Count; return false; }
        bool Conflict() { telemetryMappingConflictCount += remoteVariants.Count; return false; }

        var now = timeProvider.GetUtcNow();
        var link = await db.MarketplaceProductLinks.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ExternalId == externalProductId, cancellationToken);
        var modelCodes = !preferBarcode
            ? remoteVariants.Select(variant => variant.ModelCode?.Trim()).Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value!).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
            : Array.Empty<string>();
        // Both stored key forms (shared key and legacy panel form) are looked up until the recompute.
        var barcodes = CatalogKeyNormalizer.LookupKeys(remoteVariants.Select(variant => variant.Barcode));
        var skus = CatalogKeyNormalizer.LookupKeys(remoteVariants.Select(variant => variant.Sku));
        var rawSkus = remoteVariants.Select(variant => variant.Sku?.Trim()).Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value!).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (modelCodes.Length == 0 && barcodes.Length == 0 && skus.Length == 0) return NoCounterpart();

        // A Shopify product can contain several colour variants while the
        // panel stores each colour as its own Product. Load every candidate
        // variant first; requiring one ProductId here incorrectly rejected
        // valid multi-product matches.
        var localVariants = await db.ProductVariants.AsNoTracking()
            .Where(variant => variant.TenantId == tenantId
                && ((variant.BarcodeNormalized != null && barcodes.Contains(variant.BarcodeNormalized))
                    || (variant.SkuNormalized != null && skus.Contains(variant.SkuNormalized))
                    || (crossMatchCodes && variant.BarcodeNormalized != null && skus.Contains(variant.BarcodeNormalized))
                    || (crossMatchCodes && variant.SkuNormalized != null && barcodes.Contains(variant.SkuNormalized))
                    || (preferBarcode && variant.ModelCode != null && rawSkus.Contains(variant.ModelCode))
                    || (!preferBarcode && variant.ModelCode != null && modelCodes.Contains(variant.ModelCode))))
            .ToListAsync(cancellationToken);
        if (localVariants.Count == 0 && link is null)
        {
            if (reportUnmatched)
                foreach (var remote in remoteVariants)
                    await RecordIssue(tenantId, $"product-mapping-unmatched:{connectionId}:{Short(remote.ExternalVariantId, 256)}", "PRODUCT_MAPPING_UNMATCHED",
                        Short($"Pazar yeri varyantı eşleşmedi: stok kodu {remote.Sku}, barkod {remote.Barcode ?? "yok"}, model kodu {remote.ModelCode ?? "yok"} · {snapshot.Title}. Panelde aynı stok kodu, barkod veya model kodu yok.", 1000), cancellationToken);
            return NoCounterpart();
        }

        var localVariantIds = localVariants.Select(variant => variant.Id).ToArray();
        var externalVariantIds = remoteVariants.Select(variant => Short(variant.ExternalVariantId, 256)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var existingVariantLinks = await db.MarketplaceVariantLinks
            .Where(existing => existing.TenantId == tenantId && existing.ConnectionId == connectionId && (localVariantIds.Contains(existing.VariantId) || externalVariantIds.Contains(existing.ExternalId)))
            .ToListAsync(cancellationToken);
        var claimedVariantIds = existingVariantLinks
            .Where(existing => !externalVariantIds.Contains(existing.ExternalId, StringComparer.OrdinalIgnoreCase))
            .Select(existing => existing.VariantId)
            .ToHashSet();
        var matchedVariantCount = 0;

        // Each remote variant is resolved before anything is linked: a barcode or stock code names one panel variant,
        // a model code names every colour and size of the model, so a model-code candidate must also carry the remote
        // variant's colour and size (Y5, on every marketplace; Trendyol and Hepsiburada bound by the model code alone).
        var resolved = remoteVariants.Select(remote =>
        {
            var remoteBarcode = NormalizeCatalogKey(remote.Barcode, 160);
            var remoteSku = NormalizeCatalogKey(remote.Sku, 160);
            var remoteModelCode = NormalizeCatalogKey(remote.ModelCode, 160);
            var barcodeMatches = string.IsNullOrWhiteSpace(remoteBarcode)
                ? []
                : localVariants.Where(variant => CatalogKeyNormalizer.SameKey(variant.BarcodeNormalized, remoteBarcode)
                    || crossMatchCodes && CatalogKeyNormalizer.SameKey(variant.SkuNormalized, remoteBarcode)).ToList();
            var skuMatches = string.IsNullOrWhiteSpace(remoteSku)
                ? []
                : localVariants.Where(variant => CatalogKeyNormalizer.SameKey(variant.SkuNormalized, remoteSku)
                    || crossMatchCodes && CatalogKeyNormalizer.SameKey(variant.BarcodeNormalized, remoteSku)
                    || preferBarcode && string.Equals(NormalizeCatalogKey(variant.ModelCode, 160), remoteSku, StringComparison.Ordinal)).ToList();
            var modelCandidates = preferBarcode || string.IsNullOrWhiteSpace(remoteModelCode)
                ? []
                : localVariants.Where(variant => string.Equals(NormalizeCatalogKey(variant.ModelCode, 160), remoteModelCode, StringComparison.Ordinal)).ToList();
            var modelMatches = NarrowModelCodeCandidates(modelCandidates, variant => variant.OptionSignature, remote.Options, snapshot.Title);
            var byModelCode = barcodeMatches.Count == 0 && skuMatches.Count == 0;
            var matches = !byModelCode ? barcodeMatches.Count > 0 ? barcodeMatches : skuMatches : modelMatches;
            return new
            {
                Remote = remote,
                ExternalVariantId = Short(remote.ExternalVariantId, 256),
                Matches = matches,
                Candidate = matches.Count == 1 ? matches[0] : null,
                // Model-code candidates whose colour and size could not single one out: never picked, left for review.
                ModelCodeUndecided = byModelCode && modelCandidates.Count > 0 && matches.Count != 1,
                ModelCandidateCount = modelCandidates.Count
            };
        }).ToList();

        // The panel product follows the variants the remote ones resolved to, never the first candidate a query
        // returned. Variants that land on several panel products (a model kept as one card per colour) decide none:
        // the product link is not made and the product is put up for review.
        // Shopify (preferBarcode) keeps a model as one product with every colour while the panel keeps one card per
        // colour: that is its normal shape, not a doubt. Its variants are paired by barcode or stock code, one panel
        // variant each, so they are linked one by one without a product link and nothing is put up for review
        // (owner, 14 Nov 2026: MZ035YNC, 8 cards x 3 sizes in one Shopify product, was never linked).
        var linkProduct = true;
        Guid? productId = link?.ProductId;
        if (productId is null)
        {
            var resolvedProductIds = resolved
                .Where(item => item.Candidate is not null && !claimedVariantIds.Contains(item.Candidate.Id))
                .Select(item => item.Candidate!.ProductId)
                .Distinct()
                .ToList();
            // Only the colour cards of one model (one linked model, or one brand and model code) make that normal shape;
            // cards of different models fall to the conflict handling below (audit 14 Nov 2026).
            if (resolvedProductIds.Count > 1 && preferBarcode && await ProductModelLinks.OneModelAsync(db, tenantId, resolvedProductIds, cancellationToken))
            {
                await ResolveIssue(tenantId, $"product-mapping-unmatched:{connectionId}:{externalProductId}", cancellationToken);
                linkProduct = false;
            }
            else if (resolvedProductIds.Count > 1)
            {
                await RecordIssue(tenantId, $"product-mapping-unmatched:{connectionId}:{externalProductId}", "PRODUCT_MAPPING_UNMATCHED",
                    Short($"Pazar yeri ürününün varyantları panelde {resolvedProductIds.Count} ayrı ürüne (renk kartlarına) düştü; ürün bağlantısı seçilmedi, eşleşmeyi kontrol edin · {snapshot.Title}.", 1000), cancellationToken);
                if (!crossMatchCodes) return Conflict();
                linkProduct = false;
            }
            else if (resolvedProductIds.Count == 1)
            {
                productId = resolvedProductIds[0];
                if (await db.MarketplaceProductLinks.AsNoTracking().AnyAsync(existing => existing.TenantId == tenantId && existing.ConnectionId == connectionId && existing.ProductId == productId.Value && existing.ExternalId != externalProductId, cancellationToken))
                {
                    if (!crossMatchCodes) return Conflict();
                    linkProduct = false;
                }
            }
            else linkProduct = false;
        }
        if (productId is { } chosenProductId && !await db.Products.AsNoTracking().AnyAsync(product => product.TenantId == tenantId && product.Id == chosenProductId, cancellationToken)) return NoCounterpart();

        foreach (var item in resolved)
        {
            var remote = item.Remote;
            var externalVariantId = item.ExternalVariantId;
            var matches = item.Matches;
            var candidate = item.Candidate;
            if (candidate is null || claimedVariantIds.Contains(candidate.Id))
            {
                if (candidate is not null || matches.Count > 1 || item.ModelCodeUndecided) telemetryMappingConflictCount++;
                else telemetryNoCounterpartCount++;
                if (reportUnmatched || item.ModelCodeUndecided)
                    await RecordIssue(tenantId, $"product-mapping-unmatched:{connectionId}:{externalVariantId}", "PRODUCT_MAPPING_UNMATCHED",
                        Short($"Pazar yeri varyantı eşleşmedi: stok kodu {remote.Sku}, barkod {remote.Barcode ?? "yok"}, model kodu {remote.ModelCode ?? "yok"}, {string.Join(", ", remote.Options.Where(option => IsRealColorOptionKey(option.Key) || IsSizeOptionKey(option.Key)).Select(option => $"{option.Key}: {option.Value}"))} · {snapshot.Title}. {(candidate is not null ? "Aday panel varyantı başka bir pazar yeri varyantına bağlı." : matches.Count > 1 ? $"{matches.Count} panel varyantı aday çıktı." : item.ModelCodeUndecided ? $"Model kodu {item.ModelCandidateCount} panel varyantında var ama hiçbirinin rengi ve bedeni bu varyantınki değil." : "Panelde aynı stok kodu, barkod veya model kodu yok.")}", 1000),
                        cancellationToken);
                continue;
            }

            var externalLink = existingVariantLinks.FirstOrDefault(existing => string.Equals(existing.ExternalId, externalVariantId, StringComparison.OrdinalIgnoreCase));
            if (externalLink is not null)
            {
                if (externalLink.VariantId == candidate.Id) { matchedVariantCount++; telemetryAlreadyMappedCount++; if (seedOffers) await SeedChannelOfferAsync(tenantId, connectionId, candidate.Id, remote, inventoryPolicy, now, cancellationToken, seedSource); }
                else telemetryMappingConflictCount++;
                if (externalLink.VariantId != candidate.Id && reportUnmatched)
                    await RecordIssue(tenantId, $"product-mapping-unmatched:{connectionId}:{externalVariantId}", "PRODUCT_MAPPING_UNMATCHED",
                        Short($"Pazar yeri varyantı {remote.Sku} daha önce başka bir panel varyantına bağlanmış; şimdiki eşleşme {candidate.Sku}. Bağlantı değiştirilmedi · {snapshot.Title}.", 1000), cancellationToken);
                claimedVariantIds.Add(externalLink.VariantId);
                continue;
            }
            var localLink = existingVariantLinks.FirstOrDefault(existing => existing.VariantId == candidate.Id);
            if (localLink is not null)
            {
                telemetryMappingConflictCount++;
                if (reportUnmatched)
                    await RecordIssue(tenantId, $"product-mapping-unmatched:{connectionId}:{externalVariantId}", "PRODUCT_MAPPING_UNMATCHED",
                        Short($"Pazar yeri varyantı eşleşmedi: stok kodu {remote.Sku}, panel varyantı {candidate.Sku} zaten {localLink.ExternalId} stok koduna bağlı · {snapshot.Title}.", 1000), cancellationToken);
                continue;
            }

            var mappedLink = new MarketplaceVariantLink { Id = Guid.CreateVersion7(), TenantId = tenantId, ConnectionId = connectionId, VariantId = candidate.Id, ExternalId = externalVariantId, Version = 1 };
            ListingSaleStateReads.ApplyToNewLink(mappedLink, remote.SaleState, remote.SaleStateReason, now);
            db.MarketplaceVariantLinks.Add(mappedLink);
            existingVariantLinks.Add(new MarketplaceVariantLink { TenantId = tenantId, ConnectionId = connectionId, VariantId = candidate.Id, ExternalId = externalVariantId, Version = 1 });
            claimedVariantIds.Add(candidate.Id);
            matchedVariantCount++;
            telemetryMappedCount++;
            telemetryInsertedCount++;
            if (seedOffers) await SeedChannelOfferAsync(tenantId, connectionId, candidate.Id, remote, inventoryPolicy, now, cancellationToken, seedSource);
        }

        if (matchedVariantCount == 0) return false;
        if (!linkProduct || productId is null) return true;
        if (link is null)
        {
            link = new MarketplaceProductLink
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenantId,
                ConnectionId = connectionId,
                ProductId = productId.Value,
                ExternalId = externalProductId,
                SyncStatus = "MAPPED",
                LastImportedAt = now,
                Version = 1
            };
            db.MarketplaceProductLinks.Add(link);
            telemetryInsertedCount++;
        }
        // A product link that already exists stays as it is (owner, 12 Nov 2026: mapping only links): until then it was
        // re-marked MAPPED, which wiped its sync state (SYNCED, LOCAL_CHANGES_PENDING) and its import time.

        return true;
    }

    // PTT AVM keeps a product by its barcode, which is also its seller stock code: the model code for a product with
    // variants, the product's own code for one without. Its variants are colour and size combinations, each with a
    // variant stock code equal to the panel variant's stock code; the SOAP service lists only the colour and size
    // values of a product ("FK4530/7101" = "Lacivert"), never the combinations or their codes. So the product is
    // found by its barcode (a panel model code, else a panel variant's code), every active panel variant of that
    // model is linked by its own stock code, and a variant whose colour or size PTT AVM does not list is left out.
    private async Task<bool> MapPttAvmProduct(Guid tenantId, Guid connectionId, RemoteCatalogProduct snapshot, bool reportUnmatched, bool seedOffers, ConnectionInventoryPolicy? inventoryPolicy, CancellationToken cancellationToken)
    {
        var externalProductId = Short(snapshot.ExternalProductId, 256);
        var productCode = snapshot.ProductMainId?.Trim();
        var codeKey = NormalizeCatalogKey(productCode, 160);
        if (string.IsNullOrWhiteSpace(externalProductId) || codeKey.Length == 0) return false;
        var remoteVariants = snapshot.Variants.Where(variant => !string.IsNullOrWhiteSpace(variant.ExternalVariantId)).ToList();
        if (remoteVariants.Count == 0) return false;
        var now = timeProvider.GetUtcNow();
        var lookupKeys = CatalogKeyNormalizer.LookupKeys(productCode);

        // A product with variants: the panel model whose code is the product barcode; else the model of the panel
        // variant whose own code it is (a product kept on PTT AVM under one variant's code).
        var byModel = await db.ProductVariants.AsNoTracking()
            .Where(variant => variant.TenantId == tenantId && variant.Status != ProductStatus.Archived && variant.ModelCode != null && variant.ModelCode == productCode)
            .ToListAsync(cancellationToken);
        var byCode = await db.ProductVariants.AsNoTracking()
            .Where(variant => variant.TenantId == tenantId && variant.Status != ProductStatus.Archived
                && ((variant.SkuNormalized != null && lookupKeys.Contains(variant.SkuNormalized)) || (variant.BarcodeNormalized != null && lookupKeys.Contains(variant.BarcodeNormalized))))
            .ToListAsync(cancellationToken);
        var optionValues = PttAvmOptionValues(remoteVariants);
        List<ProductVariant> modelVariants;
        var compliant = true;
        if (byModel.Count > 0)
        {
            modelVariants = byModel;
        }
        else if (byCode.Count == 1 && (optionValues.Count == 0 || string.IsNullOrWhiteSpace(byCode[0].ModelCode)))
        {
            // A product without variants: its barcode is the panel variant's own code.
            modelVariants = byCode;
        }
        else if (byCode.Count == 1)
        {
            var modelCode = byCode[0].ModelCode;
            modelVariants = await db.ProductVariants.AsNoTracking()
                .Where(variant => variant.TenantId == tenantId && variant.Status != ProductStatus.Archived && variant.ModelCode == modelCode)
                .ToListAsync(cancellationToken);
            compliant = false;
        }
        else
        {
            if (reportUnmatched)
                await RecordIssue(tenantId, $"product-mapping-unmatched:{connectionId}:ptt:{externalProductId}", "PRODUCT_MAPPING_UNMATCHED",
                    Short(byCode.Count > 1
                        ? $"PttAVM ürün kodu {productCode} panelde {byCode.Count} varyantın kodu; hangisi olduğu seçilemedi · {snapshot.Title}."
                        : $"PttAVM ürün kodu {productCode} panelde yok: aynı model kodu veya stok kodu bulunamadı · {snapshot.Title}.", 1000), cancellationToken);
            return false;
        }

        // The panel may keep one model as several products (one per colour, as Trendyol lists it): every variant is
        // linked, and the PTT AVM product links to the product of the model's first variant; the others reach the
        // product's barcode through the model code when price and stock are sent.
        var productId = modelVariants.OrderBy(variant => variant.Sku, StringComparer.Ordinal).ThenBy(variant => variant.Id).First().ProductId;
        if (!compliant && reportUnmatched)
            await RecordIssue(tenantId, $"pttavm-product-code:{connectionId}:{externalProductId}", "PTTAVM_PRODUCT_CODE_NOT_MODEL",
                Short($"PttAVM'de varyantlı ürünün barkodu/satıcı stok kodu {productCode}, panel model kodu {modelVariants[0].ModelCode}. Kurala göre PttAVM'de model koduna çevirin · {snapshot.Title}.", 1000), cancellationToken);

        var variantIds = modelVariants.Select(variant => variant.Id).ToArray();
        var codes = modelVariants.Select(PttAvmVariantCode).Where(code => code is not null).Select(code => code!).ToArray();
        var existingVariantLinks = await db.MarketplaceVariantLinks
            .Where(existing => existing.TenantId == tenantId && existing.ConnectionId == connectionId && (variantIds.Contains(existing.VariantId) || codes.Contains(existing.ExternalId)))
            .ToListAsync(cancellationToken);
        var matched = 0;
        foreach (var variant in modelVariants.OrderBy(variant => variant.SortOrder).ThenBy(variant => variant.Id))
        {
            var code = PttAvmVariantCode(variant);
            var own = existingVariantLinks.FirstOrDefault(existing => existing.VariantId == variant.Id);
            var missing = code is null ? [] : PttAvmMissingOptionValues(variant.OptionSignature, optionValues);
            if (code is null || missing.Count > 0)
            {
                // A stale link (an old "MODEL/123" option id) of a variant PTT AVM does not sell is removed, so price and
                // stock are never sent for it.
                if (own is not null && PttAvmVariantMapper.IsModelVariantId(own.ExternalId)) db.MarketplaceVariantLinks.Remove(own);
                if (reportUnmatched)
                    await RecordIssue(tenantId, $"product-mapping-unmatched:{connectionId}:{variant.Id:N}", "PRODUCT_MAPPING_UNMATCHED",
                        Short(code is null
                            ? $"PttAVM varyantı bağlanmadı: panel varyantının stok kodu ve barkodu yok · {snapshot.Title}."
                            : $"PttAVM'de bu renk/beden yok: {string.Join(", ", missing)} ({code}); ürün {productCode} · {snapshot.Title}.", 1000), cancellationToken);
                continue;
            }
            var taken = existingVariantLinks.FirstOrDefault(existing => existing.VariantId != variant.Id && string.Equals(existing.ExternalId, code, StringComparison.OrdinalIgnoreCase));
            if (taken is not null)
            {
                if (reportUnmatched)
                    await RecordIssue(tenantId, $"product-mapping-unmatched:{connectionId}:{variant.Id:N}", "PRODUCT_MAPPING_UNMATCHED",
                        Short($"PttAVM kodu {code} başka bir panel varyantına bağlı; {variant.Sku} bağlanmadı · {snapshot.Title}.", 1000), cancellationToken);
                continue;
            }
            if (own is null)
            {
                own = new MarketplaceVariantLink { Id = Guid.CreateVersion7(), TenantId = tenantId, ConnectionId = connectionId, VariantId = variant.Id, ExternalId = code, ExternalBarcode = variant.Barcode?.Trim(), ExternalSku = variant.Sku.Trim(), Version = 1 };
                // The model's sale state, as MarkPttAvmModelLinksSeen reads it for a link that already existed.
                var (modelSaleState, modelSaleReason) = PttAvmModelSaleState(snapshot);
                ListingSaleStateReads.ApplyToNewLink(own, modelSaleState, modelSaleReason, timeProvider.GetUtcNow());
                db.MarketplaceVariantLinks.Add(own);
                existingVariantLinks.Add(own);
                telemetryInsertedCount++;
            }
            else if (own.ExternalId != code || own.ExternalBarcode != variant.Barcode?.Trim() || own.ExternalSku != variant.Sku.Trim())
            {
                own.ExternalId = code;
                own.ExternalBarcode = variant.Barcode?.Trim();
                own.ExternalSku = variant.Sku.Trim();
                own.Version++;
            }
            // PTT AVM listed this product with the variant's colour/size: its code holds there.
            MarketplaceVariantLinkHealth.MarkSeen(own, now);
            matched++;
            if (seedOffers)
            {
                var seed = remoteVariants.FirstOrDefault(remote => HasOptionValues(variant.OptionSignature, remote.Options)) ?? remoteVariants[0];
                // A colour or size row's stock is not the variant's, so it never marks the panel stock as already sent.
                if (PttAvmVariantMapper.IsModelVariantId(seed.ExternalVariantId)) seed = seed with { StockQuantity = null };
                await SeedChannelOfferAsync(tenantId, connectionId, variant.Id, seed, inventoryPolicy, now, cancellationToken, "PTTAVM");
            }
        }
        if (matched == 0) return false;

        var link = await db.MarketplaceProductLinks.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ExternalId == externalProductId, cancellationToken);
        if (link is null)
        {
            if (await db.MarketplaceProductLinks.AsNoTracking().AnyAsync(existing => existing.TenantId == tenantId && existing.ConnectionId == connectionId && existing.ProductId == productId, cancellationToken))
            {
                if (reportUnmatched)
                    await RecordIssue(tenantId, $"pttavm-product-link:{connectionId}:{externalProductId}", "PRODUCT_MAPPING_UNMATCHED",
                        Short($"Panel ürünü başka bir PttAVM ürününe bağlı; {productCode} varyantları bağlandı, ürün bağı kurulmadı · {snapshot.Title}.", 1000), cancellationToken);
                return true;
            }
            db.MarketplaceProductLinks.Add(new MarketplaceProductLink { Id = Guid.CreateVersion7(), TenantId = tenantId, ConnectionId = connectionId, ProductId = productId, ExternalId = externalProductId, ExternalCode = productCode, SyncStatus = "MAPPED", LastImportedAt = now, Version = 1 });
            telemetryInsertedCount++;
        }
        else
        {
            link.ExternalCode = productCode;
            link.LastImportedAt = now;
            link.SyncStatus = "MAPPED";
            link.Version++;
        }
        return true;
    }

    // The code PTT AVM keeps a panel variant by: its stock code (PTT AVM's variant stock code), else its barcode.
    private static string? PttAvmVariantCode(ProductVariant variant) =>
        !string.IsNullOrWhiteSpace(variant.Sku) ? variant.Sku.Trim() : string.IsNullOrWhiteSpace(variant.Barcode) ? null : variant.Barcode.Trim();

    // The colour and size values PTT AVM lists for a product (its "variants" are these values, one per row).
    internal static IReadOnlySet<string> PttAvmOptionValues(IEnumerable<RemoteCatalogVariant> remoteVariants) =>
        remoteVariants
            .Where(variant => PttAvmVariantMapper.IsModelVariantId(variant.ExternalVariantId))
            .SelectMany(variant => variant.Options.Values)
            .Select(value => NormalizeCatalogKey(CleanCatalogOptionValue(value), 120))
            .Where(value => value.Length > 0)
            .ToHashSet(StringComparer.Ordinal);

    // The panel variant's colour and size values PTT AVM does not list for the product; none when it lists no values.
    internal static IReadOnlyList<string> PttAvmMissingOptionValues(string? localSignature, IReadOnlySet<string> optionValues)
    {
        if (optionValues.Count == 0 || string.IsNullOrWhiteSpace(localSignature)) return [];
        return VariantOptionSignature.ParseKeyed(localSignature)
            .Where(option => IsRealColorOptionKey(option.Key) || IsSizeOptionKey(option.Key))
            .Select(option => option.Value)
            .Where(value => value.Length > 0 && !optionValues.Contains(value))
            .ToList();
    }

    private async Task SeedChannelOfferAsync(Guid tenantId, Guid connectionId, Guid variantId, RemoteCatalogVariant remote, ConnectionInventoryPolicy? inventoryPolicy, DateTimeOffset now, CancellationToken cancellationToken, string source = "N11")
    {
        if (remote.SalePrice is null && remote.ListPrice is null) return;
        var salePrice = decimal.Round(Math.Max(0m, remote.SalePrice ?? remote.ListPrice ?? 0m), 2, MidpointRounding.AwayFromZero);
        var listPrice = decimal.Round(Math.Max(salePrice, Math.Max(0m, remote.ListPrice ?? salePrice)), 2, MidpointRounding.AwayFromZero);
        var existing = db.ChannelOffers.Local.FirstOrDefault(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.VariantId == variantId)
            ?? await db.ChannelOffers.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.VariantId == variantId, cancellationToken);
        if (existing is not null)
        {
            // Pazarama refuses an update that repeats the values it already holds. An offer that is still the
            // untouched copy of the remote price and stock counts as sent, so only real changes go out later.
            if (source == "PAZARAMA") await MarkPazaramaOfferInSyncAsync(tenantId, existing, remote, salePrice, listPrice, cancellationToken);
            if (source == "PTTAVM") await MarkPttAvmOfferInSyncAsync(tenantId, existing, remote, salePrice, listPrice, cancellationToken);
            return;
        }
        if (salePrice <= 0) return;
        var vatRate = await NewImportedOfferVatRateAsync(tenantId, connectionId, variantId, remote.VatRate, cancellationToken);
        var offer = new ChannelOffer
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            ConnectionId = connectionId,
            VariantId = variantId,
            ListPrice = listPrice,
            SalePrice = salePrice,
            Currency = NormalizeCurrency(remote.Currency),
            VatRate = vatRate,
            VatInclusion = "INCLUDED",
            RoundingMode = "HALF_EVEN",
            SafetyStock = decimal.Round(Math.Max(0m, inventoryPolicy?.DefaultSafetyStock ?? 0m), 4, MidpointRounding.ToEven),
            Status = remote.Archived ? "INACTIVE" : "ACTIVE",
            PriceVersion = 1,
            Version = 1
        };
        db.ChannelOffers.Add(offer);
        if (source == "PAZARAMA") await MarkPazaramaOfferInSyncAsync(tenantId, offer, remote, salePrice, listPrice, cancellationToken);
        if (source == "PTTAVM") await MarkPttAvmOfferInSyncAsync(tenantId, offer, remote, salePrice, listPrice, cancellationToken);
        db.ChannelPriceHistory.Add(new ChannelPriceHistory
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            OfferId = offer.Id,
            PriceVersion = offer.PriceVersion,
            ListPrice = offer.ListPrice,
            SalePrice = offer.SalePrice,
            Currency = offer.Currency,
            Reason = $"{source}_CATALOG_MAPPING",
            ActorSource = $"SYSTEM:{source}",
            EffectiveAt = now
        });
        telemetryInsertedCount++;
    }

    private async Task MarkPazaramaOfferInSyncAsync(Guid tenantId, ChannelOffer offer, RemoteCatalogVariant remote, decimal salePrice, decimal listPrice, CancellationToken cancellationToken)
    {
        var changed = false;
        if (offer.LastPriceHash is null && offer.PriceVersion == 1 && offer.SalePrice == salePrice && offer.ListPrice == listPrice)
        {
            offer.LastPriceHash = PazaramaPriceInventoryRules.PriceHash(offer.ListPrice, offer.SalePrice, offer.PriceVersion);
            changed = true;
        }
        if (offer.LastStockProjectionVersion is null && remote.StockQuantity is { } remoteStock)
        {
            var inventory = await db.InventoryItems.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == tenantId && x.VariantId == offer.VariantId && x.LocationCode == "MAIN", cancellationToken);
            if (inventory is not null && PazaramaPriceInventoryRules.Quantity(inventory.Available - offer.SafetyStock) == PazaramaPriceInventoryRules.Quantity(remoteStock))
            {
                offer.LastStockProjectionVersion = inventory.ProjectionVersion;
                changed = true;
            }
        }
        if (changed && db.Entry(offer).State != EntityState.Added) offer.Version++;
    }

    private async Task<bool> UpdateCatalogOptionsOnly(Guid tenantId, Guid connectionId, RemoteCatalogProduct snapshot, CancellationToken cancellationToken)
    {
        var externalProductId = Short(snapshot.ExternalProductId, 256);
        var link = await db.MarketplaceProductLinks.SingleOrDefaultAsync(
            x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ExternalId == externalProductId,
            cancellationToken);
        if (link is null) return false;

        var product = await db.Products.SingleOrDefaultAsync(
            x => x.TenantId == tenantId && x.Id == link.ProductId,
            cancellationToken);
        if (product is null) return false;

        var updates = new List<(ProductVariant Variant, IReadOnlyDictionary<string, string> Options, string Signature)>();
        var seenVariants = new HashSet<Guid>();
        foreach (var remote in snapshot.Variants)
        {
            var options = remote.Options
                .Where(pair => !IsWebColorOptionKey(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
            if (options.Count == 0) continue;

            var externalVariantId = Short(remote.ExternalVariantId, 256);
            var variantLink = await db.MarketplaceVariantLinks.SingleOrDefaultAsync(
                x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ExternalId == externalVariantId,
                cancellationToken);
            ProductVariant? variant = null;
            if (variantLink is not null)
            {
                variant = await db.ProductVariants.SingleOrDefaultAsync(
                    x => x.TenantId == tenantId && x.ProductId == product.Id && x.Id == variantLink.VariantId,
                    cancellationToken);
            }
            else
            {
                var sku = Short(string.IsNullOrWhiteSpace(remote.Sku) ? remote.Barcode ?? remote.ExternalVariantId : remote.Sku, 160);
                var skuKeys = CatalogLookupKeys(sku);
                var barcodeKeys = CatalogLookupKeys(remote.Barcode);
                var candidates = await db.ProductVariants
                    .Where(x => x.TenantId == tenantId && x.ProductId == product.Id
                        && (skuKeys.Contains(x.SkuNormalized)
                            || (x.BarcodeNormalized != null && barcodeKeys.Contains(x.BarcodeNormalized))))
                    .OrderBy(x => x.Id).Take(2)
                    .ToListAsync(cancellationToken);
                if (candidates.Count == 1) variant = candidates[0];
            }

            if (variant is null || !seenVariants.Add(variant.Id)) return false;
            updates.Add((variant, options, OptionSignature(options)));
        }

        // Avoid partially rewriting a product when even one remotely described
        // variant cannot be matched safely to an existing local variant, or a
        // legacy Web Color cleanup would leave a local-only variant uncovered.
        var optionVariants = snapshot.Variants.Count(remote => remote.Options.Any(pair => !IsWebColorOptionKey(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value)));
        var localVariantCount = await db.ProductVariants.CountAsync(
            x => x.TenantId == tenantId && x.ProductId == product.Id,
            cancellationToken);
        if (updates.Count == 0 || updates.Count != optionVariants || updates.Count != localVariantCount) return false;

        await NormalizeLegacyWebColorOptions(tenantId, product.Id, cancellationToken);
        var now = timeProvider.GetUtcNow();
        foreach (var (variant, options, signature) in updates)
        {
            if (!string.Equals(variant.OptionSignature, signature, StringComparison.Ordinal))
            {
                variant.OptionSignature = signature;
                variant.UpdatedAt = now;
                variant.Version++;
                telemetryUpdatedCount++;
            }
            await UpsertCatalogOptions(tenantId, connectionId, product.Id, variant.Id, options, null, cancellationToken);
        }

        return true;
    }

    private async Task<bool> UpsertCatalogProduct(Guid tenantId, Guid connectionId, RemoteCatalogProduct snapshot, CategoryAttributeContext? categoryContext, ConnectionInventoryPolicy? inventoryPolicy, CancellationToken cancellationToken, bool saveChanges = true, bool onlyNewVariants = false, bool observeOnly = false, bool preferBarcode = false, bool onlyExistingVariants = false, bool updateExistingProducts = true, bool overwriteLocalChanges = false, bool updatePrices = false, bool updateStock = false, bool isHepsiburada = false, bool matchBySku = false, string? observedPlatformName = null, DateTimeOffset? createdAt = null, bool allowNewProduct = true, IReadOnlySet<string>? onlyAddExternalVariantIds = null, bool brandMappedByExternalId = false)
    {
        var now = timeProvider.GetUtcNow();
        var externalProductId = Short(snapshot.ExternalProductId, 256);
        // New sizes added to a card the panel already links ("Yeni Ürünleri Aktar"): nothing of the card changes.
        var addOnly = onlyNewVariants && !observeOnly;
        // The listing's brand is mapped like its category (owner decision 12 Nov 2026, ImportBrandId): the mapping on
        // this connection, else the panel brand of the same name (mapped now), else a new panel brand (mapped now).
        // An observe-only read (Hepsiburada, Shopify, n11, Pazarama, PTT AVM) only gives a brand to a product it opens,
        // so its brand is settled where the product is written; a run that adds sizes to a linked card only reads it.
        var brandPlatformName = observedPlatformName ?? (isHepsiburada ? "Hepsiburada" : matchBySku ? "N11" : observeOnly ? "Shopify" : "Trendyol");
        var mappedBrandId = observeOnly ? null : await ImportBrandId(tenantId, connectionId, snapshot, brandPlatformName, brandMappedByExternalId, create: !addOnly, cancellationToken);
        // Keep the import contract version in the hash. This invalidates older
        // snapshots after changing option-role or brand-mapping interpretation,
        // so catalog records receive the corrected local relationships on the
        // next scan. The mapped brand is part of it: a brand the user maps later
        // reaches the products on the next scan although the listing is unchanged.
        var remoteHash = observeOnly
            ? Hash(JsonSerializer.Serialize(new
            {
                Snapshot = snapshot,
                OptionRoleVersion = "catalog-options-v10-shopify-variant-first-match"
            }))
            : Hash(JsonSerializer.Serialize(new
            {
                Snapshot = snapshot,
                // Reprocess existing catalog products after changing mapped
                // attribute assignment semantics, not only newly fetched rows.
                OptionRoleVersion = "catalog-options-v9-web-color-adapter",
                BrandRule = $"user-mapping-v1:{mappedBrandId:D}"
            }));
        var isNewProduct = false;
        var link = await db.MarketplaceProductLinks.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ExternalId == externalProductId, cancellationToken);
        Product? product = link is null
            ? null
            : await db.Products.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == link.ProductId, cancellationToken);
        if (observeOnly)
        {
            // Shopify has no main-product/model-code identity for this
            // integration. Resolve local products from their variants first,
            // even when an older product link already exists. A Shopify product
            // may contain one model's colours while Ravencia stores each colour
            // as a separate Product row, so existing-only sync may need to link
            // variants across more than one local product.
            var allowCrossProductVariantMatch = preferBarcode && onlyExistingVariants;
            var observedPlatform = observedPlatformName ?? (matchBySku ? "N11" : "Shopify");
            var identityCodes = CatalogKeyNormalizer.LookupKeys(snapshot.Variants.Select(variant => variant.Barcode))
                .ToHashSet(StringComparer.Ordinal);
            // n11 keys every listing by the seller stock code, which is the
            // panel SKU; the barcode is only the fallback there.
            var skuCodes = matchBySku
                ? CatalogKeyNormalizer.LookupKeys(snapshot.Variants.Select(variant => variant.Sku)).ToHashSet(StringComparer.Ordinal)
                : [];
            if (identityCodes.Count > 0 || skuCodes.Count > 0)
            {
                var identityProductIds = skuCodes.Count == 0
                    ? []
                    : await db.ProductVariants.AsNoTracking()
                        .Where(x => x.TenantId == tenantId && skuCodes.Contains(x.SkuNormalized))
                        .Select(x => x.ProductId)
                        .Distinct()
                        .OrderBy(id => id).Take(2)
                        .ToListAsync(cancellationToken);
                if (identityProductIds.Count == 0 && identityCodes.Count > 0)
                    identityProductIds = await db.ProductVariants.AsNoTracking()
                        .Where(x => x.TenantId == tenantId
                            && x.BarcodeNormalized != null
                            && identityCodes.Contains(x.BarcodeNormalized))
                        .Select(x => x.ProductId)
                        .Distinct()
                        .OrderBy(id => id).Take(2)
                        .ToListAsync(cancellationToken);
                if (identityProductIds.Count > 1)
                {
                    if (!allowCrossProductVariantMatch)
                    {
                        await RecordIssue(tenantId, $"product-sync-barcode-conflict:{connectionId}:{(skuCodes.Count > 0 ? skuCodes.First() : identityCodes.First())}", "PRODUCT_BARCODE_CONFLICT", $"{observedPlatform} ürününün stok kodu veya barkodu birden fazla yerel üründe bulundu; otomatik eşleştirme yapılmadı.", cancellationToken);
                        return false;
                    }

                    // Keep the existing product link stable when one exists;
                    // individual variants are resolved globally by barcode
                    // below and can therefore belong to sibling colour rows.
                    var representativeProductId = link?.ProductId ?? identityProductIds[0];
                    product = await db.Products.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == representativeProductId, cancellationToken);
                }
                if (identityProductIds.Count == 1)
                {
                    var variantMatchedProduct = await db.Products.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == identityProductIds[0], cancellationToken);
                    if (variantMatchedProduct is not null && link is not null && link.ProductId != variantMatchedProduct.Id)
                    {
                        await RecordIssue(tenantId, $"product-sync-link-conflict:{connectionId}:{externalProductId}", "PRODUCT_LINK_CONFLICT", $"{observedPlatform} ürünü aynı bağlantıda başka bir dış ürünle eşleşmiş yerel ürüne bağlanmadı.", cancellationToken);
                        return false;
                    }
                    if (variantMatchedProduct is not null && await db.MarketplaceProductLinks.AnyAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ProductId == variantMatchedProduct.Id && x.ExternalId != externalProductId, cancellationToken))
                    {
                        await RecordIssue(tenantId, $"product-sync-link-conflict:{connectionId}:{externalProductId}", "PRODUCT_LINK_CONFLICT", $"{observedPlatform} varyantı aynı bağlantıda başka bir dış ürünle eşleşmiş yerel ürüne bağlanmadı.", cancellationToken);
                        return false;
                    }
                    product = variantMatchedProduct;
                }
            }
            // Hepsiburada links to the panel's products the way the import always matched: by barcode, then the seller's
            // stock code, then the model code. A listing none of them finds opens no product (a Hepsiburada code is not
            // a panel product: the SIT test listings carry nothing else); it is reported as unmatched.
            if (isHepsiburada && product is null)
            {
                var stockCodes = CatalogKeyNormalizer.LookupKeys(snapshot.Variants.Select(variant => variant.Sku)).ToHashSet(StringComparer.Ordinal);
                var modelCodes = snapshot.Variants.Select(variant => variant.ModelCode?.Trim()).Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value!).Distinct(StringComparer.Ordinal).ToArray();
                var matchedProductIds = stockCodes.Count == 0 ? [] : await db.ProductVariants.AsNoTracking()
                    .Where(x => x.TenantId == tenantId && stockCodes.Contains(x.SkuNormalized)).Select(x => x.ProductId).Distinct().OrderBy(id => id).Take(2).ToListAsync(cancellationToken);
                // A model code names every colour and size of the model: the product is the one whose variants carry the
                // listing's colours and sizes, never the only card that happens to hold the model code (Y5).
                var modelCodeUndecided = false;
                if (matchedProductIds.Count == 0 && modelCodes.Length > 0)
                {
                    var modelVariants = await db.ProductVariants.AsNoTracking()
                        .Where(x => x.TenantId == tenantId && x.ModelCode != null && modelCodes.Contains(x.ModelCode))
                        .Select(x => new ModelCodeVariant(x.ProductId, x.ModelCode!, x.OptionSignature))
                        .ToListAsync(cancellationToken);
                    matchedProductIds = ModelCodeProducts(modelVariants, snapshot.Variants, snapshot.Title);
                    modelCodeUndecided = modelVariants.Count > 0 && matchedProductIds.Count == 0;
                }
                if (matchedProductIds.Count == 1
                    && !await db.MarketplaceProductLinks.AnyAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ProductId == matchedProductIds[0] && x.ExternalId != externalProductId, cancellationToken))
                    product = await db.Products.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == matchedProductIds[0], cancellationToken);
                if (product is null)
                {
                    var remote = snapshot.Variants.FirstOrDefault();
                    await RecordIssue(tenantId, $"product-mapping-unmatched:{connectionId}:{Short(externalProductId, 256)}", "PRODUCT_MAPPING_UNMATCHED",
                        Short($"Hepsiburada ürünü panelde eşleşmedi: barkod {remote?.Barcode ?? "yok"}, stok kodu {remote?.Sku ?? "yok"}, model kodu {remote?.ModelCode ?? "yok"} · {snapshot.Title}. {(matchedProductIds.Count > 1 ? "Birden fazla panel ürünü aday çıktı." : modelCodeUndecided ? "Model kodu panelde var ama hiçbir panel varyantının rengi ve bedeni bu ürününkiyle aynı değil; ürün seçilmedi." : "Panelde aynı barkod, stok kodu veya model kodu yok; Hepsiburada ürünü panelde yeni ürün olarak açılmaz.")}", 1000), cancellationToken);
                    return false;
                }
            }
        }
        // A listing without a product link of its own can still be a panel product's: Trendyol lists each colour as
        // its own content, while a product published from the panel keeps its colours in one product and links only
        // its variants. Its variants find it; the listing then brings only its stock and prices (the panel's content
        // stays), and no second, empty product is opened beside it. A link that already leads to such an empty product
        // (one an earlier import opened) gives way to the owner too, so that product is not filled in again.
        var linkedProductHollow = product is not null && !observeOnly && !await db.ProductVariants.AnyAsync(x => x.TenantId == tenantId && x.ProductId == product.Id, cancellationToken);
        if ((product is null || linkedProductHollow) && !observeOnly)
        {
            var owners = await CatalogProductsOwningVariants(tenantId, connectionId, snapshot, cancellationToken);
            if (owners.Count > 1)
            {
                await RecordIssue(tenantId, $"product-sync-variant-owner-conflict:{connectionId}:{Short(externalProductId, 200)}", "PRODUCT_VARIANT_CONFLICT", Short($"Pazar yeri ürününün varyantları panelde birden fazla ürüne ait; yeni ürün açılmadı, mevcut kayıtlar korundu · {snapshot.Title}", 1000), cancellationToken);
                return false;
            }
            if (owners.Count == 1 && await db.Products.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == owners[0], cancellationToken) is { } owner)
            {
                await SyncCatalogInventoryForPreservedProduct(tenantId, connectionId, owner, snapshot, inventoryPolicy, now, cancellationToken, updatePrices: updatePrices, updateStock: updateStock);
                if (saveChanges)
                    await db.SaveChangesAsync(cancellationToken);
                telemetrySkippedCount++;
                return updatePrices || updateStock;
            }
        }
        // "Mevcut Ürünleri Güncelle" never opens a product, and no mode opens one without a variant it can keep.
        if (product is null && (!allowNewProduct || !snapshot.Variants.Any(variant => !string.IsNullOrWhiteSpace(NormalizeCatalogKey(variant.Sku ?? variant.Barcode ?? variant.ExternalVariantId, 160)))))
        {
            telemetrySkippedCount++;
            return false;
        }
        if (product is null)
        {
            isNewProduct = true;
            var listingVatRate = ListingVatRate(snapshot);
            product = new Product
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenantId,
                Title = ProductTitle(snapshot.Title, externalProductId),
                Description = snapshot.Description ?? "",
                // The card's VAT is the listing's own (the rate its offers below are priced with); a listing that
                // carries none leaves it unknown.
                VatRate = listingVatRate,
                VatInclusion = listingVatRate is null ? null : "INCLUDED",
                Status = ProductStatus.Active,
                CreatedAt = createdAt ?? now,
                UpdatedAt = now,
                Version = 1
            };
            db.Products.Add(product);
            telemetryInsertedCount++;
            if (link is null)
            {
                link = new MarketplaceProductLink { Id = Guid.CreateVersion7(), TenantId = tenantId, ConnectionId = connectionId, ProductId = product.Id, ExternalId = externalProductId, LastImportedPayloadHash = remoteHash, SyncStatus = "SYNCED", Version = 1 };
                db.MarketplaceProductLinks.Add(link);
                telemetryInsertedCount++;
            }
        }
        else if (link is null && observeOnly)
        {
            link = new MarketplaceProductLink
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenantId,
                ConnectionId = connectionId,
                ProductId = product.Id,
                ExternalId = externalProductId,
                LastImportedPayloadHash = remoteHash,
                SyncStatus = "SYNCED",
                Version = 1
            };
            db.MarketplaceProductLinks.Add(link);
            telemetryInsertedCount++;
        }

        var expectedVariantExternalIds = snapshot.Variants
            .Select(variant => Short(variant.ExternalVariantId, 256))
            .Where(externalId => !string.IsNullOrWhiteSpace(externalId))
            .Select(MarketplaceVariantLinkCoverage.Normalize)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var linkedVariantExternalIds = expectedVariantExternalIds.Length == 0
            ? []
            : await (from variantLink in db.MarketplaceVariantLinks.AsNoTracking()
                     join variant in db.ProductVariants.AsNoTracking()
                         on new { variantLink.TenantId, variantLink.VariantId }
                         equals new { variant.TenantId, VariantId = variant.Id }
                     where variantLink.TenantId == tenantId
                         && variantLink.ConnectionId == connectionId
                         && variant.ProductId == product!.Id
                         && expectedVariantExternalIds.Contains(variantLink.ExternalId.Trim().ToUpper())
                     select variantLink.ExternalId.Trim().ToUpper()).ToListAsync(cancellationToken);
        var hasCompleteVariantLinks = MarketplaceVariantLinkCoverage.IsComplete(expectedVariantExternalIds, linkedVariantExternalIds);

        // Runs before the unchanged-payload and preserved-product paths, so a product imported before desi was read
        // (or edited locally since) still gets the marketplace desi on its empty variants.
        // Adding new sizes only ("Yeni Ürünleri Aktar") leaves the panel's variants as they are, an empty desi too.
        if (!isNewProduct && !addOnly)
            await FillMissingImportedDesi(tenantId, connectionId, snapshot, now, cancellationToken);

        // A catalog scan can revisit hundreds of products whose last imported
        // payload and local projection are already in sync. In that case the
        // inventory/media reconciliation below only repeats database reads;
        // the version marker is sufficient to prove that no local edit is
        // waiting to be preserved and that this exact remote payload was
        // already applied. Keep the legacy reconciliation path for older links
        // that do not have a trustworthy imported-product version yet.
        if (!onlyNewVariants
            && !isNewProduct
            && !updatePrices
            && !updateStock
            && link is not null
            && string.Equals(link.LastImportedPayloadHash, remoteHash, StringComparison.OrdinalIgnoreCase)
            && link.LastImportedProductVersion == product!.Version
            && string.Equals(link.SyncStatus, "SYNCED", StringComparison.OrdinalIgnoreCase)
            && hasCompleteVariantLinks)
        {
            await RepairMissingCatalogOptionAssignments(tenantId, connectionId, product.Id, snapshot, categoryContext, cancellationToken);
            if (saveChanges)
                await db.SaveChangesAsync(cancellationToken);
            telemetrySkippedCount++;
            return observeOnly;
        }

        if (!onlyNewVariants && !isNewProduct && link is not null && hasCompleteVariantLinks && string.Equals(link.LastImportedPayloadHash, remoteHash, StringComparison.OrdinalIgnoreCase) && await CatalogSnapshotAlreadyApplied(tenantId, product.Id, snapshot, cancellationToken))
        {
            // Repair missing option values/assignments even when the product payload
            // itself was already imported and no longer needs a full upsert.
            await RepairMissingCatalogOptionAssignments(tenantId, connectionId, product.Id, snapshot, categoryContext, cancellationToken);
            // A previous import may have stored the remote observation while
            // leaving the untouched local projection at zero. Reconcile that
            // legacy state even when the payload itself has not changed.
            await SyncCatalogInventoryForPreservedProduct(tenantId, connectionId, product, snapshot, inventoryPolicy, now, cancellationToken, observeOnly, preferBarcode, updatePrices, updateStock);
            if (saveChanges)
                await db.SaveChangesAsync(cancellationToken);
            telemetrySkippedCount++;
            // A Shopify read-only observation is still a successful match even
            // when the local product hash is unchanged. The local catalog is
            // intentionally preserved, but progress must report the linked
            // product as processed rather than silently skipped.
            return observeOnly;
        }

        var preserveDueToLocalChanges = link is not null && (!overwriteLocalChanges && ProductImportMergePolicy.PreserveLocalChanges(product.Version, link.LastImportedProductVersion, link.DirtyFieldsJson) || observeOnly && !isNewProduct);
        var preserveDueToImportOption = link is not null && !updateExistingProducts && !isNewProduct;
        var preserveLocal = preserveDueToLocalChanges || preserveDueToImportOption;
        if (!onlyNewVariants && !preserveLocal)
        {
            // A title or description the listing does not carry is unknown, not empty: the panel's stays.
            if (isNewProduct || !string.IsNullOrWhiteSpace(snapshot.Title)) product.Title = ProductTitle(snapshot.Title, externalProductId);
            if (isNewProduct || snapshot.Description is not null) product.Description = snapshot.Description ?? "";
            // The panel product's own status is the user's (report item 19): the import never activates or archives
            // it. What the marketplace says about the listing stays on its links (SaleState) and offers.
            product.UpdatedAt = now;
            product.Version++;
            telemetryUpdatedCount++;
        }
        else if (preserveLocal)
        {
            telemetrySkippedCount++;
            if (isHepsiburada)
                await RepairHepsiburadaVariantLinks(tenantId, connectionId, product, snapshot, cancellationToken);
        }

        // K13: a mapped panel category the user switched off or split into sub-categories cannot take
        // products, the same rule UpsertCatalogCategory applies; the product stays without a category and
        // the mapping is reported once per category.
        // An observe-only read (Pazarama, PttAVM, N11, Hepsiburada) gives a product it opens the panel category its
        // marketplace category is mapped to (the import made that mapping), as it gives it the brand; an existing
        // product's category is never touched (preserved above).
        var category = ObservedProductCategory(observeOnly, addOnly, isNewProduct, categoryContext?.LocalCategory)
            ?? (observeOnly || addOnly ? null : categoryContext?.LocalCategory ?? await UpsertCatalogCategory(tenantId, snapshot.CategoryName, cancellationToken));
        if (category is { } mappedCategory && categoryContext is not null && (!mappedCategory.IsActive || !mappedCategory.IsLeaf))
        {
            await RecordIssue(tenantId, $"category-mapping-unusable:{connectionId:D}:{mappedCategory.Id:D}", "CATEGORY_MAPPING_UNUSABLE", $"Pazaryeri kategorisinin eşlendiği \"{Short(mappedCategory.Name, 120)}\" kategorisi {(mappedCategory.IsActive ? "alt kategorilere bölünmüş" : "kapalı")}; ürünler kategorisiz aktarıldı. Eşleştirmeyi etkin bir yaprak kategoriye çevirin.", cancellationToken);
            category = null;
        }
        if (!preserveLocal && !onlyNewVariants)
        {
            // An observe-only read writes only a product it opens (an existing one is preserved above), so its brand
            // is mapped here, for that product alone.
            if (observeOnly && isNewProduct)
                mappedBrandId = await ImportBrandId(tenantId, connectionId, snapshot, brandPlatformName, brandMappedByExternalId, create: true, cancellationToken);
            product.BrandId = ImportedBrandRule.NextProductBrand(isNewProduct, product.BrandId, mappedBrandId);
            product.CategoryId = category?.Id;
        }

        var importedNewVariantCount = 0;
        if (!preserveLocal || onlyNewVariants || observeOnly)
        {
            if (!onlyNewVariants)
                await NormalizeLegacyWebColorOptions(tenantId, product.Id, cancellationToken);
            var existingVariantExternalIds = onlyNewVariants
                ? (await db.MarketplaceVariantLinks.AsNoTracking()
                        .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId)
                        .Select(x => x.ExternalId)
                        .ToListAsync(cancellationToken))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase)
                : null;
            var importedVariants = new List<ProductVariant>(snapshot.Variants.Count);
            foreach (var (remote, sortOrder) in snapshot.Variants.Select((remote, index) => (remote, index)))
            {
                var externalVariantId = Short(remote.ExternalVariantId, 256);
                if (onlyNewVariants && existingVariantExternalIds!.Contains(externalVariantId))
                    continue;
                // A variant the panel holds under another code (stock code or barcode, no link yet) is not new: it is
                // neither linked nor rewritten here ("Ürün Eşleme" links it).
                if (addOnly && onlyAddExternalVariantIds is not null && !onlyAddExternalVariantIds.Contains(externalVariantId))
                    continue;
                var importedVariant = await UpsertCatalogVariant(tenantId, connectionId, product, remote, sortOrder, categoryContext, inventoryPolicy, now, cancellationToken, observeOnly, preferBarcode, onlyExistingVariants, updatePrices, updateStock);
                if (importedVariant is not null)
                {
                    importedVariants.Add(importedVariant);
                    if (onlyNewVariants) importedNewVariantCount++;
                    existingVariantExternalIds?.Add(externalVariantId);
                }
            }
            // The new sizes a plain pull reported on this card are in the panel now.
            if (!observeOnly && !isNewProduct && importedVariants.Any(variant => db.Entry(variant).State == EntityState.Added))
                await ResolveIssue(tenantId, $"product-new-sizes:{connectionId:D}:{Short(externalProductId, 200)}", cancellationToken);
            // Whatever the import options, a product is never opened without a variant of its own: when every variant
            // turned out to be another panel product's, the new product, its link and options are dropped again.
            if (isNewProduct && importedVariants.Count == 0)
            {
                DropNewCatalogProduct(db, product);
                telemetryInsertedCount = Math.Max(0, telemetryInsertedCount - 2);
                telemetrySkippedCount++;
                await RecordIssue(tenantId, $"product-sync-no-variant:{connectionId}:{Short(externalProductId, 200)}", "PRODUCT_VARIANT_CONFLICT", Short($"Pazar yeri ürünü panelde açılmadı: varyantlarının hiçbiri eklenemedi (stok kodları başka panel ürünlerinde) · {snapshot.Title}", 1000), cancellationToken);
                if (saveChanges)
                    await db.SaveChangesAsync(cancellationToken);
                return false;
            }
            // "Ekli Olmayan Ürünler" brings what the panel lacks and never rewrites a card's own fields: the attributes
            // common to the new sizes alone are not the card's.
            if (categoryContext is not null && importedVariants.Count > 0 && !onlyNewVariants)
                await PromoteCommonImportedAttributes(tenantId, product, importedVariants, categoryContext, cancellationToken);

            if (!onlyNewVariants)
            {
                var productImageUrls = CatalogImageIdentity.DistinctUrls(
                    snapshot.ImageUrls.Concat(snapshot.Variants.SelectMany(variant => variant.ImageUrls ?? [])));
                // A listing read without images says nothing about them: the panel's gallery stays.
                if (productImageUrls.Count > 0 || isNewProduct)
                    await UpsertCatalogMedia(tenantId, product, null, productImageUrls, product.Title, cancellationToken);
            }
        }
        else
        {
            // Local product edits must not block stock observations. Match
            // existing variants without touching their local content/options,
            // then apply the inventory part of the remote snapshot. Trendyol
            // media is a separate remote projection, so a manual product edit
            // must not leave an older Shopify gallery in place.
            await SyncCatalogInventoryForPreservedProduct(tenantId, connectionId, product, snapshot, inventoryPolicy, now, cancellationToken, observeOnly, preferBarcode, updatePrices, updateStock, syncMedia: !observeOnly && updateExistingProducts);
            // A plain pull keeps the card whole, a size Trendyol added since included (the owner's rule, 12 Nov 2026: only
            // "Ekli Olmayan Ürünler" or "Mevcut Ürün Bilgilerini Güncelle" add it). The owner is told it is there.
            if (!observeOnly)
            {
                var unadded = (await RemoteVariantsMissingFromPanel(tenantId, connectionId, snapshot, cancellationToken)).Count;
                var issueKey = $"product-new-sizes:{connectionId:D}:{Short(externalProductId, 200)}";
                if (unadded > 0)
                {
                    productUnaddedSizeCount = unadded;
                    telemetryUnaddedSizeCount += unadded;
                    await RecordIssue(tenantId, issueKey, "PRODUCT_NEW_SIZES_NOT_IMPORTED", Short($"{product.Title}: Trendyol'da panelde olmayan {UnaddedSizesNote(unadded)}", 1000), cancellationToken);
                }
                else
                    await ResolveIssue(tenantId, issueKey, cancellationToken);
            }
            if (!observeOnly && updateExistingProducts && snapshot.ImageUrls.Count > 0)
            {
                var productImageUrls = CatalogImageIdentity.DistinctUrls(snapshot.ImageUrls);
                await UpsertCatalogMedia(tenantId, product, null, productImageUrls, product.Title, cancellationToken);
            }
        }
        // Adding new sizes to a card the panel holds leaves its link as it was: the card's content was not taken, so the
        // payload hash, the import marks and the local-change flags must not say it was (a later pull would otherwise
        // skip the content as already applied).
        if (addOnly)
        {
            productImportedNewVariantCount = importedNewVariantCount;
            if (saveChanges)
                await db.SaveChangesAsync(cancellationToken);
            return importedNewVariantCount > 0;
        }
        link ??= await db.MarketplaceProductLinks.SingleAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ExternalId == externalProductId, cancellationToken);
        link.LastImportedPayloadHash = remoteHash;
        link.LastImportedAt = now;
        if (preserveDueToLocalChanges && !onlyNewVariants)
        {
            link.SyncStatus = "LOCAL_CHANGES_PENDING";
            link.DirtyFieldsJson ??= "[\"product\"]";
        }
        else if (!preserveLocal)
        {
            link.LastImportedProductVersion = product.Version;
            link.SyncStatus = "SYNCED";
            link.DirtyFieldsJson = null;
            link.LastError = null;
        }
        link.Version++;
        if (saveChanges)
            await db.SaveChangesAsync(cancellationToken);
        return onlyNewVariants
            ? importedNewVariantCount > 0
            : observeOnly
                ? !isNewProduct
                : !preserveLocal;
    }

    // Takes back a product this import added and has not saved yet, with every row added for it (its link, options,
    // media). Shared rows the import may have added beside it (a category, a brand another row uses) stay; a brand added
    // for this product alone (an observe-only read opens a brand only for a product it opens) goes with it.
    // Pazarama order pages: at most this many in one window (100 orders a page; a 28-day window of 50,000 orders), and a
    // page that repeats the one before ends the window; either is named in an issue instead of looping until the lease.
    internal const int PazaramaOrderPageCap = 500;

    internal static string? PazaramaOrderPageKey(IReadOnlyList<RemoteOrder> items) =>
        items.Count == 0 ? null : string.Join(',', items.Select(item => item.ExternalOrderId).Order(StringComparer.Ordinal));

    internal static bool PazaramaOrderPageRepeated(string? previousPageKey, string? pageKey) =>
        pageKey is not null && string.Equals(previousPageKey, pageKey, StringComparison.Ordinal);

    // Pazarama's order list: at most PazaramaOrderPageCap pages per window, and a page that repeats the one before ends it.
    private static readonly PageGuard PazaramaOrderPageGuard = new(MaxPages: PazaramaOrderPageCap, PageRepeated: PazaramaOrderPageRepeated);

    // The mapped panel category an observe-only read gives a product it opens; null otherwise (an existing product, a
    // plain or add-only read, which choose their category themselves, or no mapped category).
    internal static Category? ObservedProductCategory(bool observeOnly, bool addOnly, bool isNewProduct, Category? mapped) =>
        observeOnly && !addOnly && isNewProduct ? mapped : null;

    internal static void DropNewCatalogProduct(AppDbContext db, Product product)
    {
        var brandId = product.BrandId;
        foreach (var entry in db.ChangeTracker.Entries().Where(entry => entry.State == EntityState.Added).ToList())
        {
            if (ReferenceEquals(entry.Entity, product)
                || entry.Metadata.FindProperty("ProductId") is { } productIdProperty && entry.Property(productIdProperty.Name).CurrentValue is Guid owner && owner == product.Id)
                entry.State = EntityState.Detached;
        }
        if (brandId is not { } id) return;
        var brand = db.ChangeTracker.Entries<Brand>().FirstOrDefault(entry => entry.State == EntityState.Added && entry.Entity.Id == id);
        if (brand is null) return;
        var usedElsewhere = db.ChangeTracker.Entries().Any(entry => entry.State != EntityState.Detached && !ReferenceEquals(entry.Entity, brand.Entity)
            && entry.Metadata.FindProperty("BrandId") is { } brandProperty && entry.Property(brandProperty.Name).CurrentValue is Guid used && used == id);
        if (usedElsewhere) return;
        // The mapping the import wrote for the brand it opened goes with it.
        foreach (var mapping in db.ChangeTracker.Entries<BrandMapping>().Where(entry => entry.State == EntityState.Added && entry.Entity.LocalId == id).ToList())
            mapping.State = EntityState.Detached;
        brand.State = EntityState.Detached;
    }

    // The panel products a listing's variants already belong to: by this connection's variant link, then the stock
    // code, then the barcode. At most two, which is enough to tell one owner from a conflict.
    private async Task<List<Guid>> CatalogProductsOwningVariants(Guid tenantId, Guid connectionId, RemoteCatalogProduct snapshot, CancellationToken cancellationToken)
    {
        var externalIds = snapshot.Variants.Select(variant => Short(variant.ExternalVariantId, 256)).Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).ToArray();
        var owners = externalIds.Length == 0 ? [] : await (from variantLink in db.MarketplaceVariantLinks.AsNoTracking()
                                                           join variant in db.ProductVariants.AsNoTracking()
                                                               on new { variantLink.TenantId, variantLink.VariantId }
                                                               equals new { variant.TenantId, VariantId = variant.Id }
                                                           where variantLink.TenantId == tenantId && variantLink.ConnectionId == connectionId && externalIds.Contains(variantLink.ExternalId)
                                                           select variant.ProductId).Distinct().OrderBy(id => id).Take(2).ToListAsync(cancellationToken);
        if (owners.Count > 0) return owners;
        var skuKeys = snapshot.Variants.SelectMany(variant => CatalogLookupKeys(string.IsNullOrWhiteSpace(variant.Sku) ? variant.Barcode ?? variant.ExternalVariantId : variant.Sku)).Where(key => key.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
        if (skuKeys.Length > 0)
            owners = await db.ProductVariants.AsNoTracking().Where(x => x.TenantId == tenantId && skuKeys.Contains(x.SkuNormalized)).Select(x => x.ProductId).Distinct().OrderBy(id => id).Take(2).ToListAsync(cancellationToken);
        if (owners.Count > 0) return owners;
        var barcodeKeys = snapshot.Variants.SelectMany(variant => CatalogLookupKeys(variant.Barcode)).Where(key => key.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
        return barcodeKeys.Length == 0 ? [] : await db.ProductVariants.AsNoTracking().Where(x => x.TenantId == tenantId && x.BarcodeNormalized != null && barcodeKeys.Contains(x.BarcodeNormalized)).Select(x => x.ProductId).Distinct().OrderBy(id => id).Take(2).ToListAsync(cancellationToken);
    }

    private async Task RepairHepsiburadaVariantLinks(Guid tenantId, Guid connectionId, Product product, RemoteCatalogProduct snapshot, CancellationToken cancellationToken)
    {
        var localVariants = await db.ProductVariants
            .Where(variant => variant.TenantId == tenantId && variant.ProductId == product.Id)
            .ToListAsync(cancellationToken);
        var localVariantIds = localVariants.Select(variant => variant.Id).ToArray();
        var externalIds = snapshot.Variants
            .Select(variant => MarketplaceVariantLinkCoverage.Normalize(Short(variant.ExternalVariantId, 256)))
            .Where(externalId => externalId.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (localVariantIds.Length > 0 || externalIds.Length > 0)
        {
            var existingLinks = await db.MarketplaceVariantLinks
                .Where(link => link.TenantId == tenantId
                    && link.ConnectionId == connectionId
                    && (localVariantIds.Contains(link.VariantId)
                        || externalIds.Contains(link.ExternalId.Trim().ToUpper())))
                .ToListAsync(cancellationToken);
            var linkedExternalIds = existingLinks
                .Select(link => MarketplaceVariantLinkCoverage.Normalize(link.ExternalId))
                .ToHashSet(StringComparer.Ordinal);
            var linkedVariantIds = existingLinks.Select(link => link.VariantId).ToHashSet();

            foreach (var remote in snapshot.Variants)
            {
                var externalId = Short(remote.ExternalVariantId, 256);
                var externalKey = MarketplaceVariantLinkCoverage.Normalize(externalId);
                if (externalKey.Length == 0 || linkedExternalIds.Contains(externalKey)) continue;

                var skuKey = NormalizeCatalogKey(remote.Sku, 160);
                var externalSkuKey = NormalizeCatalogKey(externalId, 160);
                var barcodeKey = NormalizeCatalogKey(remote.Barcode, 160);
                var candidates = localVariants
                    .Where(variant =>
                        skuKey.Length > 0 && CatalogKeyNormalizer.SameKey(variant.SkuNormalized, skuKey)
                        || externalSkuKey.Length > 0 && CatalogKeyNormalizer.SameKey(variant.SkuNormalized, externalSkuKey)
                        || barcodeKey.Length > 0 && CatalogKeyNormalizer.SameKey(variant.BarcodeNormalized, barcodeKey))
                    .Select(variant => variant.Id)
                    .Distinct()
                    .ToArray();
                if (candidates.Length != 1 || linkedVariantIds.Contains(candidates[0])) continue;

                var link = new MarketplaceVariantLink
                {
                    Id = Guid.CreateVersion7(),
                    TenantId = tenantId,
                    ConnectionId = connectionId,
                    VariantId = candidates[0],
                    ExternalId = externalId,
                    Version = 1
                };
                ListingSaleStateReads.ApplyToNewLink(link, remote.SaleState, remote.SaleStateReason, timeProvider.GetUtcNow());
                db.MarketplaceVariantLinks.Add(link);
                existingLinks.Add(link);
                linkedExternalIds.Add(externalKey);
                linkedVariantIds.Add(link.VariantId);
                telemetryInsertedCount++;
            }
        }

        var hasUsableMedia = await (from media in db.ProductMedia.AsNoTracking()
                                    join asset in db.FileAssets.AsNoTracking()
                                        on new { media.TenantId, FileAssetId = media.FileAssetId }
                                        equals new { asset.TenantId, FileAssetId = asset.Id }
                                    where media.TenantId == tenantId
                                        && media.ProductId == product.Id
                                        && media.Status == "ACTIVE"
                                        && asset.Status == "ACTIVE"
                                        && (asset.Classification == "PRODUCT_MEDIA_URL" || asset.Classification == "PRODUCT_MEDIA")
                                    select media.Id).AnyAsync(cancellationToken);
        if (!hasUsableMedia)
        {
            var imageUrls = CatalogImageIdentity.DistinctUrls(
                snapshot.ImageUrls.Concat(snapshot.Variants.SelectMany(variant => variant.ImageUrls ?? [])));
            if (imageUrls.Count > 0)
                await UpsertCatalogMedia(tenantId, product, null, imageUrls, product.Title, cancellationToken);
        }
    }

    // The listing's variants the panel does not hold at all: no variant link of this connection names them and no panel
    // variant carries their stock code or barcode (a code known on any card is not a new size).
    private async Task<List<RemoteCatalogVariant>> RemoteVariantsMissingFromPanel(Guid tenantId, Guid connectionId, RemoteCatalogProduct snapshot, CancellationToken cancellationToken)
    {
        var externalIds = snapshot.Variants.Select(variant => Short(variant.ExternalVariantId, 256)).Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).ToArray();
        var linked = externalIds.Length == 0
            ? []
            : (await db.MarketplaceVariantLinks.AsNoTracking()
                .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && externalIds.Contains(x.ExternalId))
                .Select(x => x.ExternalId)
                .ToListAsync(cancellationToken)).ToHashSet(StringComparer.Ordinal);
        var candidates = new List<(RemoteCatalogVariant Remote, string Sku, string[] SkuKeys, string[] BarcodeKeys)>();
        foreach (var remote in snapshot.Variants)
        {
            var externalId = Short(remote.ExternalVariantId, 256);
            if (string.IsNullOrWhiteSpace(externalId) || linked.Contains(externalId)) continue;
            var sku = Short(string.IsNullOrWhiteSpace(remote.Sku) ? remote.Barcode ?? remote.ExternalVariantId : remote.Sku, 160);
            var skuKeys = CatalogLookupKeys(sku);
            var barcodeKeys = CatalogLookupKeys(remote.Barcode);
            if (skuKeys.Length == 0) continue;
            if (db.ProductVariants.Local.Any(x => x.TenantId == tenantId && (CatalogKeyNormalizer.SameKey(x.SkuNormalized, sku) || barcodeKeys.Length > 0 && CatalogKeyNormalizer.SameKey(x.BarcodeNormalized, remote.Barcode)))) continue;
            candidates.Add((remote, sku, skuKeys, barcodeKeys));
        }
        if (candidates.Count == 0) return [];
        // The stored keys of every candidate read in one query, not one query per variant: a variant is known when a panel
        // variant's stock code is one of its stock-code keys or that variant's barcode one of its barcode keys, as before.
        var allSkuKeys = candidates.SelectMany(x => x.SkuKeys).Distinct(StringComparer.Ordinal).ToArray();
        var allBarcodeKeys = candidates.SelectMany(x => x.BarcodeKeys).Distinct(StringComparer.Ordinal).ToArray();
        var known = await db.ProductVariants.AsNoTracking()
            .Where(x => x.TenantId == tenantId && (allSkuKeys.Contains(x.SkuNormalized) || x.BarcodeNormalized != null && allBarcodeKeys.Contains(x.BarcodeNormalized)))
            .Select(x => new { x.SkuNormalized, x.BarcodeNormalized })
            .ToListAsync(cancellationToken);
        var knownSkus = known.Select(x => x.SkuNormalized).ToHashSet(StringComparer.Ordinal);
        var knownBarcodes = known.Where(x => x.BarcodeNormalized != null).Select(x => x.BarcodeNormalized!).ToHashSet(StringComparer.Ordinal);
        return candidates
            .Where(x => !x.SkuKeys.Any(knownSkus.Contains) && !x.BarcodeKeys.Any(knownBarcodes.Contains))
            .Select(x => x.Remote)
            .ToList();
    }

    // A size added to an existing card starts with the pictures the editor's add rule gives a new row (family-gallery-save
    // inheritAssignments): the pictures every existing size of its colour holds as its own list, joined with those every
    // existing row of its size holds across two other colours at least, in the card gallery's order, once each. Any row of
    // the colour without a list of its own leaves nothing to share, and nothing written means the card gallery applies.
    // The rows are the family's (brand + model code) as the database holds them before this run's new sizes.
    private async Task<List<Guid>> InheritedVariantPictureAssetIds(Guid tenantId, Product product, string optionSignature, CancellationToken cancellationToken)
    {
        var (colour, size) = ColourAndSize(optionSignature);
        if (colour is null && size is null) return [];
        var modelCode = await db.ProductVariants.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ProductId == product.Id && x.ModelCode != null && x.ModelCode != "")
            .OrderBy(x => x.SortOrder).ThenBy(x => x.Id)
            .Select(x => x.ModelCode)
            .FirstOrDefaultAsync(cancellationToken);
        var familyIds = await ProductFamilyScope.ProductIdsAsync(db, tenantId, product.BrandId, modelCode, db.ProductVariants.AsNoTracking(), cancellationToken, product.FamilyId);
        if (!familyIds.Contains(product.Id)) familyIds.Add(product.Id);
        var rows = await db.ProductVariants.AsNoTracking()
            .Where(x => x.TenantId == tenantId && familyIds.Contains(x.ProductId))
            .OrderBy(x => x.ProductId == product.Id ? 0 : 1).ThenBy(x => x.ProductId).ThenBy(x => x.SortOrder).ThenBy(x => x.Id)
            .Select(x => new { x.Id, x.OptionSignature })
            .ToListAsync(cancellationToken);
        if (rows.Count == 0) return [];
        var rowIds = rows.Select(x => x.Id).ToArray();
        var media = await (from item in db.ProductMedia.AsNoTracking()
                           join asset in db.FileAssets.AsNoTracking() on new { item.TenantId, item.FileAssetId } equals new { asset.TenantId, FileAssetId = asset.Id }
                           where item.TenantId == tenantId && item.Status == "ACTIVE"
                               && (item.VariantId != null && rowIds.Contains(item.VariantId.Value) || item.ProductId == product.Id && item.VariantId == null)
                           orderby item.SortOrder, item.Id
                           select new { item.VariantId, item.FileAssetId, asset.Classification, asset.RelativePath }).ToListAsync(cancellationToken);
        string Key(Guid assetId, string classification, string path) => classification == "PRODUCT_MEDIA_URL" ? CatalogImageIdentity.Key(path) : assetId.ToString("D");
        var assetByKey = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in media) assetByKey.TryAdd(Key(item.FileAssetId, item.Classification, item.RelativePath), item.FileAssetId);
        var rank = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in media.Where(x => x.VariantId is null)) rank.TryAdd(Key(item.FileAssetId, item.Classification, item.RelativePath), rank.Count);
        var listByVariant = media.Where(x => x.VariantId is not null).GroupBy(x => x.VariantId!.Value)
            .ToDictionary(group => group.Key, group => group.Select(x => Key(x.FileAssetId, x.Classification, x.RelativePath)).Distinct(StringComparer.OrdinalIgnoreCase).ToList());
        var assignments = rows.Select(row => (Axes: ColourAndSize(row.OptionSignature), Refs: listByVariant.GetValueOrDefault(row.Id) ?? [])).ToList();

        static List<string> Common(IReadOnlyList<List<string>> lists) => lists.Count == 0
            ? []
            : [.. lists[0].Where(key => lists.Skip(1).All(other => other.Contains(key, StringComparer.OrdinalIgnoreCase)))];
        var byColour = colour is null ? [] : Common([.. assignments.Where(row => row.Axes.Colour == colour).Select(row => row.Refs)]);
        var sizeRows = size is null ? [] : assignments.Where(row => row.Axes.Size == size && row.Axes.Colour != colour).ToList();
        var bySize = sizeRows.Select(row => row.Axes.Colour).Distinct(StringComparer.Ordinal).Count() >= 2 ? Common([.. sizeRows.Select(row => row.Refs)]) : [];
        return [.. byColour.Concat(bySize)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select((key, index) => (Key: key, Index: index, Rank: rank.GetValueOrDefault(key, int.MaxValue)))
            .OrderBy(item => item.Rank).ThenBy(item => item.Index)
            .Select(item => assetByKey[item.Key])];
    }

    private static (string? Colour, string? Size) ColourAndSize(string? optionSignature)
    {
        string? colour = null, size = null;
        foreach (var pair in VariantOptionSignature.Parse(optionSignature))
        {
            if (colour is null && VariantOptionSignature.IsColorName(pair.Key)) colour = NullIfEmpty(CatalogKeyNormalizer.Key(pair.Value, 120));
            else if (size is null && VariantOptionSignature.IsSizeName(pair.Key)) size = NullIfEmpty(CatalogKeyNormalizer.Key(pair.Value, 120));
        }
        return (colour, size);
    }

    // A size's own picture list is the owner's once it is not the import's own copy of the card gallery: Trendyol keeps
    // one picture list per colour content, which the import writes to the colour's first size and to the card gallery
    // alike, so any other list on a size (assigned in the panel) is never the import's to replace or archive, whatever
    // the link's edit marks say (they are cleared by a lookup that overwrites the card).
    private async Task<bool> VariantPicturesAreOwnersAsync(Guid tenantId, Guid productId, Guid variantId, CancellationToken cancellationToken)
    {
        var rows = await db.ProductMedia.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ProductId == productId && (x.VariantId == variantId || x.VariantId == null) && x.Status == "ACTIVE")
            .OrderBy(x => x.SortOrder).ThenBy(x => x.Id)
            .Select(x => new { x.VariantId, x.FileAssetId })
            .ToListAsync(cancellationToken);
        var own = rows.Where(x => x.VariantId == variantId).Select(x => x.FileAssetId).ToList();
        if (own.Count == 0) return false;
        return !own.SequenceEqual(rows.Where(x => x.VariantId == null).Select(x => x.FileAssetId));
    }

    // Returns the panel variants it matched (by link, then stock code, then barcode inside the card).
    private async Task<List<Guid>> SyncCatalogInventoryForPreservedProduct(
        Guid tenantId,
        Guid connectionId,
        Product product,
        RemoteCatalogProduct snapshot,
        ConnectionInventoryPolicy? inventoryPolicy,
        DateTimeOffset now,
        CancellationToken cancellationToken,
        bool observeOnly = false,
        bool preferBarcode = false,
        bool updatePrices = false, bool updateStock = false,
        bool syncMedia = false)
    {
        var matched = new List<Guid>();
        var variants = await db.ProductVariants
            .Where(x => x.TenantId == tenantId && x.ProductId == product.Id)
            .ToListAsync(cancellationToken);
        if (variants.Count == 0) return matched;

        var externalIds = snapshot.Variants
            .Select(x => Short(x.ExternalVariantId, 256))
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var links = externalIds.Length == 0
            ? new List<MarketplaceVariantLink>()
            : await db.MarketplaceVariantLinks
                .AsNoTracking()
                .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && externalIds.Contains(x.ExternalId))
                .ToListAsync(cancellationToken);

        foreach (var remote in snapshot.Variants)
        {
            var externalId = Short(remote.ExternalVariantId, 256);
            var linkedVariantId = links.FirstOrDefault(x => string.Equals(x.ExternalId, externalId, StringComparison.Ordinal))?.VariantId;
            var sku = Short(string.IsNullOrWhiteSpace(remote.Sku) ? remote.Barcode ?? remote.ExternalVariantId : remote.Sku, 160);
            var skuNormalized = NormalizeCatalogKey(sku, 160);
            var barcodeNormalized = NormalizeCatalogKey(remote.Barcode, 160);
            var variant = linkedVariantId is Guid variantId
                ? variants.FirstOrDefault(x => x.Id == variantId)
                : null;
            if (preferBarcode)
            {
                if (!string.IsNullOrWhiteSpace(barcodeNormalized))
                    variant ??= variants.FirstOrDefault(x => CatalogKeyNormalizer.SameKey(x.BarcodeNormalized, barcodeNormalized));
            }
            else
            {
                variant ??= variants.FirstOrDefault(x => CatalogKeyNormalizer.SameKey(x.SkuNormalized, skuNormalized));
                if (variant is null && !string.IsNullOrWhiteSpace(barcodeNormalized))
                    variant = variants.FirstOrDefault(x => CatalogKeyNormalizer.SameKey(x.BarcodeNormalized, barcodeNormalized));
            }
            if (variant is null) continue;

            matched.Add(variant.Id);
            await UpsertCatalogOfferAndInventory(tenantId, connectionId, variant, remote, inventoryPolicy, now, cancellationToken, updateOffer: updatePrices, observeOnly: observeOnly, updateStock: updateStock, forceStockUpdate: updateStock);
            if (syncMedia && remote.ImageUrls is not null)
                await UpsertCatalogMedia(tenantId, product, variant.Id, remote.ImageUrls, $"{product.Title} · {OptionSignature(remote.Options)}", cancellationToken);
        }
        return matched;
    }

    // The focused price/stock path (ProductImportScanPolicy.FocusedPriceStock): the listing finds its panel card the way
    // UpsertCatalogProduct finds it (the product link, else the card owning its variants by link, stock code, barcode;
    // a link to a card without variants gives way to that owner too), and every variant its panel variant the way the
    // preserved-card path does (SyncCatalogInventoryForPreservedProduct: link, stock code, barcode). Only the ticked
    // prices (sale and list, with their price history) and stock (through the ledger) are written, by the same
    // UpsertCatalogOfferAndInventory; the listing's sale state was recorded when its page was read (MarkVariantLinksSeen).
    // No category context, attribute, reference, picture, content, desi, brand, category, link or local-change mark
    // changes. A size the panel lacks is not added: it is reported (PRODUCT_NEW_SIZES_NOT_IMPORTED), as a plain pull does.
    private async Task<FocusedPriceStockOutcome> ApplyFocusedPriceStock(Guid tenantId, Guid connectionId, RemoteCatalogProduct snapshot, ConnectionInventoryPolicy? inventoryPolicy, bool updatePrices, bool updateStock, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var externalProductId = Short(snapshot.ExternalProductId, 256);
        var productId = await db.MarketplaceProductLinks.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ExternalId == externalProductId)
            .Select(x => (Guid?)x.ProductId)
            .SingleOrDefaultAsync(cancellationToken);
        var product = productId is { } linkedId
            ? await db.Products.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == linkedId, cancellationToken)
            : null;
        if (product is null || !await db.ProductVariants.AnyAsync(x => x.TenantId == tenantId && x.ProductId == product.Id, cancellationToken))
        {
            var owners = await CatalogProductsOwningVariants(tenantId, connectionId, snapshot, cancellationToken);
            if (owners.Count > 1)
            {
                await RecordIssue(tenantId, $"product-sync-variant-owner-conflict:{connectionId}:{Short(externalProductId, 200)}", "PRODUCT_VARIANT_CONFLICT", Short($"Pazar yeri ürününün varyantları panelde birden fazla ürüne ait; yeni ürün açılmadı, mevcut kayıtlar korundu · {snapshot.Title}", 1000), cancellationToken);
                return FocusedPriceStockOutcome.Conflict;
            }
            product = owners.Count == 1 ? await db.Products.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == owners[0], cancellationToken) : null;
        }
        if (product is null) return FocusedPriceStockOutcome.NotInPanel;

        var pricesBefore = priceChangedVariantIds.Count;
        var stockBefore = stockChangedVariantIds.Count;
        var matched = await SyncCatalogInventoryForPreservedProduct(tenantId, connectionId, product, snapshot, inventoryPolicy, now, cancellationToken, updatePrices: updatePrices, updateStock: updateStock);
        telemetryUnchangedVariantCount += matched.Distinct().Count(id => !priceChangedVariantIds.Contains(id) && !stockChangedVariantIds.Contains(id));

        var unadded = (await RemoteVariantsMissingFromPanel(tenantId, connectionId, snapshot, cancellationToken)).Count;
        var issueKey = $"product-new-sizes:{connectionId:D}:{Short(externalProductId, 200)}";
        if (unadded > 0)
        {
            productUnaddedSizeCount = unadded;
            telemetryUnaddedSizeCount += unadded;
            await RecordIssue(tenantId, issueKey, "PRODUCT_NEW_SIZES_NOT_IMPORTED", Short($"{product.Title}: Trendyol'da panelde olmayan {UnaddedSizesNote(unadded)}", 1000), cancellationToken);
        }
        else
            await ResolveIssue(tenantId, issueKey, cancellationToken);
        return priceChangedVariantIds.Count > pricesBefore || stockChangedVariantIds.Count > stockBefore
            ? FocusedPriceStockOutcome.Changed
            : FocusedPriceStockOutcome.Unchanged;
    }

    private async Task NormalizeLegacyWebColorOptions(Guid tenantId, Guid productId, CancellationToken cancellationToken)
    {
        var options = await db.ProductOptions
            .Where(x => x.TenantId == tenantId && x.ProductId == productId)
            .OrderBy(x => x.SortOrder)
            .ThenBy(x => x.Id)
            .ToListAsync(cancellationToken);
        var legacy = options.Where(x => IsWebColorOptionKey(x.Label) || IsWebColorOptionKey(x.NormalizedKey)).ToList();
        if (legacy.Count == 0) return;

        var canonical = options.FirstOrDefault(x => IsColorOptionKey(x.Label) && !IsWebColorOptionKey(x.Label))
            ?? options.FirstOrDefault(x => string.Equals(x.NormalizedKey, NormalizeCatalogKey("Renk", 160), StringComparison.Ordinal));
        if (canonical is null)
        {
            canonical = legacy[0];
            canonical.Label = "Renk";
            canonical.NormalizedKey = NormalizeCatalogKey("Renk", 160);
            legacy.RemoveAt(0);
        }

        foreach (var duplicate in legacy)
        {
            var assignments = await db.VariantOptionValues
                .Where(x => x.TenantId == tenantId && x.OptionId == duplicate.Id)
                .ToListAsync(cancellationToken);
            db.VariantOptionValues.RemoveRange(assignments);
            var values = await db.ProductOptionValues
                .Where(x => x.TenantId == tenantId && x.OptionId == duplicate.Id)
                .ToListAsync(cancellationToken);
            db.ProductOptionValues.RemoveRange(values);
            db.ProductOptions.Remove(duplicate);
        }
    }

    private async Task<bool> CatalogSnapshotAlreadyApplied(Guid tenantId, Guid productId, RemoteCatalogProduct snapshot, CancellationToken cancellationToken)
    {
        var product = await db.Products.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == productId, cancellationToken);
        if (product is null || !string.Equals(product.Title, ProductTitle(snapshot.Title, snapshot.ExternalProductId), StringComparison.Ordinal) || !string.Equals(product.Description, snapshot.Description ?? "", StringComparison.Ordinal)) return false;
        var variantCount = await db.ProductVariants.AsNoTracking().CountAsync(x => x.TenantId == tenantId && x.ProductId == productId, cancellationToken);
        if (variantCount < snapshot.Variants.Count) return false;
        // A gallery the user manages is not compared with the remote one; the import leaves it as it is.
        if (await MediaIsUserManagedAsync(tenantId, productId, null, cancellationToken)) return true;

        var expectedUrls = CatalogImageIdentity.DistinctUrls(snapshot.ImageUrls);
        var mediaRows = await (from media in db.ProductMedia.AsNoTracking()
                               join asset in db.FileAssets.AsNoTracking()
                                   on new { media.TenantId, media.FileAssetId } equals new { asset.TenantId, FileAssetId = asset.Id }
                               where media.TenantId == tenantId
                                   && media.ProductId == productId
                                   && media.VariantId == null
                                   && media.Status == "ACTIVE"
                                   && asset.Status == "ACTIVE"
                                   && asset.ArchivedAt == null
                                   && (asset.Classification == "PRODUCT_MEDIA_URL" || asset.Classification == "PRODUCT_MEDIA")
                               orderby media.SortOrder
                               select new { media.FileAssetId, asset.Classification, asset.RelativePath }).ToListAsync(cancellationToken);
        var actualUrls = mediaRows
            .Select(row => row.Classification == "PRODUCT_MEDIA_URL"
                ? row.RelativePath
                : $"/api/v1/files/product-media/{row.FileAssetId:D}/content")
            .ToList();
        return actualUrls.SequenceEqual(expectedUrls, StringComparer.OrdinalIgnoreCase);
    }

    // The panel brand a product import gives the listing's brand (owner decision 12 Nov 2026), the way EnsureMappedCategory
    // settles its category, from what the listing carries alone (no marketplace call):
    //   1. the brand mapping on this connection for the listing's brand id (re-verified when it was not VERIFIED);
    //   2. else the panel brand the name names (PanelBrandByName, the rule the file imports share too), mapped now;
    //   3. else a new panel brand under the listing's name, mapped now (VERIFIED, Source IMPORT).
    // A mapping is written only where the marketplace has a brand list and the listing carries the brand's id
    // (byExternalId: Trendyol brand.id). Hepsiburada, n11, Pazarama, PTT AVM and Shopify listings carry a brand name only
    // (or a vendor with no list behind it), so for them the brand is matched by name in the panel and no mapping is made.
    // A panel brand the user switched off is not revived; two panel brands mapped to one marketplace brand are the user's
    // to settle; both leave the product without a brand and list the brand on the mapping page (BRAND_MAPPING_MISSING).
    // With create false (sizes added to a linked card) only an existing mapping is read and nothing is written.
    private async Task<Guid?> ImportBrandId(Guid tenantId, Guid connectionId, RemoteCatalogProduct snapshot, string platformName, bool byExternalId, bool create, CancellationToken cancellationToken)
    {
        var externalId = byExternalId && !string.IsNullOrWhiteSpace(snapshot.BrandExternalId) ? Short(snapshot.BrandExternalId, 256) : null;
        var brandName = Short(snapshot.BrandName, 160);
        var now = timeProvider.GetUtcNow();
        Task Unusable(string reason) => externalId is null
            ? Task.CompletedTask
            : RecordIssue(tenantId, ImportedBrandRule.IssueKey(connectionId, externalId), ImportedBrandRule.IssueCode, Short(ImportedBrandRule.UnusableSummary(platformName, externalId, snapshot.BrandName, reason), 1000), cancellationToken);

        if (externalId is not null)
        {
            var mappings = db.BrandMappings.Local
                .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ScopeExternalId == "" && x.ExternalId == externalId && db.Entry(x).State == EntityState.Added)
                .ToList();
            mappings.AddRange(await db.BrandMappings
                .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ScopeExternalId == "" && x.ExternalId == externalId)
                .ToListAsync(cancellationToken));
            if (mappings.Count > 0)
            {
                var localIds = mappings.Select(x => x.LocalId).Distinct().ToArray();
                var activeIds = db.Brands.Local.Where(x => x.TenantId == tenantId && localIds.Contains(x.Id) && x.IsActive).Select(x => x.Id)
                    .Concat(await db.Brands.AsNoTracking().Where(x => x.TenantId == tenantId && localIds.Contains(x.Id) && x.IsActive).Select(x => x.Id).ToListAsync(cancellationToken))
                    .Distinct().ToArray();
                if (ImportedBrandRule.Resolve(activeIds) is { } mappedId && localIds.Length == 1)
                {
                    if (create)
                        foreach (var stale in mappings.Where(x => x.Status != "VERIFIED"))
                        {
                            stale.Status = "VERIFIED";
                            stale.VerifiedAt = now;
                            stale.Version++;
                        }
                    return mappedId;
                }
                if (create) await Unusable(localIds.Length > 1 ? "birden fazla panel markasına eşli" : "eşlendiği panel markası kapalı");
                return null;
            }
        }
        if (!create) return null;

        // By name: the rule every import shares (PanelBrandByName).
        var byName = await new PanelBrandByName(db, tenantId).FindOrCreateAsync(brandName, now, cancellationToken);
        if (byName.SwitchedOff is { } switchedOff)
        {
            await Unusable($"aynı adlı panel markası «{Short(switchedOff.Name, 120)}» kapalı");
            return null;
        }
        if (byName.Brand is not { } brand) return null;
        if (externalId is null) return brand.Id;

        // A panel brand maps to one marketplace brand per connection (unique index): one already mapped to another id
        // keeps its mapping; the product still takes it by name and the brand is listed for the user to check.
        var brandId = brand.Id;
        var alreadyMapped = db.BrandMappings.Local.Any(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ScopeExternalId == "" && x.LocalId == brandId && db.Entry(x).State != EntityState.Deleted)
            || await db.BrandMappings.AnyAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ScopeExternalId == "" && x.LocalId == brandId, cancellationToken);
        if (alreadyMapped)
        {
            await Unusable($"aynı adlı panel markası «{Short(brand.Name, 120)}» bu bağlantıda başka bir markaya eşli; ürünler bu panel markasıyla aktarıldı, eşleme yazılmadı");
            return brandId;
        }
        var brandSnapshotId = await db.ReferenceSnapshots.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ResourceType == "BRANDS" && x.ScopeExternalId == "" && x.IsCurrent)
            .Select(x => (Guid?)x.Id)
            .FirstOrDefaultAsync(cancellationToken);
        db.BrandMappings.Add(new BrandMapping { Id = Guid.CreateVersion7(), TenantId = tenantId, ConnectionId = connectionId, SnapshotId = brandSnapshotId ?? Guid.Empty, LocalId = brandId, ScopeExternalId = "", ExternalId = externalId, Status = "VERIFIED", VerifiedAt = now, Source = MappingSources.Import, Version = 1 });
        await ResolveIssue(tenantId, ImportedBrandRule.IssueKey(connectionId, externalId), cancellationToken);
        return brandId;
    }

    private async Task<Category?> UpsertCatalogCategory(Guid tenantId, string? name, CancellationToken cancellationToken)
    {
        var leafName = CategoryLeafName(name, name);
        var normalized = NormalizeCatalogKey(leafName, 160);
        if (string.IsNullOrWhiteSpace(normalized)) return null;
        var legacyNormalized = NormalizeCatalogKey(name, 160);
        var keys = CatalogLookupKeys(leafName).Concat(CatalogLookupKeys(name)).Distinct(StringComparer.Ordinal).ToArray();
        var category = db.Categories.Local.FirstOrDefault(x => x.TenantId == tenantId && x.ParentId == null && (CatalogKeyNormalizer.SameKey(x.NormalizedName, normalized) || CatalogKeyNormalizer.SameKey(x.NormalizedName, legacyNormalized)))
            ?? await db.Categories.Where(x => x.TenantId == tenantId && x.ParentId == null && keys.Contains(x.NormalizedName)).OrderBy(x => x.NormalizedName == normalized ? 0 : 1).ThenBy(x => x.Id).FirstOrDefaultAsync(cancellationToken);
        if (category is not null)
        {
            // A panel category keeps its name, path and state. Its leaf flag follows its children: a category
            // the user split into sub-categories is no longer a leaf and cannot take products.
            if (!category.IsActive) return null;
            var isLeaf = !await db.Categories.AnyAsync(x => x.TenantId == tenantId && x.ParentId == category.Id && x.IsActive, cancellationToken);
            if (category.IsLeaf != isLeaf) { category.IsLeaf = isLeaf; category.UpdatedAt = timeProvider.GetUtcNow(); category.Version++; }
            return isLeaf ? category : null;
        }
        category = new Category { Id = Guid.CreateVersion7(), TenantId = tenantId, Name = leafName, NormalizedName = normalized, Path = leafName, Depth = 0, IsLeaf = true, IsActive = true, CreatedAt = timeProvider.GetUtcNow(), UpdatedAt = timeProvider.GetUtcNow(), Version = 1 };
        db.Categories.Add(category);
        return category;
    }

    // Fills the desi of the variants linked to this snapshot's variants on this connection, only where the local desi
    // is empty. Nothing else of the product or variant changes, and a desi already set is never overwritten.
    private async Task FillMissingImportedDesi(Guid tenantId, Guid connectionId, RemoteCatalogProduct snapshot, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var desiByExternalId = snapshot.Variants
            .Where(variant => variant.Desi is not null && !string.IsNullOrWhiteSpace(variant.ExternalVariantId))
            .GroupBy(variant => Short(variant.ExternalVariantId, 256), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Desi!.Value, StringComparer.Ordinal);
        if (desiByExternalId.Count == 0) return;
        var externalIds = desiByExternalId.Keys.ToArray();
        var targets = await (from variantLink in db.MarketplaceVariantLinks
                             join variant in db.ProductVariants on new { variantLink.TenantId, variantLink.VariantId } equals new { variant.TenantId, VariantId = variant.Id }
                             where variantLink.TenantId == tenantId && variantLink.ConnectionId == connectionId
                                 && externalIds.Contains(variantLink.ExternalId) && variant.Desi == null
                             select new { variantLink.ExternalId, Variant = variant }).ToListAsync(cancellationToken);
        foreach (var target in targets)
        {
            if (target.Variant.Desi is not null || !desiByExternalId.TryGetValue(target.ExternalId, out var desi)) continue;
            target.Variant.Desi = desi;
            target.Variant.UpdatedAt = now;
            target.Variant.Version++;
            telemetryUpdatedCount++;
        }
    }

    private async Task<ProductVariant?> UpsertCatalogVariant(Guid tenantId, Guid connectionId, Product product, RemoteCatalogVariant remote, int sortOrder, CategoryAttributeContext? categoryContext, ConnectionInventoryPolicy? inventoryPolicy, DateTimeOffset now, CancellationToken cancellationToken, bool observeOnly = false, bool preferBarcode = false, bool onlyExistingVariants = false, bool updatePrices = false, bool updateStock = false)
    {
        var sku = Short(string.IsNullOrWhiteSpace(remote.Sku) ? remote.Barcode ?? remote.ExternalVariantId : remote.Sku, 160);
        var skuNormalized = NormalizeCatalogKey(sku, 160);
        if (string.IsNullOrWhiteSpace(skuNormalized)) return null;
        var barcode = string.IsNullOrWhiteSpace(remote.Barcode) ? null : Short(remote.Barcode.Trim(), 160);
        // A variant without a barcode stores NULL: "" would collide under the unique (tenant, barcode) index
        // with the next barcode-less variant (K14).
        var barcodeNormalized = NullIfEmpty(NormalizeCatalogKey(barcode, 160));
        var skuKeys = CatalogLookupKeys(sku);
        var barcodeKeys = CatalogLookupKeys(barcode);
        var externalVariantId = Short(remote.ExternalVariantId, 256);
        var optionSignature = categoryContext is null ? OptionSignature(remote.Options) : await PanelOptionSignatureAsync(tenantId, connectionId, categoryContext, remote.Options, cancellationToken);
        var link = await db.MarketplaceVariantLinks.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ExternalId == externalVariantId, cancellationToken);
        ProductVariant? variant = null;
        if (link is not null)
        {
            var linkedVariant = await db.ProductVariants.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == link.VariantId, cancellationToken);
            // A stale Shopify variant link must not override a barcode match
            // inside the resolved local parent product.
            if (linkedVariant is not null
                && linkedVariant.ProductId == product.Id
                && (!preferBarcode || CatalogKeyNormalizer.SameKey(linkedVariant.BarcodeNormalized, barcodeNormalized) || string.IsNullOrEmpty(linkedVariant.BarcodeNormalized) && barcodeNormalized is null))
                variant = linkedVariant;
        }
        // Lookups accept both stored key forms and prefer the shared key, so a legacy row and a recomputed
        // row of the same code never make a single-row lookup throw.
        if (preferBarcode && barcodeNormalized is not null)
            variant ??= db.ProductVariants.Local.FirstOrDefault(x => x.TenantId == tenantId && x.ProductId == product.Id && CatalogKeyNormalizer.SameKey(x.BarcodeNormalized, barcodeNormalized))
                ?? await db.ProductVariants.Where(x => x.TenantId == tenantId && x.ProductId == product.Id && x.BarcodeNormalized != null && barcodeKeys.Contains(x.BarcodeNormalized)).OrderBy(x => x.BarcodeNormalized == barcodeNormalized ? 0 : 1).ThenBy(x => x.Id).FirstOrDefaultAsync(cancellationToken);
        if (preferBarcode && onlyExistingVariants && variant is null && barcodeNormalized is not null)
            variant = db.ProductVariants.Local.FirstOrDefault(x => x.TenantId == tenantId && CatalogKeyNormalizer.SameKey(x.BarcodeNormalized, barcodeNormalized))
                ?? await db.ProductVariants.Where(x => x.TenantId == tenantId && x.BarcodeNormalized != null && barcodeKeys.Contains(x.BarcodeNormalized)).OrderBy(x => x.BarcodeNormalized == barcodeNormalized ? 0 : 1).ThenBy(x => x.Id).FirstOrDefaultAsync(cancellationToken);
        if (!preferBarcode)
            variant ??= db.ProductVariants.Local.FirstOrDefault(x => x.TenantId == tenantId && x.ProductId == product.Id && CatalogKeyNormalizer.SameKey(x.SkuNormalized, skuNormalized))
                ?? await db.ProductVariants.Where(x => x.TenantId == tenantId && skuKeys.Contains(x.SkuNormalized)).OrderBy(x => x.ProductId == product.Id ? 0 : 1).ThenBy(x => x.SkuNormalized == skuNormalized ? 0 : 1).ThenBy(x => x.Id).FirstOrDefaultAsync(cancellationToken);
        if (variant is not null && variant.ProductId != product.Id && !(preferBarcode && onlyExistingVariants))
        {
            await RecordIssue(tenantId, $"product-sync-variant-conflict:{connectionId}:{externalVariantId}", "PRODUCT_VARIANT_CONFLICT", "Pazar yeri varyantı başka bir yerel üründe kullanılan stok koduyla eşleşti; mevcut kayıt korunarak atlandı.", cancellationToken);
            return null;
        }
        if (!preferBarcode && variant is null && barcodeNormalized is not null)
            variant = await db.ProductVariants.Where(x => x.TenantId == tenantId && x.ProductId == product.Id && x.BarcodeNormalized != null && barcodeKeys.Contains(x.BarcodeNormalized)).OrderBy(x => x.BarcodeNormalized == barcodeNormalized ? 0 : 1).ThenBy(x => x.Id).FirstOrDefaultAsync(cancellationToken);
        if (variant is null && onlyExistingVariants)
            return null;

        var isNewVariant = variant is null;
        if (variant is null)
        {
            variant = new ProductVariant { Id = Guid.CreateVersion7(), TenantId = tenantId, ProductId = product.Id, SortOrder = sortOrder, Sku = sku, SkuNormalized = skuNormalized, Barcode = barcode, BarcodeNormalized = barcodeNormalized, ModelCode = Short(remote.ModelCode, 160), OptionSignature = optionSignature, Status = remote.Archived ? ProductStatus.Archived : ProductStatus.Active, CreatedAt = now, UpdatedAt = now, Version = 1 };
            db.ProductVariants.Add(variant);
            telemetryInsertedCount++;
        }
        else
        {
            var nextModelCode = preferBarcode ? null : Short(remote.ModelCode, 160);
            // C-9: the marketplace changing this variant's model code to one a card it does not know holds (no link on
            // this connection) would merge two products into one family; the panel keeps its code and says why.
            if (!observeOnly && ModelCodeUniquenessPolicy.ChangedCodes([(variant.ModelCode, nextModelCode)]).Count > 0
                && await ModelCodeOwnership.FindUnlinkedHolderAsync(db, tenantId, connectionId, nextModelCode!, product.Id, cancellationToken) is { } modelCodeHolder)
            {
                await RecordIssue(tenantId, $"product-sync-model-code-conflict:{connectionId}:{variant.Id:N}", ModelCodeUniquenessPolicy.ChangeConflictCode,
                    $"Pazaryerindeki yeni model kodu ({nextModelCode}) '{modelCodeHolder.Title}' ürününde kullanılıyor; iki ürün birleşmesin diye {variant.Sku} varyantının model kodu değiştirilmedi.", cancellationToken);
                nextModelCode = variant.ModelCode;
            }
            // A stored key in the legacy form that names the same code is kept until the data recompute, so
            // rewriting it can never collide with another row under the unique index. "" becomes NULL.
            var nextSkuNormalized = CatalogKeyNormalizer.SameKey(variant.SkuNormalized, skuNormalized) ? variant.SkuNormalized : skuNormalized;
            var nextBarcodeNormalized = CatalogKeyNormalizer.SameKey(variant.BarcodeNormalized, barcodeNormalized) ? variant.BarcodeNormalized : barcodeNormalized;
            if (!observeOnly && (variant.SortOrder != sortOrder || variant.Sku != sku || variant.SkuNormalized != nextSkuNormalized || variant.Barcode != barcode || variant.BarcodeNormalized != nextBarcodeNormalized || variant.ModelCode != nextModelCode || variant.OptionSignature != optionSignature))
            {
                // The variant's own status is the user's (report item 19); the listing's archived/closed state is kept
                // on its link (SaleState) and its offer, never copied onto the panel variant.
                variant.SortOrder = sortOrder; variant.Sku = sku; variant.SkuNormalized = nextSkuNormalized; variant.Barcode = barcode; variant.BarcodeNormalized = nextBarcodeNormalized; variant.ModelCode = nextModelCode; variant.OptionSignature = optionSignature; variant.UpdatedAt = now; variant.Version++;
                telemetryUpdatedCount++;
            }
        }
        // The marketplace desi fills an empty local desi (also for observed catalogs: it only adds a value). A desi
        // the person entered or an earlier import set is never overwritten.
        if (variant.Desi is null && remote.Desi is { } remoteDesi)
        {
            variant.Desi = remoteDesi;
            if (!isNewVariant) { variant.UpdatedAt = now; variant.Version++; telemetryUpdatedCount++; }
        }
        if (link is not null && link.VariantId != variant.Id)
        {
            var conflictingVariantLink = db.MarketplaceVariantLinks.Local.FirstOrDefault(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.VariantId == variant.Id && x.Id != link.Id)
                ?? await db.MarketplaceVariantLinks.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.VariantId == variant.Id && x.Id != link.Id, cancellationToken);
            if (conflictingVariantLink is not null)
            {
                await RecordIssue(tenantId, $"product-sync-variant-link-conflict:{connectionId}:{externalVariantId}", "PRODUCT_VARIANT_LINK_CONFLICT", "Aynı yerel varyantın başka bir Shopify varyant linki zaten var; ikinci link güvenli biçimde atlandı.", cancellationToken);
                return null;
            }
            link.VariantId = variant.Id;
            link.Version++;
        }
        if (link is null)
        {
            var existingVariantLink = db.MarketplaceVariantLinks.Local.FirstOrDefault(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.VariantId == variant.Id)
                ?? await db.MarketplaceVariantLinks.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.VariantId == variant.Id, cancellationToken);
            if (existingVariantLink is null)
            {
                var importedLink = new MarketplaceVariantLink { Id = Guid.CreateVersion7(), TenantId = tenantId, ConnectionId = connectionId, VariantId = variant.Id, ExternalId = externalVariantId, Version = 1 };
                ListingSaleStateReads.ApplyToNewLink(importedLink, remote.SaleState, remote.SaleStateReason, now);
                db.MarketplaceVariantLinks.Add(importedLink);
                telemetryInsertedCount++;
            }
            // A link written for a panel publication before the marketplace named the variant (a "listing:" placeholder)
            // takes the variant id the import read, unless another link already holds that id.
            else if (MarketplaceVariantLinkHealth.IsListingPlaceholder(existingVariantLink.ExternalId)
                && !await ExternalIdTaken(tenantId, connectionId, externalVariantId, existingVariantLink.Id, cancellationToken))
            {
                existingVariantLink.ExternalId = externalVariantId;
                existingVariantLink.Version++;
            }
            else if (!string.Equals(existingVariantLink.ExternalId, externalVariantId, StringComparison.Ordinal))
            {
                await RecordIssue(tenantId, $"product-sync-variant-link-conflict:{connectionId}:{externalVariantId}", "PRODUCT_VARIANT_LINK_CONFLICT", "Aynı yerel varyantın başka bir pazar yeri varyant bağlantısı zaten var; ikinci bağlantı güvenli biçimde atlandı.", cancellationToken);
                return null;
            }
        }
        if (!observeOnly || isNewVariant)
            await UpsertCatalogOptions(tenantId, connectionId, product.Id, variant.Id, remote.Options, categoryContext, cancellationToken);
        if ((!observeOnly || isNewVariant) && categoryContext is not null)
            await UpsertProductAttributeAssignments(tenantId, connectionId, product, variant, remote.Options, categoryContext, cancellationToken);
        await UpsertCatalogOfferAndInventory(tenantId, connectionId, variant, remote, inventoryPolicy, now, cancellationToken, updateOffer: isNewVariant || updatePrices, observeOnly: observeOnly, updateStock: isNewVariant || updateStock, forceStockUpdate: !isNewVariant && updateStock);
        if ((!observeOnly || isNewVariant) && remote.ImageUrls is not null)
            await UpsertCatalogMedia(tenantId, product, variant.Id, remote.ImageUrls, $"{product.Title} · {optionSignature}", cancellationToken);
        // A Trendyol size added to a card the panel already holds, without a list of its own from Trendyol (any size after
        // its colour's first), starts with the editor's add rule (InheritedVariantPictureAssetIds).
        if (isNewVariant && !observeOnly && db.Entry(product).State != EntityState.Added && remote.ImageUrls is not { Count: > 0 })
        {
            var altText = Short($"{product.Title} · {optionSignature}", 320);
            foreach (var (assetId, index) in (await InheritedVariantPictureAssetIds(tenantId, product, optionSignature, cancellationToken)).Select((assetId, index) => (assetId, index)))
                db.ProductMedia.Add(new ProductMedia { Id = Guid.CreateVersion7(), TenantId = tenantId, ProductId = product.Id, VariantId = variant.Id, FileAssetId = assetId, MediaRole = index == 0 ? "PRIMARY" : "GALLERY", SortOrder = index, AltText = altText, Status = "ACTIVE" });
        }
        return variant;
    }

    private static RemoteCatalogProduct MergeCatalogSnapshots(IEnumerable<RemoteCatalogProduct> source)
    {
        var snapshots = source.ToList();
        var first = snapshots[0];
        var variants = snapshots
            .SelectMany(snapshot => snapshot.Variants)
            .GroupBy(VariantMergeKey, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var entries = group.ToList();
                var last = entries[^1];
                var hasImagePayload = entries.Any(entry => entry.ImageUrls is not null);
                var imageUrls = CatalogImageIdentity.DistinctUrls(entries
                    .Where(entry => entry.ImageUrls is not null)
                    .SelectMany(entry => entry.ImageUrls!));
                return last with { ImageUrls = hasImagePayload ? imageUrls : null };
            })
            .ToList();
        var images = CatalogImageIdentity.DistinctUrls(
            snapshots.SelectMany(snapshot => snapshot.ImageUrls.Concat(snapshot.Variants.SelectMany(variant => variant.ImageUrls ?? []))));
        return first with
        {
            ProductMainId = snapshots.Select(x => x.ProductMainId).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)),
            Title = snapshots.Select(x => x.Title).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? first.Title,
            Description = snapshots.Select(x => x.Description).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? first.Description,
            BrandExternalId = snapshots.Select(x => x.BrandExternalId).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)),
            BrandName = snapshots.Select(x => x.BrandName).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)),
            CategoryExternalId = snapshots.Select(x => x.CategoryExternalId).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)),
            CategoryName = snapshots.Select(x => x.CategoryName).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)),
            ImageUrls = images,
            Variants = variants,
            RawJson = snapshots[^1].RawJson
        };
    }

    private static string VariantMergeKey(RemoteCatalogVariant variant) =>
        !string.IsNullOrWhiteSpace(variant.ExternalVariantId)
            ? $"id:{variant.ExternalVariantId}"
            : $"sku:{NormalizeCatalogKey(variant.Sku, 160)}";

    private async Task UpsertCatalogOfferAndInventory(Guid tenantId, Guid connectionId, ProductVariant variant, RemoteCatalogVariant remote, ConnectionInventoryPolicy? inventoryPolicy, DateTimeOffset now, CancellationToken cancellationToken, bool updateOffer = true, bool observeOnly = false, bool updateStock = true, bool forceStockUpdate = false)
    {
        if (observeOnly)
        {
            await UpsertShopifyInventoryObservations(tenantId, connectionId, variant.Id, remote, now, cancellationToken);
        }
        if (updateStock && remote.StockQuantity is decimal stockQuantity)
        {
            var observedRemoteQuantity = decimal.Round(Math.Max(0m, stockQuantity), 4, MidpointRounding.ToEven);
            var applyRemoteQuantityToOnHand = InventoryAuthorityPolicy.ShouldApplyRemoteQuantityToOnHand(inventoryPolicy?.AuthorityMode);
            var inventory = db.InventoryItems.Local.FirstOrDefault(x => x.TenantId == tenantId && x.VariantId == variant.Id && x.LocationCode == "MAIN")
                ?? await db.InventoryItems.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.VariantId == variant.Id && x.LocationCode == "MAIN", cancellationToken);
            if (inventory is null)
            {
                var opening = new InventoryItem
                {
                    Id = Guid.CreateVersion7(),
                    TenantId = tenantId,
                    VariantId = variant.Id,
                    LocationCode = "MAIN",
                    // The first catalog snapshot initializes an untouched
                    // inventory row. Subsequent writes still require the
                    // explicit remote-authoritative mode.
                    OnHand = observeOnly && !forceStockUpdate ? 0 : observedRemoteQuantity,
                    Reserved = 0,
                    Available = observeOnly && !forceStockUpdate ? 0 : observedRemoteQuantity,
                    ObservedRemoteQuantity = observedRemoteQuantity,
                    ObservedRemoteAt = now,
                    ReconciledAt = now,
                    ProjectionVersion = 1,
                    Version = 1
                };
                db.InventoryItems.Add(opening);
                // The opening stock is a stock move like any other, so Ürün Hareketleri shows where the first number
                // came from. It is not an on-hand movement for MarketplaceStockImportPolicy (it is the remote number).
                if (opening.OnHand != 0)
                {
                    stockChangedVariantIds.Add(variant.Id);
                    var openingEventId = $"catalog-opening:{connectionId:N}:{opening.Id:N}";
                    StockMovementWriter.Record(db, tenantId, opening.Id, opening.OnHand, CatalogStockSource(ProductActivityTypes.ImportOpening, connectionId, openingEventId, now, now));
                }
            }
            else
            {
                var reconciliationWasMissing = inventory.ReconciledAt is null;
                var seedInitialOnHand = InventoryAuthorityPolicy.ShouldSeedInitialOnHand(inventory);
                var applyQuantityToOnHand = (!observeOnly || forceStockUpdate) && (forceStockUpdate || applyRemoteQuantityToOnHand || seedInitialOnHand);
                var targetOnHand = applyQuantityToOnHand ? await MarketplaceStockTargetAsync(tenantId, inventory, observedRemoteQuantity, now, cancellationToken) : inventory.OnHand;
                var projectionChanged = applyQuantityToOnHand && inventory.OnHand != targetOnHand;
                var observationChanged = inventory.ObservedRemoteQuantity != observedRemoteQuantity;
                if (projectionChanged)
                {
                    stockChangedVariantIds.Add(variant.Id);
                    // K-2: the change goes through the stock ledger and reaches every connection selling the variant,
                    // like any other stock movement.
                    // The row is keyed by the projection version the move gives the item; the move bumps the row version.
                    var eventId = $"catalog-stock:{connectionId:N}:{inventory.Id:N}:v{inventory.ProjectionVersion + 1}";
                    var change = StockMovementWriter.SetOnHand(db, inventory, targetOnHand, CatalogStockSource(MarketplaceStockImportPolicy.MovementType, connectionId, eventId, MarketplaceStockReadAt(now), now));
                    await StockProjectionOutbox.EnqueueAsync(db, tenantId, [change], now, cancellationToken);
                }
                inventory.ObservedRemoteQuantity = observedRemoteQuantity;
                inventory.ObservedRemoteAt = now;
                inventory.ReconciledAt = now;
                if (!projectionChanged && (observationChanged || reconciliationWasMissing))
                    inventory.Version++;
            }
        }

        if (!updateOffer) return;
        if (remote.SalePrice is null && remote.ListPrice is null) return;
        var salePrice = decimal.Round(Math.Max(0m, remote.SalePrice ?? remote.ListPrice ?? 0m), 4, MidpointRounding.ToEven);
        var listPrice = decimal.Round(Math.Max(salePrice, Math.Max(0m, remote.ListPrice ?? salePrice)), 4, MidpointRounding.ToEven);
        var currency = NormalizeCurrency(remote.Currency);
        var status = remote.Archived ? "INACTIVE" : "ACTIVE";
        var offer = db.ChannelOffers.Local.FirstOrDefault(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.VariantId == variant.Id)
            ?? await db.ChannelOffers.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.VariantId == variant.Id, cancellationToken);
        var cardVatRate = offer is null && remote.VatRate is null
            ? db.Products.Local.FirstOrDefault(x => x.Id == variant.ProductId)?.VatRate
                ?? await db.Products.AsNoTracking().Where(x => x.TenantId == tenantId && x.Id == variant.ProductId).Select(x => x.VatRate).SingleOrDefaultAsync(cancellationToken)
            : null;
        var (vatRate, vatUnknown) = CatalogOfferVatRate(remote.VatRate, offer?.VatRate, cardVatRate);
        var observationSource = CatalogImportSource(await ConnectionPlatformCode(tenantId, connectionId, cancellationToken), observeOnly);
        if (offer is null)
        {
            if (vatUnknown) await RecordOfferVatUnknownAsync(tenantId, connectionId, cancellationToken);
            offer = new ChannelOffer
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenantId,
                ConnectionId = connectionId,
                VariantId = variant.Id,
                ListPrice = listPrice,
                SalePrice = salePrice,
                Currency = currency,
                VatRate = vatRate,
                VatInclusion = "INCLUDED",
                RoundingMode = "HALF_EVEN",
                SafetyStock = decimal.Round(Math.Max(0m, inventoryPolicy?.DefaultSafetyStock ?? 0m), 4, MidpointRounding.ToEven),
                Status = status,
                PriceVersion = 1,
                Version = 1
            };
            // The price came from the marketplace, so it counts as already sent there.
            offer.LastPriceHash = PriceInventoryComposer.StandardPriceHash(offer);
            db.ChannelOffers.Add(offer);
            priceChangedVariantIds.Add(variant.Id);
            db.ChannelPriceHistory.Add(new ChannelPriceHistory
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenantId,
                OfferId = offer.Id,
                PriceVersion = offer.PriceVersion,
                ListPrice = offer.ListPrice,
                SalePrice = offer.SalePrice,
                Currency = offer.Currency,
                Reason = $"{observationSource}_CATALOG_IMPORT",
                ActorSource = $"SYSTEM:{observationSource}",
                EffectiveAt = now
            });
            return;
        }

        // The marketplace reports VAT-inclusive prices; an offer kept without VAT stores the net
        // amount that grosses up to the same price again.
        salePrice = ChannelOfferPricePolicy.StoredFromGross(salePrice, vatRate, offer.VatInclusion, offer.RoundingMode);
        listPrice = Math.Max(salePrice, ChannelOfferPricePolicy.StoredFromGross(listPrice, vatRate, offer.VatInclusion, offer.RoundingMode));
        var priceChanged = offer.ListPrice != listPrice || offer.SalePrice != salePrice || !string.Equals(offer.Currency, currency, StringComparison.OrdinalIgnoreCase) || offer.VatRate != vatRate;
        var statusChanged = offer.Status != status;
        if (!priceChanged && !statusChanged) return;
        offer.ListPrice = listPrice;
        offer.SalePrice = salePrice;
        offer.Currency = currency;
        offer.VatRate = vatRate;
        offer.Status = status;
        offer.Version++;
        if (priceChanged)
        {
            priceChangedVariantIds.Add(variant.Id);
            offer.PriceVersion++;
            // The new price is the one the marketplace already holds; without this marker the next
            // price-and-stock send would push it back as a local change.
            offer.LastPriceHash = PriceInventoryComposer.StandardPriceHash(offer);
            db.ChannelPriceHistory.Add(new ChannelPriceHistory
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenantId,
                OfferId = offer.Id,
                PriceVersion = offer.PriceVersion,
                ListPrice = offer.ListPrice,
                SalePrice = offer.SalePrice,
                Currency = offer.Currency,
                Reason = $"{observationSource}_CATALOG_IMPORT",
                ActorSource = $"SYSTEM:{observationSource}",
                EffectiveAt = now
            });
        }
    }

    // The source a catalog import writes on the price history (Reason "<source>_CATALOG_IMPORT", ActorSource
    // "SYSTEM:<source>"): the connection's marketplace (ISLEYICI-3, 15 Nov 2026). Before, every observe-only import
    // (Hepsiburada, N11, Pazarama, PTT AVM too) was written as SHOPIFY; the old guess stays only for a connection not found.
    internal static string CatalogImportSource(string? platformCode, bool observeOnly) =>
        string.IsNullOrWhiteSpace(platformCode) ? observeOnly ? "SHOPIFY" : "TRENDYOL" : platformCode.Trim().ToUpperInvariant();

    // The VAT a catalog import gives an offer (ISLEYICI-2, 15 Nov 2026). A marketplace that does not report it (Shopify
    // never does; a Hepsiburada listing without vatRate) leaves an existing offer's VAT as it was, and a new offer takes the
    // card's VAT; 0 only when neither is known, and Unknown says so. Before, a missing VAT was written as 0.
    internal static (decimal Rate, bool Unknown) CatalogOfferVatRate(decimal? remote, decimal? offer, decimal? card)
    {
        if (remote is { } remoteRate) return (decimal.Round(Math.Max(0m, remoteRate), 4, MidpointRounding.ToEven), false);
        if (offer is { } offerRate) return (offerRate, false);
        if (card is { } cardRate) return (decimal.Round(Math.Max(0m, cardRate), 4, MidpointRounding.ToEven), false);
        return (0m, true);
    }

    private Task RecordOfferVatUnknownAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) =>
        RecordIssue(tenantId, $"offer-vat-unknown:{connectionId:N}", "OFFER_VAT_UNKNOWN",
            "Pazar yeri ürün aktarımında KDV oranı bildirmedi ve ürün kartında KDV yok; yeni açılan tekliflerin KDV oranı %0 yazıldı. Ürün kartlarının ve tekliflerin KDV oranını kontrol edin.", cancellationToken);

    // The VAT of an offer the raw import / mapping path opens (SeedChannelOfferAsync): the same rule as the catalog import
    // (CatalogOfferVatRate) — the marketplace's value, else the product card's VAT, else 0 with the single
    // OFFER_VAT_UNKNOWN issue of the connection (ISLEYICI-2).
    internal async Task<decimal> NewImportedOfferVatRateAsync(Guid tenantId, Guid connectionId, Guid variantId, decimal? remoteVatRate, CancellationToken cancellationToken)
    {
        decimal? cardVatRate = null;
        if (remoteVatRate is null)
        {
            var productId = db.ProductVariants.Local.FirstOrDefault(x => x.Id == variantId)?.ProductId
                ?? await db.ProductVariants.AsNoTracking().Where(x => x.TenantId == tenantId && x.Id == variantId).Select(x => (Guid?)x.ProductId).SingleOrDefaultAsync(cancellationToken);
            if (productId is { } cardId)
                cardVatRate = db.Products.Local.FirstOrDefault(x => x.Id == cardId)?.VatRate
                    ?? await db.Products.AsNoTracking().Where(x => x.TenantId == tenantId && x.Id == cardId).Select(x => x.VatRate).SingleOrDefaultAsync(cancellationToken);
        }
        var (vatRate, vatUnknown) = CatalogOfferVatRate(remoteVatRate, null, cardVatRate);
        if (vatUnknown) await RecordOfferVatUnknownAsync(tenantId, connectionId, cancellationToken);
        return vatRate;
    }

    // When the marketplace stock being written was read: the earliest page read of the model group being finalized,
    // else the write itself. An earlier moment is the safe side (more panel movements count as after the read).
    private DateTimeOffset MarketplaceStockReadAt(DateTimeOffset now) =>
        catalogStockReadAt is { } readAt && readAt < now ? readAt.ToUniversalTime() : now;

    private async Task<decimal> MarketplaceStockTargetAsync(Guid tenantId, InventoryItem inventory, decimal remoteQuantity, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var readAt = MarketplaceStockReadAt(now);
        var orderReserved = await db.StockReservations.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.InventoryItemId == inventory.Id && x.SourceType == "ORDER_LINE" && x.Status == ReservationStatus.Active)
            .SumAsync(x => (decimal?)x.Quantity, cancellationToken) ?? 0m;
        var afterRead = await db.StockLedgerEntries.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.InventoryItemId == inventory.Id && x.RecordedAt > readAt
                && (MarketplaceStockImportPolicy.OnHandMovementTypes.Contains(x.MovementType) || MarketplaceStockImportPolicy.ReservationMovementTypes.Contains(x.MovementType)))
            .GroupBy(x => MarketplaceStockImportPolicy.ReservationMovementTypes.Contains(x.MovementType))
            .Select(group => new { Reservation = group.Key, Delta = group.Sum(x => x.QuantityDelta) })
            .ToListAsync(cancellationToken);
        return MarketplaceStockImportPolicy.TargetOnHand(
            remoteQuantity,
            orderReserved,
            afterRead.Where(x => x.Reservation).Sum(x => x.Delta),
            afterRead.Where(x => !x.Reservation).Sum(x => x.Delta));
    }

    private async Task UpsertShopifyInventoryObservations(Guid tenantId, Guid connectionId, Guid variantId, RemoteCatalogVariant remote, DateTimeOffset observedAt, CancellationToken cancellationToken)
    {
        var levels = remote.InventoryLevels is { Count: > 0 }
            ? remote.InventoryLevels
            : remote.StockQuantity is decimal aggregate
                ? [new RemoteInventoryLevel("__aggregate__", null, aggregate, remote.RawJson)]
                : [];
        if (levels.Count == 0) return;

        var mappings = await db.ConnectionLocationMappings.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.Status == "ACTIVE")
            .ToListAsync(cancellationToken);

        foreach (var level in levels)
        {
            var externalLocationId = Short(level.ExternalLocationId, 256);
            var mapping = mappings.FirstOrDefault(x => string.Equals(Short(x.ExternalLocationId, 256), externalLocationId, StringComparison.OrdinalIgnoreCase));
            var quantity = decimal.Round(Math.Max(0m, level.Quantity), 4, MidpointRounding.ToEven);
            var observation = db.ChannelInventoryObservations.Local.FirstOrDefault(x =>
                    x.TenantId == tenantId
                    && x.ConnectionId == connectionId
                    && x.VariantId == variantId
                    && string.Equals(x.ExternalLocationId, externalLocationId, StringComparison.Ordinal))
                ?? await db.ChannelInventoryObservations.SingleOrDefaultAsync(x =>
                    x.TenantId == tenantId
                    && x.ConnectionId == connectionId
                    && x.VariantId == variantId
                    && x.ExternalLocationId == externalLocationId,
                    cancellationToken);

            if (observation is null)
            {
                db.ChannelInventoryObservations.Add(new ChannelInventoryObservation
                {
                    Id = Guid.CreateVersion7(),
                    TenantId = tenantId,
                    ConnectionId = connectionId,
                    VariantId = variantId,
                    LocationId = mapping?.LocationId,
                    ExternalLocationId = externalLocationId,
                    Quantity = quantity,
                    ObservedAt = observedAt,
                    Version = 1
                });
                continue;
            }

            if (observation.LocationId != mapping?.LocationId || observation.Quantity != quantity || observation.ObservedAt != observedAt)
            {
                observation.LocationId = mapping?.LocationId;
                observation.Quantity = quantity;
                observation.ObservedAt = observedAt;
                observation.Version++;
            }
        }
    }

    private static string NormalizeCurrency(string? value)
    {
        var currency = value?.Trim().ToUpperInvariant();
        return currency is { Length: 3 } && currency.All(character => character is >= 'A' and <= 'Z') ? currency : "TRY";
    }

    private async Task<string> PanelOptionSignatureAsync(Guid tenantId, Guid connectionId, CategoryAttributeContext categoryContext, IReadOnlyDictionary<string, string> options, CancellationToken cancellationToken)
    {
        var hasRealColorSource = options.Keys.Any(IsRealColorOptionKey);
        var candidates = new List<(string PanelLabel, bool IsWebColorSource, LocalCategoryAttribute? Mapped, string RemoteValue, int Order)>();
        var order = 0;
        foreach (var pair in options)
        {
            // Web Color is a category attribute derived from the real Renk
            // slicer. It must never create a second product option axis.
            if (IsWebColorOptionKey(pair.Key))
            {
                order++;
                continue;
            }
            if (TryGetMappedAttribute(categoryContext.Attributes, pair.Key, out var mapped) && IsCatalogProductOption(mapped, pair.Key))
            {
                var panelLabel = mapped.Definition.Name;
                var isWebColorSource = IsWebColorOptionKey(pair.Key) || IsWebColorOptionKey(mapped.Remote.Name);
                if (isWebColorSource && hasRealColorSource)
                {
                    order++;
                    continue;
                }
                candidates.Add((panelLabel, isWebColorSource, mapped, pair.Value, order));
            }
            else if (IsVariantOptionName(pair.Key) && !IsWebColorOptionKey(pair.Key))
            {
                // A missing Renk mapping must not make the importer fall back
                // to Web Color. Preserve the real slicer value under the
                // canonical local option name until it is mapped explicitly.
                candidates.Add((IsColorOptionKey(pair.Key) ? "Renk" : pair.Key, false, null, pair.Value, order));
            }
            order++;
        }

        var panelOptions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in candidates.GroupBy(candidate => NormalizeCatalogKey(candidate.PanelLabel, 160), StringComparer.Ordinal))
        {
            // Trendyol can expose both the slicer value (Renk: Tavşanlı) and
            // the marketplace display value (Web Color: Çok Renkli). They
            // map to the same panel option, so the real Renk value must win
            // regardless of the source dictionary order.
            var selected = group
                .OrderBy(candidate => candidate.IsWebColorSource ? 1 : 0)
                .ThenBy(candidate => candidate.Order)
                .First();
            // Product option names must reflect the actual slicer color. The
            // mapped category color can be a broader display value (for
            // example, Renk=İndigo alongside the category value Renk=Lacivert).
            panelOptions[selected.PanelLabel] = IsRealColorOptionKey(selected.PanelLabel) || selected.Mapped is null
                ? CleanCatalogOptionValue(selected.RemoteValue)
                : await PanelOptionValueAsync(tenantId, connectionId, categoryContext, selected.Mapped, selected.RemoteValue, cancellationToken);
        }
        if (panelOptions.Count > 0) return OptionSignature(panelOptions);
        var fallbackOptions = options.Where(pair => IsVariantOptionName(pair.Key) && !IsWebColorOptionKey(pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        return OptionSignature(fallbackOptions);
    }

    private static bool IsWebColorOptionKey(string value)
    {
        var normalized = NormalizeCatalogKey(value, 320).Replace("-", "", StringComparison.Ordinal);
        return normalized is "WEBCOLOR" or "WEBCOLOUR" or "WEBRENK";
    }

    private static string? VariantOptionAxis(string value)
    {
        if (IsColorOptionKey(value)) return "COLOR";
        if (IsSizeOptionKey(value)) return "SIZE";
        return null;
    }

    private static string? ObservedVariantValueAxis(string value) => VariantOptionAxis(value);

    private static bool IsColorOptionKey(string value)
    {
        var normalized = NormalizeCatalogKey(value, 320).Replace(" ", "", StringComparison.Ordinal);
        return normalized is "RENK" or "COLOR" or "COLOUR";
    }

    private static bool IsRealColorOptionKey(string value) => IsColorOptionKey(value) && !IsWebColorOptionKey(value);

    // True when the local option signature carries the same colour and size values as the remote options.
    // It reads both stored forms ("Renk: Mavi | Beden: XL" and the old panel "RENK=MAVI|BEDEN=XL"). Remote
    // options without a colour or size cannot narrow a model-code match.
    // Every value the marketplace names ("S"; "Lacivert / L") is one of the panel variant's option values.
    internal static bool HasOptionValues(string? localSignature, IReadOnlyDictionary<string, string> remoteOptions)
    {
        if (string.IsNullOrWhiteSpace(localSignature)) return false;
        var local = VariantOptionSignature.ParseKeyed(localSignature).Values.ToHashSet(StringComparer.Ordinal);
        var values = remoteOptions.Values
            .SelectMany(value => value.Split(['/', ',', ';', '|'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            .Select(value => NormalizeCatalogKey(CleanCatalogOptionValue(value), 120))
            .Where(value => value.Length > 0)
            .ToList();
        return values.Count > 0 && values.All(local.Contains);
    }

    // The panel variant's colour appears in the title as whole words ("Bordo" in "Bordo Kaşe Mini Etek").
    internal static bool TitleNamesColor(string? title, string? localSignature)
    {
        var color = VariantOptionSignature.ParseKeyed(localSignature).FirstOrDefault(option => VariantOptionSignature.IsColorName(option.Key)).Value;
        var titleKey = NormalizeCatalogKey(title, 320);
        return !string.IsNullOrEmpty(color) && titleKey.Length > 0 && $"-{titleKey}-".Contains($"-{color}-", StringComparison.Ordinal);
    }

    // The panel option of the same name, else the one on the same axis ("Color" on the marketplace, "Renk" in the
    // panel); values compare by the shared catalog key, so case and Turkish letters do not matter.
    internal static bool SameColorAndSize(string? localSignature, IReadOnlyDictionary<string, string> remoteOptions)
    {
        var axes = remoteOptions
            .Where(option => IsRealColorOptionKey(option.Key) || IsSizeOptionKey(option.Key))
            .ToList();
        if (axes.Count == 0 || string.IsNullOrWhiteSpace(localSignature)) return false;
        var local = VariantOptionSignature.ParseKeyed(localSignature);
        return axes.All(option =>
        {
            var expected = NormalizeCatalogKey(CleanCatalogOptionValue(option.Value), 120);
            if (local.TryGetValue(NormalizeCatalogKey(option.Key, 80), out var value)) return string.Equals(value, expected, StringComparison.Ordinal);
            var isColor = IsRealColorOptionKey(option.Key);
            var sameAxis = local.Where(pair => isColor ? IsRealColorOptionKey(pair.Key) : IsSizeOptionKey(pair.Key)).Select(pair => pair.Value).ToList();
            return sameAxis.Count == 1 && string.Equals(sameAxis[0], expected, StringComparison.Ordinal);
        });
    }

    // The model-code candidates a remote variant may be bound to (Y5). A model code names every colour and size of a
    // model, so a candidate is kept only when its colour and size are the remote variant's; options without a colour or
    // size name (PTT AVM's "S", "Lacivert / L") are matched by their values, and a colour the title names ("Bordo Kaşe
    // Mini Etek") picks among the colours of the model. A remote variant that names no colour or size keeps the model's
    // one panel variant only when nothing can contradict it: the remote names no option at all, or the panel variant has
    // no colour or size either. Anything else returns the undecided candidates (or none): the caller never picks one.
    internal static List<T> NarrowModelCodeCandidates<T>(IReadOnlyList<T> candidates, Func<T, string?> signature, IReadOnlyDictionary<string, string> remoteOptions, string? title)
    {
        if (candidates.Count == 0) return [];
        var narrowed = candidates.Where(candidate => SameColorAndSize(signature(candidate), remoteOptions)).ToList();
        if (narrowed.Count == 1) return narrowed;
        var byValues = candidates.Where(candidate => HasOptionValues(signature(candidate), remoteOptions)).ToList();
        if (byValues.Count > 1) byValues = byValues.Where(candidate => TitleNamesColor(title, signature(candidate))).ToList();
        if (byValues.Count == 1) return byValues;
        var remoteNamesAxis = remoteOptions.Any(option => (IsRealColorOptionKey(option.Key) || IsSizeOptionKey(option.Key)) && !string.IsNullOrWhiteSpace(CleanCatalogOptionValue(option.Value)));
        if (!remoteNamesAxis && candidates.Count == 1)
        {
            var namesNoOption = remoteOptions.All(option => string.IsNullOrWhiteSpace(CleanCatalogOptionValue(option.Value)));
            var localNamesAxis = VariantOptionSignature.ParseKeyed(signature(candidates[0])).Keys.Any(key => IsRealColorOptionKey(key) || IsSizeOptionKey(key));
            if (namesNoOption || !localNamesAxis) return [candidates[0]];
        }
        return narrowed.Count > 1 ? narrowed : byValues.Count > 1 ? byValues : !remoteNamesAxis && candidates.Count > 1 ? [.. candidates] : [];
    }

    internal sealed record ModelCodeVariant(Guid ProductId, string ModelCode, string? OptionSignature);

    // The panel products a listing's variants land on by model code (Hepsiburada import, Y5). Each remote variant is
    // narrowed among its model's panel variants by colour and size (NarrowModelCodeCandidates); it counts for a product
    // when the variants left all belong to that one product (a listing of one colour without sizes, say). Several
    // products returned are several candidates: the caller links none of them.
    internal static List<Guid> ModelCodeProducts(IReadOnlyList<ModelCodeVariant> modelVariants, IEnumerable<RemoteCatalogVariant> remoteVariants, string? title)
    {
        var products = new List<Guid>();
        foreach (var remote in remoteVariants)
        {
            var code = remote.ModelCode?.Trim();
            if (string.IsNullOrWhiteSpace(code)) continue;
            var candidates = modelVariants.Where(variant => string.Equals(variant.ModelCode, code, StringComparison.Ordinal)).ToList();
            var narrowed = NarrowModelCodeCandidates(candidates, variant => variant.OptionSignature, remote.Options, title);
            var narrowedProducts = narrowed.Select(variant => variant.ProductId).Distinct().ToList();
            if (narrowedProducts.Count == 1 && !products.Contains(narrowedProducts[0])) products.Add(narrowedProducts[0]);
        }
        return products;
    }

    private static bool IsVariantOptionName(string value) => VariantOptionAxis(value) is not null;

    private static bool IsCatalogProductOption(LocalCategoryAttribute mapped, string remoteName) =>
        mapped.Role == "OPTION" || IsVariantOptionName(remoteName);

    private static bool TryGetMappedAttribute(IReadOnlyDictionary<string, LocalCategoryAttribute> attributes, string remoteName, out LocalCategoryAttribute mapped)
    {
        if (attributes.TryGetValue(NormalizeCatalogKey(remoteName, 320), out var direct)
            && (IsWebColorOptionKey(remoteName) || !IsWebColorOptionKey(direct.Remote.Name)))
        {
            mapped = direct;
            return true;
        }

        var axis = VariantOptionAxis(remoteName);
        if (axis is null)
        {
            mapped = null!;
            return false;
        }

        var wantsWebColor = IsWebColorOptionKey(remoteName);
        mapped = attributes.Values
            .Where(item => VariantOptionAxis(item.Remote.Name) == axis || VariantOptionAxis(item.Definition.Name) == axis)
            .OrderByDescending(item => IsWebColorOptionKey(item.Remote.Name) == wantsWebColor)
            .ThenByDescending(item => item.Role == "OPTION")
            .FirstOrDefault()!;
        // If the feed contains the real Renk field but only Web Color is
        // mapped locally, do not silently replace the real value with the
        // marketplace presentation value. The caller can preserve the raw
        // Renk value instead.
        if (mapped is not null && !wantsWebColor && IsWebColorOptionKey(mapped.Remote.Name))
        {
            mapped = null!;
            return false;
        }
        return mapped is not null;
    }

    private static bool IsSizeOptionKey(string value)
    {
        var normalized = NormalizeCatalogKey(value, 320).Replace(" ", "", StringComparison.Ordinal);
        return normalized is "BEDEN" or "SIZE" or "SIZ" or "NUMARA" or "NUMBER";
    }

    private async Task<string> PanelOptionValueAsync(Guid tenantId, Guid connectionId, CategoryAttributeContext categoryContext, LocalCategoryAttribute mapped, string remoteValue, CancellationToken cancellationToken)
    {
        var cleanedRemoteValue = CleanCatalogOptionValue(remoteValue);
        var remote = mapped.Values.FirstOrDefault(value => NormalizeCatalogKey(value.Name, 320) == NormalizeCatalogKey(cleanedRemoteValue, 320));
        var localValue = await ResolveMappedAttributeValue(tenantId, connectionId, categoryContext, mapped, remote, cleanedRemoteValue, cancellationToken);
        return localValue?.Value ?? cleanedRemoteValue;
    }

    private async Task RepairMissingCatalogOptionAssignments(Guid tenantId, Guid connectionId, Guid productId, RemoteCatalogProduct snapshot, CategoryAttributeContext? categoryContext, CancellationToken cancellationToken)
    {
        var colorVariants = snapshot.Variants
            .Where(variant => !string.IsNullOrWhiteSpace(variant.ExternalVariantId) && variant.Options.Keys.Any(IsRealColorOptionKey))
            .ToList();
        if (colorVariants.Count == 0) return;
        var colorName = colorVariants.SelectMany(variant => variant.Options.Keys)
            .First(IsRealColorOptionKey);
        var colorLabel = categoryContext is not null && TryGetMappedAttribute(categoryContext.Attributes, colorName, out var mappedColor)
            ? mappedColor.Definition.Name
            : "Renk";
        var colorKey = NormalizeCatalogKey(colorLabel, 160);
        var colorOption = await db.ProductOptions.AsNoTracking()
            .SingleOrDefaultAsync(option => option.TenantId == tenantId && option.ProductId == productId && option.NormalizedKey == colorKey, cancellationToken);
        var externalIds = colorVariants.Select(variant => Short(variant.ExternalVariantId, 256)).ToArray();
        var linkedVariants = await (from link in db.MarketplaceVariantLinks.AsNoTracking()
                                    join variant in db.ProductVariants.AsNoTracking()
                                        on new { link.TenantId, VariantId = link.VariantId }
                                        equals new { variant.TenantId, VariantId = variant.Id }
                                    where link.TenantId == tenantId
                                        && link.ConnectionId == connectionId
                                        && externalIds.Contains(link.ExternalId)
                                        && variant.ProductId == productId
                                    select new { link.ExternalId, VariantId = variant.Id }).ToListAsync(cancellationToken);
        var assignedVariantIds = colorOption is null
            ? new HashSet<Guid>()
            : (await db.VariantOptionValues.AsNoTracking()
                .Where(assignment => assignment.TenantId == tenantId && assignment.OptionId == colorOption.Id)
                .Select(assignment => assignment.VariantId)
                .ToListAsync(cancellationToken)).ToHashSet();
        var linksByExternalId = linkedVariants.ToDictionary(link => MarketplaceVariantLinkCoverage.Normalize(link.ExternalId), link => link.VariantId, StringComparer.Ordinal);
        foreach (var remote in colorVariants)
        {
            var externalId = MarketplaceVariantLinkCoverage.Normalize(Short(remote.ExternalVariantId, 256));
            if (!linksByExternalId.TryGetValue(externalId, out var variantId) || assignedVariantIds.Contains(variantId)) continue;
            await UpsertCatalogOptions(tenantId, connectionId, productId, variantId, remote.Options, categoryContext, cancellationToken, onlyFillMissingAssignments: true);
        }
    }
    private async Task UpsertCatalogOptions(Guid tenantId, Guid connectionId, Guid productId, Guid variantId, IReadOnlyDictionary<string, string> options, CategoryAttributeContext? categoryContext, CancellationToken cancellationToken, bool onlyFillMissingAssignments = false)
    {
        var order = 0;
        var processedPanelOptions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in options.OrderBy(x => IsWebColorOptionKey(x.Key) ? 1 : 0).ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (IsWebColorOptionKey(pair.Key)) continue;
            var optionKey = NormalizeCatalogKey(pair.Key, 160); var valueKey = NormalizeCatalogKey(pair.Value, 160);
            if (string.IsNullOrWhiteSpace(optionKey) || string.IsNullOrWhiteSpace(valueKey)) continue;
            LocalCategoryAttribute? mapped = null;
            if (categoryContext is not null && (!TryGetMappedAttribute(categoryContext.Attributes, pair.Key, out mapped) || (mapped.Role != "OPTION" && !IsVariantOptionName(pair.Key)))) continue;
            var panelLabel = mapped?.Definition.Name ?? pair.Key;
            var panelValue = mapped is null || IsRealColorOptionKey(pair.Key) || IsRealColorOptionKey(mapped.Definition.Name)
                ? CleanCatalogOptionValue(pair.Value)
                : await PanelOptionValueAsync(tenantId, connectionId, categoryContext!, mapped, pair.Value, cancellationToken);
            optionKey = NormalizeCatalogKey(panelLabel, 160);
            if (categoryContext is not null && !processedPanelOptions.Add(optionKey)) continue;
            valueKey = NormalizeCatalogKey(panelValue, 160);
            // Panel-created options may still hold the legacy key form ("BEDEN/YAŞ" for "Beden/Yaş"); match
            // both so the import reuses them instead of adding a second option group.
            var optionKeys = CatalogLookupKeys(panelLabel);
            var valueKeys = CatalogLookupKeys(panelValue);
            var option = db.ProductOptions.Local.FirstOrDefault(x => x.TenantId == tenantId && x.ProductId == productId && CatalogKeyNormalizer.SameKey(x.NormalizedKey, optionKey))
                ?? await db.ProductOptions.Where(x => x.TenantId == tenantId && x.ProductId == productId && optionKeys.Contains(x.NormalizedKey)).OrderBy(x => x.NormalizedKey == optionKey ? 0 : 1).ThenBy(x => x.Id).FirstOrDefaultAsync(cancellationToken);
            if (option is null) { option = new ProductOption { Id = Guid.CreateVersion7(), TenantId = tenantId, ProductId = productId, Label = Short(panelLabel, 160), NormalizedKey = optionKey, SortOrder = order }; db.ProductOptions.Add(option); }
            var optionValue = db.ProductOptionValues.Local.FirstOrDefault(x => x.TenantId == tenantId && x.OptionId == option.Id && CatalogKeyNormalizer.SameKey(x.NormalizedKey, valueKey))
                ?? await db.ProductOptionValues.Where(x => x.TenantId == tenantId && x.OptionId == option.Id && valueKeys.Contains(x.NormalizedKey)).OrderBy(x => x.NormalizedKey == valueKey ? 0 : 1).ThenBy(x => x.Id).FirstOrDefaultAsync(cancellationToken);
            if (optionValue is null) { optionValue = new ProductOptionValue { Id = Guid.CreateVersion7(), TenantId = tenantId, OptionId = option.Id, Label = Short(panelValue, 160), NormalizedKey = valueKey, SortOrder = order }; db.ProductOptionValues.Add(optionValue); }
            var assignment = db.VariantOptionValues.Local.FirstOrDefault(x => x.TenantId == tenantId && x.VariantId == variantId && x.OptionId == option.Id)
                ?? await db.VariantOptionValues.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.VariantId == variantId && x.OptionId == option.Id, cancellationToken);
            if (assignment is null) db.VariantOptionValues.Add(new VariantOptionValue { Id = Guid.CreateVersion7(), TenantId = tenantId, VariantId = variantId, OptionId = option.Id, OptionValueId = optionValue.Id });
            else if (!onlyFillMissingAssignments) assignment.OptionValueId = optionValue.Id;
            order++;
        }
    }

    // K3: media the user manages is never replaced by an import: a custom gallery order (ORDERED_* roles),
    // an uploaded file in the scope, or a media edit recorded on a marketplace link of the product.
    private async Task<bool> MediaIsUserManagedAsync(Guid tenantId, Guid productId, Guid? variantId, CancellationToken cancellationToken)
    {
        if (await db.ProductMedia.AsNoTracking().AnyAsync(x => x.TenantId == tenantId && x.ProductId == productId && x.Status == "ACTIVE" && x.MediaRole.StartsWith("ORDERED_"), cancellationToken)) return true;
        if (await (from media in db.ProductMedia.AsNoTracking()
                   join asset in db.FileAssets.AsNoTracking() on new { media.TenantId, media.FileAssetId } equals new { asset.TenantId, FileAssetId = asset.Id }
                   where media.TenantId == tenantId && media.ProductId == productId && media.VariantId == variantId && media.Status == "ACTIVE" && asset.Classification == "PRODUCT_MEDIA"
                   select media.Id).AnyAsync(cancellationToken)) return true;
        var dirtyFields = db.MarketplaceProductLinks.Local.Where(x => x.TenantId == tenantId && x.ProductId == productId).Select(x => x.DirtyFieldsJson).ToList();
        dirtyFields.AddRange(await db.MarketplaceProductLinks.AsNoTracking().Where(x => x.TenantId == tenantId && x.ProductId == productId && x.DirtyFieldsJson != null).Select(x => x.DirtyFieldsJson).ToListAsync(cancellationToken));
        return dirtyFields.Any(json => CatalogLocalEdits.HasDirtyField(json, CatalogLocalEdits.MediaField));
    }

    private async Task UpsertCatalogMedia(Guid tenantId, Product product, Guid? variantId, IReadOnlyList<string> sourceUrls, string altText, CancellationToken cancellationToken)
    {
        if (db.Entry(product).State != EntityState.Added && await MediaIsUserManagedAsync(tenantId, product.Id, variantId, cancellationToken)) return;
        if (variantId is Guid ownVariantId && db.Entry(product).State != EntityState.Added && await VariantPicturesAreOwnersAsync(tenantId, product.Id, ownVariantId, cancellationToken)) return;
        var urls = CatalogImageIdentity.DistinctUrls(sourceUrls);
        var existing = await db.ProductMedia.Where(x => x.TenantId == tenantId && x.ProductId == product.Id && x.VariantId == variantId).ToListAsync(cancellationToken);
        var existingAssetIds = existing.Select(x => x.FileAssetId).Distinct().ToArray();
        var existingAssets = existingAssetIds.Length == 0
            ? []
            : await db.FileAssets
                .Where(x => x.TenantId == tenantId && existingAssetIds.Contains(x.Id))
                .ToListAsync(cancellationToken);
        foreach (var row in existing.Where(x => x.SortOrder >= urls.Count)) row.Status = "ARCHIVED";
        for (var index = 0; index < urls.Count; index++)
        {
            var url = urls[index];
            var asset = existingAssets.FirstOrDefault(x => x.Classification == "PRODUCT_MEDIA_URL" && string.Equals(CatalogImageIdentity.NormalizeUrl(x.RelativePath), url, StringComparison.OrdinalIgnoreCase))
                ?? db.FileAssets.Local.FirstOrDefault(x => x.TenantId == tenantId && x.Classification == "PRODUCT_MEDIA_URL" && string.Equals(CatalogImageIdentity.NormalizeUrl(x.RelativePath), url, StringComparison.OrdinalIgnoreCase))
                ?? await db.FileAssets.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Classification == "PRODUCT_MEDIA_URL" && x.RelativePath == url, cancellationToken);
            if (asset is null)
            {
                asset = new FileAsset { Id = Guid.CreateVersion7(), TenantId = tenantId, Classification = "PRODUCT_MEDIA_URL", RelativePath = url, OriginalNameSafe = Path.GetFileName(new Uri(url).AbsolutePath), MimeType = ImageMime(url), SizeBytes = 0, Sha256 = Hash(url), Status = "ACTIVE", CreatedAt = timeProvider.GetUtcNow() };
                db.FileAssets.Add(asset);
            }
            else
            {
                if (!string.Equals(asset.RelativePath, url, StringComparison.Ordinal)) asset.RelativePath = url;
                asset.Sha256 = Hash(url);
                if (asset.Status != "ACTIVE" || asset.ArchivedAt is not null) { asset.Status = "ACTIVE"; asset.ArchivedAt = null; }
            }
            var media = existing.SingleOrDefault(x => x.SortOrder == index);
            if (media is null) db.ProductMedia.Add(new ProductMedia { Id = Guid.CreateVersion7(), TenantId = tenantId, ProductId = product.Id, VariantId = variantId, FileAssetId = asset.Id, MediaRole = index == 0 ? "PRIMARY" : "GALLERY", SortOrder = index, AltText = Short(altText, 320), Status = "ACTIVE" });
            else
            {
                var nextRole = index == 0 ? "PRIMARY" : "GALLERY";
                var nextAltText = Short(altText, 320);
                if (media.FileAssetId != asset.Id || media.MediaRole != nextRole || media.AltText != nextAltText || media.Status != "ACTIVE")
                {
                    media.FileAssetId = asset.Id; media.MediaRole = nextRole; media.AltText = nextAltText; media.Status = "ACTIVE";
                }
            }
        }
    }

    // A new card's VAT from the listing: the rate most of its variants carry (0-100), else null. A variant the
    // marketplace sent without a rate counts for nothing: unknown stays unknown.
    internal static decimal? ListingVatRate(RemoteCatalogProduct snapshot) => snapshot.Variants
        .Select(variant => variant.VatRate)
        .OfType<decimal>()
        .Where(rate => rate is >= 0 and <= 100)
        .Select(rate => decimal.Round(rate, 4, MidpointRounding.ToEven))
        .GroupBy(rate => rate)
        .OrderByDescending(group => group.Count())
        .Select(group => (decimal?)group.Key)
        .FirstOrDefault();

    private static string ProductTitle(string? title, string externalId) => Short(string.IsNullOrWhiteSpace(title) ? $"Pazar yeri ürünü {externalId}" : title.Trim(), 320);
    private static string CleanCatalogOptionValue(string? value) => string.IsNullOrWhiteSpace(value) ? "" : value.Trim().Trim('"', '“', '”').Trim();
    private static string OptionSignature(IReadOnlyDictionary<string, string> options) => VariantOptionSignature.Build(options);
    private static string CategoryLeafName(string? name, string? path)
    {
        var source = string.IsNullOrWhiteSpace(path) ? name : path;
        var segments = source?.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];
        return Short(segments.Length > 0 ? segments[^1] : name, 160);
    }

    private static string Short(string? value, int maximum) => string.IsNullOrWhiteSpace(value) ? "" : value.Trim().Length <= maximum ? value.Trim() : value.Trim()[..maximum];
    // The shared catalog key (CatalogKeyNormalizer); the panel uses the same one, so SKU, barcode, option,
    // brand and category keys written here and there agree.
    private static string NormalizeCatalogKey(string? value, int maximum) => CatalogKeyNormalizer.Key(value, maximum);
    private static string[] CatalogLookupKeys(string? value, int maximum = CatalogKeyNormalizer.DefaultMaximum) => CatalogKeyNormalizer.LookupKeys(value, maximum);
    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;
    private static string ImageMime(string url) => new Uri(url).AbsolutePath.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? "image/png" : "image/jpeg";

    private sealed class CategoryAttributeContext(Category LocalCategory, string ExternalCategoryId, IReadOnlyDictionary<string, LocalCategoryAttribute> Attributes)
    {
        public Category LocalCategory { get; } = LocalCategory;
        public string ExternalCategoryId { get; } = ExternalCategoryId;
        public IReadOnlyDictionary<string, LocalCategoryAttribute> Attributes { get; } = Attributes;
        public HashSet<string> ObservedVariantValueKeys { get; } = new(StringComparer.Ordinal);
        public HashSet<Guid> ExactWebColorInitializedAttributeIds { get; } = [];
        public HashSet<Guid> ExactWebColorValueIds { get; } = [];
    }
    private sealed record LocalCategoryAttribute(AttributeDefinition Definition, ReferenceItem Remote, IReadOnlyList<ReferenceItem> Values, string Role);

    private async Task<bool> IngestWebhook(Guid tenantId, Guid connectionId, string payloadJson, string correlationId, CancellationToken cancellationToken)
    {
        string raw; string externalMessageId; string resourceType;
        try { using var payload = JsonDocument.Parse(payloadJson); raw = payload.RootElement.GetProperty("rawJson").GetString() ?? ""; externalMessageId = payload.RootElement.GetProperty("externalMessageId").GetString() ?? ""; resourceType = payload.RootElement.TryGetProperty("resourceType", out var type) ? type.GetString() ?? "" : ""; }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException) { return false; }

        var platform = await db.PlatformConnections.AsNoTracking().Where(x => x.TenantId == tenantId && x.Id == connectionId).Select(x => x.PlatformCode).SingleOrDefaultAsync(cancellationToken);
        var source = $"{platform}_WEBHOOK";
        if (platform == "HEPSIBURADA")
        {
            try
            {
                using var document = JsonDocument.Parse(raw);
                // Order and claim events end through the same inbox close as every other notice (audit 2026-10-24 Q4:
                // returning early left their inbox rows unprocessed after a successful run).
                if (string.Equals(resourceType, "ORDER_EVENT", StringComparison.Ordinal))
                {
                    if (!await ReadHepsiburadaOrderEvent(tenantId, connectionId, document.RootElement, externalMessageId, OrderEventPath(externalMessageId), correlationId, cancellationToken)) return false;
                }
                // A claim notice the mapper cannot read whole: the recent claims are read again.
                else if (string.Equals(resourceType, "CLAIM_EVENT", StringComparison.Ordinal))
                {
                    if (!await SyncReturns(tenantId, connectionId, JsonSerializer.Serialize(new { connectionId }), correlationId, cancellationToken)) return false;
                }
                else if (string.Equals(resourceType, "CLAIM_PACKAGE", StringComparison.Ordinal))
                {
                    var order = HepsiburadaJsonMapper.ClaimPackageOrder(document.RootElement);
                    TrackReceived();
                    // A claim names its order's line but not its date, VAT, title or discount: for an order the panel
                    // already has only the package is merged (an older modification time keeps the order and its lines).
                    var knownOrderModifiedAt = await db.Orders.AsNoTracking()
                        .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ExternalOrderId == order.ExternalOrderId)
                        .Select(x => (DateTimeOffset?)x.LastRemoteModifiedAt).FirstOrDefaultAsync(cancellationToken);
                    if (knownOrderModifiedAt is { } knownModifiedAt && order.LastModifiedAt >= knownModifiedAt)
                        order = order with { LastModifiedAt = knownModifiedAt.AddTicks(-1) };
                    await UpsertOrder(tenantId, connectionId, order, cancellationToken);
                    foreach (var claimElement in HepsiburadaJsonMapper.ClaimPackageClaims(document.RootElement))
                    {
                        var claim = HepsiburadaJsonMapper.ReturnClaim(claimElement);
                        await UpsertReturn(tenantId, connectionId, claim, cancellationToken);
                    }
                }
                else
                {
                    var claim = HepsiburadaJsonMapper.ReturnClaim(document.RootElement);
                    TrackReceived();
                    await UpsertReturn(tenantId, connectionId, claim, cancellationToken);
                }
            }
            catch (JsonException) { return false; }
        }
        else if (platform == "SHOPIFY")
        {
            if (!configuration.GetValue("Marketplace:PersistOrderSnapshots", true)) return true;
            string? externalOrderId = null;
            try
            {
                using var document = JsonDocument.Parse(raw);
                var orderIdProperty = string.Equals(resourceType, "REFUNDS", StringComparison.Ordinal) ? "order_id" : "id";
                if (document.RootElement.TryGetProperty(orderIdProperty, out var id))
                    externalOrderId = id.ValueKind == JsonValueKind.Number ? id.GetInt64().ToString(System.Globalization.CultureInfo.InvariantCulture) : id.GetString();
            }
            catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException) { return false; }
            if (string.IsNullOrWhiteSpace(externalOrderId)) return false;
            TrackRequest();
            var order = await orders.GetAsync(Context(tenantId, connectionId, $"webhook:{externalMessageId}", $"shopify-webhook:{connectionId}:{externalMessageId}"), externalOrderId, cancellationToken);
            if (!order.IsSuccess) { TrackResultFailure(order.Error); throw JobProcessingException.FromAdapter(order.Error!); }
            TrackReceived();
            await UpsertOrder(tenantId, connectionId, order.Value!, cancellationToken, persistFinancialObservations: true);
        }
        else
        {
            AdapterPageResult<RemoteOrder> page; try { page = TrendyolJsonMapper.Orders(raw); } catch (JsonException) { return false; }
            foreach (var issue in page.Issues ?? [])
                await RecordIssue(tenantId, $"order-webhook-contract:{connectionId}:{issue.Identity}:{issue.Code}", issue.Code, issue.Message, cancellationToken);
            // The webhook carries no storefront header; orderCountryCode names the customer's
            // storefront (TR, DE, AE…), which later targeted reads ask first.
            if (configuration.GetValue("Marketplace:PersistOrderSnapshots", true))
                await UpsertOrders(tenantId, connectionId, [.. page.Items.Select(order => order with { StoreFrontCode = TrendyolWebhookStorefront(order.RawJson) })], cancellationToken);
        }
        var inbox = await db.InboxMessages.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Source == source && x.ExternalMessageId == externalMessageId, cancellationToken); if (inbox is not null) inbox.ProcessedAt = timeProvider.GetUtcNow(); await db.SaveChangesAsync(cancellationToken); return true;
    }

    private sealed class OrderIngestionBatch
    {
        public Dictionary<string, Order> OrdersByExternalId { get; } = new(StringComparer.Ordinal);
        public Dictionary<Guid, List<OrderLine>> LinesByOrder { get; } = [];
        public Dictionary<Guid, Dictionary<string, ShipmentPackage>> PackagesByOrder { get; } = [];
        public Dictionary<Guid, HashSet<string>> EventIdsByOrder { get; } = [];
        public Dictionary<string, PackageLineAllocation> AllocationsByKey { get; } = new(StringComparer.Ordinal);
        public OrderLineVariantCandidates VariantCandidates { get; set; } = OrderLineVariantCandidates.Empty;
        public List<(OrderLine Line, DateTimeOffset ModifiedAt)> ReservationSources { get; } = [];
        // Lines read before that found their variant only now (OrderLineLateMatchStockPolicy).
        public List<OrderLine> LateMatchedLines { get; } = [];
        // Lines tied to their variant in this read, which take its cost and desi (OrderLineCostSnapshot).
        public List<OrderLine> CostSnapshotLines { get; } = [];
    }

    private async Task UpsertOrders(Guid tenantId, Guid connectionId, IReadOnlyList<RemoteOrder> remotes, CancellationToken cancellationToken)
    {
        if (remotes.Count == 0) return;
        // A long read projects page after page through one context. Rows this call loads are released
        // once the page is saved, so later SaveChanges calls do not walk every row read so far; rows the
        // caller already tracked (its cursor, its job) stay tracked.
        var trackedBefore = db.ChangeTracker.Entries().Select(entry => entry.Entity).ToHashSet(ReferenceEqualityComparer.Instance);
        var platformCode = await ConnectionPlatformCode(tenantId, connectionId, cancellationToken);
        var isShopify = platformCode == "SHOPIFY";
        var isHepsiburada = platformCode == "HEPSIBURADA";
        // The stream is package-shaped: one order can occur once per package.
        // Merge the page before materializing the order so split-package line
        // quantities are summed instead of the last package overwriting them.
        // Shopify returns a complete order snapshot, including all fulfillments
        // and refunds. The Trendyol package merger only sums active package
        // quantities and would erase Shopify cancellation quantities, so the
        // newest Shopify snapshot is retained.
        var mergedRemotes = remotes
            .Where(remote => !string.IsNullOrWhiteSpace(remote.ExternalOrderId))
            .GroupBy(remote => remote.ExternalOrderId, StringComparer.Ordinal)
            .Select(group => isShopify
                ? group.OrderByDescending(remote => remote.LastModifiedAt).First()
                : isHepsiburada
                    ? MergeHepsiburadaOrderLines(group)
                : TrendyolJsonMapper.MergeOrderPackages(group, group.Key) ?? group.OrderByDescending(remote => remote.LastModifiedAt).First())
            .ToList();
        if (mergedRemotes.Count == 0) return;
        var conflictingPackages = OrderPackageIdentityGuard.FindConflicts(mergedRemotes);
        var conflictedOrderIds = conflictingPackages
            .SelectMany(conflict => new[] { conflict.FirstOrderId, conflict.ConflictingOrderId })
            .ToHashSet(StringComparer.Ordinal);
        foreach (var conflict in conflictingPackages)
            await RecordIssue(
                tenantId,
                $"order-package-identity:{connectionId}:{conflict.ExternalPackageId}",
                "ORDER_PACKAGE_ID_CONFLICT",
                $"Paket kimliği {conflict.ExternalPackageId} birden fazla siparişe bağlandı; çakışan siparişler bu taramada uygulanmadı.",
                cancellationToken);

        var remotePackageOwners = mergedRemotes
            .SelectMany(remote => remote.Packages.Select(package => new { package.ExternalPackageId, remote.ExternalOrderId }))
            // Trendyol's "0" is every awaiting-payment order's number until its real package exists, owned by no one
            // order: it is never a conflict, and the order's own "0" row is superseded once the real package arrives.
            .Where(x => !string.IsNullOrWhiteSpace(x.ExternalPackageId) && !ShipmentPackageSupersession.IsPlaceholder(x.ExternalPackageId))
            .GroupBy(x => x.ExternalPackageId, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.First().ExternalOrderId, StringComparer.Ordinal);
        var candidatePackageIds = remotePackageOwners.Keys.ToArray();
        if (candidatePackageIds.Length > 0)
        {
            var persistedPackageOwners = await (from package in db.ShipmentPackages.AsNoTracking()
                                                join order in db.Orders.AsNoTracking()
                                                    on new { package.TenantId, package.OrderId } equals new { order.TenantId, OrderId = order.Id }
                                                where package.TenantId == tenantId
                                                    && package.ConnectionId == connectionId
                                                    && package.ExternalPackageId != ShipmentPackageSupersession.PlaceholderExternalId
                                                    && candidatePackageIds.Contains(package.ExternalPackageId)
                                                select new { package.ExternalPackageId, order.ExternalOrderId })
                .ToListAsync(cancellationToken);
            foreach (var owner in persistedPackageOwners)
            {
                if (!remotePackageOwners.TryGetValue(owner.ExternalPackageId, out var remoteOrderId)
                    || string.Equals(owner.ExternalOrderId, remoteOrderId, StringComparison.Ordinal)) continue;
                conflictedOrderIds.Add(remoteOrderId);
                await RecordIssue(
                    tenantId,
                    $"order-package-identity:{connectionId}:{owner.ExternalPackageId}",
                    "ORDER_PACKAGE_ID_CONFLICT",
                    $"Paket kimliği {owner.ExternalPackageId} mevcut siparişle çakıştı; çakışan sipariş bu taramada uygulanmadı.",
                    cancellationToken);
            }
        }
        if (conflictedOrderIds.Count > 0)
            mergedRemotes = mergedRemotes.Where(remote => !conflictedOrderIds.Contains(remote.ExternalOrderId)).ToList();
        if (mergedRemotes.Count == 0) return;
        var batch = new OrderIngestionBatch();
        var externalIds = mergedRemotes.Select(x => x.ExternalOrderId).ToArray();
        var orders = await db.Orders.Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && externalIds.Contains(x.ExternalOrderId)).ToListAsync(cancellationToken);
        foreach (var order in orders) batch.OrdersByExternalId[order.ExternalOrderId] = order;
        var orderIds = orders.Select(x => x.Id).ToArray();
        if (orderIds.Length > 0)
        {
            var lines = await db.OrderLines.Where(x => x.TenantId == tenantId && orderIds.Contains(x.OrderId)).ToListAsync(cancellationToken);
            foreach (var group in lines.GroupBy(x => x.OrderId)) batch.LinesByOrder[group.Key] = group.ToList();

            var packages = await db.ShipmentPackages.Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && orderIds.Contains(x.OrderId)).ToListAsync(cancellationToken);
            foreach (var group in packages.GroupBy(x => x.OrderId)) batch.PackagesByOrder[group.Key] = group.ToDictionary(x => x.ExternalPackageId, StringComparer.Ordinal);

            var packageIds = packages.Select(x => x.Id).ToArray();
            if (packageIds.Length > 0)
            {
                var allocations = await db.PackageLineAllocations.AsNoTracking().Where(x => x.TenantId == tenantId && packageIds.Contains(x.PackageId)).ToListAsync(cancellationToken);
                foreach (var (key, allocation) in AllocationsByLine(allocations)) batch.AllocationsByKey[key] = allocation;
            }

            var history = await db.OrderStatusHistory.AsNoTracking().Where(x => x.TenantId == tenantId && orderIds.Contains(x.OrderId)).Select(x => new { x.OrderId, x.SourceEventId }).ToListAsync(cancellationToken);
            foreach (var group in history.GroupBy(x => x.OrderId)) batch.EventIdsByOrder[group.Key] = group.Select(x => x.SourceEventId).ToHashSet(StringComparer.Ordinal);
        }

        batch.VariantCandidates = await LoadOrderLineVariantCandidates(tenantId, connectionId, OrderLineCodesOf(platformCode, remotes.SelectMany(x => x.Lines)), cancellationToken);
        foreach (var remote in mergedRemotes) await UpsertOrder(tenantId, connectionId, remote, cancellationToken, batch, saveChanges: false, persistFinancialObservations: isShopify);
        await AdoptLateMatchedLineStock(tenantId, connectionId, batch.LateMatchedLines, cancellationToken);
        await OrderLineCostSnapshot.TakeAsync(db, tenantId, batch.CostSnapshotLines, cancellationToken);
        await ProjectOrderReservations(tenantId, connectionId, batch.ReservationSources, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        foreach (var entry in db.ChangeTracker.Entries().Where(entry => entry.State == EntityState.Unchanged && !trackedBefore.Contains(entry.Entity)).ToList())
            entry.State = EntityState.Detached;
    }

    // Scan C-13 (13 Nov 2026): a line the page carries more than once took the alphabetically last status ("Shipped"
    // over "Delivered") and its order's discount was added once per copy. Each line now comes from the most recently
    // modified copy, and a copy whose lines a newer copy already carried adds no discount of its own.
    internal static RemoteOrder MergeHepsiburadaOrderLines(IEnumerable<RemoteOrder> candidates)
    {
        var matches = candidates.OrderByDescending(remote => remote.LastModifiedAt).ToArray();
        var latest = matches[0];
        var lines = matches
            .SelectMany(remote => remote.Lines)
            .GroupBy(line => line.ExternalLineId, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
        var seenLines = new HashSet<string>(StringComparer.Ordinal);
        var discountSources = new List<RemoteOrder>();
        foreach (var match in matches)
        {
            var newLines = match.Lines.Count(line => seenLines.Add(line.ExternalLineId));
            if (newLines > 0 || match.Lines.Count == 0) discountSources.Add(match);
        }
        var packages = matches
            .SelectMany(remote => remote.Packages)
            .GroupBy(package => package.ExternalPackageId, StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(package => package.OccurredAt).First())
            .ToArray();
        var gross = lines.Sum(line => line.UnitPrice * line.Quantity);
        var discount = Math.Min(gross, discountSources.Sum(remote => remote.DiscountAmount));
        return latest with
        {
            LastModifiedAt = matches.Max(remote => remote.LastModifiedAt),
            GrossAmount = gross,
            DiscountAmount = discount,
            NetAmount = Math.Max(0m, gross - discount),
            Lines = lines,
            Packages = packages
        };
    }

    private async Task<bool> UpsertOrderCore(Guid tenantId, Guid connectionId, RemoteOrder remote, CancellationToken cancellationToken, OrderIngestionBatch? batch, bool saveChanges, bool persistFinancialObservations)
    {
        // Read once per connection (ConnectionPlatformCode), not once per order of a page (ISLEYICI-8).
        var platformCode = await ConnectionPlatformCode(tenantId, connectionId, cancellationToken);
        var isShopify = platformCode == "SHOPIFY";
        var isHepsiburada = platformCode == "HEPSIBURADA";
        var isTrendyol = platformCode == "TRENDYOL";
        // n11 copies Trendyol's package model (Created, Picking, UnPacked after a split); the Pazarama
        // and PTT AVM mappers translate their item statuses to the same names.
        var usesTrendyolPackageStatuses = isTrendyol || platformCode is "N11" or "PAZARAMA" or "PTTAVM";
        var now = timeProvider.GetUtcNow();
        var order = batch?.OrdersByExternalId.GetValueOrDefault(remote.ExternalOrderId)
            ?? await db.Orders.SingleOrDefaultAsync(x => x.TenantId == tenantId
                && x.ConnectionId == connectionId
                && x.ExternalOrderId == remote.ExternalOrderId, cancellationToken);

        if (isHepsiburada && batch is null && order is not null && remote.Lines.Count == 0)
        {
            var persistedLines = await db.OrderLines.AsNoTracking()
                .Where(line => line.TenantId == tenantId && line.OrderId == order.Id)
                .OrderBy(line => line.ExternalLineId)
                .Select(line => new RemoteOrderLine(
                    line.ExternalLineId,
                    line.Sku,
                    line.Barcode,
                    line.TitleSnapshot,
                    line.OrderedQuantity,
                    line.UnitPrice,
                    line.VatRate,
                    line.RawStatus,
                    line.SourceSnapshotJson ?? "{}",
                    line.CancelledQuantity))
                .ToListAsync(cancellationToken);
            if (HepsiburadaPackageStatusReconciliationPolicy.TryHydrateStatusObservationLines(remote, persistedLines, out var hydratedOrder))
                remote = hydratedOrder;
        }

        IReadOnlyDictionary<string, decimal> remoteLineQuantities;
        // A package whose every product line was unreadable is still stored for shipping, with no lines (UnreadableOrderLines).
        var keptWithUnreadableLines = remote.Packages.Any(package => package.UnreadableLineCount > 0);
        if ((!isTrendyol && remote.Lines.Count == 0 && !keptWithUnreadableLines) || remote.Packages.Count == 0 && !isHepsiburada)
        {
            await NoteRefusedOrderAsync(tenantId, connectionId, remote, isTrendyol, order is null, cancellationToken);
            await RecordIssue(tenantId, $"order-contract:{connectionId}:{remote.ExternalOrderId}:{remote.LastModifiedAt.ToUnixTimeMilliseconds()}", "ORDER_CONTRACT_INVALID", "Pazar yeri siparişinde satır veya paket verisi eksikti; eksik sipariş projeksiyonu uygulanmadı.", cancellationToken);
            if (saveChanges) await db.SaveChangesAsync(cancellationToken);
            return false;
        }
        else
        {
            remoteLineQuantities = new Dictionary<string, decimal>(StringComparer.Ordinal);
            if (remote.Lines.Count > 0 && !PackageIngestionSafety.TryGetOrderedQuantities(remote.Lines, out remoteLineQuantities))
            {
                await NoteRefusedOrderAsync(tenantId, connectionId, remote, isTrendyol, order is null, cancellationToken);
                await RecordIssue(tenantId, $"order-lines:{connectionId}:{remote.ExternalOrderId}:{remote.LastModifiedAt.ToUnixTimeMilliseconds()}", "ORDER_LINE_QUANTITY_INVARIANT_REJECTED", "Sipariş satır kimliği veya miktarı geçersizdi; olayın hiçbir parçası uygulanmadı.", cancellationToken);
                if (saveChanges) await db.SaveChangesAsync(cancellationToken);
                return false;
            }
        }
        // O8: a split parent is known by its children naming it as their origin, in this read or among the order's stored
        // packages, whatever status Trendyol lists it under (live: 4084999185 and three more, read as their own packages
        // after the split and refused as PACKAGE_QUANTITY_INVARIANT_REJECTED on every run). Its lines live on in the
        // children; a read that brings nothing but known parents of a stored order changes nothing and is skipped quietly.
        var splitParentIds = usesTrendyolPackageStatuses
            ? await KnownSplitParentIdsAsync(tenantId, connectionId, order, remote.Packages, batch, cancellationToken)
            : new HashSet<string>(StringComparer.Ordinal);
        if (order is not null && remote.Packages.Count > 0 && remote.Packages.All(package => splitParentIds.Contains(package.ExternalPackageId)))
        {
            foreach (var parent in remote.Packages) await ResolvePackageQuantityIssues(tenantId, connectionId, parent.ExternalPackageId, cancellationToken);
            if (saveChanges) await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        // A package kept without a product line it could not read is stored (it still has to be shipped), and its order
        // joins the re-read list (RejectedOrderRereadPolicy) so a later read can fill the line in; the line is never
        // guessed meanwhile and the order gets no invoice (InvoiceIssuePolicy.OrderLineUnreadable). Every marketplace.
        if (remote.Packages.Any(package => package.UnreadableLineCount > 0)) rereadOrderIds.Add(remote.ExternalOrderId);
        var detailedPackages = remote.Packages.Where(package => !package.IsStatusObservation).ToArray();
        var allocatedLineIds = detailedPackages.SelectMany(x => x.Allocations).Select(x => x.ExternalLineId).ToHashSet(StringComparer.Ordinal);
        if (remoteLineQuantities.Count > 0 && detailedPackages.Length > 0 && remoteLineQuantities.Keys.Any(lineId => !allocatedLineIds.Contains(lineId)))
        {
            await NoteRefusedOrderAsync(tenantId, connectionId, remote, isTrendyol, order is null, cancellationToken);
            await RecordIssue(tenantId, $"order-coverage:{connectionId}:{remote.ExternalOrderId}:{remote.LastModifiedAt.ToUnixTimeMilliseconds()}", "ORDER_LINE_COVERAGE_INVALID", "Pazar yeri cevabındaki sipariş satırlarının tamamı paket tahsisinde yer almıyordu; eksik veri uygulanmadı.", cancellationToken);
            if (saveChanges) await db.SaveChangesAsync(cancellationToken);
            return false;
        }
        // A new order must arrive as a complete package aggregate. Once the
        // order already exists, a later stream page may contain only one
        // sibling package; that fragment is validated per package below and
        // merged with the persisted projection instead of being rejected as an
        // incomplete order.
        // Split parents (UnPacked) carry no quantities of their own and are left out of the check, as below.
        var quantityPackages = usesTrendyolPackageStatuses
            ? detailedPackages.Where(package => !ShipmentPackageSupersession.IsSplitParentRawStatus(package.RawStatus) && !splitParentIds.Contains(package.ExternalPackageId)).ToArray()
            : detailedPackages;
        if (order is null && remoteLineQuantities.Count > 0 && quantityPackages.Length > 0 && !PackageIngestionSafety.TryNormalizeOrder(remoteLineQuantities, quantityPackages, out _))
        {
            var rejectedEventId = remote.Packages.Count > 0
                ? PackageIngestionSafety.EventId(remote.Packages[0].ExternalPackageId, remote.Packages[0].OccurredAt)
                : remote.ExternalOrderId;
            await NoteRefusedOrderAsync(tenantId, connectionId, remote, isTrendyol, order is null, cancellationToken);
            await RecordIssue(tenantId, $"package-quantity:{connectionId}:{rejectedEventId}", "PACKAGE_QUANTITY_INVARIANT_REJECTED", "Paket miktarları sipariş satırı bütünlüğünü sağlamadı; olayın hiçbir parçası uygulanmadı.", cancellationToken);
            if (saveChanges) await db.SaveChangesAsync(cancellationToken);
            return false;
        }
        // The packages this read moves out of ManualReview by their raw status now known: their lines take the read's
        // quantities below (ApplyReadLineQuantities), or a package that turned Returned kept its lines as sold.
        var repairedFromReview = new HashSet<Guid>();
        if (order is not null)
        {
            var repairCandidates = batch is not null
                ? (batch.PackagesByOrder.GetValueOrDefault(order.Id)?.Values.Where(x => x.Status == ShipmentPackageStatus.ManualReview).ToList() ?? [])
                : await db.ShipmentPackages.Where(x => x.TenantId == tenantId && x.OrderId == order.Id && x.Status == ShipmentPackageStatus.ManualReview).ToListAsync(cancellationToken);
            foreach (var candidate in repairCandidates) { var canonical = usesTrendyolPackageStatuses ? TrendyolShipmentPackageStatusPolicy.FromRemote(candidate.RawStatus) : ShipmentPackageStatusPolicy.FromRemote(candidate.RawStatus); if (canonical != ShipmentPackageStatus.ManualReview) { candidate.Status = canonical; candidate.UpdatedAt = now; candidate.Version++; repairedFromReview.Add(candidate.Id); } }
        }
        // Do not short-circuit empty-line replays: the same remote package can need a safe local canonical projection repair after a previously unknown raw status becomes recognized.
        var orderIsFresh = order is null
            || remote.LastModifiedAt >= order.LastRemoteModifiedAt
            || TrendyolJsonMapper.IsReturnClaimReadModelSnapshot(order.CustomerSnapshotJson);
        if (!orderIsFresh) telemetrySkippedCount++;
        if (order is null) { order = new Order { Id = Guid.CreateVersion7(), TenantId = tenantId, ConnectionId = connectionId, ExternalOrderId = remote.ExternalOrderId, OrderNumber = remote.OrderNumber, Currency = remote.Currency, CustomerSnapshotJson = remote.CustomerSnapshotJson, ShipmentAddressSnapshotJson = remote.ShipmentAddressSnapshotJson, InvoiceAddressSnapshotJson = remote.InvoiceAddressSnapshotJson, DerivedStatus = "NEW", ShipmentDueAt = remote.ShipmentDueAt, CreatedAt = now, Version = 1 }; db.Orders.Add(order); batch?.OrdersByExternalId.TryAdd(remote.ExternalOrderId, order); telemetryInsertedCount++; }
        // An order never changes storefront; the first read that names it is kept for later targeted reads.
        if (order.StoreFrontCode is null && !string.IsNullOrWhiteSpace(remote.StoreFrontCode)) order.StoreFrontCode = remote.StoreFrontCode;
        var isNewOrder = db.Entry(order).State == EntityState.Added;
        if (isNewOrder) await ResolveOrderRefusalsAsync(tenantId, connectionId, remote, cancellationToken);
        var previousAmounts = (order.GrossAmount, order.DiscountAmount, order.NetAmount);
        if (orderIsFresh)
        {
            var customerSnapshot = remote.CustomerSnapshotJson;
            var shipmentAddressSnapshot = remote.ShipmentAddressSnapshotJson;
            var invoiceAddressSnapshot = remote.InvoiceAddressSnapshotJson;
            var grossAmount = remote.GrossAmount;
            var discountAmount = remote.DiscountAmount;
            var netAmount = remote.NetAmount;
            if (isShopify && ShopifyOrderCsvSnapshotPolicy.HasImport(order.CustomerSnapshotJson))
            {
                customerSnapshot = ShopifyOrderCsvSnapshotPolicy.MergeRemoteSnapshot(customerSnapshot, order.CustomerSnapshotJson);
                shipmentAddressSnapshot = ShopifyOrderCsvSnapshotPolicy.MergeRemoteSnapshot(shipmentAddressSnapshot, order.ShipmentAddressSnapshotJson);
                invoiceAddressSnapshot = ShopifyOrderCsvSnapshotPolicy.MergeRemoteSnapshot(invoiceAddressSnapshot, order.InvoiceAddressSnapshotJson);
                (grossAmount, discountAmount, netAmount) = ShopifyOrderCsvSnapshotPolicy.MergeRemoteAmounts(
                    order.CustomerSnapshotJson, remote.GrossAmount, remote.DiscountAmount, remote.NetAmount);
            }
            else if (isHepsiburada)
            {
                // Keep authoritative invoice/cargo observations when an order
                // list read omits the detail-only fields.
                customerSnapshot = ShopifyOrderCsvSnapshotPolicy.MergeRemoteSnapshot(customerSnapshot, order.CustomerSnapshotJson);
                shipmentAddressSnapshot = ShopifyOrderCsvSnapshotPolicy.MergeRemoteSnapshot(shipmentAddressSnapshot, order.ShipmentAddressSnapshotJson);
                invoiceAddressSnapshot = ShopifyOrderCsvSnapshotPolicy.MergeRemoteSnapshot(invoiceAddressSnapshot, order.InvoiceAddressSnapshotJson);
            }
            order.OrderNumber = remote.OrderNumber; order.Currency = remote.Currency; order.GrossAmount = grossAmount; order.DiscountAmount = discountAmount; order.NetAmount = netAmount; order.OrderedAt = remote.OrderedAt; order.ShipmentDueAt = remote.ShipmentDueAt; order.LastRemoteModifiedAt = remote.LastModifiedAt; order.CustomerSnapshotJson = customerSnapshot; order.ShipmentAddressSnapshotJson = shipmentAddressSnapshot; order.InvoiceAddressSnapshotJson = invoiceAddressSnapshot; order.UpdatedAt = now; if (db.Entry(order).State != EntityState.Added) { order.Version++; telemetryUpdatedCount++; }
        }
        if (persistFinancialObservations && orderIsFresh)
            await UpsertShopifyFinancialObservations(tenantId, order.Id, remote, cancellationToken);
        var existingLines = batch is not null
            ? batch.LinesByOrder.GetValueOrDefault(order.Id) ?? []
            : await db.OrderLines.Where(x => x.TenantId == tenantId && x.OrderId == order.Id).ToListAsync(cancellationToken);
        if (!orderIsFresh && isHepsiburada)
            telemetryUpdatedCount += HepsiburadaStaleOrderEnrichmentPolicy.Apply(order, existingLines, remote, now);
        // The order's lines by external line id (ISLEYICI-12: one dictionary, kept up to date as lines are matched).
        var lines = existingLines
            .GroupBy(x => x.ExternalLineId, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.First(), StringComparer.Ordinal);
        var linesBySnapshot = existingLines
            .Where(x => !string.IsNullOrWhiteSpace(x.SourceSnapshotJson) && x.SourceSnapshotJson != "{}")
            .GroupBy(x => x.SourceSnapshotJson!, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.First(), StringComparer.Ordinal);
        var packagesByExternalId = batch is not null
            ? batch.PackagesByOrder.GetValueOrDefault(order.Id) ?? new Dictionary<string, ShipmentPackage>(StringComparer.Ordinal)
            : await db.ShipmentPackages.Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.OrderId == order.Id).ToDictionaryAsync(x => x.ExternalPackageId, StringComparer.Ordinal, cancellationToken);
        var knownEventIds = batch is not null
            ? batch.EventIdsByOrder.GetValueOrDefault(order.Id) ?? new HashSet<string>(StringComparer.Ordinal)
            : (await db.OrderStatusHistory.AsNoTracking().Where(x => x.TenantId == tenantId && x.OrderId == order.Id).Select(x => x.SourceEventId).ToListAsync(cancellationToken)).ToHashSet(StringComparer.Ordinal);
        foreach (var localEvent in db.OrderStatusHistory.Local.Where(x => x.TenantId == tenantId && x.OrderId == order.Id)) knownEventIds.Add(localEvent.SourceEventId);
        var packageIds = packagesByExternalId.Values.Select(x => x.Id).ToArray();
        var allocationsByKey = batch is not null
            ? batch.AllocationsByKey.Where(x => packageIds.Contains(x.Value.PackageId)).ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal)
            : AllocationsByLine(await db.PackageLineAllocations.AsNoTracking().Where(x => x.TenantId == tenantId && packageIds.Contains(x.PackageId)).ToListAsync(cancellationToken));
        var variantCandidates = batch?.VariantCandidates ?? await LoadOrderLineVariantCandidates(tenantId, connectionId, OrderLineCodesOf(platformCode, remote.Lines), cancellationToken);
        var lateMatchedLines = batch?.LateMatchedLines ?? [];
        var costSnapshotLines = batch?.CostSnapshotLines ?? [];
        if (batch is not null)
        {
            batch.LinesByOrder[order.Id] = existingLines;
            batch.PackagesByOrder[order.Id] = packagesByExternalId;
            batch.EventIdsByOrder[order.Id] = knownEventIds;
        }
        foreach (var remoteLine in remote.Lines)
        {
            if (!orderIsFresh && (!isHepsiburada
                || !HepsiburadaOrderLineMatcher.CanAddMissingLine(remoteLine, existingLines, remote.Lines)))
                continue;
            var line = lines.GetValueOrDefault(remoteLine.ExternalLineId);
            if (line is null && isHepsiburada)
                line = HepsiburadaOrderLineMatcher.FindExistingLine(remoteLine, existingLines, remote.Lines);
            if (line is null && !string.IsNullOrWhiteSpace(remoteLine.SourceSnapshotJson) && remoteLine.SourceSnapshotJson != "{}") line = linesBySnapshot.GetValueOrDefault(remoteLine.SourceSnapshotJson);
            if (line is null) { line = new OrderLine { Id = Guid.CreateVersion7(), TenantId = tenantId, OrderId = order.Id, ExternalLineId = remoteLine.ExternalLineId, Sku = remoteLine.Sku, TitleSnapshot = remoteLine.Title, RawStatus = remoteLine.RawStatus, Version = 1 }; db.OrderLines.Add(line); existingLines.Add(line); telemetryInsertedCount++; }

            if (line.VariantId is null
                && variantCandidates.Match(OrderLineVariantMatcher.Codes(platformCode, remoteLine.Sku, remoteLine.Barcode, remoteLine.SourceSnapshotJson)).VariantId is { } matchedVariantId)
            {
                line.VariantId = matchedVariantId;
                costSnapshotLines.Add(line);
                // A line stored by an earlier read takes its stock as a late match (OrderLineLateMatchStockPolicy).
                if (db.Entry(line).State != EntityState.Added) lateMatchedLines.Add(line);
            }
            var orderedQuantity = db.Entry(line).State == EntityState.Added
                ? remoteLine.Quantity
                : Math.Max(line.OrderedQuantity, remoteLine.Quantity);
            var importedUnitPrice = isShopify && remoteLine.UnitPrice == 0 && ShopifyOrderCsvSnapshotPolicy.HasImport(line.SourceSnapshotJson)
                ? ShopifyOrderCsvSnapshotPolicy.ImportedAmount(line.SourceSnapshotJson, "unitPrice")
                : null;
            var sku = isShopify ? ShopifyOrderCsvSnapshotPolicy.PreserveRemoteSku(remoteLine.Sku, line) ?? line.Sku : remoteLine.Sku;
            var title = isShopify ? ShopifyOrderCsvSnapshotPolicy.PreserveRemoteTitle(remoteLine.Title, line) : remoteLine.Title;
            // Hepsiburada order details may omit the image field present in
            // paid-order/package lists. Keep previously observed line data
            // when a later read does not repeat it.
            var sourceSnapshot = isShopify || isHepsiburada
                ? ShopifyOrderCsvSnapshotPolicy.MergeRemoteSnapshot(remoteLine.SourceSnapshotJson, line.SourceSnapshotJson)
                : remoteLine.SourceSnapshotJson;
            // A read without the line's VAT rate (a Hepsiburada claim, say) keeps the rate an earlier read reported; a new
            // line without one stays at 0 and the invoice gate stops it (InvoiceIssuePolicy.IsLineVatRateUnknown).
            var previousExternalLineId = line.ExternalLineId;
            if (!string.Equals(previousExternalLineId, remoteLine.ExternalLineId, StringComparison.Ordinal))
                lines.Remove(previousExternalLineId);
            line.ExternalLineId = remoteLine.ExternalLineId;
            line.Sku = sku; line.Barcode = remoteLine.Barcode ?? line.Barcode; line.TitleSnapshot = title; line.SourceSnapshotJson = sourceSnapshot; line.OrderedQuantity = orderedQuantity; line.UnitPrice = importedUnitPrice ?? remoteLine.UnitPrice; line.PlatformDiscount = remoteLine.PlatformDiscount ?? line.PlatformDiscount; line.VatRate = remoteLine.VatRate ?? line.VatRate; line.RawStatus = remoteLine.RawStatus; if (db.Entry(line).State != EntityState.Added) line.Version++; lines[remoteLine.ExternalLineId] = line;
            if (!string.IsNullOrWhiteSpace(remoteLine.SourceSnapshotJson) && remoteLine.SourceSnapshotJson != "{}") linesBySnapshot[remoteLine.SourceSnapshotJson] = line;
        }
        var externalLineIdsById = lines.Values.ToDictionary(line => line.Id, line => line.ExternalLineId);
        foreach (var remotePackage in remote.Packages)
        {
            var target = usesTrendyolPackageStatuses ? TrendyolShipmentPackageStatusPolicy.FromRemote(remotePackage.RawStatus) : isShopify ? ShopifyShipmentPackageStatusPolicy.FromRemote(remotePackage.RawStatus) : isHepsiburada ? HepsiburadaShipmentPackageStatusPolicy.FromRemote(remotePackage.RawStatus) : ShipmentPackageStatusPolicy.FromRemote(remotePackage.RawStatus); var eventId = PackageIngestionSafety.EventId(remotePackage.ExternalPackageId, remotePackage.OccurredAt); var orderedQuantities = lines.ToDictionary(x => x.Key, x => x.Value.OrderedQuantity, StringComparer.Ordinal);
            var package = packagesByExternalId.GetValueOrDefault(remotePackage.ExternalPackageId);
            // The time a status is ordered by and stored with. Without a status time from the marketplace (PTT AVM),
            // OccurredAt only names the event; the package keeps the time the status was first seen (audit S4).
            var statusAt = PackageIngestionSafety.StatusTime(remotePackage, package?.StatusOccurredAt, now);
            // A package the panel unpacked is history; a late read of it does not bring it back.
            if (package is not null && ShipmentPackageSupersession.IsLocallyUnpacked(package.RawStatus)) continue;
            // Trendyol's package read closing an undelivered (claim-built) package as returned is applied although its
            // modification time is older than the claim time the package holds; the return is dated no earlier than the
            // status it ends, so the history stays in order.
            if (isTrendyol && package is not null && statusAt < package.StatusOccurredAt
                && PackageIngestionSafety.ClosesReturnRegardlessOfTime(package.Status, target, remotePackage.IsStatusObservation))
                statusAt = package.StatusOccurredAt;
            var legacyTrendyolHoldCorrection = isTrendyol
                && package is not null
                && package.Status == ShipmentPackageStatus.OnHold
                && TrendyolShipmentPackageStatusPolicy.HasSameNormalizedStatus(package.RawStatus, remotePackage.RawStatus)
                && target != ShipmentPackageStatus.OnHold
                && TrendyolShipmentPackageStatusPolicy.IsLegacyOnHoldProjection(remotePackage.RawStatus);
            // A Trendyol split parent (UnPacked) carries no quantities of its own: its lines live on in the child
            // packages, and its listed lines can exceed what the order still holds. Its lines are not checked or
            // allocated (the line projection skips split parents too), and a refusal it caused before is resolved.
            var isUnpackedParent = (usesTrendyolPackageStatuses || isHepsiburada) && ShipmentPackageSupersession.IsSplitParentRawStatus(remotePackage.RawStatus);
            var isSplitParent = isUnpackedParent || splitParentIds.Contains(remotePackage.ExternalPackageId);
            if (isSplitParent) await ResolvePackageQuantityIssues(tenantId, connectionId, remotePackage.ExternalPackageId, cancellationToken);
            // A parent known only by its children (O8), not stored: nothing of it is the order's any more; it is not opened.
            if (isSplitParent && !isUnpackedParent && package is null) continue;
            var packageAllocations = isSplitParent ? [] : remotePackage.Allocations;
            if (!isSplitParent && remotePackage.IsStatusObservation && packageAllocations.Count == 0 && package is not null)
            {
                packageAllocations = allocationsByKey.Values
                    .Where(allocation => allocation.PackageId == package.Id && externalLineIdsById.ContainsKey(allocation.OrderLineId))
                    .GroupBy(allocation => allocation.OrderLineId)
                    .Select(group => group.OrderByDescending(allocation => allocation.SourceEventId, StringComparer.Ordinal).First())
                    .Select(allocation =>
                    {
                        var activeQuantity = allocation.AllocatedQuantity;
                        var shippedQuantity = target is ShipmentPackageStatus.Shipped or ShipmentPackageStatus.Delivered or ShipmentPackageStatus.Undelivered
                            ? activeQuantity
                            : allocation.ShippedQuantity;
                        var deliveredQuantity = target == ShipmentPackageStatus.Delivered ? activeQuantity : allocation.DeliveredQuantity;
                        return new RemotePackageAllocation(externalLineIdsById[allocation.OrderLineId], activeQuantity, allocation.CancelledQuantity, shippedQuantity, deliveredQuantity, allocation.ReturnedQuantity);
                    })
                    .ToArray();
            }
            IReadOnlyDictionary<string, NormalizedPackageAllocation> safeAllocations = new Dictionary<string, NormalizedPackageAllocation>(StringComparer.Ordinal);
            if (!isSplitParent && (packageAllocations.Count > 0 || !remotePackage.IsStatusObservation))
            {
                if (!PackageIngestionSafety.TryNormalizeAll(orderedQuantities, packageAllocations, target, out safeAllocations))
                {
                    if (isTrendyol || isNewOrder) rereadOrderIds.Add(remote.ExternalOrderId);
                    await RecordIssue(tenantId, $"package-quantity:{connectionId}:{eventId}", "PACKAGE_QUANTITY_INVARIANT_REJECTED", "Package miktarları sipariş satırı bütünlüğünü sağlamadı; olayın hiçbir parçası uygulanmadı.", cancellationToken);
                    continue;
                }
            }
            var packageObservation = remotePackage with { Allocations = packageAllocations };
            // The amounts are not a status: a fresh detailed read refreshes them also when its status event is
            // already recorded or refused, so the order totals below are rebuilt from what the marketplace says now.
            if (package is not null && usesTrendyolPackageStatuses && orderIsFresh && PackageAmountPolicy.Apply(package, remotePackage))
            {
                package.UpdatedAt = now;
                if (db.Entry(package).State != EntityState.Added) package.Version++;
                telemetryUpdatedCount++;
            }
            // Like the amounts, the package's unreadable lines follow every fresh detailed read, whatever its status event.
            if (package is not null && orderIsFresh && await ApplyUnreadableLines(tenantId, connectionId, platformCode, order.OrderNumber, package, remotePackage, cancellationToken))
            {
                package.UpdatedAt = now;
                if (db.Entry(package).State != EntityState.Added) package.Version++;
                telemetryUpdatedCount++;
            }
            if (package is not null) await MergeMarketplaceInvoiceState(package, remotePackage, cancellationToken, isTrendyol ? "stream" : null);
            if (package is not null && package.Status == ShipmentPackageStatus.ManualReview && package.RawStatus == remotePackage.RawStatus && target != ShipmentPackageStatus.ManualReview) { package.Status = target; package.UpdatedAt = now; package.Version++; ApplyReadLineQuantities(tenantId, package, remotePackage, packageObservation.Allocations, safeAllocations, lines, allocationsByKey, eventId); continue; }
            // An observation that left the status unchanged writes no history row (below); its replay is still
            // known as applied by the line allocations it stored under the same event id.
            var eventAlreadyRecorded = knownEventIds.Contains(eventId)
                || package is not null && package.Status == target && string.Equals(package.RawStatus, remotePackage.RawStatus, StringComparison.Ordinal)
                    && allocationsByKey.Values.Any(allocation => allocation.PackageId == package.Id && allocation.SourceEventId == eventId);
            if (eventAlreadyRecorded)
            {
                // Shopify can enrich the same fulfillment event later with
                // delivery information while keeping its original createdAt.
                // Treat that as an idempotent status enrichment, not as a
                // reason to discard the authoritative forward transition.
                var statusChanged = package is not null
                    && (legacyTrendyolHoldCorrection || (isHepsiburada
                        ? HepsiburadaPackageStatusReconciliationPolicy.ShouldAcceptStatusObservation(
                            package.Status,
                            package.StatusOccurredAt,
                            target,
                            statusAt,
                            remotePackage.IsStatusObservation)
                        : PackageIngestionSafety.ShouldAccept(package.Status, package.StatusOccurredAt, target, statusAt, remotePackage.RawStatus)))
                    && package.Status != target;
                var packageMetadataChanged = package is not null
                    && ((!string.IsNullOrWhiteSpace(remotePackage.CargoProviderExternalId) && package.CargoProviderExternalId != remotePackage.CargoProviderExternalId)
                        || (!string.IsNullOrWhiteSpace(remotePackage.CargoTrackingNumber) && package.CargoTrackingNumber != remotePackage.CargoTrackingNumber)
                        || (!string.IsNullOrWhiteSpace(remotePackage.CargoBarcode) && package.CargoBarcode != remotePackage.CargoBarcode.Trim()));
                if (statusChanged && package is not null)
                {
                    package.Status = target;
                    package.RawStatus = remotePackage.RawStatus;
                    package.StatusOccurredAt = statusAt;
                    PackageAmountPolicy.Apply(package, remotePackage);

                    foreach (var remoteAllocation in packageObservation.Allocations)
                    {
                        if (!lines.TryGetValue(remoteAllocation.ExternalLineId, out var line)
                            || !safeAllocations.TryGetValue(remoteAllocation.ExternalLineId, out var safe)) continue;
                        UpsertLineAllocation(tenantId, package.Id, line.Id, eventId, safe.ActiveAllocatedQuantity, safe.CancelledQuantity, safe.ShippedQuantity, safe.DeliveredQuantity, safe.ReturnedQuantity, allocationsByKey);
                    }
                }
                // PTT AVM sends no status time, so its package event is named by the package status alone; Pazarama's is its
                // items' latest history entry, and an item can change status with no new entry (live 674544454: status 7 with
                // the history ending at 11). A line cancelled or returned while the package keeps its status (another line
                // still waits or stays delivered) then arrives under the event already recorded. Its lines' quantities
                // follow the read, or the cancelled line kept its stock and the returned one read as kept.
                if (package is not null && !statusChanged && (remotePackage.StatusTimeUnknown || platformCode == "PAZARAMA" || repairedFromReview.Contains(package.Id)) && !remotePackage.IsStatusObservation
                    && package.Status == target && string.Equals(package.RawStatus, remotePackage.RawStatus, StringComparison.Ordinal))
                    ApplyReadLineQuantities(tenantId, package, remotePackage, packageObservation.Allocations, safeAllocations, lines, allocationsByKey, eventId);
                if (package is not null && (statusChanged || packageMetadataChanged))
                {
                    package.OriginExternalPackageId = string.IsNullOrWhiteSpace(remotePackage.OriginExternalPackageId) ? package.OriginExternalPackageId : remotePackage.OriginExternalPackageId;
                    package.CargoProviderExternalId = string.IsNullOrWhiteSpace(remotePackage.CargoProviderExternalId) ? package.CargoProviderExternalId : remotePackage.CargoProviderExternalId;
                    package.CargoTrackingNumber = string.IsNullOrWhiteSpace(remotePackage.CargoTrackingNumber) ? package.CargoTrackingNumber : remotePackage.CargoTrackingNumber;
                    package.CargoBarcode = string.IsNullOrWhiteSpace(remotePackage.CargoBarcode) ? package.CargoBarcode : remotePackage.CargoBarcode.Trim();
                    package.UpdatedAt = now;
                    package.Version++;
                    telemetryUpdatedCount++;
                }
                // The initial projection used shipmentPackageStatus before the
                // authoritative top-level status. When the same package event
                // is replayed after that mapper correction, repair only this
                // known ReadyToShip -> New projection mismatch. This is a
                // local idempotent repair; it does not create a new remote
                // event or perform any marketplace write.
                if (package is not null
                    && package.Status == ShipmentPackageStatus.ReadyToShip
                    && string.Equals(package.RawStatus, "ReadyToShip", StringComparison.OrdinalIgnoreCase)
                    && target == ShipmentPackageStatus.New
                    && string.Equals(remotePackage.RawStatus, "Created", StringComparison.OrdinalIgnoreCase))
                {
                    package.Status = target;
                    package.RawStatus = remotePackage.RawStatus;
                    package.StatusOccurredAt = statusAt;
                    package.UpdatedAt = now;
                    package.Version++;
                }
                if (package is not null && package.Status == ShipmentPackageStatus.ManualReview && package.RawStatus == remotePackage.RawStatus && target != ShipmentPackageStatus.ManualReview) { package.Status = target; package.UpdatedAt = now; package.Version++; ApplyReadLineQuantities(tenantId, package, remotePackage, packageObservation.Allocations, safeAllocations, lines, allocationsByKey, eventId); }
                // The event is known, but a line the detailed read places in this package may have none: an order
                // first built from a return claim held only the returned lines when the event was recorded, and the
                // order read that brought the other lines found the event already recorded.
                if (package is not null && !remotePackage.IsStatusObservation)
                    telemetryInsertedCount += AddMissingLineAllocations(tenantId, package, packageObservation.Allocations, safeAllocations, lines, allocationsByKey, eventId);
                continue;
            }
            var rejectedEventId = PackageIngestionSafety.RejectedEventId(eventId);
            if (package is not null && knownEventIds.Contains(rejectedEventId))
            {
                // This exact event was refused before; it is not evaluated
                // again, only cargo details it carries are still taken.
                if (MergePackageCargoMetadata(package, remotePackage)) { package.UpdatedAt = now; package.Version++; telemetryUpdatedCount++; }
                continue;
            }
            // The panel took the package into work a moment ago; a read that still sees it Created
            // was taken before the marketplace applied the picking. It is neither applied nor
            // reported as a refused transition while the grace window lasts.
            if (package is not null && package.Status == ShipmentPackageStatus.Processing && target == ShipmentPackageStatus.New)
            {
                var pickedAt = db.OrderStatusHistory.Local
                    .Where(x => x.TenantId == tenantId && x.PackageId == package.Id && x.RawStatus == PackageIngestionSafety.InstantPickingRawStatus)
                    .Select(x => (DateTimeOffset?)x.RecordedAt).Max()
                    ?? await db.OrderStatusHistory.AsNoTracking()
                        .Where(x => x.TenantId == tenantId && x.PackageId == package.Id && x.RawStatus == PackageIngestionSafety.InstantPickingRawStatus)
                        .MaxAsync(x => (DateTimeOffset?)x.RecordedAt, cancellationToken);
                if (PackageIngestionSafety.IsCreatedEchoAfterInstantPicking(package.Status, target, pickedAt, now))
                {
                    if (MergePackageCargoMetadata(package, remotePackage)) { package.UpdatedAt = now; package.Version++; telemetryUpdatedCount++; }
                    continue;
                }
            }
            var accept = package is null || legacyTrendyolHoldCorrection || (isHepsiburada
                ? HepsiburadaPackageStatusReconciliationPolicy.ShouldAcceptStatusObservation(
                    package.Status,
                    package.StatusOccurredAt,
                    target,
                    statusAt,
                    remotePackage.IsStatusObservation)
                : PackageIngestionSafety.ShouldAccept(package.Status, package.StatusOccurredAt, target, statusAt, remotePackage.RawStatus));
            // A newer modification date with the same status is not a status change: no history row for it.
            var statusUnchanged = package is not null && package.Status == target && string.Equals(package.RawStatus, remotePackage.RawStatus, StringComparison.Ordinal);
            if (package is null) { package = new ShipmentPackage { Id = Guid.CreateVersion7(), TenantId = tenantId, ConnectionId = connectionId, OrderId = order.Id, ExternalPackageId = remotePackage.ExternalPackageId, CreatedBy = remotePackage.CreatedBy, Status = target, RawStatus = remotePackage.RawStatus, StatusOccurredAt = statusAt, CreatedAt = now, Version = 1 }; db.ShipmentPackages.Add(package); packagesByExternalId[remotePackage.ExternalPackageId] = package; telemetryInsertedCount++; await MergeMarketplaceInvoiceState(package, remotePackage, cancellationToken, isTrendyol ? "stream" : null); await ApplyUnreadableLines(tenantId, connectionId, platformCode, order.OrderNumber, package, remotePackage, cancellationToken); }
            else if (accept) { package.Status = target; package.RawStatus = remotePackage.RawStatus; package.StatusOccurredAt = statusAt; package.Version++; }
            else if (remotePackage.IsStatusObservation && statusAt >= package.StatusOccurredAt && package.Status != target)
                // Status observations carry the read time, not an event time:
                // recording each refusal would add a history row per read.
                await RecordIssue(tenantId, $"package-transition:{package.Id}:{remotePackage.RawStatus}", "PACKAGE_TRANSITION_REJECTED", "Out-of-order veya izin verilmeyen package geçişi mevcut durumu geriye götürmedi.", cancellationToken);
            else if (statusAt >= package.StatusOccurredAt && package.Status != target)
            {
                // The refused status is not applied, but the cargo details of
                // the event still are, and the event is recorded once so it is
                // not evaluated again on every read. When the marketplace
                // reports an end state the panel refuses, the package waits
                // for an operator instead of staying in an operational tab.
                MergePackageCargoMetadata(package, remotePackage);
                var terminalConflict = PackageIngestionSafety.IsTerminalConflict(package.Status, target, remotePackage.RawStatus);
                if (terminalConflict)
                {
                    package.Status = ShipmentPackageStatus.ManualReview;
                    package.RawStatus = Short(PackageIngestionSafety.ConflictRawStatus(remotePackage.RawStatus), 96);
                }
                package.UpdatedAt = now;
                package.Version++;
                db.OrderStatusHistory.Add(new OrderStatusHistory { Id = Guid.CreateVersion7(), TenantId = tenantId, OrderId = order.Id, PackageId = package.Id, CanonicalStatus = "TRANSITION_REJECTED", RawStatus = Short(remotePackage.RawStatus, 96), SourceEventId = rejectedEventId, OccurredAt = statusAt, RecordedAt = now });
                knownEventIds.Add(rejectedEventId);
                await RecordIssue(tenantId, $"package-transition:{package.Id}:{remotePackage.RawStatus}",
                    terminalConflict ? "PACKAGE_TERMINAL_STATUS_CONFLICT" : "PACKAGE_TRANSITION_REJECTED",
                    terminalConflict
                        ? $"Pazaryeri paket {package.ExternalPackageId} için {remotePackage.RawStatus} bildirdi ancak paneldeki durumdan bu geçiş yapılamıyor; paket manuel incelemeye alındı."
                        : "Out-of-order veya izin verilmeyen package geçişi mevcut durumu geriye götürmedi.", cancellationToken);
            }
            // An older read is not applied, but it still fills the cargo details the package lacks (a claim-built
            // package has no tracking number; its package read does).
            else if (!accept && PackageIngestionSafety.FillMissingCargoMetadata(package, remotePackage))
            {
                package.UpdatedAt = now;
                package.Version++;
                telemetryUpdatedCount++;
            }
            if (package is not null && !remotePackage.IsStatusObservation && !string.IsNullOrWhiteSpace(remotePackage.CreatedBy)) package.CreatedBy = remotePackage.CreatedBy;
            if (accept && package is not null)
            {
                package.OriginExternalPackageId = string.IsNullOrWhiteSpace(remotePackage.OriginExternalPackageId) ? package.OriginExternalPackageId : remotePackage.OriginExternalPackageId; package.CargoProviderExternalId = string.IsNullOrWhiteSpace(remotePackage.CargoProviderExternalId) ? package.CargoProviderExternalId : remotePackage.CargoProviderExternalId; package.CargoTrackingNumber = string.IsNullOrWhiteSpace(remotePackage.CargoTrackingNumber) ? package.CargoTrackingNumber : remotePackage.CargoTrackingNumber; package.CargoBarcode = string.IsNullOrWhiteSpace(remotePackage.CargoBarcode) ? package.CargoBarcode : remotePackage.CargoBarcode.Trim(); PackageAmountPolicy.Apply(package, remotePackage); package.UpdatedAt = now; if (!statusUnchanged) db.OrderStatusHistory.Add(new OrderStatusHistory { Id = Guid.CreateVersion7(), TenantId = tenantId, OrderId = order.Id, PackageId = package.Id, CanonicalStatus = Wire(target), RawStatus = remotePackage.RawStatus, SourceEventId = eventId, OccurredAt = statusAt, RecordedAt = now }); knownEventIds.Add(eventId);
                foreach (var remoteAllocation in packageObservation.Allocations)
                    if (lines.TryGetValue(remoteAllocation.ExternalLineId, out var line) && safeAllocations.TryGetValue(remoteAllocation.ExternalLineId, out var safe)
                        && UpsertLineAllocation(tenantId, package.Id, line.Id, eventId, safe.ActiveAllocatedQuantity, safe.CancelledQuantity, safe.ShippedQuantity, safe.DeliveredQuantity, safe.ReturnedQuantity, allocationsByKey))
                        telemetryInsertedCount++;
            }
            // A Hepsiburada package first seen through a status observation (a webhook, a shipping list) was stored
            // without lines; a detailed read of it brings them even when its status time is older than that observation.
            // Only the lines are added: the status stays as it is.
            else if (isHepsiburada && package is not null && !remotePackage.IsStatusObservation && safeAllocations.Count > 0
                && !allocationsByKey.Values.Any(allocation => allocation.PackageId == package.Id))
            {
                foreach (var remoteAllocation in packageObservation.Allocations)
                {
                    if (!lines.TryGetValue(remoteAllocation.ExternalLineId, out var line) || !safeAllocations.TryGetValue(remoteAllocation.ExternalLineId, out var safe)) continue;
                    // The quantities follow the package's current (newer) status, as a status observation's do.
                    var shipped = package.Status is ShipmentPackageStatus.Shipped or ShipmentPackageStatus.Delivered or ShipmentPackageStatus.Undelivered ? safe.ActiveAllocatedQuantity : safe.ShippedQuantity;
                    var delivered = package.Status == ShipmentPackageStatus.Delivered ? safe.ActiveAllocatedQuantity : safe.DeliveredQuantity;
                    if (UpsertLineAllocation(tenantId, package.Id, line.Id, eventId, safe.ActiveAllocatedQuantity, safe.CancelledQuantity, shipped, delivered, safe.ReturnedQuantity, allocationsByKey))
                        telemetryInsertedCount++;
                }
            }
        }
        if (isTrendyol && remote.Packages.Any(package =>
                !string.IsNullOrWhiteSpace(package.ExternalPackageId)
                && !string.Equals(package.ExternalPackageId.Trim(), "0", StringComparison.Ordinal)))
        {
            foreach (var package in packagesByExternalId.Values)
            {
                // The "0" placeholder Trendyol listed before it created the real package is closed, whatever status it
                // was left in (Awaiting, an old OnHold projection): it never ships and is never reported again.
                if (!TrendyolShipmentPackageStatusPolicy.ShouldClosePlaceholder(package.ExternalPackageId, package.Status, hasAuthoritativePackage: true)) continue;

                package.Status = ShipmentPackageStatus.Cancelled;
                package.UpdatedAt = now;
                package.Version++;
                telemetryUpdatedCount++;
            }
        }
        // n11 does not name the origin of a split package and a stream page can
        // hold the UnPacked parent without its children (or the reverse); the
        // link is made on the stored packages so it survives across pages.
        if (platformCode == "N11")
            foreach (var linked in N11OrderMapper.LinkStoredSplitPackages(packagesByExternalId.Values.ToList()))
            {
                linked.UpdatedAt = now;
                if (db.Entry(linked).State != EntityState.Added) linked.Version++;
            }
        var projectedLineQuantities = PackageLineProjectionPolicy.Recalculate(packagesByExternalId.Values.ToList(), allocationsByKey.Values.ToList(), remote.Packages);
        var cancelledBefore = lines.Values.Distinct().ToDictionary(line => line.Id, line => db.Entry(line).State == EntityState.Added ? (decimal?)null : line.CancelledQuantity);
        foreach (var line in lines.Values)
        {
            if (!projectedLineQuantities.TryGetValue(line.Id, out var projection)) continue;
            line.CancelledQuantity = projection.CancelledQuantity;
            line.ShippedQuantity = projection.ShippedQuantity;
            line.DeliveredQuantity = projection.DeliveredQuantity;
            line.ReturnedQuantity = projection.ReturnedQuantity;
        }
        // A Hepsiburada line cancelled before it was packed has no package to project from: the cancelled quantity
        // Hepsiburada reports is kept (it only grows), also when the read is older than the order.
        if (isHepsiburada)
            foreach (var remoteLine in remote.Lines.Where(x => x.CancelledQuantity > 0))
            {
                var line = lines.GetValueOrDefault(remoteLine.ExternalLineId) ?? HepsiburadaOrderLineMatcher.FindExistingLine(remoteLine, existingLines, remote.Lines);
                if (line is null || projectedLineQuantities.ContainsKey(line.Id)) continue;
                var cancelled = Math.Min(line.OrderedQuantity, remoteLine.CancelledQuantity);
                if (cancelled <= line.CancelledQuantity) continue;
                line.CancelledQuantity = cancelled;
                if (db.Entry(line).State != EntityState.Added) { line.Version++; telemetryUpdatedCount++; }
            }
        foreach (var line in lines.Values.Distinct())
            OrderLineCancelTime.Apply(line, cancelledBefore.GetValueOrDefault(line.Id), OrderLineCancelTime.Reported(line.ExternalLineId, remote), now);
        // A Hepsiburada order with a product line the panel could not read keeps the amount Hepsiburada stated: its known
        // lines are not all of it, so summing them would understate the order (UnreadableOrderLines).
        var hasUnreadableLines = isHepsiburada && (remote.Packages.Any(package => package.UnreadableLineCount > 0)
            || packagesByExternalId.Values.Concat(db.ShipmentPackages.Local.Where(x => x.TenantId == tenantId && x.OrderId == order.Id))
                .Any(package => package.Status != ShipmentPackageStatus.Cancelled && package.UnreadableLineCount > 0));
        if (isHepsiburada && !hasUnreadableLines)
        {
            // Cancelled quantities are not part of the order value.
            var knownLineGross = lines.Values.Sum(line => line.UnitPrice * OrderLinePresentationPolicy.ActiveQuantity(line.OrderedQuantity, line.CancelledQuantity));
            if (knownLineGross > 0)
            {
                order.GrossAmount = knownLineGross;
                // The read's discount belongs to the lines it names; their cancelled share is taken off with it.
                var readLines = remote.Lines
                    .Select(remoteLine => lines.GetValueOrDefault(remoteLine.ExternalLineId))
                    .OfType<OrderLine>()
                    .Distinct()
                    .Select(line => (line.UnitPrice, line.OrderedQuantity, line.CancelledQuantity));
                var remainingDiscount = HepsiburadaOrderDiscountPolicy.RemainingDiscount(remote.DiscountAmount, readLines);
                if (remote.DiscountAmount > 0 || order.DiscountAmount == 0) order.DiscountAmount = Math.Min(knownLineGross, remainingDiscount);
                order.NetAmount = Math.Max(0m, knownLineGross - order.DiscountAmount);
            }
        }
        var orderPackages = packagesByExternalId.Values
            .Concat(db.ShipmentPackages.Local.Where(x => x.TenantId == tenantId && x.OrderId == order.Id))
            .GroupBy(package => package.Id)
            .Select(group => group.First())
            .ToList();
        // Superseded packages (split parents, resent originals) do not decide
        // the order status; while only a split parent is known the order keeps
        // its last status until the child packages are read.
        var statusSources = ShipmentPackageSupersession.StatusSources(orderPackages);
        var acceptedStatuses = statusSources?.ToList() ?? [];
        if (statusSources is not null)
        {
            var derivedStatus = isHepsiburada
                ? HepsiburadaOrderLifecycleStatusPolicy.AggregatePackages(acceptedStatuses)
                : ShipmentPackageStatusPolicy.Aggregate(acceptedStatuses);
            var lifecycleBaseline = acceptedStatuses.Count == 0
                ? HepsiburadaOrderLifecycleStatusPolicy.UnpackagedBaseline(order.DerivedStatus)
                : Wire(derivedStatus);
            // An undelivered package decides the order (Teslim Edilemedi); the order's own lifecycle status does not
            // move it on.
            var hasUndeliveredHepsiburadaPackage = isHepsiburada && acceptedStatuses.Contains(ShipmentPackageStatus.Undelivered);
            if (isHepsiburada && !hasUndeliveredHepsiburadaPackage && HepsiburadaOrderLifecycleStatusPolicy.Reconcile(
                    lifecycleBaseline,
                    remote.LifecycleStatus,
                    lines.Values.Select(line => line.RawStatus)) is { } hepsiburadaStatus)
                derivedStatus = hepsiburadaStatus;
            // Without a package an unknown lifecycle status is reviewed, never left as a new order.
            order.DerivedStatus = isHepsiburada && acceptedStatuses.Count == 0
                && HepsiburadaOrderLifecycleStatusPolicy.UnknownUnpackagedStatus(order.DerivedStatus, isNewOrder, orderIsFresh, remote.LifecycleStatus) is { } unknownStatus
                ? unknownStatus
                : Wire(derivedStatus);
            if (orderIsFresh
                && order.DerivedStatus.Trim().ToUpperInvariant() is not ("DELIVERED" or "CANCELLED" or "RETURNED")
                && OpenOrderLifecyclePolicy.IsHepsiburadaClaimOnlyWithoutPackage(
                    platformCode,
                    acceptedStatuses.Count,
                    lines.Values.Select(line => line.RawStatus),
                    remote.LifecycleStatus))
                order.DerivedStatus = "UNVERIFIED";
        }
        if (usesTrendyolPackageStatuses)
        {
            // A stream page carries one sibling package at a time: the totals
            // are rebuilt from every stored live package, not from the page
            // (also after an older page that still brought a newer package).
            var packagesInRead = remote.Packages.Select(package => package.ExternalPackageId).ToHashSet(StringComparer.Ordinal);
            if (OrderAmountProjectionPolicy.FromPackages(orderPackages, packagesInRead) is { } totals)
                (order.GrossAmount, order.DiscountAmount, order.NetAmount) = totals;
            else if (orderIsFresh && !isNewOrder)
                (order.GrossAmount, order.DiscountAmount, order.NetAmount) = previousAmounts;
        }
        if (isShopify)
        {
            var manualStatus = await db.OrderStatusHistory
                .Where(x => x.TenantId == tenantId && x.OrderId == order.Id && x.RawStatus.StartsWith(ShopifyManualStatusPolicy.RawStatusPrefix))
                .OrderByDescending(x => x.OccurredAt)
                .FirstOrDefaultAsync(cancellationToken);
            if (manualStatus is not null && ShopifyManualStatusPolicy.StillHolds(manualStatus.OccurredAt, ShopifyManualStatusPolicy.RemoteStatusAt(manualStatus.SourceEventId), order.LastRemoteModifiedAt, order.DerivedStatus))
            {
                order.DerivedStatus = manualStatus.CanonicalStatus;
                // Still valid after this read: the lists treat a manual status as current while it
                // is not older than the order's last marketplace change.
                if (manualStatus.OccurredAt < order.LastRemoteModifiedAt) manualStatus.OccurredAt = order.LastRemoteModifiedAt;
            }
        }
        // Every marketplace's order moves panel stock, Shopify's included: a sale on one channel lowers
        // the stock the others are sent.
        if (batch is null)
        {
            await AdoptLateMatchedLineStock(tenantId, connectionId, lateMatchedLines, cancellationToken);
            await OrderLineCostSnapshot.TakeAsync(db, tenantId, costSnapshotLines, cancellationToken);
            await ProjectOrderReservations(tenantId, connectionId, lines.Values.Select(line => (line, remote.LastModifiedAt)).ToList(), cancellationToken);
        }
        else
            batch.ReservationSources.AddRange(lines.Values.Select(line => (line, remote.LastModifiedAt)));
        // The same order read again (overlap window, status scan) rewrites nothing and sends no event (SyncNoOpWrites).
        SyncNoOpWrites.DropUnchangedOrder(db, order, existingLines.Concat(lines.Values), orderPackages);
        if (saveChanges) await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    // Cargo details are facts about the parcel, independent of whether the
    // event's status could be applied; known values are never blanked.
    private static bool MergePackageCargoMetadata(ShipmentPackage package, RemotePackage remotePackage)
    {
        var changed = false;
        if (!string.IsNullOrWhiteSpace(remotePackage.CargoProviderExternalId) && package.CargoProviderExternalId != remotePackage.CargoProviderExternalId) { package.CargoProviderExternalId = remotePackage.CargoProviderExternalId; changed = true; }
        if (!string.IsNullOrWhiteSpace(remotePackage.CargoTrackingNumber) && package.CargoTrackingNumber != remotePackage.CargoTrackingNumber) { package.CargoTrackingNumber = remotePackage.CargoTrackingNumber; changed = true; }
        if (!string.IsNullOrWhiteSpace(remotePackage.CargoBarcode) && package.CargoBarcode != remotePackage.CargoBarcode.Trim()) { package.CargoBarcode = remotePackage.CargoBarcode.Trim(); changed = true; }
        if (!string.IsNullOrWhiteSpace(remotePackage.OriginExternalPackageId) && package.OriginExternalPackageId != remotePackage.OriginExternalPackageId) { package.OriginExternalPackageId = remotePackage.OriginExternalPackageId; changed = true; }
        return changed;
    }

    private async Task UpsertShopifyFinancialObservations(Guid tenantId, Guid orderId, RemoteOrder remote, CancellationToken cancellationToken)
    {
        var rows = await db.OrderFinancialAllocations
            .Where(x => x.TenantId == tenantId && x.OrderId == orderId && x.SourceKey.StartsWith("shopify:"))
            .ToListAsync(cancellationToken);
        var byKey = rows.ToDictionary(x => x.SourceKey, StringComparer.Ordinal);

        Upsert("shopify:payment-status", $"PAYMENT_STATUS:{Short(remote.PaymentStatus, 48)}", 0m);
        Upsert("shopify:cancellation-status", $"CANCELLATION_STATUS:{Short(remote.CancellationStatus, 48)}", 0m);
        Upsert("shopify:refund-summary", $"REFUND_STATUS:{Short(remote.RefundStatus, 48)}", remote.RefundedAmount);

        var currentRefundKeys = (remote.Refunds ?? [])
            .Where(refund => !string.IsNullOrWhiteSpace(refund.ExternalRefundId))
            .Select(refund => $"shopify:refund:{Short(refund.ExternalRefundId, 180)}")
            .ToHashSet(StringComparer.Ordinal);
        foreach (var stale in rows.Where(row => row.SourceKey.StartsWith("shopify:refund:") && row.SourceKey != "shopify:refund-summary" && !currentRefundKeys.Contains(row.SourceKey)))
            db.OrderFinancialAllocations.Remove(stale);
        foreach (var refund in remote.Refunds ?? [])
        {
            if (string.IsNullOrWhiteSpace(refund.ExternalRefundId)) continue;
            Upsert($"shopify:refund:{Short(refund.ExternalRefundId, 180)}", "REFUND", refund.Amount, refund.Currency);
        }
        return;

        void Upsert(string sourceKey, string allocationType, decimal amount, string? currency = null)
        {
            if (byKey.TryGetValue(sourceKey, out var row))
            {
                row.AllocationType = allocationType;
                row.Amount = Math.Max(0, amount);
                row.Currency = Short(currency ?? remote.Currency, 3).ToUpperInvariant();
                return;
            }
            db.OrderFinancialAllocations.Add(new OrderFinancialAllocation
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenantId,
                OrderId = orderId,
                AllocationType = allocationType,
                Amount = Math.Max(0, amount),
                Currency = Short(currency ?? remote.Currency, 3).ToUpperInvariant(),
                SourceKey = sourceKey
            });
        }
    }

    // discoverySource ("stream" or "reconciliation"), when given, counts which path first saw a
    // Trendyol package become invoiced (plan step 0.9: do invoices uploaded outside the panel reach
    // the order stream, or only the reconciliation sweep?).
    private async Task MergeMarketplaceInvoiceState(ShipmentPackage package, RemotePackage remotePackage, CancellationToken cancellationToken, string? discoverySource = null)
    {
        var wasInvoiced = package.MarketplaceInvoiceStatus == MarketplaceInvoiceStatus.Invoiced;
        var observedAt = timeProvider.GetUtcNow();
        var observation = remotePackage.Invoice;
        var incomingStatus = MarketplaceInvoiceStatePolicy.FromRemote(
            observation?.RawStatus,
            remotePackage.RawStatus,
            observation?.InvoiceNumber,
            observation?.InvoiceUrl);
        var applyStatus = MarketplaceInvoiceStatePolicy.ShouldApply(
                package.MarketplaceInvoiceStatus,
                package.MarketplaceInvoiceSourceUpdatedAt,
                package.MarketplaceInvoiceObservedAt,
                incomingStatus,
                observation?.SourceUpdatedAt,
                observedAt);
        var applyReferences = incomingStatus != MarketplaceInvoiceStatus.Unknown
            && MarketplaceInvoiceStatePolicy.ShouldApplyReferenceUpdate(
            package.MarketplaceInvoiceSourceUpdatedAt,
            package.MarketplaceInvoiceObservedAt,
            observation?.SourceUpdatedAt,
            observedAt);
        var referenceChanged = false;
        if (applyReferences && incomingStatus == MarketplaceInvoiceStatus.NotInvoiced)
        {
            referenceChanged = package.MarketplaceInvoiceNumber is not null || package.MarketplaceInvoiceUrl is not null;
            if (referenceChanged)
                MarketplaceInvoiceReferenceHistoryRecorder.RecordPreviousReferences(db, package, null, null, observedAt, "ORDER_RECONCILIATION");
            package.MarketplaceInvoiceNumber = null;
            package.MarketplaceInvoiceUrl = null;
        }
        else if (applyReferences)
        {
            var references = MarketplaceInvoiceStatePolicy.MergeReferences(
                package.MarketplaceInvoiceNumber,
                package.MarketplaceInvoiceUrl,
                observation?.InvoiceNumber,
                observation?.InvoiceUrl,
                observation?.ReferencesAreAuthoritative == true);
            var invoiceNumber = references.InvoiceNumber;
            var invoiceUrl = references.InvoiceUrl;
            referenceChanged = !string.Equals(invoiceNumber, package.MarketplaceInvoiceNumber, StringComparison.Ordinal)
                || !string.Equals(invoiceUrl, package.MarketplaceInvoiceUrl, StringComparison.Ordinal);
            if (referenceChanged)
                MarketplaceInvoiceReferenceHistoryRecorder.RecordPreviousReferences(db, package, invoiceNumber, invoiceUrl, observedAt, "ORDER_RECONCILIATION");
            package.MarketplaceInvoiceNumber = invoiceNumber;
            package.MarketplaceInvoiceUrl = invoiceUrl;
        }

        if (applyStatus)
        {
            package.MarketplaceInvoiceStatus = incomingStatus;
            package.MarketplaceInvoiceRawStatus = observation?.RawStatus ?? package.MarketplaceInvoiceRawStatus;
            package.MarketplaceInvoiceSourceUpdatedAt = observation?.SourceUpdatedAt ?? package.MarketplaceInvoiceSourceUpdatedAt;
            package.MarketplaceInvoiceObservedAt = observedAt;
            if (discoverySource is not null && !wasInvoiced && incomingStatus == MarketplaceInvoiceStatus.Invoiced)
                await NoteInvoiceDiscoveryAsync(package, discoverySource, observedAt, cancellationToken);
        }
        else if (referenceChanged)
        {
            package.MarketplaceInvoiceSourceUpdatedAt = observation?.SourceUpdatedAt ?? package.MarketplaceInvoiceSourceUpdatedAt;
            package.MarketplaceInvoiceObservedAt = observedAt;
        }
        if (!applyStatus && !referenceChanged) return;

        package.MarketplaceInvoiceObservedAt = observedAt;
        package.UpdatedAt = timeProvider.GetUtcNow();
        package.Version++;
        telemetryUpdatedCount++;

        // New invoices belong to the order rather than one shipment package.
        // Include those alongside legacy package-scoped invoices, and fail
        // closed if history contains more than one candidate.
        var matchingInvoices = await db.Invoices
            .Where(x => x.TenantId == package.TenantId && x.OrderId == package.OrderId
                && x.OriginalInvoiceId == null && x.SequencePurpose == "SALE"
                && (x.PackageId == null || x.PackageId == package.Id))
            .OrderBy(x => x.CreatedAt)
            .ToListAsync(cancellationToken);
        if (matchingInvoices.Count != 1) return;
        var invoice = matchingInvoices[0];

        if (incomingStatus == MarketplaceInvoiceStatus.NotInvoiced)
        {
            var deliveryState = await db.MarketplaceDeliveryStates.SingleOrDefaultAsync(
                x => x.TenantId == invoice.TenantId && x.InvoiceId == invoice.Id && x.PackageId == package.Id,
                cancellationToken);
            var previouslyConfirmed = string.Equals(deliveryState?.Status, "CONFIRMED", StringComparison.Ordinal);
            var hasDeliveryAttempt = InvoiceDeliveryRecoveryPolicy.HasDeliveryAttempt(deliveryState?.ExternalIdempotencyKey);
            var shouldMarkNotPresent = InvoiceDeliveryRecoveryPolicy.ShouldMarkMarketplaceInvoiceNotPresent(invoice.Status, invoice.LastErrorCode, incomingStatus);
            var shouldQueueRedelivery = InvoiceDeliveryRecoveryPolicy.ShouldQueueAutomaticRedelivery(invoice.Status, invoice.LastErrorCode, incomingStatus, hasDeliveryAttempt);
            if (previouslyConfirmed || shouldMarkNotPresent || shouldQueueRedelivery)
            {
                await RecordMarketplaceInvoiceDeliveryObservationAsync(invoice, package, "REVOKED", observedAt, cancellationToken);
                await UpdateOrderInvoiceDeliveryStatusAsync(invoice, observedAt, cancellationToken,
                    InvoiceDeliveryRecoveryPolicy.MarketplaceInvoiceNotPresentErrorCode);
                await QueueAutomaticInvoiceRedelivery(invoice, package, cancellationToken);
            }
            else if (InvoiceDeliveryRecoveryPolicy.IsNeverDeliveredMarkedNotPresent(invoice.Status, invoice.LastErrorCode, hasDeliveryAttempt,
                await db.InvoiceDocuments.AsNoTracking().AnyAsync(document => document.TenantId == invoice.TenantId && document.InvoiceId == invoice.Id, cancellationToken)))
            {
                // An earlier read marked an invoice nothing was ever sent for as removed: say what happened instead
                // (no document, not delivered). Nothing is queued; the delivery waits for the operator.
                invoice.LastErrorCode = InvoiceDeliveryRecoveryPolicy.DocumentMissingNotDeliveredErrorCode;
                invoice.UpdatedAt = observedAt;
                invoice.Version++;
            }
            return;
        }

        if (incomingStatus == MarketplaceInvoiceStatus.Received
            && observation?.ReferencesAreAuthoritative == true
            && invoice.Status == InvoiceStatus.Completed)
        {
            await RecordMarketplaceInvoiceDeliveryObservationAsync(invoice, package, "REVIEW_REQUIRED", observedAt, cancellationToken);
            await UpdateOrderInvoiceDeliveryStatusAsync(invoice, observedAt, cancellationToken);
            return;
        }

        if (incomingStatus == MarketplaceInvoiceStatus.Invoiced && invoice.Status is InvoiceStatus.Submitted or InvoiceStatus.Accepted or InvoiceStatus.MarketplacePending or InvoiceStatus.Completed)
        {
            var remoteInvoiceNumber = observation?.InvoiceNumber?.Trim();
            var localInvoiceLink = await db.InvoiceDocuments.AsNoTracking()
                .Where(document => document.TenantId == invoice.TenantId && document.InvoiceId == invoice.Id && document.PermanentUrl != null)
                .OrderByDescending(document => document.CreatedAt)
                .Select(document => document.PermanentUrl)
                .FirstOrDefaultAsync(cancellationToken);
            var exactProof = InvoiceDeliveryProofPolicy.HasExactProof(
                new(package.ExternalPackageId, observation?.RawStatus ?? string.Empty, true,
                    observation?.InvoiceNumber, observation?.InvoiceUrl, observation?.ReferencesAreAuthoritative == true),
                invoice.InvoiceNumber,
                localInvoiceLink)
                // A read without references is weaker than what the package already holds: when that is exactly this
                // invoice's number and link, the read confirms it instead of sending a confirmed delivery to review.
                || string.IsNullOrWhiteSpace(observation?.InvoiceNumber) && string.IsNullOrWhiteSpace(observation?.InvoiceUrl)
                    && InvoiceDeliveryRecoveryPolicy.StoredReferencesProveDelivery(
                        package.MarketplaceInvoiceNumber, package.MarketplaceInvoiceUrl, invoice.InvoiceNumber, localInvoiceLink);
            if (exactProof)
            {
                await RecordExactMarketplaceInvoiceProofAsync(invoice, package, observedAt, cancellationToken);
                await UpdateOrderInvoiceDeliveryStatusAsync(invoice, observedAt, cancellationToken);
            }
            else
            {
                var deliveryState = await db.MarketplaceDeliveryStates.SingleOrDefaultAsync(
                    x => x.TenantId == invoice.TenantId && x.InvoiceId == invoice.Id && x.PackageId == package.Id,
                    cancellationToken);
                if (InvoiceDeliveryRecoveryPolicy.ShouldPreservePreviouslyConfirmedDelivery(
                    invoice.Status,
                    deliveryState?.Status,
                    remoteInvoiceNumber))
                    return;

                // The send was preceded by a read that found the package empty, and Hepsiburada has reported an invoice after that write.
                // The platform cannot say which invoice it holds, so the write we just made is the only explanation; later rewrites (REVOKED) reset this.
                var acceptedAttemptAt = deliveryState is null || incomingStatus != MarketplaceInvoiceStatus.Invoiced
                    || deliveryState.Status is not ("UNKNOWN" or "REVIEW_REQUIRED")
                    ? (DateTimeOffset?)null
                    : await db.MarketplaceDeliveries.AsNoTracking()
                        .Where(x => x.TenantId == invoice.TenantId && x.InvoiceId == invoice.Id && x.PackageId == package.Id
                            && x.ErrorCode == InvoiceDeliveryRecoveryPolicy.HepsiburadaAcceptedButReadbackMissingErrorCode)
                        .OrderByDescending(x => x.CreatedAt).Select(x => (DateTimeOffset?)x.CreatedAt).FirstOrDefaultAsync(cancellationToken);
                var revokedAfterAttempt = acceptedAttemptAt is { } attemptAt && await db.MarketplaceDeliveries.AsNoTracking()
                    .AnyAsync(x => x.TenantId == invoice.TenantId && x.InvoiceId == invoice.Id && x.PackageId == package.Id && x.Status == "REVOKED" && x.CreatedAt > attemptAt, cancellationToken);
                var sameAttemptProof = deliveryState is not null && acceptedAttemptAt is { } acceptedAt && !revokedAfterAttempt
                    && observedAt is { } readAt && readAt > acceptedAt;

                if (sameAttemptProof && deliveryState is not null)
                {
                    RecordAcceptedHepsiburadaReadbackProof(deliveryState, package, observedAt);
                    await UpdateOrderInvoiceDeliveryStatusAsync(invoice, observedAt, cancellationToken);
                }
                else
                {
                    await RecordMarketplaceInvoiceDeliveryObservationAsync(invoice, package, "REVIEW_REQUIRED", observedAt, cancellationToken);
                    SetInvoiceDeliveryState(invoice, InvoiceStatus.MarketplacePending,
                        InvoiceDeliveryRecoveryPolicy.MarketplaceInvoiceIdentityMismatchErrorCode, observedAt);
                }
            }
        }
        else if (incomingStatus == MarketplaceInvoiceStatus.Rejected && invoice.Status is InvoiceStatus.Submitted or InvoiceStatus.Accepted or InvoiceStatus.MarketplacePending)
        {
            var delivery = await db.MarketplaceDeliveryStates.AsNoTracking()
                .SingleOrDefaultAsync(x => x.TenantId == package.TenantId && x.InvoiceId == invoice.Id && x.PackageId == package.Id, cancellationToken);
            var rejectedAfterDeliveryAttempt = delivery is not null
                && observation?.SourceUpdatedAt is { } remoteUpdatedAt
                && remoteUpdatedAt > delivery.CreatedAt;
            if (rejectedAfterDeliveryAttempt)
            {
                invoice.Status = InvoiceStatus.MarketplaceFailed;
                invoice.LastErrorCode = "REMOTE_INVOICE_REJECTED";
                invoice.UpdatedAt = observedAt;
                invoice.Version++;
            }
        }
    }

    private async Task QueueAutomaticInvoiceRedelivery(Invoice invoice, ShipmentPackage package, CancellationToken cancellationToken)
    {
        if (invoiceBilling is null) return;
        // An invoice in review is the owner's, and a delivery job of this package that waits, runs or stopped for review
        // already stands for it: no second job next to it under another time bucket (live 13 Oct 2026).
        if (invoice.Status == InvoiceStatus.ManualReview
            || await InvoiceDeliveryJobHygiene.AutomaticDeliveryHeldAsync(db, invoice.TenantId, invoice.Id, package.Id, reviewHolds: true, cancellationToken)) return;
        var state = db.ChangeTracker.Entries<MarketplaceDeliveryState>()
            .Where(entry => entry.Entity.TenantId == invoice.TenantId
                && entry.Entity.InvoiceId == invoice.Id
                && entry.Entity.PackageId == package.Id)
            .Select(entry => entry.Entity)
            .SingleOrDefault();
        var stateVersion = state?.Version ?? await db.MarketplaceDeliveryStates.AsNoTracking()
            .Where(item => item.TenantId == invoice.TenantId && item.InvoiceId == invoice.Id && item.PackageId == package.Id)
            .Select(item => (long?)item.Version)
            .SingleOrDefaultAsync(cancellationToken);
        if (stateVersion is null) return;

        var retryBucket = timeProvider.GetUtcNow().ToUnixTimeSeconds() / 900;
        var idempotencyKey = $"invoice-reconcile-redelivery:{invoice.Id:N}:{package.Id:N}:{stateVersion.Value}:{retryBucket}";
        await invoiceBilling.EnqueueDeliveryAsync(invoice.TenantId, invoice.Id, idempotencyKey,
            $"invoice-reconcile:{invoice.Id:N}:{package.Id:N}", cancellationToken);
    }

    private async Task RecordMarketplaceInvoiceDeliveryObservationAsync(
        Invoice invoice,
        ShipmentPackage package,
        string status,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken)
    {
        var state = await db.MarketplaceDeliveryStates.SingleOrDefaultAsync(
            x => x.TenantId == invoice.TenantId && x.InvoiceId == invoice.Id && x.PackageId == package.Id,
            cancellationToken);
        if (state?.Status == status) return;

        if (state is null)
        {
            var latestAttemptNumber = await db.MarketplaceDeliveries.AsNoTracking()
                .Where(x => x.TenantId == invoice.TenantId && x.InvoiceId == invoice.Id && x.PackageId == package.Id)
                .Select(x => (int?)x.AttemptNumber)
                .MaxAsync(cancellationToken) ?? 0;
            var proofKey = $"marketplace-observation:{invoice.Id:N}:{package.Id:N}";
            var proofHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                $"{invoice.Id:N}|{package.Id:N}|{status}|{package.MarketplaceInvoiceNumber}|{package.MarketplaceInvoiceUrl}")));
            state = new MarketplaceDeliveryState
            {
                Id = Guid.CreateVersion7(),
                TenantId = invoice.TenantId,
                InvoiceId = invoice.Id,
                ConnectionId = package.ConnectionId,
                PackageId = package.Id,
                AttemptNumber = latestAttemptNumber,
                ExternalIdempotencyKey = proofKey,
                RequestHash = proofHash,
                DeliveryType = "LINK",
                Status = "UNKNOWN",
                ExternalReference = package.ExternalPackageId,
                CreatedAt = observedAt,
                UpdatedAt = observedAt,
                Version = 1
            };
            db.MarketplaceDeliveryStates.Add(state);
        }

        var sequence = state.AttemptNumber + 1;
        db.MarketplaceDeliveries.Add(new MarketplaceDelivery
        {
            Id = Guid.CreateVersion7(),
            TenantId = state.TenantId,
            InvoiceId = state.InvoiceId,
            ConnectionId = state.ConnectionId,
            PackageId = state.PackageId,
            AttemptNumber = sequence,
            IdempotencyKey = $"delivery-observation:{state.Id:N}:{sequence}",
            ExternalIdempotencyKey = state.ExternalIdempotencyKey,
            RequestHash = state.RequestHash,
            DeliveryType = state.DeliveryType,
            Status = status,
            ExternalReference = package.ExternalPackageId,
            ErrorCode = status == "REVOKED" ? InvoiceDeliveryRecoveryPolicy.MarketplaceInvoiceNotPresentErrorCode : InvoiceDeliveryRecoveryPolicy.MarketplaceInvoiceIdentityMismatchErrorCode,
            CreatedAt = observedAt,
            CompletedAt = observedAt
        });
        state.AttemptNumber = sequence;
        state.Status = status;
        state.ExternalReference = package.ExternalPackageId;
        state.ErrorCode = status == "REVOKED" ? InvoiceDeliveryRecoveryPolicy.MarketplaceInvoiceNotPresentErrorCode : InvoiceDeliveryRecoveryPolicy.MarketplaceInvoiceIdentityMismatchErrorCode;
        state.UpdatedAt = observedAt;
        state.CompletedAt = observedAt;
        state.Version++;
    }

    private async Task RecordExactMarketplaceInvoiceProofAsync(Invoice invoice, ShipmentPackage package, DateTimeOffset observedAt, CancellationToken cancellationToken)
    {
        var state = await db.MarketplaceDeliveryStates.SingleOrDefaultAsync(
            x => x.TenantId == invoice.TenantId && x.InvoiceId == invoice.Id && x.PackageId == package.Id,
            cancellationToken);
        var proofKey = $"remote-invoice-proof:{invoice.Id:N}:{package.Id:N}";
        var proofHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{invoice.Id:N}|{package.Id:N}|{invoice.InvoiceNumber?.Trim()}|{package.MarketplaceInvoiceNumber?.Trim()}|{package.MarketplaceInvoiceUrl?.Trim()}")));
        if (state is null)
        {
            state = new MarketplaceDeliveryState
            {
                Id = Guid.CreateVersion7(),
                TenantId = invoice.TenantId,
                InvoiceId = invoice.Id,
                ConnectionId = package.ConnectionId,
                PackageId = package.Id,
                AttemptNumber = 0,
                ExternalIdempotencyKey = proofKey,
                RequestHash = proofHash,
                DeliveryType = "LINK",
                Status = "CONFIRMED",
                ExternalReference = package.ExternalPackageId,
                CreatedAt = observedAt,
                UpdatedAt = observedAt,
                CompletedAt = observedAt,
                Version = 1
            };
            db.MarketplaceDeliveryStates.Add(state);
            return;
        }
        // The same proof read again changes nothing.
        if (state.Status == "CONFIRMED" && state.ErrorCode is null && state.ExternalIdempotencyKey == proofKey && state.RequestHash == proofHash
            && state.ConnectionId == package.ConnectionId && state.ExternalReference == package.ExternalPackageId)
            return;

        state.ConnectionId = package.ConnectionId;
        state.PackageId = package.Id;
        state.ExternalIdempotencyKey = proofKey;
        state.RequestHash = proofHash;
        state.DeliveryType = "LINK";
        state.Status = "CONFIRMED";
        state.ExternalReference = package.ExternalPackageId;
        state.ErrorCode = null;
        state.UpdatedAt = observedAt;
        state.CompletedAt = observedAt;
        state.Version++;
    }

    private async Task UpdateOrderInvoiceDeliveryStatusAsync(
        Invoice invoice,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken,
        string? incompleteErrorCode = null)
    {
        var targetPackageIds = invoice.PackageId is { } legacyPackageId
            ? new[] { legacyPackageId }
            : await db.ShipmentPackages.AsNoTracking()
                .Where(x => x.TenantId == invoice.TenantId && x.OrderId == invoice.OrderId && x.Status != ShipmentPackageStatus.Cancelled)
                .Select(x => x.Id)
                .ToArrayAsync(cancellationToken);
        var states = await db.MarketplaceDeliveryStates
            .Where(x => x.TenantId == invoice.TenantId && x.InvoiceId == invoice.Id
                && x.PackageId != null && targetPackageIds.Contains(x.PackageId.Value))
            .ToListAsync(cancellationToken);
        var trackedStates = db.ChangeTracker.Entries<MarketplaceDeliveryState>()
            .Where(entry => (entry.State is EntityState.Added or EntityState.Modified)
                && entry.Entity.TenantId == invoice.TenantId && entry.Entity.InvoiceId == invoice.Id
                && entry.Entity.PackageId.HasValue && targetPackageIds.Contains(entry.Entity.PackageId.Value))
            .Select(entry => entry.Entity);
        var confirmedPackageIds = states.Concat(trackedStates)
            .DistinctBy(x => x.Id)
            .Where(x => string.Equals(x.Status, "CONFIRMED", StringComparison.Ordinal) && x.PackageId.HasValue)
            .Select(x => x.PackageId!.Value)
            .ToHashSet();
        var allConfirmed = MarketplaceInvoiceDeliveryEvidencePolicy.AllTargetsConfirmed(targetPackageIds, confirmedPackageIds);
        SetInvoiceDeliveryState(invoice,
            allConfirmed ? InvoiceStatus.Completed
                : incompleteErrorCode is not null ? InvoiceStatus.MarketplaceFailed
                : InvoiceStatus.MarketplacePending,
            allConfirmed ? null : incompleteErrorCode,
            observedAt);
    }

    // A read that finds the invoice as it already is writes nothing: Version and UpdatedAt move only on a real change,
    // so an unchanged invoice is neither "updated" on every scan nor raced by the optimistic lock.
    // An invoice in ManualReview is the owner's: a marketplace read never moves it out nor rewrites its reason
    // (InvoiceDeliveryGuardPolicy.BackgroundMayMove).
    internal static bool SetInvoiceDeliveryState(Invoice invoice, InvoiceStatus status, string? lastErrorCode, DateTimeOffset at)
    {
        if (invoice.Status == InvoiceStatus.ManualReview) return false;
        if (!InvoiceDeliveryRecoveryPolicy.DeliveryStateChanges(invoice.Status, invoice.LastErrorCode, status, lastErrorCode)) return false;
        invoice.Status = status;
        invoice.LastErrorCode = lastErrorCode;
        invoice.UpdatedAt = at;
        invoice.Version++;
        return true;
    }

    private void RecordAcceptedHepsiburadaReadbackProof(MarketplaceDeliveryState state, ShipmentPackage package, DateTimeOffset observedAt)
    {
        var sequence = state.AttemptNumber + 1;
        db.MarketplaceDeliveries.Add(new MarketplaceDelivery
        {
            Id = Guid.CreateVersion7(),
            TenantId = state.TenantId,
            InvoiceId = state.InvoiceId,
            ConnectionId = state.ConnectionId,
            PackageId = state.PackageId,
            AttemptNumber = sequence,
            IdempotencyKey = $"delivery-event:{state.Id:N}:{sequence}",
            ExternalIdempotencyKey = state.ExternalIdempotencyKey,
            RequestHash = state.RequestHash,
            DeliveryType = state.DeliveryType,
            Status = "CONFIRMED",
            ExternalReference = package.ExternalPackageId,
            CreatedAt = observedAt,
            CompletedAt = observedAt
        });
        state.AttemptNumber = sequence;
        state.Status = "CONFIRMED";
        state.ExternalReference = package.ExternalPackageId;
        state.ErrorCode = null;
        state.UpdatedAt = observedAt;
        state.CompletedAt = observedAt;
        state.Version++;
    }

    // The variant links and panel variants an order read's lines may name, read once per read.
    private sealed record OrderLineVariantCandidates(IReadOnlyCollection<OrderLineLinkCandidate> Links, IReadOnlyCollection<OrderLinePanelCandidate> Variants)
    {
        public static readonly OrderLineVariantCandidates Empty = new([], []);
        public OrderLineVariantMatch Match(OrderLineCodes codes) => OrderLineVariantMatcher.Match(codes, Links, Variants);
    }

    private static List<OrderLineCodes> OrderLineCodesOf(string? platformCode, IEnumerable<RemoteOrderLine> lines) =>
        lines.Select(line => OrderLineVariantMatcher.Codes(platformCode, line.Sku, line.Barcode, line.SourceSnapshotJson)).ToList();

    private async Task<OrderLineVariantCandidates> LoadOrderLineVariantCandidates(Guid tenantId, Guid connectionId, IReadOnlyCollection<OrderLineCodes> lines, CancellationToken cancellationToken)
    {
        // Links are compared trimmed and upper-cased (OrderLineVariantMatcher.LinkKey); panel keys in every stored
        // form, since a stored key may still be in a legacy form until the variant is saved again (K1).
        var linkCodes = OrderLineVariantMatcher.LinkLookupCodes(lines);
        var panelKeys = OrderLineVariantMatcher.PanelLookupKeys(lines);
        if (linkCodes.Length == 0 && panelKeys.Length == 0) return OrderLineVariantCandidates.Empty;
        List<OrderLineLinkCandidate> links = linkCodes.Length == 0
            ? []
            : await db.MarketplaceVariantLinks.AsNoTracking()
                .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId
                    && ((x.ExternalBarcode != null && linkCodes.Contains(x.ExternalBarcode.Trim().ToUpper()))
                        || (x.ExternalSku != null && linkCodes.Contains(x.ExternalSku.Trim().ToUpper()))
                        || linkCodes.Contains(x.ExternalId.Trim().ToUpper())))
                .OrderBy(x => x.Id)
                .Select(x => new OrderLineLinkCandidate(x.VariantId, x.ExternalId, x.ExternalBarcode, x.ExternalSku))
                .ToListAsync(cancellationToken);
        List<OrderLinePanelCandidate> variants = panelKeys.Length == 0
            ? []
            : await db.ProductVariants.AsNoTracking()
                .Where(x => x.TenantId == tenantId && (panelKeys.Contains(x.SkuNormalized) || (x.BarcodeNormalized != null && panelKeys.Contains(x.BarcodeNormalized))))
                .OrderBy(x => x.Id)
                .Select(x => new OrderLinePanelCandidate(x.Id, x.SkuNormalized, x.BarcodeNormalized))
                .ToListAsync(cancellationToken);
        return new OrderLineVariantCandidates(links, variants);
    }

    // A line that finds its variant only after it was stored (OrderLineLateMatchStockPolicy): when its order has
    // already shipped, been delivered, returned or cancelled, the line's stock baseline is what it commits, so its first
    // projection takes nothing from stock and later changes move stock from there. A line that already has stock
    // movements, a reservation or the carry rows of a reset under its marketplace ids is left to ProjectOrderReservations.
    private async Task AdoptLateMatchedLineStock(Guid tenantId, Guid connectionId, IReadOnlyCollection<OrderLine> lines, CancellationToken cancellationToken)
    {
        var candidates = lines.Where(line => line.VariantId is not null).DistinctBy(line => line.Id).ToList();
        if (candidates.Count == 0) return;
        var orderIds = candidates.Select(line => line.OrderId).Distinct().ToArray();
        var orders = db.Orders.Local.Where(x => x.TenantId == tenantId && orderIds.Contains(x.Id))
            .ToDictionary(x => x.Id, x => (x.DerivedStatus, x.ExternalOrderId));
        var unloaded = orderIds.Where(id => !orders.ContainsKey(id)).ToArray();
        if (unloaded.Length > 0)
            foreach (var row in await db.Orders.AsNoTracking().Where(x => x.TenantId == tenantId && unloaded.Contains(x.Id)).Select(x => new { x.Id, x.DerivedStatus, x.ExternalOrderId }).ToListAsync(cancellationToken))
                orders[row.Id] = (row.DerivedStatus, row.ExternalOrderId);
        var settled = candidates.Where(line => orders.TryGetValue(line.OrderId, out var order) && OrderLineLateMatchStockPolicy.OrderHasSettled(order.DerivedStatus)).ToList();
        if (settled.Count == 0) return;
        var variantIds = settled.Select(line => line.VariantId!.Value).Distinct().ToArray();
        var itemByVariant = (await db.InventoryItems.AsNoTracking()
                .Where(x => x.TenantId == tenantId && x.LocationCode == "MAIN" && variantIds.Contains(x.VariantId))
                .Select(x => new { x.Id, x.VariantId })
                .ToListAsync(cancellationToken))
            .ToDictionary(x => x.VariantId, x => x.Id);
        foreach (var item in db.InventoryItems.Local.Where(x => x.TenantId == tenantId && x.LocationCode == "MAIN" && variantIds.Contains(x.VariantId))) itemByVariant.TryAdd(item.VariantId, item.Id);
        var sourceKeys = settled.ToDictionary(line => line.Id, line => new[] { line.Id.ToString("D"), OrderInventoryStockPolicy.ResetCarryKey(connectionId, orders[line.OrderId].ExternalOrderId, line.ExternalLineId) });
        var sourceIds = sourceKeys.Values.SelectMany(keys => keys).Distinct(StringComparer.Ordinal).ToArray();
        var recorded = (await db.StockLedgerEntries.AsNoTracking().Where(x => x.TenantId == tenantId && x.SourceType == "ORDER_LINE" && sourceIds.Contains(x.SourceId)).Select(x => x.SourceId).Distinct().ToListAsync(cancellationToken))
            .Concat(await db.StockReservations.AsNoTracking().Where(x => x.TenantId == tenantId && x.SourceType == "ORDER_LINE" && sourceIds.Contains(x.SourceId)).Select(x => x.SourceId).Distinct().ToListAsync(cancellationToken))
            .Concat(db.StockLedgerEntries.Local.Where(x => x.TenantId == tenantId && x.SourceType == "ORDER_LINE" && sourceIds.Contains(x.SourceId)).Select(x => x.SourceId))
            .ToHashSet(StringComparer.Ordinal);
        var now = timeProvider.GetUtcNow();
        foreach (var line in settled)
        {
            if (!itemByVariant.TryGetValue(line.VariantId!.Value, out var itemId) || sourceKeys[line.Id].Any(recorded.Contains)) continue;
            var sourceId = line.Id.ToString("D");
            StockMovementWriter.Record(db, tenantId, itemId, OrderLineLateMatchStockPolicy.AdoptedBaseline(line.OrderedQuantity, line.CancelledQuantity, line.ShippedQuantity, line.DeliveredQuantity), BaselineSource(itemId, sourceId, line.OrderId, now, now));
            recorded.Add(sourceId);
        }
    }

    internal const string UnmatchedOrderLineIssueCode = "ORDER_LINE_VARIANT_UNMATCHED";
    private const int OrderLineRematchBatchSize = 2000;

    internal sealed record OrderLineRematchOutcome(int Examined, int Matched, int Unmatched);

    // An order line is matched when a read stores it, and a delivered order is rarely read again: a line imported
    // before its panel variant or variant link existed (a catalog built after the orders, a variant added later) would
    // stay without stock effect for good. Every order sync of the connection matches its unmatched lines again against
    // the panel as it is now (the same OrderLineVariantMatcher), takes their stock like a late read would
    // (OrderLineLateMatchStockPolicy, ProjectOrderReservations) and keeps one open issue per code no variant answers,
    // so the owner can create or map it. Idempotent: a matched line is never looked at again.
    // SIPARIS-STOK-2: a connection with more unmatched lines than one batch is read in turns. Each run reads the next
    // batch after the last line Id the cursor keeps (fully cancelled lines are left out: they take no stock); a batch of
    // fewer lines ends the pass and the next run starts again from the first. The issues of codes no longer unmatched are
    // resolved when a whole pass has been read: the cursor's LastModifiedWatermark keeps when the pass started, every
    // batch of a pass marks the issues it still sees, and the last batch resolves the ones no batch saw.
    internal const string OrderLineRematchCursor = "ORDER_LINE_REMATCH";

    internal async Task<OrderLineRematchOutcome> RematchUnmatchedOrderLines(Guid tenantId, Guid connectionId, CancellationToken cancellationToken, int batchSize = OrderLineRematchBatchSize)
    {
        var platformCode = await db.PlatformConnections.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.Id == connectionId)
            .Select(x => x.PlatformCode)
            .SingleOrDefaultAsync(cancellationToken);
        if (platformCode is null) return new(0, 0, 0);
        var cursor = await Cursor(tenantId, connectionId, OrderLineRematchCursor, cancellationToken);
        var afterId = Guid.TryParse(cursor.OpaqueCursor, out var lastId) ? lastId : Guid.Empty;
        var passStartedAt = afterId == Guid.Empty ? timeProvider.GetUtcNow() : cursor.LastModifiedWatermark ?? timeProvider.GetUtcNow();
        var unmatchedLines = from line in db.OrderLines
                             join order in db.Orders on new { line.TenantId, line.OrderId } equals new { order.TenantId, OrderId = order.Id }
                             where line.TenantId == tenantId && order.ConnectionId == connectionId && line.VariantId == null
                                 && (line.OrderedQuantity <= 0 || line.CancelledQuantity < line.OrderedQuantity)
                             select new { Line = line, order.OrderNumber, order.LastRemoteModifiedAt };
        if (afterId != Guid.Empty) unmatchedLines = unmatchedLines.Where(x => x.Line.Id.CompareTo(afterId) > 0);
        var rows = await unmatchedLines.OrderBy(x => x.Line.Id).Take(batchSize).ToListAsync(cancellationToken);
        var passEnded = rows.Count < batchSize;
        // A pass read in one batch knows every open code at once; only a pass of several batches marks what it saw.
        var multiBatchPass = afterId != Guid.Empty || !passEnded;
        cursor.OpaqueCursor = passEnded ? null : rows[^1].Line.Id.ToString("D");
        cursor.LastModifiedWatermark = passEnded ? null : passStartedAt;
        cursor.LastAttemptAt = timeProvider.GetUtcNow();
        var issuePrefix = $"order-line-variant-unmatched:{connectionId:N}:";
        var codes = rows.ToDictionary(row => row.Line.Id, row => OrderLineVariantMatcher.Codes(platformCode, row.Line.Sku, row.Line.Barcode, row.Line.SourceSnapshotJson));
        var candidates = rows.Count == 0 ? OrderLineVariantCandidates.Empty : await LoadOrderLineVariantCandidates(tenantId, connectionId, codes.Values, cancellationToken);
        var matched = new List<(OrderLine Line, DateTimeOffset ModifiedAt)>();
        var unmatched = new List<(OrderLine Line, string OrderNumber, OrderLineMatchKind Kind)>();
        foreach (var row in rows)
        {
            var match = candidates.Match(codes[row.Line.Id]);
            if (match.VariantId is { } variantId)
            {
                row.Line.VariantId = variantId;
                row.Line.Version++;
                matched.Add((row.Line, row.LastRemoteModifiedAt));
            }
            else unmatched.Add((row.Line, row.OrderNumber, match.Kind));
        }
        if (matched.Count > 0)
        {
            await AdoptLateMatchedLineStock(tenantId, connectionId, matched.Select(x => x.Line).ToList(), cancellationToken);
            await OrderLineCostSnapshot.TakeAsync(db, tenantId, matched.Select(x => x.Line), cancellationToken);
            await ProjectOrderReservations(tenantId, connectionId, matched, cancellationToken);
        }

        var openKeys = new HashSet<string>(StringComparer.Ordinal);
        // A line cancelled in full takes no stock whether it matches or not: it is not read here (SIPARIS-STOK-2: such
        // lines filled the first batch for good) and raises no notice; the notice it raised before is resolved below
        // (live 5 Nov 2026: Shopify #R1013's two cancelled lines kept two open notices).
        foreach (var group in unmatched.Where(x => !OrderLineFullyCancelled(x.Line)).GroupBy(x => UnmatchedOrderLineIssueKey(codes[x.Line.Id]), StringComparer.Ordinal))
        {
            var key = issuePrefix + group.Key;
            openKeys.Add(key);
            await RecordUnmatchedOrderLineIssue(tenantId, key, UnmatchedOrderLineSummary(platformCode, codes[group.First().Line.Id], group.First().Line.TitleSnapshot, group.Select(x => x.OrderNumber).ToList(), group.Any(x => x.Kind == OrderLineMatchKind.Ambiguous)), cancellationToken, multiBatchPass ? passStartedAt : null);
        }
        // Issues of codes that now match (or whose lines are gone) are resolved once a whole pass was read: a pass of one
        // batch resolves every issue it did not see; the last batch of a longer pass those no batch of it saw
        // (LastSeenAt before the pass started). A batch in the middle resolves only the codes it matched.
        var resolvable = !passEnded
            ? await QueryResolvableIssues(matched.Select(x => issuePrefix + UnmatchedOrderLineIssueKey(codes[x.Line.Id])).Distinct(StringComparer.Ordinal).ToArray())
            : multiBatchPass
                ? await db.OperationalIssues.Where(x => x.TenantId == tenantId && x.Code == UnmatchedOrderLineIssueCode && x.Status != IssueStatus.Resolved && x.DedupeKey.StartsWith(issuePrefix) && x.LastSeenAt < passStartedAt).ToListAsync(cancellationToken)
                : await db.OperationalIssues.Where(x => x.TenantId == tenantId && x.Code == UnmatchedOrderLineIssueCode && x.Status != IssueStatus.Resolved && x.DedupeKey.StartsWith(issuePrefix)).ToListAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        foreach (var issue in resolvable.Where(issue => !openKeys.Contains(issue.DedupeKey)))
        {
            issue.Status = IssueStatus.Resolved;
            issue.ResolvedAt = now;
        }
        await db.SaveChangesAsync(cancellationToken);
        return new(rows.Count, matched.Count, unmatched.Count);

        Task<List<OperationalIssue>> QueryResolvableIssues(string[] keys) => keys.Length == 0
            ? Task.FromResult(new List<OperationalIssue>())
            : db.OperationalIssues.Where(x => x.TenantId == tenantId && x.Status != IssueStatus.Resolved && keys.Contains(x.DedupeKey)).ToListAsync(cancellationToken);
    }

    internal static bool OrderLineFullyCancelled(OrderLine line) => line.OrderedQuantity > 0 && line.CancelledQuantity >= line.OrderedQuantity;

    private static string UnmatchedOrderLineIssueKey(OrderLineCodes codes) =>
        codes.DisplayCode is { } code && CatalogKeyNormalizer.Key(code, 120) is { Length: > 0 } key ? key : "-";

    internal static string UnmatchedOrderLineSummary(string platformCode, OrderLineCodes codes, string? title, IReadOnlyList<string> orderNumbers, bool ambiguous)
    {
        var numbers = orderNumbers.Where(number => !string.IsNullOrWhiteSpace(number)).Distinct(StringComparer.Ordinal).ToList();
        var orders = numbers.Count <= 5 ? string.Join(", ", numbers) : $"{string.Join(", ", numbers.Take(5))} ve {numbers.Count - 5} sipariş daha";
        var code = codes.DisplayCode is { } displayCode ? $"{displayCode} kodlu" : "kodsuz";
        var product = string.IsNullOrWhiteSpace(title) ? "" : $" ({title.Trim()})";
        var marketplace = IssueMarketplaceName(platformCode);
        return ambiguous
            ? $"Eşleşmeyen sipariş satırı: {marketplace} siparişi {orders} içindeki {code} ürün{product} paneldeki birden fazla varyanta uyuyor; hangisi olduğu belirsiz olduğu için satır stoğu etkilemiyor. Kodların bir varyanta özgü olmasını sağlayın."
            : $"Eşleşmeyen sipariş satırı: {marketplace} siparişi {orders} içindeki {code} ürün{product} paneldeki hiçbir varyanta eşlenemedi; satır stoğu etkilemiyor. Bu kodla bir varyant oluşturun ya da pazaryeri ürününü bir varyanta eşleyin.";
    }

    private static string IssueMarketplaceName(string? platformCode) => platformCode switch
    {
        "TRENDYOL" => "Trendyol",
        "HEPSIBURADA" => "Hepsiburada",
        "N11" => "N11",
        "PAZARAMA" => "Pazarama",
        "PTTAVM" => "PttAVM",
        "SHOPIFY" => "Shopify",
        null or "" => "Pazaryeri",
        _ => platformCode
    };

    // The notice of an order line stock could not fully give names the order and the code, so the owner knows which
    // count to check (live 5 Nov 2026: 29 notices that said only "1 adet stoktan düşülemedi").
    internal static string StockShortfallSummary(string? platformCode, string? orderNumber, string? code, decimal shortfall)
    {
        var order = string.IsNullOrWhiteSpace(orderNumber) ? $"{IssueMarketplaceName(platformCode)} siparişindeki" : $"{IssueMarketplaceName(platformCode)} siparişi {orderNumber.Trim()} içindeki";
        var product = string.IsNullOrWhiteSpace(code) ? "kodsuz ürünün" : $"{code.Trim()} kodlu ürünün";
        var units = shortfall.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture);
        return $"Sipariş stoğu kısmen düşürüldü: {order} {product} paneldeki fiziksel stoğu yetersizdi, {units} adet stoktan düşülemedi. Paneldeki stok sayısını kontrol edin. Rezervasyon tutulmaz; stok sıfırın altına inmedi (bilgi amaçlı).";
    }

    // Read once per connection for the processor's life (ISLEYICI-3/8, 15 Nov 2026): a connection never changes its
    // marketplace. A connection not found is not remembered (null).
    private readonly Dictionary<(Guid TenantId, Guid ConnectionId), string> connectionPlatformCodes = [];

    private async Task<string?> ConnectionPlatformCode(Guid tenantId, Guid connectionId, CancellationToken cancellationToken)
    {
        if (connectionPlatformCodes.TryGetValue((tenantId, connectionId), out var known)) return known;
        var platformCode = await db.PlatformConnections.AsNoTracking().Where(x => x.TenantId == tenantId && x.Id == connectionId).Select(x => x.PlatformCode).SingleOrDefaultAsync(cancellationToken);
        if (platformCode is not null) connectionPlatformCodes[(tenantId, connectionId)] = platformCode;
        return platformCode;
    }

    private async Task<string?> OrderNumberOf(Guid tenantId, Guid orderId, CancellationToken cancellationToken) =>
        db.Orders.Local.FirstOrDefault(x => x.TenantId == tenantId && x.Id == orderId)?.OrderNumber
        ?? await db.Orders.AsNoTracking().Where(x => x.TenantId == tenantId && x.Id == orderId).Select(x => x.OrderNumber).SingleOrDefaultAsync(cancellationToken);

    // One issue per unmatched code of a connection. A pass that finds the same lines again only keeps it alive (the
    // retention resolves issues not seen for days) at most hourly; a new order with the code, or an issue resolved
    // meanwhile, reopens it.
    // passStartedAt: the start of a rematch pass read in several batches; an issue this pass sees is marked seen in it,
    // so the pass's last batch does not resolve it.
    private async Task RecordUnmatchedOrderLineIssue(Guid tenantId, string key, string summary, CancellationToken cancellationToken, DateTimeOffset? passStartedAt = null)
    {
        var now = timeProvider.GetUtcNow();
        var issue = db.OperationalIssues.Local.FirstOrDefault(x => x.TenantId == tenantId && x.DedupeKey == key)
            ?? await db.OperationalIssues.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.DedupeKey == key, cancellationToken);
        if (issue is null)
        {
            db.OperationalIssues.Add(new OperationalIssue { Id = Guid.CreateVersion7(), TenantId = tenantId, DedupeKey = key, Code = UnmatchedOrderLineIssueCode, Summary = summary, Status = IssueStatus.Open, FirstSeenAt = now, LastSeenAt = now, OccurrenceCount = 1 });
            return;
        }
        if (issue.Status == IssueStatus.Resolved || !string.Equals(issue.Summary, summary, StringComparison.Ordinal))
        {
            issue.Status = IssueStatus.Open;
            issue.ResolvedAt = null;
            issue.Code = UnmatchedOrderLineIssueCode;
            issue.Summary = summary;
            issue.LastSeenAt = now;
            issue.OccurrenceCount++;
        }
        else if (now - issue.LastSeenAt >= TimeSpan.FromHours(1) || passStartedAt is { } started && issue.LastSeenAt < started) issue.LastSeenAt = now;
    }

    // A package line has one allocation (T2-DO1): every later event of the package updates it in place. Before, each event
    // added a row of its own (live 5 Nov 2026: 874 extra rows on 371 package lines, up to 7 for a one-unit line), so a sum
    // over the rows counted a line several times.
    private static string AllocationKey(Guid packageId, Guid orderLineId) => $"{packageId:D}:{orderLineId:D}";

    // The allocation of each package line, the latest event's where rows from before the one-row rule remain.
    internal static Dictionary<string, PackageLineAllocation> AllocationsByLine(IEnumerable<PackageLineAllocation> allocations) => allocations
        .GroupBy(allocation => AllocationKey(allocation.PackageId, allocation.OrderLineId), StringComparer.Ordinal)
        .ToDictionary(group => group.Key, group => group
            .OrderByDescending(allocation => PackageEventInstant(allocation.SourceEventId))
            .ThenByDescending(allocation => allocation.SourceEventId, StringComparer.Ordinal)
            .ThenByDescending(allocation => allocation.Id)
            .First(), StringComparer.Ordinal);

    // Package events are "{externalPackageId}:{unix ms}" (PackageIngestionSafety.EventId); anything else sorts first.
    internal static long PackageEventInstant(string? sourceEventId)
    {
        if (string.IsNullOrEmpty(sourceEventId)) return long.MinValue;
        var separator = sourceEventId.LastIndexOf(':');
        return separator >= 0 && long.TryParse(sourceEventId.AsSpan(separator + 1), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var milliseconds) ? milliseconds : long.MinValue;
    }

    // Writes an event's quantities to the package line's one allocation, adding it when the line has none. Callers
    // write the event the package's status was just set to (or one the line has no allocation for yet), so the row
    // always names the package's current event, the one the line projection reads (PackageLineProjectionPolicy).
    // Returns true when a row was added.
    private bool UpsertLineAllocation(Guid tenantId, Guid packageId, Guid orderLineId, string eventId, decimal allocated, decimal cancelled, decimal shipped, decimal delivered, decimal returned, Dictionary<string, PackageLineAllocation> allocationsByKey)
    {
        var key = AllocationKey(packageId, orderLineId);
        if (allocationsByKey.GetValueOrDefault(key) is not { } held)
        {
            var added = new PackageLineAllocation { Id = Guid.CreateVersion7(), TenantId = tenantId, PackageId = packageId, OrderLineId = orderLineId, SourceEventId = eventId, AllocatedQuantity = allocated, CancelledQuantity = cancelled, ShippedQuantity = shipped, DeliveredQuantity = delivered, ReturnedQuantity = returned };
            db.PackageLineAllocations.Add(added);
            allocationsByKey[key] = added;
            return true;
        }
        // Rows are read without tracking; the one written is attached (or its tracked twin taken) so the update is saved.
        var allocation = db.PackageLineAllocations.Local.FirstOrDefault(x => x.Id == held.Id) ?? db.PackageLineAllocations.Attach(held).Entity;
        allocationsByKey[key] = allocation;
        if (allocation.SourceEventId == eventId && allocation.AllocatedQuantity == allocated && allocation.CancelledQuantity == cancelled
            && allocation.ShippedQuantity == shipped && allocation.DeliveredQuantity == delivered && allocation.ReturnedQuantity == returned) return false;
        allocation.SourceEventId = eventId;
        allocation.AllocatedQuantity = allocated;
        allocation.CancelledQuantity = cancelled;
        allocation.ShippedQuantity = shipped;
        allocation.DeliveredQuantity = delivered;
        allocation.ReturnedQuantity = returned;
        return false;
    }

    // Adds the allocation of each line the read places in the package that has no allocation in any package of the
    // order; a line already allocated somewhere is never allocated again, and an existing allocation is not changed.
    // The quantities are the read's normalized ones (quantity integrity was checked for the whole package), shipped and
    // delivered following the package's stored status. Stock follows the lines' ordered and cancelled quantities, so
    // the allocation does not take the units from stock again; it only lets the line's shipped and delivered
    // quantities be projected.
    // The read's line quantities for a package whose status they were not written with: one leaving ManualReview for the
    // status its read names (a raw status the panel now knows; a package that turns Returned has its lines returned, or
    // the reports and the sale invoice kept them as sold), or a PTT AVM / Pazarama line that changed under a known event.
    // They are stored under the event the package's status is projected from (PackageLineProjectionPolicy: the read's own
    // event without a status time, else the package's status time), so the lines follow them.
    private void ApplyReadLineQuantities(
        Guid tenantId,
        ShipmentPackage package,
        RemotePackage remote,
        IReadOnlyList<RemotePackageAllocation> remoteAllocations,
        IReadOnlyDictionary<string, NormalizedPackageAllocation> safeAllocations,
        IReadOnlyDictionary<string, OrderLine> lines,
        Dictionary<string, PackageLineAllocation> allocationsByKey,
        string eventId)
    {
        var projectedEventId = remote.StatusTimeUnknown ? eventId : PackageIngestionSafety.EventId(package.ExternalPackageId, package.StatusOccurredAt);
        foreach (var remoteAllocation in remoteAllocations)
            if (lines.TryGetValue(remoteAllocation.ExternalLineId, out var line) && safeAllocations.TryGetValue(remoteAllocation.ExternalLineId, out var safe))
                UpsertLineAllocation(tenantId, package.Id, line.Id, projectedEventId, safe.ActiveAllocatedQuantity, safe.CancelledQuantity, safe.ShippedQuantity, safe.DeliveredQuantity, safe.ReturnedQuantity, allocationsByKey);
    }

    private int AddMissingLineAllocations(
        Guid tenantId,
        ShipmentPackage package,
        IReadOnlyList<RemotePackageAllocation> remoteAllocations,
        IReadOnlyDictionary<string, NormalizedPackageAllocation> safeAllocations,
        IReadOnlyDictionary<string, OrderLine> lines,
        Dictionary<string, PackageLineAllocation> allocationsByKey,
        string eventId)
    {
        var allocatedLineIds = allocationsByKey.Values.Select(allocation => allocation.OrderLineId).ToHashSet();
        var added = 0;
        foreach (var remoteAllocation in remoteAllocations)
        {
            if (!lines.TryGetValue(remoteAllocation.ExternalLineId, out var line)
                || !safeAllocations.TryGetValue(remoteAllocation.ExternalLineId, out var safe)
                || !allocatedLineIds.Add(line.Id)) continue;
            var shipped = package.Status is ShipmentPackageStatus.Shipped or ShipmentPackageStatus.Delivered or ShipmentPackageStatus.Undelivered ? safe.ActiveAllocatedQuantity : safe.ShippedQuantity;
            var delivered = package.Status == ShipmentPackageStatus.Delivered ? safe.ActiveAllocatedQuantity : safe.DeliveredQuantity;
            if (UpsertLineAllocation(tenantId, package.Id, line.Id, eventId, safe.ActiveAllocatedQuantity, safe.CancelledQuantity, shipped, delivered, safe.ReturnedQuantity, allocationsByKey)) added++;
        }
        return added;
    }

    private async Task ProjectOrderReservations(Guid tenantId, Guid connectionId, IEnumerable<(OrderLine Line, DateTimeOffset ModifiedAt)> sourceLines, CancellationToken cancellationToken)
    {
        var sources = sourceLines.Where(source => source.Line.VariantId is not null).GroupBy(source => source.Line.Id).Select(group => group.Last()).ToList();
        var lines = sources.Select(source => source.Line).ToList();
        if (lines.Count == 0) return;
        var inventoryPolicy = await db.ConnectionInventoryPolicies.AsNoTracking()
            .SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId, cancellationToken);
        var modifiedAtByLine = sources.ToDictionary(source => source.Line.Id, source => source.ModifiedAt);
        var variantIds = lines.Select(line => line.VariantId!.Value).Distinct().ToArray();
        var items = await db.InventoryItems
            .Where(x => x.TenantId == tenantId && x.LocationCode == "MAIN" && variantIds.Contains(x.VariantId))
            .ToListAsync(cancellationToken);
        var itemsByVariant = items.ToDictionary(x => x.VariantId);
        foreach (var item in db.InventoryItems.Local.Where(x => x.TenantId == tenantId && x.LocationCode == "MAIN" && variantIds.Contains(x.VariantId))) itemsByVariant.TryAdd(item.VariantId, item);
        var sourceIds = lines.Select(line => line.Id.ToString("D")).ToArray();
        var itemIds = itemsByVariant.Values.Select(item => item.Id).ToArray();
        var reservations = itemIds.Length == 0
            ? []
            : await db.StockReservations
                .Where(x => x.TenantId == tenantId && x.SourceType == "ORDER_LINE" && itemIds.Contains(x.InventoryItemId) && sourceIds.Contains(x.SourceId))
                .ToListAsync(cancellationToken);
        var reservationsByKey = reservations.ToDictionary(x => (x.InventoryItemId, x.SourceId), x => x);
        List<StockLedgerEntry> orderLedgerRows = itemIds.Length == 0
            ? []
            : await db.StockLedgerEntries.AsNoTracking()
                .Where(x => x.TenantId == tenantId && itemIds.Contains(x.InventoryItemId) && sourceIds.Contains(x.SourceId)
                    && (x.MovementType == "ORDER_SHIPPED"
                        || x.MovementType == "ORDER_SHIPMENT_PROGRESS"
                        || x.MovementType == "ORDER_RECEIVED"
                        || x.MovementType == "ORDER_STOCK_BASELINE"
                        || x.MovementType == OrderInventoryStockPolicy.ShortfallMovementType))
                .ToListAsync(cancellationToken);
        var persistedLedgerIds = orderLedgerRows.Select(x => x.Id).ToHashSet();
        orderLedgerRows.AddRange(db.StockLedgerEntries.Local.Where(x => x.TenantId == tenantId
            && itemIds.Contains(x.InventoryItemId) && sourceIds.Contains(x.SourceId) && persistedLedgerIds.Add(x.Id)
            && (x.MovementType == "ORDER_SHIPPED" || x.MovementType == "ORDER_SHIPMENT_PROGRESS"
                || x.MovementType == "ORDER_RECEIVED" || x.MovementType == "ORDER_STOCK_BASELINE"
                || x.MovementType == OrderInventoryStockPolicy.ShortfallMovementType)));
        await AdoptResetOrderLineStock(tenantId, connectionId, lines, itemsByVariant, orderLedgerRows, reservationsByKey, cancellationToken);
        var legacyShippedLedger = orderLedgerRows
            .Where(x => x.MovementType == "ORDER_SHIPPED")
            .GroupBy(x => x.SourceId)
            .ToDictionary(group => group.Key, group => Math.Max(0m, -group.Sum(x => x.QuantityDelta)), StringComparer.Ordinal);
        var shippedLedger = orderLedgerRows
            .Where(x => x.MovementType is "ORDER_SHIPPED" or "ORDER_SHIPMENT_PROGRESS")
            .GroupBy(x => x.SourceId)
            .ToDictionary(group => group.Key, group => Math.Max(0m, -group.Sum(x => x.QuantityDelta)), StringComparer.Ordinal);
        var receivedLedgerDeltas = orderLedgerRows
            .Where(x => x.MovementType == "ORDER_RECEIVED")
            .GroupBy(x => x.SourceId)
            .ToDictionary(group => group.Key, group => group.Sum(x => x.QuantityDelta), StringComparer.Ordinal);
        var stockBaselines = orderLedgerRows
            .Where(x => x.MovementType == "ORDER_STOCK_BASELINE")
            .GroupBy(x => x.SourceId)
            .ToDictionary(group => group.Key, group => group.Sum(x => x.QuantityDelta), StringComparer.Ordinal);
        var shortfalls = orderLedgerRows
            .Where(x => x.MovementType == OrderInventoryStockPolicy.ShortfallMovementType)
            .GroupBy(x => x.SourceId)
            .ToDictionary(group => group.Key, group => group.Sum(x => x.QuantityDelta), StringComparer.Ordinal);
        var now = timeProvider.GetUtcNow();
        var outbox = new List<StockProjectionChange>();
        string? platformCode = null;
        foreach (var line in lines)
        {
            var variantId = line.VariantId!.Value;
            if (!itemsByVariant.TryGetValue(variantId, out var item)) continue;
            var sourceId = line.Id.ToString("D");
            reservationsByKey.TryGetValue((item.Id, sourceId), out var reservation);
            var targetShipped = Math.Min(
                Math.Max(0m, line.OrderedQuantity),
                Math.Max(line.ShippedQuantity, line.DeliveredQuantity));
            var consumedShipped = shippedLedger.GetValueOrDefault(sourceId);
            var shipmentDelta = Math.Max(0m, targetShipped - consumedShipped);
            var eventId = $"{line.Id:N}:{modifiedAtByLine[line.Id].ToUnixTimeMilliseconds()}";

            // Owner decision (30 Oct 2026): the panel holds no reservations. A reservation an earlier projection left (the
            // units an order could not take from stock) is released and kept as the line's shortfall instead, without
            // touching on-hand stock, so the units are neither taken later nor held from what the marketplaces are sent.
            if (reservation is { Status: ReservationStatus.Active } held)
            {
                var heldQuantity = held.Quantity;
                held.Status = ReservationStatus.Released; held.ReleasedAt = now; held.ExpiresAt = null; held.Version++;
                var releaseEventId = $"{eventId}:reservation-to-shortfall:{held.Id:N}";
                outbox.Add(StockMovementWriter.ReleaseReserved(item, heldQuantity, releaseEventId));
                StockMovementWriter.Record(db, tenantId, item.Id, heldQuantity, OrderLineSource("ORDER_RESERVATION_RELEASED", sourceId, releaseEventId, $"order-reservation:{releaseEventId}", line.OrderId, modifiedAtByLine[line.Id], now));
                RecordShortfall(tenantId, item.Id, sourceId, line.OrderId, heldQuantity, releaseEventId, modifiedAtByLine[line.Id], now);
                shortfalls[sourceId] = shortfalls.GetValueOrDefault(sourceId) + heldQuantity;
            }

            if (!stockBaselines.TryGetValue(sourceId, out var baselineQuantity))
            {
                // A line read for the first time takes its units from stock whatever its status, shipped and delivered
                // included (owner decision, 30 Oct 2026). Only units a shipment of the time before order-time deduction
                // already took (ORDER_SHIPPED rows) are its opening baseline; a line of a reset or a late match brings its
                // own baseline (AdoptResetOrderLineStock, AdoptLateMatchedLineStock).
                baselineQuantity = OrderInventoryStockPolicy.InitialBaselineQuantity(legacyShippedLedger.GetValueOrDefault(sourceId));
                stockBaselines[sourceId] = baselineQuantity;
                StockMovementWriter.Record(db, tenantId, item.Id, baselineQuantity, BaselineSource(item.Id, sourceId, line.OrderId, modifiedAtByLine[line.Id], now));
            }

            var targetCommitted = Math.Max(
                OrderInventoryStockPolicy.TargetCommittedQuantity(line.OrderedQuantity, line.CancelledQuantity, targetShipped),
                consumedShipped);
            var receivedLedgerDelta = receivedLedgerDeltas.GetValueOrDefault(sourceId);
            var shortfall = shortfalls.GetValueOrDefault(sourceId);
            // Physical stock leaves OnHand as soon as the order is ingested; never below zero unless the connection allows
            // negative stock. What stock could not give is the line's shortfall: reported, not reserved, never taken later.
            // A cancellation gives back what the line took (its shortfall first, which took nothing). Shipment progress is
            // tracked separately so a later fulfillment update cannot deduct the same unit for a second time.
            var movement = OrderInventoryStockPolicy.Plan(baselineQuantity, shortfall, receivedLedgerDelta, targetCommitted, item.OnHand, inventoryPolicy?.NegativeStockAllowed == true);
            var committedBeforeMovement = OrderInventoryStockPolicy.Committed(baselineQuantity, shortfall, receivedLedgerDelta);
            if (movement.OnHandDelta != 0)
            {
                var committedAfterStockDelta = Math.Max(0m, committedBeforeMovement - movement.OnHandDelta);
                var stockEventId = $"{eventId}:committed:{committedBeforeMovement.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)}:{committedAfterStockDelta.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)}";
                outbox.Add(StockMovementWriter.Move(db, item, movement.OnHandDelta, StockMovementRounding.OnHand,
                    OrderLineSource("ORDER_RECEIVED", sourceId, stockEventId, $"order-stock-received:{item.Id:N}:{stockEventId}", line.OrderId, modifiedAtByLine[line.Id], now)));
                receivedLedgerDeltas[sourceId] = receivedLedgerDelta + movement.OnHandDelta;
            }
            if (movement.ShortfallDelta != 0)
            {
                var shortfallAfter = shortfall + movement.ShortfallDelta;
                var shortfallEventId = $"{eventId}:shortfall:{shortfall.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)}:{shortfallAfter.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)}";
                RecordShortfall(tenantId, item.Id, sourceId, line.OrderId, movement.ShortfallDelta, shortfallEventId, modifiedAtByLine[line.Id], now);
                shortfalls[sourceId] = shortfallAfter;
                if (movement.ShortfallDelta > 0)
                    await RecordIssue(
                        tenantId,
                        StockShortfallNotices.DedupeKey(line.Id),
                        StockShortfallNotices.IssueCode,
                        StockShortfallSummary(platformCode ??= await ConnectionPlatformCode(tenantId, connectionId, cancellationToken), await OrderNumberOf(tenantId, line.OrderId, cancellationToken), line.Sku ?? line.Barcode, shortfallAfter),
                        cancellationToken);
                // The units that could not be taken are no longer ordered (a cancellation), so the notice closes; a stock count
                // set by hand closes it too (StockShortfallNotices).
                else if (shortfallAfter <= 0) await ResolveIssue(tenantId, StockShortfallNotices.DedupeKey(line.Id), cancellationToken);
            }

            if (shipmentDelta > 0)
            {
                var shipmentEventId = $"{line.Id:N}:shipment-progress:{targetShipped.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)}";
                StockMovementWriter.Record(db, tenantId, item.Id, -shipmentDelta, OrderLineSource("ORDER_SHIPMENT_PROGRESS", sourceId, shipmentEventId, $"order-shipment-progress:{item.Id:N}:{shipmentEventId}", line.OrderId, modifiedAtByLine[line.Id], now));
                shippedLedger[sourceId] = consumedShipped + shipmentDelta;
            }
        }
        // Every connection selling the variant gets the new stock, not only the one that sold it.
        await StockProjectionOutbox.EnqueueAsync(db, tenantId, outbox, now, cancellationToken);
    }

    // The units of an order line that stock could not give (positive) or that are no longer owed (negative). A marker, not
    // a stock movement: on-hand does not change with it, and the panel's ledger view leaves it out.
    private void RecordShortfall(Guid tenantId, Guid itemId, string sourceId, Guid orderId, decimal quantity, string eventId, DateTimeOffset occurredAt, DateTimeOffset now) =>
        StockMovementWriter.Record(db, tenantId, itemId, quantity, OrderLineSource(OrderInventoryStockPolicy.ShortfallMovementType, sourceId, eventId, $"order-stock-shortfall:{itemId:N}:{eventId}", orderId, occurredAt, now));

    // An order line's stock ledger row (SIPARIS-STOK-8: written through StockMovementWriter).
    private static StockLedgerSource OrderLineSource(string movementType, string sourceId, string sourceEventId, string idempotencyKey, Guid orderId, DateTimeOffset occurredAt, DateTimeOffset now) =>
        new(movementType, "ORDER_LINE", sourceId, sourceEventId, idempotencyKey, occurredAt, now, $"order:{orderId:N}");

    // A catalog read's stock row (an opening number or a marketplace-authoritative change): the event id is the key.
    private static StockLedgerSource CatalogStockSource(string movementType, Guid connectionId, string eventId, DateTimeOffset occurredAt, DateTimeOffset now) =>
        new(movementType, MarketplaceStockImportPolicy.SourceType, connectionId.ToString("D"), eventId, eventId, occurredAt, now, $"catalog-import:{connectionId:N}");

    private static StockLedgerSource BaselineSource(Guid itemId, string sourceId, Guid orderId, DateTimeOffset occurredAt, DateTimeOffset now) =>
        OrderLineSource("ORDER_STOCK_BASELINE", sourceId, $"baseline:{sourceId}", $"order-stock-baseline:{itemId:N}:{sourceId}", orderId, occurredAt, now);

    // A connection's order reset leaves the stock movements and reservation of a deleted line under a carry key
    // (OrderInventoryStockPolicy.ResetCarryKey). The line a later sync imports under the same marketplace order and
    // line ids takes them over before its first projection, so the units the order already took from stock are not
    // taken a second time and its reservation is not held twice. Only rows of the line's own inventory item move.
    private async Task AdoptResetOrderLineStock(
        Guid tenantId,
        Guid connectionId,
        List<OrderLine> lines,
        Dictionary<Guid, InventoryItem> itemsByVariant,
        List<StockLedgerEntry> orderLedgerRows,
        Dictionary<(Guid InventoryItemId, string SourceId), StockReservation> reservationsByKey,
        CancellationToken cancellationToken)
    {
        var projectedSourceIds = orderLedgerRows.Select(x => x.SourceId).Concat(reservationsByKey.Keys.Select(key => key.SourceId)).ToHashSet(StringComparer.Ordinal);
        var unprojected = lines.Where(line => itemsByVariant.ContainsKey(line.VariantId!.Value) && !projectedSourceIds.Contains(line.Id.ToString("D"))).ToList();
        if (unprojected.Count == 0) return;
        var orderIds = unprojected.Select(line => line.OrderId).Distinct().ToArray();
        var externalOrderIds = db.Orders.Local.Where(x => x.TenantId == tenantId && orderIds.Contains(x.Id)).ToDictionary(x => x.Id, x => x.ExternalOrderId);
        var unloaded = orderIds.Where(id => !externalOrderIds.ContainsKey(id)).ToArray();
        if (unloaded.Length > 0)
        {
            var rows = await db.Orders.AsNoTracking().Where(x => x.TenantId == tenantId && unloaded.Contains(x.Id)).Select(x => new { x.Id, x.ExternalOrderId }).ToListAsync(cancellationToken);
            foreach (var row in rows) externalOrderIds[row.Id] = row.ExternalOrderId;
        }
        var carryKeys = unprojected
            .Where(line => externalOrderIds.ContainsKey(line.OrderId))
            .ToDictionary(line => line.Id, line => OrderInventoryStockPolicy.ResetCarryKey(connectionId, externalOrderIds[line.OrderId], line.ExternalLineId));
        if (carryKeys.Count == 0) return;
        var keys = carryKeys.Values.Distinct(StringComparer.Ordinal).ToArray();
        var itemIds = unprojected.Select(line => itemsByVariant[line.VariantId!.Value].Id).Distinct().ToArray();
        var carriedLedger = await db.StockLedgerEntries
            .Where(x => x.TenantId == tenantId && x.SourceType == "ORDER_LINE" && itemIds.Contains(x.InventoryItemId) && keys.Contains(x.SourceId))
            .ToListAsync(cancellationToken);
        var carriedReservations = await db.StockReservations
            .Where(x => x.TenantId == tenantId && x.SourceType == "ORDER_LINE" && itemIds.Contains(x.InventoryItemId) && keys.Contains(x.SourceId))
            .ToListAsync(cancellationToken);
        if (carriedLedger.Count == 0 && carriedReservations.Count == 0) return;
        foreach (var line in unprojected)
        {
            if (!carryKeys.TryGetValue(line.Id, out var key)) continue;
            var itemId = itemsByVariant[line.VariantId!.Value].Id;
            var sourceId = line.Id.ToString("D");
            foreach (var row in carriedLedger.Where(x => x.InventoryItemId == itemId && x.SourceId == key))
            {
                row.SourceId = sourceId;
                orderLedgerRows.Add(row);
            }
            foreach (var reservation in carriedReservations.Where(x => x.InventoryItemId == itemId && x.SourceId == key))
            {
                reservation.SourceId = sourceId;
                reservation.Version++;
                reservationsByKey[(itemId, sourceId)] = reservation;
            }
        }
    }

    private async Task<JobExecutionResult> DispatchStockProjection(Guid tenantId, Guid connectionId, string payloadJson, string correlationId, Guid? jobId, CancellationToken cancellationToken)
    {
        Guid variantId; string trigger;
        try
        {
            using var payload = JsonDocument.Parse(payloadJson);
            variantId = payload.RootElement.GetProperty("variantId").GetGuid();
            // The change that queued the dispatch names why the send goes (the send log's trigger).
            trigger = PriceStockSendTriggers.FromSourceEvent(payload.RootElement.TryGetProperty("sourceEventId", out var source) && source.ValueKind == JsonValueKind.String ? source.GetString() : null);
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException or FormatException) { return JobExecutionResult.Blocked("STOCK_PROJECTION_PAYLOAD_INVALID", "Stok projection işi geçersiz payload içeriyor."); }
        var (blocked, decision) = await StockProjectionGateAsync(tenantId, connectionId, cancellationToken);
        if (blocked is not null) return blocked;
        if (decision == StockProjectionDispatchDecision.Skip) return JobExecutionResult.SuccessWithNote("AUTOMATIC_WRITES_OFF", "Otomatik fiyat-stok yazması kapalı; pazaryerine gönderilmedi.");
        if (!await db.ChannelOffers.AsNoTracking().AnyAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.VariantId == variantId && x.Status == "ACTIVE", cancellationToken)) return JobExecutionResult.Success();
        if (decision == StockProjectionDispatchDecision.Dispatch && await OrderFreshnessWaitAsync(tenantId, connectionId, jobId, cancellationToken) is { } wait) return wait;
        return await DispatchStockProjectionVariant(tenantId, connectionId, variantId, decision, correlationId, trigger, cancellationToken);
    }

    // Owner decision C: an automatic send waits (bounded) for an order read of the connection that started after the
    // change was queued, so an order placed before the change is taken off the panel before the absolute number goes out.
    // The job's own creation time is the change's time: the outbox queues it in the same save as the change.
    private async Task<JobExecutionResult?> OrderFreshnessWaitAsync(Guid tenantId, Guid connectionId, Guid? jobId, CancellationToken cancellationToken)
    {
        if (jobId is not { } id) return null;
        var changedAt = await db.IntegrationJobs.AsNoTracking().Where(x => x.Id == id).Select(x => (DateTimeOffset?)x.CreatedAt).SingleOrDefaultAsync(cancellationToken);
        var readsOrders = await db.ConnectionSyncPolicies.AsNoTracking().AnyAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ResourceType == "ORDERS" && x.Enabled, cancellationToken);
        var cursor = await db.SyncCursors.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ResourceType == OrderFreshnessGatePolicy.OrderReadCursor)
            .Select(x => new { x.LastAttemptAt, x.LastSuccessAt })
            .SingleOrDefaultAsync(cancellationToken);
        var maxWait = TimeSpan.FromSeconds(Math.Clamp(configuration.GetValue("MarketplaceSync:StockPush:OrderFreshnessMaxWaitSeconds", (int)OrderFreshnessGatePolicy.DefaultMaxWait.TotalSeconds), 0, 3_600));
        var now = timeProvider.GetUtcNow();
        var decision = OrderFreshnessGatePolicy.Decide(readsOrders && cursor is not null, changedAt, cursor?.LastAttemptAt, cursor?.LastSuccessAt, now, maxWait);
        // Webhook first: healthy order notifications cover the change once they have had time to arrive and every one
        // received has been ingested, without waiting for the (stretched) scheduled read.
        if (decision == OrderFreshnessDecision.Wait && OrderFreshnessGatePolicy.NotificationGraceElapsed(changedAt, now)
            && await ScheduledJobProducer.WebhookHealthyAsync(db, tenantId, connectionId, now, cancellationToken))
        {
            var notificationsPending = await db.IntegrationJobs.AsNoTracking().AnyAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId
                && (x.JobType == MarketplaceJobTypes.WebhookIngest || x.JobType == MarketplaceJobTypes.ShopifyWebhookIngest || x.JobType == MarketplaceJobTypes.HepsiburadaWebhookIngest)
                && (x.Status == JobStatus.Pending || x.Status == JobStatus.Leased || x.Status == JobStatus.RetryScheduled), cancellationToken);
            decision = OrderFreshnessGatePolicy.Decide(readsOrders && cursor is not null, changedAt, cursor?.LastAttemptAt, cursor?.LastSuccessAt, now, maxWait, webhookHealthy: true, notificationsPending);
        }
        return decision == OrderFreshnessDecision.Wait
            ? JobExecutionResult.Deferred(OrderFreshnessGatePolicy.WaitCode, OrderFreshnessGatePolicy.WaitMessage, OrderFreshnessGatePolicy.RecheckDelay)
            : null;
    }

    // Whether an automatic stock write may go out on the connection at all; read once per reconcile pass
    // instead of once per candidate. A write policy without a row is off (owner decision A).
    private async Task<(JobExecutionResult? Blocked, StockProjectionDispatchDecision Decision)> StockProjectionGateAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken)
    {
        var connection = await db.PlatformConnections.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == connectionId, cancellationToken);
        if (connection is null) return (JobExecutionResult.Blocked("CONNECTION_NOT_FOUND", "Bağlantı bulunamadı."), StockProjectionDispatchDecision.Skip);
        // Stock projection is an automatic write path. Stage manual writes remain
        // available, but an automatic projection must never enqueue a remote write
        // while the explicit external-write switch is off.
        if (!WritesEnabled(connection.SettingsJson)) return (null, StockProjectionDispatchDecision.Skip);
        return (null, StockProjectionOutboxPolicy.Decide(
            await StrictWritePolicyEnabledAsync(tenantId, connectionId, MarketplaceExternalWritePolicies.Stock, cancellationToken),
            await StrictWritePolicyEnabledAsync(tenantId, connectionId, MarketplaceExternalWritePolicies.Price, cancellationToken)));
    }

    private async Task<JobExecutionResult> DispatchStockProjectionVariant(Guid tenantId, Guid connectionId, Guid variantId, StockProjectionDispatchDecision decision, string correlationId, string trigger, CancellationToken cancellationToken)
    {
        if (decision == StockProjectionDispatchDecision.HoldForPricePolicy)
        {
            // The request would carry the price too; sending it with the price policy off could
            // overwrite a price the seller manages on the marketplace. The stock waits, visibly.
            await RecordIssue(tenantId, $"stock-projection-price-policy:{connectionId:N}", "STOCK_WRITE_HELD_PRICE_POLICY_DISABLED", "Stok değişikliği pazaryerine gönderilmedi: fiyat-stok isteği fiyatı da taşıdığı için fiyat dış yazma akışı da açık olmalı. Fiyat akışı açıldığında stok kontrol taramasıyla gönderilir.", cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            return JobExecutionResult.Success();
        }
        var composer = new PriceInventoryComposer(db);
        var build = await composer.BuildAsync(tenantId, connectionId, cancellationToken, variantId, automatic: true);
        if (!build.Succeeded)
        {
            var code = build.Error!.Code;
            if (code == "INVENTORY_REQUIRED")
            {
                await RecordIssue(tenantId, $"price-inventory-inventory-required:{connectionId:N}", code, build.Error.Message, cancellationToken);
                await db.SaveChangesAsync(cancellationToken);
            }
            // An offer the user has to fix stays visible as an issue instead of a blocked job (G: no silent freeze).
            if (PriceInventoryOfferIssues.IsOfferProblem(code))
            {
                foreach (var invalid in composer.InvalidOffers)
                    await RecordIssue(tenantId, PriceInventoryOfferIssues.Key(connectionId, invalid.VariantId), invalid.Code, invalid.Message, cancellationToken);
                if (composer.InvalidOffers.Count == 0) await RecordIssue(tenantId, PriceInventoryOfferIssues.Key(connectionId, variantId), code, build.Error.Message, cancellationToken);
                await db.SaveChangesAsync(cancellationToken);
                return JobExecutionResult.Success();
            }
            if (!StockProjectionOutboxPolicy.IsSettledBuildFailure(code)) return JobExecutionResult.Blocked(code, build.Error.Message);
            // Already sent or not live on this connection: nothing to send now.
            return JobExecutionResult.Success();
        }
        return await QueueAutomaticPushAsync(tenantId, connectionId, build.Value!, variantId, correlationId, trigger, cancellationToken);
    }

    // One automatic PRICE_INVENTORY_SYNC job for a built draft (one variant's change or a reconcile batch). Offers the
    // build left out are reported; the offers it carries are fine again, so their offer issues close.
    private async Task<JobExecutionResult> QueueAutomaticPushAsync(Guid tenantId, Guid connectionId, PriceInventoryDraft draft, Guid? variantId, string correlationId, string trigger, CancellationToken cancellationToken)
    {
        foreach (var invalid in draft.InvalidOffers)
            await RecordIssue(tenantId, PriceInventoryOfferIssues.Key(connectionId, invalid.VariantId), invalid.Code, invalid.Message, cancellationToken);
        await ResolveOfferIssuesAsync(tenantId, connectionId, draft.Lines.Select(line => line.VariantId).Distinct().ToArray(), cancellationToken);
        var dedup = PriceInventoryOutboxPolicy.DedupKey(connectionId, draft.Lines);
        var now = timeProvider.GetUtcNow();
        // A failed job of the same values gives up its key in the same save as the new job (Q-1/K-1).
        var existing = await db.IntegrationJobs.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.JobType == MarketplaceJobTypes.PriceInventorySync && x.JobDedupKey == dedup, cancellationToken);
        if (existing is not null)
        {
            if (!PriceInventoryOutboxPolicy.AutomaticSendMayRetire(existing.Status, existing.LastErrorCode, existing.CompletedAt, now))
            {
                await db.SaveChangesAsync(cancellationToken);
                return JobExecutionResult.Success();
            }
            existing.JobDedupKey = PriceInventoryOutboxPolicy.RetiredDedupKey(dedup, existing.Id); existing.Version++;
        }
        var id = Guid.CreateVersion7();
        var jobPayload = JsonSerializer.Serialize(new PriceInventoryJobPayload(id, connectionId, "SUBMIT", draft.PayloadHash, draft.PayloadJson, draft.Lines, null, null, variantId, Automatic: true, Trigger: trigger));
        var writeDelay = await ExternalWriteDelaySecondsAsync(tenantId, connectionId, MarketplaceExternalWritePolicies.Stock, cancellationToken);
        Jobs.JobEnqueuer.Add(db, new IntegrationJobRequest(tenantId, connectionId, MarketplaceJobTypes.PriceInventorySync, jobPayload, dedup, correlationId, now.AddSeconds(writeDelay)) { Id = id, Priority = JobPriorityPolicy.For(MarketplaceJobTypes.PriceInventorySync, userInitiated: true), MaxAttempts = 10, CreatedAt = now });
        await db.SaveChangesAsync(cancellationToken);
        return JobExecutionResult.Success();
    }

    private async Task ResolveOfferIssuesAsync(Guid tenantId, Guid connectionId, IReadOnlyCollection<Guid> variantIds, CancellationToken cancellationToken)
    {
        if (variantIds.Count == 0) return;
        var keys = variantIds.Select(variantId => PriceInventoryOfferIssues.Key(connectionId, variantId)).ToArray();
        var now = timeProvider.GetUtcNow();
        foreach (var issue in await db.OperationalIssues.Where(x => x.TenantId == tenantId && keys.Contains(x.DedupeKey) && x.Status != IssueStatus.Resolved).ToListAsync(cancellationToken))
        {
            issue.Status = IssueStatus.Resolved;
            issue.ResolvedAt = now;
        }
    }

    private bool WritesEnabled(string settingsJson)
    {
        if (!configuration.GetValue<bool>("FeatureFlags:ExternalWrites")) return false;
        try
        {
            using var document = JsonDocument.Parse(settingsJson);
            return document.RootElement.TryGetProperty("ExternalWritesEnabled", out var enabled) && enabled.ValueKind == JsonValueKind.True;
        }
        catch (JsonException) { return false; }
    }

    private async Task<bool> ExternalWriteMasterAllowedAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken)
    {
        var connection = await db.PlatformConnections.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == connectionId, cancellationToken);
        return connection is not null && (string.Equals(connection.Environment, "STAGE", StringComparison.OrdinalIgnoreCase) || WritesEnabled(connection.SettingsJson));
    }

    // The PTT AVM MANUAL_WRITE switch, checked after the master switch (the callers check that first). A missing row
    // refuses the write: it is not read as permission.
    private async Task<bool> PttAvmManualWritesAllowedAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken)
    {
        var enabled = await db.ConnectionSyncPolicies.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ResourceType == MarketplaceExternalWritePolicies.Manual)
            .Select(x => (bool?)x.Enabled)
            .SingleOrDefaultAsync(cancellationToken);
        return PttAvmManualWritePolicy.Decide(masterWritesEnabled: true, enabled) == PttAvmManualWriteDecision.Allowed;
    }

    private async Task<bool> HasHepsiburadaPublicationEvidenceAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken)
    {
        // A STAGE (SIT) connection does not require PRODUCT_WRITE: the SIT product import is what produces
        // that evidence. PRICE_WRITE/INVENTORY_WRITE stay required; PRODUCTION requires all three.
        var connection = await db.PlatformConnections.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == connectionId && x.PlatformCode == "HEPSIBURADA", cancellationToken);
        if (connection is null) return false;
        return await HasHepsiburadaWriteEvidenceAsync(tenantId, connectionId, cancellationToken, HepsiburadaStageCertificationPolicy.PublicationEvidenceCapabilities(connection));
    }

    private async Task<bool> HasHepsiburadaWriteEvidenceAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken, params string[] required)
    {
        var connection = await db.PlatformConnections.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == connectionId && x.PlatformCode == "HEPSIBURADA", cancellationToken);
        if (connection is null || required.Length == 0) return false;
        var capabilities = await db.PlatformCapabilities.AsNoTracking().Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && required.Contains(x.Code)).ToListAsync(cancellationToken);
        return required.All(code => CapabilityEvidencePolicy.IsVerifiedWriteCapability(capabilities.SingleOrDefault(x => x.Code == code), connection, code));
    }

    private async Task<bool> ExternalWriteAllowedAsync(Guid tenantId, Guid connectionId, string resourceType, CancellationToken cancellationToken)
    {
        var connection = await db.PlatformConnections.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == connectionId, cancellationToken);
        if (connection is null) return false;
        if (!string.Equals(connection.Environment, "STAGE", StringComparison.OrdinalIgnoreCase) && !WritesEnabled(connection.SettingsJson)) return false;
        return await ExternalWritePolicyEnabledAsync(tenantId, connectionId, resourceType, cancellationToken);
    }

    private async Task<bool> ExternalWritePolicyEnabledAsync(Guid tenantId, Guid connectionId, string resourceType, CancellationToken cancellationToken)
    {
        var policy = await db.ConnectionSyncPolicies.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ResourceType == resourceType, cancellationToken);
        return policy is null || policy.Enabled;
    }

    private async Task<bool> StrictWritePolicyEnabledAsync(Guid tenantId, Guid connectionId, string resourceType, CancellationToken cancellationToken) =>
        await db.ConnectionSyncPolicies.AsNoTracking().AnyAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ResourceType == resourceType && x.Enabled, cancellationToken);

    private async Task<int> ExternalWriteDelaySecondsAsync(Guid tenantId, Guid connectionId, string resourceType, CancellationToken cancellationToken)
    {
        var policy = await db.ConnectionSyncPolicies.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ResourceType == resourceType, cancellationToken);
        return policy is null ? 0 : Math.Clamp(policy.IntervalSeconds, 0, 86_400);
    }

    // Trendyol's claims feed is date-bounded for the initial scan. Invalidating
    // the previous all-time cursor makes the next read start at three months.
    private const string ShopifyReturnSyncStateVersion = "shopify-returns-v1";
    // Pages counts the pages read with the current query (Cursor), for the restart before Shopify's 25,000-result cap.
    internal sealed record ShopifyReturnSyncState(string Version, string? Cursor, DateTimeOffset? ModifiedAfter, DateTimeOffset Anchor, int Pages = 0);
    internal const int ShopifyReturnInitialLookbackDays = 180;
    internal static readonly TimeSpan ShopifyReturnOverlap = TimeSpan.FromMinutes(15);

    // Where a new Shopify return read starts: 180 days back on the first read or when asked for a full read, otherwise
    // 15 minutes before the last completed read began. A failed run never widens the read: the watermark moves only when
    // a whole run completes, so the next run still covers everything the failed one missed (the failure counters on the
    // RETURNS cursor are the job's telemetry, and one throttled page used to turn every next run into a 180-day read).
    internal static DateTimeOffset ShopifyReturnReadStart(bool forceFull, DateTimeOffset? lastSuccessAt, DateTimeOffset? lastModifiedWatermark, DateTimeOffset now) =>
        forceFull || lastSuccessAt is null || (lastModifiedWatermark ?? lastSuccessAt) is not { } watermark
            ? now.AddDays(-ShopifyReturnInitialLookbackDays)
            : watermark - ShopifyReturnOverlap;

    // The orders are sorted by updated_at, oldest first, and one query pages through at most 25,000 of them; near that cap
    // (200 pages, as for orders) the read starts a new query from the latest order change it has seen. Orders read twice
    // are harmless (claims are upserted). Without a later change time the query keeps paging.
    internal static ShopifyReturnSyncState NextShopifyReturnPage(ShopifyReturnSyncState state, string nextCursor, DateTimeOffset? lastModifiedSeen) =>
        state.Pages + 1 >= ShopifyOrderHistoryPolicy.PagesBeforeRestart && lastModifiedSeen is { } seen && (state.ModifiedAfter is not { } from || seen > from)
            ? state with { ModifiedAfter = seen, Cursor = null, Pages = 0 }
            : state with { Cursor = nextCursor, Pages = state.Pages + 1 };
    internal const string ReturnSyncStateVersion = "returns-v10";
    // PageCursor is the adapter's NextCursor kept as it came (Trendyol: "s:Created=2,Accepted=5", one next page per
    // claim status bucket that still has claims). Page counts the pages read and is the request cursor only when
    // PageCursor is empty (first page, or a state written before PageCursor existed), so old states keep working.
    // PermanentFailures counts the permanent refusals (4xx) of the saved page in a row.
    internal sealed record ReturnSyncState(string Version, int StoreFrontIndex, int Page, bool Full = true, DateTimeOffset? StartAt = null, DateTimeOffset? EndAt = null, string? PageCursor = null, int PermanentFailures = 0);
    internal const int MaximumReturnPageCursorLength = 1_000;
    internal const int ReturnPagePermanentFailureLimit = 3;

    // A permanent refusal of a saved page (400/404/422, or a validation/not-found error) is counted; a quota,
    // sign-in or server error is not. The read restarts from its baseline at the limit.
    internal static (int Failures, bool Restart) ReturnPageRefusal(ReturnSyncState state, AdapterError? error)
    {
        var permanent = error is not null
            && (error.Class is AdapterErrorClass.Validation or AdapterErrorClass.NotFound || error.HttpStatus is 400 or 404 or 422)
            && error.Class is not (AdapterErrorClass.RateLimit or AdapterErrorClass.Authentication or AdapterErrorClass.Remote5xx or AdapterErrorClass.TransientNetwork);
        if (!permanent) return (state.PermanentFailures, false);
        var failures = state.PermanentFailures + 1;
        return (failures, failures >= ReturnPagePermanentFailureLimit);
    }

    internal static string ReturnPageCursor(ReturnSyncState state) =>
        string.IsNullOrWhiteSpace(state.PageCursor) ? state.Page.ToString(System.Globalization.CultureInfo.InvariantCulture) : state.PageCursor;

    // A numeric NextCursor is a plain page number; any other non-empty value is opaque and is sent back unchanged.
    // Without a usable NextCursor the next page number is the only way on.
    internal static ReturnSyncState NextReturnPage(ReturnSyncState state, string? nextCursor)
    {
        if (int.TryParse(nextCursor, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var page))
            return state with { Page = page, PageCursor = null };
        if (!string.IsNullOrWhiteSpace(nextCursor) && nextCursor.Length <= MaximumReturnPageCursorLength)
            return state with { Page = state.Page + 1, PageCursor = nextCursor };
        return state with { Page = state.Page + 1, PageCursor = null };
    }
    private const string HepsiburadaReturnSyncStateVersion = "hepsiburada-returns-v2";
    private static readonly string[] HepsiburadaClaimStatuses = ["NewRequest", "AwaitingAction", "InDispute", "Accepted", "Rejected", "Refunded", "Cancelled", "AwaitingPreApproval"];
    private sealed record HepsiburadaReturnSyncState(string Version, int StatusIndex, int Offset, bool Full, DateTimeOffset? StatusChangedAfter, DateTimeOffset AnchorEnd);

    private async Task<bool> SyncReturns(Guid tenantId, Guid connectionId, string payloadJson, string correlationId, CancellationToken cancellationToken)
    {
        var platformCode = await db.PlatformConnections.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.Id == connectionId)
            .Select(x => x.PlatformCode)
            .SingleOrDefaultAsync(cancellationToken);
        if (string.Equals(platformCode, "SHOPIFY", StringComparison.OrdinalIgnoreCase))
            return await SyncShopifyReturns(tenantId, connectionId, payloadJson, correlationId, cancellationToken);
        if (string.Equals(platformCode, "HEPSIBURADA", StringComparison.OrdinalIgnoreCase))
            return await SyncHepsiburadaReturns(tenantId, connectionId, payloadJson, correlationId, cancellationToken);
        if (string.Equals(platformCode, "N11", StringComparison.OrdinalIgnoreCase))
            return await SyncN11Returns(tenantId, connectionId, payloadJson, correlationId, cancellationToken);
        if (string.Equals(platformCode, "PAZARAMA", StringComparison.OrdinalIgnoreCase))
            return await SyncPazaramaReturns(tenantId, connectionId, payloadJson, correlationId, cancellationToken);
        if (string.Equals(platformCode, "PTTAVM", StringComparison.OrdinalIgnoreCase))
            return await SyncPttAvmReturns(tenantId, connectionId, payloadJson, correlationId, cancellationToken);

        var cursor = await Cursor(tenantId, connectionId, "RETURNS", cancellationToken);
        var configuredOverlapSeconds = await db.ConnectionSyncPolicies.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ResourceType == "RETURNS")
            .Select(x => (int?)x.OverlapSeconds)
            .SingleOrDefaultAsync(cancellationToken) ?? 900;
        var overlap = TimeSpan.FromSeconds(Math.Clamp(configuredOverlapSeconds, 60, 86_400));
        // A failed run resumes its saved state: the watermark moves only when a whole run completes, and the
        // saved window and page say where the failed run stopped, so nothing is skipped and nothing is read
        // twice. (The run used to restart from the three-month baseline after any failure; the failure count
        // it looked at is the telemetry of the same "RETURNS" row, so one quota error meant a full re-read.)
        var state = ReadReturnSyncState(cursor, timeProvider.GetUtcNow(), overlap, ReadBoolean(payloadJson, "forceFull"));
        var pagesThisSlice = 0;
        do
        {
            var storefront = TrendyolReadStorefronts.ReturnCodes[state.StoreFrontIndex];
            // getClaims startDate/endDate are claim-creation filters, while the
            // local cursor watermark is lastModifiedDate. Reusing that watermark
            // as startDate can silently skip claims, so each read starts from page 0
            // and remains idempotent at the local claim key.
            var window = state.Full || state.StartAt is null || state.EndAt is null
                ? new ReturnPollWindow(null, null, storefront)
                : new ReturnPollWindow(state.StartAt, state.EndAt, storefront);
            TrackRequest();
            var result = await returns.PollAsync(Context(tenantId, connectionId, correlationId, $"return-sync:{storefront}:{state.Page}"), window, new(ReturnPageCursor(state), 200), cancellationToken);
            if (!result.IsSuccess && state.StoreFrontIndex > 0 && state.Page == 0 && state.PageCursor is null && result.Error?.HttpStatus is 400 or 404)
            {
                if (state.StoreFrontIndex + 1 < TrendyolReadStorefronts.ReturnCodes.Length)
                {
                    state = state with { StoreFrontIndex = state.StoreFrontIndex + 1, Page = 0, PageCursor = null };
                    cursor.OpaqueCursor = SerializeReturnSyncState(state);
                    cursor.Version++;
                    await db.SaveChangesAsync(cancellationToken);
                    continue;
                }
                cursor.OpaqueCursor = null;
                cursor.LastModifiedWatermark = timeProvider.GetUtcNow();
                cursor.Version++;
                await db.SaveChangesAsync(cancellationToken);
                break;
            }
            if (!result.IsSuccess)
            {
                TrackResultFailure(result.Error);
                // A saved page the marketplace keeps refusing for good (its cursor no longer valid) would hold the
                // read there forever: after a few such refusals in a row the read starts again from its baseline.
                if (state.Page > 0 || state.PageCursor is not null)
                {
                    var (failures, restart) = ReturnPageRefusal(state, result.Error);
                    if (failures != state.PermanentFailures)
                    {
                        cursor.OpaqueCursor = restart ? null : SerializeReturnSyncState(state with { PermanentFailures = failures });
                        cursor.Version++;
                        await db.SaveChangesAsync(cancellationToken);
                    }
                }
                throw JobProcessingException.FromAdapter(result.Error!);
            }
            foreach (var _ in result.Value!.Items) TrackReceived();
            foreach (var claim in result.Value!.Items) await UpsertReturn(tenantId, connectionId, claim, cancellationToken);
            pagesThisSlice++;
            state = state with { PermanentFailures = 0 };
            var nextStorefront = false;
            if (result.Value.HasMore)
            {
                state = NextReturnPage(state, result.Value.NextCursor);
            }
            else if (state.StoreFrontIndex + 1 < TrendyolReadStorefronts.ReturnCodes.Length)
            {
                state = state with { StoreFrontIndex = state.StoreFrontIndex + 1, Page = 0, PageCursor = null };
                nextStorefront = true;
            }
            else
            {
                cursor.OpaqueCursor = null;
                // The window's end (when the run started) is the next run's start, so claims changed while
                // this run read are not skipped.
                cursor.LastModifiedWatermark = state.EndAt ?? timeProvider.GetUtcNow();
                cursor.Version++;
                await db.SaveChangesAsync(cancellationToken);
                break;
            }
            cursor.OpaqueCursor = SerializeReturnSyncState(state);
            cursor.Version++;
            await db.SaveChangesAsync(cancellationToken);
            // The read hands its lock and slot back between storefronts and every few pages; the saved state
            // continues it right after, so a long baseline no longer holds the returns lane in one piece.
            if (ReturnSyncSliceEnds(nextStorefront, pagesThisSlice))
                throw new JobProcessingException(JobExecutionResult.Continue("İade okuması kaldığı sayfadan birkaç saniye içinde devam edecek."));
        } while (!cancellationToken.IsCancellationRequested);
        await DateTrendyolReturnDecisionsAsync(tenantId, connectionId, correlationId, cancellationToken);
        return true;
    }

    internal const int ReturnSyncPagesPerSlice = 5;

    internal static bool ReturnSyncSliceEnds(bool movedToNextStorefront, int pagesThisSlice) =>
        movedToNextStorefront || pagesThisSlice >= ReturnSyncPagesPerSlice;

    private async Task<bool> SyncShopifyReturns(Guid tenantId, Guid connectionId, string payloadJson, string correlationId, CancellationToken cancellationToken)
    {
        var cursor = await Cursor(tenantId, connectionId, "RETURNS", cancellationToken);
        var now = timeProvider.GetUtcNow();
        var forceFull = ReadBoolean(payloadJson, "forceFull");
        // A run stopped between pages resumes its saved query, so the page cursor is always sent with the query it came from.
        var state = (forceFull ? null : ReadShopifyReturnSyncState(cursor.OpaqueCursor))
            ?? new(ShopifyReturnSyncStateVersion, null, ShopifyReturnReadStart(forceFull, cursor.LastSuccessAt, cursor.LastModifiedWatermark, now), now);
        while (!cancellationToken.IsCancellationRequested)
        {
            TrackRequest();
            var page = await returns.PollAsync(Context(tenantId, connectionId, correlationId, $"shopify-return-sync:{state.Cursor ?? "first"}"), new(state.ModifiedAfter, state.Anchor), new(state.Cursor, 100), cancellationToken);
            if (!page.IsSuccess) { TrackResultFailure(page.Error); throw JobProcessingException.FromAdapter(page.Error!); }
            foreach (var _ in page.Value!.Items) TrackReceived();
            foreach (var claim in page.Value.Items) await UpsertReturn(tenantId, connectionId, claim, cancellationToken);
            if (page.Value.HasMore)
            {
                if (string.IsNullOrWhiteSpace(page.Value.NextCursor) || string.Equals(page.Value.NextCursor, state.Cursor, StringComparison.Ordinal))
                    throw JobProcessingException.FromAdapter(new AdapterError(AdapterErrorClass.ContractViolation, "SHOPIFY_RETURN_CURSOR_INVALID", "Shopify iade akışı geçersiz veya yinelenen sayfa imleci döndürdü.", 502, null, null));
                state = NextShopifyReturnPage(state, page.Value.NextCursor, page.Value.LastModifiedSeen);
                cursor.OpaqueCursor = JsonSerializer.Serialize(state);
                cursor.Version++;
                await db.SaveChangesAsync(cancellationToken);
                continue;
            }
            cursor.OpaqueCursor = null;
            cursor.LastModifiedWatermark = state.Anchor;
            cursor.LastSuccessAt = timeProvider.GetUtcNow();
            cursor.LastError = null;
            cursor.LastErrorAt = null;
            cursor.ConsecutiveFailureCount = 0;
            cursor.Version++;
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        return false;
    }

    // n11 lists claims by request date only, so a 45-day request window is re-read (every 30 minutes, see ReturnRereadPolicy) to
    // pick up later status changes (approval, denial, refund) of recent claims.
    private const int PazaramaReturnLookbackDays = 45;
    private const int PazaramaReturnInitialLookbackDays = 90;
    private const int PazaramaReturnWindowDays = 28;

    // Pazarama lists refunds by request date in windows of at most a month. Every refund names the order
    // (orderNumber) and the product (productCode = barcode); the order line is found among the stored lines.
    private async Task<bool> SyncPazaramaReturns(Guid tenantId, Guid connectionId, string payloadJson, string correlationId, CancellationToken cancellationToken)
    {
        var cursor = await Cursor(tenantId, connectionId, "RETURNS", cancellationToken);
        var now = timeProvider.GetUtcNow();
        var full = ReadBoolean(payloadJson, "forceFull") || cursor.LastSuccessAt is null;
        var wideCursor = await Cursor(tenantId, connectionId, ReturnRereadPolicy.WideCursorResource, cancellationToken);
        var lookbackDays = ReturnRereadPolicy.LookbackDays(full, PazaramaReturnInitialLookbackDays, PazaramaReturnLookbackDays, wideCursor.LastSuccessAt, now);
        var from = now.AddDays(-lookbackDays);
        for (var windowStart = from; windowStart < now && !cancellationToken.IsCancellationRequested; windowStart = windowStart.AddDays(PazaramaReturnWindowDays))
        {
            var windowEnd = windowStart.AddDays(PazaramaReturnWindowDays) < now ? windowStart.AddDays(PazaramaReturnWindowDays) : now;
            var window = new ReturnPollWindow(windowStart, windowEnd);
            var page = 1;
            while (!cancellationToken.IsCancellationRequested)
            {
                TrackRequest();
                var result = await returns.PollAsync(Context(tenantId, connectionId, correlationId, $"pazarama-return-sync:{windowStart:yyyyMMdd}:{page}"), window, new(page.ToString(CultureInfo.InvariantCulture), 100), cancellationToken);
                if (!result.IsSuccess) { TrackResultFailure(result.Error); throw JobProcessingException.FromAdapter(result.Error!); }
                await RecordReturnPageIssuesAsync(tenantId, connectionId, "pazarama-return", result.Value!.Issues, cancellationToken);
                foreach (var claim in result.Value!.Items)
                {
                    TrackReceived();
                    var resolved = await ResolvePazaramaReturnOrderLinesAsync(tenantId, connectionId, correlationId, claim, cancellationToken);
                    await UpsertReturn(tenantId, connectionId, resolved, cancellationToken);
                }
                if (!result.Value.HasMore) break;
                page = int.TryParse(result.Value.NextCursor, NumberStyles.None, CultureInfo.InvariantCulture, out var next) && next > page ? next : page + 1;
            }
        }
        NoteWideReturnRead(wideCursor, lookbackDays, PazaramaReturnLookbackDays, now);
        cursor.OpaqueCursor = null;
        cursor.LastModifiedWatermark = now;
        cursor.LastSuccessAt = timeProvider.GetUtcNow();
        cursor.Version++;
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    private static void NoteWideReturnRead(SyncCursor wideCursor, int lookbackDays, int wideDays, DateTimeOffset now)
    {
        if (lookbackDays < wideDays) return;
        wideCursor.LastSuccessAt = now;
        wideCursor.Version++;
    }

    // A page's rows the adapter could not read (no claim or order number) are recorded, as on Hepsiburada.
    private async Task RecordReturnPageIssuesAsync(Guid tenantId, Guid connectionId, string keyPrefix, IReadOnlyList<AdapterPageIssue>? issues, CancellationToken cancellationToken)
    {
        foreach (var issue in issues ?? [])
        {
            TrackSkipped();
            await RecordIssue(tenantId, $"{keyPrefix}:{connectionId:N}:{issue.Identity}", issue.Code, issue.Message, cancellationToken);
        }
    }

    private async Task<RemoteReturnClaim> ResolvePazaramaReturnOrderLinesAsync(Guid tenantId, Guid connectionId, string correlationId, RemoteReturnClaim claim, CancellationToken cancellationToken)
    {
        var barcode = claim.Lines.Select(line => line.ProductCode?.Trim()).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        var stockCode = claim.Lines.Select(line => line.StockCode?.Trim()).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        decimal? refundAmount = null;
        try
        {
            using var raw = JsonDocument.Parse(claim.RawJson);
            barcode ??= raw.RootElement.TryGetProperty("productCode", out var code) && code.ValueKind == JsonValueKind.String ? code.GetString()?.Trim() : null;
            stockCode ??= raw.RootElement.TryGetProperty("productStockCode", out var stock) && stock.ValueKind == JsonValueKind.String ? stock.GetString()?.Trim() : null;
            refundAmount = PazaramaReturnMapper.RefundAmount(raw.RootElement);
        }
        catch (JsonException) { }
        var issueKey = $"pazarama-return-line:{connectionId:N}:{claim.ExternalClaimId}";
        if (string.IsNullOrWhiteSpace(barcode) && string.IsNullOrWhiteSpace(stockCode))
        {
            await RecordIssue(tenantId, issueKey, "PAZARAMA_RETURN_LINE_UNMATCHED", $"Pazarama iade talebi {claim.ExternalClaimId} için ürün barkodu gelmedi; sipariş satırı eşlenemedi.", cancellationToken);
            return claim;
        }

        var orderId = await db.Orders.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && (x.ExternalOrderId == claim.ExternalOrderId || x.OrderNumber == claim.ExternalOrderId))
            .Select(x => (Guid?)x.Id)
            .SingleOrDefaultAsync(cancellationToken);
        if (orderId is null)
        {
            TrackRequest();
            var order = await orders.GetAsync(Context(tenantId, connectionId, correlationId, $"pazarama-return-order:{claim.ExternalOrderId}"), claim.ExternalOrderId, cancellationToken);
            if (!order.IsSuccess) return claim;
            TrackReceived();
            await UpsertOrder(tenantId, connectionId, order.Value!, cancellationToken);
            orderId = await db.Orders.AsNoTracking()
                .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && (x.ExternalOrderId == claim.ExternalOrderId || x.OrderNumber == claim.ExternalOrderId))
                .Select(x => (Guid?)x.Id)
                .SingleOrDefaultAsync(cancellationToken);
            if (orderId is null) return claim;
        }

        var lines = await db.OrderLines.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.OrderId == orderId.Value)
            .Select(x => new { x.ExternalLineId, x.Barcode, x.Sku, Quantity = x.OrderedQuantity, x.UnitPrice })
            .ToListAsync(cancellationToken);
        var matched = lines.Where(line => !string.IsNullOrWhiteSpace(barcode) && string.Equals(line.Barcode?.Trim(), barcode.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
        if (matched.Count == 0 && !string.IsNullOrWhiteSpace(stockCode))
            matched = lines.Where(line => string.Equals(line.Sku?.Trim(), stockCode.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
        var matches = matched.Select(line => line.ExternalLineId).ToList();
        if (matches.Count == 0)
        {
            await RecordIssue(tenantId, issueKey, "PAZARAMA_RETURN_LINE_UNMATCHED", $"Pazarama iade talebi {claim.ExternalClaimId} (barkod {barcode ?? stockCode}) siparişteki hiçbir satırla eşleşmedi; iade satırı bağlanamadı.", cancellationToken);
            return claim;
        }
        await ResolveIssue(tenantId, issueKey, cancellationToken);
        // The quantity Pazarama did not send is worked out from the order line only when every matching line agrees on
        // its quantity and unit price; otherwise it stays unknown (0) and UpsertReturn records "adet bilinmiyor".
        var sameShape = matched.Select(line => (line.Quantity, line.UnitPrice)).Distinct().Count() == 1;
        return claim with
        {
            Lines = claim.Lines.Select(line => line with
            {
                ExternalOrderLineId = matches[0],
                AlternateExternalOrderLineIds = matches.Skip(1).ToList(),
                Quantity = sameShape ? PazaramaReturnMapper.ResolveQuantity(line.Quantity, refundAmount, matched[0].UnitPrice, matched[0].Quantity) ?? 0m : line.Quantity
            }).ToList()
        };
    }

    private const int N11ReturnLookbackDays = 45;
    private const int N11ReturnInitialLookbackDays = 90;

    private async Task<bool> SyncN11Returns(Guid tenantId, Guid connectionId, string payloadJson, string correlationId, CancellationToken cancellationToken)
    {
        var cursor = await Cursor(tenantId, connectionId, "RETURNS", cancellationToken);
        var now = timeProvider.GetUtcNow();
        var full = ReadBoolean(payloadJson, "forceFull") || cursor.LastSuccessAt is null;
        var wideCursor = await Cursor(tenantId, connectionId, ReturnRereadPolicy.WideCursorResource, cancellationToken);
        var lookbackDays = ReturnRereadPolicy.LookbackDays(full, N11ReturnInitialLookbackDays, N11ReturnLookbackDays, wideCursor.LastSuccessAt, now);
        var window = new ReturnPollWindow(now.AddDays(-lookbackDays), now);
        var page = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            TrackRequest();
            var result = await returns.PollAsync(Context(tenantId, connectionId, correlationId, $"n11-return-sync:{page}"), window, new(page.ToString(CultureInfo.InvariantCulture), 100), cancellationToken);
            if (!result.IsSuccess) { TrackResultFailure(result.Error); throw JobProcessingException.FromAdapter(result.Error!); }
            await RecordReturnPageIssuesAsync(tenantId, connectionId, "n11-return", result.Value!.Issues, cancellationToken);
            foreach (var claim in result.Value!.Items)
            {
                TrackReceived();
                var resolved = await ResolveN11ReturnOrderLinesAsync(tenantId, connectionId, correlationId, claim, cancellationToken);
                await UpsertReturn(tenantId, connectionId, resolved, cancellationToken);
            }
            if (!result.Value.HasMore) break;
            page = int.TryParse(result.Value.NextCursor, NumberStyles.None, CultureInfo.InvariantCulture, out var next) && next > page ? next : page + 1;
        }
        NoteWideReturnRead(wideCursor, lookbackDays, N11ReturnLookbackDays, now);
        cursor.OpaqueCursor = null;
        cursor.LastModifiedWatermark = now;
        cursor.LastSuccessAt = timeProvider.GetUtcNow();
        cursor.Version++;
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    // An n11 claim names the product (productId) but not the order line. Ensure the order is
    // stored, then point the claim at the order lines whose n11 snapshot carries that product.
    private async Task<RemoteReturnClaim> ResolveN11ReturnOrderLinesAsync(Guid tenantId, Guid connectionId, string correlationId, RemoteReturnClaim claim, CancellationToken cancellationToken)
    {
        var productId = claim.Lines.Select(line => line.ProductCode?.Trim()).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        if (productId is null)
            try
            {
                using var raw = JsonDocument.Parse(claim.RawJson);
                if (raw.RootElement.TryGetProperty("productId", out var value) && value.ValueKind == JsonValueKind.String) productId = value.GetString()?.Trim();
            }
            catch (JsonException) { }
        if (string.IsNullOrWhiteSpace(productId))
        {
            await RecordIssue(tenantId, $"n11-return-line:{connectionId:N}:{claim.ExternalClaimId}", "N11_RETURN_LINE_UNMATCHED", $"N11 iade talebi {claim.ExternalClaimId} için ürün numarası gelmedi; sipariş satırı eşlenemedi.", cancellationToken);
            return claim;
        }

        var orderId = await db.Orders.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && (x.ExternalOrderId == claim.ExternalOrderId || x.OrderNumber == claim.ExternalOrderId))
            .Select(x => (Guid?)x.Id)
            .SingleOrDefaultAsync(cancellationToken);
        if (orderId is null)
        {
            TrackRequest();
            var order = await orders.GetAsync(Context(tenantId, connectionId, correlationId, $"n11-return-order:{claim.ExternalOrderId}"), claim.ExternalOrderId, cancellationToken);
            if (!order.IsSuccess) return claim;
            TrackReceived();
            await UpsertOrder(tenantId, connectionId, order.Value!, cancellationToken);
            orderId = await db.Orders.AsNoTracking()
                .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && (x.ExternalOrderId == claim.ExternalOrderId || x.OrderNumber == claim.ExternalOrderId))
                .Select(x => (Guid?)x.Id)
                .SingleOrDefaultAsync(cancellationToken);
            if (orderId is null) return claim;
        }

        var lines = await db.OrderLines.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.OrderId == orderId.Value)
            .Select(x => new { x.ExternalLineId, x.SourceSnapshotJson, Quantity = x.OrderedQuantity })
            .ToListAsync(cancellationToken);
        var matched = lines.Where(line => string.Equals(N11LineProductId(line.SourceSnapshotJson), productId, StringComparison.Ordinal)).ToList();
        var matches = matched.Select(line => line.ExternalLineId).ToList();
        if (matches.Count == 0)
        {
            await RecordIssue(tenantId, $"n11-return-line:{connectionId:N}:{claim.ExternalClaimId}", "N11_RETURN_LINE_UNMATCHED", $"N11 iade talebi {claim.ExternalClaimId} (ürün {productId}) siparişteki hiçbir satırla eşleşmedi; iade satırı bağlanmadı.", cancellationToken);
            return claim;
        }
        // n11 sent no quantity: only an order line of one unit tells it (nothing else can come back); otherwise it stays
        // unknown (0) and UpsertReturn records "adet bilinmiyor" instead of writing 1.
        var singleUnit = matched.All(line => line.Quantity == 1);
        return claim with
        {
            Lines = claim.Lines.Select(line => line with { ExternalOrderLineId = matches[0], AlternateExternalOrderLineIds = matches.Skip(1).ToList(), Quantity = line.Quantity > 0 ? line.Quantity : singleUnit ? 1 : 0 }).ToList()
        };
    }

    internal static string? N11LineProductId(string? snapshotJson)
    {
        if (string.IsNullOrWhiteSpace(snapshotJson)) return null;
        try
        {
            using var document = JsonDocument.Parse(snapshotJson);
            return document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("productId", out var value)
                ? value.ValueKind switch
                {
                    JsonValueKind.String => value.GetString()?.Trim(),
                    JsonValueKind.Number => value.GetRawText(),
                    _ => null
                }
                : null;
        }
        catch (JsonException) { return null; }
    }

    private static ShopifyReturnSyncState? ReadShopifyReturnSyncState(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        try
        {
            var state = JsonSerializer.Deserialize<ShopifyReturnSyncState>(value);
            return state is { Version: ShopifyReturnSyncStateVersion } ? state : null;
        }
        catch (JsonException) { return null; }
    }
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

    // F: the catch-up for every platform. One pass builds one batch of the offers whose price or stock was not sent
    // (the platform's own batch limit: Trendyol 1,000, N11 and Pazarama their request caps, Hepsiburada its upload
    // limit, PTT AVM 1,000 products) and queues one push, so the platform's request rate is one request per pass.
    // A pass waits while an automatic push of the connection is still on its way, and (decision C) until an order read
    // started after the pass was queued has finished. Offers it cannot send are reported, not a reason to stop.
    private async Task<JobExecutionResult> ReconcileStock(Guid tenantId, Guid connectionId, string correlationId, Guid? jobId, CancellationToken cancellationToken)
    {
        var (blocked, decision) = await StockProjectionGateAsync(tenantId, connectionId, cancellationToken);
        if (blocked is not null || decision == StockProjectionDispatchDecision.Skip)
            return JobExecutionResult.SuccessWithNote("AUTOMATIC_WRITES_OFF", "Otomatik fiyat-stok yazması kapalı; stok uzlaştırması gönderim yapmadı.");
        if (decision == StockProjectionDispatchDecision.HoldForPricePolicy)
        {
            // Nothing goes out while the price policy is off: one held candidate is enough to show the issue.
            var held = await db.ChannelOffers.AsNoTracking()
                .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.Status == "ACTIVE")
                .OrderBy(x => x.VariantId).Select(x => (Guid?)x.VariantId).FirstOrDefaultAsync(cancellationToken);
            if (held is { } heldVariant) await DispatchStockProjectionVariant(tenantId, connectionId, heldVariant, decision, correlationId, PriceStockSendTriggers.Reconcile, cancellationToken);
            return JobExecutionResult.Success();
        }
        if (await AutomaticPushInFlightAsync(tenantId, connectionId, cancellationToken))
            return JobExecutionResult.SuccessWithNote("PRICE_INVENTORY_PUSH_IN_FLIGHT", "Bağlantının önceki fiyat-stok gönderimi sürüyor; uzlaştırma bir sonraki turda devam edecek.");
        if (await OrderFreshnessWaitAsync(tenantId, connectionId, jobId, cancellationToken) is { } wait) return wait;

        var composer = new PriceInventoryComposer(db);
        var build = await composer.BuildAsync(tenantId, connectionId, cancellationToken, automatic: true);
        if (!build.Succeeded)
        {
            var code = build.Error!.Code;
            if (code == "INVENTORY_REQUIRED")
                await RecordIssue(tenantId, $"price-inventory-inventory-required:{connectionId:N}", code, build.Error.Message, cancellationToken);
            // Every offer the pass could send is one the user has to fix: each is reported by itself.
            foreach (var invalid in composer.InvalidOffers)
                await RecordIssue(tenantId, PriceInventoryOfferIssues.Key(connectionId, invalid.VariantId), invalid.Code, invalid.Message, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            if (StockProjectionOutboxPolicy.IsSettledBuildFailure(code) || code == "INVENTORY_REQUIRED" || PriceInventoryOfferIssues.IsOfferProblem(code)) return JobExecutionResult.Success();
            return JobExecutionResult.Blocked(code, build.Error.Message);
        }
        if (build.Value!.MissingInventorySkus.Count > 0)
            await RecordIssue(tenantId, $"price-inventory-inventory-required:{connectionId:N}", "INVENTORY_REQUIRED", PriceInventoryComposer.MissingInventoryMessage(build.Value.MissingInventorySkus), cancellationToken);
        // ReconciledAt is an observation watermark, not an enqueue timestamp. It is updated only when a remote catalog
        // read observes stock; a disabled, blocked, or merely queued write remains visible as stale until then.
        return await QueueAutomaticPushAsync(tenantId, connectionId, build.Value, null, correlationId, PriceStockSendTriggers.Reconcile, cancellationToken);
    }

    // An automatic price/stock push of the connection that is queued, running or retrying.
    private Task<bool> AutomaticPushInFlightAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) =>
        db.IntegrationJobs.AsNoTracking().AnyAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId
            && x.JobType == MarketplaceJobTypes.PriceInventorySync && x.TriggerType != "user"
            && (x.Status == JobStatus.Pending || x.Status == JobStatus.Leased || x.Status == JobStatus.RetryScheduled), cancellationToken);

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
    // Every marketplace: a record the mapper refused while its order number was readable (AdapterPageIssue.RereadOrderId)
    // joins the re-read list (RejectedOrderRereadPolicy), so it is read again by number instead of being passed over.
    // ISLEYICI-12: one order page of a list read (Trendyol stream and reconcile, Shopify, N11, Pazarama): its orders are
    // counted, its contract issues recorded under the read's key prefix, its refused orders noted for a re-read, and its
    // orders stored. Hepsiburada's paid-order page stores only the orders it may import, so it keeps its own body.
    private async Task IngestOrderPageAsync(Guid tenantId, Guid connectionId, AdapterPageResult<RemoteOrder> page, string issueKeyPrefix, CancellationToken cancellationToken)
    {
        foreach (var _ in page.Items) TrackReceived();
        foreach (var issue in page.Issues ?? [])
            await RecordIssue(tenantId, $"{issueKeyPrefix}:{connectionId}:{issue.Identity}:{issue.Code}", issue.Code, issue.Message, cancellationToken);
        NoteRefusedOrders(page.Issues);
        await UpsertOrders(tenantId, connectionId, page.Items, cancellationToken);
    }

    private void NoteRefusedOrders(IReadOnlyList<AdapterPageIssue>? issues)
    {
        foreach (var issue in issues ?? [])
            if (!string.IsNullOrWhiteSpace(issue.RereadOrderId)) rereadOrderIds.Add(issue.RereadOrderId.Trim());
    }

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
    // The split parents of an order (O8): the packages that a package of this read, or one the order already stores,
    // names as its origin.
    private async Task<HashSet<string>> KnownSplitParentIdsAsync(Guid tenantId, Guid connectionId, Order? order, IReadOnlyList<RemotePackage> remotePackages, OrderIngestionBatch? batch, CancellationToken cancellationToken)
    {
        IEnumerable<(string ExternalPackageId, string? OriginExternalPackageId)> stored = [];
        if (order is not null)
            stored = batch?.PackagesByOrder.GetValueOrDefault(order.Id) is { } batchPackages
                ? batchPackages.Values.Select(package => (package.ExternalPackageId, package.OriginExternalPackageId)).ToList()
                : (await db.ShipmentPackages.AsNoTracking()
                    .Where(package => package.TenantId == tenantId && package.ConnectionId == connectionId && package.OrderId == order.Id && package.OriginExternalPackageId != null)
                    .Select(package => new { package.ExternalPackageId, package.OriginExternalPackageId })
                    .ToListAsync(cancellationToken))
                    .Select(package => (package.ExternalPackageId, package.OriginExternalPackageId));
        return SplitParentIds(remotePackages.Select(package => (package.ExternalPackageId, package.OriginExternalPackageId)).Concat(stored));
    }

    internal static HashSet<string> SplitParentIds(IEnumerable<(string ExternalPackageId, string? OriginExternalPackageId)> packages) => packages
        .Where(package => !string.IsNullOrWhiteSpace(package.OriginExternalPackageId) && !string.Equals(package.OriginExternalPackageId, package.ExternalPackageId, StringComparison.Ordinal))
        .Select(package => package.OriginExternalPackageId!)
        .ToHashSet(StringComparer.Ordinal);

    // The package's unreadable product lines as a detailed read found them (RemotePackage.UnreadableLineCount; null: the
    // read does not judge them), the same for every marketplace (UnreadableOrderLines). While lines are unreadable the
    // order carries an open issue that says so; a read that finds every line again clears the count and resolves it, and
    // the line issues the adapters raised for the package (whatever the read: order poll, notification, package list).
    // True when the count changed.
    private async Task<bool> ApplyUnreadableLines(Guid tenantId, Guid connectionId, string? platformCode, string orderNumber, ShipmentPackage package, RemotePackage remote, CancellationToken cancellationToken)
    {
        if (remote.UnreadableLineCount is not { } count) return false;
        var issueKey = UnreadableOrderLines.PackageIssueKey(connectionId, package.ExternalPackageId);
        if (count > 0)
            await RecordIssue(tenantId, issueKey, UnreadableOrderLines.IssueCode, UnreadableOrderLines.PackageIssueSummary(platformCode, orderNumber, package.ExternalPackageId, count), cancellationToken);
        if (package.UnreadableLineCount == count) return false;
        var cleared = package.UnreadableLineCount > 0 && count == 0;
        package.UnreadableLineCount = count;
        if (!cleared) return true;
        // The adapters' line issues are keyed {source}:{connection}:{package}[:{line}]:{code}.
        var packageKey = $":{connectionId}:{package.ExternalPackageId}:";
        var now = timeProvider.GetUtcNow();
        foreach (var issue in await db.OperationalIssues.Where(x => x.TenantId == tenantId && x.Status != IssueStatus.Resolved
                && (x.DedupeKey == issueKey || (x.Code == UnreadableOrderLines.IssueCode || x.Code == UnreadableOrderLines.AmountIssueCode || x.Code == MissingLinesIssueCode) && x.DedupeKey.Contains(packageKey))).ToListAsync(cancellationToken))
        {
            issue.Status = IssueStatus.Resolved;
            issue.ResolvedAt = now;
        }
        return true;
    }

    private const string MissingLinesIssueCode = "ORDER_PACKAGE_LINES_MISSING";

    // Every open quantity refusal of the package, whatever event it was raised for.
    private async Task ResolvePackageQuantityIssues(Guid tenantId, Guid connectionId, string externalPackageId, CancellationToken cancellationToken)
    {
        var prefix = OperationalIssueRetentionService.PackageQuantityIssuePrefix(connectionId, externalPackageId);
        foreach (var issue in await db.OperationalIssues.Where(x => x.TenantId == tenantId && x.Status != IssueStatus.Resolved && x.DedupeKey.StartsWith(prefix)).ToListAsync(cancellationToken))
        {
            issue.Status = IssueStatus.Resolved;
            issue.ResolvedAt = timeProvider.GetUtcNow();
        }
    }
    // Looks at the issues this unit of work already holds first, as RecordIssue does (ISLEYICI-8): an issue opened and
    // resolved before the next save is resolved, not left open.
    private async Task ResolveIssue(Guid tenantId, string key, CancellationToken cancellationToken) { var issue = db.OperationalIssues.Local.FirstOrDefault(x => x.TenantId == tenantId && x.DedupeKey == key) ?? await db.OperationalIssues.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.DedupeKey == key, cancellationToken); if (issue is not null && issue.Status != IssueStatus.Resolved) { issue.Status = IssueStatus.Resolved; issue.ResolvedAt = timeProvider.GetUtcNow(); } }
    private AdapterContext Context(Guid tenantId, Guid connectionId, string correlationId, string idempotency) => new(tenantId, connectionId, correlationId, idempotency, timeProvider.GetUtcNow().AddMinutes(2));
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static string Wire<T>(T value) where T : Enum => string.Concat(value.ToString().Select((ch, index) => char.IsUpper(ch) && index > 0 ? "_" + ch : ch.ToString())).ToUpperInvariant();
}
