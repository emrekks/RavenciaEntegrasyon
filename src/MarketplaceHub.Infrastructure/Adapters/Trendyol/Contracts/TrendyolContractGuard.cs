using System.Text.Json;

namespace MarketplaceHub.Infrastructure.Adapters.Trendyol.Contracts;

public static class TrendyolContractGuard
{
    public static bool HasContentArray(string json) { try { using var document = JsonDocument.Parse(json); return document.RootElement.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array; } catch (JsonException) { return false; } }
    public static bool HasBatchRequestId(string json) { try { using var document = JsonDocument.Parse(json); return document.RootElement.TryGetProperty("batchRequestId", out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()); } catch (JsonException) { return false; } }
}
