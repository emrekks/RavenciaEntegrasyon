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
