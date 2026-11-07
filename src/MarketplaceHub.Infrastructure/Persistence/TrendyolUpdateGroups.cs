using System.Text.Json;
using System.Text.Json.Nodes;
using MarketplaceHub.Application;

namespace MarketplaceHub.Infrastructure.Persistence;

// The grouped Trendyol send of "Platformlarda Güncelle" (owner, 7 Nov 2026: RYP003T's six colour cards went in six
// content requests). The cards one action sends to one Trendyol connection carry the same SendGroupId; each card keeps
// its own job. A card that reaches a SUBMIT phase marks itself ready and waits a short while for the group's cards still
// on their way there; the last one ready (or the first after the wait) sends the phase for every ready card in one
// request (content-bulk-update, variant-bulk-update, delivery-info-bulk-update and the unapproved update take up to
// 1,000 items), polls the one batch id once and gives each card its own answer:
//  - a content line is the card whose contentId it names; a refused content stops only that card, and a refused line
//    no card's content names stops every card of the request (unknown stays unknown);
//  - a variant, delivery or unapproved line is the card whose barcode it names, as for a card sent alone;
//  - each card's effect record is written before the request, so after an unclear answer no card of the request is sent
//    again: each card's own run finds its record and stops for review.
// The pure parts (who waits, who goes together, the merged request, which lines are whose) live here.
internal static class TrendyolUpdateGroups
{
    public const int MaxItemsPerRequest = 1000;
    // How long a ready card waits for the group's cards still on their way to its phase. The cards of one send run one
    // after another under the connection's product lock, each after its own content lookups: a few seconds each.
    public static readonly TimeSpan ReadyWait = TimeSpan.FromMinutes(2);
    public const string WaitingCode = "PRODUCT_UPDATE_GROUP_PENDING";
    // The polling card answered this card: the next run completes it (DonePhase) or stops it with GroupOutcome.
    public const string DonePhase = "GROUP_DONE";
    public const string StoppedPhase = "GROUP_STOPPED";

    // SUBMIT_CONTENT and POLL_CONTENT → CONTENT.
    public static string Part(string phase) => phase.Replace("SUBMIT_", "", StringComparison.Ordinal).Replace("POLL_", "", StringComparison.Ordinal);

    // Where a phase stands in the update: the unapproved request and the content go first, then the variants, then the
    // delivery; a card the polling card answered is past them all.
    public static int Order(string phase) => Part(phase.Trim().ToUpperInvariant()) switch
    {
        "UNAPPROVED" or "CONTENT" => 1,
        "VARIANTS" => 2,
        "DELIVERY" => 3,
        DonePhase or StoppedPhase => 4,
        _ => 0
    };

    public static bool IsReady(ProductUpdateJobPayload card, string phase) =>
        string.Equals(card.Phase, phase, StringComparison.Ordinal) && card.GroupReadySince is not null;

    // A card still on its way to this phase: at an earlier one, or at this one but not ready yet (its content lookups
    // have not run).
    public static bool IsBehind(ProductUpdateJobPayload card, string phase) =>
        Order(card.Phase) < Order(phase) || string.Equals(card.Phase, phase, StringComparison.Ordinal) && card.GroupReadySince is null;

    // The cards that go in one request, the first (the sending card) always among them. A card waits for a later request
    // when one of its items names a content or barcode an earlier card's does (Trendyol takes one item per content), when
    // its request differs from the first's outside the item list, or when it would take the request past 1,000 items.
    public static IReadOnlyList<T> Select<T>(IReadOnlyList<(T Card, string PartJson)> cards)
    {
        var selected = new List<T>();
        if (cards.Count == 0) return selected;
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? envelope = null;
        var items = 0;
        foreach (var (card, json) in cards)
        {
            var parsed = Parse(json);
            if (parsed is null) { if (selected.Count == 0) return selected; continue; }
            var keys = Keys(parsed.Value.Items);
            var first = selected.Count == 0;
            if (!first && (parsed.Value.Envelope != envelope || items + parsed.Value.Items.Count > MaxItemsPerRequest || keys.Any(taken.Contains))) continue;
            envelope ??= parsed.Value.Envelope;
            items += parsed.Value.Items.Count;
            foreach (var key in keys) taken.Add(key);
            selected.Add(card);
        }
        return selected;
    }

