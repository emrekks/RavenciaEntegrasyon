using System.Globalization;
using System.Text.Json;
using MarketplaceHub.Application;

namespace MarketplaceHub.Infrastructure.Adapters.TrendyolEFaturam.Mapping;

public static class TrendyolEFaturamJsonMapper
{
    public static InvoiceSubmissionResult OutgoingInvoice(string json, string? remoteRequestId)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var uuid = RequiredText(root, "invoiceUuid");
        var invoiceId = RequiredText(root, "invoiceId");
        var status = RequiredText(root, "status");
        return new(uuid, invoiceId, uuid, status, remoteRequestId);
    }

    public static InvoiceRemoteStatus InvoiceStatus(string json, string externalReference)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
            root = content.EnumerateArray().FirstOrDefault();
        if (root.ValueKind != JsonValueKind.Object) throw new JsonException("invoice status missing");
        var raw = RequiredText(root, "status");
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var status)) throw new JsonException("status invalid");
        var classification = TrendyolEFaturamStatusCatalog.Classify(status);
        var uuid = Text(root, "invoiceUuid") ?? externalReference;
        var invoiceId = Text(root, "invoiceId");
        var gibStatus = Text(root, "gibStatus");
        var gibCode = NullableInteger(root, "gibStatusCode");
        return new(externalReference, raw, classification.CanonicalStatus, invoiceId, uuid, classification.IsTerminal, gibStatus, gibCode);
    }

    public static string PermanentDocumentUrl(string responseText)
    {
        var value = responseText.Trim().Trim('"');
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https") throw new JsonException("Permanent document URL missing");
        return uri.AbsoluteUri;
    }

    private static string RequiredText(JsonElement root, string name) => Text(root, name) is { Length: > 0 } value ? value : throw new JsonException($"{name} missing");
    private static string? Text(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined) ? value.ToString().Trim() : null;
    private static int? NullableInteger(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.TryGetInt32(out var result) ? result : null;
}
