using System.Net.Http.Json;
using System.Text.Json;
using MarketplaceHub.Application;

namespace MarketplaceHub.Infrastructure.Adapters.Shopify;

public sealed class ShopifyGraphQlClient(IHttpClientFactory clients, ShopifyAuthenticationHandler authentication, TimeProvider timeProvider)
    : IConnectionPort, IProductPort, IInventoryPricePort, IOrderPort
{
    private const string TestQuery = "query ConnectionTest { shop { id myshopifyDomain } }";

    public async Task<AdapterResult<ConnectionIdentity>> TestAsync(AdapterContext context, CancellationToken cancellationToken)
    {
        var auth = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken);
        if (auth is null) return AdapterResult<ConnectionIdentity>.Failure(ShopifyFailures.Configuration());
        using var request = new HttpRequestMessage(HttpMethod.Post, auth.GraphQlEndpoint) { Content = JsonContent.Create(new { query = TestQuery }) };
        request.Headers.TryAddWithoutValidation("X-Shopify-Access-Token", auth.AccessToken);
        try
        {
            using var response = await clients.CreateClient("Shopify").SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            var requestId = response.Headers.TryGetValues("x-request-id", out var ids) ? ids.FirstOrDefault() : null;
            if (!response.IsSuccessStatusCode) return AdapterResult<ConnectionIdentity>.Failure(new(AdapterErrorClass.Authentication, "SHOPIFY_CONNECTION_FAILED", "Shopify bağlantı testi başarısız.", (int)response.StatusCode, null, requestId));
            if (!response.Headers.TryGetValues("X-Shopify-API-Version", out var versions) || versions.FirstOrDefault() != ShopifyContract.ApiVersion)
                return AdapterResult<ConnectionIdentity>.Failure(new(AdapterErrorClass.ContractViolation, "SHOPIFY_VERSION_MISMATCH", "Shopify yanıtındaki API sürümü pinlenen sürümle eşleşmedi.", 502, null, requestId));
            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            if (ShopifyGraphQlContract.Errors(json).Count > 0) return AdapterResult<ConnectionIdentity>.Failure(new(AdapterErrorClass.ContractViolation, "SHOPIFY_GRAPHQL_ERROR", "Shopify GraphQL yanıtı hata içeriyor.", 422, null, requestId));
            using var document = JsonDocument.Parse(json);
            var returnedDomain = document.RootElement.GetProperty("data").GetProperty("shop").GetProperty("myshopifyDomain").GetString();
            if (!string.Equals(returnedDomain, auth.ShopDomain, StringComparison.OrdinalIgnoreCase)) return AdapterResult<ConnectionIdentity>.Failure(ShopifyFailures.Configuration("Shopify yanıtındaki mağaza alanı bağlantı kapsamıyla eşleşmedi."));
            return AdapterResult<ConnectionIdentity>.Success(new(ShopifyContract.PlatformCode, auth.Environment, auth.ShopDomain, ShopifyContract.ApiVersion, auth.ShopDomain));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return AdapterResult<ConnectionIdentity>.Failure(new(AdapterErrorClass.TransientNetwork, "REMOTE_TIMEOUT", "Shopify isteği zaman aşımına uğradı.", null, TimeSpan.FromSeconds(5), null)); }
        catch (HttpRequestException) { return AdapterResult<ConnectionIdentity>.Failure(new(AdapterErrorClass.TransientNetwork, "REMOTE_NETWORK_ERROR", "Shopify ağına güvenli bağlantı kurulamadı.", null, TimeSpan.FromSeconds(5), null)); }
        catch (JsonException) { return AdapterResult<ConnectionIdentity>.Failure(new(AdapterErrorClass.ContractViolation, "SHOPIFY_CONTRACT_INVALID", "Shopify yanıt sözleşmesi doğrulanamadı.", 502, null, null)); }
    }

    public async Task<AdapterResult<IReadOnlyList<CapabilityEvidence>>> DiscoverCapabilitiesAsync(AdapterContext context, CancellationToken cancellationToken)
    {
        var test = await TestAsync(context, cancellationToken);
        if (!test.IsSuccess) return AdapterResult<IReadOnlyList<CapabilityEvidence>>.Failure(test.Error!);
        var identity = test.Value!;
        IReadOnlyList<CapabilityEvidence> evidence = [new(F3Capabilities.ConnectionTest, "SUPPORTED", ShopifyContract.ApiVersion, identity.Environment, identity.ExternalStoreId, ShopifyContract.VersionSource, ShopifyContract.ApiVersion, null, null, "Admin GraphQL bağlantısı ve dönen API sürümü doğrulandı; diğer yetenekler development-store fixture kanıtına kadar UNKNOWN kalır.", null, timeProvider.GetUtcNow())];
        return AdapterResult<IReadOnlyList<CapabilityEvidence>>.Success(evidence);
    }

    public Task<AdapterResult<AdapterPageResult<RemoteProduct>>> ListAsync(AdapterContext context, AdapterPageRequest page, ProductReadFilter filter, CancellationToken cancellationToken) => Closed<AdapterPageResult<RemoteProduct>>("Ürün read scope ve development-store fixture kanıtı bekleniyor.");
    public Task<AdapterResult<RemoteOperationRef>> UpsertAsync(AdapterContext context, ProductPublication publication, CancellationToken cancellationToken) => WriteClosed<RemoteOperationRef>();
    public Task<AdapterResult<RemoteOperationStatus>> GetOperationAsync(AdapterContext context, string externalOperationId, CancellationToken cancellationToken) => Closed<RemoteOperationStatus>("Bulk operation fixture kanıtı bekleniyor.");
    public Task<AdapterResult<bool>> ArchiveAsync(AdapterContext context, ExternalProductIdentity identity, CancellationToken cancellationToken) => WriteClosed<bool>();
    public Task<AdapterResult<BatchResult<BatchLineResult>>> PushStockAsync(AdapterContext context, IReadOnlyList<StockPushLine> lines, CancellationToken cancellationToken) => WriteClosed<BatchResult<BatchLineResult>>();
    public Task<AdapterResult<BatchResult<BatchLineResult>>> PushPricesAsync(AdapterContext context, IReadOnlyList<PricePushLine> lines, CancellationToken cancellationToken) => WriteClosed<BatchResult<BatchLineResult>>();
    public Task<AdapterResult<AdapterPageResult<RemoteOrder>>> PollAsync(AdapterContext context, OrderPollWindow window, AdapterPageRequest page, CancellationToken cancellationToken) => Closed<AdapterPageResult<RemoteOrder>>("Order read scope ve development-store fixture kanıtı bekleniyor.");
    public Task<AdapterResult<RemoteOrder>> GetAsync(AdapterContext context, string externalOrderId, CancellationToken cancellationToken) => Closed<RemoteOrder>("Order read scope ve development-store fixture kanıtı bekleniyor.");
    public Task<AdapterResult<PackageActionResult>> ExecutePackageActionAsync(AdapterContext context, PackageActionCommand command, CancellationToken cancellationToken) => WriteClosed<PackageActionResult>();

    private static Task<AdapterResult<T>> Closed<T>(string message) => Task.FromResult(AdapterResult<T>.Failure(ShopifyFailures.Closed(message)));
    private static Task<AdapterResult<T>> WriteClosed<T>() => Task.FromResult(AdapterResult<T>.Failure(ShopifyFailures.WriteClosed()));
}
