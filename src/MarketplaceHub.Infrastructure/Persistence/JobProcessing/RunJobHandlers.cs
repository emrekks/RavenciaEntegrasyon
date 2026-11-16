using MarketplaceHub.Application;

namespace MarketplaceHub.Infrastructure.Persistence;

// ISLEYICI-9 stage 4 (16 Nov 2026): a job family that runs inside the processor's run. Unlike a public IJobHandler (the
// question and finance handlers, which keep their own JobOutcome telemetry), a run handler works on the processor's
// JobRunContext and its run services (JobRunServices): the execution lock, the read recorder and every counter stay
// the processor's, its exceptions reach the processor's own catch blocks, and the processor still records the
// telemetry, the read progress and the read records exactly where it did when the processor ran the method itself.
// The processor makes each handler from its own ports and AppDbContext and looks it up through its dispatch tables:
// a bool handler answers as the BoolJobDispatch methods do (false: Blocked F3_JOB_REJECTED).
internal interface IRunJobHandler
{
    // The job types this handler runs.
    IReadOnlyCollection<string> JobTypes { get; }
}

internal interface IRunBoolJobHandler : IRunJobHandler
{
    Task<bool> HandleAsync(JobRequest request, JobRunContext run, CancellationToken cancellationToken);
}

// A handler whose type also runs on the processor's DirectJobDispatch path (a Trendyol-only path): answers as those methods
// do, with a result the processor records as it records any direct result.
internal interface IRunDirectJobHandler : IRunJobHandler
{
    Task<JobExecutionResult> HandleDirectAsync(JobRequest request, JobRunContext run, CancellationToken cancellationToken);
}

// The order reads (OrderReadService): new orders (ORDERS_HOT, no baseline), the order history (ORDERS_RECOVERY, baseline
// allowed), the open-order scans and the order reconciliation; Trendyol's windowed reconciliation is the direct path.
internal sealed class OrderReadRunHandler(Func<JobRunContext, JobRunServices> services) : IRunBoolJobHandler, IRunDirectJobHandler
{
    public static readonly IReadOnlyCollection<string> SyncTypes = [MarketplaceJobTypes.OrderSync, MarketplaceJobTypes.ShopifyOrderSync, MarketplaceJobTypes.HepsiburadaOrderSync, MarketplaceJobTypes.N11OrderSync, MarketplaceJobTypes.PazaramaOrderSync, MarketplaceJobTypes.PttAvmOrderSync];
    public static readonly IReadOnlyCollection<string> RecoveryTypes = [MarketplaceJobTypes.OrderRecoverySync, MarketplaceJobTypes.ShopifyOrderRecoverySync, MarketplaceJobTypes.HepsiburadaOrderRecoverySync, MarketplaceJobTypes.N11OrderRecoverySync, MarketplaceJobTypes.PazaramaOrderRecoverySync, MarketplaceJobTypes.PttAvmOrderRecoverySync];
    public static readonly IReadOnlyCollection<string> StatusTypes = [MarketplaceJobTypes.OrderStatusSync, MarketplaceJobTypes.ShopifyOrderStatusSync, MarketplaceJobTypes.HepsiburadaOrderStatusSync, MarketplaceJobTypes.PazaramaOrderStatusSync, MarketplaceJobTypes.PttAvmOrderStatusSync];
    public static readonly IReadOnlyCollection<string> ReconciliationTypes = [MarketplaceJobTypes.OrderReconciliation, MarketplaceJobTypes.ShopifyOrderReconciliation];
    public static readonly IReadOnlyCollection<string> Types = [.. SyncTypes, .. RecoveryTypes, .. StatusTypes, .. ReconciliationTypes];

    public IReadOnlyCollection<string> JobTypes => Types;

