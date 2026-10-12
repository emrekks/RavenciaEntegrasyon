namespace MarketplaceHub.Application;

public enum CategoryReuseDecision
{
    CreateNew,
    ReuseExisting,
    CreateSeparate,
    NeedsReview
}

public sealed record CategoryReuseCandidate(Guid CategoryId, string Name, IReadOnlySet<string> AttributeKeys);

public sealed record CategoryReuseResult(CategoryReuseDecision Decision, Guid? CategoryId, string PanelName, string Reason);

public static class CategoryReusePolicy
{
    // A marketplace category is bound to an existing panel category with the same
    // leaf name only when both expose exactly the same attribute set. Different
    // attribute sets (for example Hepsiburada "Bluz" vs a Trendyol "Bluz") stay
    // separate panel categories. Anything that cannot be decided safely goes to a
    // review list instead of being guessed.
    public static CategoryReuseResult Decide(
        string leafName,
        string platformLabel,
        IReadOnlySet<string> remoteAttributeKeys,
        IReadOnlyList<CategoryReuseCandidate> sameLeafCandidates)
    {
        if (sameLeafCandidates.Count == 0)
            return new(CategoryReuseDecision.CreateNew, null, leafName, "Panelde aynı adlı kategori yok.");
        if (remoteAttributeKeys.Count == 0)
            return new(CategoryReuseDecision.NeedsReview, null, leafName, "Pazar yeri kategorisinin özellikleri alınamadı; aynı adlı panel kategorisiyle karşılaştırılamıyor.");

        // A panel category can carry several known attribute sets (one per linked
        // marketplace); it matches when any of them is identical.
        var equal = sameLeafCandidates
            .Where(candidate => candidate.AttributeKeys.SetEquals(remoteAttributeKeys))
            .GroupBy(candidate => candidate.CategoryId)
            .Select(group => group.First())
            .ToList();
        if (equal.Count == 1)
            return new(CategoryReuseDecision.ReuseExisting, equal[0].CategoryId, equal[0].Name, "Kategori özellikleri birebir aynı.");
        if (equal.Count > 1)
            return new(CategoryReuseDecision.NeedsReview, null, leafName, "Özellikleri aynı olan birden çok panel kategorisi var.");

        var suffixed = $"{leafName} ({platformLabel})";
        return new(CategoryReuseDecision.CreateSeparate, null, suffixed, "Aynı adlı panel kategorisinin özellikleri farklı; ayrı kategori açılacak.");
    }
}

public sealed record CatalogStructureRunView(
    Guid Id,
    Guid ConnectionId,
    Guid? JobId,
    string Mode,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt,
    string? ErrorSummary,
    System.Text.Json.JsonElement? Report);
