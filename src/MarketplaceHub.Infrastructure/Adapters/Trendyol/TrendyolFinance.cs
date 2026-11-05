using System.Globalization;
using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Infrastructure.Adapters.Trendyol.ErrorMapping;

namespace MarketplaceHub.Infrastructure.Adapters.Trendyol;

// Trendyol's current account statement (Cari Hesap Ekstresi) and cargo invoice lines, read only:
// https://developers.trendyol.com/v2.0/docs/current-account-statement-integration.md
// https://developers.trendyol.com/v2.0/docs/cargo-invoice-details.md
public sealed partial class TrendyolHttpClient : ITrendyolFinancePort
{
    public Task<AdapterResult<RemoteStatementPage>> GetSettlementsAsync(AdapterContext context, string transactionType, DateTimeOffset start, DateTimeOffset end, int page, CancellationToken cancellationToken) =>
        FinancePageAsync(context, TrendyolFinanceRequestContract.Settlements, transactionType, null, start, end, page, cancellationToken);

    public Task<AdapterResult<RemoteStatementPage>> GetOtherFinancialsAsync(AdapterContext context, string transactionType, string? transactionSubType, DateTimeOffset start, DateTimeOffset end, int page, CancellationToken cancellationToken) =>
        FinancePageAsync(context, TrendyolFinanceRequestContract.OtherFinancials, transactionType, transactionSubType, start, end, page, cancellationToken);

    public async Task<AdapterResult<RemoteStatementPage>> GetPayoutRecordsAsync(AdapterContext context, string source, string transactionType, string? transactionSubType, string paymentOrderId, int page, CancellationToken cancellationToken)
    {
        var authorized = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken);
        if (authorized is null) return AdapterResult<RemoteStatementPage>.Failure(TrendyolErrorMapper.Configuration());
        var path = TrendyolFinanceRequestContract.PayoutPath(source, authorized.Connection.ExternalStoreId, transactionType, transactionSubType, paymentOrderId, page);
        var response = await SendAsync(authorized, HttpMethod.Get, path, null, cancellationToken);
        if (!response.IsSuccess) return AdapterResult<RemoteStatementPage>.Failure(response.Error!, response.RateLimit);
        try { return AdapterResult<RemoteStatementPage>.Success(TrendyolFinanceMapper.Page(response.Value!, source, transactionType, transactionSubType), response.RateLimit); }
        catch (JsonException) { return AdapterResult<RemoteStatementPage>.Failure(TrendyolErrorMapper.Contract(), response.RateLimit); }
    }

    public async Task<AdapterResult<RemotePayoutOrderPage>> GetPayoutOrdersAsync(AdapterContext context, int page, CancellationToken cancellationToken)
    {
        var authorized = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken);
        if (authorized is null) return AdapterResult<RemotePayoutOrderPage>.Failure(TrendyolErrorMapper.Configuration());
        var response = await SendAsync(authorized, HttpMethod.Get, TrendyolFinanceRequestContract.PayoutOrdersPath(authorized.Connection.ExternalStoreId, page), null, cancellationToken);
        if (!response.IsSuccess) return AdapterResult<RemotePayoutOrderPage>.Failure(response.Error!, response.RateLimit);
        try { return AdapterResult<RemotePayoutOrderPage>.Success(TrendyolFinanceMapper.PayoutPage(response.Value!), response.RateLimit); }
        catch (JsonException) { return AdapterResult<RemotePayoutOrderPage>.Failure(TrendyolErrorMapper.Contract(), response.RateLimit); }
    }

    public async Task<AdapterResult<RemoteCargoInvoicePage>> GetCargoInvoiceItemsAsync(AdapterContext context, string invoiceSerialNumber, int page, CancellationToken cancellationToken)
    {
        var authorized = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken);
        if (authorized is null) return AdapterResult<RemoteCargoInvoicePage>.Failure(TrendyolErrorMapper.Configuration());
        var response = await SendAsync(authorized, HttpMethod.Get, TrendyolFinanceRequestContract.CargoInvoiceItemsPath(authorized.Connection.ExternalStoreId, invoiceSerialNumber, page), null, cancellationToken);
        if (!response.IsSuccess) return AdapterResult<RemoteCargoInvoicePage>.Failure(response.Error!, response.RateLimit);
        try { return AdapterResult<RemoteCargoInvoicePage>.Success(TrendyolFinanceMapper.CargoPage(response.Value!), response.RateLimit); }
        catch (JsonException) { return AdapterResult<RemoteCargoInvoicePage>.Failure(TrendyolErrorMapper.Contract(), response.RateLimit); }
    }

    private async Task<AdapterResult<RemoteStatementPage>> FinancePageAsync(AdapterContext context, string source, string transactionType, string? transactionSubType,
        DateTimeOffset start, DateTimeOffset end, int page, CancellationToken cancellationToken)
    {
        if (end <= start || end - start > TrendyolFinanceRequestContract.MaximumWindow)
            return AdapterResult<RemoteStatementPage>.Failure(new(AdapterErrorClass.InternalBug, "TRENDYOL_FINANCE_WINDOW_INVALID", "Trendyol finans sorgusu en çok 15 günlük bir aralık alır.", null, null, null));
        var authorized = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken);
        if (authorized is null) return AdapterResult<RemoteStatementPage>.Failure(TrendyolErrorMapper.Configuration());
        var path = TrendyolFinanceRequestContract.Path(source, authorized.Connection.ExternalStoreId, transactionType, transactionSubType, start, end, page);
        var response = await SendAsync(authorized, HttpMethod.Get, path, null, cancellationToken);
        if (!response.IsSuccess) return AdapterResult<RemoteStatementPage>.Failure(response.Error!, response.RateLimit);
        try { return AdapterResult<RemoteStatementPage>.Success(TrendyolFinanceMapper.Page(response.Value!, source, transactionType, transactionSubType), response.RateLimit); }
        catch (JsonException) { return AdapterResult<RemoteStatementPage>.Failure(TrendyolErrorMapper.Contract(), response.RateLimit); }
    }
}

