using System.Net.Http.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Infrastructure.Adapters.TrendyolEFaturam.ErrorMapping;

namespace MarketplaceHub.Infrastructure.Adapters.TrendyolEFaturam;

public sealed class TrendyolEFaturamHttpClient(IHttpClientFactory clients, TrendyolEFaturamAuthenticationHandler authentication) : IInvoiceProviderPort
{
    public async Task<AdapterResult<ConnectionIdentity>> TestConnectionAsync(AdapterContext context, CancellationToken cancellationToken)
    {
        var configured = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken); if (configured is null) return AdapterResult<ConnectionIdentity>.Failure(TrendyolEFaturamErrorMapper.Configuration());
        var login = await SignIn(configured, cancellationToken); if (!login.IsSuccess) return AdapterResult<ConnectionIdentity>.Failure(login.Error!, login.RateLimit);
        return AdapterResult<ConnectionIdentity>.Success(new("TRENDYOL_EFATURAM", configured.Connection.Environment, configured.Connection.ExternalStoreId, "1.0.0", configured.Connection.ExternalStoreId), login.RateLimit);
    }

    public Task<AdapterResult<TaxpayerResult>> QueryTaxpayerAsync(AdapterContext context, TaxpayerQuery query, CancellationToken cancellationToken) =>
        Task.FromResult(AdapterResult<TaxpayerResult>.Failure(TrendyolEFaturamErrorMapper.Unsupported("API kullanıcısı/pazaryeri entegratörü modeli ve partner scope test firma ile doğrulanmadan taxpayer HTTP çağrısı yapılmaz.")));
    public Task<AdapterResult<InvoiceSubmissionResult>> SubmitAsync(AdapterContext context, InvoiceSubmission submission, CancellationToken cancellationToken) =>
        Task.FromResult(AdapterResult<InvoiceSubmissionResult>.Failure(TrendyolEFaturamErrorMapper.Unsupported("Invoice submit capability test firma safe-write kanıtı bekliyor.")));
    public Task<AdapterResult<InvoiceRemoteStatus>> QueryStatusAsync(AdapterContext context, ExternalInvoiceReference reference, CancellationToken cancellationToken) =>
        Task.FromResult(AdapterResult<InvoiceRemoteStatus>.Failure(TrendyolEFaturamErrorMapper.Unsupported("E-Fatura/E-Arşiv status query ayrımı test firma kanıtı bekliyor.")));
    public Task<AdapterResult<RemoteInvoiceDocument>> GetDocumentAsync(AdapterContext context, ExternalInvoiceReference reference, string documentKind, CancellationToken cancellationToken) =>
        Task.FromResult(AdapterResult<RemoteInvoiceDocument>.Failure(TrendyolEFaturamErrorMapper.Unsupported("Document download response ve content contract test firma kanıtı bekliyor.")));
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
            return AdapterResult<string>.Success("TOKEN_VERIFIED", rate);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return AdapterResult<string>.Failure(new(AdapterErrorClass.TransientNetwork, "EFATURAM_TIMEOUT", "E-Faturam bağlantı testi zaman aşımına uğradı.", null, TimeSpan.FromSeconds(5), null)); }
        catch (HttpRequestException) { return AdapterResult<string>.Failure(new(AdapterErrorClass.TransientNetwork, "EFATURAM_NETWORK_ERROR", "E-Faturam ağına güvenli bağlantı kurulamadı.", null, TimeSpan.FromSeconds(5), null)); }
    }
}
