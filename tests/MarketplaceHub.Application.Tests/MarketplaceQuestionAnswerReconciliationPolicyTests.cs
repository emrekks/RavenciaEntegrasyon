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
    public void RejectedMerchantConversationDoesNotResolveUncertainSubmission()
    {
        var remote = Question("WAITING_FOR_ANSWER", [new("Merchant", "Merhaba", CreatedAt, "Cevap reddedildi")]);

        Assert.Equal(
            QuestionAnswerSubmissionReconciliation.Pending,
            MarketplaceQuestionAnswerReconciliationPolicy.Evaluate(remote, "Merhaba"));
    }

    private static RemoteMarketplaceQuestion Question(string status, IReadOnlyList<RemoteQuestionConversation> conversations) =>
        new("external-1", "PRODUCT", status, "Soru", null, null, null, null, null, null, null, CreatedAt, null, CreatedAt, conversations);
}