    public Task<bool> HandleAsync(JobRequest request, JobRunContext run, CancellationToken cancellationToken)
    {
        var reads = services(run).OrderRead;
        if (SyncTypes.Contains(request.JobType))
            return reads.SyncOrdersAndMatchLines(request.TenantId, request.ConnectionId, request.PayloadJson, request.CorrelationId, "ORDERS_HOT", allowBaseline: false, cancellationToken);
        if (RecoveryTypes.Contains(request.JobType))
            return reads.SyncOrdersAndMatchLines(request.TenantId, request.ConnectionId, request.PayloadJson, request.CorrelationId, "ORDERS_RECOVERY", allowBaseline: true, cancellationToken);
        if (StatusTypes.Contains(request.JobType))
            return reads.SyncOpenOrders(request.TenantId, request.ConnectionId, request.CorrelationId, cancellationToken);
        if (ReconciliationTypes.Contains(request.JobType))
            return reads.ReconcileOrders(request.TenantId, request.ConnectionId, request.PayloadJson, request.CorrelationId, cancellationToken);
        throw new InvalidOperationException($"{request.JobType} is not an order read.");
    }

    // Trendyol's order reconciliation by windows (the table sends only Trendyol connections here).
    public Task<JobExecutionResult> HandleDirectAsync(JobRequest request, JobRunContext run, CancellationToken cancellationToken) =>
        services(run).OrderRead.ReconcileTrendyolOrderWindows(request.TenantId, request.ConnectionId, request.PayloadJson, request.CorrelationId, cancellationToken);
}

// The order invoice checks (InvoiceReconciliationService). Trendyol's is the direct path: a run stopped at its slice budget
// with orders left continues shortly; the others answer as the bool methods do.
internal sealed class OrderInvoiceReconciliationRunHandler(Func<JobRunContext, JobRunServices> services) : IRunBoolJobHandler, IRunDirectJobHandler
{
    public static readonly IReadOnlyCollection<string> Types = [MarketplaceJobTypes.OrderInvoiceReconciliation, MarketplaceJobTypes.ShopifyOrderInvoiceReconciliation, MarketplaceJobTypes.HepsiburadaOrderInvoiceReconciliation];

    public IReadOnlyCollection<string> JobTypes => Types;

    public async Task<bool> HandleAsync(JobRequest request, JobRunContext run, CancellationToken cancellationToken) =>
        (await services(run).InvoiceReconciliation.ReconcileOrderInvoices(request.TenantId, request.ConnectionId, request.PayloadJson, request.CorrelationId, request.JobId, cancellationToken)).Succeeded;

    // Trendyol's invoice check (the table sends only Trendyol connections here).
    public async Task<JobExecutionResult> HandleDirectAsync(JobRequest request, JobRunContext run, CancellationToken cancellationToken) =>
        (await services(run).InvoiceReconciliation.ReconcileOrderInvoices(request.TenantId, request.ConnectionId, request.PayloadJson, request.CorrelationId, request.JobId, cancellationToken)).Unfinished
            ? JobExecutionResult.Continue("Fatura kontrolü kaldığı siparişten birkaç saniye içinde devam edecek.")
            : JobExecutionResult.Success();
}

// Trendyol's cargo-info check (OrderReadService.ReconcileTrendyolCargoInfo); another store's job ends in the dispatch
// table with its "not applicable" note before it gets here.
internal sealed class TrendyolCargoInfoRunHandler(Func<JobRunContext, JobRunServices> services) : IRunBoolJobHandler
{
    public static readonly IReadOnlyCollection<string> Types = [MarketplaceJobTypes.TrendyolOrderCargoInfoReconciliation];

    public IReadOnlyCollection<string> JobTypes => Types;

    public Task<bool> HandleAsync(JobRequest request, JobRunContext run, CancellationToken cancellationToken) =>
        services(run).OrderRead.ReconcileTrendyolCargoInfo(request.TenantId, request.ConnectionId, request.CorrelationId, cancellationToken);
}

