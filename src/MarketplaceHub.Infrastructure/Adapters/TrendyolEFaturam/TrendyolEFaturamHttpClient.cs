using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Infrastructure.Adapters.TrendyolEFaturam.Contracts;
using MarketplaceHub.Infrastructure.Adapters.TrendyolEFaturam.ErrorMapping;
using MarketplaceHub.Infrastructure.Adapters.TrendyolEFaturam.Mapping;
using Microsoft.Extensions.Configuration;

namespace MarketplaceHub.Infrastructure.Adapters.TrendyolEFaturam;

public sealed class TrendyolEFaturamHttpClient(
    IHttpClientFactory clients,
    TrendyolEFaturamAuthenticationHandler authentication,
    IConfiguration configuration,
    Microsoft.Extensions.Options.IOptions<TrendyolEFaturamOptions> options,
    SafeRemoteDocumentDownloader documents) : IInvoiceProviderPort
{
    private readonly TrendyolEFaturamOptions _options = options.Value;
    private TimeSpan RequestTimeout => _options.Timeout > TimeSpan.Zero && _options.Timeout <= TimeSpan.FromMinutes(2) ? _options.Timeout : TimeSpan.FromSeconds(30);
    private bool GlobalWritesEnabled => configuration.GetValue<bool>("FeatureFlags:ExternalWrites");

    public async Task<AdapterResult<ConnectionIdentity>> TestConnectionAsync(AdapterContext context, CancellationToken cancellationToken)
    {
        var configured = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken);
        if (configured is null) return AdapterResult<ConnectionIdentity>.Failure(TrendyolEFaturamErrorMapper.Configuration());
        var access = await AcquireAccess(configured, cancellationToken);
        if (!access.IsSuccess) return AdapterResult<ConnectionIdentity>.Failure(access.Error!, access.RateLimit);
        return AdapterResult<ConnectionIdentity>.Success(new(
            "TRENDYOL_EFATURAM",
            configured.Connection.Environment,
            configured.Connection.ExternalStoreId,
            "1.0.0",
            $"company:{access.Value!.CompanyId};user:{access.Value.UserId};model:{configured.IntegrationModel}"), access.RateLimit);
    }

    public async Task<AdapterResult<TaxpayerResult>> QueryTaxpayerAsync(AdapterContext context, TaxpayerQuery query, CancellationToken cancellationToken)
    {
        if (query.TaxId.Length is not (10 or 11) || !query.TaxId.All(char.IsAsciiDigit))
            return AdapterResult<TaxpayerResult>.Failure(new(AdapterErrorClass.Validation, "EFATURAM_TAX_ID_INVALID", "VKN/TCKN 10 veya 11 rakam olmalıdır.", null, null, null));
        var configured = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken);
        if (configured is null) return AdapterResult<TaxpayerResult>.Failure(TrendyolEFaturamErrorMapper.Configuration());
        if (configured.IntegrationModel != "MARKETPLACE" || !long.TryParse(configured.Connection.ExternalStoreId, out var partnerId) || partnerId <= 0)
            return AdapterResult<TaxpayerResult>.Failure(TrendyolEFaturamErrorMapper.Unsupported("Mükellef sorgusu pazaryeri entegratörü modeli ve sayısal Partner ID gerektirir."));
        var partner = await SignIn(configured, configured.Credential.PartnerEmail!, configured.Credential.PartnerPassword!, cancellationToken);
        if (!partner.IsSuccess) return AdapterResult<TaxpayerResult>.Failure(partner.Error!, partner.RateLimit);
        var response = await SendAuthorized(configured, partner.Value!, HttpMethod.Get, TrendyolEFaturamEndpoints.TaxpayerStatus(partnerId, query.TaxId), null, cancellationToken);
        if (!response.IsSuccess) return AdapterResult<TaxpayerResult>.Failure(response.Error!, response.RateLimit);
        try { return AdapterResult<TaxpayerResult>.Success(TrendyolEFaturamJsonMapper.Taxpayer(response.Value!.Body), response.RateLimit); }
        catch (JsonException) { return AdapterResult<TaxpayerResult>.Failure(TrendyolEFaturamErrorMapper.Contract(), response.RateLimit); }
    }

    public async Task<AdapterResult<InvoiceSubmissionResult>> SubmitAsync(AdapterContext context, InvoiceSubmission submission, CancellationToken cancellationToken)
    {
        var configured = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken);
        if (configured is null) return AdapterResult<InvoiceSubmissionResult>.Failure(TrendyolEFaturamErrorMapper.Configuration());
        if (!CanWrite(configured)) return AdapterResult<InvoiceSubmissionResult>.Failure(TrendyolEFaturamErrorMapper.Unsupported("E-Faturam dış gönderim izinleri kapalı."));
        string officialPayload;
        try { officialPayload = TrendyolEFaturamCanonicalPayload.Create(configured.Settings, submission.PayloadJson); }
        catch (Exception exception) when (exception is JsonException or ArgumentException or FormatException)
        {
            return AdapterResult<InvoiceSubmissionResult>.Failure(new(AdapterErrorClass.Validation, "EFATURAM_FISCAL_PAYLOAD_INVALID", exception.Message, null, null, null));
        }
        var access = await AcquireAccess(configured, cancellationToken);
        if (!access.IsSuccess) return AdapterResult<InvoiceSubmissionResult>.Failure(access.Error!, access.RateLimit);
        using var payload = JsonDocument.Parse(officialPayload);
        var endpoint = submission.InvoiceType == "EARSIVFATURA" ? TrendyolEFaturamEndpoints.CreateEArchive : TrendyolEFaturamEndpoints.CreateOutgoingInvoice;
        var response = await SendAuthorized(configured, access.Value!.AccessToken, HttpMethod.Post, endpoint, JsonContent.Create(payload.RootElement), cancellationToken);
        if (!response.IsSuccess) return AdapterResult<InvoiceSubmissionResult>.Failure(response.Error!, response.RateLimit);
        try { return AdapterResult<InvoiceSubmissionResult>.Success(TrendyolEFaturamJsonMapper.OutgoingInvoice(response.Value!.Body, response.Value.RemoteRequestId), response.RateLimit); }
        catch (JsonException) { return AdapterResult<InvoiceSubmissionResult>.Failure(TrendyolEFaturamErrorMapper.Contract(), response.RateLimit); }
    }

    public async Task<AdapterResult<InvoiceRemoteStatus>> QueryStatusAsync(AdapterContext context, ExternalInvoiceReference reference, CancellationToken cancellationToken)
    {
        var configured = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken);
        if (configured is null || configured.Settings.CompanyId is not > 0) return AdapterResult<InvoiceRemoteStatus>.Failure(TrendyolEFaturamErrorMapper.Configuration());
        var access = await AcquireAccess(configured, cancellationToken);
        if (!access.IsSuccess) return AdapterResult<InvoiceRemoteStatus>.Failure(access.Error!, access.RateLimit);
        var uuid = reference.EttnUuid ?? reference.ExternalReference;
        AdapterResult<AuthorizedResponse> response;
        if (reference.InvoiceType == "EARSIVFATURA")
        {
            response = await SendAuthorized(configured, access.Value!.AccessToken, HttpMethod.Get, TrendyolEFaturamEndpoints.EArchiveStatus(uuid), null, cancellationToken);
        }
        else
        {
            // The public documentation does not expose a stable UUID-based outgoing E-Invoice
            // status path. The exact Stage/SIT-proven relative path must be configured by an
            // operator; otherwise the adapter fails closed instead of guessing an endpoint.
            if (string.IsNullOrWhiteSpace(_options.OutgoingInvoiceStatusPath))
                return AdapterResult<InvoiceRemoteStatus>.Failure(new(
                    AdapterErrorClass.NotSupported,
                    "EFATURAM_EINVOICE_STATUS_EVIDENCE_REQUIRED",
                    "Giden E-Fatura durum sorgusu için doğrulanmış Stage/SIT endpoint kanıtı ve yapılandırması gereklidir.",
                    null, null, null));
            var search = new
            {
                invoiceUuid = uuid,
                companyId = access.Value!.CompanyId,
                pagination = new { page = 0, size = 1 }
            };
            if (!TryNormalizeRelativePath(_options.OutgoingInvoiceStatusPath, out var statusPath))
                return AdapterResult<InvoiceRemoteStatus>.Failure(new(
                    AdapterErrorClass.ContractViolation,
                    "EFATURAM_EINVOICE_STATUS_PATH_INVALID",
                    "Giden E-Fatura durum sorgusu yolu göreli, boş olmayan ve üst dizin geçişi içermeyen bir API yolu olmalıdır.",
                    null, null, null));
            response = await SendAuthorized(configured, access.Value.AccessToken, HttpMethod.Post, statusPath, JsonContent.Create(search), cancellationToken);
        }
        if (!response.IsSuccess) return AdapterResult<InvoiceRemoteStatus>.Failure(response.Error!, response.RateLimit);
        try { return AdapterResult<InvoiceRemoteStatus>.Success(TrendyolEFaturamJsonMapper.InvoiceStatus(response.Value!.Body, reference.ExternalReference), response.RateLimit); }
        catch (JsonException) { return AdapterResult<InvoiceRemoteStatus>.Failure(TrendyolEFaturamErrorMapper.Contract(), response.RateLimit); }
    }

    public async Task<AdapterResult<RemoteInvoiceDocument>> GetDocumentAsync(AdapterContext context, ExternalInvoiceReference reference, string documentKind, CancellationToken cancellationToken)
    {
        var configured = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken);
        if (configured is null || configured.Settings.CompanyId is not > 0) return AdapterResult<RemoteInvoiceDocument>.Failure(TrendyolEFaturamErrorMapper.Configuration());
        if (!string.Equals(documentKind, "PDF", StringComparison.OrdinalIgnoreCase)) return AdapterResult<RemoteInvoiceDocument>.Failure(TrendyolEFaturamErrorMapper.Unsupported("Yalnız PDF belge akışı doğrulandı."));
        var providerType = reference.InvoiceType == "EARSIVFATURA" ? "EARCHIVE" : reference.InvoiceType is "TEMELFATURA" or "TICARIFATURA" ? "EINVOICE" : null;
        if (providerType is null) return AdapterResult<RemoteInvoiceDocument>.Failure(TrendyolEFaturamErrorMapper.Contract());
        var documentUuid = reference.EttnUuid ?? reference.ExternalReference;
        var access = await AcquireAccess(configured, cancellationToken);
        if (!access.IsSuccess) return AdapterResult<RemoteInvoiceDocument>.Failure(access.Error!, access.RateLimit);
        var response = await SendAuthorized(configured, access.Value!.AccessToken, HttpMethod.Post, TrendyolEFaturamEndpoints.PermanentDocumentUrl,
            JsonContent.Create(new { companyId = access.Value.CompanyId, documentUuid, documentType = providerType, fileExtension = "pdf" }), cancellationToken);
        if (!response.IsSuccess) return AdapterResult<RemoteInvoiceDocument>.Failure(response.Error!, response.RateLimit);
        string permanentUrl;
        try { permanentUrl = TrendyolEFaturamJsonMapper.PermanentDocumentUrl(response.Value!.Body); }
        catch (JsonException) { return AdapterResult<RemoteInvoiceDocument>.Failure(TrendyolEFaturamErrorMapper.Contract(), response.RateLimit); }
        try
        {
            var download = await documents.DownloadPdfAsync(permanentUrl, cancellationToken);
            if (download.RemoteStatus is { } status) return AdapterResult<RemoteInvoiceDocument>.Failure(TrendyolEFaturamErrorMapper.FromStatus(status, null, null));
            if (!download.Succeeded) return AdapterResult<RemoteInvoiceDocument>.Failure(new(AdapterErrorClass.ContractViolation, download.ErrorCode ?? "EFATURAM_DOCUMENT_REJECTED", "E-Faturam belge adresi veya PDF içeriği güvenlik doğrulamasını geçemedi.", null, null, null));
            return AdapterResult<RemoteInvoiceDocument>.Success(new("PDF", "application/pdf", $"invoice-{documentUuid}.pdf", download.Content!, documentUuid, download.FinalUrl), response.RateLimit);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return AdapterResult<RemoteInvoiceDocument>.Failure(new(AdapterErrorClass.TransientNetwork, "EFATURAM_DOCUMENT_TIMEOUT", "E-Faturam belgesi zaman aşımı nedeniyle indirilemedi.", null, TimeSpan.FromSeconds(5), null)); }
        catch (Exception exception) when (exception is HttpRequestException or SocketException) { return AdapterResult<RemoteInvoiceDocument>.Failure(new(AdapterErrorClass.TransientNetwork, "EFATURAM_DOCUMENT_NETWORK_ERROR", "E-Faturam belgesi indirilemedi.", null, TimeSpan.FromSeconds(5), null)); }
    }

    public async Task<AdapterResult<InvoiceCancellationResult>> CancelAsync(AdapterContext context, InvoiceCancellation command, CancellationToken cancellationToken)
    {
        var configured = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken);
        if (configured is null || configured.Settings.CompanyId is not > 0) return AdapterResult<InvoiceCancellationResult>.Failure(TrendyolEFaturamErrorMapper.Configuration());
        if (!CanWrite(configured)) return AdapterResult<InvoiceCancellationResult>.Failure(TrendyolEFaturamErrorMapper.Unsupported("E-Faturam dış gönderim izinleri kapalı."));
        var access = await AcquireAccess(configured, cancellationToken);
        if (!access.IsSuccess) return AdapterResult<InvoiceCancellationResult>.Failure(access.Error!, access.RateLimit);
        var uuid = command.EttnUuid ?? command.ExternalReference;
        var response = await SendAuthorized(configured, access.Value!.AccessToken, HttpMethod.Post, TrendyolEFaturamEndpoints.CancelEArchive, JsonContent.Create(new { invoiceUuid = uuid, companyId = access.Value.CompanyId }), cancellationToken);
        if (!response.IsSuccess) return AdapterResult<InvoiceCancellationResult>.Failure(response.Error!, response.RateLimit);
        return AdapterResult<InvoiceCancellationResult>.Success(new(command.ExternalReference, "CANCELLATION_SUBMITTED", "PENDING", false), response.RateLimit);
    }

    private async Task<AdapterResult<TrendyolEFaturamAccessContext>> AcquireAccess(TrendyolEFaturamRequestContext context, CancellationToken cancellationToken)
    {
        if (context.IntegrationModel == "API_USER")
        {
            if (context.Settings.CompanyId is not > 0 || context.Settings.UserId is not > 0) return AdapterResult<TrendyolEFaturamAccessContext>.Failure(TrendyolEFaturamErrorMapper.Configuration());
            var login = await SignIn(context, context.Credential.Email!, context.Credential.Password!, cancellationToken);
            return login.IsSuccess
                ? AdapterResult<TrendyolEFaturamAccessContext>.Success(new(login.Value!, context.Settings.CompanyId.Value, context.Settings.UserId.Value, null), login.RateLimit)
                : AdapterResult<TrendyolEFaturamAccessContext>.Failure(login.Error!, login.RateLimit);
        }

        var partner = await SignIn(context, context.Credential.PartnerEmail!, context.Credential.PartnerPassword!, cancellationToken);
        if (!partner.IsSuccess) return AdapterResult<TrendyolEFaturamAccessContext>.Failure(partner.Error!, partner.RateLimit);
        var response = await SendAuthorized(context, partner.Value!, HttpMethod.Post, TrendyolEFaturamEndpoints.CustomerSignIn,
            JsonContent.Create(new { email = context.Credential.CustomerEmail, password = context.Credential.CustomerPassword, taxId = context.Credential.CustomerTaxId }), cancellationToken);
        if (!response.IsSuccess) return AdapterResult<TrendyolEFaturamAccessContext>.Failure(response.Error!, response.RateLimit);
        try
        {
            var customer = TrendyolEFaturamJsonMapper.CustomerAccess(response.Value!.Body);
            var partnerId = long.TryParse(context.Connection.ExternalStoreId, out var parsed) ? parsed : customer.PartnerCustomerId;
            if (context.Settings.CompanyId is > 0 && context.Settings.CompanyId != customer.CompanyId || context.Settings.UserId is > 0 && context.Settings.UserId != customer.UserId)
                return AdapterResult<TrendyolEFaturamAccessContext>.Failure(new(AdapterErrorClass.Validation, "EFATURAM_CUSTOMER_SCOPE_MISMATCH", "customerSignIn companyId/userId ile bağlantı ayarları eşleşmiyor.", null, null, response.Value.RemoteRequestId), response.RateLimit);
            return AdapterResult<TrendyolEFaturamAccessContext>.Success(new(customer.AccessToken, customer.CompanyId, customer.UserId, partnerId), response.RateLimit);
        }
        catch (JsonException)
        {
            return AdapterResult<TrendyolEFaturamAccessContext>.Failure(TrendyolEFaturamErrorMapper.Contract(), response.RateLimit);
        }
    }

    private async Task<AdapterResult<string>> SignIn(TrendyolEFaturamRequestContext context, string email, string password, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(context.BaseAddress, TrendyolEFaturamEndpoints.SignIn)) { Content = JsonContent.Create(new { email, password }) };
        request.Headers.Accept.ParseAdd("application/json");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(RequestTimeout);
        try
        {
            using var response = await clients.CreateClient("TrendyolEFaturam").SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token);
            var retryAfter = response.Headers.RetryAfter?.Delta;
            var rate = new RateLimitMetadata(null, response.Headers.RetryAfter?.Date, retryAfter);
            var remoteId = response.Headers.TryGetValues("x-request-id", out var ids) ? ids.FirstOrDefault() : null;
            if (!response.IsSuccessStatusCode) return AdapterResult<string>.Failure(TrendyolEFaturamErrorMapper.FromStatus(response.StatusCode, retryAfter, remoteId), rate);
            if (!response.Headers.TryGetValues("x-access-token", out var tokens) || string.IsNullOrWhiteSpace(tokens.FirstOrDefault())) return AdapterResult<string>.Failure(TrendyolEFaturamErrorMapper.Contract(), rate);
            return AdapterResult<string>.Success(tokens.First(), rate);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return AdapterResult<string>.Failure(new(AdapterErrorClass.TransientNetwork, "EFATURAM_TIMEOUT", "E-Faturam bağlantı isteği zaman aşımına uğradı.", null, TimeSpan.FromSeconds(5), null)); }
        catch (HttpRequestException) { return AdapterResult<string>.Failure(new(AdapterErrorClass.TransientNetwork, "EFATURAM_NETWORK_ERROR", "E-Faturam ağına güvenli bağlantı kurulamadı.", null, TimeSpan.FromSeconds(5), null)); }
    }

    private bool CanWrite(TrendyolEFaturamRequestContext context) => GlobalWritesEnabled && context.ExternalWritesEnabled && context.IntegrationModel is "API_USER" or "MARKETPLACE";

    private async Task<AdapterResult<AuthorizedResponse>> SendAuthorized(TrendyolEFaturamRequestContext context, string token, HttpMethod method, string endpoint, HttpContent? content, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, new Uri(context.BaseAddress, endpoint)) { Content = content };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.TryAddWithoutValidation("x-access-token", token);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(RequestTimeout);
        try
        {
            using var response = await clients.CreateClient("TrendyolEFaturam").SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token);
            var retryAfter = response.Headers.RetryAfter?.Delta;
            var rate = new RateLimitMetadata(null, response.Headers.RetryAfter?.Date, retryAfter);
            var remoteId = response.Headers.TryGetValues("x-request-id", out var ids) ? ids.FirstOrDefault() : null;
            if (!response.IsSuccessStatusCode) return AdapterResult<AuthorizedResponse>.Failure(TrendyolEFaturamErrorMapper.FromStatus(response.StatusCode, retryAfter, remoteId), rate);
            return AdapterResult<AuthorizedResponse>.Success(new(await response.Content.ReadAsStringAsync(linked.Token), remoteId), rate);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return AdapterResult<AuthorizedResponse>.Failure(new(AdapterErrorClass.TransientNetwork, "EFATURAM_TIMEOUT", "E-Faturam isteği zaman aşımına uğradı.", null, TimeSpan.FromSeconds(5), null)); }
        catch (HttpRequestException) { return AdapterResult<AuthorizedResponse>.Failure(new(AdapterErrorClass.TransientNetwork, "EFATURAM_NETWORK_ERROR", "E-Faturam ağına güvenli bağlantı kurulamadı.", null, TimeSpan.FromSeconds(5), null)); }
    }

    private static bool TryNormalizeRelativePath(string value, out string path)
    {
        path = value.Trim();
        if (Uri.TryCreate(path, UriKind.Absolute, out _)) return false;
        path = path.TrimStart('/');
        return path.Length > 0 && !path.Contains("..", StringComparison.Ordinal);
    }

    private sealed record AuthorizedResponse(string Body, string? RemoteRequestId);
}
