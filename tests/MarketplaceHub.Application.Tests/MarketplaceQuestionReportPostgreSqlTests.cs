using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Adapters.Hepsiburada;
using MarketplaceHub.Infrastructure.Adapters.Trendyol;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// "Soruyu bildir": a question is reported to the marketplace by that marketplace's rules.
[Collection("postgres-integration")]
public sealed class MarketplaceQuestionReportPostgreSqlTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    [PostgreSqlFact]
    public async Task HepsiburadaWaitingQuestion_IsReportedWithTheChosenReasonAndClosesAsRejected()
    {
        await using var scope = await SeedAsync("HEPSIBURADA", "WAITING_FOR_ANSWER");
        var port = new ReportTestPort(Remote("WAITING_FOR_ANSWER"));
        var service = scope.Service(port);

        var result = await service.ReportAsync(scope.TenantId, scope.UserId, scope.Question.Id, new(scope.Question.Version, "ADVERTISEMENT", null), "question-report:one", "report-1", CancellationToken.None);

        Assert.True(result.Succeeded, result.Error?.Message);
        Assert.Equal("REJECTED", result.Value!.Status);
        Assert.Equal("REJECTED", scope.Question.Status);
        var sent = Assert.Single(port.Reports);
        Assert.Equal("ADVERTISEMENT", sent.ReasonCode);
        Assert.Equal("Reklam", sent.ReasonText);
        var audit = await scope.Db.AuditLogs.Where(row => row.TenantId == scope.TenantId && row.TargetId == scope.Question.Id.ToString("D")).Select(row => row.Action).ToListAsync();
        Assert.Contains("QUESTION_REPORT_STARTED", audit);
        Assert.Contains("QUESTION_REPORTED", audit);
    }

    [PostgreSqlFact]
    public async Task SameIdempotencyKey_ReturnsTheReportedQuestionWithoutASecondReport()
    {
        await using var scope = await SeedAsync("HEPSIBURADA", "WAITING_FOR_ANSWER");
        var port = new ReportTestPort(Remote("WAITING_FOR_ANSWER"));
        var service = scope.Service(port);
        var startVersion = scope.Question.Version;

        var first = await service.ReportAsync(scope.TenantId, scope.UserId, scope.Question.Id, new(startVersion, "INSULT", null), "question-report:same", "report-1", CancellationToken.None);
        // The retry carries the version the first try started from.
        var second = await service.ReportAsync(scope.TenantId, scope.UserId, scope.Question.Id, new(startVersion, "INSULT", null), "question-report:same", "report-2", CancellationToken.None);

        Assert.True(first.Succeeded);
        Assert.True(second.Succeeded);
        Assert.Equal(first.Value!.Version, second.Value!.Version);
        Assert.Single(port.Reports);
    }

    [PostgreSqlFact]
    public async Task StaleVersion_IsRefusedBeforeTheMarketplaceIsCalled()
    {
        await using var scope = await SeedAsync("HEPSIBURADA", "WAITING_FOR_ANSWER");
        var port = new ReportTestPort(Remote("WAITING_FOR_ANSWER"));

        var result = await scope.Service(port).ReportAsync(scope.TenantId, scope.UserId, scope.Question.Id, new(scope.Question.Version - 1, "ADVERTISEMENT", null), "question-report:stale", "report-stale", CancellationToken.None);

        Assert.Equal("QUESTION_STALE", result.Error?.Code);
        Assert.Equal(412, result.Error?.Status);
        Assert.Equal(0, port.DetailCalls);
        Assert.Empty(port.Reports);
    }

    [PostgreSqlFact]
    public async Task OnlyAnOpenWaitingQuestionCanBeReported()
    {
        foreach (var (status, expired) in new[] { ("ANSWERED", false), ("REJECTED", false), ("EXPIRED", false), ("WAITING_FOR_ANSWER", true) })
        {
            await using var scope = await SeedAsync("HEPSIBURADA", status, expired ? fixture.Now.AddMinutes(-1) : fixture.Now.AddHours(5));
            var port = new ReportTestPort(Remote(status));

            var result = await scope.Service(port).ReportAsync(scope.TenantId, scope.UserId, scope.Question.Id, new(scope.Question.Version, "ADVERTISEMENT", null), $"question-report:{status}", "report-closed", CancellationToken.None);

            Assert.Equal("QUESTION_REPORT_NOT_ALLOWED", result.Error?.Code);
            Assert.Empty(port.Reports);
        }
    }

    [PostgreSqlFact]
    public async Task ReasonsFollowThePlatformList_AndOtherNeedsANote()
    {
        await using var scope = await SeedAsync("HEPSIBURADA", "WAITING_FOR_ANSWER");
        var port = new ReportTestPort(Remote("WAITING_FOR_ANSWER"));
        var service = scope.Service(port);

        var unknown = await service.ReportAsync(scope.TenantId, scope.UserId, scope.Question.Id, new(scope.Question.Version, "SPAM_UNKNOWN", null), "question-report:unknown", "r", CancellationToken.None);
        var missingNote = await service.ReportAsync(scope.TenantId, scope.UserId, scope.Question.Id, new(scope.Question.Version, "OTHER", " kısa "), "question-report:note", "r", CancellationToken.None);
        var withNote = await service.ReportAsync(scope.TenantId, scope.UserId, scope.Question.Id, new(scope.Question.Version, "OTHER", "Rakip mağaza yönlendirmesi"), "question-report:note-ok", "r", CancellationToken.None);

        Assert.Equal("QUESTION_REPORT_REASON_INVALID", unknown.Error?.Code);
        Assert.Equal("QUESTION_REPORT_NOTE_REQUIRED", missingNote.Error?.Code);
        Assert.True(withNote.Succeeded, withNote.Error?.Message);
        Assert.Equal("Rakip mağaza yönlendirmesi", Assert.Single(port.Reports).Note);
    }

    [PostgreSqlFact]
    public async Task UnsupportedPlatform_IsRefusedWithItsReason()
    {
        await using var scope = await SeedAsync("TRENDYOL", "WAITING_FOR_ANSWER");
        var port = new ReportTestPort(Remote("WAITING_FOR_ANSWER"));

        var result = await scope.Service(port).ReportAsync(scope.TenantId, scope.UserId, scope.Question.Id, new(scope.Question.Version, "ADVERTISEMENT", null), "question-report:ty", "report-ty", CancellationToken.None);

        Assert.Equal("QUESTION_REPORT_UNSUPPORTED", result.Error?.Code);
        Assert.Contains("Satıcı Paneli", result.Error?.Message);
        Assert.Equal(0, port.DetailCalls);
    }

    [PostgreSqlFact]
    public async Task QuestionAnsweredOnTheMarketplaceMeanwhile_IsNotReportedAndTakesItsState()
    {
        await using var scope = await SeedAsync("HEPSIBURADA", "WAITING_FOR_ANSWER");
        var port = new ReportTestPort(Remote("ANSWERED"));

        var result = await scope.Service(port).ReportAsync(scope.TenantId, scope.UserId, scope.Question.Id, new(scope.Question.Version, "ADVERTISEMENT", null), "question-report:answered", "report-answered", CancellationToken.None);

        Assert.Equal("QUESTION_ALREADY_CLOSED", result.Error?.Code);
        Assert.Equal("ANSWERED", scope.Question.Status);
        Assert.Empty(port.Reports);
    }

    [PostgreSqlFact]
    public async Task ExternalWritesSwitchedOff_LeavesTheQuestionWaiting()
    {
        await using var scope = await SeedAsync("HEPSIBURADA", "WAITING_FOR_ANSWER");
        var port = new ReportTestPort(Remote("WAITING_FOR_ANSWER"))
        {
            Outcome = AdapterResult<RemoteQuestionReportResult>.Failure(new(AdapterErrorClass.Validation, "EXTERNAL_WRITES_DISABLED", "Bu bağlantıda veya ortamda pazaryerine soru bildirme kapalı.", 403, null, null))
        };

        var result = await scope.Service(port).ReportAsync(scope.TenantId, scope.UserId, scope.Question.Id, new(scope.Question.Version, "ADVERTISEMENT", null), "question-report:off", "report-off", CancellationToken.None);

        Assert.Equal("EXTERNAL_WRITES_DISABLED", result.Error?.Code);
        Assert.Equal("WAITING_FOR_ANSWER", scope.Question.Status);
        Assert.Contains("QUESTION_REPORT_FAILED", await scope.Db.AuditLogs.Where(row => row.TenantId == scope.TenantId).Select(row => row.Action).ToListAsync());
    }

    private async Task<Scope> SeedAsync(string platformCode, string status, DateTimeOffset? expiresAt = null)
    {
        var tenant = new Tenant { Id = Guid.CreateVersion7(), Code = $"question-report-{Guid.NewGuid():N}", DisplayName = "question-report", Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow, Version = 1 };
        var connection = new PlatformConnection { Id = Guid.CreateVersion7(), TenantId = tenant.Id, PublicId = Guid.CreateVersion7(), PlatformCode = platformCode, Environment = "PRODUCTION", DisplayName = "Bildirim test mağazası", ExternalStoreId = "merchant-17", Status = "ACTIVE", ApiVersion = "V1" };
        var question = new MarketplaceQuestion
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenant.Id,
            ConnectionId = connection.Id,
            ExternalQuestionId = "external-question",
            Kind = "PRODUCT",
            Status = status,
            QuestionText = "E-ticaret başarını bir üst seviyeye taşı",
            CreatedAt = fixture.Now.AddHours(-1),
            ExpiresAt = expiresAt ?? fixture.Now.AddHours(5),
            LastRemoteModifiedAt = fixture.Now.AddHours(-1),
            LastSyncedAt = fixture.Now.AddMinutes(-1),
            Version = 3
        };
        var db = fixture.CreateContext();
        var transaction = await db.Database.BeginTransactionAsync();
        db.Tenants.Add(tenant);
        db.PlatformConnections.Add(connection);
        db.MarketplaceQuestions.Add(question);
        await db.SaveChangesAsync();
        return new(db, transaction, tenant.Id, Guid.CreateVersion7(), question, fixture.TimeProvider);
    }

    private RemoteMarketplaceQuestion Remote(string status) =>
        new("external-question", "PRODUCT", status, "E-ticaret başarını bir üst seviyeye taşı", null, null, null, null, null, "Müşteri", null, fixture.Now.AddHours(-1), fixture.Now.AddHours(5), fixture.Now.AddHours(-1), []);

    private sealed record Scope(AppDbContext Db, Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction Transaction, Guid TenantId, Guid UserId, MarketplaceQuestion Question, TimeProvider Clock) : IAsyncDisposable
    {
        public MarketplaceQuestionService Service(IQuestionPort port) => new(Db, port, Clock, NullLogger<MarketplaceQuestionService>.Instance);

        public async ValueTask DisposeAsync()
        {
            await Transaction.DisposeAsync();
            await Db.DisposeAsync();
        }
    }

    private sealed class ReportTestPort(RemoteMarketplaceQuestion remote) : IQuestionPort
    {
        public int DetailCalls { get; private set; }
        public List<RemoteQuestionReport> Reports { get; } = [];
        public AdapterResult<RemoteQuestionReportResult> Outcome { get; init; } = AdapterResult<RemoteQuestionReportResult>.Success(new(true));

        public Task<AdapterResult<MarketplaceQuestionPage>> ListQuestionsAsync(AdapterContext context, QuestionPollRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<AdapterResult<RemoteMarketplaceQuestion>> GetQuestionAsync(AdapterContext context, string questionId, string kind, CancellationToken cancellationToken)
        {
            DetailCalls++;
            return Task.FromResult(AdapterResult<RemoteMarketplaceQuestion>.Success(remote));
        }

        public Task<AdapterResult<RemoteQuestionAnswerResult>> AnswerQuestionAsync(AdapterContext context, string questionId, string kind, string answer, CancellationToken cancellationToken) => throw new NotSupportedException();

        public QuestionReportCapability ReportCapability(string platformCode) =>
            platformCode == "HEPSIBURADA" ? HepsiburadaQuestionRequestContract.ReportCapability : TrendyolQuestionRequestContract.ReportCapability;

        public Task<AdapterResult<RemoteQuestionReportResult>> ReportQuestionAsync(AdapterContext context, string questionId, string kind, RemoteQuestionReport report, CancellationToken cancellationToken)
        {
            Reports.Add(report);
            return Task.FromResult(Outcome);
        }
    }
}
