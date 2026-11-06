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

    // Where a new image goes in one gallery (a product's own or one variant's): the asked position when it is free, holds
    // an archived row or holds the same image; otherwise after the gallery's last row. The editor asks for "after the
    // pictures I know" (their count), but a family reorder leaves sparse positions (rank × stride), so that position
    // could hold another live image, often another colour's, and the upload used to overwrite it.
    // A position at or past the end of the live pictures ("after the pictures I know") goes after the last live row even
    // when that number is free: between ranks 0, 92, 184… a free 3 would put the new picture second in the strip.
    public static int UploadSortOrder(int requested, IEnumerable<(int SortOrder, bool Active, Guid AssetId)> scopeRows, Guid assetId)
    {
        var rows = scopeRows.ToList();
        var holder = rows.FirstOrDefault(row => row.SortOrder == requested);
        var heldBySameImage = rows.Any(row => row.SortOrder == requested) && holder.Active && holder.AssetId == assetId;
        if (heldBySameImage) return requested;
        var others = rows.Where(row => row.Active && row.AssetId != assetId).ToList();
        if (requested >= others.Count && others.Any(row => row.SortOrder > requested)) return rows.Max(row => row.SortOrder) + 1;
        if (!rows.Any(row => row.SortOrder == requested) || !holder.Active) return requested;
        return Math.Max(requested, rows.Max(row => row.SortOrder)) + 1;
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
