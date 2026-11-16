using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Microsoft.Extensions.Configuration;

namespace MarketplaceHub.Infrastructure.Persistence;

// ISLEYICI-9 stage 3e (16 Nov 2026): the return read loops, moved out of the job processor as they were: the claim reads
// of every marketplace (SyncReturns with its resumable states, buckets, windows and page guards), the open-claim scans,
// the return decision dating and the return reconciliation. It works for one run of one job (run) on the job's
// AppDbContext, stores claims through that run's ReturnIngestionService and their orders through its
// OrderIngestionService; the processor's methods of the same names call into it. Reads only: nothing is sent to a
// marketplace.
internal sealed partial class ReturnReadService(
    AppDbContext db,
    IssueRecorder issues,
    SyncCursorStore cursors,
    IReturnPort returns,
    IOrderPort orders,
    IConfiguration configuration,
    TimeProvider timeProvider,
    OrderIngestionService orderIngestion,
    ReturnIngestionService returnIngestion,
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

    private void TrackSkipped() => run.TrackSkipped();

    private static bool ReadBoolean(string payloadJson, string propertyName) => JobPayloadReader.Boolean(payloadJson, propertyName);

    private static int ReadBoundedInt(string payloadJson, string propertyName, int fallback, int minimum, int maximum) =>
        JobPayloadReader.BoundedInt(payloadJson, propertyName, fallback, minimum, maximum);

    private static string Short(string? value, int maximum) => MarketplaceJobProcessor.Short(value, maximum);

    // The same repeated-page test as Pazarama's order pages.
    private static bool PazaramaOrderPageRepeated(string? previousPageKey, string? pageKey) => OrderReadService.PazaramaOrderPageRepeated(previousPageKey, pageKey);

    private Task<string?> ConnectionPlatformCode(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) =>
        orderIngestion.ConnectionPlatformCode(tenantId, connectionId, cancellationToken);

    private Task<bool> UpsertOrder(Guid tenantId, Guid connectionId, RemoteOrder remote, CancellationToken cancellationToken) =>
        orderIngestion.UpsertOrder(tenantId, connectionId, remote, cancellationToken);

    private Task UpsertReturn(Guid tenantId, Guid connectionId, RemoteReturnClaim remote, CancellationToken cancellationToken) =>
        returnIngestion.UpsertReturn(tenantId, connectionId, remote, cancellationToken);
}
