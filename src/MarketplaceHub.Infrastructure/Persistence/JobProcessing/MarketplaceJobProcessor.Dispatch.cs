using MarketplaceHub.Application;

namespace MarketplaceHub.Infrastructure.Persistence;

// Which method runs a job type the processor runs itself (KUYRUK-9 step 1). A type with its own IJobHandler class is
// not listed. ProcessAsync looks a type up in DirectJobDispatch first, then in BoolJobDispatch; a type in neither ends
// Blocked F3_JOB_REJECTED, as the switch's `_ => false` did.
public sealed partial class MarketplaceJobProcessor
{
    private sealed record JobDispatchArgs(Guid TenantId, Guid ConnectionId, string JobType, string PayloadJson, string CorrelationId, Guid? JobId, string? Platform);

    // ISLEYICI-9 stage 4: the job families that run in a run job handler (IRunJobHandler), made from this processor's ports
    // and AppDbContext and given its run; the tables below map their types to them.
    private ProductSyncRunHandler? productSyncHandler;
    private CatalogStructureImportRunHandler? catalogStructureImportHandler;
    private ReferenceSyncRunHandler? referenceSyncHandler;

    private ProductSyncRunHandler ProductSyncHandler => productSyncHandler ??= new(ServicesFor);

    private CatalogStructureImportRunHandler CatalogStructureImportHandler => catalogStructureImportHandler ??= new(ServicesFor);

    private ReferenceSyncRunHandler ReferenceSyncHandler => referenceSyncHandler ??= new(ServicesFor);

    private OrderReadRunHandler? orderReadHandler;

    private OrderReadRunHandler OrderReadHandler => orderReadHandler ??= new(ServicesFor);

    private ReturnReadRunHandler? returnReadHandler;

    private ReturnReadRunHandler ReturnReadHandler => returnReadHandler ??= new(ServicesFor);

    // ISLEYICI-9 stage 5 step 1: the invoice checks, the cargo-info check, the listing verification and the connection tests.
    private OrderInvoiceReconciliationRunHandler? orderInvoiceReconciliationHandler;
    private TrendyolCargoInfoRunHandler? trendyolCargoInfoHandler;
    private ListingVerificationRunHandler? listingVerificationHandler;
    private ConnectionTestRunHandler? connectionTestHandler;

    private OrderInvoiceReconciliationRunHandler OrderInvoiceReconciliationHandler => orderInvoiceReconciliationHandler ??= new(ServicesFor);

    private TrendyolCargoInfoRunHandler TrendyolCargoInfoHandler => trendyolCargoInfoHandler ??= new(ServicesFor);

    private ListingVerificationRunHandler ListingVerificationHandler => listingVerificationHandler ??= new(ServicesFor);

    private ConnectionTestRunHandler ConnectionTestHandler => connectionTestHandler ??= new(ServicesFor);

    // ISLEYICI-9 stage 5 step 2: the effect check.
    private ExternalEffectReconcileRunHandler? externalEffectReconcileHandler;

    private ExternalEffectReconcileRunHandler ExternalEffectReconcileHandler => externalEffectReconcileHandler ??= new(ServicesFor);

    // The run handlers the dispatch tables use, for the dispatch tests.
    internal IReadOnlyList<IRunJobHandler> RunJobHandlers => [ProductSyncHandler, CatalogStructureImportHandler, ReferenceSyncHandler, OrderReadHandler, ReturnReadHandler,
        OrderInvoiceReconciliationHandler, TrendyolCargoInfoHandler, ListingVerificationHandler, ConnectionTestHandler, ExternalEffectReconcileHandler];

    // The request a run handler gets: the job as the processor got it, with the run's read recorder when it keeps records.
    private JobRequest RunRequest(JobDispatchArgs a) =>
        new(a.TenantId, a.ConnectionId, a.JobType, a.PayloadJson, a.CorrelationId, a.JobId, run.ReadRecordingEnabled ? run.ReadRecorder : null);

