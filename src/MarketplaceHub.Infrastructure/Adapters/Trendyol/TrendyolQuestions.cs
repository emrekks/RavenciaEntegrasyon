using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Infrastructure.Adapters.Trendyol.ErrorMapping;

namespace MarketplaceHub.Infrastructure.Adapters.Trendyol;

public sealed partial class TrendyolHttpClient : IQuestionPort
{
    private static readonly string[] QuestionStatuses = ["WAITING_FOR_ANSWER", "ANSWERED", "REPORTED", "REJECTED", "UNANSWERED"];

    public async Task<AdapterResult<MarketplaceQuestionPage>> ListQuestionsAsync(AdapterContext context, QuestionPollRequest query, CancellationToken cancellationToken)
    {
        var authorized = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken);
        if (authorized is null) return AdapterResult<MarketplaceQuestionPage>.Failure(TrendyolErrorMapper.Configuration());
        var start = query.StartDate ?? timeProvider.GetUtcNow().AddDays(-7);
        var end = query.EndDate ?? timeProvider.GetUtcNow();
        var response = await SendAsync(authorized, HttpMethod.Get, TrendyolQuestionRequestContract.ListPath(authorized.Connection.ExternalStoreId, query, start, end), null, cancellationToken);
        if (!response.IsSuccess) return AdapterResult<MarketplaceQuestionPage>.Failure(response.Error!, response.RateLimit);
        try { return AdapterResult<MarketplaceQuestionPage>.Success(TrendyolQuestionMapper.Page(response.Value!, "PRODUCT"), response.RateLimit); }
        catch (JsonException) { return AdapterResult<MarketplaceQuestionPage>.Failure(TrendyolErrorMapper.Contract(), response.RateLimit); }
    }

    public async Task<AdapterResult<RemoteMarketplaceQuestion>> GetQuestionAsync(AdapterContext context, string questionId, string kind, CancellationToken cancellationToken)
    {
        var authorized = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken);
        if (authorized is null) return AdapterResult<RemoteMarketplaceQuestion>.Failure(TrendyolErrorMapper.Configuration());
        var response = await SendAsync(authorized, HttpMethod.Get, TrendyolQuestionRequestContract.DetailPath(authorized.Connection.ExternalStoreId, questionId), null, cancellationToken);
        if (!response.IsSuccess) return AdapterResult<RemoteMarketplaceQuestion>.Failure(response.Error!, response.RateLimit);
        try
        {
            using var document = JsonDocument.Parse(response.Value!);
            var question = TrendyolQuestionMapper.Map(document.RootElement, "PRODUCT");
            return question is null ? AdapterResult<RemoteMarketplaceQuestion>.Failure(new(AdapterErrorClass.ContractViolation, "TRENDYOL_QUESTION_CONTRACT", "Trendyol soru yanıtı eksik alan içeriyor.", null, null, null)) : AdapterResult<RemoteMarketplaceQuestion>.Success(question, response.RateLimit);
        }
        catch (JsonException) { return AdapterResult<RemoteMarketplaceQuestion>.Failure(TrendyolErrorMapper.Contract(), response.RateLimit); }
    }

    public async Task<AdapterResult<RemoteQuestionAnswerResult>> AnswerQuestionAsync(AdapterContext context, string questionId, string kind, string answer, CancellationToken cancellationToken)
    {
        var authorized = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken);
        if (authorized is null) return AdapterResult<RemoteQuestionAnswerResult>.Failure(TrendyolErrorMapper.Configuration());
        if (!IntegrationRuntimePolicy.AllowsExternalWrite(authorized.Connection, context, GlobalWritesEnabled, authorized.ExternalWritesEnabled))
            return AdapterResult<RemoteQuestionAnswerResult>.Failure(new(AdapterErrorClass.Validation, "EXTERNAL_WRITES_DISABLED", "Bu bağlantıda veya ortamda pazaryerine cevap gönderme kapalı.", 403, null, null));
        if (!TrendyolQuestionRequestContract.ValidAnswer(answer)) return AdapterResult<RemoteQuestionAnswerResult>.Failure(new(AdapterErrorClass.Validation, "QUESTION_ANSWER_LENGTH", "Trendyol cevabı 10–2000 karakter arasında olmalıdır.", 422, null, null));
        var content = JsonContent.Create(new { text = answer.Trim() });
        var response = await SendAsync(authorized, HttpMethod.Post, TrendyolQuestionRequestContract.AnswerPath(authorized.Connection.ExternalStoreId, questionId), content, cancellationToken);
        if (!response.IsSuccess) return AdapterResult<RemoteQuestionAnswerResult>.Failure(response.Error!, response.RateLimit);
        string? answerId = null;
        try { using var json = JsonDocument.Parse(response.Value!); answerId = TrendyolQuestionMapper.Text(json.RootElement, "answerId"); } catch (JsonException) { }
        return AdapterResult<RemoteQuestionAnswerResult>.Success(new(true, false, answerId), response.RateLimit);
    }
}

