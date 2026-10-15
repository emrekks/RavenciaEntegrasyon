using System.Globalization;
using System.Net;
using System.Text.Json;
using MarketplaceHub.Application;

namespace MarketplaceHub.Infrastructure.Adapters.Pazarama;

// Customer product questions (phase 8). POST QuestionAnswer/getApprovalAnswersByMerchantSearch lists
// questions with their answers by status and date (pageIndex starts at 1), GET getApprovalAnswerById reads one
// and PUT QuestionAnswer/sellerAnswer answers it. An answer does not go live at once: Pazarama puts it in
// approval (status 2) and may reject it (status 3), so a saved answer is accepted but not verified.
public sealed partial class PazaramaHttpClient : IQuestionPort
{
    internal const string QuestionSearch = "QuestionAnswer/getApprovalAnswersByMerchantSearch";
    internal const string QuestionDetail = "QuestionAnswer/getApprovalAnswerById";
    internal const string QuestionAnswer = "QuestionAnswer/sellerAnswer";

    public async Task<AdapterResult<MarketplaceQuestionPage>> ListQuestionsAsync(AdapterContext context, QuestionPollRequest request, CancellationToken cancellationToken)
    {
        var account = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken);
        if (account is null) return CredentialMissing<MarketplaceQuestionPage>();
        var items = new List<RemoteMarketplaceQuestion>();
        var totalPages = 0;
        long total = 0;
        RateLimitMetadata? rate = null;
        // The panel's "answered" covers answers that are live (1) and answers waiting for approval (2).
        foreach (var status in PazaramaQuestionMapper.PazaramaStatuses(request.Status))
        {
            var result = await SendAsync(account, HttpMethod.Post, QuestionSearch, cancellationToken, Json(PazaramaQuestionMapper.SearchBody(request, status)));
            rate = result.RateLimit ?? rate;
            if (!result.IsSuccess) return AdapterResult<MarketplaceQuestionPage>.Failure(result.Error!, result.RateLimit);
            using var document = result.Value!;
            try
            {
                var page = PazaramaQuestionMapper.Page(document.RootElement, request.Page);
                items.AddRange(page.Items);
                totalPages = Math.Max(totalPages, page.TotalPages);
                total += page.TotalElements;
            }
            catch (JsonException)
            {
                return Failure<MarketplaceQuestionPage>(AdapterErrorClass.ContractViolation, "PAZARAMA_QUESTION_CONTRACT_INVALID", $"Pazarama soru yanıtı beklenen yapıda değil (alanlar: {ShapeOf(document.RootElement)}).", HttpStatusCode.BadGateway);
            }
        }
        return AdapterResult<MarketplaceQuestionPage>.Success(new(items, request.Page, totalPages, total), rate);
    }

    public async Task<AdapterResult<RemoteMarketplaceQuestion>> GetQuestionAsync(AdapterContext context, string questionId, string kind, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(questionId, out var id))
            return Failure<RemoteMarketplaceQuestion>(AdapterErrorClass.Validation, "PAZARAMA_QUESTION_ID_INVALID", "Pazarama soru numarası geçersiz.", HttpStatusCode.UnprocessableEntity);
        var account = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken);
        if (account is null) return CredentialMissing<RemoteMarketplaceQuestion>();
        var result = await SendAsync(account, HttpMethod.Get, $"{QuestionDetail}?questionId={id}", cancellationToken);
        if (!result.IsSuccess) return AdapterResult<RemoteMarketplaceQuestion>.Failure(result.Error!, result.RateLimit);
        using var document = result.Value!;
        var question = Data(document.RootElement, out var data) ? PazaramaQuestionMapper.Detail(data) : null;
        return question is null
            ? Failure<RemoteMarketplaceQuestion>(AdapterErrorClass.NotFound, "PAZARAMA_QUESTION_NOT_FOUND", "Pazarama soruyu bulamadı.", HttpStatusCode.NotFound)
            : AdapterResult<RemoteMarketplaceQuestion>.Success(question, result.RateLimit);
    }

    // Accepted, never verified: the answer is only live once Pazarama approves it, which the next list shows.
    public async Task<AdapterResult<RemoteQuestionAnswerResult>> AnswerQuestionAsync(AdapterContext context, string questionId, string kind, string answer, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(questionId, out var id))
            return Failure<RemoteQuestionAnswerResult>(AdapterErrorClass.Validation, "PAZARAMA_QUESTION_ID_INVALID", "Pazarama soru numarası geçersiz.", HttpStatusCode.UnprocessableEntity);
        if (string.IsNullOrWhiteSpace(answer))
            return Failure<RemoteQuestionAnswerResult>(AdapterErrorClass.Validation, "PAZARAMA_ANSWER_EMPTY", "Cevap metni boş olamaz.", HttpStatusCode.UnprocessableEntity);
        var account = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken);
        if (account is null) return CredentialMissing<RemoteQuestionAnswerResult>();
        if (WriteBlocked<RemoteQuestionAnswerResult>(account, "soru cevabı") is { } blocked) return blocked;
        var saved = await SendAsync(account, HttpMethod.Put, QuestionAnswer, cancellationToken, Json(new { questionId = id.ToString(), text = answer.Trim() }));
        if (!saved.IsSuccess) return AdapterResult<RemoteQuestionAnswerResult>.Failure(saved.Error!, saved.RateLimit);
        saved.Value!.Dispose();
        return AdapterResult<RemoteQuestionAnswerResult>.Success(new(true, false, null), saved.RateLimit);
    }
}

