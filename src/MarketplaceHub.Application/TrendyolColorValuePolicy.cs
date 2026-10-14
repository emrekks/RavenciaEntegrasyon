using System.Collections.Generic;
using System.Globalization;

namespace MarketplaceHub.Application;

public static class TrendyolColorValuePolicy
{
    public static bool UsesCustomPanelColorValue(string? attributeName)
    {
        if (string.IsNullOrWhiteSpace(attributeName)) return false;

        var normalized = attributeName.Trim()
            .Replace("[TDG]", "", StringComparison.OrdinalIgnoreCase)
            .Replace("[A-TDG]", "", StringComparison.OrdinalIgnoreCase)
            .Replace(" ", "", StringComparison.Ordinal)
            .Replace("-", "", StringComparison.Ordinal)
            .Replace("_", "", StringComparison.Ordinal)
            .ToUpperInvariant();

        return normalized is "RENK" or "COLOR" or "COLOUR";
    }

    public static string FormatPanelColorValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";

        var culture = CultureInfo.GetCultureInfo("tr-TR");
        var normalized = value.Trim().ToLower(culture);
        return culture.TextInfo.ToTitleCase(normalized);
    }

    public static bool TrySetCustomPanelColorValue(IDictionary<string, object?> payload, string? attributeName, string? panelValue)
    {
        if (!UsesCustomPanelColorValue(attributeName)) return false;

        payload["customAttributeValue"] = FormatPanelColorValue(panelValue);
        return true;
    }
}
