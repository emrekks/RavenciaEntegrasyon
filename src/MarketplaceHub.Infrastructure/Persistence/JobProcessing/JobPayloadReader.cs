using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MarketplaceHub.Infrastructure.Persistence;

// ISLEYICI-9 stage 1 (16 Nov 2026): reading a job's JSON payload, in one place. A missing property or a body that is not
// JSON gives the fallback, as the processor's own readers did; only the order read request (OrderRead) refuses an
// unreadable body, because an order read without its request must not start (KUYRUK-9: ORDER_SYNC_PAYLOAD_INVALID).
internal static class JobPayloadReader
{
    public static int BoundedInt(string payloadJson, string propertyName, int fallback, int minimum, int maximum)
    {
        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            return document.RootElement.TryGetProperty(propertyName, out var value) && value.TryGetInt32(out var parsed)
                ? Math.Clamp(parsed, minimum, maximum)
                : fallback;
        }
        catch (JsonException) { return fallback; }
    }

    public static bool Boolean(string payloadJson, string propertyName)
    {
        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            return document.RootElement.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.True;
        }
        catch (JsonException) { return false; }
    }

    public static bool BooleanOrDefault(string payloadJson, string propertyName, bool fallback)
    {
        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            return document.RootElement.TryGetProperty(propertyName, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? value.GetBoolean()
                : fallback;
        }
        catch (JsonException) { return fallback; }
    }

    public static string? Text(string payloadJson, string propertyName)
    {
        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            return document.RootElement.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch (JsonException) { return null; }
    }

    // What an order read job asks for: one order by its number (with the package it named, where the marketplace reads
    // packages), or a list read, full or since the watermark. Null when the body is not JSON.
    public static OrderReadRequest? OrderRead(string payloadJson)
    {
        try
        {
            using var payload = JsonDocument.Parse(payloadJson);
            var root = payload.RootElement;
            string? externalOrderId = null, packageNumber = null;
            var full = false;
            if (root.TryGetProperty("externalOrderId", out var value) && value.ValueKind == JsonValueKind.String) externalOrderId = value.GetString();
            if (root.TryGetProperty("full", out var fullValue) && fullValue.ValueKind is JsonValueKind.True or JsonValueKind.False) full = fullValue.GetBoolean();
            if (root.TryGetProperty("packageNumber", out var packageValue) && packageValue.ValueKind == JsonValueKind.String) packageNumber = packageValue.GetString();
            return new(externalOrderId, full, packageNumber);
        }
        catch (JsonException) { return null; }
    }
}

internal sealed record OrderReadRequest(string? ExternalOrderId, bool Full, string? PackageNumber)
{
    public bool IsSingleOrder => !string.IsNullOrWhiteSpace(ExternalOrderId);
}

// ISLEYICI-12 point 7 (16 Nov 2026): the text helpers the processor and the job handlers each kept a copy of.
internal static class TextValues
{
    // The value cut to length characters; null stays null.
    public static string? Truncate(string? value, int length) => value is null || value.Length <= length ? value : value[..length];

    // Upper-case hex SHA-256 of the UTF-8 text.
    public static string Sha256Hex(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
