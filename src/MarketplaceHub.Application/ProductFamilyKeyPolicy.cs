namespace MarketplaceHub.Application;

// D9 (K18): a model-code family is the products of one brand whose variants share a model code under the shared
// catalog key, so "abc-01" and "ABC 01" are one family while two brands using the same model code stay apart.
// The key is compared in memory: the database upper case and the invariant upper case differ outside ASCII.
public static class ProductFamilyKeyPolicy
{
    // Null when the model code is empty: such a product is a family of its own.
    public static string? ModelCodeKey(string? modelCode) =>
        CatalogKeyNormalizer.Key(modelCode) is { Length: > 0 } key ? key : null;

    public static string FamilyKey(Guid productId, Guid? brandId, string? modelCode) =>
        ModelCodeKey(modelCode) is { } key
            ? $"model:{(brandId is { } brand ? brand.ToString("D") : "-")}:{key}"
            : $"product:{productId:D}";

    public static bool SameFamily(Guid? leftBrandId, string? leftModelCode, Guid? rightBrandId, string? rightModelCode) =>
        leftBrandId == rightBrandId
        && ModelCodeKey(leftModelCode) is { } key
        && string.Equals(key, ModelCodeKey(rightModelCode), StringComparison.Ordinal);
}
