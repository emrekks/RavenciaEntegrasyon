using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Microsoft.Extensions.Configuration;

namespace MarketplaceHub.Infrastructure.Persistence;

// ISLEYICI-9 stage 5 step 3e (16 Nov 2026): the three webhook ingests (Trendyol's order packages, Shopify's order and
// refund notices, Hepsiburada's order events and claims), moved out of the job processor whole: each of its partial
// files became a part of this class unchanged but for its class line. A notice only makes the panel read: it stores what
// it reads through the run's order and return ingestion and read services, closes its inbox row and queues targeted
// order reads; nothing is sent. It works for one run of one job (run) on the job's AppDbContext.
internal sealed partial class WebhookIngestService(
    AppDbContext db,
    IssueRecorder issues,
    IOrderPort orders,
    IOrderPackageReadPort orderPackages,
    IConfiguration configuration,
    TimeProvider timeProvider,
    OrderIngestionService orderIngestion,
    ReturnIngestionService returnIngestion,
    OrderReadService orderRead,
    ReturnReadService returnRead,
    ActionJobService actionJobs,
    JobRunContext run)
{
    // What the moved code used from the processor: the same classes and helpers the processor calls.
    private Task RecordIssue(Guid tenantId, string key, string code, string summary, CancellationToken cancellationToken) =>
        issues.RecordAsync(tenantId, key, code, summary, timeProvider.GetUtcNow(), cancellationToken);

    private AdapterContext Context(Guid tenantId, Guid connectionId, string correlationId, string idempotency) => new(tenantId, connectionId, correlationId, idempotency, timeProvider.GetUtcNow().AddMinutes(2));

    private void TrackRequest() => run.TrackRequest();

    private void TrackReceived() => run.TrackReceived();

    private void TrackResultFailure(AdapterError? error) => run.TrackResultFailure(error);

    private static string Hash(string value) => TextValues.Sha256Hex(value);

    private Task<string?> ConnectionPlatformCode(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) =>
        orderIngestion.ConnectionPlatformCode(tenantId, connectionId, cancellationToken);

    private Task UpsertOrders(Guid tenantId, Guid connectionId, IReadOnlyList<RemoteOrder> remotes, CancellationToken cancellationToken) =>
        orderIngestion.UpsertOrders(tenantId, connectionId, remotes, cancellationToken);

    private Task<bool> UpsertOrder(Guid tenantId, Guid connectionId, RemoteOrder remote, CancellationToken cancellationToken, OrderIngestionService.OrderIngestionBatch? batch = null, bool saveChanges = true, bool persistFinancialObservations = false) =>
        orderIngestion.UpsertOrder(tenantId, connectionId, remote, cancellationToken, batch, saveChanges, persistFinancialObservations);

    private void NoteRefusedOrders(IReadOnlyList<AdapterPageIssue>? pageIssues) => orderIngestion.NoteRefusedOrders(pageIssues);

    private Task UpsertReturn(Guid tenantId, Guid connectionId, RemoteReturnClaim remote, CancellationToken cancellationToken) =>
        returnIngestion.UpsertReturn(tenantId, connectionId, remote, cancellationToken);

    private Task<bool> SyncHepsiburadaOrders(Guid tenantId, Guid connectionId, string payloadJson, string correlationId, string cursorResourceType, bool allowBaseline, CancellationToken cancellationToken) =>
        orderRead.SyncHepsiburadaOrders(tenantId, connectionId, payloadJson, correlationId, cursorResourceType, allowBaseline, cancellationToken);

    private Task SaveRefusedOrdersForRereadAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) =>
        orderRead.SaveRefusedOrdersForRereadAsync(tenantId, connectionId, cancellationToken);

    private static string? TrendyolWebhookStorefront(string rawPackageJson) => OrderReadService.TrendyolWebhookStorefront(rawPackageJson);

    private Task<bool> SyncReturns(Guid tenantId, Guid connectionId, string payloadJson, string correlationId, CancellationToken cancellationToken) =>
        returnRead.SyncReturns(tenantId, connectionId, payloadJson, correlationId, cancellationToken);

    private Task MarkHepsiburadaPackageUnpackedAsync(Guid tenantId, Guid connectionId, Guid packageId, DateTimeOffset now, CancellationToken cancellationToken) =>
        actionJobs.MarkHepsiburadaPackageUnpackedAsync(tenantId, connectionId, packageId, now, cancellationToken);
}
