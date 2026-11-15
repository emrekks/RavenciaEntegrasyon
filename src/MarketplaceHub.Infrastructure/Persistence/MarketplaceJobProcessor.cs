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
