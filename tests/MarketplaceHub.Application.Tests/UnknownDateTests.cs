using System.Text.Json;
using System.Xml.Linq;
using MarketplaceHub.Application;
using MarketplaceHub.Infrastructure.Adapters.Hepsiburada;
using MarketplaceHub.Infrastructure.Adapters.N11;
using MarketplaceHub.Infrastructure.Adapters.Pazarama;
using MarketplaceHub.Infrastructure.Adapters.Trendyol;
using MarketplaceHub.Infrastructure.Adapters.Trendyol.Mapping;
using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// A date a marketplace does not send stays unknown (null); it is never stored as the Unix epoch, which the panel shows as 1970.
public sealed class UnknownDateTests
{
    [Fact]
    public void TrendyolQuestionWithoutDatesHasNoDates()
    {
        const string json = """{"content":[{"id":1,"status":"WAITING_FOR_ANSWER","text":"Dar mı?"}],"page":0,"totalPages":1,"totalElements":1}""";

        var question = Assert.Single(TrendyolQuestionMapper.Page(json, "PRODUCT").Items);

        Assert.Null(question.CreatedAt);
        Assert.Null(question.LastModifiedAt);
        Assert.Null(Assert.Single(question.Conversations).CreatedAt);
    }

    [Fact]
    public void N11QuestionWithoutDatesHasNoDates()
    {
        var detail = XElement.Parse("<productQuestion><productId>1</productId><question>Var mı?</question><answer>Evet</answer><status>CLOSED</status></productQuestion>");

        var question = N11QuestionMapper.Question("9", null, detail)!;

        Assert.Null(question.CreatedAt);
        Assert.Null(question.LastModifiedAt);
        Assert.Null(Assert.Single(question.Conversations).CreatedAt);
    }

    [Fact]
    public void PazaramaQuestionWithoutDatesHasNoDates()
    {
        using var document = JsonDocument.Parse("""{"questionId":"q1","question":"Kaç cm?","questionStatus":1}""");

        var question = PazaramaQuestionMapper.Question(document.RootElement)!;
        var detail = PazaramaQuestionMapper.Detail(document.RootElement)!;

        Assert.Null(question.CreatedAt);
        Assert.Null(question.LastModifiedAt);
        Assert.Null(detail.CreatedAt);
        Assert.Null(detail.LastModifiedAt);
    }

    [Fact]
    public void N11ReturnWithoutDatesHasNoChangeTime()
    {
        var element = XElement.Parse("<claim><claimReturnId>c1</claimReturnId><orderNumber>o1</orderNumber><status>PENDING</status></claim>");

        var claim = N11ReturnMapper.Claim(element)!;

        Assert.Null(claim.LastModifiedAt);
    }

    [Fact]
    public void PazaramaReturnWithoutDatesHasNoChangeTimeInsteadOfTheReadTime()
    {
        using var document = JsonDocument.Parse("""{"refundId":"r1","orderNumber":"o1","refundStatus":1}""");

        var claim = PazaramaReturnMapper.Claim(document.RootElement, DateTimeOffset.Parse("2026-11-05T10:00:00Z"))!;

        Assert.Null(claim.LastModifiedAt);
    }

    [Fact]
    public void TrendyolReturnWithoutDatesHasNoChangeTime()
    {
        const string json = """{"content":[{"claimId":"c1","orderNumber":"1","claimItemStatus":{"name":"WaitingInAction"},"items":[]}],"page":0,"totalPages":1}""";

        var claim = Assert.Single(TrendyolJsonMapper.Returns(json).Items);

        Assert.Null(claim.LastModifiedAt);
        Assert.Null(claim.ActionDueAt);
    }

    [Fact]
    public void ClaimWithoutAnyDateIsNotProjectedAsAnOrderOf1970()
    {
        const string trendyol = """{"claimId":"c1","orderNumber":"1","items":[]}""";
        const string hepsiburada = """{"claimNumber":"c1","orderNumber":"1","quantity":1}""";

        Assert.Null(TrendyolJsonMapper.OrderFromReturnClaim(trendyol));
        Assert.Null(HepsiburadaJsonMapper.OrderFromReturnClaim(hepsiburada));
    }

    [Fact]
    public void UndatedAnswersAreDeduplicatedAndSortedLast()
    {
        var at = DateTimeOffset.Parse("2026-10-05T22:09:00Z");
        var history = MarketplaceQuestionHistory.Deduplicate(
        [
            new RemoteQuestionConversation("Merchant", "A", null),
            new RemoteQuestionConversation("Merchant", "A", null),
            new RemoteQuestionConversation("Customer", "B", at)
        ]);

        Assert.Equal(2, history.Count);
        Assert.Equal(at, history[0].CreatedAt);
        Assert.Null(history[1].CreatedAt);
    }
}
