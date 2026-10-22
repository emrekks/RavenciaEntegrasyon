using System.Text;

namespace MarketplaceHub.Application;

// Turkey's 81 provinces (il) by their official names, and the reading of a marketplace address's city into one of
// them. Marketplaces write the same province many ways ("İSTANBUL", "istanbul", "Istanbul Avrupa", "K.Maraş",
// "Afyon", "İçel"); anything that names no province is unknown, never guessed.
public static class TurkishProvinces
{
    public const string Unknown = "Bilinmiyor";

    public static readonly IReadOnlyList<string> Names =
    [
        "Adana", "Adıyaman", "Afyonkarahisar", "Ağrı", "Amasya", "Ankara", "Antalya", "Artvin", "Aydın", "Balıkesir",
        "Bilecik", "Bingöl", "Bitlis", "Bolu", "Burdur", "Bursa", "Çanakkale", "Çankırı", "Çorum", "Denizli",
        "Diyarbakır", "Edirne", "Elazığ", "Erzincan", "Erzurum", "Eskişehir", "Gaziantep", "Giresun", "Gümüşhane", "Hakkari",
        "Hatay", "Isparta", "Mersin", "İstanbul", "İzmir", "Kars", "Kastamonu", "Kayseri", "Kırklareli", "Kırşehir",
        "Kocaeli", "Konya", "Kütahya", "Malatya", "Manisa", "Kahramanmaraş", "Mardin", "Muğla", "Muş", "Nevşehir",
        "Niğde", "Ordu", "Rize", "Sakarya", "Samsun", "Siirt", "Sinop", "Sivas", "Tekirdağ", "Tokat",
        "Trabzon", "Tunceli", "Şanlıurfa", "Uşak", "Van", "Yozgat", "Zonguldak", "Aksaray", "Bayburt", "Karaman",
        "Kırıkkale", "Batman", "Şırnak", "Bartın", "Ardahan", "Iğdır", "Yalova", "Karabük", "Kilis", "Osmaniye", "Düzce"
    ];

    // Other names marketplaces use for a province (folded: upper case, Turkish letters to ASCII, letters only).
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.Ordinal)
    {
        ["AFYON"] = "Afyonkarahisar",
        ["KMARAS"] = "Kahramanmaraş",
        ["MARAS"] = "Kahramanmaraş",
        ["ICEL"] = "Mersin",
        ["URFA"] = "Şanlıurfa",
        ["SURFA"] = "Şanlıurfa",
        ["ANTEP"] = "Gaziantep",
        ["IZMIT"] = "Kocaeli",
        ["ADAPAZARI"] = "Sakarya",
        ["ISTANBULAVRUPA"] = "İstanbul",
        ["ISTANBULANADOLU"] = "İstanbul",
        ["ISTANBULAVRUPAYAKASI"] = "İstanbul",
        ["ISTANBULANADOLUYAKASI"] = "İstanbul",
        ["DERSIM"] = "Tunceli"
    };

    private static readonly Dictionary<string, string> ByKey = Build();

    private static Dictionary<string, string> Build()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in Names) map[Fold(name)] = name;
        foreach (var (alias, name) in Aliases) map[alias] = name;
        return map;
    }

    // The province an address's city text names, or null. A text such as "Kadıköy / İstanbul" or "İstanbul (Avrupa)"
    // is read part by part when the whole names none.
    public static string? Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (ByKey.TryGetValue(Fold(raw), out var whole)) return whole;
        foreach (var part in raw.Split(['/', ',', '-', '(', ')', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (ByKey.TryGetValue(Fold(part), out var name)) return name;
        // "Kadıköy İstanbul": a single word that is a province, only when exactly one word is.
        var words = raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(word => ByKey.TryGetValue(Fold(word), out var name) ? name : null)
            .Where(name => name is not null)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return words.Count == 1 ? words[0] : null;
    }

    // Upper case in Turkish, Turkish letters to their ASCII base, everything but letters dropped ("K.Maraş" -> "KMARAS").
    internal static string Fold(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value.Trim())
        {
            var mapped = ch switch
            {
                'ı' or 'I' or 'i' or 'İ' => 'I',
                'ş' or 'Ş' => 'S',
                'ğ' or 'Ğ' => 'G',
                'ü' or 'Ü' => 'U',
                'ö' or 'Ö' => 'O',
                'ç' or 'Ç' => 'C',
                'â' or 'Â' => 'A',
                'î' or 'Î' => 'I',
                'û' or 'Û' => 'U',
                _ => char.ToUpperInvariant(ch)
            };
            if (mapped is >= 'A' and <= 'Z') builder.Append(mapped);
        }
        return builder.ToString();
    }
}
