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

    [Fact]
    public void A_resent_answer_is_not_rejected_by_the_rejection_of_the_earlier_one()
    {
        var sentAgain = CreatedAt.AddHours(2);
        var staleRejection = Question("REJECTED", [new("Merchant", "Merhaba", CreatedAt, "Cevap reddedildi")]);
        var sameTextRejectedBefore = Question("WAITING_FOR_ANSWER", [new("Merchant", "Merhaba", CreatedAt, "Cevap reddedildi")]);
        var freshRejection = Question("REJECTED", [new("Merchant", "Merhaba", sentAgain.AddMinutes(3), "Cevap reddedildi")]);

        Assert.Equal(QuestionAnswerSubmissionReconciliation.Pending, MarketplaceQuestionAnswerReconciliationPolicy.Evaluate(staleRejection, "Merhaba", sentAgain));
        Assert.Equal(QuestionAnswerSubmissionReconciliation.Pending, MarketplaceQuestionAnswerReconciliationPolicy.Evaluate(sameTextRejectedBefore, "Merhaba", sentAgain));
        Assert.Equal(QuestionAnswerSubmissionReconciliation.Rejected, MarketplaceQuestionAnswerReconciliationPolicy.Evaluate(freshRejection, "Merhaba", sentAgain));
    }

    [Fact]
    public void Without_merchant_messages_the_question_change_dates_the_rejection()
    {
        var sent = CreatedAt.AddHours(-1);

        Assert.Equal(QuestionAnswerSubmissionReconciliation.Rejected, MarketplaceQuestionAnswerReconciliationPolicy.Evaluate(Question("REJECTED", []), null, sent));
        Assert.Equal(QuestionAnswerSubmissionReconciliation.Pending, MarketplaceQuestionAnswerReconciliationPolicy.Evaluate(Question("REJECTED", []), null, CreatedAt.AddHours(1)));
    }

    [Fact]
    public void An_answer_held_for_approval_has_arrived()
    {
        Assert.Equal(QuestionAnswerSubmissionReconciliation.Submitted, MarketplaceQuestionAnswerReconciliationPolicy.Evaluate(Question("WAITING_FOR_APPROVE", []), "Merhaba"));
    }

    [Fact]
    public void An_uncertain_answer_still_missing_after_twenty_minutes_was_not_delivered_and_keeps_its_draft()
    {
        var sent = CreatedAt;
        var row = UncertainRow(sent);

        MarketplaceQuestionService.ReconcileAnswerSubmission(row, Question("WAITING_FOR_ANSWER", []), sent.AddMinutes(10));
        Assert.Equal("UNKNOWN", row.AnswerSubmissionStatus);
        Assert.Equal("ANSWER_SUBMITTED", row.Status);

        MarketplaceQuestionService.ReconcileAnswerSubmission(row, Question("WAITING_FOR_ANSWER", []), sent.AddMinutes(21));
        Assert.Equal("NOT_DELIVERED", row.AnswerSubmissionStatus);
        Assert.Equal("WAITING_FOR_ANSWER", row.Status);
        Assert.Equal("Merhaba", row.PendingAnswerText);
    }

    [Fact]
    public void An_answer_held_for_approval_shows_as_waiting_for_approval()
    {
        var row = UncertainRow(CreatedAt);

        MarketplaceQuestionService.ReconcileAnswerSubmission(row, Question("WAITING_FOR_APPROVE", []), CreatedAt.AddMinutes(1));

        Assert.Equal("SUBMITTED", row.AnswerSubmissionStatus);
        Assert.Equal("WAITING_FOR_APPROVE", row.Status);
    }

    [Fact]
    public void The_submission_time_rides_on_the_key_and_old_keys_count_as_long_ago()
    {
        var stamped = QuestionAnswerSubmission.Stamp(new string('k', 300), CreatedAt);

        Assert.True(stamped.Length <= 256);
        Assert.Equal(CreatedAt, QuestionAnswerSubmission.SubmittedAt(stamped));
        Assert.Null(QuestionAnswerSubmission.SubmittedAt("question-answer:abc"));
        Assert.True(QuestionAnswerSubmission.NotDelivered(null, CreatedAt));
        Assert.False(QuestionAnswerSubmission.NotDelivered(CreatedAt, CreatedAt.AddMinutes(19)));
    }

    [Fact]
    public void Only_a_connection_that_never_opened_proves_an_answer_was_not_sent()
    {
        Assert.True(MarketplaceQuestionService.NeverReachedMarketplace(new HttpRequestException(HttpRequestError.ConnectionError)));
        Assert.True(MarketplaceQuestionService.NeverReachedMarketplace(new HttpRequestException(HttpRequestError.NameResolutionError)));
        Assert.False(MarketplaceQuestionService.NeverReachedMarketplace(new HttpRequestException(HttpRequestError.ResponseEnded)));
        Assert.False(MarketplaceQuestionService.NeverReachedMarketplace(new TaskCanceledException()));
    }

    [Fact]
    public void Rejected_questions_wait_for_a_new_answer_and_approval_counts_as_answered()
    {
        Assert.Equal(["WAITING_FOR_ANSWER", "REJECTED"], MarketplaceQuestionService.StatusesOf("waiting"));
        Assert.Contains("WAITING_FOR_APPROVE", MarketplaceQuestionService.StatusesOf("ANSWERED"));
        Assert.DoesNotContain("REJECTED", MarketplaceQuestionService.StatusesOf("OTHER"));
        Assert.Equal(["EXPIRED"], MarketplaceQuestionService.StatusesOf("EXPIRED")[..1]);
    }

    private static MarketplaceHub.Domain.MarketplaceQuestion UncertainRow(DateTimeOffset sent) => new()
    {
        ExternalQuestionId = "external-1",
        Kind = "PRODUCT",
        Status = "ANSWER_SUBMITTED",
        QuestionText = "Soru",
        PendingAnswerText = "Merhaba",
        AnswerSubmissionStatus = "UNKNOWN",
        AnswerSubmissionKey = QuestionAnswerSubmission.Stamp("question-answer:1", sent)
    };

    [Theory]
    [InlineData("TRENDYOL", "Kısa", "QUESTION_ANSWER_LENGTH")]
    [InlineData("HEPSIBURADA", "Evet", null)]
    [InlineData("TRENDYOL", "Detaylar için https://ornek.com adresine bakın.", "QUESTION_ANSWER_CONTENT")]
    [InlineData("N11", "Bize destek@ornek.com adresinden yazın.", "QUESTION_ANSWER_CONTENT")]
    [InlineData("PAZARAMA", "Bizi 0532 123 45 67 numarasından arayın.", "QUESTION_ANSWER_CONTENT")]
    [InlineData("TRENDYOL", "Bizi 5321234567 numarasından arayın.", "QUESTION_ANSWER_CONTENT")]
    [InlineData("TRENDYOL", "Merkezimizi 444 1 234 numarasından arayın.", "QUESTION_ANSWER_CONTENT")]
    [InlineData("TRENDYOL", "11697863851 numaralı siparişiniz yarın kargoda.", null)]
    [InlineData("HEPSIBURADA", "5000031618 numaralı paketiniz yarın kargoda.", null)]
    [InlineData("HEPSIBURADA", "Bizi 0500 003 16 18 numarasından arayın.", "QUESTION_ANSWER_CONTENT")]
    [InlineData("TRENDYOL", "Ürünün ölçüsü 42 x 30 cm, ağırlığı 1,5 kg.", null)]
    public void AnswerPolicyChecksLengthAndContactDetails(string platform, string answer, string? expectedCode) =>
        Assert.Equal(expectedCode, QuestionAnswerPolicy.Validate(platform, answer)?.Code);

    private static RemoteMarketplaceQuestion Question(string status, IReadOnlyList<RemoteQuestionConversation> conversations) =>
        new("external-1", "PRODUCT", status, "Soru", null, null, null, null, null, null, null, CreatedAt, null, CreatedAt, conversations);
}
