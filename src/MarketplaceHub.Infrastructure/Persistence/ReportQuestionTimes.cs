using System.Text.Json;
using MarketplaceHub.Application;

namespace MarketplaceHub.Infrastructure.Persistence;

// The answer time of each question from its stored conversation (MarketplaceQuestion.HistoryJson: an array of
// { Text, Author "Customer" | "Merchant", CreatedAt, RejectionReason }): from the customer's first message to the
// store's first message after it. A question asked outside the period, a conversation that does not read, and an answer
// timed before the question or more than 30 days after it are left out, never guessed.
internal static class ReportQuestionTimes
{
    private static readonly TimeSpan Longest = TimeSpan.FromDays(30);

    public static ReportQuestionTimeView Build(IEnumerable<string> histories, DateTimeOffset start, DateTimeOffset end, IReadOnlyList<string> platforms)
    {
        var hours = new List<double>();
        var waiting = 0;
        foreach (var history in histories)
        {
            if (Read(history) is not { } messages) continue;
            var asked = messages.Where(x => x.Customer && x.At is not null).Select(x => x.At!.Value).DefaultIfEmpty().Min();
            if (asked == default || asked < start || asked >= end) continue;
            var answered = messages.Where(x => !x.Customer && x.At is { } at && at >= asked).Select(x => x.At!.Value).DefaultIfEmpty().Min();
            if (answered == default) { if (!messages.Any(x => !x.Customer)) waiting++; continue; }
            if (answered - asked > Longest) continue;
            hours.Add((answered - asked).TotalHours);
        }
        hours.Sort();
        decimal? median = hours.Count == 0 ? null : Math.Round((decimal)(hours.Count % 2 == 1 ? hours[hours.Count / 2] : (hours[hours.Count / 2 - 1] + hours[hours.Count / 2]) / 2), 1);
        return new(hours.Count, waiting, hours.Count == 0 ? null : Math.Round((decimal)hours.Average(), 1), median, platforms);
    }

    private static List<(bool Customer, DateTimeOffset? At)>? Read(string history)
    {
        try
        {
            using var document = JsonDocument.Parse(history);
            if (document.RootElement.ValueKind != JsonValueKind.Array) return null;
            var result = new List<(bool, DateTimeOffset?)>();
            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                string? author = null;
                DateTimeOffset? at = null;
                foreach (var property in item.EnumerateObject())
                {
                    if (property.NameEquals("Author") || property.NameEquals("author")) author = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : null;
                    else if ((property.NameEquals("CreatedAt") || property.NameEquals("createdAt")) && property.Value.ValueKind == JsonValueKind.String && property.Value.TryGetDateTimeOffset(out var value)) at = value;
                }
                if (author is "Customer" or "Merchant") result.Add((author == "Customer", at));
            }
            return result;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
