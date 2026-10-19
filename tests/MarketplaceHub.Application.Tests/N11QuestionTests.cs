using System.Xml.Linq;
using MarketplaceHub.Application;
using MarketplaceHub.Infrastructure.Adapters.N11;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class N11QuestionTests
{
    private static XElement Detail(string answer) => XElement.Parse($$"""
        <productQuestion>
          <productId>4455667788</productId><productTitle>Kadın Bluz</productTitle><questionSubject>Beden</questionSubject>
          <question>M beden kaç cm?</question><answer>{{answer}}</answer><fullName>Ayşe Y.</fullName><email>x@example.com</email>
          <status>{{(answer.Length == 0 ? "OPEN" : "CLOSED")}}</status><questionDate>2026-10-12</questionDate><answeredDate>{{(answer.Length == 0 ? "" : "2026-10-13")}}</answeredDate>
          <images><image>https://img.example.com/1.jpg</image></images>
        </productQuestion>
        """);

    [Fact]
    public void OpenQuestionsWaitForAnAnswer()
    {
        var question = N11QuestionMapper.Question("123", null, Detail(""))!;

        Assert.Equal("WAITING_FOR_ANSWER", question.Status);
        Assert.Equal("PRODUCT", question.Kind);
        Assert.Equal("Beden: M beden kaç cm?", question.Text);
        Assert.Equal("Kadın Bluz", question.ProductName);
        Assert.Equal("https://img.example.com/1.jpg", question.ProductImageUrl);
        Assert.Equal(new DateTimeOffset(2026, 10, 12, 0, 0, 0, TimeSpan.FromHours(3)), question.CreatedAt);
        Assert.Empty(question.Conversations);
    }

    [Fact]
    public void AnsweredQuestionsCarryTheSellerAnswer()
    {
        var question = N11QuestionMapper.Question("123", null, Detail("Göğüs 96 cm."))!;

        Assert.Equal("ANSWERED", question.Status);
        var reply = Assert.Single(question.Conversations);
        Assert.Equal("Göğüs 96 cm.", reply.Text);
        Assert.Equal(new DateTimeOffset(2026, 10, 13, 0, 0, 0, TimeSpan.FromHours(3)), question.LastModifiedAt);
    }

    [Fact]
    public void ListRequestMapsPanelStatesToN11States()
    {
        var body = N11HttpClient.QuestionListBody(new QuestionPollRequest("PRODUCT", "WAITING_FOR_ANSWER", null, null, 0, 50), new DateTimeOffset(2026, 10, 15, 9, 0, 0, TimeSpan.Zero));

        Assert.Contains("<status>OPEN</status>", body);
        Assert.Contains("<startDate>01/10/2026</startDate>", body);
        Assert.Contains("<endDate>15/10/2026</endDate>", body);
        Assert.Contains("<pageSize>50</pageSize>", body);
    }

    [Fact]
    public void ListAnswersWithoutAResultBlockAreAccepted()
    {
        const string body = """
            <SOAP-ENV:Envelope xmlns:SOAP-ENV="http://schemas.xmlsoap.org/soap/envelope/"><SOAP-ENV:Body>
              <ns3:GetProductQuestionListResponse xmlns:ns3="http://www.n11.com/ws/schemas">
                <productQuestions><productQuestion><id>123</id></productQuestion></productQuestions>
                <pagingData><currentPage>0</currentPage><pageSize>50</pageSize><totalCount>1</totalCount><pageCount>1</pageCount></pagingData>
              </ns3:GetProductQuestionListResponse>
            </SOAP-ENV:Body></SOAP-ENV:Envelope>
            """;

        Assert.Null(N11HttpClient.SoapResponse(body, "GetProductQuestionList", requireResult: false).Error);
        Assert.NotNull(N11HttpClient.SoapResponse(body, "GetProductQuestionList").Error);
    }

    [Theory]
    [InlineData(1, 1d / 75)]
    [InlineData(600, 480d / 60)]
    public void TinyQuotasAreEvenlySpaced(int perMinute, double tokensPerSecond)
    {
        Assert.Equal(tokensPerSecond, N11RateLimitHandler.BucketShape(perMinute).TokensPerSecond, 6);
    }
}