    private static BoolJob RunBool(Func<MarketplaceJobProcessor, IRunBoolJobHandler> handler) =>
        (p, a, cancellationToken) => handler(p).HandleAsync(p.RunRequest(a), p.run, cancellationToken);

    private static DirectJob RunDirect(Func<MarketplaceJobProcessor, IRunDirectJobHandler> handler) =>
        async (p, a, cancellationToken) => await handler(p).HandleDirectAsync(p.RunRequest(a), p.run, cancellationToken);

    // A handler's direct path for Trendyol connections only; another platform's job goes on to BoolJobDispatch.
    private static DirectJob RunDirectOnTrendyol(Func<MarketplaceJobProcessor, IRunDirectJobHandler> handler) =>
        async (p, a, cancellationToken) => a.Platform == "TRENDYOL" ? await handler(p).HandleDirectAsync(p.RunRequest(a), p.run, cancellationToken) : null;

    private delegate Task<JobExecutionResult?> DirectJob(MarketplaceJobProcessor processor, JobDispatchArgs a, CancellationToken cancellationToken);

    private delegate Task<bool> BoolJob(MarketplaceJobProcessor processor, JobDispatchArgs a, CancellationToken cancellationToken);

    // Methods that return a JobExecutionResult. A null result means the method is not for this connection's platform
    // (a Trendyol-only path), and the job goes on to BoolJobDispatch.
    private static readonly IReadOnlyDictionary<string, DirectJob> DirectJobDispatch = new Dictionary<string, DirectJob>(StringComparer.Ordinal)
    {
        [MarketplaceJobTypes.ProductCreate] = async (p, a, cancellationToken) => await p.CreateProduct(a.TenantId, a.ConnectionId, a.PayloadJson, a.CorrelationId, cancellationToken),
        [MarketplaceJobTypes.ProductApprovalReconcile] = async (p, a, cancellationToken) => await p.ReconcileProductApproval(a.TenantId, a.ConnectionId, a.PayloadJson, a.CorrelationId, cancellationToken),
        [MarketplaceJobTypes.ProductUpdate] = async (p, a, cancellationToken) => await p.UpdateProduct(a.TenantId, a.ConnectionId, a.PayloadJson, a.CorrelationId, cancellationToken),
        [MarketplaceJobTypes.ProductArchive] = async (p, a, cancellationToken) => await p.ArchiveProduct(a.TenantId, a.ConnectionId, a.PayloadJson, a.CorrelationId, cancellationToken),
        [MarketplaceJobTypes.PriceInventorySync] = async (p, a, cancellationToken) => await p.SyncPriceInventory(a.TenantId, a.ConnectionId, a.PayloadJson, a.CorrelationId, cancellationToken),
        [MarketplaceJobTypes.StockProjectionDispatch] = async (p, a, cancellationToken) => await p.DispatchStockProjection(a.TenantId, a.ConnectionId, a.PayloadJson, a.CorrelationId, a.JobId, cancellationToken),
        [MarketplaceJobTypes.StockReconciliation] = async (p, a, cancellationToken) => await p.ReconcileStock(a.TenantId, a.ConnectionId, a.CorrelationId, a.JobId, cancellationToken),
        [MarketplaceJobTypes.CommonLabel] = async (p, a, cancellationToken) => await p.CommonLabel(a.TenantId, a.ConnectionId, a.PayloadJson, a.CorrelationId, cancellationToken),
        [MarketplaceJobTypes.CapabilityProbe] = async (p, a, cancellationToken) => await p.LabelCapabilityProbe(a.TenantId, a.ConnectionId, a.PayloadJson, a.CorrelationId, cancellationToken),
        [MarketplaceJobTypes.StageTestOrder] = async (p, a, cancellationToken) => await p.CreateStageTestOrder(a.TenantId, a.ConnectionId, a.PayloadJson, a.CorrelationId, cancellationToken),
        [MarketplaceJobTypes.OrderReconciliation] = RunDirectOnTrendyol(p => p.OrderReadHandler),
        [MarketplaceJobTypes.ExternalEffectReconcile] = RunDirect(p => p.ExternalEffectReconcileHandler),
        [MarketplaceJobTypes.ListingVerify] = RunDirect(p => p.ListingVerificationHandler),
        [MarketplaceJobTypes.ListingLookup] = RunDirect(p => p.ListingVerificationHandler),
        [MarketplaceJobTypes.OrderInvoiceReconciliation] = RunDirectOnTrendyol(p => p.OrderInvoiceReconciliationHandler),
        // J1 (15 Nov 2026): the cargo-info check reads Trendyol packages only. On another store it has nothing to do and
        // succeeds with a note, instead of ending Blocked F3_JOB_REJECTED as if something had been refused.
        [MarketplaceJobTypes.TrendyolOrderCargoInfoReconciliation] = (_, a, _) => Task.FromResult(a.Platform == "TRENDYOL"
            ? null
            : JobExecutionResult.SuccessWithNote("CARGO_INFO_NOT_APPLICABLE", "Kargo bilgisi kontrolü yalnız Trendyol mağazalarında yapılır; bu mağazada yapılacak bir şey yok.")),
    };