public static class PazaramaQuestionMapper
{
    // Panel states to Pazarama question states: 0 waiting for an answer, 1 answered, 2 answer waiting for
    // approval, 3 rejected.
    public static IReadOnlyList<int> PazaramaStatuses(string? panelStatus) => panelStatus?.Trim().ToUpperInvariant() switch
    {
        "WAITING_FOR_ANSWER" => [0],
        "ANSWERED" => [1, 2],
        "REJECTED" => [3],
        _ => [0, 1, 2, 3]
    };

    public static string PanelStatus(int? status) => status switch
    {
        0 => "WAITING_FOR_ANSWER",
        1 or 2 => "ANSWERED",
        3 => "REJECTED",
        _ => "WAITING_FOR_ANSWER"
    };

    public static object SearchBody(QuestionPollRequest request, int status) => new
    {
        barcode = (string?)null,
        topicId = (string?)null,
        questionStartDate = request.StartDate is { } start ? Iso(start) : null,
        questionEndDate = request.EndDate is { } end ? Iso(end) : null,
        questionStatus = status,
        pageIndex = Math.Max(1, request.Page),
        pageSize = Math.Clamp(request.Size, 1, 100)
    };

    // Turkey time, as Pazarama shows it.
    private static string Iso(DateTimeOffset value) =>
        value.ToOffset(PazaramaHttpClient.TurkeyOffset).ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);

    public static MarketplaceQuestionPage Page(JsonElement root, int pageNumber)
    {
        if (root.ValueKind != JsonValueKind.Object) throw new JsonException("Pazarama question response is not an object.");
        var items = new List<RemoteMarketplaceQuestion>();
        // No questions can come back as a null or missing data, an empty object or a bare list.
        if (!root.TryGetProperty("data", out var data) || data.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return new(items, pageNumber, 0, 0);
        JsonElement rows = default;
        if (data.ValueKind == JsonValueKind.Array) rows = data;
        else if (data.ValueKind == JsonValueKind.Object)
            foreach (var name in new[] { "approvalAnswersByMerchantSearchs", "approvalAnswersByMerchantSearch", "approvalAnswersByMerchant", "items", "data" })
                if (data.TryGetProperty(name, out var list) && list.ValueKind == JsonValueKind.Array) { rows = list; break; }
        if (data.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array)) throw new JsonException("Pazarama question data is neither an object nor a list.");
        if (rows.ValueKind == JsonValueKind.Array)
            foreach (var row in rows.EnumerateArray())
                if (row.ValueKind == JsonValueKind.Object && Question(row) is { } question) items.Add(question);
        var paging = data.ValueKind == JsonValueKind.Object && (data.TryGetProperty("pageResponse", out var page) || data.TryGetProperty("pageResult", out page)) && page.ValueKind == JsonValueKind.Object ? page : default;
        var totalPages = (int)(Number(paging, "totalPages") ?? 0);
        var total = Number(paging, "totalCount") ?? items.Count;
        return new(items, pageNumber, totalPages, total);
    }

    public static RemoteMarketplaceQuestion? Question(JsonElement row)
    {
        var id = Text(row, "questionId");
        var text = Text(row, "question");
        if (id is null || text is null) return null;
        var status = (int?)Number(row, "questionStatus");
        var asked = Instant(Text(row, "questionDate")) ?? DateTimeOffset.UnixEpoch;
        var answer = Text(row, "answer");
        var answered = Instant(Text(row, "answerDate"));
        var conversations = new List<RemoteQuestionConversation>();
        if (answer is not null)
            conversations.Add(new("Merchant", answer, answered ?? asked, status == 3 ? "Cevap Pazarama tarafından reddedildi." : null));
        return new RemoteMarketplaceQuestion(
            id, KindOf(Text(row, "orderNumber")), PanelStatus(status), text,
            Text(row, "productName"), Text(row, "productImageUrl"),
            null, Text(row, "barcode"), null,
            Text(row, "maskedUserName"), Text(row, "orderNumber"),
            asked, null, answered ?? asked, conversations);
    }

    // The detail has no answer text; it adds the order number and the topic.
    public static RemoteMarketplaceQuestion? Detail(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object) return null;
        var id = Text(data, "questionId");
        var text = Text(data, "question");
        if (id is null || text is null) return null;
        var topic = Text(data, "topicName");
        var asked = Instant(Text(data, "questionDate")) ?? DateTimeOffset.UnixEpoch;
        return new RemoteMarketplaceQuestion(
            id, KindOf(Text(data, "orderNumber")), PanelStatus((int?)Number(data, "questionStatus")),
            topic is null ? text : $"{topic}: {text}",
            Text(data, "productName"), Text(data, "productImageUrl"),
            null, Text(data, "barcode"), null,
            Text(data, "maskedUserName"), Text(data, "orderNumber"),
            asked, null, asked, []);
    }

    // A question that names an order is an order question; the rest are about a product.
    private static string KindOf(string? orderNumber) => orderNumber is null or "0" ? "PRODUCT" : "ORDER";

    // "2023-12-28T09:12:16.333": Turkey time without an offset, stored as UTC.
    public static DateTimeOffset? Instant(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var text = value.Trim();
        if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var withOffset) && System.Text.RegularExpressions.Regex.IsMatch(text, @"(Z|[+-]\d\d:?\d\d)$"))
            return withOffset.ToUniversalTime();
        return DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var local)
            ? new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), TimeSpan.FromHours(3)).ToUniversalTime()
            : null;
    }

    private static string? Text(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var value)) return null;
        var text = value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null
        };
        return string.IsNullOrWhiteSpace(text) || text.Trim() == "null" ? null : text.Trim();
    }

    private static long? Number(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : null;
}
