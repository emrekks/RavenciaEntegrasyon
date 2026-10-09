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
            var taxId = FindTaxId(addressRoot);
            if (!ValidTaxId(taxId))
                taxId = FindTaxId(customer.RootElement);
            var isCorporate = IsCorporateRecipient(customer.RootElement, addressRoot, taxId);
            if (invoiceType == "EARSIVFATURA" && !isCorporate)
            {
                // Apply the consumer e-Archive identifier uniformly to orders
                // from every supported marketplace.
                taxId = "11111111111";
            }
            else if (!ValidTaxId(taxId))
            {
                throw new JsonException("EFATURAM_RECIPIENT_TAX_ID_REQUIRED");
            }

            // E-Faturam requires recipientInfo.name. Hepsiburada stores the
            // recipient's full name as `name` on the flat invoice-address snapshot,
            // rather than splitting it into firstName/lastName.
            var recipientName = isCorporate
                ? NullText(address, "companyName", "companyTitle", "businessName", "legalName", "tradeName", "company", "business", "name", "fullName", "recipientName", "firstName")
                    ?? NullText(addressRoot, "companyName", "companyTitle", "businessName", "legalName", "tradeName", "company", "business", "name", "fullName", "recipientName", "firstName")
                    ?? NullText(customer.RootElement, "companyName", "companyTitle", "businessName", "legalName", "tradeName", "company", "business", "name", "fullName", "recipientName", "customerName", "customerFirstName", "firstName")
                : NullText(address, "firstName", "name", "fullName", "recipientName")
                    ?? NullText(addressRoot, "firstName", "name", "fullName", "recipientName")
                    ?? NullText(customer.RootElement, "customerFirstName", "firstName", "name", "fullName", "recipientName", "customerName");
            if (recipientName is not { Length: >= 2 })
                throw new JsonException("EFATURAM_RECIPIENT_NAME_REQUIRED");
            var recipientSurname = isCorporate
                ? null
                : NullText(address, "lastName", "surname", "familyName")
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
                    recipientSurname, NullText(address, "taxOffice") ?? NullText(addressRoot, "taxOffice") ?? NullText(customer.RootElement, "taxOffice")),
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
    private static bool IsCorporateRecipient(JsonElement customer, JsonElement address, string taxId)
    {
        if (HasCompanyDetails(customer) || HasCompanyDetails(address)) return true;
        if (HasCorporateMarker(customer) || HasCorporateMarker(address)) return true;
        if (HasIndividualMarker(customer) || HasIndividualMarker(address)) return false;
        // Ten-digit Turkish VKN values identify companies. Eleven-digit values
        // are treated as personal IDs unless source fields say otherwise.
        return taxId.Length == 10 && taxId.All(char.IsAsciiDigit);
    }

    private static bool HasCorporateMarker(JsonElement source, int depth = 0)
    {
        if (depth > 5) return false;
        if (source.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in source.EnumerateObject())
            {
                var name = property.Name;
                if (name.Equals("isCorporate", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("corporate", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("isCompany", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("company", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("isBusiness", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("business", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("isLegalEntity", StringComparison.OrdinalIgnoreCase))
                {
                    if (BooleanValue(property.Value) == true) return true;
                }
                else if (name.Equals("customerType", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("invoiceCustomerType", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("invoiceType", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("customerCategory", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("entityType", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("marketplaceInvoiceStatus", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("buyerType", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("recipientType", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("personType", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("type", StringComparison.OrdinalIgnoreCase))
                {
                    var value = property.Value.ToString().Trim().ToLowerInvariant();
                    if (value.Contains("kurumsal", StringComparison.Ordinal)
                        || value.Contains("corporate", StringComparison.Ordinal)
                        || value.Contains("company", StringComparison.Ordinal)
                        || value.Contains("business", StringComparison.Ordinal)
                        || value.Contains("tuzelkisi", StringComparison.Ordinal)
                        || value.Contains("tüzel kişi", StringComparison.Ordinal)) return true;
                }
                if (HasCorporateMarker(property.Value, depth + 1)) return true;
            }
        }
        else if (source.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in source.EnumerateArray())
                if (HasCorporateMarker(item, depth + 1)) return true;
        }
        return false;
    }

    private static bool HasCompanyDetails(JsonElement source, int depth = 0)
    {
        if (depth > 5) return false;
        if (source.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in source.EnumerateObject())
            {
                if ((property.Name.Equals("companyName", StringComparison.OrdinalIgnoreCase)
                     || property.Name.Equals("companyTitle", StringComparison.OrdinalIgnoreCase)
                     || property.Name.Equals("businessName", StringComparison.OrdinalIgnoreCase)
                     || property.Name.Equals("legalName", StringComparison.OrdinalIgnoreCase)
                     || property.Name.Equals("tradeName", StringComparison.OrdinalIgnoreCase)
                     || property.Name.Equals("company", StringComparison.OrdinalIgnoreCase)
                     || property.Name.Equals("business", StringComparison.OrdinalIgnoreCase)
                     || property.Name.Equals("taxOffice", StringComparison.OrdinalIgnoreCase))
                    && property.Value.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(property.Value.GetString())) return true;
                if (HasCompanyDetails(property.Value, depth + 1)) return true;
            }
        }
        else if (source.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in source.EnumerateArray())
                if (HasCompanyDetails(item, depth + 1)) return true;
        }
        return false;
    }

    private static bool HasIndividualMarker(JsonElement source, int depth = 0)
    {
        if (depth > 5) return false;
        if (source.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in source.EnumerateObject())
            {
                if (property.Name.Equals("isCorporate", StringComparison.OrdinalIgnoreCase)
                    || property.Name.Equals("corporate", StringComparison.OrdinalIgnoreCase)
                    || property.Name.Equals("isCompany", StringComparison.OrdinalIgnoreCase)
                    || property.Name.Equals("company", StringComparison.OrdinalIgnoreCase)
                    || property.Name.Equals("isBusiness", StringComparison.OrdinalIgnoreCase)
                    || property.Name.Equals("business", StringComparison.OrdinalIgnoreCase)
                    || property.Name.Equals("isLegalEntity", StringComparison.OrdinalIgnoreCase))
                {
                    if (BooleanValue(property.Value) == false) return true;
                }
                else if (property.Name.Equals("isIndividual", StringComparison.OrdinalIgnoreCase)
                    || property.Name.Equals("isPerson", StringComparison.OrdinalIgnoreCase)
                    || property.Name.Equals("individual", StringComparison.OrdinalIgnoreCase))
                {
                    if (BooleanValue(property.Value) == true) return true;
                }
                else if (property.Name.Equals("customerType", StringComparison.OrdinalIgnoreCase)
                    || property.Name.Equals("invoiceCustomerType", StringComparison.OrdinalIgnoreCase)
                    || property.Name.Equals("invoiceType", StringComparison.OrdinalIgnoreCase)
                    || property.Name.Equals("customerCategory", StringComparison.OrdinalIgnoreCase)
                    || property.Name.Equals("entityType", StringComparison.OrdinalIgnoreCase)
                    || property.Name.Equals("marketplaceInvoiceStatus", StringComparison.OrdinalIgnoreCase)
                    || property.Name.Equals("buyerType", StringComparison.OrdinalIgnoreCase)
                    || property.Name.Equals("recipientType", StringComparison.OrdinalIgnoreCase)
                    || property.Name.Equals("personType", StringComparison.OrdinalIgnoreCase)
                    || property.Name.Equals("type", StringComparison.OrdinalIgnoreCase))
                {
                    var value = property.Value.ToString().Trim().ToLowerInvariant();
                    if (value.Contains("bireysel", StringComparison.Ordinal)
                        || value.Contains("individual", StringComparison.Ordinal)
                        || value.Contains("private person", StringComparison.Ordinal)
                        || value.Contains("gerçek kişi", StringComparison.Ordinal)
                        || value.Contains("gercekkisi", StringComparison.Ordinal)) return true;
                }
                if (HasIndividualMarker(property.Value, depth + 1)) return true;
            }
        }
        else if (source.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in source.EnumerateArray())
                if (HasIndividualMarker(item, depth + 1)) return true;
        }
        return false;
    }

    private static bool? BooleanValue(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.True) return true;
        if (value.ValueKind == JsonValueKind.False) return false;
        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString()?.Trim();
            if (string.Equals(text, "true", StringComparison.OrdinalIgnoreCase) || text == "1") return true;
            if (string.Equals(text, "false", StringComparison.OrdinalIgnoreCase) || text == "0") return false;
        }
        return null;
    }

    private static string FindTaxId(JsonElement source, int depth = 0)
    {
        var value = Text(source, "customerTaxNumber", "taxNumber", "invoiceTaxNumber", "identityNumber", "identityNo", "customerIdentityNumber", "tcIdentityNumber", "turkishIdentityNumber", "taxId", "taxIdentifier", "nationalIdentityNumber", "tckn", "vkn");
        if ((ValidTaxId(value) && value != "11111111111") || depth >= 5) return value == "11111111111" ? "" : value;
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
