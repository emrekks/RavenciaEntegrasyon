using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using MarketplaceHub.Application;

namespace MarketplaceHub.Infrastructure.Adapters.Hepsiburada;

public sealed partial class HepsiburadaHttpClient
{
    private static readonly string[] ClaimStatuses = ["NewRequest", "AwaitingAction", "InDispute", "Accepted", "Rejected", "Refunded", "Cancelled", "AwaitingPreApproval"];
    // Hepsiburada has no endpoint that lists rejection reasons; the values below are the documented
    // ClaimRejectionReason list ("Talep Reddetme" > "Reddetme Sebepleri", checked 20 Oct 2026):
    // https://developers.hepsiburada.com/tr/companies/hepsiburada?guide=talep-onemli-bilgiler-2&product=talep-entegrasyonu&view=guide
    // The reject body (POST claims/number/{claimNumber}/reject) accepts optional Reports/UploadedReportsUrls, but the
    // documentation does not require evidence for any reason, so EvidenceRequired stays false for all of them.
    internal static readonly ReturnIssueReason[] ClaimRejectionReasons =
    [
        new("CustomerReturnedWrongItem", "İade edilen ürün siparişteki ürün değil", false),
        new("ProductIsDamaged", "İade edilen ürün kusurlu veya hasarlı", false),
        new("MissingQuantity", "İade edilen ürün adedi eksik", false),
        new("NoSuchAccessory", "İade edilen ürün tekrar satılabilir durumda değil", false),
        new("BoxIsEmptyWithReport", "Paket boş, tutanak mevcut", false),
        new("BoxIsEmptyWithoutReport", "Paket boş, tutanak yok", false),
        new("SomePartsOrSomeAccessoriesOrSomePapersAreMissing", "Ürün parçası, aksesuarı veya faturası eksik", false),
        new("ReturnedProductIsNotDelivered", "İade ürünü teslim edilmedi", false),
        new("NewProductWillBeSent", "Müşteriye yeni ürün gönderilecek", false),
        new("ExtraProductHasBeenReturned", "Fazla gönderilen ürün iade edildi", false),
        new("ProductNotWrong", "Gönderilen ürün yanlış değil", false),
        new("ProductNotDefective", "Gönderilen ürün kusurlu değil", false),
        new("StockProblem", "Stok sorunu nedeniyle değişim yapılamıyor", false),
        new("ReturnedProductHasAccountOrPassword", "Üründe hesap veya parola bulunuyor", false),
        new("MarkedAsServiceProcess", "Ürün servis veya analiz sürecine alınacak", false),
        new("ProductSentComplete", "Ürün eksiksiz gönderildi", false),
        new("MissingItemOrPartCannotBeSupplied", "Eksik ürün veya parça tedarik edilemiyor", false),
        new("ClaimedComponentIsNotPartOfTheProduct", "Talep edilen parça ürün içeriğine dahil değil", false),
        new("InvoiceReplacesWarranty", "Fatura garanti belgesi yerine geçer", false),
        new("PartialShipmentMissingPackageWillBeDelivered", "Eksik paket kısmi sevkiyatla teslim edilecek", false),
        new("CustomerProblemSolved", "Müşteri sorunu çözüldü", false),
        new("Other", "Diğer", false)
    ];
    public async Task<AdapterResult<AdapterPageResult<RemoteReturnClaim>>> PollAsync(AdapterContext context, ReturnPollWindow window, AdapterPageRequest page, CancellationToken cancellationToken)
    {
        var account = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken);
        if (account is null) return Failure<AdapterPageResult<RemoteReturnClaim>>(AdapterErrorClass.Authentication, "HEPSIBURADA_CREDENTIAL_INVALID", "Hepsiburada bağlantı bilgileri bulunamadı.", HttpStatusCode.Unauthorized);
        if (!TryClaimStatus(window.Status, out var status))
            return Failure<AdapterPageResult<RemoteReturnClaim>>(AdapterErrorClass.Validation, "HEPSIBURADA_CLAIM_STATUS_INVALID", "Talep listelemesi için belgelenmiş bir Hepsiburada talep durumu zorunludur.", HttpStatusCode.BadRequest);
        if ((window.ModifiedAfter is null) != (window.ModifiedBefore is null)
            || window.ModifiedAfter is { } start && window.ModifiedBefore is { } end && end < start
            || (window.StatusModifiedAfter is null) != (window.StatusModifiedBefore is null)
            || window.StatusModifiedAfter is { } statusStart && window.StatusModifiedBefore is { } statusEnd && statusEnd < statusStart)
            return Failure<AdapterPageResult<RemoteReturnClaim>>(AdapterErrorClass.Validation, "HEPSIBURADA_CLAIM_WINDOW_INVALID", "Talep tarih filtresi başlangıç ve bitiş tarihlerini birlikte ve artan sırada gerektirir.", HttpStatusCode.BadRequest);

