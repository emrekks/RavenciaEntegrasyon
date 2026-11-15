using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MarketplaceHub.Application;

namespace MarketplaceHub.Infrastructure.Adapters.Webhooks;

// The webhook checks the Trendyol and Hepsiburada verifiers share: the subscription's API key or Basic credentials
// compared in fixed time, and the canonical form of a body (properties sorted, whitespace dropped) for the event hash.
internal static class WebhookAuth
{
    internal static bool IsAuthenticated(string type, string? username, string? password, string? apiKey, IReadOnlyDictionary<string, string> headers)
    {
        if (type == "API_KEY") return apiKey is not null && Header(headers, "x-api-key") is { } actual && FixedEquals(actual, apiKey);
        if (type == "BASIC_AUTHENTICATION" && username is not null && password is not null)
        {
            var expected = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"));
            return Header(headers, "Authorization") is { } actual && FixedEquals(actual, expected);
        }
        return false;
    }

    internal static string? Header(IReadOnlyDictionary<string, string> headers, string name) =>
        headers.FirstOrDefault(item => string.Equals(item.Key, name, StringComparison.OrdinalIgnoreCase)).Value;

    internal static bool FixedEquals(string actual, string expected)
    {
        var left = Encoding.UTF8.GetBytes(actual);
        var right = Encoding.UTF8.GetBytes(expected);
        return left.Length == right.Length && CryptographicOperations.FixedTimeEquals(left, right);
    }

    internal static AdapterError AuthenticationFailed() =>
        new(AdapterErrorClass.Authentication, "WEBHOOK_AUTHENTICATION_FAILED", "Webhook kimlik doğrulaması başarısız.", 401, null, null);
}

internal static class CanonicalJson
{
    internal static string Write(JsonElement value)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) WriteValue(writer, value);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteValue(Utf8JsonWriter writer, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in value.EnumerateObject().OrderBy(item => item.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteValue(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray()) WriteValue(writer, item);
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(value.GetString());
                break;
            case JsonValueKind.Number:
                writer.WriteRawValue(value.GetRawText(), skipInputValidation: true);
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            default:
                writer.WriteNullValue();
                break;
        }
    }
}
