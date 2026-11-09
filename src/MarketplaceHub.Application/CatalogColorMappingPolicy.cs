namespace MarketplaceHub.Application;

public static class CatalogColorMappingPolicy
{
    private static readonly IReadOnlyDictionary<string, string[]> Fallbacks = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["BORDO"] = ["KIRMIZI"],
        ["FUSYA"] = ["PEMBE", "MOR"],
        ["HARDAL"] = ["SARI"],
        ["KIREMIT"] = ["TURUNCU"],
        ["MERCAN"] = ["TURUNCU", "PEMBE"],
        ["MURDUM"] = ["MOR", "LACIVERT"],
        ["PETROL"] = ["MAVI", "YESIL"],
        ["SAKS"] = ["MAVI"],
        ["TABA"] = ["KAHVERENGI"],
        ["VIZON"] = ["KAHVERENGI", "BEJ"],
        ["EKRU"] = ["BEJ", "BEYAZ"],
        ["KREM"] = ["BEJ", "BEYAZ"],
        ["LACIVERT"] = ["MAVI"],
        ["FISTIK"] = ["YESIL"],
        ["YAVRUAGZI"] = ["PEMBE"]
    };

    public static IReadOnlyList<string> FallbackKeys(string? value)
    {
        var key = Normalize(value);
        return Fallbacks.TryGetValue(key, out var values) ? values : [];
    }

    // The shared catalog key folds dotless ı to I ("Fıstık" and "Yavruağzı" would otherwise miss the table).
    private static string Normalize(string? value) => CatalogKeyNormalizer.Key(value).Replace("-", "", StringComparison.Ordinal);
}
