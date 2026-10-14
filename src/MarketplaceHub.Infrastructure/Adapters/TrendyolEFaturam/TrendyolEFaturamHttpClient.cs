using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Infrastructure.Adapters.TrendyolEFaturam.Contracts;
using MarketplaceHub.Infrastructure.Adapters.TrendyolEFaturam.ErrorMapping;
using MarketplaceHub.Infrastructure.Adapters.TrendyolEFaturam.Mapping;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace MarketplaceHub.Infrastructure.Adapters.TrendyolEFaturam;

public sealed class TrendyolEFaturamHttpClient(
    IHttpClientFactory clients,
    TrendyolEFaturamAuthenticationHandler authentication,
    IConfiguration configuration,
    Microsoft.Extensions.Options.IOptions<TrendyolEFaturamOptions> options,
    SafeRemoteDocumentDownloader documents,
    TimeProvider timeProvider,
    ILogger<TrendyolEFaturamHttpClient> logger) : IInvoiceProviderPort
{
    private const string ConnectionProbeInvoiceUuid = "00000000-0000-0000-0000-000000000000";
    private readonly TrendyolEFaturamOptions _options = options.Value;
    private TimeSpan RequestTimeout => _options.Timeout > TimeSpan.Zero && _options.Timeout <= TimeSpan.FromMinutes(2) ? _options.Timeout : TimeSpan.FromSeconds(30);
    private bool GlobalWritesEnabled => configuration.GetValue<bool>("FeatureFlags:ExternalWrites");

    public async Task<AdapterResult<ConnectionIdentity>> TestConnectionAsync(AdapterContext context, CancellationToken cancellationToken)
    {
        var configured = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken);
        if (configured is null) return AdapterResult<ConnectionIdentity>.Failure(TrendyolEFaturamErrorMapper.Configuration());
        var access = await AcquireAccess(configured, cancellationToken);
        if (!access.IsSuccess) return AdapterResult<ConnectionIdentity>.Failure(access.Error!, access.RateLimit);

        // A successful sign-in only proves that the portal account exists. API_USER access can
        // still be missing, in which case every invoice operation is rejected with 401. Probe a
        // non-existent document through a read-only protected endpoint so the connection is not
        // marked VERIFIED until the freshly issued token is accepted by the invoice API.
        var protectedApiProbe = await SendAuthorized(
            configured,
            access.Value!.AccessToken,
            HttpMethod.Get,
            TrendyolEFaturamEndpoints.EArchiveStatus(ConnectionProbeInvoiceUuid),
            null,
            cancellationToken);
        if (!protectedApiProbe.IsSuccess
            && protectedApiProbe.Error?.HttpStatus is not (404 or 409))
            return AdapterResult<ConnectionIdentity>.Failure(protectedApiProbe.Error!, protectedApiProbe.RateLimit);

        return AdapterResult<ConnectionIdentity>.Success(new(
            "TRENDYOL_EFATURAM",
            configured.Connection.Environment,
            configured.Connection.ExternalStoreId,
            "1.0.0",
            $"company:{access.Value!.CompanyId};user:{access.Value.UserId};model:API_USER"), access.RateLimit);
    }

    public async Task<AdapterResult<InvoiceSubmissionResult>> SubmitAsync(AdapterContext context, InvoiceSubmission submission, CancellationToken cancellationToken)
    {
        var configured = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken);
        if (configured is null) return AdapterResult<InvoiceSubmissionResult>.Failure(TrendyolEFaturamErrorMapper.Configuration());
        if (!CanWrite(configured, context)) return AdapterResult<InvoiceSubmissionResult>.Failure(TrendyolEFaturamErrorMapper.Unsupported("E-Faturam dış gönderim izinleri kapalı."));
        var access = await AcquireAccess(configured, cancellationToken);
        if (!access.IsSuccess) return AdapterResult<InvoiceSubmissionResult>.Failure(access.Error!, access.RateLimit);
        string officialPayload;
        // The active integration authenticates the business' own portal account directly.
        // PARTNER is reserved for marketplace customerSignIn/sub-customer sessions; the
        // provider's direct-account E-Archive example uses PORTAL for this sign-in model.
        try
        {
            officialPayload = TrendyolEFaturamCanonicalPayload.Create(
                new(access.Value!.CompanyId, access.Value.UserId, null, "PORTAL"),
                submission.PayloadJson);
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or FormatException)
        {
            return AdapterResult<InvoiceSubmissionResult>.Failure(new(AdapterErrorClass.Validation, "EFATURAM_FISCAL_PAYLOAD_INVALID", exception.Message, null, null, null));
        }
        var endpoint = submission.InvoiceType == "EARSIVFATURA" ? TrendyolEFaturamEndpoints.CreateEArchive : TrendyolEFaturamEndpoints.CreateOutgoingInvoice;
        // The invoice gateway does not reliably consume chunked JsonContent bodies and answers
        // with "Failed to read request" before model validation. ByteArrayContent provides an
        // exact Content-Length and the same application/json media type as the official curl
        // example, while preserving the already validated canonical payload byte-for-byte.
        using var payload = new ByteArrayContent(Encoding.UTF8.GetBytes(officialPayload));
        payload.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        var response = await SendAuthorized(configured, access.Value!.AccessToken, HttpMethod.Post, endpoint, payload, cancellationToken);
        if (!response.IsSuccess) return AdapterResult<InvoiceSubmissionResult>.Failure(response.Error!, response.RateLimit);
        try { return AdapterResult<InvoiceSubmissionResult>.Success(TrendyolEFaturamJsonMapper.OutgoingInvoice(response.Value!.Body, response.Value.RemoteRequestId), response.RateLimit); }
        catch (JsonException) { return AdapterResult<InvoiceSubmissionResult>.Failure(TrendyolEFaturamErrorMapper.Contract(), response.RateLimit); }
    }

    public async Task<AdapterResult<InvoiceRemoteStatus>> QueryStatusAsync(AdapterContext context, ExternalInvoiceReference reference, CancellationToken cancellationToken)
    {
        var configured = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken);
        if (configured is null) return AdapterResult<InvoiceRemoteStatus>.Failure(TrendyolEFaturamErrorMapper.Configuration());
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
            // The provider does not publish a stable UUID-based outgoing E-Invoice status path.
            // This is endpoint configuration, not capability evidence: manual Stage requests are
            // allowed to reach the provider as soon as a validated relative path is configured.
            // Until then the adapter fails closed rather than guessing an endpoint.
            if (string.IsNullOrWhiteSpace(_options.OutgoingInvoiceStatusPath))
                return AdapterResult<InvoiceRemoteStatus>.Failure(new(
                    AdapterErrorClass.Validation,
                    "EFATURAM_EINVOICE_STATUS_PATH_NOT_CONFIGURED",
                    "Giden E-Fatura durum sorgusu için sağlayıcının güncel göreli endpoint yolu yapılandırılmalıdır.",
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
        if (configured is null) return AdapterResult<RemoteInvoiceDocument>.Failure(TrendyolEFaturamErrorMapper.Configuration());
        if (!string.Equals(documentKind, "PDF", StringComparison.OrdinalIgnoreCase)) return AdapterResult<RemoteInvoiceDocument>.Failure(TrendyolEFaturamErrorMapper.Unsupported("Yalnız PDF belge akışı doğrulandı."));
        var providerType = reference.InvoiceType == "EARSIVFATURA" ? "EARCHIVE" : reference.InvoiceType is "TEMELFATURA" or "TICARIFATURA" ? "EINVOICE" : null;
        if (providerType is null) return AdapterResult<RemoteInvoiceDocument>.Failure(TrendyolEFaturamErrorMapper.Contract());
        var documentUuid = reference.EttnUuid ?? reference.ExternalReference;
        var access = await AcquireAccess(configured, cancellationToken);
        if (!access.IsSuccess) return AdapterResult<RemoteInvoiceDocument>.Failure(access.Error!, access.RateLimit);
        var response = await SendAuthorized(configured, access.Value!.AccessToken, HttpMethod.Post, TrendyolEFaturamEndpoints.PermanentDocumentUrl,
            JsonContent.Create(new { companyId = access.Value.CompanyId, documentUuid, documentType = providerType, fileExtension = "pdf" }), cancellationToken, "text/plain");
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
        if (configured is null) return AdapterResult<InvoiceCancellationResult>.Failure(TrendyolEFaturamErrorMapper.Configuration());
        if (!CanWrite(configured, context)) return AdapterResult<InvoiceCancellationResult>.Failure(TrendyolEFaturamErrorMapper.Unsupported("E-Faturam dış gönderim izinleri kapalı."));
        var access = await AcquireAccess(configured, cancellationToken);
        if (!access.IsSuccess) return AdapterResult<InvoiceCancellationResult>.Failure(access.Error!, access.RateLimit);
        var uuid = command.EttnUuid ?? command.ExternalReference;
        var response = await SendAuthorized(configured, access.Value!.AccessToken, HttpMethod.Post, TrendyolEFaturamEndpoints.CancelEArchive, JsonContent.Create(new { invoiceUuid = uuid, companyId = access.Value.CompanyId }), cancellationToken);
        if (!response.IsSuccess) return AdapterResult<InvoiceCancellationResult>.Failure(response.Error!, response.RateLimit);
        return AdapterResult<InvoiceCancellationResult>.Success(new(command.ExternalReference, "CANCELLATION_SUBMITTED", "PENDING", false), response.RateLimit);
    }

    private async Task<AdapterResult<TrendyolEFaturamAccessContext>> AcquireAccess(TrendyolEFaturamRequestContext context, CancellationToken cancellationToken)
    {
        var login = await SignIn(context, context.Credential.Email!, context.Credential.Password!, cancellationToken);
        if (!login.IsSuccess) return AdapterResult<TrendyolEFaturamAccessContext>.Failure(login.Error!, login.RateLimit);
        if (!TrendyolEFaturamDirectAccountAccess.TryRead(login.Value!, out var access))
            return AdapterResult<TrendyolEFaturamAccessContext>.Failure(new(
                AdapterErrorClass.ContractViolation,
                "EFATURAM_SIGNIN_SCOPE_MISSING",
                "E-Faturam oturumu tekil firma ve kullanıcı kapsamı döndürmedi.",
                null, null, null), login.RateLimit);
        var now = timeProvider.GetUtcNow();
        logger.LogInformation(
            "E-Faturam access token metadata. IssuedAt={IssuedAt} NotBefore={NotBefore} ExpiresAt={ExpiresAt} Issuer={Issuer} Audience={Audience} HasInvoiceCreatePrivilege={HasInvoiceCreatePrivilege} HasInvoiceReadPrivilege={HasInvoiceReadPrivilege}",
            access.IssuedAt,
            access.NotBefore,
            access.ExpiresAt,
            access.Issuer,
            access.Audience,
            access.HasInvoiceCreatePrivilege,
            access.HasInvoiceReadPrivilege);
        if (access.ExpiresAt is { } expiresAt && expiresAt <= now)
            return AdapterResult<TrendyolEFaturamAccessContext>.Failure(new(
                AdapterErrorClass.Authentication,
                "EFATURAM_ACCESS_TOKEN_EXPIRED",
                "E-Faturam sign-in yanıtında süresi dolmuş bir access token döndürdü.",
                401, null, null), login.RateLimit);
        if (access.NotBefore is { } notBefore && notBefore > now)
        {
            var wait = notBefore - now;
            if (wait > TimeSpan.FromSeconds(15))
                return AdapterResult<TrendyolEFaturamAccessContext>.Failure(new(
                    AdapterErrorClass.Authentication,
                    "EFATURAM_ACCESS_TOKEN_NOT_ACTIVE",
                    "E-Faturam sign-in yanıtındaki access token henüz geçerli değil.",
                    401, null, null), login.RateLimit);
            await Task.Delay(wait, timeProvider, cancellationToken);
        }
        return AdapterResult<TrendyolEFaturamAccessContext>.Success(access, login.RateLimit);
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
    private bool CanWrite(TrendyolEFaturamRequestContext configured, AdapterContext context) =>
        IntegrationRuntimePolicy.IsManualStage(configured.Connection, context)
        || (IntegrationRuntimePolicy.IsProduction(configured.Connection) && GlobalWritesEnabled && configured.ExternalWritesEnabled);

    private async Task<AdapterResult<AuthorizedResponse>> SendAuthorized(TrendyolEFaturamRequestContext context, string token, HttpMethod method, string endpoint, HttpContent? content, CancellationToken cancellationToken, string acceptMediaType = "application/json")
    {
        using var request = new HttpRequestMessage(method, new Uri(context.BaseAddress, endpoint)) { Content = content };
        request.Headers.Accept.ParseAdd(acceptMediaType);
        // The public API contract documents x-access-token, while the provider's current Stage
        // portal sends the same access token as Authorization: Bearer for protected invoice API
        // calls. Send both equivalent representations so direct API_USER traffic remains
        // compatible with the documented gateway and the provider's own active client.
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.TryAddWithoutValidation("x-access-token", token);
        request.Headers.TryAddWithoutValidation("Accept-Language", "tr");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(RequestTimeout);
        try
        {
            using var response = await clients.CreateClient("TrendyolEFaturam").SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token);
            var retryAfter = response.Headers.RetryAfter?.Delta;
            var rate = new RateLimitMetadata(null, response.Headers.RetryAfter?.Date, retryAfter);
            var remoteId = response.Headers.TryGetValues("x-request-id", out var ids) ? ids.FirstOrDefault() : null;
            if (!response.IsSuccessStatusCode)
            {
                remoteId ??= await TrendyolEFaturamProblemDetails.TryReadReferenceAsync(response.Content, linked.Token);
                return AdapterResult<AuthorizedResponse>.Failure(TrendyolEFaturamErrorMapper.FromAuthorizedStatus(response.StatusCode, retryAfter, remoteId), rate);
            }
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
