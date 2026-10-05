using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using MarketplaceHub.Application;

namespace MarketplaceHub.Infrastructure.Adapters.Hepsiburada;

public sealed partial class HepsiburadaHttpClient : IQuestionPort
{
    public async Task<AdapterResult<MarketplaceQuestionPage>> ListQuestionsAsync(AdapterContext context, QuestionPollRequest query, CancellationToken cancellationToken)
    {
        var account = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken);
        if (account?.QuestionBaseAddress is null) return AdapterResult<MarketplaceQuestionPage>.Failure(new(AdapterErrorClass.Authentication, "HEPSIBURADA_QUESTION_CREDENTIALS", "Hepsiburada soru bağlantısı veya servis anahtarı eksik.", 401, null, null));
        var response = await SendAsync(account, account.QuestionBaseAddress, HttpMethod.Get, HepsiburadaQuestionRequestContract.ListPath(query), null, cancellationToken,
            request => request.Headers.TryAddWithoutValidation("merchantId", account.Connection.ExternalStoreId));
        if (!response.IsSuccess) return AdapterResult<MarketplaceQuestionPage>.Failure(response.Error!, response.RateLimit);
        try { return AdapterResult<MarketplaceQuestionPage>.Success(HepsiburadaQuestionMapper.Page(response.Value!, query), response.RateLimit); }
        catch (JsonException) { return AdapterResult<MarketplaceQuestionPage>.Failure(new(AdapterErrorClass.ContractViolation, "HEPSIBURADA_QUESTION_CONTRACT", "Hepsiburada soru yanıtının biçimi tanınmadı.", null, null, null), response.RateLimit); }
    }

    public async Task<AdapterResult<RemoteMarketplaceQuestion>> GetQuestionAsync(AdapterContext context, string questionId, string kind, CancellationToken cancellationToken)
    {
        var account = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken);
        if (account?.QuestionBaseAddress is null) return AdapterResult<RemoteMarketplaceQuestion>.Failure(new(AdapterErrorClass.Authentication, "HEPSIBURADA_QUESTION_CREDENTIALS", "Hepsiburada soru bağlantısı veya servis anahtarı eksik.", 401, null, null));
        var response = await SendAsync(account, account.QuestionBaseAddress, HttpMethod.Get, HepsiburadaQuestionRequestContract.DetailPath(questionId), null, cancellationToken,
            request => request.Headers.TryAddWithoutValidation("merchantId", account.Connection.ExternalStoreId));
        if (!response.IsSuccess) return AdapterResult<RemoteMarketplaceQuestion>.Failure(response.Error!, response.RateLimit);
        try
        {
            var question = HepsiburadaQuestionMapper.Single(response.Value!, kind);
            return question is null
                ? AdapterResult<RemoteMarketplaceQuestion>.Failure(new(AdapterErrorClass.NotFound, "QUESTION_NOT_FOUND_REMOTE", "Pazaryerinde soru bulunamadı veya artık erişilebilir değil.", 404, null, null), response.RateLimit)
                : AdapterResult<RemoteMarketplaceQuestion>.Success(question, response.RateLimit);
        }
        catch (JsonException) { return AdapterResult<RemoteMarketplaceQuestion>.Failure(new(AdapterErrorClass.ContractViolation, "HEPSIBURADA_QUESTION_CONTRACT", "Hepsiburada soru yanıtının biçimi tanınmadı.", null, null, null), response.RateLimit); }
    }

    public async Task<AdapterResult<RemoteQuestionAnswerResult>> AnswerQuestionAsync(AdapterContext context, string questionId, string kind, string answer, CancellationToken cancellationToken)
    {
        var account = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken);
        if (account?.QuestionBaseAddress is null) return AdapterResult<RemoteQuestionAnswerResult>.Failure(new(AdapterErrorClass.Authentication, "HEPSIBURADA_QUESTION_CREDENTIALS", "Hepsiburada soru bağlantısı veya servis anahtarı eksik.", 401, null, null));
        if (!IntegrationRuntimePolicy.AllowsExternalWrite(account.Connection, context, GlobalWritesEnabled, ConnectionWritesEnabled(account.Connection.SettingsJson)))
            return AdapterResult<RemoteQuestionAnswerResult>.Failure(new(AdapterErrorClass.Validation, "EXTERNAL_WRITES_DISABLED", "Bu bağlantıda veya ortamda pazaryerine cevap gönderme kapalı.", 403, null, null));
        if (!HepsiburadaQuestionRequestContract.ValidAnswer(answer)) return AdapterResult<RemoteQuestionAnswerResult>.Failure(new(AdapterErrorClass.Validation, "QUESTION_ANSWER_LENGTH", "Cevap 1–2000 karakter arasında olmalıdır.", 422, null, null));
        using var content = HepsiburadaQuestionRequestContract.CreateAnswerContent(answer);
        var response = await SendAsync(account, account.QuestionBaseAddress, HttpMethod.Post, HepsiburadaQuestionRequestContract.AnswerPath(questionId), content, cancellationToken,
            request => request.Headers.TryAddWithoutValidation("merchantId", account.Connection.ExternalStoreId));
        if (!response.IsSuccess) return AdapterResult<RemoteQuestionAnswerResult>.Failure(response.Error!, response.RateLimit);
        return AdapterResult<RemoteQuestionAnswerResult>.Success(new(true, false, null), response.RateLimit);
    }

}

