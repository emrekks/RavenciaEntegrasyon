using System.Globalization;
using System.Net;
using System.Xml.Linq;
using MarketplaceHub.Application;

namespace MarketplaceHub.Infrastructure.Adapters.N11;

// Customer product questions: SOAP ProductService (https://api.n11.com/ws/ProductService.wsdl)
// GetProductQuestionList (n11 allows one call per minute), GetProductQuestionDetail and
// SaveProductAnswer. n11 has only product questions and two states: OPEN and CLOSED (answered).
public sealed partial class N11HttpClient : IQuestionPort
{
    private const string ProductService = "ws/productService/";

    public async Task<AdapterResult<MarketplaceQuestionPage>> ListQuestionsAsync(AdapterContext context, QuestionPollRequest request, CancellationToken cancellationToken)
    {
        var account = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken);
        if (account is null) return CredentialMissing<MarketplaceQuestionPage>();
        var listed = await SendSoapAsync(account, ProductService, "GetProductQuestionList", QuestionListBody(request, timeProvider.GetUtcNow()), cancellationToken, requireResult: false, rateBucket: "questions-list");
        if (!listed.IsSuccess) return AdapterResult<MarketplaceQuestionPage>.Failure(listed.Error!, listed.RateLimit);

        // The list carries no dates or customer; the detail call fills them per question.
        var items = new List<RemoteMarketplaceQuestion>();
        foreach (var summary in listed.Value!.Descendants().Where(x => x.Name.LocalName == "productQuestion"))
        {
            var id = Child(summary, "id");
            if (id is null) continue;
            var detail = await SendSoapAsync(account, ProductService, "GetProductQuestionDetail", SoapElement("productQuestionId", id), cancellationToken, requireResult: false);
            if (!detail.IsSuccess) return AdapterResult<MarketplaceQuestionPage>.Failure(detail.Error!, detail.RateLimit);
            var question = N11QuestionMapper.Question(id, summary, detail.Value!.Descendants().FirstOrDefault(x => x.Name.LocalName == "productQuestion"));
            if (question is not null) items.Add(question);
        }
        var paging = listed.Value.Elements().FirstOrDefault(x => x.Name.LocalName == "pagingData");
        var pageCount = int.TryParse(Child(paging, "pageCount"), NumberStyles.None, CultureInfo.InvariantCulture, out var pages) ? pages : 0;
        var total = long.TryParse(Child(paging, "totalCount"), NumberStyles.None, CultureInfo.InvariantCulture, out var count) ? count : items.Count;
        return AdapterResult<MarketplaceQuestionPage>.Success(new(items, request.Page, pageCount, total));
    }

    internal static string QuestionListBody(QuestionPollRequest request, DateTimeOffset now)
    {
        var status = N11QuestionMapper.N11Status(request.Status);
        return "<productQuestionSearch>" + SoapElement("buyerEmail", "") + SoapElement("subject", "")
            + (status is null ? "" : SoapElement("status", status)) + SoapElement("questionDate", "")
            + SoapElement("startDate", TurkeyDate(request.StartDate ?? now.AddDays(-14))) + SoapElement("endDate", TurkeyDate(request.EndDate ?? now))
            + "</productQuestionSearch><pagingData>" + SoapElement("currentPage", Math.Max(0, request.Page).ToString(CultureInfo.InvariantCulture))
            + SoapElement("pageSize", Math.Clamp(request.Size, 1, 100).ToString(CultureInfo.InvariantCulture)) + "</pagingData>";
    }

    public async Task<AdapterResult<RemoteMarketplaceQuestion>> GetQuestionAsync(AdapterContext context, string questionId, string kind, CancellationToken cancellationToken)
    {
        var account = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken);
        if (account is null) return CredentialMissing<RemoteMarketplaceQuestion>();
        var detail = await SendSoapAsync(account, ProductService, "GetProductQuestionDetail", SoapElement("productQuestionId", questionId), cancellationToken, requireResult: false);
        if (!detail.IsSuccess) return AdapterResult<RemoteMarketplaceQuestion>.Failure(detail.Error!, detail.RateLimit);
        var question = N11QuestionMapper.Question(questionId, null, detail.Value!.Descendants().FirstOrDefault(x => x.Name.LocalName == "productQuestion"));
        return question is null
            ? Failure<RemoteMarketplaceQuestion>(AdapterErrorClass.NotFound, "N11_QUESTION_NOT_FOUND", "N11 soruyu bulamadı.", HttpStatusCode.NotFound)
            : AdapterResult<RemoteMarketplaceQuestion>.Success(question);
    }

    // n11 accepts one answer per question; the save result is the only confirmation.
    public async Task<AdapterResult<RemoteQuestionAnswerResult>> AnswerQuestionAsync(AdapterContext context, string questionId, string kind, string answer, CancellationToken cancellationToken)
    {
        var account = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken);
        if (account is null) return CredentialMissing<RemoteQuestionAnswerResult>();
        if (WriteBlocked<RemoteQuestionAnswerResult>(account, "soru cevabı") is { } blocked) return blocked;
        var saved = await SendSoapAsync(account, ProductService, "SaveProductAnswer", SoapElement("productQuestionId", questionId) + SoapElement("answer", answer.Trim()), cancellationToken);
        return saved.IsSuccess
            ? AdapterResult<RemoteQuestionAnswerResult>.Success(new(true, false, null))
            : AdapterResult<RemoteQuestionAnswerResult>.Failure(saved.Error!, saved.RateLimit);
    }
}

public static class N11QuestionMapper
{
    // Panel states → n11 search states; other panel states have no n11 counterpart.
    public static string? N11Status(string? panelStatus) => panelStatus?.Trim().ToUpperInvariant() switch
    {
        "WAITING_FOR_ANSWER" => "OPEN",
        "ANSWERED" => "CLOSED",
        _ => null
    };

    public static RemoteMarketplaceQuestion? Question(string id, XElement? summary, XElement? detail)
    {
        string? Value(string name) => N11HttpClient.Child(detail, name) ?? N11HttpClient.Child(summary, name);
        var text = Value("question");
        if (text is null) return null;
        var subject = Value("questionSubject");
        var answer = Value("answer");
        var asked = N11ReturnMapper.Date(N11HttpClient.Child(detail, "questionDate")) ?? DateTimeOffset.UnixEpoch;
        var answered = N11ReturnMapper.Date(N11HttpClient.Child(detail, "answeredDate"));
        var status = N11HttpClient.Child(detail, "status")?.ToUpperInvariant();
        var isAnswered = answer is not null || status is "CLOSED" or "ANSWERED";
        var image = (detail ?? summary)?.Descendants().FirstOrDefault(x => x.Name.LocalName == "image")?.Value.Trim();
        IReadOnlyList<RemoteQuestionConversation> conversations = answer is null
            ? []
            : [new RemoteQuestionConversation("Merchant", answer, answered ?? asked)];
        return new RemoteMarketplaceQuestion(
            id,
            "PRODUCT",
            isAnswered ? "ANSWERED" : "WAITING_FOR_ANSWER",
            subject is null || text.StartsWith(subject, StringComparison.OrdinalIgnoreCase) ? text : $"{subject}: {text}",
            Value("productTitle"),
            string.IsNullOrWhiteSpace(image) ? null : image,
            null, null, null,
            N11HttpClient.Child(detail, "fullName"),
            null,
            asked,
            null,
            answered ?? asked,
            conversations);
    }
}
