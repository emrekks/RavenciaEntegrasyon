using System.Globalization;
using System.Text;

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

    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var form = value.Trim().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(form.Length);
        foreach (var character in form)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(character)) builder.Append(char.ToUpperInvariant(character));
        }
        return builder.ToString();
    }
}
