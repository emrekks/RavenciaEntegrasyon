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
            recipientInfo = source.Recipient,
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
            orderInfo = new { orderId = source.OrderNumber, orderDate = source.OrderDate.ToString("yyyy-MM-dd") },
            issuedAt = ProviderDateTime(source.IssuedAt),
            deliveryInfo = source.Delivery is null ? null : new { source.Delivery.CarrierTaxId, source.Delivery.CarrierName, source.Delivery.CarrierSurname, sentAt = source.Delivery.SentAt.ToString("yyyy-MM-dd") },
            paymentInfo = source.Payment is null ? null : new { source.Payment.PurchaseUrl, source.Payment.PaymentAgentName, source.Payment.PaymentType, paymentDate = ProviderDateTime(source.Payment.PaymentDate), source.Payment.PaymentMeans }
        };
        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    public static string CreateEArchiveV2(EfaturamFiscalAccount account, EfaturamInvoicePayloadSource source)
    {
        if (account.CompanyId <= 0 || account.UserId <= 0) throw new ArgumentOutOfRangeException(nameof(account));
        if (!string.Equals(source.InvoiceType, "EARSIVFATURA", StringComparison.Ordinal)) throw new ArgumentException("V2 E-Archive payload requires EARSIVFATURA.", nameof(source));
        if (source.Lines.Count == 0) throw new ArgumentException("Invoice requires at least one line.", nameof(source));
        if (source.Lines.Any(line => line.Quantity <= 0)) throw new ArgumentException("Invoice line quantity must be greater than zero.", nameof(source));

        var taxExclusive = Money(source.Lines.Sum(x => x.TaxableAmount));
        var taxTotal = Money(source.Lines.Sum(x => x.VatAmount));
        var discountTotal = Money(source.Lines.Sum(x => x.DiscountAmount));
        var payable = Money(source.Lines.Sum(x => x.PayableAmount));
        if (Money(taxExclusive + taxTotal) != payable) throw new ArgumentException("Invoice total equation is invalid.", nameof(source));

        // The provider's current portal and V2 API exchange monetary values in the
        // invoice currency's major unit (TRY), unlike the retired V1 contract which
        // expected integer kuruş values.
        var lineRows = source.Lines.Select(line => new
        {
            itemName = line.ItemName,
            unitCode = line.UnitCode,
            unitPriceAmount = Money((line.TaxableAmount + line.DiscountAmount) / line.Quantity),
            quantity = line.Quantity,
            subtotal = Money(line.TaxableAmount + line.DiscountAmount),
            totalAmount = Money(line.TaxableAmount),
            totalDiscountAmount = Money(line.DiscountAmount),
            totalTax = new
            {
                totalTaxAmount = Money(line.VatAmount),
                subTotalTaxes = new[]
                {
                    new
                    {
                        taxableAmount = Money(line.TaxableAmount),
                        taxAmount = Money(line.VatAmount),
                        taxType = "KDV",
                        percent = line.VatRate
                    }
                }
            }
        }).ToArray();

        var taxes = source.Lines.GroupBy(x => x.VatRate).Select(group => new
        {
            taxableAmount = Money(group.Sum(x => x.TaxableAmount)),
            taxAmount = Money(group.Sum(x => x.VatAmount)),
            taxType = "KDV",
            percent = group.Key
        }).ToArray();

        var payload = new
        {
            source = account.Source,
            receiverInfo = source.Recipient,
            invoiceIdentification = new
            {
                invoiceType = source.InvoiceType,
                invoiceTypeCode = "SATIS",
                autoInvoiceId = true,
                issuedAt = ProviderDateTime(source.IssuedAt)
            },
            invoiceLines = lineRows,
            totalTax = new { totalTaxAmount = taxTotal, subTotalTaxes = taxes },
            invoiceTotal = new
            {
                totalRawPrice = Money(taxExclusive + discountTotal),
                totalSubtotal = taxExclusive,
                totalAmount = Money(taxExclusive + taxTotal),
                totalDiscountAmount = discountTotal,
                payableAmount = payable
            },
            currencyInfo = new
            {
                currency = source.Currency,
                hasExchange = false,
                sourceCurrency = source.Currency,
                targetCurrency = "TRY",
                calculationRate = 0,
                exchangeDate = ProviderDateTime(source.IssuedAt)
            },
            bankInfo = Array.Empty<object>(),
            notes = new[] { source.Note },
            references = new
            {
                orderInfo = new
                {
                    orderId = source.OrderNumber,
                    orderDate = source.OrderDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                }
            },
            paymentInfo = source.Payment is null ? null : new
            {
                source.Payment.PurchaseUrl,
                source.Payment.PaymentAgentName,
                paymentDate = ProviderDateTime(source.Payment.PaymentDate),
                source.Payment.PaymentMeans,
                instructionNote = "Trendyol siparişi"
            },
            deliveryInfo = source.Delivery is null ? null : new
            {
                source.Delivery.CarrierTaxId,
                source.Delivery.CarrierName,
                sentAt = source.Delivery.SentAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            },
            targetAlias = ""
        };

        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    private static decimal Money(decimal value) => decimal.Round(value, 2, MidpointRounding.AwayFromZero);
    private static long Kurus(decimal value) => decimal.ToInt64(Money(value) * 100m);
    private static decimal KurusDecimal(decimal value) => decimal.Round(value * 100m, 4, MidpointRounding.AwayFromZero);
    private static string ProviderDateTime(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
}
