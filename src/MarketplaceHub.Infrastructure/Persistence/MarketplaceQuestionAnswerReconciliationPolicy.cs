using MarketplaceHub.Application;

namespace MarketplaceHub.Infrastructure.Persistence;

internal enum QuestionAnswerSubmissionReconciliation
{
    Pending,
    Submitted,
    Confirmed,
    Rejected
}

internal static class MarketplaceQuestionAnswerReconciliationPolicy
{
    public static QuestionAnswerSubmissionReconciliation Evaluate(RemoteMarketplaceQuestion remote, string? pendingAnswer)
    {
        if (remote.Status.Equals("ANSWERED", StringComparison.OrdinalIgnoreCase))
            return QuestionAnswerSubmissionReconciliation.Confirmed;

        // The marketplace refused the answer that was sent: the submission is
        // over and the seller may answer again.
        if (remote.Status.Equals("REJECTED", StringComparison.OrdinalIgnoreCase))
            return QuestionAnswerSubmissionReconciliation.Rejected;

        if (!remote.Status.Equals("WAITING_FOR_ANSWER", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(pendingAnswer))
            return QuestionAnswerSubmissionReconciliation.Pending;

        var matchingMerchantAnswers = remote.Conversations
            .Where(conversation => IsMerchant(conversation.Author)
                && string.Equals(conversation.Text.Trim(), pendingAnswer.Trim(), StringComparison.Ordinal))
            .ToList();

        if (matchingMerchantAnswers.Any(conversation => string.IsNullOrWhiteSpace(conversation.RejectionReason)))
            return QuestionAnswerSubmissionReconciliation.Submitted;
        return matchingMerchantAnswers.Count > 0
            ? QuestionAnswerSubmissionReconciliation.Rejected
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
