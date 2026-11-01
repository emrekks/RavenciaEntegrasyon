namespace MarketplaceHub.Application;

// A model code belongs to one product. A new card ("Yeni Ürün", "Kopyala ile yeni ürün", a draft too) may not take a
// model code the tenant's catalog already holds: a new colour or size of that model is added from the existing
// product's editor. Only the user's create path is checked; the editor's family save, the imports and an update are not.
// Codes are compared on the shared catalog key (ProductFamilyKeyPolicy.ModelCodeKey): case, spaces and separators aside.
public static class ModelCodeUniquenessPolicy
{
    public const string ConflictCode = "MODEL_CODE_IN_USE";
    public const string Field = "modelCode";
    public const int MaxCodeLength = 160;

    public sealed record Holder(Guid ProductId, string Title, string ModelCode);

    public static string Message(string productTitle) =>
        $"Bu model kodu '{productTitle}' ürününde kullanılıyor. Yeni renk veya beden eklemek için o ürünü düzenleyin.";

    // The distinct keys of the given model codes; an empty code has none (a card without a model code is free).
    public static IReadOnlyList<string> Keys(IEnumerable<string?> modelCodes) =>
        modelCodes.Select(ProductFamilyKeyPolicy.ModelCodeKey).OfType<string>().Distinct(StringComparer.Ordinal).ToList();

    // The first stored card whose model code has one of the keys, by the given (stable) order.
    public static Holder? FirstHolder(IReadOnlyCollection<string> keys, IEnumerable<Holder> rows)
    {
        if (keys.Count == 0) return null;
        return rows.FirstOrDefault(row => ProductFamilyKeyPolicy.ModelCodeKey(row.ModelCode) is { } key && keys.Contains(key, StringComparer.Ordinal));
    }
}