    // One request of every card's items, in the cards' order; the rest of the request is the first card's.
    public static string Merge(IEnumerable<string> partJsons)
    {
        JsonObject? root = null;
        var items = new JsonArray();
        foreach (var json in partJsons)
        {
            if (JsonNode.Parse(json) is not JsonObject value || value["items"] is not JsonArray cardItems)
                throw new InvalidOperationException("A grouped Trendyol request part is not an item list.");
            root ??= value;
            foreach (var item in cardItems.ToList())
            {
                cardItems.Remove(item);
                items.Add(item);
            }
        }
        if (root is null) throw new InvalidOperationException("A grouped Trendyol request has no part.");
        root["items"] = items;
        return root.ToJsonString();
    }

    public static int ItemCount(string partJson) => Parse(partJson)?.Items.Count ?? 0;

    // What names an item in Trendyol's batch answer: its contentId (a content request), else its barcode.
    public static IReadOnlyList<string> Keys(string partJson) => Parse(partJson) is { } parsed ? Keys(parsed.Items) : [];

    // The batch lines of each card (by index), and the lines no card's items name.
    public static (IReadOnlyList<IReadOnlyList<RemoteOperationLine>> ByCard, IReadOnlyList<RemoteOperationLine> Unattributed) Assign(IReadOnlyList<IReadOnlyCollection<string>> cardKeys, IEnumerable<RemoteOperationLine> lines)
    {
        var byCard = cardKeys.Select(_ => new List<RemoteOperationLine>()).ToList();
        var unattributed = new List<RemoteOperationLine>();
        foreach (var line in lines)
        {
            var key = line.ExternalKey?.Trim();
            var owner = string.IsNullOrWhiteSpace(key) ? -1 : cardKeys.ToList().FindIndex(keys => keys.Contains(key, StringComparer.OrdinalIgnoreCase));
            if (owner < 0) unattributed.Add(line);
            else byCard[owner].Add(line);
        }
        return (byCard, unattributed);
    }

    public static JobExecutionResult Result(ProductUpdateGroupOutcome outcome) => outcome.Kind switch
    {
        "MANUAL_REVIEW" => JobExecutionResult.ManualReview(outcome.Code, outcome.Message, outcome.RemoteRequestId),
        "DEAD" => JobExecutionResult.Dead(outcome.Code, outcome.Message, outcome.RemoteRequestId),
        _ => JobExecutionResult.Blocked(outcome.Code, outcome.Message, outcome.RemoteRequestId)
    };

    public static ProductUpdateGroupOutcome Outcome(JobExecutionResult result, string profileStatus, string? rejectionCode) => new(
        result.Kind switch { JobCompletionKind.ManualReview => "MANUAL_REVIEW", JobCompletionKind.Dead => "DEAD", _ => "BLOCKED" },
        profileStatus, rejectionCode, result.ErrorCode ?? "PRODUCT_UPDATE_FAILED", result.ErrorSummary ?? "Ürün güncellemesi tamamlanamadı.", result.RemoteRequestId);

    private static IReadOnlyList<string> Keys(JsonArray items) => items
        .Select(item => item as JsonObject)
        .Select(item => item?["contentId"] is JsonValue contentId ? contentId.ToJsonString().Trim('"') : item?["barcode"] is JsonValue barcode ? barcode.ToJsonString().Trim('"') : null)
        .Where(key => !string.IsNullOrWhiteSpace(key))
        .Select(key => key!.Trim())
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    // The request's items and the rest of it (compared between cards), or null when it is not an item list.
    private static (JsonArray Items, string Envelope)? Parse(string partJson)
    {
        try
        {
            if (JsonNode.Parse(partJson) is not JsonObject root || root["items"] is not JsonArray items) return null;
            var rest = new JsonObject();
            foreach (var (name, value) in root)
                if (name != "items") rest[name] = value?.DeepClone();
            return (items, rest.ToJsonString());
        }
        catch (JsonException) { return null; }
    }
}