// The listing verification scan and the single-code lookups it queues (ListingVerificationService); both answer with a
// result of their own (a slice that continues, a note), so they run on the direct path only.
internal sealed class ListingVerificationRunHandler(Func<JobRunContext, JobRunServices> services) : IRunDirectJobHandler
{
    public static readonly IReadOnlyCollection<string> Types = [MarketplaceJobTypes.ListingVerify, MarketplaceJobTypes.ListingLookup];

    public IReadOnlyCollection<string> JobTypes => Types;

    public Task<JobExecutionResult> HandleDirectAsync(JobRequest request, JobRunContext run, CancellationToken cancellationToken) => request.JobType switch
    {
        MarketplaceJobTypes.ListingVerify => services(run).ListingVerification.VerifyListings(request.TenantId, request.ConnectionId, request.CorrelationId, request.JobId, cancellationToken),
        MarketplaceJobTypes.ListingLookup => services(run).ListingVerification.RunListingLookups(request.TenantId, request.ConnectionId, request.CorrelationId, request.JobId, cancellationToken),
        _ => throw new InvalidOperationException($"{request.JobType} is not a listing verification.")
    };
}

// The check of an unclear marketplace answer (ExternalEffectReconcileService); its result names a wait or a slice of its own.
internal sealed class ExternalEffectReconcileRunHandler(Func<JobRunContext, JobRunServices> services) : IRunDirectJobHandler
{
    public static readonly IReadOnlyCollection<string> Types = [MarketplaceJobTypes.ExternalEffectReconcile];

    public IReadOnlyCollection<string> JobTypes => Types;

    public Task<JobExecutionResult> HandleDirectAsync(JobRequest request, JobRunContext run, CancellationToken cancellationToken) =>
        services(run).ExternalEffectReconcile.ReconcileExternalEffect(request.TenantId, request.ConnectionId, request.PayloadJson, request.CorrelationId, cancellationToken);
}

// The package actions and the return decisions (ActionJobService): marketplace writes the user started.
internal sealed class ActionJobRunHandler(Func<JobRunContext, JobRunServices> services) : IRunBoolJobHandler
{
    public static readonly IReadOnlyCollection<string> Types = [MarketplaceJobTypes.ShipmentAction, MarketplaceJobTypes.ReturnAction];

    public IReadOnlyCollection<string> JobTypes => Types;

    public Task<bool> HandleAsync(JobRequest request, JobRunContext run, CancellationToken cancellationToken) => request.JobType switch
    {
        MarketplaceJobTypes.ShipmentAction => services(run).ActionJobs.ShipmentAction(request.TenantId, request.ConnectionId, request.PayloadJson, request.CorrelationId, cancellationToken),
        MarketplaceJobTypes.ReturnAction => services(run).ActionJobs.ReturnAction(request.TenantId, request.ConnectionId, request.PayloadJson, request.CorrelationId, cancellationToken),
        _ => throw new InvalidOperationException($"{request.JobType} is not a package or return action.")
    };
}

// The connection test of every marketplace (ConnectionTestService).
internal sealed class ConnectionTestRunHandler(Func<JobRunContext, JobRunServices> services) : IRunBoolJobHandler
{
    public static readonly IReadOnlyCollection<string> Types = [MarketplaceJobTypes.ConnectionTest, MarketplaceJobTypes.ShopifyConnectionTest, MarketplaceJobTypes.HepsiburadaConnectionTest, MarketplaceJobTypes.N11ConnectionTest, MarketplaceJobTypes.PazaramaConnectionTest, MarketplaceJobTypes.PttAvmConnectionTest];

    public IReadOnlyCollection<string> JobTypes => Types;

    public Task<bool> HandleAsync(JobRequest request, JobRunContext run, CancellationToken cancellationToken) =>
        services(run).ConnectionTest.TestConnection(request.TenantId, request.ConnectionId, request.CorrelationId, cancellationToken);
}

