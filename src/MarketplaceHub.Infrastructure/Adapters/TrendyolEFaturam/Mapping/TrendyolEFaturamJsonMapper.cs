using System.Globalization;
using System.Text.Json;
using MarketplaceHub.Application;

namespace MarketplaceHub.Infrastructure.Adapters.TrendyolEFaturam.Mapping;

public static partial class TrendyolEFaturamJsonMapper
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

    // What an unreadable creation answer still tells: its invoiceUuid (with the number and status when present), read from
    // the JSON where it parses and from the raw text where it does not. Null when no UUID can be found.
    public static InvoiceSubmissionResult? PartialOutgoingInvoice(string json, string? remoteRequestId)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object && Text(root, "invoiceUuid") is { } uuid && Guid.TryParse(uuid, out _))
                return new(uuid, Text(root, "invoiceId") is { Length: > 0 } number ? number : null, uuid, Text(root, "status") ?? "UNKNOWN", remoteRequestId);
            return null;
        }
        catch (JsonException)
        {
            var match = RawInvoiceUuid().Match(json);
            return match.Success ? new(match.Groups[1].Value, null, match.Groups[1].Value, "UNKNOWN", remoteRequestId) : null;
        }
    }

    /// <summary>
    /// The 200 answer of the taxpayer lookup: rows of aliases (INVOICE or DESPATCH_ADVICE). Only undeleted INVOICE
    /// receiver (PK) aliases count. A row of another VKN/TCKN, or no usable INVOICE alias, says nothing certain and
    /// is Unknown: the documented "not a taxpayer" answer is a 404, never an empty list.
    /// </summary>
    public static EInvoiceTaxpayerLookup TaxPayers(string json, string taxId)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array) throw new JsonException("taxpayer list missing");
        string? title = null;
        var aliases = new List<EInvoiceTaxpayerAlias>();
        foreach (var row in document.RootElement.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object) throw new JsonException("taxpayer row invalid");
            if (!string.Equals(Text(row, "taxId"), taxId, StringComparison.Ordinal))
                return EInvoiceTaxpayerLookup.Unknown("EINVOICE_TAXPAYER_IDENTITY_MISMATCH");
            if (Text(row, "deletedAt") is not null) continue;
            title ??= Text(row, "title");
            if (!string.Equals(Text(row, "aliasType"), "INVOICE", StringComparison.OrdinalIgnoreCase)) continue;
            var postBoxType = Text(row, "postBoxType");
            if (Text(row, "alias") is not { Length: > 0 } alias
                || string.Equals(postBoxType, "GB", StringComparison.OrdinalIgnoreCase)
                || alias.StartsWith("urn:mail:defaultgb", StringComparison.OrdinalIgnoreCase)) continue;
            aliases.Add(new(alias, postBoxType));
        }
        return aliases.Count == 0
            ? EInvoiceTaxpayerLookup.Unknown("EINVOICE_TAXPAYER_NO_INVOICE_ALIAS")
            : new(EInvoiceTaxpayerState.Registered, title, aliases);
    }

    [System.Text.RegularExpressions.GeneratedRegex("\"invoiceUuid\"\\s*:\\s*\"([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})\"")]
    private static partial System.Text.RegularExpressions.Regex RawInvoiceUuid();

    // expectedUuid is the UUID the status was read for. A paged reply ("content") is read at the item that carries it,
    // never at whatever came first: another invoice's status must not be stored as this one's. A single item that
    // names no UUID is the one asked for; an answer without the UUID asked for is a contract error (unknown).
    public static InvoiceRemoteStatus InvoiceStatus(string json, string externalReference, string? expectedUuid = null)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
            root = StatusItem(content, expectedUuid ?? externalReference);
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

    private static JsonElement StatusItem(JsonElement content, string expectedUuid)
    {
        var items = content.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.Object).ToList();
        var match = items.Where(item => string.Equals(Text(item, "invoiceUuid"), expectedUuid.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
        if (match.Count == 1) return match[0];
        if (match.Count == 0 && items.Count == 1 && Text(items[0], "invoiceUuid") is null) return items[0];
        throw new JsonException("invoice status for the requested UUID missing or ambiguous");
    }

    public static string PermanentDocumentUrl(string responseText)
    {
        var value = responseText.Trim().Trim('"');
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https") throw new JsonException("Permanent document URL missing");
        return uri.AbsoluteUri;
    }

    private static string RequiredText(JsonElement root, string name) => Text(root, name) is { Length: > 0 } value ? value : throw new JsonException($"{name} missing");
    private static string? Text(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined) ? value.ToString().Trim() : null;
    // O8: gibStatusCode may come as null (or as text); TryGetInt32 throws on anything but a number, so read it safely.
    private static int? NullableInteger(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value)) return null;
        if (RemoteJson.Int32(value) is { } number) return number;
        return value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
    }
}
