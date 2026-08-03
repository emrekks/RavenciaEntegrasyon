using System.Text.Json;

namespace MarketplaceHub.Infrastructure.Adapters.TrendyolEFaturam.Contracts;

public static class TrendyolEFaturamCanonicalPayload
{
    public static string Create(TrendyolEFaturamConnectionSettings settings, string canonicalJson)
    {
        if (settings.CompanyId is not > 0 || settings.UserId is not > 0)
            throw new JsonException("E-Faturam fiscal account is incomplete.");

        using var document = JsonDocument.Parse(canonicalJson);
        var root = document.RootElement;
        var order = RequiredObject(root, "Order");
        var customer = ParseSnapshot(order, "CustomerSnapshotJson");
        var addressSnapshot = ParseSnapshot(order, "InvoiceAddressSnapshotJson");
        try
        {
            var address = RequiredObject(addressSnapshot.RootElement, "invoiceAddress");
            var taxId = Text(address, "taxNumber", "invoiceTaxNumber", "identityNumber", "IdentityNumber");
            if (taxId.Length is not (10 or 11) || !taxId.All(char.IsDigit))
                throw new JsonException("EFATURAM_RECIPIENT_TAX_ID_REQUIRED");

            var invoiceType = RequiredText(root, "InvoiceType");
            var lines = RequiredArray(root, "Lines").EnumerateArray().Select(line =>
            {
                var total = Decimal(line, "LineTotal");
                var vat = Decimal(line, "VatAmount");
                return new EfaturamInvoiceLine(
                    RequiredText(line, "DescriptionSnapshot"), Unit(Text(line, "UnitSnapshot")), Decimal(line, "Quantity"), Decimal(line, "UnitPrice"),
                    decimal.Round(total - vat, 2, MidpointRounding.AwayFromZero), vat, Decimal(line, "VatRate"), Decimal(line, "DiscountAmount"), total);
            }).ToArray();

            var orderedAt = DateTimeOffset.Parse(RequiredText(order, "OrderedAt"));
            var source = new EfaturamInvoicePayloadSource(
                RequiredText(root, "Id"), invoiceType, RequiredText(root, "Currency"), RequiredText(root, "Note"), RequiredText(order, "OrderNumber"),
                DateOnly.FromDateTime(orderedAt.Date), DateTimeOffset.Parse(RequiredText(root, "IssuedAt")),
                new(taxId, Text(address, "countryCode") is { Length: > 0 } country ? country : "TR", Text(address, "city"), Text(address, "district"),
                    Text(address, "fullAddress", "address1", "addressText", "address"), NullText(address, "postalCode"), NullText(address, "phone"),
                    NullText(address, "email") ?? NullText(customer.RootElement, "customerEmail"), NullText(address, "firstName") ?? NullText(customer.RootElement, "customerFirstName"),
                    NullText(address, "lastName") ?? NullText(customer.RootElement, "customerLastName"), NullText(address, "taxOffice")),
                lines);
            return TrendyolEFaturamInvoicePayload.Create(new(settings.CompanyId.Value, settings.UserId.Value, settings.Prefix), source);
        }
        finally
        {
            customer.Dispose();
            addressSnapshot.Dispose();
        }
    }

    private static JsonDocument ParseSnapshot(JsonElement parent, string name) => JsonDocument.Parse(RequiredText(parent, name));
    private static JsonElement RequiredObject(JsonElement parent, string name) => parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object ? value : throw new JsonException($"{name} missing");
    private static JsonElement RequiredArray(JsonElement parent, string name) => parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array ? value : throw new JsonException($"{name} missing");
    private static string RequiredText(JsonElement parent, string name) => Text(parent, name) is { Length: > 0 } value ? value : throw new JsonException($"{name} missing");
    private static decimal Decimal(JsonElement parent, string name) => parent.TryGetProperty(name, out var value) && value.TryGetDecimal(out var number) ? number : throw new JsonException($"{name} missing");
    private static string Text(JsonElement parent, params string[] names)
    {
        foreach (var name in names)
            if (parent.TryGetProperty(name, out var value) && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
                return value.ToString().Trim();
        return "";
    }
    private static string? NullText(JsonElement parent, params string[] names) => Text(parent, names) is { Length: > 0 } value ? value : null;
    private static string Unit(string value) => value.Trim().ToUpperInvariant() switch { "ADET" or "C62" => "C62", _ => throw new JsonException("EFATURAM_UNIT_CODE_UNSUPPORTED") };
}
