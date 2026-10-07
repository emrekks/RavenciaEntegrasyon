using MarketplaceHub.Application;

namespace MarketplaceHub.Infrastructure.Persistence;

internal enum QuestionAnswerSubmissionReconciliation
{
    Pending,
    Submitted,
    Confirmed
}

internal static class MarketplaceQuestionAnswerReconciliationPolicy
{
    public static QuestionAnswerSubmissionReconciliation Evaluate(RemoteMarketplaceQuestion remote, string? pendingAnswer)
    {
        if (remote.Status.Equals("ANSWERED", StringComparison.OrdinalIgnoreCase))
            return QuestionAnswerSubmissionReconciliation.Confirmed;

        if (!remote.Status.Equals("WAITING_FOR_ANSWER", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(pendingAnswer))
            return QuestionAnswerSubmissionReconciliation.Pending;

        var matchingMerchantAnswer = remote.Conversations.Any(conversation =>
            IsMerchant(conversation.Author)
            && string.IsNullOrWhiteSpace(conversation.RejectionReason)
            && string.Equals(conversation.Text.Trim(), pendingAnswer.Trim(), StringComparison.Ordinal));

        return matchingMerchantAnswer
            ? QuestionAnswerSubmissionReconciliation.Submitted
            : QuestionAnswerSubmissionReconciliation.Pending;
    }

    private static bool IsMerchant(string author)
    {
        var value = author.Trim();
        return value.Equals("Merchant", StringComparison.OrdinalIgnoreCase)
            || value.Equals("Seller", StringComparison.OrdinalIgnoreCase)
            || value.Equals("Satıcı", StringComparison.OrdinalIgnoreCase)
            || value.Equals("SATICI", StringComparison.OrdinalIgnoreCase)
            || value.Equals("Mağaza", StringComparison.OrdinalIgnoreCase);
    }
}
