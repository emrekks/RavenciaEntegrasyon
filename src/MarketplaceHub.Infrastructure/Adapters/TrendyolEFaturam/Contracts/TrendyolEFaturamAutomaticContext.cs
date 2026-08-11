using System.Globalization;
using System.Text;
using System.Text.Json;

namespace MarketplaceHub.Infrastructure.Adapters.TrendyolEFaturam.Contracts;

public sealed record EfaturamCarrierIdentity(string Code, string Name, string TaxId);

public static class TrendyolCarrierCatalog
{
    // Source: Trendyol Kargo Şirketleri Listesi (V2). The Aras value is published
    // as a numeric 9-digit value; it is left-padded because E-Faturam requires a
    // 10/11 digit VKN/TCKN and numeric rendering drops a leading zero.
    private static readonly EfaturamCarrierIdentity[] Items =
    [
        new("SENDEOMP", "Kolay Gelsin Marketplace", "2910804196"),
        new("CEVATEDARIK", "Ceva Tedarik Marketplace", "1800038254"),
        new("DHLECOMMP", "DHL eCommerce Marketplace", "6080712084"),
        new("PTTMP", "PTT Kargo Marketplace", "7320068060"),
        new("SURATMP", "Sürat Kargo Marketplace", "7870233582"),
        new("TEXMP", "Trendyol Express Marketplace", "8590921777"),
        new("HOROZMP", "Horoz Kargo Marketplace", "4630097122"),
        new("CEVAMP", "CEVA Marketplace", "8450298557"),
        new("YKMP", "Yurtiçi Kargo Marketplace", "3130557669"),
        new("ARASMP", "Aras Kargo Marketplace", "0720039666")
    ];

    private static readonly IReadOnlyDictionary<string, EfaturamCarrierIdentity> Index = BuildIndex();

    public static bool TryResolve(string? provider, out EfaturamCarrierIdentity identity)
    {
        identity = null!;
        if (string.IsNullOrWhiteSpace(provider)) return false;
        return Index.TryGetValue(Normalize(provider), out identity!);
    }

    private static IReadOnlyDictionary<string, EfaturamCarrierIdentity> BuildIndex()
    {
        var result = new Dictionary<string, EfaturamCarrierIdentity>(StringComparer.Ordinal);
        foreach (var item in Items)
        {
            Add(result, item.Code, item);
            Add(result, item.Name, item);
            Add(result, item.Name.Replace(" Marketplace", "", StringComparison.OrdinalIgnoreCase), item);
            Add(result, item.Name.Replace(" Kargo", "", StringComparison.OrdinalIgnoreCase), item);
        }
        Add(result, "Trendyol Express", Items.Single(x => x.Code == "TEXMP"));
        Add(result, "Yurtiçi Kargo", Items.Single(x => x.Code == "YKMP"));
        Add(result, "Aras Kargo", Items.Single(x => x.Code == "ARASMP"));
        Add(result, "Sürat Kargo", Items.Single(x => x.Code == "SURATMP"));
        Add(result, "PTT Kargo", Items.Single(x => x.Code == "PTTMP"));
        Add(result, "Horoz Kargo", Items.Single(x => x.Code == "HOROZMP"));
        Add(result, "Kolay Gelsin", Items.Single(x => x.Code == "SENDEOMP"));
        Add(result, "DHL eCommerce", Items.Single(x => x.Code == "DHLECOMMP"));
        Add(result, "CEVA", Items.Single(x => x.Code == "CEVAMP"));
        Add(result, "Ceva Tedarik", Items.Single(x => x.Code == "CEVATEDARIK"));
        return result;
    }

    private static void Add(IDictionary<string, EfaturamCarrierIdentity> index, string alias, EfaturamCarrierIdentity value) => index[Normalize(alias)] = value;

    private static string Normalize(string value)
    {
        var normalized = value.Trim().ToUpperInvariant().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(normalized.Length);
        foreach (var character in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(character)) builder.Append(character);
        }
        return builder.ToString();
    }
}

public static class TrendyolEFaturamCustomerAccess
{
    public static bool TryRead(string responseBody, out TrendyolEFaturamAccessContext access)
    {
        access = null!;
        if (string.IsNullOrWhiteSpace(responseBody)) return false;
        try
        {
            using var document = JsonDocument.Parse(responseBody);
            var root = document.RootElement;
            if (!root.TryGetProperty("companyId", out var companyValue) || !TryLong(companyValue, out var companyId)
                || !root.TryGetProperty("userId", out var userValue) || !TryLong(userValue, out var userId)
                || !root.TryGetProperty("accessToken", out var tokenValue) || tokenValue.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(tokenValue.GetString()) || companyId <= 0 || userId <= 0) return false;
            access = new(tokenValue.GetString()!, companyId, userId);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryLong(JsonElement value, out long result)
    {
        result = 0;
        if (value.ValueKind == JsonValueKind.Number) return value.TryGetInt64(out result);
        return value.ValueKind == JsonValueKind.String
            && long.TryParse(value.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out result);
    }
}