    // Methods that return bool: false ends the job Blocked F3_JOB_REJECTED.
    private static readonly IReadOnlyDictionary<string, BoolJob> BoolJobDispatch = Table(
        ([.. ConnectionTestRunHandler.Types], RunBool(p => p.ConnectionTestHandler)),
        ([.. ReferenceSyncRunHandler.Types], RunBool(p => p.ReferenceSyncHandler)),
        ([.. OrderReadRunHandler.SyncTypes, .. OrderReadRunHandler.RecoveryTypes, .. OrderReadRunHandler.StatusTypes], RunBool(p => p.OrderReadHandler)),
        ([.. TrendyolCargoInfoRunHandler.Types], RunBool(p => p.TrendyolCargoInfoHandler)),
        ([.. OrderReadRunHandler.ReconciliationTypes], RunBool(p => p.OrderReadHandler)),
        ([.. OrderInvoiceReconciliationRunHandler.Types], RunBool(p => p.OrderInvoiceReconciliationHandler)),
        ([.. ProductSyncRunHandler.Types], RunBool(p => p.ProductSyncHandler)),
        ([.. ReturnReadRunHandler.Types], RunBool(p => p.ReturnReadHandler)),
        ([.. CatalogStructureImportRunHandler.Types], RunBool(p => p.CatalogStructureImportHandler)),
        ([MarketplaceJobTypes.WebhookIngest, MarketplaceJobTypes.ShopifyWebhookIngest, MarketplaceJobTypes.HepsiburadaWebhookIngest],
            async (p, a, cancellationToken) => await p.IngestWebhook(a.TenantId, a.ConnectionId, a.PayloadJson, a.CorrelationId, cancellationToken)),
        ([MarketplaceJobTypes.ShipmentAction],
            async (p, a, cancellationToken) => await p.ShipmentAction(a.TenantId, a.ConnectionId, a.PayloadJson, a.CorrelationId, cancellationToken)),
        ([MarketplaceJobTypes.ReturnAction],
            async (p, a, cancellationToken) => await p.ReturnAction(a.TenantId, a.ConnectionId, a.PayloadJson, a.CorrelationId, cancellationToken)));

    // The job types each table lists, for the test that every processor job type has a way to run.
    internal static IReadOnlyCollection<string> DirectDispatchJobTypes => [.. DirectJobDispatch.Keys];

    internal static IReadOnlyCollection<string> BoolDispatchJobTypes => [.. BoolJobDispatch.Keys];

    private static Dictionary<string, BoolJob> Table(params (string[] JobTypes, BoolJob Run)[] entries)
    {
        var table = new Dictionary<string, BoolJob>(StringComparer.Ordinal);
        foreach (var (jobTypes, run) in entries)
            foreach (var jobType in jobTypes) table.Add(jobType, run);
        return table;
    }
}
