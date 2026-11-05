using System.Globalization;
using System.Text.Json;
using MarketplaceHub.Application;

namespace MarketplaceHub.Infrastructure.Adapters.Hepsiburada;

// Muhasebe: Kayıt Bazlı Muhasebe Servisi (GET mpfinance-external.hepsiburada.com/transactions/merchantid/{merchantId}),
// read only. Status Paid / WillBePaid; one date range is required (due or payment day here); at most 100 a page.
// Times are Istanbul's, as every Hepsiburada service writes and reads them.
public sealed partial class HepsiburadaHttpClient : IHepsiburadaFinancePort
{
    internal const int FinancePageSize = 100;

    public async Task<AdapterResult<RemoteFinanceRecordPage>> GetFinanceRecordsAsync(AdapterContext context, string status, bool byDueDate, DateTimeOffset start, DateTimeOffset end, int offset, CancellationToken cancellationToken)
    {
        var account = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken);
        if (account?.FinanceBaseAddress is null) return AdapterResult<RemoteFinanceRecordPage>.Failure(new(AdapterErrorClass.Authentication, "HEPSIBURADA_FINANCE_CREDENTIALS", "Hepsiburada muhasebe bağlantısı veya servis anahtarı eksik.", 401, null, null));
        var response = await SendAsync(account, account.FinanceBaseAddress, HttpMethod.Get, HepsiburadaFinanceRequestContract.Path(account.Connection.ExternalStoreId, status, byDueDate, start, end, offset), null, cancellationToken);
        if (!response.IsSuccess) return AdapterResult<RemoteFinanceRecordPage>.Failure(response.Error!, response.RateLimit);
        using var document = response.Value!;
        try { return AdapterResult<RemoteFinanceRecordPage>.Success(HepsiburadaFinanceMapper.Page(document.RootElement), response.RateLimit); }
        catch (JsonException) { return AdapterResult<RemoteFinanceRecordPage>.Failure(new(AdapterErrorClass.ContractViolation, "HEPSIBURADA_FINANCE_CONTRACT", "Hepsiburada muhasebe yanıtının biçimi tanınmadı.", null, null, null), response.RateLimit); }
    }
}

internal static class HepsiburadaFinanceRequestContract
{
    public static string Path(string merchantId, string status, bool byDueDate, DateTimeOffset start, DateTimeOffset end, int offset)
    {
        var range = byDueDate ? "DueDate" : "PaymentDate";
        return $"transactions/merchantid/{Uri.EscapeDataString(merchantId)}?Offset={Math.Max(0, offset)}&Limit={HepsiburadaHttpClient.FinancePageSize}&Status={Uri.EscapeDataString(status)}"
            + $"&{range}Start={Uri.EscapeDataString(Local(start))}&{range}End={Uri.EscapeDataString(Local(end))}";
    }

    // Istanbul calendar days, as the guide's example (dueDateStart=2021-08-01&dueDateEnd=2021-08-31); a date-time was refused (400).
    private static string Local(DateTimeOffset value) => value.ToOffset(TimeSpan.FromHours(3)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}

public static class HepsiburadaFinanceMapper
{
    private const string Lira = "949";

    // A record without an id cannot be stored once and only once: the page is then a contract error.
    public static RemoteFinanceRecordPage Page(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) throw new JsonException("Finance page is not an object.");
        var items = new List<RemoteFinanceRecord>();
        if (root.TryGetProperty("items", out var list) && list.ValueKind == JsonValueKind.Array)
            foreach (var item in list.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) throw new JsonException("Finance record is not an object.");
                var id = Text(item, "id") ?? throw new JsonException("Finance record has no id.");
                var amount = Money(item, "amount");
                var currency = amount.Currency is null ? null : amount.Currency == Lira ? "TRY" : amount.Currency == "840" ? "USD" : amount.Currency;
                items.Add(new(id, Text(item, "transactionType"), Text(item, "transactionTypeCategory"), Text(item, "status"), Text(item, "orderNumber"), Text(item, "packageNumber"),
                    Text(item, "invoiceNumber"), Text(item, "sku"), amount.Value, Money(item, "netAmount").Value, currency, Bool(item, "isIncome"),
                    Time(item, "orderDate"), Time(item, "invoiceDate"), Time(item, "dueDate"), Time(item, "paymentDate"), item.GetRawText()));
            }
        // A null count is no count (TryGetInt32 would throw on it): the page's own size stands in, as when it is missing.
        var count = RemoteJson.Int32(root, "count") ?? items.Count;
        return new(items, count);
    }

    private static (decimal? Value, string? Currency) Money(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var money) || money.ValueKind != JsonValueKind.Object) return (null, null);
        decimal? value = money.TryGetProperty("value", out var number) && number.ValueKind == JsonValueKind.Number && number.TryGetDecimal(out var parsed) ? parsed : null;
        return (value, Text(money, "currencyCode"));
    }

    private static string? Text(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return null;
        var text = value.ValueKind switch { JsonValueKind.String => value.GetString(), JsonValueKind.Number => value.GetRawText(), _ => null };
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    private static bool? Bool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;

    // A time without an offset is Istanbul's.
    internal static DateTimeOffset? Time(JsonElement element, string name)
    {
        var text = Text(element, name);
        if (text is null) return null;
        if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var withOffset)
            && (text.EndsWith('Z') || text.Contains('+') || text.LastIndexOf('-') > 9))
            return withOffset.ToUniversalTime();
        return DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var local)
            ? new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), TimeSpan.FromHours(3)).ToUniversalTime()
            : null;
    }
}
