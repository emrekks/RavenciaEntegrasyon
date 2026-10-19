using MarketplaceHub.Infrastructure.Jobs.Handlers;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class QuestionSyncJobHandlerTests
{
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly Guid Connection = Guid.NewGuid();

    [Fact]
    public async Task ASyncReportsWhatItAddedAndUpdated()
    {
        var service = new StubQuestions(new QuestionSyncView(2, 3, DateTimeOffset.UtcNow));
        var handler = new QuestionSyncJobHandler(service);

        var outcome = await handler.HandleAsync(new JobRequest(Tenant, Connection, MarketplaceJobTypes.QuestionSync, """{"kind":"OPEN"}""", "c", null), CancellationToken.None);

        Assert.True(outcome.Result.Succeeded);
        Assert.Equal(new JobTelemetry(Requests: 1, Received: 5, Inserted: 2, Updated: 3, Changed: 5), outcome.Telemetry);
        Assert.Equal((Tenant, Connection, "OPEN"), service.Called);
    }

    [Fact]
    public async Task AFailedSyncKeepsTheProcessorsRejectedOutcome()
    {
        var handler = new QuestionSyncJobHandler(new StubQuestions(new QuestionSyncView(0, 0, DateTimeOffset.UtcNow, "boom")));

        var failed = await handler.HandleAsync(new JobRequest(Tenant, Connection, MarketplaceJobTypes.QuestionSync, "{}", "c", null), CancellationToken.None);
        var unreadable = await handler.HandleAsync(new JobRequest(Tenant, Connection, MarketplaceJobTypes.QuestionSync, "not json", "c", null), CancellationToken.None);

        Assert.Equal("F3_JOB_REJECTED", failed.Result.ErrorCode);
        Assert.Equal(1, failed.Telemetry.Failed);
        Assert.Equal("F3_JOB_REJECTED", unreadable.Result.ErrorCode);
        Assert.True(handler.Handles(MarketplaceJobTypes.QuestionSync));
        Assert.False(handler.Handles(MarketplaceJobTypes.OrderSync));
    }

    private sealed class StubQuestions(QuestionSyncView answer) : IMarketplaceQuestionService
    {
        public (Guid, Guid, string?)? Called { get; private set; }
        public Task<QuestionSyncView> SyncConnectionAsync(Guid tenantId, Guid connectionId, string? kind, CancellationToken cancellationToken)
        {
            Called = (tenantId, connectionId, kind);
            return Task.FromResult(answer);
        }
        public Task<MarketplaceQuestionListPage> ListAsync(Guid tenantId, QuestionListQuery query, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<MarketplaceQuestionView?> GetAsync(Guid tenantId, Guid id, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<QuestionSyncView> SyncAsync(Guid tenantId, string? kind, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<MarketplaceQuestionSyncStateView>> SyncStatesAsync(Guid tenantId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ServiceResult<MarketplaceQuestionView>> AnswerAsync(Guid tenantId, Guid userId, Guid id, long expectedVersion, string answer, string idempotencyKey, string correlationId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<MarketplaceQuestionTemplateView>> TemplatesAsync(Guid tenantId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ServiceResult<MarketplaceQuestionTemplateView>> SaveTemplateAsync(Guid tenantId, Guid? id, long? expectedVersion, SaveQuestionTemplateCommand command, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ServiceResult<bool>> DeleteTemplateAsync(Guid tenantId, Guid id, long expectedVersion, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
