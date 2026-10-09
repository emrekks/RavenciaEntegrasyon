using System.Text.Json;

namespace MarketplaceHub.Infrastructure.Adapters.TrendyolEFaturam.Contracts;

public static class TrendyolEFaturamCanonicalPayload
{
    public static string Create(EfaturamFiscalAccount account, string canonicalJson)
    {
        if (account.CompanyId <= 0 || account.UserId <= 0)
            throw new JsonException("E-Faturam access token scope is incomplete.");

        using var document = JsonDocument.Parse(canonicalJson);
        var root = document.RootElement;
        var order = RequiredObject(root, "Order");
        var customer = ParseSnapshot(order, "CustomerSnapshotJson");
        var addressSnapshot = ParseSnapshot(order, "InvoiceAddressSnapshotJson");
        try
        {
            // Trendyol order feeds store invoiceAddress as the snapshot root,
            // while other marketplace/local snapshots may wrap it in invoiceAddress
            // or address. Accept both shapes so valid recipient details reach the fiscal payload.
            var addressRoot = addressSnapshot.RootElement;
            var address = addressRoot;
            if (addressRoot.ValueKind != JsonValueKind.Object)
                throw new JsonException("invoiceAddress missing");
            for (var depth = 0; depth < 3; depth++)
            {
                if (!TryGetProperty(address, "invoiceAddress", out var wrappedAddress)
                    && !TryGetProperty(address, "invoice", out wrappedAddress)
                    && !TryGetProperty(address, "address", out wrappedAddress)) break;
                address = wrappedAddress.ValueKind == JsonValueKind.Object ? wrappedAddress : throw new JsonException("invoiceAddress missing");
            }
            var taxId = Text(address, "taxNumber", "invoiceTaxNumber", "identityNumber", "identityNo", "tcIdentityNumber", "taxId", "taxIdentifier", "nationalIdentityNumber", "tckn", "vkn");
            if (!ValidTaxId(taxId))
                taxId = Text(customer.RootElement, "customerTaxNumber", "taxNumber", "invoiceTaxNumber", "identityNumber", "identityNo", "customerIdentityNumber", "tcIdentityNumber", "taxId", "taxIdentifier", "nationalIdentityNumber", "tckn", "vkn");
            if (!ValidTaxId(taxId))
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
            EfaturamPayment? payment = null;
            EfaturamDelivery? delivery = null;
            if (invoiceType == "EARSIVFATURA")
            {
                var package = RequiredObject(root, "Package");
                var cargoProvider = RequiredText(package, "CargoProviderExternalId");
                if (!TrendyolCarrierCatalog.TryResolve(cargoProvider, out var carrier))
                    throw new JsonException("EFATURAM_CARRIER_CATALOG_MISS");
                var sentAt = DateTimeOffset.Parse(RequiredText(package, "StatusOccurredAt"));
                payment = new("https://www.trendyol.com", "Trendyol", "PAZARYERI", orderedAt, "MEDIATOR");
                delivery = new(carrier.TaxId, carrier.Name, null, DateOnly.FromDateTime(sentAt.Date));
            }

            var source = new EfaturamInvoicePayloadSource(
                RequiredText(root, "Id"), invoiceType, RequiredText(root, "Currency"), RequiredText(root, "Note"), RequiredText(order, "OrderNumber"),
                DateOnly.FromDateTime(orderedAt.Date), DateTimeOffset.Parse(RequiredText(root, "IssuedAt")),
                new(taxId, Text(address, "countryCode") is { Length: > 0 } country ? country : "TR", Text(address, "city"), Text(address, "district"),
                    Text(address, "fullAddress", "address1", "addressText", "address"), NullText(address, "postalCode"), NullText(address, "phone"),
                    NullText(address, "email") ?? NullText(customer.RootElement, "customerEmail"), NullText(address, "firstName") ?? NullText(customer.RootElement, "customerFirstName"),
                    NullText(address, "lastName") ?? NullText(customer.RootElement, "customerLastName"), NullText(address, "taxOffice")),
                lines, payment, delivery);
            return TrendyolEFaturamInvoicePayload.Create(account, source);
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
    private static bool ValidTaxId(string value) => value.Length is 10 or 11 && value.All(char.IsAsciiDigit);
    private static string Text(JsonElement parent, params string[] names)
    {
        foreach (var name in names)
            if (TryGetProperty(parent, name, out var value) && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
                return value.ToString().Trim();
        return "";
    }
    private static bool TryGetProperty(JsonElement parent, string name, out JsonElement value)
    {
        if (parent.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in parent.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }
        value = default;
        return false;
    }
    private static string? NullText(JsonElement parent, params string[] names) => Text(parent, names) is { Length: > 0 } value ? value : null;
    private static string Unit(string value) => value.Trim().ToUpperInvariant() switch { "ADET" or "C62" => "C62", _ => throw new JsonException("EFATURAM_UNIT_CODE_UNSUPPORTED") };
}
