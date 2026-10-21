namespace MarketplaceHub.Domain;

using System.Text.Json;

public sealed record VatIncludedInvoiceAmount(decimal TaxExclusiveAmount, decimal VatAmount, decimal PayableAmount);
public sealed record InvoicePackageLineSource(Guid OrderLineId, string Description, string? Sku, decimal Quantity, decimal UnitPrice, decimal VatRate);
public sealed record InvoicePackageLineCalculation(Guid OrderLineId, string Description, string? Sku, decimal Quantity, decimal UnitPrice, decimal DiscountAmount, decimal VatRate, decimal VatAmount, decimal PayableAmount);
public sealed record InvoicePackageCalculation(IReadOnlyList<InvoicePackageLineCalculation> Lines, decimal TaxExclusiveTotal, decimal DiscountTotal, decimal TaxTotal, decimal PayableTotal);
public sealed record InvoicePackageTotalSource(decimal NetAmount, decimal GrossAmount, decimal DiscountAmount);
public sealed record InvoiceOrderTotals(decimal GrossAmount, decimal DiscountAmount, decimal PayableAmount);

public static class InvoiceAmounts
{
    public static InvoiceOrderTotals ResolveOrderTotals(
        decimal orderPayableAmount,
        decimal orderGrossAmount,
        decimal orderDiscountAmount,
        IReadOnlyList<InvoicePackageTotalSource> activePackages)
    {
        var hasPackagePayableTotals = activePackages.Count > 0 && activePackages.All(package => package.NetAmount > 0);
        var hasPackageGrossTotals = activePackages.Count > 0 && activePackages.All(package => package.GrossAmount > 0);
        return new(
            hasPackageGrossTotals ? activePackages.Sum(package => package.GrossAmount) : orderGrossAmount,
            hasPackageGrossTotals ? activePackages.Sum(package => package.DiscountAmount) : orderDiscountAmount,
            hasPackagePayableTotals ? activePackages.Sum(package => package.NetAmount) : orderPayableAmount);
    }

