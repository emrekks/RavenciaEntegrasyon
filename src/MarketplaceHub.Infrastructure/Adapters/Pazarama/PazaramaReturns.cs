using System.Globalization;
using System.Net;
using System.Text.Json;
using MarketplaceHub.Application;

namespace MarketplaceHub.Infrastructure.Adapters.Pazarama;

// Return reads (phase 7, read side). POST order/getRefund lists refund requests by request date and
// status; each refund names the order (orderNumber) and the product (productCode = barcode), not the
// order line, so the processor resolves the line from the stored order lines. Approving or rejecting
// (order/updateRefund) comes with the write side of this phase.
public sealed partial class PazaramaHttpClient : IReturnPort
{
    internal const string Refunds = "order/getRefund";
    internal const int RefundPageSize = 100;

    public async Task<AdapterResult<AdapterPageResult<RemoteReturnClaim>>> PollAsync(AdapterContext context, ReturnPollWindow window, AdapterPageRequest page, CancellationToken cancellationToken)
    {
        if (window.ModifiedAfter is not { } start || window.ModifiedBefore is not { } end || end <= start || end - start > TimeSpan.FromDays(31))
            return Failure<AdapterPageResult<RemoteReturnClaim>>(AdapterErrorClass.Validation, "PAZARAMA_RETURN_WINDOW_INVALID", "Pazarama iadeleri en fazla bir aylık, başlangıcı ve bitişi belli bir tarih aralığıyla sorgulanabilir.", HttpStatusCode.UnprocessableEntity);
        var pageNumber = int.TryParse(page.Cursor, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed > 0 ? parsed : 1;
        var account = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken);
        if (account is null) return CredentialMissing<AdapterPageResult<RemoteReturnClaim>>();
        var result = await SendAsync(account, HttpMethod.Post, Refunds, cancellationToken, Json(RefundBody(start, end, pageNumber, Math.Clamp(page.Limit, 1, RefundPageSize))));
        if (!result.IsSuccess) return AdapterResult<AdapterPageResult<RemoteReturnClaim>>.Failure(result.Error!, result.RateLimit);
        using var document = result.Value!;
        try { return AdapterResult<AdapterPageResult<RemoteReturnClaim>>.Success(PazaramaReturnMapper.Page(document.RootElement, pageNumber, timeProvider.GetUtcNow()), result.RateLimit); }
        catch (JsonException)
        {
            return Failure<AdapterPageResult<RemoteReturnClaim>>(AdapterErrorClass.ContractViolation, "PAZARAMA_RETURN_CONTRACT_INVALID", "Pazarama iade yanıtı beklenen yapıda değil.", HttpStatusCode.BadGateway);
        }
    }

    // Dates go as plain days in Turkey time; the end day is sent as the day after so the last day is covered.
    internal static object RefundBody(DateTimeOffset start, DateTimeOffset end, int page, int size) => new
    {
        pageSize = size,
        pageNumber = page,
        requestStartDate = Day(start.ToOffset(TurkeyOffset).Date),
        requestEndDate = Day(end.ToOffset(TurkeyOffset).Date.AddDays(1))
    };

    // Explicit: IOrderPort.GetAsync has the same parameters on this class.
    Task<AdapterResult<RemoteReturnClaim>> IReturnPort.GetAsync(AdapterContext context, string externalReturnId, CancellationToken cancellationToken) =>
        Task.FromResult(Failure<RemoteReturnClaim>(AdapterErrorClass.NotSupported, "PAZARAMA_RETURN_DETAIL_UNSUPPORTED", "Pazarama tek iade okuma servisi sunmuyor; iadeler liste taramasıyla güncellenir.", HttpStatusCode.NotImplemented));

    // The documented reasons for rejecting a refund (RefundRejectType).
    public Task<AdapterResult<IReadOnlyList<ReturnIssueReason>>> IssueReasonsAsync(AdapterContext context, CancellationToken cancellationToken) =>
        Task.FromResult(AdapterResult<IReadOnlyList<ReturnIssueReason>>.Success(PazaramaReturnMapper.RejectReasons));

    public Task<AdapterResult<ReturnActionResult>> ExecuteAsync(AdapterContext context, ReturnActionCommand command, CancellationToken cancellationToken) =>
        Task.FromResult(NotSupported<ReturnActionResult>("PAZARAMA_RETURN_ACTION_UNSUPPORTED", "Pazarama iadesini onaylamak veya reddetmek için şimdilik Pazarama satıcı panelini kullanın; panelden bu işlem sonraki aşamada açılacak."));
}

