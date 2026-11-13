namespace MarketplaceHub.Application;

// A model code belongs to one product. A new card ("Yeni Ürün", "Kopyala ile yeni ürün", a draft too) may not take a
// model code the tenant's catalog already holds: a new colour or size of that model is added from the existing
// product's editor. The user's create path is checked; so is a model code an editor save or an Excel upload changes to
// one another product holds (scan C-9, 13 Nov 2026). The editor's family save adding rows under its own code and the
// marketplace imports (the marketplace's own codes) are not.
// Codes are compared on the shared catalog key (ProductFamilyKeyPolicy.ModelCodeKey): case, spaces and separators aside.
public static class ModelCodeUniquenessPolicy
{
    public const string ConflictCode = "MODEL_CODE_IN_USE";
    public const string Field = "modelCode";
    public const int MaxCodeLength = 160;

    public sealed record Holder(Guid ProductId, string Title, string ModelCode);

    public const string ChangeConflictCode = "MODEL_CODE_CHANGE_IN_USE";

    public static string ChangeMessage(string productTitle) =>
        $"Yeni model kodu '{productTitle}' ürününde kullanılıyor; değiştirilirse iki ürün tek ürünün renkleri gibi birleşir. Başka bir model kodu girin.";

    // The new codes of the rows whose model code really changes (another key, not only another spelling).
    public static IReadOnlyList<string> ChangedCodes(IEnumerable<(string? Before, string? After)> rows) =>
        rows.Where(row => ProductFamilyKeyPolicy.ModelCodeKey(row.After) is { } after && after != ProductFamilyKeyPolicy.ModelCodeKey(row.Before))
            .Select(row => row.After!.Trim()).Distinct(StringComparer.Ordinal).ToList();

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