    public static bool TryCalculatePackage(
        IReadOnlyList<InvoicePackageLineSource> sourceLines,
        decimal packageGrossAmount,
        decimal packageDiscountAmount,
        decimal packagePayableAmount,
        out InvoicePackageCalculation? calculation,
        out string? error)
    {
        calculation = null;
        error = null;
        if (sourceLines.Count == 0 || packagePayableAmount <= 0)
        {
            error = "Paket tutarı veya faturalanabilir ürün kalemi bulunamadı.";
            return false;
        }
        if (sourceLines.Any(line => line.Quantity <= 0 || line.UnitPrice < 0 || line.VatRate < 0))
        {
            error = "Ürün miktarı, fiyatı veya KDV oranı geçersiz.";
            return false;
        }

        var grossLines = sourceLines.Select(line => Money(line.Quantity * line.UnitPrice)).ToArray();
        var lineGrossTotal = grossLines.Sum();
        var grossTotal = packageGrossAmount > 0 ? Money(packageGrossAmount) : lineGrossTotal;
        var payableTotal = Money(packagePayableAmount);
        var discountGross = packageDiscountAmount > 0
            ? Money(packageDiscountAmount)
            : Money(Math.Max(0, grossTotal - payableTotal));
        if (Math.Abs(lineGrossTotal - grossTotal) > 0.01m || discountGross < 0 || discountGross > grossTotal
            || Math.Abs(grossTotal - discountGross - payableTotal) > 0.01m)
        {
            error = $"Paket kalemleri ({lineGrossTotal:0.00}), brüt tutar ({grossTotal:0.00}), indirim ({discountGross:0.00}) ve ödenecek tutar ({payableTotal:0.00}) birbiriyle uyuşmuyor.";
            return false;
        }

        var lines = new List<InvoicePackageLineCalculation>(sourceLines.Count);
        var allocatedGrossDiscount = 0m;
        for (var index = 0; index < sourceLines.Count; index++)
        {
            var source = sourceLines[index];
            var lineGross = grossLines[index];
            var lineGrossDiscount = index == sourceLines.Count - 1
                ? Money(discountGross - allocatedGrossDiscount)
                : grossTotal == 0 ? 0 : Money(discountGross * lineGross / grossTotal);
            allocatedGrossDiscount += lineGrossDiscount;
            if (lineGrossDiscount < 0 || lineGrossDiscount > lineGross)
            {
                error = "Paket indirimi ürün kalemlerine güvenli biçimde dağıtılamadı.";
                return false;
            }

            var grossAmounts = FromVatIncluded(lineGross, source.VatRate);
            var payable = Money(lineGross - lineGrossDiscount);
            var netAmounts = FromVatIncluded(payable, source.VatRate);
            var taxExclusiveDiscount = Money(grossAmounts.TaxExclusiveAmount - netAmounts.TaxExclusiveAmount);
            lines.Add(new(source.OrderLineId, source.Description, source.Sku, source.Quantity,
                decimal.Round(netAmounts.TaxExclusiveAmount / source.Quantity, 4, MidpointRounding.AwayFromZero),
                taxExclusiveDiscount, source.VatRate, netAmounts.VatAmount, netAmounts.PayableAmount));
        }

        var roundedPayable = lines.Sum(line => line.PayableAmount);
        var roundingDifference = Money(payableTotal - roundedPayable);
        if (Math.Abs(roundingDifference) > 0.01m)
        {
            error = "Yuvarlama sonrası fatura toplamı paket toplamıyla uyuşmuyor.";
            return false;
        }
        if (roundingDifference != 0)
        {
            // D10: the kuruş goes to the VAT of the last line that carries VAT; a 0% line never gets VAT. When no
            // line carries VAT, the last line's net (and payable) takes it together, so its VAT stays 0.
            var vatIndex = lines.FindLastIndex(line => line.VatRate > 0 && line.VatAmount + roundingDifference >= 0 && line.PayableAmount + roundingDifference >= 0);
            if (vatIndex >= 0)
            {
                var target = lines[vatIndex];
                lines[vatIndex] = target with { PayableAmount = target.PayableAmount + roundingDifference, VatAmount = target.VatAmount + roundingDifference };
            }
            else
            {
                var netIndex = lines.FindLastIndex(line => line.PayableAmount + roundingDifference >= 0);
                if (netIndex < 0)
                {
                    error = "Yuvarlama farkı fatura kalemlerine güvenli biçimde dağıtılamadı.";
                    return false;
                }
                var target = lines[netIndex];
                var payable = target.PayableAmount + roundingDifference;
                lines[netIndex] = target with
                {
                    PayableAmount = payable,
                    UnitPrice = decimal.Round((payable - target.VatAmount) / target.Quantity, 4, MidpointRounding.AwayFromZero)
                };
            }
        }

        calculation = new(
            lines,
            lines.Sum(line => Money(line.PayableAmount - line.VatAmount)),
            lines.Sum(line => line.DiscountAmount),
            lines.Sum(line => line.VatAmount),
            payableTotal);
        return true;
    }

    /// <summary>
    /// The e-document scenario for an order of any marketplace. Trendyol states both the corporate buyer
    /// (commercial) and its e-Invoice registration (eInvoiceAvailable). Another platform selects e-Invoice
    /// only when its snapshot carries an explicit e-Invoice flag (Pazarama isEInvoiceObliged, kept as isEInvoice);
    /// its corporate buyers without that flag are stopped by <see cref="RequiresEInvoiceRecipientReview"/>.
    /// </summary>
    public static string InvoiceDocumentType(string? platformCode, string customerSnapshotJson, string invoiceAddressSnapshotJson)
    {
        if (IsTrendyol(platformCode)) return TrendyolInvoiceType(customerSnapshotJson, invoiceAddressSnapshotJson);
        return EInvoiceRegistration(customerSnapshotJson, invoiceAddressSnapshotJson) == true ? "TEMELFATURA" : "EARSIVFATURA";
    }

    /// <summary>
    /// True when a non-Trendyol order's buyer is a company (a valid 10-digit VKN, a company name next to a
    /// tax/identity number, or a corporate invoiceType) and the platform did not state that it is not an
    /// e-Invoice taxpayer. An automatic e-Archive could then be the wrong document, so the order needs review.
    /// A buyer flagged as an e-Invoice taxpayer is caught here too, for drafts saved as e-Archive earlier.
    /// </summary>
    public static bool RequiresEInvoiceRecipientReview(string? platformCode, string? customerSnapshotJson, string? invoiceAddressSnapshotJson) =>
        !IsTrendyol(platformCode)
        && EInvoiceRegistration(customerSnapshotJson, invoiceAddressSnapshotJson) != false
        && IsCorporateBuyer(customerSnapshotJson, invoiceAddressSnapshotJson);

