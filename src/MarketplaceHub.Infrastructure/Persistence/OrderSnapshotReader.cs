using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;

namespace MarketplaceHub.Infrastructure.Persistence;

internal static class OrderSnapshotReader
{
    // The shared catalog key (CatalogKeyNormalizer), so order and return lines find variants stored by the panel
    // or the import under the same key (K1).
    internal static string NormalizeCatalogKey(string? value, int maximum) => CatalogKeyNormalizer.Key(value, maximum);

    // The codes an order or return line may name a linked variant by: the link id and the marketplace's stock code
    // (a Hepsiburada link is keyed by its hbSku and keeps the merchant SKU beside it).
    internal static IEnumerable<string> LinkIdentities(MarketplaceVariantLink link)
    {
        yield return MarketplaceVariantLinkCoverage.Normalize(link.ExternalId);
        if (!string.IsNullOrWhiteSpace(link.ExternalSku)) yield return MarketplaceVariantLinkCoverage.Normalize(link.ExternalSku);
    }

    // The marketplace's own ids for the sold item in a line snapshot (Trendyol productCode/contentId, Hepsiburada
    // hbSku/listingId, Shopify variant_id), which a variant link is keyed by. They still find the linked variant after
    // the barcode or stock code was changed on one side only: Trendyol line MZ052BOD-16 is listing 1696621714, linked
    // to the panel variant still coded MZ051BYC-16, and matched no variant by its codes.
    internal static readonly string[] SourceListingIdFields = ["productCode", "contentId", "hbSku", "listingId", "variantId", "variant_id"];

    internal static string[] LineMarketplaceIdentities(string? sku, string? sourceSnapshotJson)
    {
        var identities = new List<string>();
        var normalizedSku = MarketplaceVariantLinkCoverage.Normalize(sku);
        if (normalizedSku.Length > 0) identities.Add(normalizedSku);
        if (!string.IsNullOrWhiteSpace(sourceSnapshotJson))
        {
            try
            {
                using var document = JsonDocument.Parse(sourceSnapshotJson);
                if (document.RootElement.ValueKind == JsonValueKind.Object)
                    foreach (var field in SourceListingIdFields)
                    {
                        if (!document.RootElement.TryGetProperty(field, out var value)) continue;
                        var text = value.ValueKind switch { JsonValueKind.String => value.GetString(), JsonValueKind.Number => value.GetRawText(), _ => null };
                        var normalized = MarketplaceVariantLinkCoverage.Normalize(text);
                        if (normalized.Length > 0 && normalized != "0" && !identities.Contains(normalized)) identities.Add(normalized);
                    }
            }
            catch (JsonException) { }
        }
        return identities.ToArray();
    }

    internal static string[] LineMarketplaceIdentities(OrderLine line) => LineMarketplaceIdentities(line.Sku, line.SourceSnapshotJson);

    internal static string[] CatalogLookupKeys(string? value, int maximum = 160)
    {
        var normalized = NormalizeCatalogKey(value, maximum);
        if (normalized.Length == 0) return [];
        // Stored variant keys may still be in a legacy form until the deferred recompute; look up those too.
        var keys = CatalogKeyNormalizer.LookupKeys(value, maximum).ToList();
        var withoutMarketplacePadding = normalized.TrimStart('0');
        if (withoutMarketplacePadding.Length > 0
            && withoutMarketplacePadding.Length < normalized.Length
            && withoutMarketplacePadding.Any(char.IsLetter))
            keys.Add(withoutMarketplacePadding);
        return keys.ToArray();
    }

    internal static Dictionary<string, ProductVariant> BarcodeVariantLookup(IEnumerable<ProductVariant> variants) => variants
        .Where(variant => !string.IsNullOrWhiteSpace(variant.Barcode))
        .SelectMany(variant => CatalogLookupKeys(variant.Barcode).Select(key => (Key: key, Variant: variant)))
        .GroupBy(candidate => candidate.Key, StringComparer.OrdinalIgnoreCase)
        .Where(group => group.Select(candidate => candidate.Variant.Id).Distinct().Count() == 1)
        .ToDictionary(group => group.Key, group => group.First().Variant, StringComparer.OrdinalIgnoreCase);

