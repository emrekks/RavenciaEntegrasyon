namespace MarketplaceHub.Domain;

using System.Text.Json;

public sealed record VatIncludedInvoiceAmount(decimal TaxExclusiveAmount, decimal VatAmount, decimal PayableAmount);

public static class InvoiceAmounts
{
    public static string TrendyolInvoiceType(string customerSnapshotJson, string invoiceAddressSnapshotJson, string eInvoiceType = "TEMELFATURA")
    {
        try
        {
            using var customer = JsonDocument.Parse(customerSnapshotJson);
            using var address = JsonDocument.Parse(invoiceAddressSnapshotJson);
            var commercial = customer.RootElement.TryGetProperty("commercial", out var commercialValue) && commercialValue.ValueKind == JsonValueKind.True;
            var available = address.RootElement.TryGetProperty("invoiceAddress", out var invoiceAddress) && invoiceAddress.TryGetProperty("eInvoiceAvailable", out var availableValue) && availableValue.ValueKind == JsonValueKind.True;
            var configured = eInvoiceType.Trim().ToUpperInvariant();
            if (configured is not ("TEMELFATURA" or "TICARIFATURA")) configured = "TEMELFATURA";
            return commercial && available ? configured : "EARSIVFATURA";
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
