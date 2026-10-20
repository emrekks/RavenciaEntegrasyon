using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MarketplaceHub.Infrastructure.Adapters.TrendyolEFaturam.Contracts;

public sealed record EfaturamFiscalAccount(long CompanyId, long UserId, string? Prefix, string Source = "WEB");
public sealed record EfaturamRecipient(string TaxId, string CountryCode, string City, string District, string Address, string? PostalCode, string? Phone, string? Email, string? Name, string? Surname, string? TaxOffice);
public sealed record EfaturamDelivery(string CarrierTaxId, string CarrierName, string? CarrierSurname, DateOnly SentAt);
public sealed record EfaturamPayment(string PurchaseUrl, string? PaymentAgentName, string? PaymentType, DateTimeOffset PaymentDate, string PaymentMeans);
public sealed record EfaturamInvoiceLine(string ItemName, string UnitCode, decimal Quantity, decimal UnitPrice, decimal TaxableAmount, decimal VatAmount, decimal VatRate, decimal DiscountAmount, decimal PayableAmount);
public sealed record EfaturamInvoicePayloadSource(string LocalReferenceId, string InvoiceType, string Currency, string Note, string OrderNumber, DateOnly OrderDate, DateTimeOffset IssuedAt, EfaturamRecipient Recipient, IReadOnlyList<EfaturamInvoiceLine> Lines, EfaturamPayment? Payment = null, EfaturamDelivery? Delivery = null);

public static class TrendyolEFaturamInvoicePayload
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string Create(EfaturamFiscalAccount account, EfaturamInvoicePayloadSource source)
    {
        if (account.CompanyId <= 0 || account.UserId <= 0) throw new ArgumentOutOfRangeException(nameof(account));
        if (source.Lines.Count == 0) throw new ArgumentException("Invoice requires at least one line.", nameof(source));
        if (source.Lines.Any(line => line.Quantity <= 0)) throw new ArgumentException("Invoice line quantity must be greater than zero.", nameof(source));

        var lineRows = source.Lines.Select(line => new
        {
            // E-Faturam expects unitPriceAmount and totalAmount to use the same,
            // tax-exclusive basis. Marketplace order snapshots keep UnitPrice as
            // the customer-facing tax-inclusive amount, so derive the official
            // unit price from the line basis instead of forwarding that snapshot.
            unitCode = line.UnitCode,
            quantity = line.Quantity,
            totalAmount = Kurus(line.TaxableAmount + line.DiscountAmount),
            taxAmount = Kurus(line.VatAmount),
            taxableAmount = Kurus(line.TaxableAmount),
            taxPercent = decimal.ToInt32(line.VatRate),
            taxName = "KDV",
            taxCode = "0015",
            itemName = line.ItemName,
            unitPriceAmount = KurusDecimal((line.TaxableAmount + line.DiscountAmount) / line.Quantity),
            totalDiscountAmount = Kurus(line.DiscountAmount),
            totalTax = new
            {
                totalTaxAmount = Kurus(line.VatAmount),
                subTotalTaxes = new[] { new { taxableAmount = Kurus(line.TaxableAmount), taxAmount = Kurus(line.VatAmount), taxType = "KDV", percent = line.VatRate, name = "KDV" } }
            }
        }).ToArray();

        var taxExclusive = source.Lines.Sum(x => x.TaxableAmount);
        var taxTotal = source.Lines.Sum(x => x.VatAmount);
        var discountTotal = source.Lines.Sum(x => x.DiscountAmount);
        var payable = source.Lines.Sum(x => x.PayableAmount);
        if (Money(taxExclusive + taxTotal) != Money(payable)) throw new ArgumentException("Invoice total equation is invalid.", nameof(source));

        var taxes = source.Lines.GroupBy(x => x.VatRate).Select(group => new
        {
            taxableAmount = Kurus(group.Sum(x => x.TaxableAmount)),
            taxAmount = Kurus(group.Sum(x => x.VatAmount)),
            taxType = "KDV",
            percent = group.Key,
            name = "KDV"
        }).ToArray();

        var payload = new
        {
            autoInvoiceId = true,
            companyId = account.CompanyId,
            localReferenceId = source.LocalReferenceId,
            prefix = account.Prefix,
            userId = account.UserId,
            source = account.Source,
            notes = new[] { source.Note },
            recipientInfo = EfaturamRecipientNormalizer.Normalize(source.Recipient),
            currencyInfo = new { currency = source.Currency, hasExchange = false },
            invoiceInfo = new { invoiceType = source.InvoiceType, invoiceTypeCode = "SATIS" },
            invoiceLines = lineRows,
            totalTax = new { totalTaxAmount = Kurus(taxTotal), subTotalTaxes = taxes },
            invoiceTotal = new
            {
                lineExtensionAmount = Kurus(taxExclusive + discountTotal),
                taxExclusiveAmount = Kurus(taxExclusive),
                taxInclusiveAmount = Kurus(taxExclusive + taxTotal),
                payableAmount = Kurus(payable),
                allowanceTotalAmount = Kurus(discountTotal)
            },
            orderInfo = new { orderId = source.OrderNumber, orderDate = source.OrderDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) },
            issuedAt = ProviderDateTime(source.IssuedAt),
            deliveryInfo = source.Delivery is null ? null : new { source.Delivery.CarrierTaxId, source.Delivery.CarrierName, source.Delivery.CarrierSurname, sentAt = source.Delivery.SentAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) },
            paymentInfo = source.Payment is null ? null : new { source.Payment.PurchaseUrl, source.Payment.PaymentAgentName, source.Payment.PaymentType, paymentDate = ProviderDateTime(source.Payment.PaymentDate), source.Payment.PaymentMeans }
        };
        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    private static decimal Money(decimal value) => decimal.Round(value, 2, MidpointRounding.AwayFromZero);
    private static long Kurus(decimal value) => decimal.ToInt64(Money(value) * 100m);
    private static decimal KurusDecimal(decimal value) => decimal.Round(value * 100m, 4, MidpointRounding.AwayFromZero);
    private static string ProviderDateTime(DateTimeOffset value) => EfaturamTurkeyTime.ProviderDateTime(value);
}

