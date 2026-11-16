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

    // The run handlers the dispatch tables use, for the dispatch tests.
    internal IReadOnlyList<IRunJobHandler> RunJobHandlers => [ProductSyncHandler, CatalogStructureImportHandler, ReferenceSyncHandler, OrderReadHandler, ReturnReadHandler];

    // The request a run handler gets: the job as the processor got it, with the run's read recorder when it keeps records.
    private JobRequest RunRequest(JobDispatchArgs a) =>
        new(a.TenantId, a.ConnectionId, a.JobType, a.PayloadJson, a.CorrelationId, a.JobId, run.ReadRecordingEnabled ? run.ReadRecorder : null);

    private static BoolJob RunBool(Func<MarketplaceJobProcessor, IRunBoolJobHandler> handler) =>
        (p, a, cancellationToken) => handler(p).HandleAsync(p.RunRequest(a), p.run, cancellationToken);

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
        [MarketplaceJobTypes.OrderReconciliation] = async (p, a, cancellationToken) => a.Platform == "TRENDYOL"
            ? await p.OrderReadHandler.HandleDirectAsync(p.RunRequest(a), p.run, cancellationToken)
            : null,
        [MarketplaceJobTypes.ExternalEffectReconcile] = async (p, a, cancellationToken) => await p.ReconcileExternalEffect(a.TenantId, a.ConnectionId, a.PayloadJson, a.CorrelationId, cancellationToken),
        [MarketplaceJobTypes.ListingVerify] = async (p, a, cancellationToken) => await p.VerifyListings(a.TenantId, a.ConnectionId, a.CorrelationId, a.JobId, cancellationToken),
        [MarketplaceJobTypes.ListingLookup] = async (p, a, cancellationToken) => await p.RunListingLookups(a.TenantId, a.ConnectionId, a.CorrelationId, a.JobId, cancellationToken),
        [MarketplaceJobTypes.OrderInvoiceReconciliation] = async (p, a, cancellationToken) => a.Platform == "TRENDYOL"
            ? (await p.ReconcileOrderInvoices(a.TenantId, a.ConnectionId, a.PayloadJson, a.CorrelationId, a.JobId, cancellationToken)).Unfinished
                ? JobExecutionResult.Continue("Fatura kontrolü kaldığı siparişten birkaç saniye içinde devam edecek.")
                : JobExecutionResult.Success()
            : null,
        // J1 (15 Nov 2026): the cargo-info check reads Trendyol packages only. On another store it has nothing to do and
        // succeeds with a note, instead of ending Blocked F3_JOB_REJECTED as if something had been refused.
        [MarketplaceJobTypes.TrendyolOrderCargoInfoReconciliation] = (_, a, _) => Task.FromResult(a.Platform == "TRENDYOL"
            ? null
            : JobExecutionResult.SuccessWithNote("CARGO_INFO_NOT_APPLICABLE", "Kargo bilgisi kontrolü yalnız Trendyol mağazalarında yapılır; bu mağazada yapılacak bir şey yok.")),
    };

    // Methods that return bool: false ends the job Blocked F3_JOB_REJECTED.
    private static readonly IReadOnlyDictionary<string, BoolJob> BoolJobDispatch = Table(
        ([MarketplaceJobTypes.ConnectionTest, MarketplaceJobTypes.ShopifyConnectionTest, MarketplaceJobTypes.HepsiburadaConnectionTest, MarketplaceJobTypes.N11ConnectionTest, MarketplaceJobTypes.PazaramaConnectionTest, MarketplaceJobTypes.PttAvmConnectionTest],
            async (p, a, cancellationToken) => await p.TestConnection(a.TenantId, a.ConnectionId, a.CorrelationId, cancellationToken)),
        ([.. ReferenceSyncRunHandler.Types], RunBool(p => p.ReferenceSyncHandler)),
        ([.. OrderReadRunHandler.SyncTypes, .. OrderReadRunHandler.RecoveryTypes, .. OrderReadRunHandler.StatusTypes], RunBool(p => p.OrderReadHandler)),
        ([MarketplaceJobTypes.TrendyolOrderCargoInfoReconciliation],
            async (p, a, cancellationToken) => await p.ReconcileTrendyolCargoInfo(a.TenantId, a.ConnectionId, a.CorrelationId, cancellationToken)),
        ([.. OrderReadRunHandler.ReconciliationTypes], RunBool(p => p.OrderReadHandler)),
        ([MarketplaceJobTypes.OrderInvoiceReconciliation, MarketplaceJobTypes.ShopifyOrderInvoiceReconciliation, MarketplaceJobTypes.HepsiburadaOrderInvoiceReconciliation],
            async (p, a, cancellationToken) => (await p.ReconcileOrderInvoices(a.TenantId, a.ConnectionId, a.PayloadJson, a.CorrelationId, a.JobId, cancellationToken)).Succeeded),
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
