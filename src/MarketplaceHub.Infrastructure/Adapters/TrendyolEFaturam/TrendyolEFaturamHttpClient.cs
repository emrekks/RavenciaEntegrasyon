using System.Net.Http.Json;
using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Infrastructure.Adapters.TrendyolEFaturam.Contracts;
using MarketplaceHub.Infrastructure.Adapters.TrendyolEFaturam.ErrorMapping;
using MarketplaceHub.Infrastructure.Adapters.TrendyolEFaturam.Mapping;
using Microsoft.Extensions.Configuration;

namespace MarketplaceHub.Infrastructure.Adapters.TrendyolEFaturam;

public sealed class TrendyolEFaturamHttpClient(IHttpClientFactory clients, TrendyolEFaturamAuthenticationHandler authentication, IConfiguration configuration) : IInvoiceProviderPort
{
    private bool GlobalWritesEnabled => configuration.GetValue<bool>("FeatureFlags:ExternalWrites");

    public async Task<AdapterResult<ConnectionIdentity>> TestConnectionAsync(AdapterContext context, CancellationToken cancellationToken)
    {
        var configured = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken); if (configured is null) return AdapterResult<ConnectionIdentity>.Failure(TrendyolEFaturamErrorMapper.Configuration());
        var login = await SignIn(configured, cancellationToken); if (!login.IsSuccess) return AdapterResult<ConnectionIdentity>.Failure(login.Error!, login.RateLimit);
        return AdapterResult<ConnectionIdentity>.Success(new("TRENDYOL_EFATURAM", configured.Connection.Environment, configured.Connection.ExternalStoreId, "1.0.0", configured.Connection.ExternalStoreId), login.RateLimit);
    }

    public Task<AdapterResult<TaxpayerResult>> QueryTaxpayerAsync(AdapterContext context, TaxpayerQuery query, CancellationToken cancellationToken) =>
        Task.FromResult(AdapterResult<TaxpayerResult>.Failure(TrendyolEFaturamErrorMapper.Unsupported("API kullanıcısı/pazaryeri entegratörü modeli ve partner scope test firma ile doğrulanmadan taxpayer HTTP çağrısı yapılmaz.")));

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
        var login = await SignIn(configured, cancellationToken); if (!login.IsSuccess) return AdapterResult<InvoiceSubmissionResult>.Failure(login.Error!, login.RateLimit);
        using var payload = JsonDocument.Parse(officialPayload);
        var response = await SendAuthorized(configured, login.Value!, HttpMethod.Post, TrendyolEFaturamEndpoints.CreateOutgoingInvoice, JsonContent.Create(payload.RootElement), cancellationToken);
        if (!response.IsSuccess) return AdapterResult<InvoiceSubmissionResult>.Failure(response.Error!, response.RateLimit);
        try { return AdapterResult<InvoiceSubmissionResult>.Success(TrendyolEFaturamJsonMapper.OutgoingInvoice(response.Value!.Body, response.Value.RemoteRequestId), response.RateLimit); }
        catch (JsonException) { return AdapterResult<InvoiceSubmissionResult>.Failure(TrendyolEFaturamErrorMapper.Contract(), response.RateLimit); }
    }

    public Task<AdapterResult<InvoiceRemoteStatus>> QueryStatusAsync(AdapterContext context, ExternalInvoiceReference reference, CancellationToken cancellationToken) =>
        Task.FromResult(AdapterResult<InvoiceRemoteStatus>.Failure(TrendyolEFaturamErrorMapper.Unsupported("E-Fatura/E-Arşiv status query ayrımı test firma kanıtı bekliyor.")));

    public async Task<AdapterResult<RemoteInvoiceDocument>> GetDocumentAsync(AdapterContext context, ExternalInvoiceReference reference, string documentKind, CancellationToken cancellationToken)
    {
        var configured = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken);
        if (configured is null || configured.Settings.CompanyId is not > 0) return AdapterResult<RemoteInvoiceDocument>.Failure(TrendyolEFaturamErrorMapper.Configuration());
        if (!string.Equals(documentKind, "PDF", StringComparison.OrdinalIgnoreCase)) return AdapterResult<RemoteInvoiceDocument>.Failure(TrendyolEFaturamErrorMapper.Unsupported("Yalnız PDF belge akışı doğrulandı."));
        var providerType = reference.InvoiceType == "EARSIVFATURA" ? "EARCHIVE" : reference.InvoiceType == "TEMELFATURA" ? "EINVOICE" : null;
        if (providerType is null) return AdapterResult<RemoteInvoiceDocument>.Failure(TrendyolEFaturamErrorMapper.Contract());
        var documentUuid = reference.EttnUuid ?? reference.ExternalReference;
        var login = await SignIn(configured, cancellationToken); if (!login.IsSuccess) return AdapterResult<RemoteInvoiceDocument>.Failure(login.Error!, login.RateLimit);
        var response = await SendAuthorized(configured, login.Value!, HttpMethod.Post, TrendyolEFaturamEndpoints.PermanentDocumentUrl,
            JsonContent.Create(new { companyId = configured.Settings.CompanyId.Value, documentUuid, documentType = providerType, fileExtension = "pdf" }), cancellationToken);
        if (!response.IsSuccess) return AdapterResult<RemoteInvoiceDocument>.Failure(response.Error!, response.RateLimit);
        string permanentUrl;
        try { permanentUrl = TrendyolEFaturamJsonMapper.PermanentDocumentUrl(response.Value!.Body); }
        catch (JsonException) { return AdapterResult<RemoteInvoiceDocument>.Failure(TrendyolEFaturamErrorMapper.Contract(), response.RateLimit); }
        try
        {
            using var download = await clients.CreateClient("TrendyolEFaturam").GetAsync(permanentUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!download.IsSuccessStatusCode) return AdapterResult<RemoteInvoiceDocument>.Failure(TrendyolEFaturamErrorMapper.FromStatus(download.StatusCode, null, null));
            if (download.Content.Headers.ContentLength is > 20_000_000) return AdapterResult<RemoteInvoiceDocument>.Failure(TrendyolEFaturamErrorMapper.Contract());
            var content = await download.Content.ReadAsByteArrayAsync(cancellationToken);
            if (content.Length == 0 || content.Length > 20_000_000) return AdapterResult<RemoteInvoiceDocument>.Failure(TrendyolEFaturamErrorMapper.Contract());
            return AdapterResult<RemoteInvoiceDocument>.Success(new("PDF", "application/pdf", $"invoice-{documentUuid}.pdf", content, documentUuid, permanentUrl), response.RateLimit);
        }
        catch (HttpRequestException) { return AdapterResult<RemoteInvoiceDocument>.Failure(new(AdapterErrorClass.TransientNetwork, "EFATURAM_DOCUMENT_NETWORK_ERROR", "E-Faturam belgesi indirilemedi.", null, TimeSpan.FromSeconds(5), null)); }
    }

    public Task<AdapterResult<InvoiceCancellationResult>> CancelAsync(AdapterContext context, InvoiceCancellation command, CancellationToken cancellationToken) =>
        Task.FromResult(AdapterResult<InvoiceCancellationResult>.Failure(TrendyolEFaturamErrorMapper.Unsupported("Cancellation mali policy ve provider capability kanıtı bekliyor.")));

    private async Task<AdapterResult<string>> SignIn(TrendyolEFaturamRequestContext context, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(context.BaseAddress, TrendyolEFaturamEndpoints.SignIn)) { Content = JsonContent.Create(new { email = context.Email, password = context.Password }) };
        request.Headers.Accept.ParseAdd("application/json"); using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); linked.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            using var response = await clients.CreateClient("TrendyolEFaturam").SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token); var retryAfter = response.Headers.RetryAfter?.Delta; var rate = new RateLimitMetadata(null, response.Headers.RetryAfter?.Date, retryAfter); var remoteId = response.Headers.TryGetValues("x-request-id", out var ids) ? ids.FirstOrDefault() : null;
            if (!response.IsSuccessStatusCode) return AdapterResult<string>.Failure(TrendyolEFaturamErrorMapper.FromStatus(response.StatusCode, retryAfter, remoteId), rate);
            if (!response.Headers.TryGetValues("x-access-token", out var tokens) || string.IsNullOrWhiteSpace(tokens.FirstOrDefault())) return AdapterResult<string>.Failure(TrendyolEFaturamErrorMapper.Contract(), rate);
            return AdapterResult<string>.Success(tokens.First(), rate);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return AdapterResult<string>.Failure(new(AdapterErrorClass.TransientNetwork, "EFATURAM_TIMEOUT", "E-Faturam bağlantı testi zaman aşımına uğradı.", null, TimeSpan.FromSeconds(5), null)); }
        catch (HttpRequestException) { return AdapterResult<string>.Failure(new(AdapterErrorClass.TransientNetwork, "EFATURAM_NETWORK_ERROR", "E-Faturam ağına güvenli bağlantı kurulamadı.", null, TimeSpan.FromSeconds(5), null)); }
    }

    private bool CanWrite(TrendyolEFaturamRequestContext context) => GlobalWritesEnabled && context.ExternalWritesEnabled && context.IntegrationModel is "API_USER" or "MARKETPLACE";

    private async Task<AdapterResult<AuthorizedResponse>> SendAuthorized(TrendyolEFaturamRequestContext context, string token, HttpMethod method, string endpoint, HttpContent? content, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, new Uri(context.BaseAddress, endpoint)) { Content = content };
        request.Headers.Accept.ParseAdd("application/json"); request.Headers.TryAddWithoutValidation("x-access-token", token);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); linked.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            using var response = await clients.CreateClient("TrendyolEFaturam").SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token);
            var retryAfter = response.Headers.RetryAfter?.Delta; var rate = new RateLimitMetadata(null, response.Headers.RetryAfter?.Date, retryAfter); var remoteId = response.Headers.TryGetValues("x-request-id", out var ids) ? ids.FirstOrDefault() : null;
            if (!response.IsSuccessStatusCode) return AdapterResult<AuthorizedResponse>.Failure(TrendyolEFaturamErrorMapper.FromStatus(response.StatusCode, retryAfter, remoteId), rate);
            return AdapterResult<AuthorizedResponse>.Success(new(await response.Content.ReadAsStringAsync(linked.Token), remoteId), rate);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return AdapterResult<AuthorizedResponse>.Failure(new(AdapterErrorClass.TransientNetwork, "EFATURAM_TIMEOUT", "E-Faturam isteği zaman aşımına uğradı.", null, TimeSpan.FromSeconds(5), null)); }
        catch (HttpRequestException) { return AdapterResult<AuthorizedResponse>.Failure(new(AdapterErrorClass.TransientNetwork, "EFATURAM_NETWORK_ERROR", "E-Faturam ağına güvenli bağlantı kurulamadı.", null, TimeSpan.FromSeconds(5), null)); }
    }

    private sealed record AuthorizedResponse(string Body, string? RemoteRequestId);
}