/// <summary>
/// Fits recipientInfo to E-Faturam's documented field rules: postal code five digits, phone ten digits (a leading 0 or
/// +90 is dropped), e-mail lower case, address at most 255 and name/surname at most 127 characters, city at least two
/// and district at least one character. Optional fields that do not fit are left out; a missing city or district stops
/// the request (ArgumentException, reported as EFATURAM_FISCAL_PAYLOAD_INVALID) instead of sending an empty value.
/// </summary>
public static class EfaturamRecipientNormalizer
{
    public const int AddressMaxLength = 255;
    public const int NameMaxLength = 127;
    public const string CityRequiredCode = "EFATURAM_RECIPIENT_CITY_REQUIRED";
    public const string DistrictRequiredCode = "EFATURAM_RECIPIENT_DISTRICT_REQUIRED";

    private static readonly System.Text.RegularExpressions.Regex EmailShape = new(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    public static EfaturamRecipient Normalize(EfaturamRecipient recipient)
    {
        var city = Collapse(recipient.City);
        if (city.Length < 2) throw new ArgumentException(CityRequiredCode);
        var district = Collapse(recipient.District);
        if (district.Length < 1) throw new ArgumentException(DistrictRequiredCode);
        return recipient with
        {
            City = city,
            District = district,
            Address = Truncate(Collapse(recipient.Address), AddressMaxLength),
            PostalCode = PostalCode(recipient.PostalCode),
            Phone = Phone(recipient.Phone),
            Email = Email(recipient.Email),
            Name = recipient.Name is null ? null : Truncate(Collapse(recipient.Name), NameMaxLength),
            Surname = recipient.Surname is null ? null : Truncate(Collapse(recipient.Surname), NameMaxLength) is { Length: > 0 } surname ? surname : null
        };
    }

    internal static string? PostalCode(string? value)
    {
        var digits = Digits(value);
        return digits.Length == 5 ? digits : null;
    }

    internal static string? Phone(string? value)
    {
        var digits = Digits(value);
        if (digits.Length == 12 && digits.StartsWith("90", StringComparison.Ordinal)) digits = digits[2..];
        if (digits.Length == 11 && digits.StartsWith('0')) digits = digits[1..];
        return digits.Length == 10 && digits[0] != '0' ? digits : null;
    }

    internal static string? Email(string? value)
    {
        var email = value?.Trim().ToLowerInvariant();
        return !string.IsNullOrEmpty(email) && email.Length <= 254 && EmailShape.IsMatch(email) ? email : null;
    }

    private static string Digits(string? value) => new((value ?? "").Where(char.IsAsciiDigit).ToArray());
    private static string Collapse(string? value) => string.Join(' ', (value ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    private static string Truncate(string value, int length) => value.Length <= length ? value : value[..length].TrimEnd();
}

/// <summary>Every date and time sent to E-Faturam is Istanbul time (UTC+3 all year, no daylight saving since 2016).</summary>
public static class EfaturamTurkeyTime
{
    public static readonly TimeSpan Offset = TimeSpan.FromHours(3);

    /// <summary>The Istanbul calendar day of an instant (order date, shipment date).</summary>
    public static DateOnly Day(DateTimeOffset value) => DateOnly.FromDateTime(value.ToOffset(Offset).DateTime);

    // The gateway only reads values with a trailing Z (an offset such as +03:00 gets "Failed to read request") and prints the clock digits
    // as written without converting them, so the Istanbul wall-clock time is written in that shape to print Istanbul time.
    public static string ProviderDateTime(DateTimeOffset value) =>
        value.ToOffset(Offset).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
}