internal static class TrendyolFinanceRequestContract
{
    public const string Settlements = "SETTLEMENTS";
    public const string OtherFinancials = "OTHER_FINANCIALS";
    public const int PageSize = 500;
    public static readonly TimeSpan MaximumWindow = TimeSpan.FromDays(15);

    // supplierId is listed as a required query parameter next to the seller id in the path.
    public static string Path(string source, string sellerId, string transactionType, string? transactionSubType, DateTimeOffset start, DateTimeOffset end, int page)
    {
        var seller = Uri.EscapeDataString(sellerId);
        var resource = source == Settlements ? "settlements" : "otherfinancials";
        var subType = string.IsNullOrWhiteSpace(transactionSubType) ? "" : $"&transactionSubType={Uri.EscapeDataString(transactionSubType)}";
        return $"finance/che/sellers/{seller}/{resource}?transactionType={Uri.EscapeDataString(transactionType)}{subType}&startDate={start.ToUnixTimeMilliseconds()}&endDate={end.ToUnixTimeMilliseconds()}&page={Math.Max(0, page)}&size={PageSize}&supplierId={seller}";
    }

    // A payout's records: by paymentOrderId, no date window (MoneyMovements are other financials too).
    public static string PayoutPath(string source, string sellerId, string transactionType, string? transactionSubType, string paymentOrderId, int page)
    {
        var seller = Uri.EscapeDataString(sellerId);
        var resource = source == Settlements ? "settlements" : "otherfinancials";
        var subType = string.IsNullOrWhiteSpace(transactionSubType) ? "" : $"&transactionSubType={Uri.EscapeDataString(transactionSubType)}";
        return $"finance/che/sellers/{seller}/{resource}?transactionType={Uri.EscapeDataString(transactionType)}{subType}&paymentOrderId={Uri.EscapeDataString(paymentOrderId)}&page={Math.Max(0, page)}&size={PageSize}&supplierId={seller}";
    }

    // At most ten payouts a page (the documented maximum).
    public static string PayoutOrdersPath(string sellerId, int page) =>
        $"finance/che/sellers/{Uri.EscapeDataString(sellerId)}/payment-order?page={Math.Max(0, page)}&size=10";

