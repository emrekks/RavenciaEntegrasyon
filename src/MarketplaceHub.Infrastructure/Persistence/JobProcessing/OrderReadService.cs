using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Microsoft.Extensions.Configuration;

namespace MarketplaceHub.Infrastructure.Persistence;

// ISLEYICI-9 stage 3e (16 Nov 2026): the order read loops, moved out of the job processor as they were: the order reads
// of every marketplace (SyncOrdersAndMatchLines with its windows, streams, resumable states, page guards and re-reads),
// the open-order scans, the order lifecycle and the order reconciliation. It works for one run of one job (run) on the
// job's AppDbContext and stores what it reads through that run's OrderIngestionService; the processor's methods of the
// same names call into it. Reads only: nothing is sent to a marketplace.
internal sealed partial class OrderReadService(
    AppDbContext db,
    IssueRecorder issues,
    SyncCursorStore cursors,
    IOrderPort orders,
    IOrderPackageReadPort orderPackages,
    IHepsiburadaInvoiceStatusPort hepsiburadaInvoiceStatus,
    IConfiguration configuration,
    TimeProvider timeProvider,
    OrderIngestionService orderIngestion,
    JobRunContext run)
{
    public JobRunContext Run => run;

    // What the moved code used from the processor: the same classes and helpers the processor calls.
    private Task RecordIssue(Guid tenantId, string key, string code, string summary, CancellationToken cancellationToken) =>
        issues.RecordAsync(tenantId, key, code, summary, timeProvider.GetUtcNow(), cancellationToken);

    private Task ResolveIssue(Guid tenantId, string key, CancellationToken cancellationToken) =>
        issues.ResolveAsync(tenantId, key, timeProvider.GetUtcNow(), cancellationToken);

    private Task<SyncCursor> Cursor(Guid tenantId, Guid connectionId, string resource, CancellationToken cancellationToken) =>
        cursors.GetAsync(tenantId, connectionId, resource, cancellationToken);

    private AdapterContext Context(Guid tenantId, Guid connectionId, string correlationId, string idempotency) => new(tenantId, connectionId, correlationId, idempotency, timeProvider.GetUtcNow().AddMinutes(2));

    private void TrackRequest() => run.TrackRequest();

    private void TrackReceived() => run.TrackReceived();

    private void TrackResultFailure(AdapterError? error) => run.TrackResultFailure(error);

    private static int ReadBoundedInt(string payloadJson, string propertyName, int fallback, int minimum, int maximum) =>
        JobPayloadReader.BoundedInt(payloadJson, propertyName, fallback, minimum, maximum);

    private static bool TrendyolPackageNotAsked(AdapterError? error) => MarketplaceJobProcessor.TrendyolPackageNotAsked(error);

    private Task<string?> ConnectionPlatformCode(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) =>
        orderIngestion.ConnectionPlatformCode(tenantId, connectionId, cancellationToken);

    internal Task UpsertOrders(Guid tenantId, Guid connectionId, IReadOnlyList<RemoteOrder> remotes, CancellationToken cancellationToken) =>
        orderIngestion.UpsertOrders(tenantId, connectionId, remotes, cancellationToken);

    private Task<bool> UpsertOrder(Guid tenantId, Guid connectionId, RemoteOrder remote, CancellationToken cancellationToken, OrderIngestionService.OrderIngestionBatch? batch = null, bool saveChanges = true, bool persistFinancialObservations = false) =>
        orderIngestion.UpsertOrder(tenantId, connectionId, remote, cancellationToken, batch, saveChanges, persistFinancialObservations);

    private Task IngestOrderPageAsync(Guid tenantId, Guid connectionId, AdapterPageResult<RemoteOrder> page, string issueKeyPrefix, CancellationToken cancellationToken) =>
        orderIngestion.IngestOrderPageAsync(tenantId, connectionId, page, issueKeyPrefix, cancellationToken);

    private void NoteRefusedOrders(IReadOnlyList<AdapterPageIssue>? pageIssues) => orderIngestion.NoteRefusedOrders(pageIssues);

    private Task MergeMarketplaceInvoiceState(ShipmentPackage package, RemotePackage remotePackage, CancellationToken cancellationToken, string? discoverySource = null) =>
        orderIngestion.MergeMarketplaceInvoiceState(package, remotePackage, cancellationToken, discoverySource);

    private Task<OrderIngestionService.OrderLineRematchOutcome> RematchUnmatchedOrderLines(Guid tenantId, Guid connectionId, CancellationToken cancellationToken, int batchSize = OrderIngestionService.OrderLineRematchBatchSize) =>
        orderIngestion.RematchUnmatchedOrderLines(tenantId, connectionId, cancellationToken, batchSize);

    private Task<IReadOnlyList<string>> RefusedOrdersToRereadAsync(Guid tenantId, Guid connectionId, IReadOnlyCollection<string> listed, DateTimeOffset now, CancellationToken cancellationToken) =>
        orderIngestion.RefusedOrdersToRereadAsync(tenantId, connectionId, listed, now, cancellationToken);
}
