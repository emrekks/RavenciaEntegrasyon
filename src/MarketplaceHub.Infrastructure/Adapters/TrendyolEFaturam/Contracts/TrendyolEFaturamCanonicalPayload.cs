using System.Globalization;
using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;

namespace MarketplaceHub.Infrastructure.Adapters.TrendyolEFaturam.Contracts;

public static class TrendyolEFaturamCanonicalPayload
{
    public static bool IsCorporateRecipient(string customerSnapshotJson, string invoiceAddressSnapshotJson, string taxId)
    {
        try
        {
            using var customer = JsonDocument.Parse(customerSnapshotJson);
            using var address = JsonDocument.Parse(invoiceAddressSnapshotJson);
            return IsCorporateRecipient(customer.RootElement, address.RootElement, taxId);
        }
        catch (JsonException) { return taxId.Length == 10 && taxId.All(char.IsAsciiDigit); }
    }

    /// <summary>
    /// The recipient tax identifier exactly as <see cref="Create"/> sends it, for validation before submission.
    /// Returns an empty identifier when the snapshots cannot be read.
    /// </summary>
    public static (string TaxId, bool IsCorporate) ResolveRecipientTaxIdentity(string customerSnapshotJson, string invoiceAddressSnapshotJson, string invoiceType)
    {
        try
        {
            using var customer = JsonDocument.Parse(customerSnapshotJson);
            using var addressSnapshot = JsonDocument.Parse(invoiceAddressSnapshotJson);
            using var normalizedAddress = NormalizeAddressSnapshot(addressSnapshot.RootElement);
            return RecipientTaxIdentity(customer.RootElement, normalizedAddress.RootElement, invoiceType);
        }
        catch (JsonException) { return ("", false); }
    }

    /// <summary>
    /// The recipient name exactly as <see cref="Create"/> sends it (name and surname), so the invoice preview shows the
    /// person or company the invoice is issued to: the invoice address before the account holder. Null when the
    /// snapshots cannot be read or hold no usable name.
    /// </summary>
    public static string? ResolveRecipientName(string customerSnapshotJson, string invoiceAddressSnapshotJson, string invoiceType)
    {
        try
        {
            using var customer = JsonDocument.Parse(string.IsNullOrWhiteSpace(customerSnapshotJson) ? "{}" : customerSnapshotJson);
            using var addressSnapshot = JsonDocument.Parse(string.IsNullOrWhiteSpace(invoiceAddressSnapshotJson) ? "{}" : invoiceAddressSnapshotJson);
            using var normalizedAddress = NormalizeAddressSnapshot(addressSnapshot.RootElement);
            var addressRoot = normalizedAddress.RootElement;
            var (_, isCorporate) = RecipientTaxIdentity(customer.RootElement, addressRoot, invoiceType);
            return RecipientName(customer.RootElement, addressRoot, UnwrapAddress(addressRoot), isCorporate) is var (name, surname)
                ? string.Join(' ', new[] { name, surname }.Where(part => !string.IsNullOrWhiteSpace(part)))
                : null;
        }
        catch (JsonException) { return null; }
    }

    // Trendyol order feeds store invoiceAddress as the snapshot root, while other marketplace/local snapshots may wrap it
    // in invoiceAddress or address. Accept both shapes so valid recipient details reach the fiscal payload.
    private static JsonElement UnwrapAddress(JsonElement addressRoot)
    {
        var address = addressRoot;
        for (var depth = 0; depth < 3; depth++)
        {
            if (!TryGetObjectProperty(address, out var wrappedAddress, "invoiceAddress", "invoice", "address")) break;
            address = wrappedAddress;
        }
        return address;
    }

