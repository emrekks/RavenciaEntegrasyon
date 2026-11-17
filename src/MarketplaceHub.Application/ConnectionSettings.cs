using System.Text.Json;

namespace MarketplaceHub.Application;

// Readers of a connection's SettingsJson that every layer shares (ADAPTOR-DIGER-9).
public static class ConnectionSettings
{
    public const string ExternalWritesEnabledProperty = "ExternalWritesEnabled";

    // The connection's own external-write switch: only a JSON true counts. Missing, blank, unreadable or non-object
    // settings keep writes off (the copies this replaced threw on a null or non-object value; the stored settings are
    // always an object, and a throw never allowed a write either).
    public static bool ExternalWritesEnabled(string? settingsJson)
    {
        if (string.IsNullOrWhiteSpace(settingsJson)) return false;
        try
        {
            using var document = JsonDocument.Parse(settingsJson);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty(ExternalWritesEnabledProperty, out var enabled) && enabled.ValueKind == JsonValueKind.True;
        }
        catch (JsonException) { return false; }
    }
}
