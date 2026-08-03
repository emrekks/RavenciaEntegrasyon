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

    public static InvoiceSubmissionResult OutgoingInvoice(string json, string? remoteRequestId)
    {
        using var document = JsonDocument.Parse(json); var root = document.RootElement;
        var uuid = RequiredText(root, "invoiceUuid");
        var invoiceId = RequiredText(root, "invoiceId");
        var status = RequiredText(root, "status");
        return new(uuid, invoiceId, uuid, status, remoteRequestId);
    }

    public static string PermanentDocumentUrl(string responseText)
    {
        var value = responseText.Trim().Trim('"');
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https") throw new JsonException("Permanent document URL missing");
        return uri.AbsoluteUri;
    }

    private static string RequiredText(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) throw new JsonException($"{name} missing");
        var text = value.ToString(); return string.IsNullOrWhiteSpace(text) ? throw new JsonException($"{name} missing") : text;
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