    // E-Faturam requires recipientInfo.name. Hepsiburada stores the recipient's full name as `name` on the flat
    // invoice-address snapshot, rather than splitting it into firstName/lastName. Null when no name of two letters is found.
    private static (string Name, string? Surname)? RecipientName(JsonElement customer, JsonElement addressRoot, JsonElement address, bool isCorporate)
    {
        var recipientFirstName = isCorporate
            ? null
            : NullText(address, "firstName")
                ?? NullText(addressRoot, "firstName")
                ?? NullText(customer, "customerFirstName", "firstName");
        var recipientName = isCorporate
            ? NullText(address, "companyName", "companyTitle", "businessName", "legalName", "tradeName", "company", "business", "name", "fullName", "recipientName", "firstName")
                ?? NullText(addressRoot, "companyName", "companyTitle", "businessName", "legalName", "tradeName", "company", "business", "name", "fullName", "recipientName", "firstName")
                ?? NullText(customer, "companyName", "companyTitle", "businessName", "legalName", "tradeName", "company", "business", "name", "fullName", "recipientName", "customerName", "customerFirstName", "firstName")
            : recipientFirstName
                ?? NullText(address, "name", "fullName", "recipientName")
                ?? NullText(addressRoot, "name", "fullName", "recipientName")
                ?? NullText(customer, "name", "fullName", "recipientName", "customerName");
        if (recipientName is not { Length: >= 2 }) return null;
        var recipientSurname = isCorporate
            ? null
            : NullText(address, "lastName", "surname", "familyName")
                ?? NullText(addressRoot, "lastName", "surname", "familyName")
                ?? NullText(customer, "customerLastName", "lastName", "surname", "familyName");
        if (!isCorporate && recipientSurname is null && recipientFirstName is null)
            (recipientName, recipientSurname) = SplitFullName(recipientName);
        return (recipientName, recipientSurname);
    }

    private static (string TaxId, bool IsCorporate) RecipientTaxIdentity(JsonElement customer, JsonElement addressRoot, string invoiceType)
    {
        var taxId = FindTaxId(addressRoot);
        if (!ValidTaxId(taxId))
            taxId = FindTaxId(customer);
        var isCorporate = IsCorporateRecipient(customer, addressRoot, taxId);
        // Apply the consumer e-Archive identifier uniformly to orders from every supported marketplace.
        if (invoiceType == "EARSIVFATURA" && !isCorporate) taxId = "11111111111";
        return (taxId, isCorporate);
    }

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
            using var normalizedAddress = NormalizeAddressSnapshot(addressSnapshot.RootElement);
            var addressRoot = normalizedAddress.RootElement;
            var address = UnwrapAddress(addressRoot);
            var invoiceType = RequiredText(root, "InvoiceType");
            var (taxId, isCorporate) = RecipientTaxIdentity(customer.RootElement, addressRoot, invoiceType);
            if (!ValidTaxId(taxId))
                throw new JsonException("EFATURAM_RECIPIENT_TAX_ID_REQUIRED");

            var (recipientName, recipientSurname) = RecipientName(customer.RootElement, addressRoot, address, isCorporate)
                ?? throw new JsonException("EFATURAM_RECIPIENT_NAME_REQUIRED");

            var lines = RequiredArray(root, "Lines").EnumerateArray().Select(line =>
            {
                var total = Decimal(line, "LineTotal");
                var vat = Decimal(line, "VatAmount");
                return new EfaturamInvoiceLine(
                    RequiredText(line, "DescriptionSnapshot"), Unit(Text(line, "UnitSnapshot")), Decimal(line, "Quantity"), Decimal(line, "UnitPrice"),
                    decimal.Round(total - vat, 2, MidpointRounding.AwayFromZero), vat, Decimal(line, "VatRate"), Decimal(line, "DiscountAmount"), total);
            }).ToArray();

            var orderedAt = DateTimeOffset.Parse(RequiredText(order, "OrderedAt"), CultureInfo.InvariantCulture);
            var includeInternetSalesInfo = OptionalBoolean(root, "IncludeInternetSalesInfo", false);
            EfaturamPayment? payment = null;
            EfaturamDelivery? delivery = null;
            if (invoiceType == "EARSIVFATURA")
            {
                var package = RequiredObject(root, "Package");
                var cargoProvider = RequiredText(package, "CargoProviderExternalId");
                if (!TrendyolCarrierCatalog.TryResolve(cargoProvider, out var carrier))
                    throw new JsonException("EFATURAM_CARRIER_CATALOG_MISS");
                var sentAt = DateTimeOffset.Parse(RequiredText(package, "StatusOccurredAt"), CultureInfo.InvariantCulture);
                if (includeInternetSalesInfo)
                    payment = MarketplacePayment(root, orderedAt, customer.RootElement);
                // Stored instants are UTC; 01:30 in Istanbul is still the previous UTC day.
                delivery = new(carrier.TaxId, carrier.Name, null, EfaturamTurkeyTime.Day(sentAt));
            }