public static class PazaramaReturnMapper
{
    public static readonly IReadOnlyList<ReturnIssueReason> RejectReasons =
    [
        new("1", "Gelen ürün bana ait değil", false), new("2", "Gelen ürün defolu/zarar görmüş", false), new("3", "Gelen ürün adedi eksik", false),
        new("4", "Gelen ürün yanlış", false), new("5", "Gelen ürün kullanılmış", false), new("6", "Gelen ürün sahte", false),
        new("7", "Gelen ürünün parçası/aksesuarı eksik", false), new("8", "Gönderdiğim ürün kusurlu değil", false), new("9", "Gönderdiğim ürün yanlış değil", false),
        new("10", "İade paketi boş geldi", false), new("11", "İade paketi elime ulaşmadı", false), new("12", "Diğer", false)
    ];

    // data.refundList is the page, data.responsePage {pageIndex,totalPages,totalCount} the position.
    public static AdapterPageResult<RemoteReturnClaim> Page(JsonElement root, int pageNumber, DateTimeOffset now)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("data", out var data))
            throw new JsonException("Pazarama refund response has no data.");
        var claims = new List<RemoteReturnClaim>();
        var issues = new List<AdapterPageIssue>();
        // A null data or an object without refundList is an empty answer; anything else is not this contract.
        if (data.ValueKind is not (JsonValueKind.Object or JsonValueKind.Null)) throw new JsonException("Pazarama refund response data is not an object.");
        JsonElement rows = default;
        if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("refundList", out var list))
            rows = list.ValueKind == JsonValueKind.Array ? list : throw new JsonException("Pazarama refundList is not a list.");
        if (rows.ValueKind == JsonValueKind.Array)
            foreach (var row in rows.EnumerateArray())
            {
                var claim = Claim(row, now);
                if (claim is null) issues.Add(new("RETURN_CLAIM_INVALID", Text(row, "refundId") ?? Text(row, "refundNumber") ?? "unknown", "Pazarama iadesi içeri alınmadı: iade veya sipariş numarası yok."));
                else claims.Add(claim);
            }
        var paging = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("responsePage", out var responsePage) && responsePage.ValueKind == JsonValueKind.Object ? responsePage : default;
        var pageIndex = Number(paging, "pageIndex") ?? pageNumber;
        var totalPages = Number(paging, "totalPages") ?? 0;
        var hasMore = claims.Count + issues.Count > 0 && pageIndex < totalPages;
        return new(claims, hasMore ? (pageIndex + 1).ToString(CultureInfo.InvariantCulture) : null, hasMore, Number(paging, "totalCount") is { } total ? (int)Math.Min(total, int.MaxValue) : null, issues);
    }

    public static RemoteReturnClaim? Claim(JsonElement row, DateTimeOffset now)
    {
        var refundId = Text(row, "refundId") ?? Text(row, "refundNumber");
        var orderNumber = Text(row, "orderNumber");
        if (refundId is null || orderNumber is null) return null;
        var status = CanonicalStatus((int?)Number(row, "refundStatus"));
        var requested = Instant(Text(row, "refundDate"));
        var orderedAt = Instant(Text(row, "orderDate"));
        return new RemoteReturnClaim(
            refundId,
            orderNumber,
            status,
            Text(row, "refundType"),
            Text(row, "description") ?? Text(row, "boDescription"),
            null,
            requested ?? now,
            [new RemoteReturnLine(refundId, "", 1, RawStatus: status)],
            row.GetRawText(),
            Text(row, "shipmentCompanyName"),
            Text(row, "shipmentCode"),
            null,
            orderedAt);
    }

    // Refund states (getRefund / updateRefund), named as the shared return status canonicalizer understands them.
    // 1 waits for the seller; 2 and 8 are approvals, 4 an approval by Pazarama staff, 6 the automatic approval of
    // a request the seller left alone; 3 and 5 are rejections; 7 is a request the customer withdrew.
    public static string CanonicalStatus(int? status) => status switch
    {
        1 => "WaitingInAction",
        2 or 4 or 6 or 8 => "Accepted",
        3 or 5 => "Rejected",
        7 => "Cancelled",
        _ => "WaitingInAction"
    };

    // "6 Ekim 2021 19:46" (Turkish month names) or an ISO instant; Pazarama's clock is Turkey time.
    public static DateTimeOffset? Instant(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var text = value.Trim();
        if (System.Text.RegularExpressions.Regex.IsMatch(text, @"(Z|[+-]\d\d:?\d\d)$") && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var withOffset))
            return withOffset.ToUniversalTime();
        string[] formats = ["d MMMM yyyy HH:mm", "d MMMM yyyy H:mm", "yyyy-MM-dd HH:mm", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-ddTHH:mm:ss.FFFFFFF", "dd.MM.yyyy HH:mm"];
        foreach (var culture in new[] { CultureInfo.GetCultureInfo("tr-TR"), CultureInfo.InvariantCulture })
            if (DateTime.TryParseExact(text, formats, culture, DateTimeStyles.None, out var local))
                return new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), TimeSpan.FromHours(3)).ToUniversalTime();
        return null;
    }

    private static string? Text(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var value)) return null;
        var text = value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null
        };
        return string.IsNullOrWhiteSpace(text) || text.Trim() == "null" ? null : text.Trim();
    }

    private static long? Number(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : null;
}
