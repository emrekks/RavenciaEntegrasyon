using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Infrastructure.Adapters.Hepsiburada;
using MarketplaceHub.Infrastructure.Adapters.Trendyol;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class MarketplaceQuestionAdapterTests
{
    [Fact]
    public void TrendyolQuestionKeepsTheCustomersNumberEvenWhenTheNameIsHidden()
    {
        const string json = """
        {"content":[
          {"id":1,"status":"WAITING_FOR_ANSWER","text":"Kalıbı dar mı?","creationDate":1790000000000,"customerId":98765432,"userName":"","showUserName":false},
          {"id":2,"status":"WAITING_FOR_ANSWER","text":"Boyu kaç cm?","creationDate":1790000000000,"customerId":0},
          {"id":3,"status":"WAITING_FOR_ANSWER","text":"Astarlı mı?","creationDate":1790000000000}
        ],"page":0,"totalPages":1,"totalElements":3}
        """;

        var page = TrendyolQuestionMapper.Page(json, "PRODUCT");

        Assert.Equal(["98765432", null, null], page.Items.Select(item => item.CustomerKey));
    }

    [Fact]
    public void TrendyolQuestionPageMapsAnswerAndRejectedAnswerHistory()
    {
        var firstMessageAt = DateTimeOffset.Parse("2026-10-01T10:00:00Z").ToUnixTimeMilliseconds();
        var answerAt = DateTimeOffset.Parse("2026-10-01T11:00:00Z").ToUnixTimeMilliseconds();
        var rejectedAt = DateTimeOffset.Parse("2026-10-01T12:00:00Z").ToUnixTimeMilliseconds();
        var json = $$"""
        {
          "content": [
            {
              "id": 42,
              "status": "REJECTED",
              "text": "Bu ürün pamuklu mu?",
              "creationDate": {{firstMessageAt}},
              "productName": "Triko bluz",
              "productMainId": "MODEL-1",
              "answer": { "id": 9, "text": "Evet, pamukludur.", "creationDate": {{answerAt}} },
              "rejectedAnswer": {
                "id": 10,
                "text": "Ürün bilgisini kontrol edin.",
                "reason": "Yasaklı ifade",
                "creationDate": {{rejectedAt}}
              }
            }
          ],
          "page": 0,
          "totalPages": 1,
          "totalElements": 1
        }
        """;

        var page = TrendyolQuestionMapper.Page(json, "PRODUCT");

        Assert.Equal(1, page.TotalPages);
        var question = Assert.Single(page.Items);
        Assert.Equal("42", question.Id);
        Assert.Equal("REJECTED", question.Status);
        Assert.Equal("MODEL-1", question.ProductModelCode);
        Assert.Equal(3, question.Conversations.Count);
        Assert.Equal("Ürün bilgisini kontrol edin.", question.Conversations[2].Text);
        Assert.Equal("Yasaklı ifade", question.Conversations[2].RejectionReason);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(rejectedAt), question.Conversations[2].CreatedAt);
    }

    [Fact]
    public void HepsiburadaMapsOrderQuestionConversationsAndExactExpiry()
    {
        const string json = """
        {"totalPages":1,"totalElements":1,"issues":[{"issueNumber":"HB-77","status":1,"createdAt":"2026-10-02T10:00:00Z","lastModifiedAt":"2026-10-03T10:00:00Z","expireDate":"2026-10-04T10:00:00Z","customerName":"Ayşe","orderNumber":"ORDER-9","source":2,"product":{"name":"Elbise","stockCode":"SKU-9","productCode":"1579897570"},"lastContent":"Son mesaj","conversations":[{"from":"Customer","content":"Sipariş ne zaman kargoya verilir?","createdAt":"2026-10-02T10:00:00Z"},{"from":"Seller","content":"Yarın kargoya vereceğiz.","createdAt":"2026-10-03T10:00:00Z"}]}]}
        """;

        using var document = JsonDocument.Parse(json);
        var page = HepsiburadaQuestionMapper.Page(document, new("ORDER", null, null, null, 1, 25));
        var question = Assert.Single(page.Items);

        Assert.Equal("ORDER", question.Kind);
        Assert.Equal("WAITING_FOR_ANSWER", question.Status);
        Assert.Equal("Sipariş ne zaman kargoya verilir?", question.Text);
        Assert.Equal("ORDER-9", question.OrderNumber);
        Assert.Null(question.ProductModelCode);
        Assert.Equal(DateTimeOffset.Parse("2026-10-04T10:00:00Z"), question.ExpiresAt);
        Assert.Equal("Seller", question.Conversations[1].Author);
    }

    [Fact]
    public void HepsiburadaQuestionMapsNestedProductImageAliases()
    {
        using var document = JsonDocument.Parse("""{"issues":[{"issueNumber":"HB-IMAGE","status":2,"createdAt":"2026-10-02T10:00:00Z","product":{"name":"Elbise","stockCode":"SKU-9","productCode":"1579897570","images":[{"url":"https://productimages.hepsiburada.net/item.jpg"}]}}]}""");

        var question = Assert.Single(HepsiburadaQuestionMapper.Page(document, new("PRODUCT", null, null, null, 1, 25)).Items);

        Assert.Equal("https://productimages.hepsiburada.net/item.jpg", question.ProductImageUrl);
        Assert.Null(question.ProductModelCode);
    }

    [Fact]
    public void HepsiburadaDetailMapsNestedIssueAndExpiredStatus()
    {
        using var document = JsonDocument.Parse("""{"data":{"issue":{"number":"HB-88","status":"AutoClosed","createdAt":"2026-10-01T12:00:00Z","expireDate":1790942400000,"lastContent":"Soru metni"}}}""");

        var question = HepsiburadaQuestionMapper.Single(document, "PRODUCT");

        Assert.NotNull(question);
        Assert.Equal("EXPIRED", question.Status);
        Assert.Equal("Soru metni", question.Text);
        Assert.NotNull(question.ExpiresAt);
    }

    [Fact]
    public void TrendyolRequestIncludesRequiredSupplierAndClampsPagingAndAnswerLength()
    {
        var start = DateTimeOffset.Parse("2026-09-24T00:00:00Z");
        var end = DateTimeOffset.Parse("2026-10-01T00:00:00Z");
        var path = TrendyolQuestionRequestContract.ListPath("seller/17", new("PRODUCT", "WAITING_FOR_ANSWER", start, end, -1, 500), start, end);

        Assert.Contains("supplierId=seller%2F17", path);
        Assert.Contains("page=0&size=50", path);
        Assert.Contains("orderByField=LastModifiedDate", path);
        var requestUri = new Uri(new Uri("https://apigw.trendyol.com/integration/"), path);
        Assert.Equal("/integration/qna/sellers/seller%2F17/questions/filter", requestUri.AbsolutePath);
        Assert.Equal("/integration/qna/sellers/seller%2F17/questions/42", new Uri(new Uri("https://apigw.trendyol.com/integration/"), TrendyolQuestionRequestContract.DetailPath("seller/17", "42")).AbsolutePath);
        Assert.Equal("/integration/qna/sellers/seller%2F17/questions/42/answers", new Uri(new Uri("https://apigw.trendyol.com/integration/"), TrendyolQuestionRequestContract.AnswerPath("seller/17", "42")).AbsolutePath);
        Assert.False(TrendyolQuestionRequestContract.ValidAnswer("too short"));
        Assert.True(TrendyolQuestionRequestContract.ValidAnswer(new string('x', 10)));
        Assert.True(TrendyolQuestionRequestContract.ValidAnswer(new string('x', 2000)));
        Assert.False(TrendyolQuestionRequestContract.ValidAnswer(new string('x', 2001)));
    }

    [Fact]
    public void TrendyolQuestionRequestsIncludeRequiredStoreFrontCode()
    {
        var context = new TrendyolRequestContext(null!, new Uri("https://apigw.trendyol.com/integration/"), "api-key", "api-secret", "123456 - SelfIntegration", false);

        using var request = TrendyolAuthenticationHandler.Create(context, HttpMethod.Get, "qna/sellers/123456/questions/filter");

        Assert.Equal("TR", Assert.Single(request.Headers.GetValues("storeFrontCode")));
    }

    [Fact]
    public async Task HepsiburadaRequestUsesOrderSourceAndMultipartAnswerField()
    {
        var path = HepsiburadaQuestionRequestContract.ListPath(new("ORDER", "WAITING_FOR_ANSWER", null, null, 0, 100));
        Assert.Contains("page=1&size=25", path);
        Assert.Contains("status=1", path);
        Assert.EndsWith("source=2", path);
        Assert.Equal("api/v1.0/issues/HB%2F77/answer", HepsiburadaQuestionRequestContract.AnswerPath("HB/77"));
        Assert.False(HepsiburadaQuestionRequestContract.ValidAnswer(""));
        Assert.True(HepsiburadaQuestionRequestContract.ValidAnswer(new string('x', 1)));
        Assert.False(HepsiburadaQuestionRequestContract.ValidAnswer(new string('x', 2001)));

        using var content = HepsiburadaQuestionRequestContract.CreateAnswerContent("  Metin cevabı  ");
        Assert.Equal("multipart/form-data", content.Headers.ContentType?.MediaType);
        var field = Assert.Single(content);
        Assert.Equal("Answer", field.Headers.ContentDisposition?.Name);
        Assert.Equal("Metin cevabı", await field.ReadAsStringAsync());
    }

    [Fact]
    public async Task HepsiburadaReportPostsTheReasonAsRejectReasonJson()
    {
        Assert.Equal("api/v1.0/issues/HB%2F77/reject", HepsiburadaQuestionRequestContract.RejectPath("HB/77"));
        Assert.Equal("Reklam", HepsiburadaQuestionRequestContract.RejectReasonText(new("ADVERTISEMENT", "Reklam", "  ")));
        var text = HepsiburadaQuestionRequestContract.RejectReasonText(new("OTHER", "Diğer", "  Rakip mağazaya yönlendiriyor  "));
        Assert.Equal("Diğer: Rakip mağazaya yönlendiriyor", text);
        Assert.False(HepsiburadaQuestionRequestContract.ValidRejectReason(" "));
        Assert.False(HepsiburadaQuestionRequestContract.ValidRejectReason(new string('x', 2001)));

        using var content = HepsiburadaQuestionRequestContract.CreateRejectContent(text);
        Assert.Equal("application/json", content.Headers.ContentType?.MediaType);
        using var body = JsonDocument.Parse(await content.ReadAsStringAsync());
        var property = Assert.Single(body.RootElement.EnumerateObject());
        Assert.Equal("rejectReason", property.Name);
        Assert.Equal("Diğer: Rakip mağazaya yönlendiriyor", property.Value.GetString());
    }

    [Fact]
    public void ReportCapabilitiesFollowEachMarketplacesDocumentation()
    {
        var hepsiburada = HepsiburadaQuestionRequestContract.ReportCapability;
        Assert.True(hepsiburada.Supported);
        Assert.Equal(["WAITING_FOR_ANSWER"], hepsiburada.AllowedStatuses);
        Assert.Equal("REJECTED", hepsiburada.ResultStatus);
        Assert.Contains(hepsiburada.Reasons, reason => reason is { Code: "ADVERTISEMENT", Label: "Reklam", NoteRequired: false });
        Assert.Contains(hepsiburada.Reasons, reason => reason is { Code: "OTHER", NoteRequired: true });
        // The longest note after the longest label still fits Hepsiburada's 2000 characters.
        Assert.True(hepsiburada.Reasons.Max(reason => reason.Label.Length) + 2 + hepsiburada.NoteMaxLength <= HepsiburadaQuestionRequestContract.RejectReasonMaxLength);
        Assert.StartsWith("https://developers.hepsiburada.com/", hepsiburada.DocumentationUrl);

        var trendyol = TrendyolQuestionRequestContract.ReportCapability;
        Assert.False(trendyol.Supported);
        Assert.Contains("Satıcı Paneli", trendyol.UnsupportedReason);
        Assert.Empty(trendyol.Reasons);
        Assert.False(MarketplaceHub.Infrastructure.Adapters.N11.N11HttpClient.QuestionReportSupport.Supported);
        Assert.False(MarketplaceHub.Infrastructure.Adapters.Pazarama.PazaramaHttpClient.QuestionReportSupport.Supported);
    }
}