            // An e-Invoice goes to the alias the taxpayer lookup chose right before this request; without one it is not sent.
            var targetAlias = invoiceType == "TEMELFATURA" ? NullText(root, "TargetAlias") ?? throw new JsonException("EFATURAM_TARGET_ALIAS_REQUIRED") : null;
            var source = new EfaturamInvoicePayloadSource(
                RequiredText(root, "Id"), invoiceType, RequiredText(root, "Currency"), RequiredText(root, "Note"), RequiredText(order, "OrderNumber"),
                EfaturamTurkeyTime.Day(orderedAt), DateTimeOffset.Parse(RequiredText(root, "IssuedAt"), CultureInfo.InvariantCulture),
                new(taxId, Text(address, "countryCode") is { Length: > 0 } country ? country : "TR", Text(address, "city", "cityName", "province"), Text(address, "district", "districtName", "town", "county"),
                    WithoutMarketplaceIds(Text(address, "fullAddress", "address1", "addressText", "address")), NullText(address, "postalCode"), NullText(address, "phone"),
                    NullText(address, "email") ?? NullText(customer.RootElement, "customerEmail"), recipientName,
                    recipientSurname, NullText(address, "taxOffice") ?? NullText(addressRoot, "taxOffice") ?? NullText(customer.RootElement, "taxOffice")),
                lines, payment, delivery, targetAlias,
                NullText(root, "InvoiceUuid") is { } invoiceUuid ? (Guid.TryParse(invoiceUuid, out var parsedUuid) ? parsedUuid : throw new JsonException("EFATURAM_INVOICE_UUID_INVALID")) : null);
            return TrendyolEFaturamInvoicePayload.Create(account, source);
        }
        finally
        {
            customer.Dispose();
            addressSnapshot.Dispose();
        }
    }

    private static JsonDocument ParseSnapshot(JsonElement parent, string name) => JsonDocument.Parse(RequiredText(parent, name));
    private static EfaturamPayment MarketplacePayment(JsonElement root, DateTimeOffset orderedAt, JsonElement customer) =>
        InternetSalePayment(Text(root, "MarketplacePlatformCode"), customer, orderedAt, out var problem)
            ?? throw new JsonException(problem ?? "EFATURAM_MARKETPLACE_PAYMENT_INFO_UNSUPPORTED");

    /// <summary>
    /// The E-Arşiv internet sale's payment information (report item 18), the same structure for every marketplace:
    /// E-Faturam needs the site the sale was made on (purchaseUrl) and the payment means; the payment agent is optional
    /// (efaturam-api-son.md, EArchivePaymentInfo). A marketplace sale is paid through the marketplace (MEDIATOR). A Shopify
    /// sale is made on the shop's primary domain and paid by the means its payment gateways name. Null, with the reason,
    /// when it cannot be filled: the preview then turns the option off instead of failing the invoice.
    /// </summary>
    public static EfaturamPayment? InternetSalePayment(string? platformCode, string customerSnapshotJson, DateTimeOffset orderedAt, out string? problem)
    {
        try
        {
            using var customer = JsonDocument.Parse(string.IsNullOrWhiteSpace(customerSnapshotJson) ? "{}" : customerSnapshotJson);
            return InternetSalePayment(platformCode, customer.RootElement, orderedAt, out problem);
        }
        catch (JsonException)
        {
            problem = ShopifyPaymentInfoMissing;
            return null;
        }
    }

    public const string ShopifyPaymentInfoMissing = "EFATURAM_SHOPIFY_PAYMENT_INFO_UNVERIFIED";

    private static EfaturamPayment? InternetSalePayment(string? platformCode, JsonElement customer, DateTimeOffset orderedAt, out string? problem)
    {
        problem = null;
        switch (platformCode?.Trim().ToUpperInvariant())
        {
            case null or "" or "TRENDYOL": return new("https://www.trendyol.com", "Trendyol", "PAZARYERI", orderedAt, "MEDIATOR");
            case "HEPSIBURADA": return new("https://www.hepsiburada.com", "Hepsiburada", "PAZARYERI", orderedAt, "MEDIATOR");
            case "N11": return new("https://www.n11.com", "n11", "PAZARYERI", orderedAt, "MEDIATOR");
            case "PAZARAMA": return new("https://www.pazarama.com", "Pazarama", "PAZARYERI", orderedAt, "MEDIATOR");
            case "PTTAVM": return new("https://www.pttavm.com", "PTT AVM", "PAZARYERI", orderedAt, "MEDIATOR");
            case "SHOPIFY":
                var site = NullText(customer, "shopPrimaryDomainUrl");
                var means = ShopifyPaymentMeans(customer.ValueKind == JsonValueKind.Object && customer.TryGetProperty("paymentGatewayNames", out var names) && names.ValueKind == JsonValueKind.Array
                    ? names.EnumerateArray().Where(name => name.ValueKind == JsonValueKind.String).Select(name => name.GetString() ?? "").ToArray()
                    : []);
                if (site is null || !Uri.TryCreate(site, UriKind.Absolute, out var siteUri) || siteUri.Scheme != Uri.UriSchemeHttps || means is null)
                {
                    problem = ShopifyPaymentInfoMissing;
                    return null;
                }
                return new(siteUri.GetLeftPart(UriPartial.Authority), null, null, orderedAt, means);
            default:
                problem = "EFATURAM_MARKETPLACE_PAYMENT_INFO_UNSUPPORTED";
                return null;
        }
    }

    /// <summary>
    /// The E-Faturam payment means of a Shopify order from its payment gateway names: cash on delivery ON_DELIVERY, a bank
    /// transfer EFT, a card gateway (Shopify Payments, iyzico, PayTR, Stripe, a "card" gateway) CREDIT_CARD, anything else
    /// (a manual payment, gateways that disagree) OTHER. Null when the order names no gateway.
    /// </summary>
    public static string? ShopifyPaymentMeans(IReadOnlyCollection<string> gatewayNames)
    {
        var means = gatewayNames.Where(name => !string.IsNullOrWhiteSpace(name)).Select(name =>
        {
            var value = name.Trim().ToLowerInvariant().Replace('_', ' ');
            if (value.Contains("cash on delivery", StringComparison.Ordinal) || value is "cod" || value.Contains("(cod)", StringComparison.Ordinal)
                || value.Contains("kapıda", StringComparison.Ordinal) || value.Contains("kapida", StringComparison.Ordinal)) return "ON_DELIVERY";
            if (value.Contains("bank deposit", StringComparison.Ordinal) || value.Contains("bank transfer", StringComparison.Ordinal)
                || value.Contains("havale", StringComparison.Ordinal) || value.Contains("eft", StringComparison.Ordinal)) return "EFT";
            if (value.Contains("shopify payments", StringComparison.Ordinal) || value.Contains("iyzico", StringComparison.Ordinal) || value.Contains("paytr", StringComparison.Ordinal)
                || value.Contains("stripe", StringComparison.Ordinal) || value.Contains("credit", StringComparison.Ordinal) || value.Contains("card", StringComparison.Ordinal)
                || value.Contains("kredi", StringComparison.Ordinal) || value.Contains("kart", StringComparison.Ordinal)) return "CREDIT_CARD";
            return "OTHER";
        }).Distinct(StringComparer.Ordinal).ToList();
        return means.Count switch
        {
            0 => null,
            1 => means[0],
            _ => "OTHER"
        };
    }

    private static bool OptionalBoolean(JsonElement parent, string name, bool fallback) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : fallback;
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
    // A null or text amount is a missing amount (a validation error), not an InvalidOperationException from TryGetDecimal.
    private static decimal Decimal(JsonElement parent, string name) => parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number) ? number : throw new JsonException($"{name} missing");
    private static bool ValidTaxId(string value) => value.Length is 10 or 11 && value.All(char.IsAsciiDigit);
    // Report item 12, one rule for every marketplace: the buyer kind the marketplace states decides (Trendyol commercial,
    // n11 and Pazarama invoiceType, PTT AVM FaturaTip, Hepsiburada's 11111111111 identity for an individual buyer). Without
    // such a flag the tax identity decides: a 10-digit VKN is a company, an 11-digit TCKN a person. Without either, a
    // company name (Shopify sends one with no tax number) makes the buyer corporate, and the invoice then stops for the
    // owner because a corporate invoice needs the tax number; with no company name the buyer is a person.
    private static bool IsCorporateRecipient(JsonElement customer, JsonElement address, string taxId) =>
        RecipientKind(customer, address, taxId).IsCorporate;

    private enum BuyerKindSource { MarketplaceFlag, TaxNumber, IdentityNumber, CompanyName, NoInformation }

    // The one buyer-kind rule (see above), with what decided it; the invoice and the order's label both read it.
    private static (bool IsCorporate, BuyerKindSource Source) RecipientKind(JsonElement customer, JsonElement address, string taxId)
    {
        if ((ExplicitBuyerKind(customer) ?? ExplicitBuyerKind(address)) is { } stated) return (stated, BuyerKindSource.MarketplaceFlag);
        if (HasCorporateMarker(customer) || HasCorporateMarker(address)) return (true, BuyerKindSource.MarketplaceFlag);
        if (HasIndividualMarker(customer) || HasIndividualMarker(address)) return (false, BuyerKindSource.MarketplaceFlag);
        if (ValidTaxId(taxId)) return taxId.Length == 10 ? (true, BuyerKindSource.TaxNumber) : (false, BuyerKindSource.IdentityNumber);
        return HasCompanyDetails(customer) || HasCompanyDetails(address) ? (true, BuyerKindSource.CompanyName) : (false, BuyerKindSource.NoInformation);
    }

    /// <summary>
    /// The order's buyer kind for the order list and detail, by exactly the rule the invoice uses (report item 12):
    /// Individual, Corporate, or CorporateTaxIdMissing when the buyer is corporate but no tax number is on the order
    /// (the invoice then stops and asks the owner), with a short Turkish reason. Unreadable snapshots count as
    /// individual, as they do for the invoice.
    /// </summary>
    public static OrderBuyerTypeView ResolveBuyerType(string? customerSnapshotJson, string? invoiceAddressSnapshotJson)
    {
        bool isCorporate;
        BuyerKindSource source;
        string taxId;
        try
        {
            using var customer = JsonDocument.Parse(string.IsNullOrWhiteSpace(customerSnapshotJson) ? "{}" : customerSnapshotJson);
            using var addressSnapshot = JsonDocument.Parse(string.IsNullOrWhiteSpace(invoiceAddressSnapshotJson) ? "{}" : invoiceAddressSnapshotJson);
            using var normalizedAddress = NormalizeAddressSnapshot(addressSnapshot.RootElement);
            var addressRoot = normalizedAddress.RootElement;
            // The same tax number the invoice sends (RecipientTaxIdentity): the invoice address first, then the customer.
            taxId = FindTaxId(addressRoot);
            if (!ValidTaxId(taxId)) taxId = FindTaxId(customer.RootElement);
            (isCorporate, source) = RecipientKind(customer.RootElement, addressRoot, taxId);
        }
        catch (JsonException)
        {
            return new(OrderBuyerTypeView.Individual, "Bilgi yok, bireysel sayıldı");
        }

        if (!isCorporate)
            return new(OrderBuyerTypeView.Individual, source switch
            {
                BuyerKindSource.MarketplaceFlag => "Pazaryeri bireysel olarak işaretledi",
                BuyerKindSource.IdentityNumber => "TC kimlik numarası var",
                _ => "Bilgi yok, bireysel sayıldı"
            });
        if (!ValidTaxId(taxId))
            return new(OrderBuyerTypeView.CorporateTaxIdMissing, source == BuyerKindSource.MarketplaceFlag
                ? "Pazaryeri kurumsal olarak işaretledi, vergi numarası yok — fatura kesilirken sorulacak"
                : "Şirket adı var, vergi numarası yok — fatura kesilirken sorulacak");
        // The invoice also checks the number's check digits (InvoicingBillingService.RecipientTaxIdentityIsValid).
        if (!TurkishTaxIdentity.IsValid(taxId))
            return new(OrderBuyerTypeView.CorporateTaxIdMissing, "Vergi numarası geçersiz — fatura kesilirken sorulacak");
        return new(OrderBuyerTypeView.Corporate, source == BuyerKindSource.MarketplaceFlag ? "Pazaryeri kurumsal olarak işaretledi" : "Vergi numarası var");
    }

    /// <summary>
    /// The buyer kind the marketplace states on the order: true corporate, false individual, null when it says nothing
    /// (report item 12).
    /// </summary>
    public static bool? ExplicitBuyerKind(string customerSnapshotJson, string invoiceAddressSnapshotJson)
    {
        try
        {
            using var customer = JsonDocument.Parse(string.IsNullOrWhiteSpace(customerSnapshotJson) ? "{}" : customerSnapshotJson);
            using var address = JsonDocument.Parse(string.IsNullOrWhiteSpace(invoiceAddressSnapshotJson) ? "{}" : invoiceAddressSnapshotJson);
            return ExplicitBuyerKind(customer.RootElement) ?? ExplicitBuyerKind(address.RootElement);
        }
        catch (JsonException) { return null; }
    }

    // Trendyol commercial (true/false); n11 and Pazarama invoiceType (1 individual, 2 corporate); PTT AVM FaturaTip, kept
    // as invoiceType ("Bireysel", "Kurumsal"); Hepsiburada's identityNo 11111111111, which it sends only for an individual
    // buyer (hepsiburada-api-son.md:13394). Searched a few levels deep: n11 keeps the billing address inside the order.
    private static bool? ExplicitBuyerKind(JsonElement source)
    {
        bool? commercial = null, invoiceType = null, placeholderIdentity = null;
        Visit(source, 0);
        return commercial ?? invoiceType ?? placeholderIdentity;

        void Visit(JsonElement node, int depth)
        {
            if (depth > 3) return;
            if (node.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in node.EnumerateArray()) Visit(item, depth + 1);
                return;
            }
            if (node.ValueKind != JsonValueKind.Object) return;
            foreach (var property in node.EnumerateObject())
            {
                var name = property.Name;
                if (name.Equals("commercial", StringComparison.OrdinalIgnoreCase))
                    commercial ??= BooleanValue(property.Value);
                else if (name.Equals("invoiceType", StringComparison.OrdinalIgnoreCase) || name.Equals("faturaTip", StringComparison.OrdinalIgnoreCase) || name.Equals("faturaType", StringComparison.OrdinalIgnoreCase))
                    invoiceType ??= BuyerKindWord(property.Value);
                else if ((name.Equals("identityNo", StringComparison.OrdinalIgnoreCase) || name.Equals("turkishIdentityNumber", StringComparison.OrdinalIgnoreCase))
                    && property.Value.ValueKind == JsonValueKind.String && property.Value.GetString()?.Trim() == "11111111111")
                    placeholderIdentity ??= false;
                else if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                    Visit(property.Value, depth + 1);
            }
        }
    }

    private static bool? BuyerKindWord(JsonElement value)
    {
        var text = value.ValueKind switch
        {
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.String => value.GetString(),
            _ => null
        };
        return text?.Trim().ToLowerInvariant() switch
        {
            "1" or "bireysel" or "individual" or "gerçek kişi" or "gercek kisi" => false,
            "2" or "kurumsal" or "corporate" or "tüzel kişi" or "tuzel kisi" => true,
            _ => null
        };
    }

    private static bool HasCorporateMarker(JsonElement source, int depth = 0)
    {
        if (depth > 5) return false;
        if (source.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in source.EnumerateObject())
            {
                var name = property.Name;
                // Trendyol marks a corporate buyer with commercial=true on the order package.
                if (name.Equals("isCorporate", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("corporate", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("commercial", StringComparison.OrdinalIgnoreCase)
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
    private static readonly System.Text.RegularExpressions.Regex MarketplaceId = new(@"[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\s*[,;:-]?\s*", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    // Marketplace feeds sometimes prefix the street text with the address record id; it must not reach the fiscal document.
    private static string WithoutMarketplaceIds(string value) => MarketplaceId.Replace(value, string.Empty).Trim();
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
    private static (string Name, string? Surname) SplitFullName(string name)
    {
        var separator = name.LastIndexOf(' ');
        if (separator <= 0 || separator == name.Length - 1) return (name, null);

        var firstName = name[..separator].Trim();
        var surname = name[(separator + 1)..].Trim();
        return firstName.Length >= 2 && surname.Length >= 2 ? (firstName, surname) : (name, null);
    }
    private static string Unit(string value) => value.Trim().ToUpperInvariant() switch { "ADET" or "C62" => "C62", _ => throw new JsonException("EFATURAM_UNIT_CODE_UNSUPPORTED") };
}
