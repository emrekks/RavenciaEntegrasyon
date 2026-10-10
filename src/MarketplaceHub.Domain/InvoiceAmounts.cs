namespace MarketplaceHub.Domain;

using System.Text.Json;

public sealed record VatIncludedInvoiceAmount(decimal TaxExclusiveAmount, decimal VatAmount, decimal PayableAmount);
public sealed record InvoicePackageLineSource(Guid OrderLineId, string Description, string? Sku, decimal Quantity, decimal UnitPrice, decimal VatRate);
public sealed record InvoicePackageLineCalculation(Guid OrderLineId, string Description, string? Sku, decimal Quantity, decimal UnitPrice, decimal DiscountAmount, decimal VatRate, decimal VatAmount, decimal PayableAmount);
public sealed record InvoicePackageCalculation(IReadOnlyList<InvoicePackageLineCalculation> Lines, decimal TaxExclusiveTotal, decimal DiscountTotal, decimal TaxTotal, decimal PayableTotal);

public static class InvoiceAmounts
{
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
            var last = lines[^1];
            lines[^1] = last with { PayableAmount = last.PayableAmount + roundingDifference, VatAmount = last.VatAmount + roundingDifference };
        }

        calculation = new(
            lines,
            lines.Sum(line => Money(line.PayableAmount - line.VatAmount)),
            lines.Sum(line => line.DiscountAmount),
            lines.Sum(line => line.VatAmount),
            payableTotal);
        return true;
    }

    public static string TrendyolInvoiceType(string customerSnapshotJson, string invoiceAddressSnapshotJson)
    {
        try
        {
            using var customer = JsonDocument.Parse(customerSnapshotJson);
            using var address = JsonDocument.Parse(invoiceAddressSnapshotJson);
            var commercial = customer.RootElement.TryGetProperty("commercial", out var commercialValue) && commercialValue.ValueKind == JsonValueKind.True;
            var available = address.RootElement.TryGetProperty("invoiceAddress", out var invoiceAddress) && invoiceAddress.TryGetProperty("eInvoiceAvailable", out var availableValue) && availableValue.ValueKind == JsonValueKind.True;
            return commercial && available ? "TEMELFATURA" : "EARSIVFATURA";
        }
        catch (JsonException) { return "EARSIVFATURA"; }
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