        var offset = int.TryParse(page.Cursor, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedOffset) ? Math.Max(0, parsedOffset) : 0;
        var limit = Math.Clamp(page.Limit, 1, 100);
        var response = await SendAsync(account, account.OmsBaseAddress, HttpMethod.Get, Claims(account, status, ClaimQuery(
            offset,
            limit,
            window.ModifiedAfter,
            window.ModifiedBefore,
            window.StatusModifiedAfter,
            window.StatusModifiedBefore)), cancellationToken);
        using var responseDocument = response.Value;
        if (!response.IsSuccess) return AdapterResult<AdapterPageResult<RemoteReturnClaim>>.Failure(response.Error!, response.RateLimit);
        try
        {
            var pageResult = HepsiburadaJsonMapper.ClaimPage(response.Value!.RootElement);
            var claims = new List<RemoteReturnClaim>(pageResult.Items.Count);
            var issues = new List<AdapterPageIssue>();
            for (var index = 0; index < pageResult.Items.Count; index++)
            {
                var item = pageResult.Items[index];
                try { claims.Add(HepsiburadaJsonMapper.ReturnClaim(item)); }
                catch (JsonException)
                {
                    var identity = HepsiburadaJsonMapper.ReturnClaimIdentity(item) ?? $"offset:{offset + index}";
                    issues.Add(new("HEPSIBURADA_CLAIM_CONTRACT_INVALID", identity, "Talep kaydında güvenli order ve kalem bağlantısı için gerekli alanlar eksik; kayıt atlandı."));
                }
            }

            var nextOffset = offset + pageResult.Items.Count;
            var hasMore = pageResult.TotalCount is { } total ? nextOffset < total : pageResult.Items.Count == limit;
            return AdapterResult<AdapterPageResult<RemoteReturnClaim>>.Success(new(claims, hasMore ? nextOffset.ToString(CultureInfo.InvariantCulture) : null, hasMore, pageResult.TotalCount, issues), response.RateLimit);
        }
        catch (JsonException)
        {
            return Failure<AdapterPageResult<RemoteReturnClaim>>(AdapterErrorClass.ContractViolation, "HEPSIBURADA_CLAIM_LIST_CONTRACT_INVALID", "Hepsiburada talep listesi beklenen sayfa sözleşmesiyle eşleşmiyor.", HttpStatusCode.BadGateway);
        }
    }
    async Task<AdapterResult<RemoteReturnClaim>> IReturnPort.GetAsync(AdapterContext context, string externalReturnId, CancellationToken cancellationToken) => await Unsupported<RemoteReturnClaim>("Hepsiburada tekil talep detayı için belgeli bir GET endpoint'i sunmuyor; durum taramasıyla güncellenmeye devam edecek.");
    public Task<AdapterResult<IReadOnlyList<ReturnIssueReason>>> IssueReasonsAsync(AdapterContext context, CancellationToken cancellationToken) => Task.FromResult(AdapterResult<IReadOnlyList<ReturnIssueReason>>.Success(ClaimRejectionReasons));
    public async Task<AdapterResult<ReturnActionResult>> ExecuteAsync(AdapterContext context, ReturnActionCommand command, CancellationToken cancellationToken)
    {
        if (!string.Equals(settings.AuthenticationMode, "BASIC", StringComparison.OrdinalIgnoreCase))
            return await Unsupported<ReturnActionResult>("Hepsiburada auth biçimi SIT hesabında doğrulanana kadar talep aksiyonu gönderilmedi.");
        var account = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken);
        if (account is null) return Failure<ReturnActionResult>(AdapterErrorClass.Authentication, "HEPSIBURADA_CREDENTIAL_INVALID", "Hepsiburada bağlantı bilgileri bulunamadı.", HttpStatusCode.Unauthorized);
        if (!ExternalWriteGate.AllowsRuntimeWrite(account.Connection, context, GlobalWritesEnabled))
            return await Unsupported<ReturnActionResult>("Hepsiburada talep aksiyonu yalnız doğrulanmış Stage bağlantısında veya dış yazma kapıları açılmış canlı bağlantıda kullanılabilir.");
        if (string.IsNullOrWhiteSpace(command.ExternalClaimId))
            return Failure<ReturnActionResult>(AdapterErrorClass.Validation, "HEPSIBURADA_CLAIM_NUMBER_REQUIRED", "Talep numarası zorunludur.", HttpStatusCode.BadRequest);

        var action = command.Action.Trim().ToUpperInvariant();
        string path;
        object body;
        string resultStatus;
        if (action == "APPROVE")
        {
            var finalizedWith = command.FinalizedWith?.Trim();
            if (finalizedWith is not ("Refund" or "Change"))
                return Failure<ReturnActionResult>(AdapterErrorClass.Validation, "HEPSIBURADA_FINALIZED_WITH_REQUIRED", "Talep kabulünde iade veya ürün değişimi seçilmelidir.", HttpStatusCode.BadRequest);
            path = AcceptClaim(account, command.ExternalClaimId);
            body = new { FinalizedWith = finalizedWith };
            resultStatus = "ACCEPTED";
        }
        else if (action == "PREAPPROVAL_CONFIRM")
        {
            path = ConfirmClaimPreApproval(account, command.ExternalClaimId);
            body = new { preApprovalReason = "ProductInvestigation" };
            resultStatus = "PREAPPROVAL_CONFIRM_SUBMITTED";
        }
        else if (action == "REJECT")
        {
            if (string.IsNullOrWhiteSpace(command.ReasonCode) || !ClaimRejectionReasons.Any(reason => reason.Id == command.ReasonCode)
                || string.IsNullOrWhiteSpace(command.Explanation) || command.Explanation.Trim().Length > 500)
                return Failure<ReturnActionResult>(AdapterErrorClass.Validation, "HEPSIBURADA_CLAIM_REJECTION_INVALID", "Talep reddinde belgelenmiş ret nedeni ve en fazla 500 karakterlik açıklama zorunludur.", HttpStatusCode.BadRequest);
            path = RejectClaim(account, command.ExternalClaimId);
            body = new { ClaimRejectionReason = command.ReasonCode, MerchantStatement = command.Explanation.Trim() };
            resultStatus = "REJECTED";
        }
        else
            return Failure<ReturnActionResult>(AdapterErrorClass.Validation, "HEPSIBURADA_CLAIM_ACTION_INVALID", "Talep aksiyonu APPROVE, PREAPPROVAL_CONFIRM veya REJECT olmalıdır.", HttpStatusCode.BadRequest);

        using var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        var response = await SendAsync(account, account.OmsBaseAddress, HttpMethod.Post, path, content, cancellationToken);
        using var responseDocument = response.Value;
        return response.IsSuccess
            ? AdapterResult<ReturnActionResult>.Success(new(command.ExternalClaimId, resultStatus, null), response.RateLimit)
            : AdapterResult<ReturnActionResult>.Failure(response.Error!, response.RateLimit);
    }
    internal static string Claims(HepsiburadaRequestContext context, string status, string query) => $"claims/merchantId/{Uri.EscapeDataString(context.Connection.ExternalStoreId)}/status/{Uri.EscapeDataString(status)}?{query}";
    internal static string AcceptClaim(HepsiburadaRequestContext context, string claimNumber) => $"claims/number/{Uri.EscapeDataString(claimNumber)}/accept";
    internal static string ConfirmClaimPreApproval(HepsiburadaRequestContext context, string claimNumber) => $"claims/number/{Uri.EscapeDataString(claimNumber)}/preapprovalconfirm";
    internal static string RejectClaim(HepsiburadaRequestContext context, string claimNumber) => $"claims/number/{Uri.EscapeDataString(claimNumber)}/reject";
    internal static string ClaimQuery(
        int offset,
        int limit,
        DateTimeOffset? beginDate,
        DateTimeOffset? endDate,
        DateTimeOffset? statusBeginDate = null,
        DateTimeOffset? statusEndDate = null)
    {
        var query = new List<string>
        {
            $"offset={Math.Max(0, offset).ToString(CultureInfo.InvariantCulture)}",
            $"limit={Math.Clamp(limit, 1, 100).ToString(CultureInfo.InvariantCulture)}"
        };
        if (beginDate is { } begin)
            query.Add("beginDate=" + Uri.EscapeDataString(HepsiburadaJsonMapper.QueryTime(begin)));
        if (endDate is { } end)
            query.Add("endDate=" + Uri.EscapeDataString(HepsiburadaJsonMapper.QueryTime(end)));
        if (statusBeginDate is { } statusBegin)
            query.Add("statusBeginDate=" + Uri.EscapeDataString(HepsiburadaJsonMapper.QueryTime(statusBegin)));
        if (statusEndDate is { } statusFinish)
            query.Add("statusEndDate=" + Uri.EscapeDataString(HepsiburadaJsonMapper.QueryTime(statusFinish)));
        return string.Join('&', query);
    }

    internal static bool TryClaimStatus(string? value, out string status)
    {
        var normalized = value?.Trim().Replace("_", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal);
        var match = ClaimStatuses.FirstOrDefault(candidate => string.Equals(candidate, normalized, StringComparison.OrdinalIgnoreCase));
        // C31: the claims path takes the status in its documented PascalCase (e.g. NewRequest); other casings
        // are answered "Wrong Claim Status".
        status = match ?? "";
        return match is not null;
    }
}
