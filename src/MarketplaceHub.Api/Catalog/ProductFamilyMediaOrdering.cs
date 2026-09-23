namespace MarketplaceHub.Api.Catalog;

public static class ProductFamilyMediaOrdering
{
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