internal static class HepsiburadaQuestionRequestContract
{
    public static string ListPath(QuestionPollRequest query)
    {
        var values = new List<string> { $"page={Math.Max(1, query.Page)}", $"size={Math.Clamp(query.Size, 1, 25)}", "sortBy=1", "desc=true" };
        if (query.Status is not null) values.Add("status=" + Uri.EscapeDataString(HepsiburadaQuestionMapper.ToRemoteStatus(query.Status)));
        if (query.StartDate is { } start) values.Add("minModifiedAt=" + Uri.EscapeDataString(start.ToString("O", CultureInfo.InvariantCulture)));
        if (query.EndDate is { } end) values.Add("maxModifiedAt=" + Uri.EscapeDataString(end.ToString("O", CultureInfo.InvariantCulture)));
        values.Add("source=" + (query.Kind.Equals("ORDER", StringComparison.OrdinalIgnoreCase) ? "2" : "1"));
        return $"api/v1.0/issues?{string.Join('&', values)}";
    }

    public static string DetailPath(string questionId) => $"api/v1.0/issues/{Uri.EscapeDataString(questionId)}";
    public static string AnswerPath(string questionId) => $"api/v1.0/issues/{Uri.EscapeDataString(questionId)}/answer";
    public static MultipartFormDataContent CreateAnswerContent(string answer)
    {
        var content = new MultipartFormDataContent();
        content.Add(new StringContent(answer.Trim()), "Answer");
        return content;
    }
    public static bool ValidAnswer(string? answer) => answer?.Trim().Length is >= 1 and <= 2000;
}

internal static class HepsiburadaQuestionMapper
{
    public static MarketplaceQuestionPage Page(JsonDocument document, QuestionPollRequest query)
    {
        var root = document.RootElement; var rows = FindArray(root, "content", "issues", "items", "data");
        var items = rows.Select(item => Map(item, query.Kind)).Where(item => item is not null).Select(item => item!).ToArray();
        var totalPages = Number(root, "totalPages", "totalPageCount", "pages") ?? (items.Length < query.Size ? query.Page : query.Page + 1);
        var total = Number(root, "totalElements", "totalCount", "total", "count") ?? items.Length;
        return new(items, query.Page, totalPages, total);
    }
    public static RemoteMarketplaceQuestion? Single(JsonDocument document, string kind)
    {
        return FindSingle(document.RootElement, kind, 0);
    }

