using MarketplaceHub.Application;

namespace MarketplaceHub.Infrastructure.Persistence;

// Which method runs a job type the processor runs itself (KUYRUK-9 step 1). A type with its own IJobHandler class is
// not listed. ProcessAsync looks a type up in DirectJobDispatch first, then in BoolJobDispatch; a type in neither ends
// Blocked F3_JOB_REJECTED, as the switch's `_ => false` did.
public sealed partial class MarketplaceJobProcessor
{
    private sealed record JobDispatchArgs(Guid TenantId, Guid ConnectionId, string PayloadJson, string CorrelationId, Guid? JobId, string? Platform);

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
            ? await p.ReconcileTrendyolOrderWindows(a.TenantId, a.ConnectionId, a.PayloadJson, a.CorrelationId, cancellationToken)
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
        ([MarketplaceJobTypes.ReferenceSync, MarketplaceJobTypes.HepsiburadaReferenceSync, MarketplaceJobTypes.N11ReferenceSync, MarketplaceJobTypes.PazaramaReferenceSync, MarketplaceJobTypes.PttAvmReferenceSync],
            async (p, a, cancellationToken) => await p.SyncReferenceJob(a.TenantId, a.ConnectionId, a.PayloadJson, a.CorrelationId, a.JobId, cancellationToken)),
        ([MarketplaceJobTypes.OrderSync, MarketplaceJobTypes.ShopifyOrderSync, MarketplaceJobTypes.HepsiburadaOrderSync, MarketplaceJobTypes.N11OrderSync, MarketplaceJobTypes.PazaramaOrderSync, MarketplaceJobTypes.PttAvmOrderSync],
            async (p, a, cancellationToken) => await p.SyncOrdersAndMatchLines(a.TenantId, a.ConnectionId, a.PayloadJson, a.CorrelationId, "ORDERS_HOT", allowBaseline: false, cancellationToken)),
        ([MarketplaceJobTypes.OrderRecoverySync, MarketplaceJobTypes.ShopifyOrderRecoverySync, MarketplaceJobTypes.HepsiburadaOrderRecoverySync, MarketplaceJobTypes.N11OrderRecoverySync, MarketplaceJobTypes.PazaramaOrderRecoverySync, MarketplaceJobTypes.PttAvmOrderRecoverySync],
            async (p, a, cancellationToken) => await p.SyncOrdersAndMatchLines(a.TenantId, a.ConnectionId, a.PayloadJson, a.CorrelationId, "ORDERS_RECOVERY", allowBaseline: true, cancellationToken)),
        ([MarketplaceJobTypes.OrderStatusSync, MarketplaceJobTypes.ShopifyOrderStatusSync, MarketplaceJobTypes.HepsiburadaOrderStatusSync, MarketplaceJobTypes.PazaramaOrderStatusSync, MarketplaceJobTypes.PttAvmOrderStatusSync],
            async (p, a, cancellationToken) => await p.SyncOpenOrders(a.TenantId, a.ConnectionId, a.CorrelationId, cancellationToken)),
        ([MarketplaceJobTypes.TrendyolOrderCargoInfoReconciliation],
            async (p, a, cancellationToken) => await p.ReconcileTrendyolCargoInfo(a.TenantId, a.ConnectionId, a.CorrelationId, cancellationToken)),
        ([MarketplaceJobTypes.OrderReconciliation, MarketplaceJobTypes.ShopifyOrderReconciliation],
            async (p, a, cancellationToken) => await p.ReconcileOrders(a.TenantId, a.ConnectionId, a.PayloadJson, a.CorrelationId, cancellationToken)),
        ([MarketplaceJobTypes.OrderInvoiceReconciliation, MarketplaceJobTypes.ShopifyOrderInvoiceReconciliation, MarketplaceJobTypes.HepsiburadaOrderInvoiceReconciliation],
            async (p, a, cancellationToken) => (await p.ReconcileOrderInvoices(a.TenantId, a.ConnectionId, a.PayloadJson, a.CorrelationId, a.JobId, cancellationToken)).Succeeded),
        ([MarketplaceJobTypes.ProductSync, MarketplaceJobTypes.ShopifyProductSync, MarketplaceJobTypes.HepsiburadaProductSync],
            async (p, a, cancellationToken) => await p.SyncProducts(a.TenantId, a.ConnectionId, a.PayloadJson, a.CorrelationId, a.JobId, cancellationToken)),
        ([MarketplaceJobTypes.ReturnSync, MarketplaceJobTypes.HepsiburadaReturnSync],
            async (p, a, cancellationToken) => await p.SyncReturns(a.TenantId, a.ConnectionId, a.PayloadJson, a.CorrelationId, cancellationToken)),
        ([MarketplaceJobTypes.ReturnStatusSync],
            async (p, a, cancellationToken) => await p.SyncOpenReturns(a.TenantId, a.ConnectionId, a.CorrelationId, cancellationToken)),
        ([MarketplaceJobTypes.ReturnReconciliation],
            async (p, a, cancellationToken) => await p.ReconcileReturns(a.TenantId, a.ConnectionId, a.PayloadJson, a.CorrelationId, cancellationToken)),
        ([MarketplaceJobTypes.CatalogStructureImport],
            async (p, a, cancellationToken) => await p.ImportCatalogStructure(a.TenantId, a.ConnectionId, a.PayloadJson, a.CorrelationId, a.JobId, cancellationToken)),
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