// The return reads (ReturnReadService): new claims, the open-claim scans and Trendyol's return reconciliation.
internal sealed class ReturnReadRunHandler(Func<JobRunContext, JobRunServices> services) : IRunBoolJobHandler
{
    public static readonly IReadOnlyCollection<string> SyncTypes = [MarketplaceJobTypes.ReturnSync, MarketplaceJobTypes.HepsiburadaReturnSync];
    public static readonly IReadOnlyCollection<string> StatusTypes = [MarketplaceJobTypes.ReturnStatusSync];
    public static readonly IReadOnlyCollection<string> ReconciliationTypes = [MarketplaceJobTypes.ReturnReconciliation];
    public static readonly IReadOnlyCollection<string> Types = [.. SyncTypes, .. StatusTypes, .. ReconciliationTypes];

    public IReadOnlyCollection<string> JobTypes => Types;

    public Task<bool> HandleAsync(JobRequest request, JobRunContext run, CancellationToken cancellationToken)
    {
        var reads = services(run).ReturnRead;
        if (SyncTypes.Contains(request.JobType))
            return reads.SyncReturns(request.TenantId, request.ConnectionId, request.PayloadJson, request.CorrelationId, cancellationToken);
        if (StatusTypes.Contains(request.JobType))
            return reads.SyncOpenReturns(request.TenantId, request.ConnectionId, request.CorrelationId, cancellationToken);
        if (ReconciliationTypes.Contains(request.JobType))
            return reads.ReconcileReturns(request.TenantId, request.ConnectionId, request.PayloadJson, request.CorrelationId, cancellationToken);
        throw new InvalidOperationException($"{request.JobType} is not a return read.");
    }
}

// ProductSync, ShopifyProductSync and HepsiburadaProductSync: the product read (CatalogImportService.SyncProducts).
internal sealed class ProductSyncRunHandler(Func<JobRunContext, JobRunServices> services) : IRunBoolJobHandler
{
    public static readonly IReadOnlyCollection<string> Types = [MarketplaceJobTypes.ProductSync, MarketplaceJobTypes.ShopifyProductSync, MarketplaceJobTypes.HepsiburadaProductSync];

    public IReadOnlyCollection<string> JobTypes => Types;

    public Task<bool> HandleAsync(JobRequest request, JobRunContext run, CancellationToken cancellationToken) =>
        services(run).CatalogImport.SyncProducts(request.TenantId, request.ConnectionId, request.PayloadJson, request.CorrelationId, request.JobId, cancellationToken);
}

// The reference list reads of every marketplace with their follow-ups (ReferenceSyncService.SyncReferenceJob). The brand
// list is read whole and the user maps it.
internal sealed class ReferenceSyncRunHandler(Func<JobRunContext, JobRunServices> services) : IRunBoolJobHandler
{
    public static readonly IReadOnlyCollection<string> Types = [MarketplaceJobTypes.ReferenceSync, MarketplaceJobTypes.HepsiburadaReferenceSync, MarketplaceJobTypes.N11ReferenceSync, MarketplaceJobTypes.PazaramaReferenceSync, MarketplaceJobTypes.PttAvmReferenceSync];

    public IReadOnlyCollection<string> JobTypes => Types;

    public Task<bool> HandleAsync(JobRequest request, JobRunContext run, CancellationToken cancellationToken) =>
        services(run).ReferenceSync.SyncReferenceJob(request.TenantId, request.ConnectionId, request.PayloadJson, request.CorrelationId, request.JobId, cancellationToken);
}

// CatalogStructureImport: the category and attribute structure read (CatalogImportService.ImportCatalogStructure).
internal sealed class CatalogStructureImportRunHandler(Func<JobRunContext, JobRunServices> services) : IRunBoolJobHandler
{
    public static readonly IReadOnlyCollection<string> Types = [MarketplaceJobTypes.CatalogStructureImport];

    public IReadOnlyCollection<string> JobTypes => Types;

    public Task<bool> HandleAsync(JobRequest request, JobRunContext run, CancellationToken cancellationToken) =>
        services(run).CatalogImport.ImportCatalogStructure(request.TenantId, request.ConnectionId, request.PayloadJson, request.CorrelationId, request.JobId, cancellationToken);
}