    private static RemoteMarketplaceQuestion? FindSingle(JsonElement root, string kind, int depth)
    {
        if (depth > 5) return null;
        if (Text(root, "issueNumber", "number", "id") is not null) return Map(root, kind);
        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var nested in root.EnumerateArray())
                if (FindSingle(nested, kind, depth + 1) is { } result) return result;
            return null;
        }
        foreach (var name in new[] { "issue", "data", "result", "content", "item" })
        {
            var nested = Property(root, name);
            if (nested.ValueKind is JsonValueKind.Object or JsonValueKind.Array && FindSingle(nested, kind, depth + 1) is { } result) return result;
        }
        return null;
    }

    private static RemoteMarketplaceQuestion? Map(JsonElement item, string kind)
    {
        var id = Text(item, "issueNumber", "number", "id"); if (string.IsNullOrWhiteSpace(id)) return null;
        var product = Property(item, "product"); var rawStatus = Text(item, "status") ?? "WaitingForAnswer";
        var status = rawStatus.Replace("_", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal).ToUpperInvariant() switch
        { "1" or "WAITINGFORANSWER" => "WAITING_FOR_ANSWER", "2" or "ANSWERED" => "ANSWERED", "3" or "REJECTED" => "REJECTED", "4" or "AUTOCLOSED" => "EXPIRED", _ => rawStatus.ToUpperInvariant() };
        var created = Date(item, "createdAt", "creationDate", "createdDate") ?? DateTimeOffset.UnixEpoch; var modified = Date(item, "lastModifiedAt", "lastModifiedDate", "modifiedAt") ?? created;
        var conversations = FindArray(item, "conversations").Select(message => new RemoteQuestionConversation(Text(message, "from", "author", "sender") ?? "Customer", Text(message, "content", "text", "message") ?? "", Date(message, "createdAt", "creationDate", "date") ?? created, Text(message, "rejectReason", "rejectionReason", "reason"))).ToList();
        if (conversations.Count == 0 && Text(item, "lastContent") is { Length: > 0 } latest) conversations.Add(new(status == "WAITING_FOR_ANSWER" ? "Customer" : "Merchant", latest, modified, Text(item, "rejectReason", "rejectionReason")));
        var customerQuestion = conversations.FirstOrDefault(message => message.Author.Contains("customer", StringComparison.OrdinalIgnoreCase))?.Text
            ?? Text(item, "questionText", "question", "lastContent") ?? conversations.FirstOrDefault()?.Text ?? "";
        return new(id, kind, status, customerQuestion, Text(product, "name"), Text(product, "imageUrl"), Text(product, "sku", "merchantSku"), Text(product, "barcode"), Text(product, "stockCode", "modelCode"), Text(item, "customerName"), Text(item, "orderNumber", "orderNo", "orderId"), created, Date(item, "expireDate"), modified, conversations);
    }
    public static string ToRemoteStatus(string status) => status switch { "WAITING_FOR_ANSWER" => "1", "ANSWERED" => "2", "REJECTED" => "3", "EXPIRED" => "4", _ => status };
    private static JsonElement Property(JsonElement item, string name) => item.ValueKind == JsonValueKind.Object && item.EnumerateObject().FirstOrDefault(property => property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Value is { } value ? value : default;
    private static string? Text(JsonElement item, params string[] names) { foreach (var name in names) { var value = Property(item, name); if (value.ValueKind == JsonValueKind.String) return value.GetString(); if (value.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False) return value.ToString(); } return null; }
    private static DateTimeOffset? Date(JsonElement item, params string[] names) { foreach (var name in names) { var value = Property(item, name); if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var epoch)) { try { return epoch < 10_000_000_000 ? DateTimeOffset.FromUnixTimeSeconds(epoch) : DateTimeOffset.FromUnixTimeMilliseconds(epoch); } catch (ArgumentOutOfRangeException) { } } if (value.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date)) return date.ToUniversalTime(); } return null; }
    private static int? Number(JsonElement item, params string[] names) => int.TryParse(Text(item, names), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : null;
    private static IEnumerable<JsonElement> FindArray(JsonElement item, params string[] names) { if (item.ValueKind == JsonValueKind.Array) return item.EnumerateArray().ToArray(); foreach (var name in names) { var value = Property(item, name); if (value.ValueKind == JsonValueKind.Array) return value.EnumerateArray().ToArray(); if (value.ValueKind == JsonValueKind.Object) { var nested = FindArray(value, "content", "items", "issues"); if (nested.Any()) return nested; } } return []; }
}
