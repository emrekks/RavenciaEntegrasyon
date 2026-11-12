using System.Globalization;

namespace MarketplaceHub.Application;

// The word a person types to confirm a data reset: spaces at either end and the case of the letters do not matter
// ("trendyol", "TRENDYOL", "verileri sil" and "VERİLERİ SİL" all confirm). The panel compares the same way.
public static class ConfirmationText
{
    private static readonly CultureInfo Turkish = CultureInfo.GetCultureInfo("tr-TR");

    public static bool Matches(string? typed, string expected)
    {
        if (typed is null) return false;
        var text = typed.Trim();
        return string.Equals(text, expected, StringComparison.OrdinalIgnoreCase)
            || string.Equals(text.ToUpper(Turkish), expected.ToUpper(Turkish), StringComparison.Ordinal);
    }
}