    private static readonly string[] EInvoiceFlagFields = ["eInvoiceAvailable", "isEInvoice", "isEInvoiceObliged"];
    private static readonly string[] TaxNumberFields = ["taxNumber", "taxId", "vkn", "customerTaxNumber", "invoiceTaxNumber", "taxIdentityNumber", "taxIdentifier"];
    private static readonly string[] IdentityNumberFields = ["identityNumber", "identityNo", "tcIdentityNumber", "turkishIdentityNumber", "customerIdentityNumber", "nationalIdentityNumber", "tcId", "tckn"];
    private static readonly string[] CompanyNameFields = ["companyName", "companyTitle", "company", "tradeName", "legalName", "businessName"];

    private static bool IsTrendyol(string? platformCode) => string.Equals(platformCode?.Trim(), "TRENDYOL", StringComparison.OrdinalIgnoreCase);

    // An explicit e-Invoice flag on either snapshot root: true wins, false only when no true is present, null when absent.
    private static bool? EInvoiceRegistration(string? customerSnapshotJson, string? invoiceAddressSnapshotJson)
    {
        bool? registration = null;
        foreach (var json in new[] { customerSnapshotJson, invoiceAddressSnapshotJson })
        {
            if (string.IsNullOrWhiteSpace(json)) continue;
            try
            {
                using var document = JsonDocument.Parse(json);
                if (document.RootElement.ValueKind != JsonValueKind.Object) continue;
                foreach (var property in document.RootElement.EnumerateObject())
                {
                    if (!EInvoiceFlagFields.Contains(property.Name, StringComparer.OrdinalIgnoreCase)) continue;
                    if (property.Value.ValueKind == JsonValueKind.True) return true;
                    if (property.Value.ValueKind == JsonValueKind.False) registration = false;
                }
            }
            catch (JsonException) { }
        }
        return registration;
    }

    private static bool IsCorporateBuyer(string? customerSnapshotJson, string? invoiceAddressSnapshotJson)
    {
        foreach (var json in new[] { invoiceAddressSnapshotJson, customerSnapshotJson })
        {
            if (string.IsNullOrWhiteSpace(json)) continue;
            try
            {
                using var document = JsonDocument.Parse(json);
                if (HasCorporateBuyer(document.RootElement, 0)) return true;
            }
            catch (JsonException) { }
        }
        return false;
    }

