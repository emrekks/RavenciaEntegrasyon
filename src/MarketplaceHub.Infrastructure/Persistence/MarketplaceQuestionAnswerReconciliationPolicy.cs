using System.Globalization;
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
    // Marketplace and server clocks differ a little; a rejection this much older than the submission still counts.
    internal static readonly TimeSpan ClockTolerance = TimeSpan.FromMinutes(1);

    public static QuestionAnswerSubmissionReconciliation Evaluate(RemoteMarketplaceQuestion remote, string? pendingAnswer, DateTimeOffset? submittedAt = null)
    {
        if (remote.Status.Equals("ANSWERED", StringComparison.OrdinalIgnoreCase))
            return QuestionAnswerSubmissionReconciliation.Confirmed;

        // The marketplace holds the answer for its approval: it arrived.
        if (remote.Status.Equals("WAITING_FOR_APPROVE", StringComparison.OrdinalIgnoreCase))
            return QuestionAnswerSubmissionReconciliation.Submitted;

        // The marketplace refused an answer. When the answer was resent after an earlier rejection, the question
        // still reads REJECTED until the marketplace looks at the new one: only a rejection newer than this
        // submission ends it. The rejected merchant message dates the rejection; a marketplace that lists no
        // merchant message is judged by the question's last change instead.
        if (remote.Status.Equals("REJECTED", StringComparison.OrdinalIgnoreCase))
        {
            if (submittedAt is not { } sent) return QuestionAnswerSubmissionReconciliation.Rejected;
            var merchantMessages = remote.Conversations.Where(conversation => IsMerchant(conversation.Author)).ToList();
            var rejectedNow = merchantMessages.Count == 0
                ? remote.LastModifiedAt >= sent - ClockTolerance
                : merchantMessages.Any(conversation => !string.IsNullOrWhiteSpace(conversation.RejectionReason) && conversation.CreatedAt >= sent - ClockTolerance);
            return rejectedNow ? QuestionAnswerSubmissionReconciliation.Rejected : QuestionAnswerSubmissionReconciliation.Pending;
        }

        if (!remote.Status.Equals("WAITING_FOR_ANSWER", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(pendingAnswer))
            return QuestionAnswerSubmissionReconciliation.Pending;

        var matchingMerchantAnswers = remote.Conversations
            .Where(conversation => IsMerchant(conversation.Author)
                && string.Equals(conversation.Text.Trim(), pendingAnswer.Trim(), StringComparison.Ordinal))
            .ToList();

        if (matchingMerchantAnswers.Any(conversation => string.IsNullOrWhiteSpace(conversation.RejectionReason)))
            return QuestionAnswerSubmissionReconciliation.Submitted;
        // The same text rejected before this submission is the earlier attempt, not this one.
        return matchingMerchantAnswers.Any(conversation => submittedAt is not { } sent || conversation.CreatedAt >= sent - ClockTolerance)
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

// When an answer was sent. The question table has no column for it yet (schema change listed for the owner), so
// the time rides on the submission key as "{key}|at={unix ms}"; a key without it is an older submission.
internal static class QuestionAnswerSubmission
{
    private const string Marker = "|at=";
    private const int KeyColumnLength = 256;

    // An answer whose fate stayed unknown and that the marketplace still does not show after this long was not
    // delivered: the question opens again and the draft is kept.
    public static readonly TimeSpan NotDeliveredAfter = TimeSpan.FromMinutes(20);

    public static string Stamp(string idempotencyKey, DateTimeOffset submittedAt)
    {
        var suffix = $"{Marker}{submittedAt.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)}";
        var key = idempotencyKey.Length + suffix.Length > KeyColumnLength ? idempotencyKey[..(KeyColumnLength - suffix.Length)] : idempotencyKey;
        return key + suffix;
    }

    public static DateTimeOffset? SubmittedAt(string? stampedKey)
    {
        if (string.IsNullOrEmpty(stampedKey)) return null;
        var index = stampedKey.LastIndexOf(Marker, StringComparison.Ordinal);
        return index >= 0 && long.TryParse(stampedKey.AsSpan(index + Marker.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var milliseconds)
            ? DateTimeOffset.FromUnixTimeMilliseconds(milliseconds)
            : null;
    }

    // Without a recorded time (submissions from before the stamp) the answer has waited long enough.
    public static bool NotDelivered(DateTimeOffset? submittedAt, DateTimeOffset now) =>
        submittedAt is not { } sent || now - sent >= NotDeliveredAfter;
}
