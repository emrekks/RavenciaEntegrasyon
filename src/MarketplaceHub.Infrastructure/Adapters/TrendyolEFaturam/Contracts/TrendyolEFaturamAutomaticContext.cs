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

public static class TrendyolEFaturamDirectAccountAccess
{
    public static bool TryRead(string token, out TrendyolEFaturamAccessContext access)
    {
        access = null!;
        if (string.IsNullOrWhiteSpace(token)) return false;
        try
        {
            var segments = token.Split('.');
            if (segments.Length != 3) return false;
            var payload = segments[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + ((4 - payload.Length % 4) % 4), '=');
            using var document = JsonDocument.Parse(Convert.FromBase64String(payload));
            var root = document.RootElement;
            if (!TryGetCompanyId(root, out var companyId)
                || !TryGetUserId(root, out var userId)
                || companyId <= 0 || userId <= 0) return false;
            access = new(
                token,
                companyId,
                userId,
                TryGetNumericDate(root, "iat"),
                TryGetNumericDate(root, "nbf"),
                TryGetNumericDate(root, "exp"),
                TryGetString(root, "iss"),
                TryGetAudience(root),
                TryGetPrivilege(root, companyId, "INVOICE_CREATE"),
                TryGetPrivilege(root, companyId, "INVOICE_READ"));
            return true;
        }
        catch (Exception exception) when (exception is FormatException or JsonException)
        {
            return false;
        }
    }

    private static bool TryGetLong(JsonElement root, string name, out long result)
    {
        foreach (var property in root.EnumerateObject())
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                return TryLong(property.Value, out result);
        result = 0;
        return false;
    }

    private static DateTimeOffset? TryGetNumericDate(JsonElement root, string name)
    {
        if (!TryGetLong(root, name, out var value) || value <= 0) return null;
        try
        {
            return value > 10_000_000_000
                ? DateTimeOffset.FromUnixTimeMilliseconds(value)
                : DateTimeOffset.FromUnixTimeSeconds(value);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static string? TryGetString(JsonElement root, string name)
    {
        foreach (var property in root.EnumerateObject())
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)
                && property.Value.ValueKind == JsonValueKind.String)
                return property.Value.GetString();
        return null;
    }

    private static string? TryGetAudience(JsonElement root)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (!string.Equals(property.Name, "aud", StringComparison.OrdinalIgnoreCase)) continue;
            if (property.Value.ValueKind == JsonValueKind.String) return property.Value.GetString();
            if (property.Value.ValueKind == JsonValueKind.Array)
                return string.Join(',', property.Value.EnumerateArray()
                    .Where(value => value.ValueKind == JsonValueKind.String)
                    .Select(value => value.GetString())
                    .Where(value => !string.IsNullOrWhiteSpace(value)));
        }
        return null;
    }

    private static bool TryGetCompanyId(JsonElement root, out long result)
    {
        if (TryGetLong(root, "companyId", out result)) return true;
        if (!root.TryGetProperty("privs", out var privileges) || privileges.ValueKind != JsonValueKind.Object)
        {
            result = 0;
            return false;
        }

        var companyIds = privileges.EnumerateObject()
            .Select(value => value.Name)
            .Where(value => long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out _))
            .Select(value => long.Parse(value, CultureInfo.InvariantCulture))
            .Where(value => value > 0)
            .Distinct()
            .ToArray();
        result = companyIds.Length == 1 ? companyIds[0] : 0;
        return result > 0;
    }

    private static bool TryGetUserId(JsonElement root, out long result) =>
        TryGetLong(root, "userId", out result)
        || TryGetLong(root, "sub", out result);

    private static bool? TryGetPrivilege(JsonElement root, long companyId, string privilege)
    {
        if (!root.TryGetProperty("privs", out var privileges)
            || privileges.ValueKind != JsonValueKind.Object
            || !privileges.TryGetProperty(companyId.ToString(CultureInfo.InvariantCulture), out var companyPrivileges)
            || companyPrivileges.ValueKind != JsonValueKind.Array)
            return null;
        return companyPrivileges.EnumerateArray().Any(value =>
            value.ValueKind == JsonValueKind.String
            && string.Equals(value.GetString(), privilege, StringComparison.OrdinalIgnoreCase));
    }


    private static bool TryLong(JsonElement value, out long result)
    {
        result = 0;
        if (value.ValueKind == JsonValueKind.Number) return value.TryGetInt64(out result);
        return value.ValueKind == JsonValueKind.String
            && long.TryParse(value.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out result);
    }
}
