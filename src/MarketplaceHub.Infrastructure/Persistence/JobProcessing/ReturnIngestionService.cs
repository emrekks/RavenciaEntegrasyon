using MarketplaceHub.Application;
using Microsoft.Extensions.Configuration;

namespace MarketplaceHub.Infrastructure.Persistence;

// ISLEYICI-9 stage 3c (16 Nov 2026): the return claim ingestion, moved out of the job processor as it was: storing a claim
// read (UpsertReturn/UpsertReturnCore), settling the return decisions the marketplace answered, and removing a cancelled
// claim. It works for one run of one job (run) on the job's AppDbContext, with the same issue recorder as the processor,
// and stores the orders a claim brings through that run's OrderIngestionService; the processor's methods of the same
// names call into it. The configuration is read for the two write switches the claim's invoice adjustment checks.
internal sealed partial class ReturnIngestionService(
    AppDbContext db,
    IssueRecorder issues,
    TimeProvider timeProvider,
    IConfiguration configuration,
    OrderIngestionService orderIngestion,
    JobRunContext run)
{
    public JobRunContext Run => run;

    private Task RecordIssue(Guid tenantId, string key, string code, string summary, CancellationToken cancellationToken) =>
        issues.RecordAsync(tenantId, key, code, summary, timeProvider.GetUtcNow(), cancellationToken);

    private Task ResolveIssue(Guid tenantId, string key, CancellationToken cancellationToken) =>
        issues.ResolveAsync(tenantId, key, timeProvider.GetUtcNow(), cancellationToken);

    private Task<string?> ConnectionPlatformCode(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) =>
        orderIngestion.ConnectionPlatformCode(tenantId, connectionId, cancellationToken);

    private Task<bool> UpsertOrder(Guid tenantId, Guid connectionId, RemoteOrder remote, CancellationToken cancellationToken) =>
        orderIngestion.UpsertOrder(tenantId, connectionId, remote, cancellationToken);

    private static string? SafeReturnTrackingLink(string? value) => ActionJobService.SafeReturnTrackingLink(value);

    private static string Wire<T>(T value) where T : Enum => MarketplaceJobProcessor.Wire(value);
}