    // Each object is judged on its own fields, so a carrier's company name elsewhere in the snapshot does not
    // pair with the buyer's personal identity number.
    private static bool HasCorporateBuyer(JsonElement node, int depth)
    {
        if (depth > 3) return false;
        if (node.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in node.EnumerateArray())
                if (HasCorporateBuyer(item, depth + 1)) return true;
            return false;
        }
        if (node.ValueKind != JsonValueKind.Object) return false;
        var taxNumber = FieldText(node, TaxNumberFields);
        if (TurkishTaxIdentity.IsValidVkn(taxNumber)) return true;
        if (FieldText(node, CompanyNameFields) is not null
            && (IsTaxIdShape(taxNumber) || IsTaxIdShape(FieldText(node, IdentityNumberFields)))) return true;
        if (IsCorporateInvoiceType(node)) return true;
        foreach (var property in node.EnumerateObject())
            if (HasCorporateBuyer(property.Value, depth + 1)) return true;
        return false;
    }

    // n11 and Pazarama billing addresses carry invoiceType 1 (individual) or 2 (corporate).
    private static bool IsCorporateInvoiceType(JsonElement node)
    {
        var value = FieldText(node, ["invoiceType"])?.ToLowerInvariant();
        return value is not null
            && (value == "2" || value.Contains("kurumsal", StringComparison.Ordinal) || value.Contains("corporate", StringComparison.Ordinal));
    }

    private static bool IsTaxIdShape(string? value) =>
        value is { Length: 10 or 11 } && value.All(char.IsAsciiDigit) && value != "11111111111";

    private static string? FieldText(JsonElement node, string[] names)
    {
        foreach (var property in node.EnumerateObject())
        {
            if (!names.Contains(property.Name, StringComparer.OrdinalIgnoreCase)) continue;
            var text = property.Value.ValueKind switch
            {
                JsonValueKind.String => property.Value.GetString(),
                JsonValueKind.Number => property.Value.GetRawText(),
                _ => null
            };
            if (!string.IsNullOrWhiteSpace(text)) return text.Trim();
        }
        return null;
    }

    public static string TrendyolInvoiceType(string customerSnapshotJson, string invoiceAddressSnapshotJson)
    {
        try
        {
            using var customer = JsonDocument.Parse(customerSnapshotJson);
            using var address = JsonDocument.Parse(invoiceAddressSnapshotJson);
            var commercial = customer.RootElement.ValueKind == JsonValueKind.Object
                && customer.RootElement.TryGetProperty("commercial", out var commercialValue) && commercialValue.ValueKind == JsonValueKind.True;
            return commercial && EInvoiceAvailable(address.RootElement) ? "TEMELFATURA" : "EARSIVFATURA";
        }
        catch (JsonException) { return "EARSIVFATURA"; }
    }

    // The Trendyol order mapper stores the invoiceAddress object itself as the snapshot root; older or
    // local snapshots may still wrap it in an invoiceAddress property. The root value wins when present.
    private static bool EInvoiceAvailable(JsonElement address)
    {
        if (address.ValueKind != JsonValueKind.Object) return false;
        if (address.TryGetProperty("eInvoiceAvailable", out var flat)) return flat.ValueKind == JsonValueKind.True;
        return address.TryGetProperty("invoiceAddress", out var nested)
            && nested.ValueKind == JsonValueKind.Object
            && nested.TryGetProperty("eInvoiceAvailable", out var value)
            && value.ValueKind == JsonValueKind.True;
    }

    public static VatIncludedInvoiceAmount FromVatIncluded(decimal payableAmount, decimal vatRate)
    {
        if (payableAmount < 0) throw new ArgumentOutOfRangeException(nameof(payableAmount));
        if (vatRate < 0) throw new ArgumentOutOfRangeException(nameof(vatRate));

        var payable = Money(payableAmount);
        var taxExclusive = vatRate == 0 ? payable : Money(payable / (1 + vatRate / 100m));
        return new(taxExclusive, payable - taxExclusive, payable);
    }

    public static string TurkishInvoiceNote(decimal amount)
    {
        if (amount < 0) throw new ArgumentOutOfRangeException(nameof(amount));
        var rounded = Money(amount);
        var lira = decimal.ToInt64(decimal.Truncate(rounded));
        var kurus = decimal.ToInt32((rounded - lira) * 100m);
        var text = $"YALNIZ: {IntegerToTurkish(lira)} TÜRK LİRASI";
        if (kurus > 0) text += $" {IntegerToTurkish(kurus)} KURUŞ";
        return text;
    }

    private static decimal Money(decimal value) => decimal.Round(value, 2, MidpointRounding.AwayFromZero);

    private static string IntegerToTurkish(long value)
    {
        if (value == 0) return "SIFIR";
        string[] ones = ["", "BİR", "İKİ", "ÜÇ", "DÖRT", "BEŞ", "ALTI", "YEDİ", "SEKİZ", "DOKUZ"];
        string[] tens = ["", "ON", "YİRMİ", "OTUZ", "KIRK", "ELLİ", "ALTMIŞ", "YETMİŞ", "SEKSEN", "DOKSAN"];
        string[] groups = ["", "BİN", "MİLYON", "MİLYAR", "TRİLYON"];
        var parts = new List<string>();
        var groupIndex = 0;
        while (value > 0)
        {
            var group = (int)(value % 1000);
            if (group > 0)
            {
                var groupParts = new List<string>();
                var hundreds = group / 100;
                var remainder = group % 100;
                if (hundreds > 0)
                {
                    if (hundreds > 1) groupParts.Add(ones[hundreds]);
                    groupParts.Add("YÜZ");
                }
                if (remainder / 10 > 0) groupParts.Add(tens[remainder / 10]);
                if (remainder % 10 > 0) groupParts.Add(ones[remainder % 10]);
                if (groupIndex > 0)
                {
                    if (!(groupIndex == 1 && group == 1)) parts.Insert(0, string.Join(' ', groupParts));
                    parts.Insert(groupIndex == 1 && group == 1 ? 0 : 1, groups[groupIndex]);
                }
                else parts.Insert(0, string.Join(' ', groupParts));
            }
            value /= 1000;
            groupIndex++;
        }
        return string.Join(' ', parts.Where(x => x.Length > 0));
    }
}
