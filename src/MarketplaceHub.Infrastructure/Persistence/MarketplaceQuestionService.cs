using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MarketplaceHub.Infrastructure.Persistence;

public sealed class MarketplaceQuestionService(AppDbContext db, IQuestionPort port, TimeProvider timeProvider, ILogger<MarketplaceQuestionService> logger) : IMarketplaceQuestionService
{
    private static readonly string[] TrendyolStatuses = ["WAITING_FOR_ANSWER", "ANSWERED", "REPORTED", "REJECTED", "UNANSWERED"];
    private static readonly string[] HepsiburadaStatuses = ["WAITING_FOR_ANSWER", "ANSWERED", "REJECTED", "EXPIRED"];
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public async Task<MarketplaceQuestionListPage> ListAsync(Guid tenantId, QuestionListQuery query, CancellationToken cancellationToken)
    {
        var rows = from question in db.MarketplaceQuestions.AsNoTracking()
                   join connection in db.PlatformConnections.AsNoTracking() on new { question.TenantId, Id = question.ConnectionId } equals new { connection.TenantId, connection.Id }
                   where question.TenantId == tenantId
                   select new { Question = question, Connection = connection };
        var kind = query.Kind.Trim().ToUpperInvariant();
        if (kind is "PRODUCT" or "ORDER") rows = rows.Where(row => row.Question.Kind == kind);
        if (!string.IsNullOrWhiteSpace(query.Status) && !query.Status.Equals("ALL", StringComparison.OrdinalIgnoreCase))
        {
            var statuses = query.Status.Trim().ToUpperInvariant() switch
            {
                "WAITING" => new[] { "WAITING_FOR_ANSWER", "ANSWER_SUBMITTED" },
                "ANSWERED" => new[] { "ANSWERED" },
                "EXPIRED" => new[] { "EXPIRED", "UNANSWERED", "AUTOCLOSED" },
                "OTHER" => new[] { "REPORTED", "REJECTED" },
                _ => [query.Status.Trim().ToUpperInvariant()]
            };
            rows = rows.Where(row => statuses.Contains(row.Question.Status));
        }
        if (!string.IsNullOrWhiteSpace(query.PlatformCode)) rows = rows.Where(row => row.Connection.PlatformCode == query.PlatformCode.Trim().ToUpperInvariant());
        if (query.ConnectionId is { } connectionId) rows = rows.Where(row => row.Connection.Id == connectionId);
        if (query.DateFrom is { } from) rows = rows.Where(row => row.Question.CreatedAt >= from);
        if (query.DateTo is { } to) rows = rows.Where(row => row.Question.CreatedAt <= to);
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            rows = rows.Where(row => row.Question.QuestionText.Contains(term) || (row.Question.ProductName != null && row.Question.ProductName.Contains(term)) || (row.Question.ProductSku != null && row.Question.ProductSku.Contains(term)) || (row.Question.ExternalOrderNumber != null && row.Question.ExternalOrderNumber.Contains(term)) || row.Question.ExternalQuestionId.Contains(term));
        }
        var totalCount = await rows.CountAsync(cancellationToken);
        var page = Math.Max(1, query.Page); var limit = Math.Clamp(query.Limit, 1, 100);
        var orderedRows = query.Sort.Trim().ToUpperInvariant() switch
        {
            "OLDEST" => rows.OrderBy(row => row.Question.CreatedAt).ThenBy(row => row.Question.Id),
            "UPDATED" => rows.OrderByDescending(row => row.Question.LastRemoteModifiedAt).ThenByDescending(row => row.Question.CreatedAt),
            _ => rows.OrderByDescending(row => row.Question.CreatedAt).ThenByDescending(row => row.Question.Id)
        };
        var items = await orderedRows
            .Skip((page - 1) * limit).Take(limit).Select(row => Map(row.Question, row.Connection.PlatformCode, row.Connection.DisplayName)).ToListAsync(cancellationToken);
        return new(items, page, limit, totalCount);
    }

    public async Task<MarketplaceQuestionView?> GetAsync(Guid tenantId, Guid id, CancellationToken cancellationToken)
    {
        return await (from question in db.MarketplaceQuestions.AsNoTracking()
                      join connection in db.PlatformConnections.AsNoTracking() on new { question.TenantId, Id = question.ConnectionId } equals new { connection.TenantId, connection.Id }
                      where question.TenantId == tenantId && question.Id == id
                      select Map(question, connection.PlatformCode, connection.DisplayName)).SingleOrDefaultAsync(cancellationToken);
    }

    public async Task<QuestionSyncView> SyncAsync(Guid tenantId, string? kind, CancellationToken cancellationToken)
    {
        var connections = await db.PlatformConnections.AsNoTracking().Where(connection => connection.TenantId == tenantId
            && (connection.Status == "ACTIVE" || connection.Status == "VERIFIED")
            && (connection.PlatformCode == "TRENDYOL" || connection.PlatformCode == "HEPSIBURADA"))
            .Select(connection => connection.Id).ToListAsync(cancellationToken);
        var added = 0; var updated = 0; string? error = null;
        foreach (var id in connections)
        {
            var result = await SyncConnectionAsync(tenantId, id, kind, cancellationToken);
            added += result.Added; updated += result.Updated;
            if (result.Error is not null) error = result.Error;
        }
        return new(added, updated, timeProvider.GetUtcNow(), error);
    }

    public async Task<QuestionSyncView> SyncConnectionAsync(Guid tenantId, Guid connectionId, string? kind, CancellationToken cancellationToken)
    {
        var connection = await db.PlatformConnections.AsNoTracking().SingleOrDefaultAsync(row => row.TenantId == tenantId && row.Id == connectionId
            && (row.PlatformCode == "TRENDYOL" || row.PlatformCode == "HEPSIBURADA"), cancellationToken);
        if (connection is null) return new(0, 0, timeProvider.GetUtcNow(), "Pazaryeri bağlantısı bulunamadı.");
        var state = await db.MarketplaceQuestionSyncStates.SingleOrDefaultAsync(row => row.TenantId == tenantId && row.ConnectionId == connectionId, cancellationToken);
        if (state is null) { state = new() { TenantId = tenantId, ConnectionId = connectionId, HistoryStartedAt = timeProvider.GetUtcNow().AddYears(-1) }; db.MarketplaceQuestionSyncStates.Add(state); await db.SaveChangesAsync(cancellationToken); }
        state.HistoryStartedAt ??= timeProvider.GetUtcNow().AddYears(-1);
        var initial = !state.HistoryImported;
        var now = timeProvider.GetUtcNow(); var added = 0; var updated = 0;
        string? failedRequestContext = null;
        state.ProgressStatus = initial ? "INITIAL" : "UPDATING"; state.LastRunStartedAt = now; state.Version++;
        await db.SaveChangesAsync(cancellationToken);
        try
        {
            var kinds = connection.PlatformCode == "TRENDYOL" ? new[] { "PRODUCT" } : new[] { "PRODUCT", "ORDER" };
            var statuses = connection.PlatformCode == "TRENDYOL" ? TrendyolStatuses : HepsiburadaStatuses;
            var liveStart = connection.PlatformCode == "TRENDYOL" ? now.AddDays(-14) : now.AddDays(-7);
            var pageBase = connection.PlatformCode == "TRENDYOL" ? 0 : 1;
            var pageSize = connection.PlatformCode == "TRENDYOL" ? 50 : 25;
            updated += await ReconcilePendingAnswerSubmissionsAsync(tenantId, connectionId, now, cancellationToken);
            if (initial)
            {
                foreach (var questionKind in kinds)
                    foreach (var status in statuses)
                    {
                        var liveRequest = new QuestionPollRequest(questionKind, status, liveStart, now, pageBase, pageSize);
                        await FetchAndSavePageAsync(liveRequest, "live:initial", countForHistory: false);
                    }
            }
            else
            {
                // Reuse the durable sync-state cursor after the one-time history
                // import. Live polling is bounded per lease and resumes at the
                // next page/status/kind on the next scheduled job.
                if (state.HistoryStatusIndex >= statuses.Length)
                {
                    state.HistoryStatusIndex = 0;
                    state.HistoryKindIndex = 0;
                    state.HistoryPageIndex = pageBase;
                }
                MarketplaceQuestionLiveCursor? cursor = new(
                    Math.Clamp(state.HistoryStatusIndex, 0, statuses.Length - 1),
                    Math.Clamp(state.HistoryKindIndex, 0, kinds.Length - 1),
                    Math.Max(pageBase, state.HistoryPageIndex));
                for (var batch = 0; batch < 6 && cursor is not null; batch++)
                {
                    var request = new QuestionPollRequest(kinds[cursor.KindIndex], statuses[cursor.StatusIndex], liveStart, now, cursor.PageIndex, pageSize);
                    var result = await FetchAndSavePageAsync(request, $"live:{cursor.StatusIndex}:{cursor.KindIndex}:{cursor.PageIndex}", countForHistory: false);
                    cursor = MarketplaceQuestionLiveCursorPolicy.Advance(cursor, result, pageSize, pageBase, statuses.Length, kinds.Length);
                    state.HistoryStatusIndex = cursor?.StatusIndex ?? statuses.Length;
                    state.HistoryKindIndex = cursor?.KindIndex ?? 0;
                    state.HistoryPageIndex = cursor?.PageIndex ?? pageBase;
                    state.Version++;
                    await db.SaveChangesAsync(cancellationToken);
                }
            }

            // Long history imports run in bounded pages so a large merchant history never monopolizes one worker lease.
            // Waiting questions are imported first; subsequent passes continue from the persisted status/kind/window/page cursor.
            for (var batch = 0; initial && batch < 3 && !state.HistoryImported; batch++)
            {
                var statusIndex = Math.Clamp(state.HistoryStatusIndex, 0, statuses.Length);
                if (statusIndex >= statuses.Length) { state.HistoryImported = true; break; }
                var kindIndex = Math.Clamp(state.HistoryKindIndex, 0, kinds.Length - 1);
                var questionKind = kinds[kindIndex];
                var historyStart = state.HistoryStartedAt!.Value;
                var historyWindows = connection.PlatformCode == "TRENDYOL"
                    ? WindowsOldestFirst(historyStart, now, TimeSpan.FromDays(14)).Select(historyWindow => ((DateTimeOffset?)historyWindow.Start, (DateTimeOffset?)historyWindow.End)).ToArray()
                    : new[] { ((DateTimeOffset?)historyStart, (DateTimeOffset?)now) };
                if (historyWindows.Length == 0) { state.HistoryImported = true; break; }
                var windowIndex = Math.Clamp(state.HistoryWindowIndex, 0, historyWindows.Length - 1);
                var window = historyWindows[windowIndex];
                var page = connection.PlatformCode == "TRENDYOL" ? Math.Max(0, state.HistoryPageIndex) : Math.Max(1, state.HistoryPageIndex);
                var request = new QuestionPollRequest(questionKind, statuses[statusIndex], window.Item1, window.Item2, page, connection.PlatformCode == "TRENDYOL" ? 50 : 25);
                var result = await FetchAndSavePageAsync(request, $"history:{statusIndex}:{kindIndex}:{windowIndex}:{page}", countForHistory: true);
                var lastPage = result.Items.Count == 0 || result.Items.Count < request.Size || page >= result.TotalPages;
                if (lastPage)
                {
                    state.HistoryPageIndex = connection.PlatformCode == "TRENDYOL" ? 0 : 1;
                    state.HistoryWindowIndex++;
                    if (state.HistoryWindowIndex >= historyWindows.Length)
                    {
                        state.HistoryWindowIndex = 0;
                        state.HistoryKindIndex++;
                        if (state.HistoryKindIndex >= kinds.Length) { state.HistoryKindIndex = 0; state.HistoryStatusIndex++; }
                    }
                }
                else state.HistoryPageIndex = page + 1;
                if (state.HistoryStatusIndex >= statuses.Length) state.HistoryImported = true;
                state.Version++;
                await db.SaveChangesAsync(cancellationToken);
            }

            state.ProgressStatus = state.HistoryImported ? "IDLE" : "INITIAL"; state.LastSuccessAt = now; state.LastError = null; state.Version++;
            await db.SaveChangesAsync(cancellationToken);
            return new(added, updated, now);

            async Task<MarketplaceQuestionPage> FetchAndSavePageAsync(QuestionPollRequest request, string pageKey, bool countForHistory)
            {
                var requestRange = request.StartDate is { } start && request.EndDate is { } end
                    ? $"; aralık {start.ToUnixTimeMilliseconds()}–{end.ToUnixTimeMilliseconds()}"
                    : string.Empty;
                var requestPhase = pageKey == "live" ? "canlı" : $"geçmiş {pageKey}";
                failedRequestContext = $"{requestPhase}; {request.Kind}/{request.Status}; sayfa {request.Page}{requestRange}";
                var context = new AdapterContext(tenantId, connectionId, $"question-sync-{Guid.NewGuid():N}", $"question-sync:{connectionId:N}:{pageKey}", now.AddMinutes(2), Operation: IntegrationOperation.Automatic);
                var response = await port.ListQuestionsAsync(context, request, cancellationToken);
                if (!response.IsSuccess) throw new QuestionSyncException(response.Error!);
                foreach (var remote in response.Value!.Items)
                {
                    var outcome = await UpsertAsync(tenantId, connectionId, remote, now, cancellationToken);
                    if (outcome) added++; else updated++;
                }
                if (countForHistory) state.ImportedCount += response.Value.Items.Count;
                state.Version++;
                await db.SaveChangesAsync(cancellationToken);
                return response.Value;
            }
        }
        catch (QuestionSyncException exception)
        {
            var httpStatus = exception.Error.HttpStatus is { } status ? $"HTTP {status}; " : string.Empty;
            state.LastError = $"{exception.Error.Code} ({httpStatus}{failedRequestContext ?? "istek ayrıntısı yok"})";
            state.ProgressStatus = "FAILED"; state.Version++;
            await db.SaveChangesAsync(cancellationToken);
            logger.LogWarning("Soru eşitleme başarısız. TenantId: {TenantId}, ConnectionId: {ConnectionId}, Code: {Code}, HttpStatus: {HttpStatus}, Request: {Request}, RemoteRequestId: {RemoteRequestId}", tenantId, connectionId, exception.Error.Code, exception.Error.HttpStatus, failedRequestContext, exception.Error.RemoteRequestId);
            return new(added, updated, now, exception.Error.SafeMessage);
        }
    }

    public async Task<ServiceResult<MarketplaceQuestionView>> AnswerAsync(Guid tenantId, Guid userId, Guid id, long expectedVersion, string answer, string idempotencyKey, string correlationId, CancellationToken cancellationToken)
    {
        var question = await db.MarketplaceQuestions.SingleOrDefaultAsync(row => row.TenantId == tenantId && row.Id == id, cancellationToken);
        if (question is null) return ServiceResult<MarketplaceQuestionView>.Fail("QUESTION_NOT_FOUND", "Soru bulunamadı.", 404);
        if (question.Version != expectedVersion) return ServiceResult<MarketplaceQuestionView>.Fail("QUESTION_STALE", "Soru yenilendi. Güncel kaydı açıp tekrar deneyin.", 412);
        var reconcilingPriorSubmission = question.AnswerSubmissionStatus is "SUBMITTING" or "UNKNOWN" or "SUBMITTED";
        if (!reconcilingPriorSubmission && question.Status != "WAITING_FOR_ANSWER" && question.Status != "ANSWER_SUBMITTED") return ServiceResult<MarketplaceQuestionView>.Fail("QUESTION_CLOSED", "Bu soru artık cevap beklemiyor.", 409);
        var connection = await db.PlatformConnections.AsNoTracking().SingleAsync(row => row.TenantId == tenantId && row.Id == question.ConnectionId, cancellationToken);
        if (!reconcilingPriorSubmission)
        {
            var answerLimit = connection.PlatformCode == "TRENDYOL" ? (10, 2000) : (1, 2000);
            if (answer.Trim().Length < answerLimit.Item1 || answer.Trim().Length > answerLimit.Item2)
                return ServiceResult<MarketplaceQuestionView>.Fail("QUESTION_ANSWER_LENGTH", $"Cevap {answerLimit.Item1}–{answerLimit.Item2} karakter arasında olmalıdır.", 422);
        }
        var context = new AdapterContext(tenantId, question.ConnectionId, correlationId, idempotencyKey, timeProvider.GetUtcNow().AddMinutes(2), Operation: IntegrationOperation.Manual);
        var current = await port.GetQuestionAsync(context, question.ExternalQuestionId, question.Kind, cancellationToken);
        if (!current.IsSuccess) return ServiceResult<MarketplaceQuestionView>.Fail(current.Error!.Code, current.Error.SafeMessage, current.Error.HttpStatus ?? 502);
        if (reconcilingPriorSubmission)
        {
            await UpsertAsync(tenantId, question.ConnectionId, current.Value!, timeProvider.GetUtcNow(), cancellationToken);
            ReconcileAnswerSubmission(question, current.Value!);
            if (question.Version == expectedVersion) question.Version++;
            await db.SaveChangesAsync(cancellationToken);

            var reconciled = question.AnswerSubmissionStatus is "SUBMITTED" or "CONFIRMED";
            return ServiceResult<MarketplaceQuestionView>.Fail(
                reconciled ? "QUESTION_ANSWER_RECONCILED" : "QUESTION_ANSWER_RESULT_UNKNOWN",
                reconciled
                    ? "Önceki cevap pazaryerinde bulundu; yeni cevap gönderilmedi."
                    : "Önceki cevap isteğinin sonucu hâlâ belirsiz. Yinelenen cevap oluşmaması için yeni gönderim yapılmadı.",
                409);
        }
        if (!current.Value!.Status.Equals("WAITING_FOR_ANSWER", StringComparison.OrdinalIgnoreCase))
        {
            await UpsertAsync(tenantId, question.ConnectionId, current.Value, timeProvider.GetUtcNow(), cancellationToken); await db.SaveChangesAsync(cancellationToken);
            return ServiceResult<MarketplaceQuestionView>.Fail("QUESTION_ALREADY_CLOSED", "Soru pazaryerinde artık cevap beklemiyor. Güncel durum alındı.", 409);
        }
        question.AnswerSubmissionKey = idempotencyKey; question.PendingAnswerText = answer.Trim(); question.AnswerSubmissionStatus = "SUBMITTING"; question.Version++;
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateConcurrencyException) { return ServiceResult<MarketplaceQuestionView>.Fail("QUESTION_STALE", "Bu soru başka bir işlem tarafından güncellendi.", 409); }
        AdapterResult<RemoteQuestionAnswerResult> sent;
        try
        {
            sent = await port.AnswerQuestionAsync(context, question.ExternalQuestionId, question.Kind, answer, cancellationToken);
        }
        catch (Exception exception) when (exception is OperationCanceledException or HttpRequestException or TimeoutException)
        {
            question.AnswerSubmissionStatus = "UNKNOWN";
            question.Status = "ANSWER_SUBMITTED";
            question.Version++;
            await db.SaveChangesAsync(CancellationToken.None);
            logger.LogWarning(exception, "Soru cevabının uzak sonucu kesinleşmedi. TenantId: {TenantId}, ConnectionId: {ConnectionId}, QuestionId: {QuestionId}", tenantId, question.ConnectionId, question.Id);
            return ServiceResult<MarketplaceQuestionView>.Fail("QUESTION_ANSWER_RESULT_UNKNOWN", "Cevap isteği kesildi; yinelenen cevap oluşmaması için durum doğrulanana kadar yeniden gönderim kapatıldı.", 409);
        }
        if (!sent.IsSuccess)
        {
            var uncertain = sent.Error!.Class is AdapterErrorClass.TransientNetwork or AdapterErrorClass.Remote5xx;
            question.AnswerSubmissionStatus = uncertain ? "UNKNOWN" : "FAILED";
            if (uncertain) question.Status = "ANSWER_SUBMITTED";
            question.Version++;
            await db.SaveChangesAsync(cancellationToken);
            return ServiceResult<MarketplaceQuestionView>.Fail(sent.Error.Code, sent.Error.SafeMessage, sent.Error.HttpStatus ?? 502);
        }
        question.AnswerSubmissionStatus = "SUBMITTED"; question.Status = "ANSWER_SUBMITTED";
        var history = ReadHistory(question.HistoryJson).ToList();
        history.RemoveAll(item => item.Author == "Local draft");
        history.Add(new("Merchant", answer.Trim(), timeProvider.GetUtcNow())); question.HistoryJson = JsonSerializer.Serialize(history);
        question.Version++;
        await db.SaveChangesAsync(cancellationToken);
        return ServiceResult<MarketplaceQuestionView>.Ok(Map(question, connection.PlatformCode, connection.DisplayName));
    }

    private async Task<int> ReconcilePendingAnswerSubmissionsAsync(Guid tenantId, Guid connectionId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        const int batchSize = 10;
        const string resourceType = "QUESTION_ANSWER_RECONCILIATION";
        var cursor = await db.SyncCursors.SingleOrDefaultAsync(row => row.TenantId == tenantId && row.ConnectionId == connectionId && row.ResourceType == resourceType, cancellationToken);
        if (cursor is null)
        {
            cursor = new SyncCursor { Id = Guid.CreateVersion7(), TenantId = tenantId, ConnectionId = connectionId, ResourceType = resourceType, Version = 1 };
            db.SyncCursors.Add(cursor);
        }
        if (cursor.LastErrorAt is { } lastErrorAt && lastErrorAt > now.AddMinutes(-5)) return 0;
        cursor.LastRequestCount = 0;
        cursor.LastReceivedCount = 0;
        cursor.LastUpdatedCount = 0;
        cursor.LastFailedCount = 0;
        cursor.Version++;
        await db.SaveChangesAsync(cancellationToken);

        var candidatesQuery = db.MarketplaceQuestions
            .Where(row => row.TenantId == tenantId && row.ConnectionId == connectionId
                && row.LastSyncedAt <= now.AddMinutes(-5)
                && (row.AnswerSubmissionStatus == "SUBMITTING" || row.AnswerSubmissionStatus == "UNKNOWN"));
        var hasCursor = Guid.TryParse(cursor.OpaqueCursor, out var cursorId);
        var afterCursor = candidatesQuery;
        if (hasCursor) afterCursor = afterCursor.Where(row => row.Id.CompareTo(cursorId) > 0);
        var candidates = await afterCursor.OrderBy(row => row.Id).Take(batchSize).ToListAsync(cancellationToken);
        if (hasCursor && candidates.Count < batchSize)
        {
            var wrapped = await candidatesQuery.Where(row => row.Id.CompareTo(cursorId) <= 0)
                .OrderBy(row => row.Id)
                .Take(batchSize - candidates.Count)
                .ToListAsync(cancellationToken);
            candidates.AddRange(wrapped);
        }

        var updated = 0;
        foreach (var question in candidates)
        {
            cursor.OpaqueCursor = question.Id.ToString("D");
            cursor.LastAttemptAt = now;
            cursor.LastRequestCount++;
            cursor.Version++;
            await db.SaveChangesAsync(cancellationToken);

            var context = new AdapterContext(tenantId, connectionId, $"question-answer-reconcile-{Guid.NewGuid():N}",
                $"question-answer-reconcile:{connectionId:N}:{question.Id:N}", now.AddMinutes(2), Operation: IntegrationOperation.Automatic);
            var result = await port.GetQuestionAsync(context, question.ExternalQuestionId, question.Kind, cancellationToken);
            if (!result.IsSuccess)
            {
                cursor.LastError = result.Error!.Code;
                cursor.LastErrorAt = now;
                cursor.ConsecutiveFailureCount++;
                cursor.LastFailedCount++;
                cursor.Version++;
                await db.SaveChangesAsync(cancellationToken);
                logger.LogWarning("Belirsiz soru cevabının uzak durumu okunamadı. TenantId: {TenantId}, ConnectionId: {ConnectionId}, QuestionId: {QuestionId}, Code: {Code}", tenantId, connectionId, question.Id, result.Error.Code);
                break;
            }

            if (!string.Equals(result.Value!.Id, question.ExternalQuestionId, StringComparison.Ordinal))
            {
                cursor.LastError = "QUESTION_DETAIL_ID_MISMATCH";
                cursor.LastErrorAt = now;
                cursor.ConsecutiveFailureCount++;
                cursor.LastFailedCount++;
                cursor.Version++;
                await db.SaveChangesAsync(cancellationToken);
                logger.LogWarning("Soru detay yanıtındaki kimlik yerel kayıtla eşleşmedi. TenantId: {TenantId}, ConnectionId: {ConnectionId}, QuestionId: {QuestionId}", tenantId, connectionId, question.Id);
                break;
            }

            var previousVersion = question.Version;
            await UpsertAsync(tenantId, connectionId, result.Value, now, cancellationToken);
            if (question.AnswerSubmissionStatus == "SUBMITTING") question.AnswerSubmissionStatus = "UNKNOWN";
            question.LastSyncedAt = now;
            if (question.Version == previousVersion) question.Version++;
            cursor.LastSuccessAt = now;
            cursor.LastReceivedCount++;
            cursor.LastUpdatedCount++;
            cursor.LastError = null;
            cursor.LastErrorAt = null;
            cursor.ConsecutiveFailureCount = 0;
            cursor.Version++;
            await db.SaveChangesAsync(cancellationToken);
            updated++;
        }

        return updated;
    }

    public async Task<IReadOnlyList<MarketplaceQuestionTemplateView>> TemplatesAsync(Guid tenantId, CancellationToken cancellationToken) => await db.MarketplaceQuestionTemplates.AsNoTracking().Where(row => row.TenantId == tenantId).OrderBy(row => row.Title).Select(row => new MarketplaceQuestionTemplateView(row.Id, row.Title, row.Text, row.CreatedAt, row.UpdatedAt, row.Version)).ToListAsync(cancellationToken);

    public async Task<ServiceResult<MarketplaceQuestionTemplateView>> SaveTemplateAsync(Guid tenantId, Guid? id, long? expectedVersion, SaveQuestionTemplateCommand command, CancellationToken cancellationToken)
    {
        var title = command.Title.Trim(); var text = command.Text.Trim();
        if (title.Length is < 1 or > 120 || text.Length is < 1 or > 2000) return ServiceResult<MarketplaceQuestionTemplateView>.Fail("QUESTION_TEMPLATE_INVALID", "Başlık 1–120, cevap metni 1–2000 karakter olmalıdır.", 422);
        var now = timeProvider.GetUtcNow(); MarketplaceQuestionTemplate row;
        if (id is null)
        {
            if (await db.MarketplaceQuestionTemplates.AnyAsync(candidate => candidate.TenantId == tenantId && candidate.Title == title, cancellationToken)) return ServiceResult<MarketplaceQuestionTemplateView>.Fail("QUESTION_TEMPLATE_TITLE_EXISTS", "Bu başlıkta hazır cevap zaten bulunuyor.", 409);
            row = new() { Id = Guid.CreateVersion7(), TenantId = tenantId, Title = title, Text = text, CreatedAt = now, UpdatedAt = now }; db.MarketplaceQuestionTemplates.Add(row);
        }
        else
        {
            row = await db.MarketplaceQuestionTemplates.SingleOrDefaultAsync(candidate => candidate.TenantId == tenantId && candidate.Id == id, cancellationToken) ?? null!;
            if (row is null) return ServiceResult<MarketplaceQuestionTemplateView>.Fail("QUESTION_TEMPLATE_NOT_FOUND", "Hazır cevap bulunamadı.", 404);
            if (expectedVersion != row.Version) return ServiceResult<MarketplaceQuestionTemplateView>.Fail("QUESTION_TEMPLATE_STALE", "Hazır cevap başka bir işlemde değiştirildi.", 412);
            row.Title = title; row.Text = text; row.UpdatedAt = now; row.Version++;
        }
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateException) { return ServiceResult<MarketplaceQuestionTemplateView>.Fail("QUESTION_TEMPLATE_TITLE_EXISTS", "Bu başlıkta hazır cevap zaten bulunuyor.", 409); }
        return ServiceResult<MarketplaceQuestionTemplateView>.Ok(new(row.Id, row.Title, row.Text, row.CreatedAt, row.UpdatedAt, row.Version));
    }

    public async Task<ServiceResult<bool>> DeleteTemplateAsync(Guid tenantId, Guid id, long expectedVersion, CancellationToken cancellationToken)
    {
        var row = await db.MarketplaceQuestionTemplates.SingleOrDefaultAsync(candidate => candidate.TenantId == tenantId && candidate.Id == id, cancellationToken);
        if (row is null) return ServiceResult<bool>.Fail("QUESTION_TEMPLATE_NOT_FOUND", "Hazır cevap bulunamadı.", 404);
        if (row.Version != expectedVersion) return ServiceResult<bool>.Fail("QUESTION_TEMPLATE_STALE", "Hazır cevap başka bir işlemde değiştirildi.", 412);
        db.MarketplaceQuestionTemplates.Remove(row); await db.SaveChangesAsync(cancellationToken); return ServiceResult<bool>.Ok(true);
    }

    public async Task<IReadOnlyList<MarketplaceQuestionSyncStateView>> SyncStatesAsync(Guid tenantId, CancellationToken cancellationToken) =>
        await (from connection in db.PlatformConnections.AsNoTracking()
               join state in db.MarketplaceQuestionSyncStates.AsNoTracking() on new { connection.TenantId, ConnectionId = connection.Id } equals new { state.TenantId, ConnectionId = state.ConnectionId } into states
               from state in states.DefaultIfEmpty()
               where connection.TenantId == tenantId && (connection.Status == "ACTIVE" || connection.Status == "VERIFIED") && (connection.PlatformCode == "TRENDYOL" || connection.PlatformCode == "HEPSIBURADA")
               select new MarketplaceQuestionSyncStateView(connection.Id, connection.PlatformCode, connection.DisplayName, state != null && state.HistoryImported, state == null ? null : state.HistoryStartedAt,
                   state == null ? "QUEUED" : state.ProgressStatus, state == null ? 0 : state.ImportedCount, state == null ? null : state.LastRunStartedAt,
                   state == null ? null : state.LastSuccessAt, state == null ? null : state.LastError)).ToListAsync(cancellationToken);

    private async Task<bool> UpsertAsync(Guid tenantId, Guid connectionId, RemoteMarketplaceQuestion remote, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var row = await db.MarketplaceQuestions.SingleOrDefaultAsync(candidate => candidate.TenantId == tenantId && candidate.ConnectionId == connectionId && candidate.ExternalQuestionId == remote.Id, cancellationToken);
        var isNew = row is null;
        row ??= new MarketplaceQuestion { Id = Guid.CreateVersion7(), TenantId = tenantId, ConnectionId = connectionId, ExternalQuestionId = remote.Id, Kind = remote.Kind, QuestionText = remote.Text, Status = remote.Status, CreatedAt = remote.CreatedAt, LastRemoteModifiedAt = remote.LastModifiedAt };
        if (remote.LastModifiedAt >= row.LastRemoteModifiedAt)
        {
            row.Kind = remote.Kind; row.Status = remote.Status;
            if (remote.Text.Length > 0) row.QuestionText = remote.Text;
            row.ProductName = remote.ProductName; row.ProductImageUrl = remote.ProductImageUrl; row.ProductSku = remote.ProductSku; row.ProductBarcode = remote.ProductBarcode; row.ProductModelCode = remote.ProductModelCode; row.CustomerName = remote.CustomerName; row.ExternalOrderNumber = remote.OrderNumber;
            row.HistoryJson = MergeHistory(row.HistoryJson, remote.Conversations); row.ExpiresAt = remote.ExpiresAt; row.LastRemoteModifiedAt = remote.LastModifiedAt;
            ReconcileAnswerSubmission(row, remote);
            row.LastSyncedAt = now; row.Version++;
        }
        if (isNew) db.MarketplaceQuestions.Add(row);
        return isNew;
    }

    private static void ReconcileAnswerSubmission(MarketplaceQuestion row, RemoteMarketplaceQuestion remote)
    {
        var resolution = MarketplaceQuestionAnswerReconciliationPolicy.Evaluate(remote, row.PendingAnswerText);
        if (resolution == QuestionAnswerSubmissionReconciliation.Confirmed)
        {
            row.AnswerSubmissionStatus = "CONFIRMED";
            row.PendingAnswerText = null;
            row.Status = "ANSWERED";
            return;
        }

        if (resolution == QuestionAnswerSubmissionReconciliation.Submitted
            && row.AnswerSubmissionStatus is "SUBMITTING" or "UNKNOWN" or "SUBMITTED")
        {
            row.AnswerSubmissionStatus = "SUBMITTED";
            row.PendingAnswerText = null;
            row.Status = "ANSWER_SUBMITTED";
            return;
        }

        if (remote.Status.Equals("WAITING_FOR_ANSWER", StringComparison.OrdinalIgnoreCase)
            && row.AnswerSubmissionStatus is "SUBMITTING" or "UNKNOWN" or "SUBMITTED")
            row.Status = "ANSWER_SUBMITTED";
    }

    private static string? MergeHistory(string? existingJson, IReadOnlyList<RemoteQuestionConversation> incoming)
    {
        var history = ReadHistory(existingJson).ToList();
        foreach (var message in incoming)
        {
            if (!history.Any(existing => existing.Author == message.Author && existing.CreatedAt == message.CreatedAt && existing.Text == message.Text)) history.Add(message);
        }
        return history.Count == 0 ? null : JsonSerializer.Serialize(history.OrderBy(message => message.CreatedAt));
    }
    private static IReadOnlyList<RemoteQuestionConversation> ReadHistory(string? json) { try { return json is null ? [] : JsonSerializer.Deserialize<RemoteQuestionConversation[]>(json, JsonOptions) ?? []; } catch (JsonException) { return []; } }
    private static MarketplaceQuestionView Map(MarketplaceQuestion row, string platformCode, string storeName) => new(row.Id, row.ConnectionId, platformCode, storeName, row.ExternalQuestionId, row.Kind, row.Status, row.QuestionText, row.ProductName, row.ProductImageUrl, row.ProductSku, row.ProductBarcode, row.ProductModelCode, row.CustomerName, row.ExternalOrderNumber, ReadHistory(row.HistoryJson), row.CreatedAt, row.ExpiresAt, row.LastRemoteModifiedAt, row.LastSyncedAt, row.Version);
    private static IEnumerable<(DateTimeOffset Start, DateTimeOffset End)> WindowsOldestFirst(DateTimeOffset start, DateTimeOffset end, TimeSpan maximum)
    {
        for (var cursor = start; cursor < end; cursor = cursor.Add(maximum)) yield return (cursor, cursor.Add(maximum) < end ? cursor.Add(maximum) : end);
    }
    private sealed class QuestionSyncException(AdapterError error) : Exception(error.SafeMessage) { public AdapterError Error { get; } = error; }
}
