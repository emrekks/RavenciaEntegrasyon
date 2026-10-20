using System.Globalization;
using System.Text;
using MarketplaceHub.Application;

namespace MarketplaceHub.Infrastructure.Persistence;

internal static class MarketplaceQuestionHistory
{
    // A sent answer is stored once at the local send time and read back later
    // with the marketplace's own time; within this window the same author and
    // text is the same message.
    public static readonly TimeSpan DuplicateWindow = TimeSpan.FromHours(24);

    public static IReadOnlyList<RemoteQuestionConversation> Deduplicate(IEnumerable<RemoteQuestionConversation> messages)
    {
        var rows = new List<(string Author, string Text, RemoteQuestionConversation Message)>();

        foreach (var message in messages)
        {
            var author = NormalizeAuthor(message.Author);
            var text = NormalizeText(message.Text);
            var createdAt = message.CreatedAt.ToUniversalTime();
            // A rejected answer and a later resend of the same text are two
            // messages; only an unrejected copy absorbs a near-time duplicate.
            var index = rows.FindIndex(row => row.Author == author && row.Text == text
                && (row.Message.CreatedAt.ToUniversalTime() == createdAt
                    || string.IsNullOrWhiteSpace(row.Message.RejectionReason) && (row.Message.CreatedAt - message.CreatedAt).Duration() <= DuplicateWindow));
            if (index < 0)
            {
                rows.Add((author, text, message));
                continue;
            }

            if (string.IsNullOrWhiteSpace(rows[index].Message.RejectionReason) && !string.IsNullOrWhiteSpace(message.RejectionReason))
                rows[index] = (author, text, message);
        }

        return rows.Select(row => row.Message).OrderBy(message => message.CreatedAt).ToArray();
    }

    private static string NormalizeAuthor(string value)
    {
        // The dotless ı of "Satıcı" has no invariant upper case; fold it first.
        var decomposed = value.Trim().Replace('ı', 'i').Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var character in decomposed)
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
                builder.Append(char.ToUpperInvariant(character));
        var normalized = builder.ToString();
        if (normalized.Contains("MERCHANT", StringComparison.Ordinal) || normalized.Contains("SELLER", StringComparison.Ordinal) || normalized.Contains("SATICI", StringComparison.Ordinal)) return "SELLER";
        if (normalized.Contains("CUSTOMER", StringComparison.Ordinal) || normalized.Contains("BUYER", StringComparison.Ordinal) || normalized.Contains("MUSTERI", StringComparison.Ordinal)) return "CUSTOMER";
        return normalized;
    }

    private static string NormalizeText(string value) => string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
