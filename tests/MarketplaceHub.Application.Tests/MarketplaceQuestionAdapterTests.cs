using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Infrastructure.Adapters.Hepsiburada;
using MarketplaceHub.Infrastructure.Adapters.Trendyol;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class MarketplaceQuestionAdapterTests
{
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
        {"totalPages":1,"totalElements":1,"issues":[{"issueNumber":"HB-77","status":1,"createdAt":"2026-10-02T10:00:00Z","lastModifiedAt":"2026-10-03T10:00:00Z","expireDate":"2026-10-04T10:00:00Z","customerName":"Ayşe","orderNumber":"ORDER-9","source":2,"product":{"name":"Elbise","stockCode":"SKU-9"},"lastContent":"Son mesaj","conversations":[{"from":"Customer","content":"Sipariş ne zaman kargoya verilir?","createdAt":"2026-10-02T10:00:00Z"},{"from":"Seller","content":"Yarın kargoya vereceğiz.","createdAt":"2026-10-03T10:00:00Z"}]}]}
        """;

        using var document = JsonDocument.Parse(json);
        var page = HepsiburadaQuestionMapper.Page(document, new("ORDER", null, null, null, 1, 25));
        var question = Assert.Single(page.Items);

        Assert.Equal("ORDER", question.Kind);
        Assert.Equal("WAITING_FOR_ANSWER", question.Status);
        Assert.Equal("Sipariş ne zaman kargoya verilir?", question.Text);
        Assert.Equal("ORDER-9", question.OrderNumber);
        Assert.Equal("SKU-9", question.ProductModelCode);
        Assert.Equal(DateTimeOffset.Parse("2026-10-04T10:00:00Z"), question.ExpiresAt);
        Assert.Equal("Seller", question.Conversations[1].Author);
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
        Assert.Contains("/questions/42/answers", TrendyolQuestionRequestContract.AnswerPath("seller/17", "42"));
        Assert.False(TrendyolQuestionRequestContract.ValidAnswer("too short"));
        Assert.True(TrendyolQuestionRequestContract.ValidAnswer(new string('x', 10)));
        Assert.True(TrendyolQuestionRequestContract.ValidAnswer(new string('x', 2000)));
        Assert.False(TrendyolQuestionRequestContract.ValidAnswer(new string('x', 2001)));
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
}
