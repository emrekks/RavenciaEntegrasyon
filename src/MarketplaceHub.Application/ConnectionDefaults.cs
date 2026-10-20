using System.Globalization;
using System.Text.Json;

namespace MarketplaceHub.Application;

// Per-connection defaults kept in PlatformConnection.SettingsJson.
public static class ConnectionDefaults
{
    // "DefaultVatRate": the VAT rate (0-100) used for an offer when the marketplace or the import file gives
    // none. Null when the connection has no valid default, so callers never silently assume 0 %.
    public static decimal? VatRate(string? settingsJson)
    {
        if (string.IsNullOrWhiteSpace(settingsJson)) return null;
        try
        {
            using var document = JsonDocument.Parse(settingsJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("DefaultVatRate", out var value)) return null;
            decimal? rate = value.ValueKind switch
            {
                JsonValueKind.Number when value.TryGetDecimal(out var number) => number,
                JsonValueKind.String when decimal.TryParse(value.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var text) => text,
                _ => null
            };
            return rate is >= 0 and <= 100 ? decimal.Round(rate.Value, 4, MidpointRounding.ToEven) : null;
        }
        catch (JsonException) { return null; }
    }
}
