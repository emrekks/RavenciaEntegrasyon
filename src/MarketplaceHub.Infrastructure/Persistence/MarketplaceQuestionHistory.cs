using System.Globalization;
using System.Text;
using MarketplaceHub.Application;

namespace MarketplaceHub.Infrastructure.Persistence;

internal static class MarketplaceQuestionHistory
{
    public static IReadOnlyList<RemoteQuestionConversation> Deduplicate(IEnumerable<RemoteQuestionConversation> messages)
    {
        var rows = new List<RemoteQuestionConversation>();
        var positions = new Dictionary<(string Author, DateTimeOffset CreatedAt, string Text), int>();

        foreach (var message in messages)
        {
            var key = (NormalizeAuthor(message.Author), message.CreatedAt.ToUniversalTime(), NormalizeText(message.Text));
            if (!positions.TryGetValue(key, out var index))
            {
                positions.Add(key, rows.Count);
                rows.Add(message);
                continue;
            }

            if (string.IsNullOrWhiteSpace(rows[index].RejectionReason) && !string.IsNullOrWhiteSpace(message.RejectionReason))
                rows[index] = message;
        }

        return rows.OrderBy(message => message.CreatedAt).ToArray();
    }

    private static string NormalizeAuthor(string value)
    {
        var decomposed = value.Trim().Normalize(NormalizationForm.FormD);
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