internal static class TrendyolQuestionRequestContract
{
    public static string ListPath(string sellerId, QuestionPollRequest query, DateTimeOffset start, DateTimeOffset end) =>
        $"integration/qna/sellers/{Uri.EscapeDataString(sellerId)}/questions/filter?supplierId={Uri.EscapeDataString(sellerId)}&startDate={start.ToUnixTimeMilliseconds()}&endDate={end.ToUnixTimeMilliseconds()}&status={Uri.EscapeDataString(query.Status ?? "WAITING_FOR_ANSWER")}&page={Math.Max(0, query.Page)}&size={Math.Clamp(query.Size, 1, 50)}&orderByField=LastModifiedDate&orderByDirection=DESC";

    public static string DetailPath(string sellerId, string questionId) => $"integration/qna/sellers/{Uri.EscapeDataString(sellerId)}/questions/{Uri.EscapeDataString(questionId)}?supplierId={Uri.EscapeDataString(sellerId)}";
    public static string AnswerPath(string sellerId, string questionId) => $"integration/qna/sellers/{Uri.EscapeDataString(sellerId)}/questions/{Uri.EscapeDataString(questionId)}/answers";
    public static bool ValidAnswer(string? answer) => answer?.Trim().Length is >= 10 and <= 2000;
}

internal static class TrendyolQuestionMapper
{
    public static MarketplaceQuestionPage Page(string json, string kind)
    {
        using var document = JsonDocument.Parse(json); var root = document.RootElement;
        var content = Property(root, "content");
        if (content.ValueKind != JsonValueKind.Array) throw new JsonException("Question page did not include content.");
        var items = content.EnumerateArray().Select(item => Map(item, kind)).Where(item => item is not null).Select(item => item!).ToArray();
        return new(items, Number(root, "page") ?? 0, Number(root, "totalPages") ?? (items.Length > 0 ? 1 : 0), Number(root, "totalElements") ?? items.Length);
    }
    public static RemoteMarketplaceQuestion? Map(JsonElement item, string kind)
    {
        var id = Text(item, "id"); if (string.IsNullOrWhiteSpace(id)) return null;
        var created = Date(item, "creationDate") ?? DateTimeOffset.UnixEpoch;
        var status = (Text(item, "status") ?? "WAITING_FOR_ANSWER").Trim().ToUpperInvariant();
        var history = new List<RemoteQuestionConversation> { new("Customer", Text(item, "text") ?? "", created) };
        AppendAnswer(history, Property(item, "answer"), "Merchant", created);
        AppendAnswer(history, Property(item, "rejectedAnswer"), "Merchant", created, Text(Property(item, "rejectedAnswer"), "reason"));
        var lastModified = Date(item, "lastModifiedDate") ?? Date(item, "rejectedDate") ?? Date(item, "reportedDate") ?? Date(Property(item, "answer"), "creationDate") ?? Date(Property(item, "rejectedAnswer"), "creationDate") ?? created;
        var rejectedReason = Text(item, "reason", "reportReason");
        return new(id, kind, status, Text(item, "text") ?? "", Text(item, "productName"), Text(item, "imageUrl"), null, Text(item, "barcode"), Text(item, "productMainId"), Text(item, "userName"), null, created, null, lastModified,
            history.Select(message => message.RejectionReason is null && message.Author == "Merchant" && status == "REJECTED" && rejectedReason is not null ? message with { RejectionReason = rejectedReason } : message).ToArray());
    }
    public static string? Text(JsonElement item, params string[] names) { foreach (var name in names) { var value = Property(item, name); if (value.ValueKind == JsonValueKind.String) return value.GetString(); if (value.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False) return value.ToString(); } return null; }
    private static void AppendAnswer(List<RemoteQuestionConversation> history, JsonElement answer, string author, DateTimeOffset fallback, string? reason = null)
    {
        if (answer.ValueKind != JsonValueKind.Object) return; var text = Text(answer, "text"); if (!string.IsNullOrWhiteSpace(text)) history.Add(new(author, text, Date(answer, "creationDate") ?? fallback, reason));
    }
    private static DateTimeOffset? Date(JsonElement item, params string[] names)
    {
        foreach (var name in names)
        {
            var value = Property(item, name);
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var epoch)) { try { return DateTimeOffset.FromUnixTimeMilliseconds(epoch); } catch (ArgumentOutOfRangeException) { } }
            if (value.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date)) return date.ToUniversalTime();
        }
        return null;
    }
    private static int? Number(JsonElement item, string name) => int.TryParse(Text(item, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
    private static JsonElement Property(JsonElement item, string name) => item.ValueKind == JsonValueKind.Object && item.EnumerateObject().FirstOrDefault(property => property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Value is { } value ? value : default;
}
