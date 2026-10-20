using MarketplaceHub.Application;
using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class MarketplaceQuestionAnswerReconciliationPolicyTests
{
    private static readonly DateTimeOffset CreatedAt = DateTimeOffset.Parse("2026-10-06T10:00:00Z");

    [Fact]
    public void AnsweredRemoteQuestionConfirmsAnUncertainSubmission()
    {
        var remote = Question("ANSWERED", []);

        Assert.Equal(
            QuestionAnswerSubmissionReconciliation.Confirmed,
            MarketplaceQuestionAnswerReconciliationPolicy.Evaluate(remote, "Merhaba"));
    }

    [Theory]
    [InlineData("Merchant")]
    [InlineData("Seller")]
    [InlineData("Satıcı")]
    public void MatchingMerchantConversationConfirmsSubmissionWasRecorded(string author)
    {
        var remote = Question("WAITING_FOR_ANSWER", [new(author, "Merhaba", CreatedAt)]);

        Assert.Equal(
            QuestionAnswerSubmissionReconciliation.Submitted,
            MarketplaceQuestionAnswerReconciliationPolicy.Evaluate(remote, " Merhaba "));
    }

    [Fact]
    public void CustomerMessageWithSameTextDoesNotResolveUncertainSubmission()
    {
        var remote = Question("WAITING_FOR_ANSWER", [new("Customer", "Merhaba", CreatedAt)]);

        Assert.Equal(
            QuestionAnswerSubmissionReconciliation.Pending,
            MarketplaceQuestionAnswerReconciliationPolicy.Evaluate(remote, "Merhaba"));
    }

    [Fact]
    public void RejectedMerchantConversationEndsTheSubmission()
    {
        var remote = Question("WAITING_FOR_ANSWER", [new("Merchant", "Merhaba", CreatedAt, "Cevap reddedildi")]);

        Assert.Equal(
            QuestionAnswerSubmissionReconciliation.Rejected,
            MarketplaceQuestionAnswerReconciliationPolicy.Evaluate(remote, "Merhaba"));
    }

    [Fact]
    public void RejectedQuestionStatusEndsTheSubmissionEvenWithoutPendingText()
    {
        var remote = Question("REJECTED", []);

        Assert.Equal(
            QuestionAnswerSubmissionReconciliation.Rejected,
            MarketplaceQuestionAnswerReconciliationPolicy.Evaluate(remote, null));
    }

    [Fact]
    public void AnUnrejectedCopyOfTheAnswerStillCountsAsSubmitted()
    {
        var remote = Question("WAITING_FOR_ANSWER", [new("Merchant", "Merhaba", CreatedAt, "Cevap reddedildi"), new("Merchant", "Merhaba", CreatedAt.AddMinutes(5))]);

        Assert.Equal(
            QuestionAnswerSubmissionReconciliation.Submitted,
            MarketplaceQuestionAnswerReconciliationPolicy.Evaluate(remote, "Merhaba"));
    }

    [Theory]
    [InlineData("TRENDYOL", "Kısa", "QUESTION_ANSWER_LENGTH")]
    [InlineData("HEPSIBURADA", "Evet", null)]
    [InlineData("TRENDYOL", "Detaylar için https://ornek.com adresine bakın.", "QUESTION_ANSWER_CONTENT")]
    [InlineData("N11", "Bize destek@ornek.com adresinden yazın.", "QUESTION_ANSWER_CONTENT")]
    [InlineData("PAZARAMA", "Bizi 0532 123 45 67 numarasından arayın.", "QUESTION_ANSWER_CONTENT")]
    [InlineData("TRENDYOL", "Bizi 5321234567 numarasından arayın.", "QUESTION_ANSWER_CONTENT")]
    [InlineData("TRENDYOL", "Merkezimizi 444 1 234 numarasından arayın.", "QUESTION_ANSWER_CONTENT")]
    [InlineData("TRENDYOL", "11697863851 numaralı siparişiniz yarın kargoda.", null)]
    [InlineData("TRENDYOL", "Ürünün ölçüsü 42 x 30 cm, ağırlığı 1,5 kg.", null)]
    public void AnswerPolicyChecksLengthAndContactDetails(string platform, string answer, string? expectedCode) =>
        Assert.Equal(expectedCode, QuestionAnswerPolicy.Validate(platform, answer)?.Code);

    private static RemoteMarketplaceQuestion Question(string status, IReadOnlyList<RemoteQuestionConversation> conversations) =>
        new("external-1", "PRODUCT", status, "Soru", null, null, null, null, null, null, null, CreatedAt, null, CreatedAt, conversations);
}
