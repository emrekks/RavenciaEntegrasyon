using System.Globalization;
using System.Text.Json;
using System.Xml.Linq;
using MarketplaceHub.Application;

namespace MarketplaceHub.Infrastructure.Adapters.N11;

// Hakediş: SOAP SettlementService (https://api.n11.com/ws/SettlementService.wsdl), read only. GetSettlementList gives a
// day's totals (settlementDate, remittanceDate, status, paymentAmount, deductionAmount, settlementAmount);
// GetSettlementDetail a day's order item lines. Days are Turkey's calendar days.
public sealed partial class N11HttpClient : IN11SettlementPort
{
    private const string SettlementService = "ws/settlementService/";
    internal const int SettlementPageSize = 100;

    public async Task<AdapterResult<RemoteSettlementDayPage>> GetSettlementDaysAsync(AdapterContext context, DateOnly start, DateOnly end, int page, CancellationToken cancellationToken)
    {
        var account = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken);
        if (account is null) return CredentialMissing<RemoteSettlementDayPage>();
        var listed = await SendSoapAsync(account, SettlementService, "GetSettlementList",
            SoapElement("startDate", SettlementDate(start)) + SoapElement("endDate", SettlementDate(end)) + Paging(page), cancellationToken);
        if (!listed.IsSuccess) return AdapterResult<RemoteSettlementDayPage>.Failure(listed.Error!, listed.RateLimit);
        return AdapterResult<RemoteSettlementDayPage>.Success(N11SettlementMapper.Days(listed.Value!, page));
    }

    public async Task<AdapterResult<RemoteSettlementItemPage>> GetSettlementItemsAsync(AdapterContext context, DateOnly day, int page, CancellationToken cancellationToken)
    {
        var account = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken);
        if (account is null) return CredentialMissing<RemoteSettlementItemPage>();
        var detail = await SendSoapAsync(account, SettlementService, "GetSettlementDetail", SoapElement("date", SettlementDate(day)) + Paging(page), cancellationToken);
        if (!detail.IsSuccess) return AdapterResult<RemoteSettlementItemPage>.Failure(detail.Error!, detail.RateLimit);
        return AdapterResult<RemoteSettlementItemPage>.Success(N11SettlementMapper.Items(detail.Value!, page));
    }

    // yyyy-MM-dd: n11 refused dd/MM/yyyy here ("startDate alanındaki tarih geçersizdir", 4 Nov 2026), unlike its other
    // SOAP services; the published PHP client (metineren/n11soap-api) sends yyyy-MM-dd too.
    internal static string SettlementDate(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string Paging(int page) =>
        "<pagingData>" + SoapElement("currentPage", Math.Max(0, page).ToString(CultureInfo.InvariantCulture))
        + SoapElement("pageSize", SettlementPageSize.ToString(CultureInfo.InvariantCulture)) + "</pagingData>";
}

public static class N11SettlementMapper
{
    // The buyer's name and e-mail come on every line; they are not the payout's business and are never stored.
    private static readonly HashSet<string> Personal = new(StringComparer.OrdinalIgnoreCase) { "buyerName", "buyerEmail" };

    public static RemoteSettlementDayPage Days(XElement response, int page)
    {
        var items = response.Descendants().Where(x => x.Name.LocalName == "settlementList")
            .Select(element => new RemoteSettlementDay(
                Day(N11HttpClient.Child(element, "settlementDate")), Day(N11HttpClient.Child(element, "remittanceDate")), N11HttpClient.Child(element, "status"),
                Number(element, "paymentAmount"), Number(element, "deductionAmount"), Number(element, "settlementAmount")))
            .ToList();
        return new(items, page, PageCount(response.Descendants().FirstOrDefault(x => x.Name.LocalName == "settlementListData")));
    }

    // A line without an order item id cannot be stored once and only once: the page is then a contract error.
    public static RemoteSettlementItemPage Items(XElement response, int page)
    {
        var detail = response.Descendants().FirstOrDefault(x => x.Name.LocalName == "settlementDetail");
        var summary = detail?.Descendants().FirstOrDefault(x => x.Name.LocalName == "settlement");
        var list = detail?.Descendants().FirstOrDefault(x => x.Name.LocalName == "orderItemSettlementDataList");
        var items = new List<RemoteSettlementItem>();
        foreach (var element in list?.Descendants().Where(x => x.Name.LocalName == "orderItemSettlement") ?? [])
        {
            var id = N11HttpClient.Child(element, "orderItemId") ?? throw new JsonException("N11 settlement line has no order item id.");
            items.Add(new(id, N11HttpClient.Child(element, "orderNumber"), Number(element, "commission"), Number(element, "serviceFee") + Number(element, "serviceFeeVat"),
                Number(element, "adFee") + Number(element, "adFeeVat"), Number(element, "cargoPrice"), Number(element, "sellerDiscount") + Number(element, "sellerCouponDiscount"),
                Number(element, "deductionAmount"), Number(element, "paymentAmount"), Number(element, "settlementAmount"),
                Date(N11HttpClient.Child(element, "completedDate")), Day(N11HttpClient.Child(element, "settlementDate")), Day(N11HttpClient.Child(element, "remittanceDate")),
                Day(N11HttpClient.Child(element, "paymentDate")), RawJson(element)));
        }
        return new(items, page, PageCount(list), N11HttpClient.Child(summary, "status"));
    }

    // The line's own fields as JSON, without the buyer's.
    internal static string RawJson(XElement element) =>
        JsonSerializer.Serialize(element.Elements().Where(x => !Personal.Contains(x.Name.LocalName) && !x.HasElements)
            .GroupBy(x => x.Name.LocalName).ToDictionary(group => group.Key, group => group.First().Value.Trim()));

    private static int PageCount(XElement? container)
    {
        var metadata = container?.Elements().FirstOrDefault(x => x.Name.LocalName is "metadata" or "pagingData");
        return int.TryParse(N11HttpClient.Child(metadata, "pageCount"), NumberStyles.None, CultureInfo.InvariantCulture, out var count) ? count : 0;
    }

    // A field n11 leaves out is 0 (no such fee). A value that is there but cannot be read (O5: "1234,50", which the
    // invariant culture took as 123450) is a contract error for the page, never a made-up amount.
    internal static decimal Number(XElement element, string name)
    {
        var text = N11HttpClient.Child(element, name);
        if (string.IsNullOrWhiteSpace(text)) return 0m;
        return RemoteJson.ParseDecimal(text) ?? throw new JsonException($"N11 settlement field {name} is not a number.");
    }

    // A calendar day as n11 writes it (dd/MM/yyyy, or yyyy-MM-dd); null when there is none or it cannot be read.
    internal static DateOnly? Day(string? value) => N11ReturnMapper.Date(value) is { } instant
        ? DateOnly.FromDateTime(instant.ToOffset(TimeSpan.FromHours(3)).DateTime)
        : null;

    private static DateTimeOffset? Date(string? value) => N11ReturnMapper.Date(value);
}
