using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Xml.Linq;
using MarketplaceHub.Application;

namespace MarketplaceHub.Infrastructure.Adapters.N11;

// Returns: SOAP ReturnService (https://api.n11.com/ws/ReturnService.wsdl). Each n11 claim covers
// one product of an order and names the product, not the order line; the processor resolves
// the order line from the stored order line snapshots (productId).
public sealed partial class N11HttpClient : IReturnPort
{
    private const string ReturnService = "ws/returnService/";

    public async Task<AdapterResult<AdapterPageResult<RemoteReturnClaim>>> PollAsync(AdapterContext context, ReturnPollWindow window, AdapterPageRequest page, CancellationToken cancellationToken)
    {
        if (window.ModifiedAfter is not { } start || window.ModifiedBefore is not { } end || end <= start)
            return Failure<AdapterPageResult<RemoteReturnClaim>>(AdapterErrorClass.Validation, "N11_RETURN_WINDOW_INVALID", "N11 iadeleri başlangıcı ve bitişi belli bir tarih aralığıyla sorgulanır.", HttpStatusCode.BadRequest);
        var pageNumber = int.TryParse(page.Cursor, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
        var account = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken);
        if (account is null) return CredentialMissing<AdapterPageResult<RemoteReturnClaim>>();
        var listed = await SendSoapAsync(account, ReturnService, "ClaimReturnList", ClaimReturnListBody(start, end, pageNumber), cancellationToken);
        if (!listed.IsSuccess) return AdapterResult<AdapterPageResult<RemoteReturnClaim>>.Failure(listed.Error!, listed.RateLimit);
        return AdapterResult<AdapterPageResult<RemoteReturnClaim>>.Success(N11ReturnMapper.Page(listed.Value!, pageNumber));
    }

    // ClaimReturnList searches by request date; "ALL" covers every claim state. The WSDL types the
    // period as plain strings; n11's SOAP services use dd/MM/yyyy dates (Turkey time).
    internal static string ClaimReturnListBody(DateTimeOffset start, DateTimeOffset end, int page) =>
        "<searchData>" + SoapElement("status", "ALL")
        + "<period>" + SoapElement("startDate", TurkeyDate(start)) + SoapElement("endDate", TurkeyDate(end)) + "</period></searchData>"
        + "<pagingData>" + SoapElement("currentPage", page.ToString(CultureInfo.InvariantCulture)) + "</pagingData>";

    internal static string TurkeyDate(DateTimeOffset value) =>
        TimeZoneInfo.ConvertTime(value, TurkeyTimeZone).ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);

    // Explicit: IOrderPort.GetAsync has the same parameters on this class.
    Task<AdapterResult<RemoteReturnClaim>> IReturnPort.GetAsync(AdapterContext context, string externalReturnId, CancellationToken cancellationToken) =>
        Task.FromResult(Failure<RemoteReturnClaim>(AdapterErrorClass.NotSupported, "N11_RETURN_DETAIL_UNSUPPORTED", "N11 tek iade okuma servisi sunmuyor; iadeler liste taramasıyla güncellenir.", HttpStatusCode.NotImplemented));

    public async Task<AdapterResult<IReadOnlyList<ReturnIssueReason>>> IssueReasonsAsync(AdapterContext context, CancellationToken cancellationToken)
    {
        var account = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken);
        if (account is null) return CredentialMissing<IReadOnlyList<ReturnIssueReason>>();
        var reasons = await SendSoapAsync(account, ReturnService, "ClaimReturnDenyReasonTypes", "", cancellationToken);
        if (!reasons.IsSuccess) return AdapterResult<IReadOnlyList<ReturnIssueReason>>.Failure(reasons.Error!, reasons.RateLimit);
        IReadOnlyList<ReturnIssueReason> items = reasons.Value!.Descendants().Where(x => x.Name.LocalName == "denyReasonTypeData")
            .Select(x => (Id: Child(x, "id"), Name: Child(x, "value")))
            .Where(x => x.Id is not null && x.Name is not null)
            .Select(x => new ReturnIssueReason(x.Id!, x.Name!, false))
            .ToList();
        return AdapterResult<IReadOnlyList<ReturnIssueReason>>.Success(items);
    }

    // Approve is sent as ClaimReturnApprove. Deny needs the return shipment type, cargo company and
    // tracking data, whose accepted values n11 does not document, so it stays on n11's own panel.
    public async Task<AdapterResult<ReturnActionResult>> ExecuteAsync(AdapterContext context, ReturnActionCommand command, CancellationToken cancellationToken)
    {
        if (!string.Equals(command.Action, "APPROVE", StringComparison.OrdinalIgnoreCase))
            return Failure<ReturnActionResult>(AdapterErrorClass.NotSupported, "N11_RETURN_ACTION_UNSUPPORTED", "N11 iadesini reddetmek için N11 satıcı panelini kullanın; panelden yalnız onay gönderilir.", HttpStatusCode.NotImplemented);
        if (!long.TryParse(command.ExternalClaimId, NumberStyles.None, CultureInfo.InvariantCulture, out _))
            return Failure<ReturnActionResult>(AdapterErrorClass.Validation, "N11_RETURN_ID_INVALID", "N11 iade numarası geçersiz.", HttpStatusCode.BadRequest);
        var account = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken);
        if (account is null) return CredentialMissing<ReturnActionResult>();
        if (WriteBlocked<ReturnActionResult>(account, "iade onayı") is { } blocked) return blocked;
        var approved = await SendSoapAsync(account, ReturnService, "ClaimReturnApprove", SoapElement("claimReturnId", command.ExternalClaimId) + SoapElement("splitQuantity", ""), cancellationToken);
        return approved.IsSuccess
            ? AdapterResult<ReturnActionResult>.Success(new(command.ExternalClaimId, "APPROVED", null))
            : AdapterResult<ReturnActionResult>.Failure(approved.Error!, approved.RateLimit);
    }
}

