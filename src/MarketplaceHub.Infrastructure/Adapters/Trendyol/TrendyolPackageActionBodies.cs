using System.Text.Json;

namespace MarketplaceHub.Infrastructure.Adapters.Trendyol;

// C14: "updatePackageUnsupplied" (kalem iptali) takes { lines: [{ lineId, quantity }], reasonId } where reasonId is one of
// Trendyol's unsupplied reasons (500-506). The body is rebuilt from validated values so nothing else reaches Trendyol.
public static class TrendyolPackageActionBodies
{
    public const int MinUnsuppliedReasonId = 500;
    public const int MaxUnsuppliedReasonId = 506;
    public const string UnsuppliedInvalidMessage = "Kalem iptali için her satırda pozitif lineId ve adet, ayrıca 500-506 arasında bir iptal nedeni (reasonId) gerekir; satırlar yinelenemez.";

    public sealed record UnsuppliedLine(long LineId, int Quantity);
    public sealed record UnsuppliedBody(IReadOnlyList<UnsuppliedLine> Lines, int ReasonId);

    public static bool TryBuildUnsupplied(string? payloadJson, out UnsuppliedBody? body)
    {
        body = null;
        if (string.IsNullOrWhiteSpace(payloadJson)) return false;
        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("reasonId", out var reason) || reason.ValueKind != JsonValueKind.Number || !reason.TryGetInt32(out var reasonId)
                || reasonId is < MinUnsuppliedReasonId or > MaxUnsuppliedReasonId
                || !root.TryGetProperty("lines", out var lines) || lines.ValueKind != JsonValueKind.Array || lines.GetArrayLength() == 0)
                return false;
            var parsed = new List<UnsuppliedLine>(lines.GetArrayLength());
            var seen = new HashSet<long>();
            foreach (var line in lines.EnumerateArray())
            {
                if (line.ValueKind != JsonValueKind.Object
                    || !line.TryGetProperty("lineId", out var lineIdElement) || !TryPositiveLong(lineIdElement, out var lineId)
                    || !line.TryGetProperty("quantity", out var quantityElement) || quantityElement.ValueKind != JsonValueKind.Number || !quantityElement.TryGetInt32(out var quantity) || quantity <= 0
                    || !seen.Add(lineId))
                    return false;
                parsed.Add(new(lineId, quantity));
            }
            body = new(parsed, reasonId);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    // Trendyol line ids are numbers; a numeric string from an older payload is accepted.
    private static bool TryPositiveLong(JsonElement element, out long value)
    {
        value = 0;
        return element.ValueKind switch
        {
            JsonValueKind.Number => element.TryGetInt64(out value) && value > 0,
            JsonValueKind.String => long.TryParse(element.GetString(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out value) && value > 0,
            _ => false
        };
    }
}