    // The marketplace's own photo address in a line snapshot, ready to show; null when the line carries none.
    internal static string? SourceLineImageUrl(string? json) => NormalizeImageUrl(SourceImageUrl(json ?? "{}"));

    // The order's buyer kind by the invoice's rule (report item 12), and the order type that follows from it: a micro
    // export first, then KURUMSAL for a corporate buyer (with or without its tax number), else BIREYSEL. The kind stored
    // on the order (Order.BuyerType) decides when set, so the label always equals what the "Fatura Türü" filter matched.
    internal static (OrderBuyerTypeView BuyerType, string OrderType) BuyerType(string customerJson, string invoiceAddressJson, bool isMicroExport, string? storedKind = null)
    {
        var buyer = OrderBuyerTypes.Label(storedKind, customerJson, invoiceAddressJson);
        return (buyer, isMicroExport ? "MIKRO_IHRACAT" : buyer.Kind == OrderBuyerTypeView.Individual ? "BIREYSEL" : "KURUMSAL");
    }

    internal static (string Name, string? Email, string? Phone, string? TaxOrIdentityNumber, bool IsMicroExport, bool? IsEInvoiceAvailable) Customer(string customerJson, string invoiceAddressJson, string shipmentAddressJson)
    {
        var name = ResolveCustomerName(customerJson, invoiceAddressJson, shipmentAddressJson);
        var email = JsonText(customerJson, "customerEmail", "email") ?? JsonText(invoiceAddressJson, "email") ?? JsonText(shipmentAddressJson, "email");
        var phone = JsonText(customerJson, "customerPhone", "customerPhoneNumber", "phone", "phoneNumber") ?? JsonText(invoiceAddressJson, "phone", "phoneNumber", "mobilePhone") ?? JsonText(shipmentAddressJson, "phone", "phoneNumber", "mobilePhone");
        var tax = ResolveCustomerTaxOrIdentityNumber(customerJson, invoiceAddressJson, shipmentAddressJson);
        // The "Mikro İhracat" label: the one micro-export rule, which the list's filters and card read too.
        var micro = OrderMicroExportRules.Matches(customerJson);
        var eInvoice = JsonNullableBool(customerJson, "eInvoiceAvailable", "isEInvoice") ?? JsonNullableBool(invoiceAddressJson, "eInvoiceAvailable", "isEInvoice");
        return (name, email, phone, tax, micro, eInvoice);
    }

    internal static readonly HashSet<string> FastDeliveryTypes = new(["TodayDelivery", "SameDayShipping", "FastDelivery"], StringComparer.OrdinalIgnoreCase);

