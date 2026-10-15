using System.Globalization;
using System.Net;
using System.Text.Json;
using MarketplaceHub.Application;

namespace MarketplaceHub.Infrastructure.Adapters.Pazarama;

// Accounting and finance (phase 10): POST order/paymentAgreement lists the money movements of a date
// range - sales, refunds, commission, allowance (hakediş), transfer date, cargo debt, withholding - and the
// commission invoices. It is read-only and asked for one order at a time (orderId carries the order number).
public sealed partial class PazaramaHttpClient : IOrderFinancePort
{
    internal const string PaymentAgreement = "order/paymentAgreement";

    public async Task<AdapterResult<RemoteOrderFinance>> GetOrderFinanceAsync(AdapterContext context, string orderNumber, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
    {
        if (!long.TryParse(orderNumber?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number <= 0)
            return Failure<RemoteOrderFinance>(AdapterErrorClass.Validation, "PAZARAMA_ORDER_NUMBER_INVALID", "Pazarama sipariş numarası geçersiz.", HttpStatusCode.UnprocessableEntity);
        if (to <= from) return Failure<RemoteOrderFinance>(AdapterErrorClass.Validation, "PAZARAMA_FINANCE_WINDOW_INVALID", "Finans sorgusunun bitişi başlangıcından sonra olmalıdır.", HttpStatusCode.UnprocessableEntity);
        var account = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken);
        if (account is null) return CredentialMissing<RemoteOrderFinance>();
        var result = await SendAsync(account, HttpMethod.Post, PaymentAgreement, cancellationToken, Json(PazaramaFinanceMapper.Body(number, from, to)));
        if (!result.IsSuccess) return AdapterResult<RemoteOrderFinance>.Failure(result.Error!, result.RateLimit);
        using var document = result.Value!;
        try { return AdapterResult<RemoteOrderFinance>.Success(PazaramaFinanceMapper.Finance(document.RootElement, number), result.RateLimit); }
        catch (JsonException)
        {
            return Failure<RemoteOrderFinance>(AdapterErrorClass.ContractViolation, "PAZARAMA_FINANCE_CONTRACT_INVALID", "Pazarama finans yanıtı beklenen yapıda değil.", HttpStatusCode.BadGateway);
        }
    }
}

public static class PazaramaFinanceMapper
{
    // Dates go in Turkey time with milliseconds, as in the documented request.
    public static object Body(long orderNumber, DateTimeOffset from, DateTimeOffset to) => new
    {
        startDate = Iso(from),
        endDate = Iso(to),
        allowanceStartDate = (string?)null,
        allowanceEndDate = (string?)null,
        orderId = orderNumber
    };

    private static string Iso(DateTimeOffset value) => value.ToOffset(PazaramaHttpClient.TurkeyOffset).ToString("yyyy-MM-ddTHH:mm:ss.fff", CultureInfo.InvariantCulture);

    // Only the lines of the asked order are kept: the service answers by date range.
    public static RemoteOrderFinance Finance(JsonElement root, long orderNumber)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("data", out var data) || data.ValueKind is not (JsonValueKind.Object or JsonValueKind.Null))
            throw new JsonException("Pazarama finance response has no data object.");
        var transactions = new List<RemoteFinanceTransaction>();
        var invoices = new List<RemoteCommissionInvoice>();
        if (data.ValueKind == JsonValueKind.Object)
        {
            if (data.TryGetProperty("transactionList", out var list))
            {
                if (list.ValueKind != JsonValueKind.Array) throw new JsonException("Pazarama transactionList is not a list.");
                foreach (var row in list.EnumerateArray())
                    if (row.ValueKind == JsonValueKind.Object && Number(row, "orderId") is { } id && (long)id == orderNumber)
                        transactions.Add(new(Text(row, "trxCode") ?? Text(row, "trxId") ?? "", Text(row, "status") ?? "", Number(row, "amount") ?? 0, Number(row, "commissionAmount") ?? 0,
                            Number(row, "allowanceAmount") ?? 0, Number(row, "couponDiscount") ?? 0, Number(row, "integrationAmount") ?? 0, Number(row, "merchantCargoDebt") ?? 0,
                            Number(row, "stoppageAmount") ?? 0, (int)(Number(row, "installmentNumber") ?? 0), Instant(Text(row, "transactionDate")), Instant(Text(row, "transferredDate"))));
            }
            if (data.TryGetProperty("commissionInvoiceDetails", out var details))
            {
                if (details.ValueKind != JsonValueKind.Array) throw new JsonException("Pazarama commissionInvoiceDetails is not a list.");
                foreach (var row in details.EnumerateArray())
                    if (row.ValueKind == JsonValueKind.Object && Number(row, "orderNumber") is { } id && (long)id == orderNumber)
                        invoices.Add(new(orderNumber.ToString(CultureInfo.InvariantCulture), Text(row, "commissionInvoiceNumber"), Instant(Text(row, "commissionInvoiceDate")), Number(row, "totalAmountKDV") ?? 0, Text(row, "operationStatus")));
            }
        }
        return new(transactions.OrderBy(x => x.TransactionDate).ToList(), invoices);
    }

    // "2026-04-05T00:00:00": Turkey time without an offset, stored as UTC.
    public static DateTimeOffset? Instant(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return DateTime.TryParse(value.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var local)
            ? new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), TimeSpan.FromHours(3)).ToUniversalTime()
            : null;
    }

    private static string? Text(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var value)) return null;
        var text = value.ValueKind switch { JsonValueKind.String => value.GetString(), JsonValueKind.Number => value.GetRawText(), _ => null };
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    private static decimal? Number(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number) ? number : null;
}
