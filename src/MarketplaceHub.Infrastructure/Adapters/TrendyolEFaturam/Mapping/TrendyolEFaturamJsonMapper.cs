using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MarketplaceHub.Application;

namespace MarketplaceHub.Infrastructure.Adapters.TrendyolEFaturam.Mapping;

public static class TrendyolEFaturamJsonMapper
{
    public static TaxpayerResult Taxpayer(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (!root.TryGetProperty("applicationDetail", out var details) || details.ValueKind != JsonValueKind.Array) throw new JsonException("applicationDetail missing");
        var applications = details.EnumerateArray().Select(item => new TaxpayerApplicationResult(
            Integer(item, "type"),
            Text(item, "serviceName") ?? string.Empty,
            Text(item, "gibStatus") ?? string.Empty,
            item.TryGetProperty("activated", out var activated) && activated.ValueKind == JsonValueKind.True,
            Date(item, "activationDate"),
            Date(item, "deactivationDate"))).ToArray();
        var customer = root.TryGetProperty("partnerCustomerId", out var id) && id.ValueKind == JsonValueKind.Number ? id.GetInt64().ToString(CultureInfo.InvariantCulture) : null;
        var eInvoiceRegistered = applications.Any(x => x.Activated && (x.Type == 1 || x.ServiceName.Contains("E-FATURA", StringComparison.OrdinalIgnoreCase) || x.ServiceName.Contains("EINVOICE", StringComparison.OrdinalIgnoreCase)));
        return new(eInvoiceRegistered, customer, applications, Hash(json));
    }

    public static (string AccessToken, long CompanyId, long UserId, long? PartnerCustomerId) CustomerAccess(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var token = RequiredText(root, "accessToken");
        var companyId = RequiredLong(root, "companyId");
        var userId = RequiredLong(root, "userId");
        var partnerCustomerId = NullableLong(root, "partnerCustomerId");
        if (companyId <= 0 || userId <= 0) throw new JsonException("customerSignIn scope invalid");
        return (token, companyId, userId, partnerCustomerId);
    }

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
    private static int Integer(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.TryGetInt32(out var result) ? result : 0;
    private static long RequiredLong(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.TryGetInt64(out var result) ? result : throw new JsonException($"{name} missing");
    private static long? NullableLong(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.TryGetInt64(out var result) ? result : null;
    private static int? NullableInteger(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.TryGetInt32(out var result) ? result : null;
    private static DateTimeOffset? Date(JsonElement root, string name) => Text(root, name) is { Length: > 0 } value && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed) ? parsed : null;
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
