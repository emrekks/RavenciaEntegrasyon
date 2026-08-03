using System.Text.Json;
using System.Text.Json.Serialization;

namespace MarketplaceHub.Infrastructure.Adapters.TrendyolEFaturam.Contracts;

public sealed record EfaturamFiscalAccount(long CompanyId, long UserId, string? Prefix, string Source = "PARTNER");
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

        var lineRows = source.Lines.Select(line => new
        {
            unitCode = line.UnitCode,
            quantity = line.Quantity,
            totalAmount = Kurus(line.TaxableAmount + line.DiscountAmount),
            taxAmount = Kurus(line.VatAmount),
            taxableAmount = Kurus(line.TaxableAmount),
            taxPercent = decimal.ToInt32(line.VatRate),
            taxName = "KDV",
            taxCode = "0015",
            itemName = line.ItemName,
            unitPriceAmount = KurusDecimal(line.UnitPrice),
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
        if (Money(taxExclusive + taxTotal - discountTotal) != Money(payable)) throw new ArgumentException("Invoice total equation is invalid.", nameof(source));

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
                lineExtensionAmount = Kurus(taxExclusive),
                taxExclusiveAmount = Kurus(taxExclusive),
                taxInclusiveAmount = Kurus(taxExclusive + taxTotal),
                payableAmount = Kurus(payable),
                allowanceTotalAmount = Kurus(discountTotal)
            },
            orderInfo = new { orderId = source.OrderNumber, orderDate = source.OrderDate.ToString("yyyy-MM-dd") },
            issuedAt = source.IssuedAt,
            deliveryInfo = source.Delivery is null ? null : new { source.Delivery.CarrierTaxId, source.Delivery.CarrierName, source.Delivery.CarrierSurname, sentAt = source.Delivery.SentAt.ToString("yyyy-MM-dd") },
            paymentInfo = source.Payment is null ? null : new { source.Payment.PurchaseUrl, source.Payment.PaymentAgentName, source.Payment.PaymentType, source.Payment.PaymentDate, source.Payment.PaymentMeans }
        };
        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    private static decimal Money(decimal value) => decimal.Round(value, 2, MidpointRounding.AwayFromZero);
    private static long Kurus(decimal value) => decimal.ToInt64(Money(value) * 100m);
    private static decimal KurusDecimal(decimal value) => decimal.Round(value * 100m, 4, MidpointRounding.AwayFromZero);
}
