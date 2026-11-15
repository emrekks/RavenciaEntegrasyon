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
