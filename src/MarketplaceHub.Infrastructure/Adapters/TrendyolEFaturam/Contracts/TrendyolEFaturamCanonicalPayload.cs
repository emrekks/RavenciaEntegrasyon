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
            using var normalizedAddress = NormalizeAddressSnapshot(addressSnapshot.RootElement);
            var addressRoot = normalizedAddress.RootElement;
            var address = addressRoot;
            for (var depth = 0; depth < 3; depth++)
            {
                if (!TryGetObjectProperty(address, out var wrappedAddress, "invoiceAddress", "invoice", "address")) break;
                address = wrappedAddress;
            }
            var invoiceType = RequiredText(root, "InvoiceType");
            var taxId = Text(address, "taxNumber", "invoiceTaxNumber", "identityNumber", "identityNo", "tcIdentityNumber", "turkishIdentityNumber", "taxId", "taxIdentifier", "nationalIdentityNumber", "tckn", "vkn");
            if (!ValidTaxId(taxId))
                taxId = FindTaxId(addressRoot);
            if (!ValidTaxId(taxId))
                taxId = FindTaxId(customer.RootElement);
            if (!ValidTaxId(taxId))
            {
                // GİB e-Arşiv guidance permits this sentinel when the buyer's TCKN is not required.
                // Restrict it to consumer/e-Archive cases; corporate and e-Invoice records still need a real VKN/TCKN.
                if (invoiceType == "EARSIVFATURA" && !IsCorporateRecipient(addressRoot, address, customer.RootElement))
                    taxId = "11111111111";
                else
                    throw new JsonException("EFATURAM_RECIPIENT_TAX_ID_REQUIRED");
            }

            // E-Faturam requires recipientInfo.name. Hepsiburada stores the
            // recipient's full name as `name` on the flat invoice-address snapshot,
            // rather than splitting it into firstName/lastName.
            var recipientName = NullText(address, "firstName", "name", "fullName", "recipientName", "companyTitle", "businessName", "legalName", "tradeName", "companyName")
                ?? NullText(addressRoot, "firstName", "name", "fullName", "recipientName", "companyTitle", "businessName", "legalName", "tradeName", "companyName")
                ?? NullText(customer.RootElement, "customerFirstName", "firstName", "name", "fullName", "recipientName", "customerName", "companyTitle", "businessName", "legalName", "tradeName", "companyName");
            if (recipientName is not { Length: >= 2 })
                throw new JsonException("EFATURAM_RECIPIENT_NAME_REQUIRED");
            var recipientSurname = NullText(address, "lastName", "surname", "familyName")
                ?? NullText(addressRoot, "lastName", "surname", "familyName")
                ?? NullText(customer.RootElement, "customerLastName", "lastName", "surname", "familyName");

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
                    NullText(address, "email") ?? NullText(customer.RootElement, "customerEmail"), recipientName,
                    recipientSurname, NullText(address, "taxOffice")),
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
    private static JsonDocument NormalizeAddressSnapshot(JsonElement snapshot)
    {
        if (snapshot.ValueKind == JsonValueKind.Object)
            return JsonDocument.Parse(snapshot.GetRawText());

        if (snapshot.ValueKind == JsonValueKind.String)
        {
            var text = snapshot.GetString()?.Trim() ?? "";
            if (text.Length > 0)
            {
                try
                {
                    using var nested = JsonDocument.Parse(text);
                    if (nested.RootElement.ValueKind == JsonValueKind.Object)
                        return JsonDocument.Parse(nested.RootElement.GetRawText());
                }
                catch (JsonException) { }
            }

            return JsonDocument.Parse(JsonSerializer.Serialize(new { address = text }));
        }

        throw new JsonException("invoiceAddress missing");
    }
    private static JsonElement RequiredObject(JsonElement parent, string name) => parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object ? value : throw new JsonException($"{name} missing");
    private static JsonElement RequiredArray(JsonElement parent, string name) => parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array ? value : throw new JsonException($"{name} missing");
    private static string RequiredText(JsonElement parent, string name) => Text(parent, name) is { Length: > 0 } value ? value : throw new JsonException($"{name} missing");
    private static decimal Decimal(JsonElement parent, string name) => parent.TryGetProperty(name, out var value) && value.TryGetDecimal(out var number) ? number : throw new JsonException($"{name} missing");
    private static bool ValidTaxId(string value) => value.Length is 10 or 11 && value.All(char.IsAsciiDigit);
    private static string FindTaxId(JsonElement source, int depth = 0)
    {
        var value = Text(source, "customerTaxNumber", "taxNumber", "invoiceTaxNumber", "identityNumber", "identityNo", "customerIdentityNumber", "tcIdentityNumber", "turkishIdentityNumber", "taxId", "taxIdentifier", "nationalIdentityNumber", "tckn", "vkn");
        if (ValidTaxId(value) || depth >= 5) return value;
        if (source.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in source.EnumerateObject())
            {
                var nested = FindTaxId(property.Value, depth + 1);
                if (ValidTaxId(nested)) return nested;
            }
        }
        else if (source.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in source.EnumerateArray())
            {
                var nested = FindTaxId(item, depth + 1);
                if (ValidTaxId(nested)) return nested;
            }
        }
        return "";
    }
    private static bool IsCorporateRecipient(params JsonElement[] sources)
    {
        var explicitlyIndividual = sources.Any(source =>
        {
            var recipientType = Text(source, "recipientType", "customerType", "entityType", "taxPayerType", "invoiceRecipientType");
            return Boolean(source, "isIndividual", "isPerson", "isConsumer") == true
                || Boolean(source, "isCorporate", "isCompany", "isBusiness", "corporate") == false
                || recipientType.Contains("individual", StringComparison.OrdinalIgnoreCase)
                || recipientType.Contains("person", StringComparison.OrdinalIgnoreCase)
                || recipientType.Contains("consumer", StringComparison.OrdinalIgnoreCase)
                || recipientType.Contains("bireysel", StringComparison.OrdinalIgnoreCase)
                || recipientType.Contains("gerçek", StringComparison.OrdinalIgnoreCase);
        });
        foreach (var source in sources)
        {
            if (Boolean(source, "isCorporate", "isCompany", "isBusiness", "corporate") == true) return true;
            var recipientType = Text(source, "recipientType", "customerType", "entityType", "taxPayerType", "invoiceRecipientType");
            if (recipientType.Contains("corporate", StringComparison.OrdinalIgnoreCase)
                || recipientType.Contains("company", StringComparison.OrdinalIgnoreCase)
                || recipientType.Contains("business", StringComparison.OrdinalIgnoreCase)
                || recipientType.Contains("kurumsal", StringComparison.OrdinalIgnoreCase)
                || recipientType.Contains("tüzel", StringComparison.OrdinalIgnoreCase)) return true;
            if (new[] { "companyTitle", "businessName", "legalName", "tradeName", "taxOffice" }
                .Any(name => Text(source, name).Length > 0)) return true;
            var taxNumber = Text(source, "vkn", "taxNumber", "invoiceTaxNumber", "taxId", "taxIdentifier");
            if (taxNumber.Length == 10 && taxNumber.All(char.IsAsciiDigit)) return true;
            var companyName = Text(source, "companyName");
            var personName = Text(source, "fullName", "name", "recipientName", "firstName");
            if (!explicitlyIndividual && companyName.Length > 0 && !string.Equals(companyName, personName, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
    private static bool? Boolean(JsonElement parent, params string[] names)
    {
        foreach (var name in names)
        {
            if (!TryGetProperty(parent, name, out var value)) continue;
            return value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.String when bool.TryParse(value.GetString(), out var parsed) => parsed,
                _ => null
            };
        }
        return null;
    }
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
    private static bool TryGetObjectProperty(JsonElement parent, out JsonElement value, params string[] names)
    {
        foreach (var name in names)
        {
            if (TryGetProperty(parent, name, out var candidate) && candidate.ValueKind == JsonValueKind.Object)
            {
                value = candidate;
                return true;
            }
        }
        value = default;
        return false;
    }
    private static string? NullText(JsonElement parent, params string[] names) => Text(parent, names) is { Length: > 0 } value ? value : null;
    private static string Unit(string value) => value.Trim().ToUpperInvariant() switch { "ADET" or "C62" => "C62", _ => throw new JsonException("EFATURAM_UNIT_CODE_UNSUPPORTED") };
}
