namespace MarketplaceHub.Application;

// Which value mappings "Gözden Geçirilecek Eşlemeler" lists (ReferenceDataService.ListMappingConflictsAsync).
public static class MappingConflictRules
{
    // Hepsiburada lets one panel value feed several of its values in one field (the value editor allows it, the
    // publication sends the one whose name matches: HepsiburadaProductPublicationComposer.SelectOutboundEnumValue); the
    // other marketplaces take one value per panel value.
    public static bool AllowsSeveralValuesPerPanelValue(string? platformCode) =>
        string.Equals(platformCode?.Trim(), "HEPSIBURADA", StringComparison.OrdinalIgnoreCase);

    // "Aynı Kategoride Birden Fazla Değer": one panel value mapped to several values of one category field.
    public static bool IsSameCategoryConflict(string? platformCode, IEnumerable<string> externalIds) =>
        !AllowsSeveralValuesPerPanelValue(platformCode) && externalIds.Distinct(StringComparer.Ordinal).Skip(1).Any();

    // "Kategoriler Arasında Farklı Değer": one panel value mapped to different values in different categories. On
    // Hepsiburada a category holds a set of values; categories holding the same set agree.
    public static bool IsCrossCategoryConflict(string? platformCode, IEnumerable<(string Category, string ExternalId)> mappings)
    {
        var rows = mappings.ToList();
        if (rows.Select(x => x.Category).Distinct(StringComparer.Ordinal).Count() < 2 || rows.Select(x => x.ExternalId).Distinct(StringComparer.Ordinal).Count() < 2)
            return false;
        if (!AllowsSeveralValuesPerPanelValue(platformCode)) return true;
        return rows.GroupBy(x => x.Category, StringComparer.Ordinal)
            .Select(category => string.Join('\u001f', category.Select(x => x.ExternalId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)))
            .Distinct(StringComparer.Ordinal)
            .Skip(1)
            .Any();
    }
}