    public static string CargoInvoiceItemsPath(string sellerId, string invoiceSerialNumber, int page) =>
        $"finance/che/sellers/{Uri.EscapeDataString(sellerId)}/cargo-invoice/{Uri.EscapeDataString(invoiceSerialNumber)}/items?page={Math.Max(0, page)}&size={PageSize}";
}

internal static class TrendyolFinanceMapper
{
    // A record without an id or a date cannot be stored once and only once; the whole page is then a contract error
    // (the window stays unread) instead of a record silently dropped.
    public static RemoteStatementPage Page(string json, string source, string requestedType, string? transactionSubType)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new JsonException("Finance page is not an object.");
        var items = new List<RemoteStatementRecord>();
        if (root.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
            foreach (var item in content.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) throw new JsonException("Finance record is not an object.");
                var id = Text(item, "id") ?? throw new JsonException("Finance record has no id.");
                var date = Time(item, "transactionDate") ?? throw new JsonException("Finance record has no transaction date.");
                items.Add(new(id, source, requestedType, transactionSubType, Text(item, "transactionType"), date, Text(item, "description"),
                    Text(item, "orderNumber"), Text(item, "shipmentPackageId"), Text(item, "barcode"),
                    Number(item, "debt") ?? 0m, Number(item, "credit") ?? 0m, Number(item, "commissionRate"), Number(item, "commissionAmount"), Number(item, "sellerRevenue"),
                    Text(item, "paymentOrderId"), Time(item, "paymentDate"), item.GetRawText(),
                    Text(item, "country"), Text(item, "affiliate"), Text(item, "currency"), Text(item, "commissionInvoiceSerialNumber")));
            }
        else if (root.TryGetProperty("content", out var other) && other.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
            throw new JsonException("Finance page content is not a list.");
        return new(items, Int(root, "page") ?? 0, Int(root, "totalPages") ?? 0, (long)(Number(root, "totalElements") ?? items.Count));
    }

    // A payout without an id cannot be matched to its records; the page is then a contract error.
    public static RemotePayoutOrderPage PayoutPage(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new JsonException("Payment order page is not an object.");
        var items = new List<RemotePayoutOrder>();
        if (root.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
            foreach (var item in content.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) throw new JsonException("Payment order is not an object.");
                var id = Text(item, "id") ?? throw new JsonException("Payment order has no id.");
                var amount = Number(item, "amount") ?? throw new JsonException("Payment order has no amount.");
                items.Add(new(id, Text(item, "status"), Text(item, "paymentType"), amount, Time(item, "payoutDate"), Text(item, "regionCode"), Text(item, "regionName"), item.GetRawText()));
            }
        return new(items, Int(root, "page") ?? 0, Int(root, "totalPages") ?? 0);
    }

    public static RemoteCargoInvoicePage CargoPage(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new JsonException("Cargo invoice page is not an object.");
        var items = new List<RemoteCargoInvoiceItem>();
        if (root.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
            foreach (var item in content.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) throw new JsonException("Cargo invoice line is not an object.");
                var amount = Number(item, "amount") ?? throw new JsonException("Cargo invoice line has no amount.");
                items.Add(new(Text(item, "shipmentPackageType"), Text(item, "parcelUniqueId"), Text(item, "orderNumber"), amount, Int(item, "desi"), item.GetRawText()));
            }
        return new(items, Int(root, "page") ?? 0, Int(root, "totalPages") ?? 0);
    }

    // Ids and numbers come as JSON numbers or strings ("725041340"); either is read as text.
    internal static string? Text(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return null;
        var text = value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null
        };
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    internal static decimal? Number(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return null;
        return RemoteJson.Decimal(value);
    }

    private static int? Int(JsonElement element, string name) => Number(element, name) is { } number ? (int)Math.Clamp(Math.Truncate(number), int.MinValue, int.MaxValue) : null;

    // Epoch milliseconds.
    private static DateTimeOffset? Time(JsonElement element, string name) =>
        Number(element, name) is { } milliseconds && milliseconds > 0 && milliseconds < 253_402_300_800_000m
            ? DateTimeOffset.FromUnixTimeMilliseconds((long)milliseconds)
            : null;
}