    // The order row's marks, read from the package fields the marketplace sent (top level of the customer snapshot only,
    // never from a line). A field that is missing or holds another value leaves its mark out.
    // - Micro export: the one micro-export rule (OrderMicroExportRules, the "Mikro İhracat" label's), on every platform. micro
    //   and countryCode are independent (Trendyol docs), so a foreign country alone is no micro export. The country is
    //   the invoice address's: the shipment address of a micro order is Trendyol's Istanbul export depot (TR); the
    //   storefront is the fallback.
    // - Delivery point: Trendyol deliveryAddressType=CollectionPoint; n11 any deliveryAddressType (KTN, EASYPOINT, PUP).
    // - Fast delivery: Trendyol fastDeliveryType TodayDelivery/SameDayShipping/FastDelivery, unless the same package
    //   says fastDelivery=false (the two disagree; no mark rather than a wrong one).
    internal static OrderRowMarks? RowMarks(string? platformCode, string? storeFrontCode, string customerJson, string invoiceAddressJson)
    {
        var platform = platformCode?.Trim().ToUpperInvariant();
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(customerJson) ? "{}" : customerJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            string? TopText(string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()?.Trim() : null;
            bool? TopBool(string name) => root.TryGetProperty(name, out var value) ? value.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => null } : null;

            var micro = OrderMicroExportRules.Matches(customerJson);
            var country = micro ? MicroExportCountry(invoiceAddressJson, storeFrontCode) : null;
            OrderRowMarks marks;
            if (platform == "N11")
            {
                var pointType = TopText("deliveryAddressType") is { Length: > 0 } type && !string.Equals(type, "null", StringComparison.OrdinalIgnoreCase) ? type.ToUpperInvariant() : null;
                marks = new(micro, country, pointType is not null, pointType, null);
            }
            else if (platform == "TRENDYOL")
            {
                var deliveryPoint = string.Equals(TopText("deliveryAddressType"), "CollectionPoint", StringComparison.OrdinalIgnoreCase);
                var fastType = TopText("fastDeliveryType") is { } fast && FastDeliveryTypes.TryGetValue(fast, out var known) && TopBool("fastDelivery") != false ? known : null;
                marks = new(micro, country, deliveryPoint, null, fastType);
            }
            else marks = new(micro, country, false, null, null);
            return marks.MicroExport || marks.DeliveryPoint || marks.FastDeliveryType is not null ? marks : null;
        }
        catch (JsonException) { return null; }
    }

    internal static string? MicroExportCountry(string invoiceAddressJson, string? storeFrontCode)
    {
        static string? Code(string? value) => value?.Trim().ToUpperInvariant() is { Length: 2 } code && code.All(char.IsAsciiLetterUpper) && code != "TR" ? code : null;
        string? invoiceCountry = null;
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(invoiceAddressJson) ? "{}" : invoiceAddressJson);
            if (document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("countryCode", out var value) && value.ValueKind == JsonValueKind.String)
                invoiceCountry = value.GetString();
        }
        catch (JsonException) { }
        return Code(invoiceCountry) ?? Code(storeFrontCode);
    }

    internal static string ResolveCustomerName(string customerJson, string invoiceAddressJson, string shipmentAddressJson)
    {
        var customerName = NameFrom(customerJson, "customerFirstName", "customerLastName", "customerName", "customerFullName", "buyerName", "fullName", "name");
        var invoiceName = NameFrom(invoiceAddressJson, "firstName", "lastName", "invoiceFirstName", "invoiceLastName", "fullName", "name", "company", "companyName");
        var shipmentName = NameFrom(shipmentAddressJson, "firstName", "lastName", "shippingFirstName", "shippingLastName", "fullName", "name", "company", "companyName");
        return FirstMeaningful(customerName, invoiceName, shipmentName) ?? "—";
    }

    internal static string? ResolveCustomerTaxOrIdentityNumber(string customerJson, string invoiceAddressJson, string shipmentAddressJson) =>
        JsonText(customerJson, "customerTaxNumber", "taxNumber", "identityNumber", "customerIdentityNumber", "tcIdentityNumber")
        ?? JsonText(invoiceAddressJson, "taxNumber", "identityNumber", "tcIdentityNumber")
        ?? JsonText(shipmentAddressJson, "taxNumber", "identityNumber", "tcIdentityNumber");

    internal static string? NameFrom(string json, string firstName, string lastName, params string[] fullNameFields)
    {
        var first = JsonText(json, firstName);
        var last = JsonText(json, lastName);
        var combined = string.Join(' ', new[] { first, last }.Where(x => !string.IsNullOrWhiteSpace(x)));
        return !string.IsNullOrWhiteSpace(combined) ? combined : JsonText(json, fullNameFields);
    }

    internal static string? FirstMeaningful(params string?[] names) => names.FirstOrDefault(name => IsMeaningfulText(name) && !IsPlaceholderCustomerName(name!));

    internal static bool IsPlaceholderCustomerName(string name) => name.Trim() is "Adı Soyadı" or "Ad Soyad" or "İsim Soyisim";

    // A shipping deadline only: Trendyol's (extended) agreed hand-over date or a marketplace's due date;
    // delivery estimates and modification times are not deadlines.
    internal static DateTimeOffset? OperationalDueAt(string json) =>
        JsonInstant(json, "extendedAgreedDeliveryDate", "agreedDeliveryDate", "dueDate", "shipmentDueDate", "deliveryDueAt");

    // n11 facts the order card shows besides the shared fields: cargo tracking (number and carrier
    // link), delivery type, commission rate and the discounts split by who paid them.
    internal static IReadOnlyList<OrderInfoItem> N11OrderInfo(IEnumerable<string?> lineSnapshots, string customerSnapshotJson)
    {
        var info = new List<OrderInfoItem>();
        var trackingNumber = JsonText(customerSnapshotJson, "cargoSenderNumber");
        var trackingLink = JsonText(customerSnapshotJson, "cargoTrackingLink");
        var safeLink = Uri.TryCreate(trackingLink, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps ? uri.ToString() : null;
        if (trackingNumber is not null) info.Add(new("Kargo takip no", trackingNumber, safeLink));
        if (JsonText(customerSnapshotJson, "deliveryAddressType") is { } deliveryType) info.Add(new("Teslimat tipi", deliveryType));

        decimal mallDiscount = 0, sellerDiscount = 0;
        var rates = new SortedSet<decimal>();
        foreach (var snapshot in lineSnapshots)
        {
            if (string.IsNullOrWhiteSpace(snapshot)) continue;
            try
            {
                using var document = JsonDocument.Parse(snapshot);
                var line = document.RootElement;
                if (line.ValueKind != JsonValueKind.Object) continue;
                mallDiscount += JsonAmount(line, "totalMallDiscountPrice");
                sellerDiscount += JsonAmount(line, "totalSellerDiscountPrice");
                if (line.TryGetProperty("commissionRate", out var rate) && rate.ValueKind == JsonValueKind.Number && rate.TryGetDecimal(out var commission))
                    rates.Add(commission - JsonAmount(line, "sellerCampaignCommissionRate"));
            }
            catch (JsonException) { }
        }
        var tr = System.Globalization.CultureInfo.GetCultureInfo("tr-TR");
        if (rates.Count > 0) info.Add(new("Komisyon oranı", string.Join(" / ", rates.Select(rate => "%" + rate.ToString("0.##", tr)))));
        if (mallDiscount > 0) info.Add(new("N11 indirimi", mallDiscount.ToString("N2", tr) + " ₺"));
        if (sellerDiscount > 0) info.Add(new("Satıcı indirimi", sellerDiscount.ToString("N2", tr) + " ₺"));
        return info;
    }

    internal static decimal JsonAmount(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var amount) ? amount : 0;

    internal static SourceLineView SourceLine(string? json)
    {
        var snapshot = json ?? "{}";
        var image = NormalizeImageUrl(SourceImageUrl(snapshot));
        var color = JsonText(snapshot, "productColor", "color", "colorName");
        var size = JsonText(snapshot, "productSize", "size", "sizeName");
        var options = new List<string>();
        if (!string.IsNullOrWhiteSpace(color)) options.Add($"Renk: {color}");
        if (!string.IsNullOrWhiteSpace(size)) options.Add($"Beden: {size}");
        return new(image, JsonText(snapshot, "modelCode"), options.Count == 0 ? null : string.Join(" | ", options));
    }

    // The product's own image fields, the most specific first. The first field that holds a usable address wins,
    // wherever it sits in the snapshot: a match in document order picked a claim photo listed before the product's.
    internal static readonly string[] ProductImageFields = ["productImageUrl", "productImageUrlFormat", "emaproductImageUrlFormat", "imageUrl", "productImage", "image"];

    // Parts of a line snapshot whose images are not the product: a Hepsiburada claim merged into the line keeps the
    // customer's evidence photos under Reports[].ImageUrl (an images.hepsiburada.net/oms/claim address the panel cannot load).
    internal static readonly HashSet<string> NonProductImageContainers = new(StringComparer.OrdinalIgnoreCase) { "Reports" };

    internal static string? SourceImageUrl(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            return FindImageUrl(document.RootElement)
                ?? ProductImageFields.Select(field => FindImageField(document.RootElement, field)).FirstOrDefault(value => value is not null);
        }
        catch (JsonException) { return null; }
    }

    // The line's image and, when it is the marketplace's, the catalog's photo to show if that address no longer loads.
    internal static (string? ImageUrl, string? FallbackImageUrl) LineImageUrls(string? sourceImageUrl, string? catalogImageUrl) =>
        sourceImageUrl is null
            ? (catalogImageUrl, null)
            : (sourceImageUrl, catalogImageUrl is null || string.Equals(catalogImageUrl, sourceImageUrl, StringComparison.Ordinal) ? null : catalogImageUrl);

    // A return detail line's photo: the marketplace's with the catalog's as fallback (LineImageUrls); with neither, the
    // barcode lookup (/orders/product-image) is the photo.
    // The lookup carries the claim's store (connectionId) when known, as the order screens' own lookup does.
    internal static (string? ImageUrl, string? FallbackImageUrl) ReturnDetailLineImageUrls(string? sourceImageUrl, string? catalogImageUrl, string? barcode, Guid? connectionId = null)
    {
        var images = LineImageUrls(sourceImageUrl, catalogImageUrl);
        return images.ImageUrl is null && !string.IsNullOrWhiteSpace(barcode)
            ? ($"/api/v1/orders/product-image?barcode={Uri.EscapeDataString(barcode)}{(connectionId is { } id && id != Guid.Empty ? $"&connectionId={id:D}" : "")}", null)
            : images;
    }

    internal static string? FindImageField(JsonElement element, string field)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (NonProductImageContainers.Contains(property.Name)) continue;
                if (property.Name.Equals(field, StringComparison.OrdinalIgnoreCase) && property.Value.ValueKind == JsonValueKind.String)
                {
                    var value = property.Value.GetString();
                    if (NormalizeImageUrl(value) is not null) return value;
                }
                var nested = FindImageField(property.Value, field); if (nested is not null) return nested;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array) foreach (var item in element.EnumerateArray()) { var nested = FindImageField(item, field); if (nested is not null) return nested; }
        return null;
    }

    internal static string? NormalizeImageUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var candidate = value.Trim();
        // Hepsiburada returns productImageUrlFormat with a {size} token.
        // Keep the stored marketplace snapshot untouched and resolve the token
        // for the order UI when projecting the image URL.
        candidate = candidate.Replace("{size}", "500", StringComparison.OrdinalIgnoreCase);
        if (candidate.StartsWith("//", StringComparison.Ordinal)) candidate = "https:" + candidate;
        else if (candidate.StartsWith("/", StringComparison.Ordinal)) candidate = "https://cdn.dsmcdn.com" + candidate;
        return Uri.TryCreate(candidate, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps ? uri.ToString() : null;
    }

    internal static string? FindImageUrl(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (NonProductImageContainers.Contains(property.Name)) continue;
                if (property.Name.Equals("images", StringComparison.OrdinalIgnoreCase) && property.Value.ValueKind == JsonValueKind.Array)
                    foreach (var image in property.Value.EnumerateArray())
                    {
                        var url = image.ValueKind == JsonValueKind.Object && image.TryGetProperty("url", out var value) ? value.ToString() : image.ToString();
                        if (NormalizeImageUrl(url) is not null) return url;
                    }
                var nested = FindImageUrl(property.Value); if (!string.IsNullOrWhiteSpace(nested)) return nested;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array) foreach (var item in element.EnumerateArray()) { var nested = FindImageUrl(item); if (!string.IsNullOrWhiteSpace(nested)) return nested; }
        return null;
    }

    internal sealed record SourceLineView(string? ImageUrl, string? ModelCode, string? OptionSignature);

    internal static string? MaskIdentityNumber(string? value)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return null;
        return trimmed.Length > 4 ? $"***{trimmed[^4..]}" : "***";
    }

    // Only the delivery fields a label prints: no phone, e-mail, identity or tax number.
    internal static OrderAddressView? ListAddress(string shipmentAddressJson)
    {
        var name = NameFrom(shipmentAddressJson, "firstName", "lastName", "shippingFirstName", "shippingLastName", "fullName", "name", "company", "companyName");
        var address = new OrderAddressView(
            IsMeaningfulText(name) ? name : null,
            JsonText(shipmentAddressJson, "fullAddress"),
            JsonText(shipmentAddressJson, "address1"),
            JsonText(shipmentAddressJson, "address2"),
            JsonText(shipmentAddressJson, "neighborhood"),
            JsonText(shipmentAddressJson, "district", "countyName"),
            JsonText(shipmentAddressJson, "city", "province"),
            JsonText(shipmentAddressJson, "postalCode", "zip"),
            JsonText(shipmentAddressJson, "countryCode", "country"),
            JsonBool(shipmentAddressJson, "isDeliveryPoint"),
            JsonText(shipmentAddressJson, "deliveryPointType"));
        return address == new OrderAddressView(null, null, null, null, null, null, null, null, null) ? null : address;
    }

    internal static string? JsonText(string json, params string[] names)
    {
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            return FindText(document.RootElement, new HashSet<string>(names, StringComparer.OrdinalIgnoreCase));
        }
        catch (JsonException) { return null; }
    }

    internal static string? FindText(JsonElement element, HashSet<string> names)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (names.Contains(property.Name) && property.Value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined or JsonValueKind.Object or JsonValueKind.Array))
                {
                    var value = property.Value.ToString();
                    if (IsMeaningfulText(value)) return value;
                }
                var nested = FindText(property.Value, names); if (!string.IsNullOrWhiteSpace(nested)) return nested;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array) foreach (var item in element.EnumerateArray()) { var nested = FindText(item, names); if (!string.IsNullOrWhiteSpace(nested)) return nested; }
        return null;
    }

    internal static bool IsMeaningfulText(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Any(char.IsLetterOrDigit);

    internal static bool JsonBool(string json, params string[] names)
    {
        var value = JsonText(json, names);
        return bool.TryParse(value, out var parsed) && parsed || value is "1" or "YES" or "EVET";
    }

    internal static bool? JsonNullableBool(string json, params string[] names)
    {
        var value = JsonText(json, names);
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (bool.TryParse(value, out var parsed)) return parsed;
        return value is "1" or "YES" or "EVET" ? true : value is "0" or "NO" or "HAYIR" ? false : null;
    }

    internal static DateTimeOffset? JsonInstant(string json, params string[] names)
    {
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            var namesSet = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
            return FindInstant(document.RootElement, namesSet);
        }
        catch (JsonException) { return null; }
    }

    internal static DateTimeOffset? FindInstant(JsonElement element, HashSet<string> names)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (names.Contains(property.Name))
                {
                    if (property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt64(out var num) && num > 0)
                    {
                        try { return DateTimeOffset.FromUnixTimeMilliseconds(num); } catch (ArgumentOutOfRangeException) { }
                    }
                    else if (property.Value.ValueKind == JsonValueKind.String)
                    {
                        var str = property.Value.GetString();
                        if (long.TryParse(str, out var numStr) && numStr > 0)
                        {
                            try { return DateTimeOffset.FromUnixTimeMilliseconds(numStr); } catch (ArgumentOutOfRangeException) { }
                        }
                        if (DateTimeOffset.TryParse(str, out var parsed)) return parsed;
                    }
                }
                var nested = FindInstant(property.Value, names);
                if (nested is not null) return nested;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var nested = FindInstant(item, names);
                if (nested is not null) return nested;
            }
        }
        return null;
    }
}
