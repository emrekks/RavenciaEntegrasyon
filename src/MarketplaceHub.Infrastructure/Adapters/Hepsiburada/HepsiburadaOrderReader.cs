using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MarketplaceHub.Application;
using Microsoft.Extensions.Options;

namespace MarketplaceHub.Infrastructure.Adapters.Hepsiburada;

/// <summary>Only performs documented SIT order-list reads. It never creates, changes, or packages an order.</summary>
public sealed class HepsiburadaOrderReader(IHttpClientFactory clients, HepsiburadaAuthenticationHandler authentication, IOptions<HepsiburadaOptions> options, TimeProvider timeProvider)
{
    public async Task<AdapterResult<HepsiburadaOrderReadEvidence>> ProbeAsync(AdapterContext context, CancellationToken cancellationToken)
    {
        var result = await ReadAsync(context, 0, 1, cancellationToken);
        if (!result.IsSuccess) return AdapterResult<HepsiburadaOrderReadEvidence>.Failure(result.Error!, result.RateLimit);
        try
        {
            var page = HepsiburadaOrderJsonMapper.Orders(result.Value!.Json);
            if (page.Items.Count == 0)
                return AdapterResult<HepsiburadaOrderReadEvidence>.Failure(new(AdapterErrorClass.NotSupported, "HEPSIBURADA_ORDER_FIXTURE_EMPTY", "Hepsiburada SIT sipariş yanıtı boş; alan eşlemesi için dolu test siparişi bekleniyor.", 422, null, null), result.RateLimit);
            return AdapterResult<HepsiburadaOrderReadEvidence>.Success(new(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(result.Value.Json))).ToLowerInvariant(), page.Items.Count, timeProvider.GetUtcNow()), result.RateLimit);
        }
        catch (JsonException)
        {
            return AdapterResult<HepsiburadaOrderReadEvidence>.Failure(new(AdapterErrorClass.ContractViolation, "HEPSIBURADA_ORDER_CONTRACT_VIOLATION", "Hepsiburada SIT sipariş yanıtı doğrulanmış sipariş alanlarıyla eşleşmedi.", 422, null, null), result.RateLimit);
        }
    }

    public async Task<AdapterResult<HepsiburadaOrderReadResponse>> ReadAsync(AdapterContext context, int offset, int limit, CancellationToken cancellationToken)
    {
        var authorized = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken);
        if (authorized is null) return AdapterResult<HepsiburadaOrderReadResponse>.Failure(new(AdapterErrorClass.Authentication, "HEPSIBURADA_CONFIGURATION_UNAVAILABLE", "Hepsiburada SIT bağlantısı veya şifreli credential kullanılamıyor.", 422, null, null));

        var safeOffset = Math.Max(0, offset);
        var safeLimit = Math.Clamp(limit, 1, 50);
        var relative = $"orders/merchantid/{Uri.EscapeDataString(authorized.Connection.ExternalStoreId)}?offset={safeOffset}&limit={safeLimit}";
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(authorized.BaseAddress, relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{authorized.Username}:{authorized.Password}")));
        request.Headers.TryAddWithoutValidation("User-Agent", authorized.UserAgentIdentity);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(options.Value.Timeout);

        try
        {
            using var response = await clients.CreateClient("Hepsiburada").SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token);
            var retryAfter = response.Headers.RetryAfter?.Delta;
            var requestId = response.Headers.TryGetValues("x-request-id", out var values) ? values.FirstOrDefault() : null;
            var rate = new RateLimitMetadata(response.Headers.TryGetValues("X-RateLimit-Remaining", out var remaining) && int.TryParse(remaining.FirstOrDefault(), out var remainingValue) ? remainingValue : null, null, retryAfter);
            if (!response.IsSuccessStatusCode) return AdapterResult<HepsiburadaOrderReadResponse>.Failure(HepsiburadaErrorClassifier.FromHttpStatus((int)response.StatusCode, retryAfter, requestId), rate);
            var json = await response.Content.ReadAsStringAsync(linked.Token);
            if (Encoding.UTF8.GetByteCount(json) is 0 or > 1_048_576 || !HepsiburadaSitEnvelope.TryValidate(Encoding.UTF8.GetBytes(json), out _))
                return AdapterResult<HepsiburadaOrderReadResponse>.Failure(new(AdapterErrorClass.ContractViolation, "HEPSIBURADA_CONTRACT_VIOLATION", "Hepsiburada SIT bağlantı yanıtı doğrulanmış anonim zarfla eşleşmedi.", (int)response.StatusCode, null, requestId), rate);
            return AdapterResult<HepsiburadaOrderReadResponse>.Success(new(json), rate);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return AdapterResult<HepsiburadaOrderReadResponse>.Failure(HepsiburadaErrorClassifier.Timeout()); }
        catch (HttpRequestException) { return AdapterResult<HepsiburadaOrderReadResponse>.Failure(new(AdapterErrorClass.TransientNetwork, "HEPSIBURADA_NETWORK_ERROR", "Hepsiburada SIT bağlantısına ulaşılamadı.", null, TimeSpan.FromSeconds(5), null)); }
    }
}

public sealed record HepsiburadaOrderReadResponse(string Json);
public sealed record HepsiburadaOrderReadEvidence(string ResponseSha256, int OrderCount, DateTimeOffset VerifiedAt);
