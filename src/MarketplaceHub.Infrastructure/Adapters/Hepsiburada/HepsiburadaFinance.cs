using System.Text.Json;
using MarketplaceHub.Application;
using static MarketplaceHub.Infrastructure.Adapters.RemoteFields;

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
    private static string Local(DateTimeOffset value) => TurkeyTime.Format(value, "yyyy-MM-dd");
}

public static class HepsiburadaFinanceMapper
{
    private const string Lira = "949";

    // A record without an id, a readable amount or any of its days (invoice, order, due) is not stored: it is listed in
    // Skipped and the page's other records are kept (one such record used to stop the whole 60-day read).
    public static RemoteFinanceRecordPage Page(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) throw new JsonException("Finance page is not an object.");
        var items = new List<RemoteFinanceRecord>();
        var skipped = new List<RemoteSkippedFinanceRecord>();
        if (root.TryGetProperty("items", out var list) && list.ValueKind == JsonValueKind.Array)
            foreach (var item in list.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) throw new JsonException("Finance record is not an object.");
                var id = Text(item, "id");
                var amount = Money(item, "amount");
                var dated = Time(item, "invoiceDate") ?? Time(item, "orderDate") ?? Time(item, "dueDate");
                if (id is null || amount.Value is null || dated is null)
                {
                    skipped.Add(SkippedFinanceRecords.Skip(id, item.GetRawText(), Text(item, "orderNumber"), Text(item, "transactionType"), id is null, amount.Value is null, dated is null));
                    continue;
                }
                var currency = amount.Currency is null ? null : amount.Currency == Lira ? "TRY" : amount.Currency == "840" ? "USD" : amount.Currency;
                items.Add(new(id, Text(item, "transactionType"), Text(item, "transactionTypeCategory"), Text(item, "status"), Text(item, "orderNumber"), Text(item, "packageNumber"),
                    Text(item, "invoiceNumber"), Text(item, "sku"), amount.Value, Money(item, "netAmount").Value, currency, Bool(item, "isIncome"),
                    Time(item, "orderDate"), Time(item, "invoiceDate"), Time(item, "dueDate"), Time(item, "paymentDate"), item.GetRawText()));
            }
        // A null count is no count (TryGetInt32 would throw on it): the page's own size stands in, as when it is missing.
        var count = RemoteJson.Int32(root, "count") ?? items.Count + skipped.Count;
        return new(items, count, skipped);
    }

    // The finance amount is a {value, currencyCode} object whose value is read as a JSON number only
    // (RemoteFields.JsonNumber); a text value has never been read from this service.
    private static (decimal? Value, string? Currency) Money(JsonElement item, string name) =>
        item.TryGetProperty(name, out var money) && money.ValueKind == JsonValueKind.Object
            ? (JsonNumber(money, "value"), Text(money, "currencyCode"))
            : (null, null);

    // Hepsiburada writes Istanbul local times: a time without an offset is Istanbul's, one with Z or an offset keeps it
    // (TurkeyTime, the policy of every Hepsiburada text timestamp; it replaced the old "a '-' after index 9" guess).
    internal static DateTimeOffset? Time(JsonElement element, string name) => TurkeyTime.Parse(Text(element, name));
}