public static class N11ReturnMapper
{
    public static AdapterPageResult<RemoteReturnClaim> Page(XElement response, int pageNumber)
    {
        var claims = new List<RemoteReturnClaim>();
        var issues = new List<AdapterPageIssue>();
        foreach (var element in response.Descendants().Where(x => x.Name.LocalName == "claimReturn"))
        {
            var claim = Claim(element);
            if (claim is null) issues.Add(new("RETURN_CLAIM_INVALID", N11HttpClient.Child(element, "claimReturnId") ?? "unknown", "N11 iadesi içeri alınmadı: iade veya sipariş numarası yok."));
            else claims.Add(claim);
        }
        var paging = response.Elements().FirstOrDefault(x => x.Name.LocalName == "pagingData");
        var pageCount = int.TryParse(N11HttpClient.Child(paging, "pageCount"), NumberStyles.None, CultureInfo.InvariantCulture, out var count) ? count : 0;
        var hasMore = claims.Count > 0 && pageNumber + 1 < pageCount;
        return new(claims, hasMore ? (pageNumber + 1).ToString(CultureInfo.InvariantCulture) : null, hasMore, null, issues);
    }

    public static RemoteReturnClaim? Claim(XElement element)
    {
        var claimId = N11HttpClient.Child(element, "claimReturnId");
        var orderNumber = N11HttpClient.Child(element, "orderNumber");
        if (claimId is null || orderNumber is null) return null;
        var rawStatus = N11HttpClient.Child(element, "status") ?? "";
        var quantity = decimal.TryParse(N11HttpClient.Child(element, "quantity"), NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) && parsed > 0 ? parsed : 1;
        var dates = new[] { "requestDate", "pendingRequestDate", "pendedDate", "approvedDate", "deniedDate", "cancelledDate" }
            .Select(name => Date(N11HttpClient.Child(element, name)))
            .Where(x => x is not null)
            .Select(x => x!.Value)
            .ToList();
        var requested = Date(N11HttpClient.Child(element, "requestDate"));
        var raw = JsonSerializer.Serialize(element.Elements().GroupBy(x => x.Name.LocalName).ToDictionary(x => x.Key, x => x.First().Value.Trim()));
        return new RemoteReturnClaim(
            claimId,
            orderNumber,
            CanonicalStatus(rawStatus),
            N11HttpClient.Child(element, "returnReasonType"),
            N11HttpClient.Child(element, "returnReasonDescription"),
            null,
            dates.Count > 0 ? dates.Max() : DateTimeOffset.UnixEpoch,
            [new RemoteReturnLine(claimId, "", quantity, RawStatus: CanonicalStatus(rawStatus))],
            raw,
            N11HttpClient.Child(element, "shipmentCompany"),
            N11HttpClient.Child(element, "trackingNumber"),
            null,
            requested);
    }

    // n11 claim states, named as the shared return status canonicalizer understands them.
    public static string CanonicalStatus(string n11Status) => n11Status.Trim().ToUpperInvariant() switch
    {
        "REQUESTED" => "Created",
        "APPROVAL_WAITING" or "PENDING" or "PENDED" => "WaitingInAction",
        "APPROVED" => "Accepted",
        "DENIED" => "Rejected",
        "CANCELLED" => "Cancelled",
        "MANUAL_REFUND" => "Refunded",
        var other => other
    };

    // xs:date answers usually come as yyyy-MM-dd (sometimes with an offset); dd/MM/yyyy is accepted too.
    internal static DateTimeOffset? Date(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        string[] formats = ["yyyy-MM-dd", "yyyy-MM-ddzzz", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-ddTHH:mm:sszzz", "dd/MM/yyyy", "dd/MM/yyyy HH:mm"];
        return DateTimeOffset.TryParseExact(value.Trim(), formats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed) ? parsed : null;
    }
}
