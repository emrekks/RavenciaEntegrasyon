namespace MarketplaceHub.Api.Catalog;

public static class ProductFamilyMediaOrdering
{
    public static List<T> OrderByRequestedKeys<T>(IReadOnlyList<T> items, IEnumerable<string> requestedKeys, Func<T, string> keySelector)
    {
        var groupsByKey = items.ToDictionary(keySelector, StringComparer.OrdinalIgnoreCase);
        var ordered = new List<T>(items.Count);
        var added = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in requestedKeys)
            if (added.Add(key) && groupsByKey.TryGetValue(key, out var item)) ordered.Add(item);
        foreach (var item in items)
            if (added.Add(keySelector(item))) ordered.Add(item);
        return ordered;
    }

    public static bool Move<T>(List<T> items, int sourceIndex, int targetIndex)
    {
        if (sourceIndex < 0 || targetIndex < 0 || sourceIndex >= items.Count || targetIndex >= items.Count || sourceIndex == targetIndex)
            return false;

        var moved = items[sourceIndex];
        items.RemoveAt(sourceIndex);
        items.Insert(targetIndex, moved);
        return true;
    }
}
