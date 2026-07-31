using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MarketplaceHub.Application;

namespace MarketplaceHub.Infrastructure.Adapters.TrendyolEFaturam.Mapping;

public static class TrendyolEFaturamJsonMapper
{
    public static TaxpayerResult Taxpayer(string json)
    {
        using var document = JsonDocument.Parse(json); var root = document.RootElement;
        if (!root.TryGetProperty("applicationDetail", out var details) || details.ValueKind != JsonValueKind.Array) throw new JsonException("applicationDetail missing");
        var active = details.EnumerateArray().Any(x => x.TryGetProperty("activated", out var value) && value.ValueKind == JsonValueKind.True);
        var customer = root.TryGetProperty("partnerCustomerId", out var id) && id.ValueKind == JsonValueKind.Number ? id.GetInt64().ToString(System.Globalization.CultureInfo.InvariantCulture) : null;
        return new(active, customer, Hash(json));
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
