using System.Text.Json;
using MarketplaceHub.Application;

namespace MarketplaceHub.Infrastructure.Jobs.Handlers;

// Marketplace questions of one connection ("kind" picks the open or the answered ones).
public sealed class QuestionSyncJobHandler(IMarketplaceQuestionService questions) : IJobHandler
{
    // An unreadable payload cannot get better by running again.
    private static readonly JobExecutionResult Rejected = JobExecutionResult.Blocked("F3_JOB_REJECTED", "Job payload, capability or current entity state did not permit the operation.");

    public bool Handles(string jobType) => jobType == MarketplaceJobTypes.QuestionSync;

    public async Task<JobOutcome> HandleAsync(JobRequest request, CancellationToken cancellationToken)
    {
        string? kind;
        try
        {
            using var payload = JsonDocument.Parse(request.PayloadJson);
            kind = payload.RootElement.TryGetProperty("kind", out var value) ? value.GetString() : null;
        }
        catch (JsonException)
        {
            return new(Rejected, new());
        }
        var result = await questions.SyncConnectionAsync(request.TenantId, request.ConnectionId, kind, cancellationToken);
        var changed = result.Added + result.Updated;
        if (result.Error is not null)
            return new(Failed(result), new(Requests: 1, Received: changed, Inserted: result.Added, Updated: result.Updated, Failed: 1));
        return new(JobExecutionResult.Success(), new(Requests: 1, Received: changed, Inserted: result.Added, Updated: result.Updated, Changed: changed));
    }

    // A marketplace failure keeps its class: a quota or network hiccup is retried (a quota keeps the attempt and backs
    // the connection off), refused credentials wait, a changed contract goes to review, with the marketplace's own
    // message instead of a generic rejection.
    internal static JobExecutionResult Failed(QuestionSyncView result) => result.Failure is { } failure
        ? JobExecutionResult.FromAdapterError(failure)
        : JobExecutionResult.Blocked("QUESTION_SYNC_CONNECTION_UNAVAILABLE", result.Error);
}
