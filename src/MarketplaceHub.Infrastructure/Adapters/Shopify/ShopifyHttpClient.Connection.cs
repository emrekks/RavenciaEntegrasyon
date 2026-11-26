using System.Net;
using System.Text.Json;
using MarketplaceHub.Application;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Adapters.Shopify;

public sealed partial class ShopifyHttpClient
{
    public async Task<AdapterResult<ConnectionIdentity>> TestAsync(AdapterContext context, CancellationToken cancellationToken)
    {
        var shop = await authentication.LoadAsync(context.TenantId, context.ConnectionId, settings.ApiVersion, cancellationToken);
        if (shop is null) return CredentialFailure(Fail<ConnectionIdentity>(AdapterErrorClass.Authentication, "SHOPIFY_CREDENTIAL_INVALID", "Shopify mağaza bilgileri veya yetkilendirmesi geçersiz.", HttpStatusCode.Unauthorized));
        var result = await QueryAsync(shop, "query { shop { name primaryDomain { host } currencyCode } locations(first: 50) { nodes { id name isActive } } }", cancellationToken: cancellationToken);
        if (!result.IsSuccess) return AdapterResult<ConnectionIdentity>.Failure(result.Error!, result.RateLimit);
        try
        {
            var root = result.Value;
            if (!root.TryGetProperty("shop", out var shopElement) || shopElement.ValueKind != JsonValueKind.Object)
                return Fail<ConnectionIdentity>(AdapterErrorClass.ContractViolation, "SHOPIFY_CONNECTION_CONTRACT_INVALID", "Shopify mağaza doğrulama yanıtı mağaza bilgisini içermiyor.", HttpStatusCode.BadGateway);
            var host = shopElement.TryGetProperty("primaryDomain", out var primaryDomain)
                && primaryDomain.ValueKind == JsonValueKind.Object
                && primaryDomain.TryGetProperty("host", out var hostElement)
                && hostElement.ValueKind == JsonValueKind.String
                ? hostElement.GetString()
                : null;
            host = string.IsNullOrWhiteSpace(host) ? $"{shop.Shop}.myshopify.com" : host;
            var locationCount = root.TryGetProperty("locations", out var locations)
                && locations.ValueKind == JsonValueKind.Object
                && locations.TryGetProperty("nodes", out var nodes)
                && nodes.ValueKind == JsonValueKind.Array
                ? nodes.GetArrayLength()
                : 0;
            return AdapterResult<ConnectionIdentity>.Success(new("SHOPIFY", shop.Connection.Environment, shop.Shop, shop.ApiVersion, $"{host}:{locationCount}"), result.RateLimit);
        }
        catch (Exception exception) when (exception is KeyNotFoundException or InvalidOperationException or JsonException)
        {
            return Fail<ConnectionIdentity>(AdapterErrorClass.ContractViolation, "SHOPIFY_CONNECTION_CONTRACT_INVALID", "Shopify mağaza doğrulama yanıtı beklenen alanları içermiyor.", HttpStatusCode.BadGateway);
        }
    }

    public async Task<AdapterResult<IReadOnlyList<CapabilityEvidence>>> DiscoverCapabilitiesAsync(AdapterContext context, CancellationToken cancellationToken)
    {
        var test = await TestAsync(context, cancellationToken);
        if (!test.IsSuccess) return AdapterResult<IReadOnlyList<CapabilityEvidence>>.Failure(test.Error!, test.RateLimit);
        var identity = test.Value!;
        var now = timeProvider.GetUtcNow();
        var products = await ListCatalogAsync(context, new(null, 1), new(null), cancellationToken);
        var orders = await PollAsync(context, new OrderPollWindow(null, now, null), new(null, 1), cancellationToken);
        var categories = await ReadAsync(context, new("CATEGORIES", null), new(null, 1), cancellationToken);
        var scopesContext = await authentication.LoadAsync(context.TenantId, context.ConnectionId, settings.ApiVersion, cancellationToken);
        if (scopesContext is null) return CredentialFailure(AdapterResult<IReadOnlyList<CapabilityEvidence>>.Failure(new(AdapterErrorClass.Authentication, "SHOPIFY_CREDENTIAL_INVALID", "Shopify yetkilendirmesi bulunamadı.", 401, null, null)));
        var scopesResult = await QueryAsync(scopesContext, "query { currentAppInstallation { accessScopes { handle } } }", cancellationToken: cancellationToken);
        var scopes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (scopesResult.IsSuccess && scopesResult.Value.TryGetProperty("currentAppInstallation", out var installation)
            && installation.ValueKind == JsonValueKind.Object && installation.TryGetProperty("accessScopes", out var accessScopes)
            && accessScopes.ValueKind == JsonValueKind.Array)
            foreach (var scope in accessScopes.EnumerateArray())
                if (scope.TryGetProperty("handle", out var handle) && handle.ValueKind == JsonValueKind.String && handle.GetString() is { Length: > 0 } value) scopes.Add(value);
        var evidence = new List<CapabilityEvidence>
        {
            Supported(MarketplaceCapabilities.ConnectionTest, identity, "https://shopify.dev/docs/api/admin-graphql", "Shopify mağaza, uygulama tokenı, ürün/sipariş ve müşteri okuma izinleri, para birimi ve depo bilgileri doğrulandı.", now, "read_products,read_inventory,read_orders,read_customers,read_locations")
        };
        evidence.Add(Probe(MarketplaceCapabilities.ProductRead, identity, "https://shopify.dev/docs/api/admin-graphql/latest/objects/Product", products, "GraphQL ürün ve varyant okuması", now, "read_products,read_inventory"));
        evidence.Add(Probe(MarketplaceCapabilities.OrderRead, identity, "https://shopify.dev/docs/api/admin-graphql/latest/objects/Order", orders, "GraphQL sipariş, müşteri ve teslimat durumu okuması", now, "read_orders,read_customers"));
        evidence.Add(Probe(MarketplaceCapabilities.ReferenceRead, identity, "https://shopify.dev/docs/api/admin-graphql/2026-10/queries/productTypes", categories, "GraphQL mağaza ürün türleri (productTypes) okuması", now, "read_products"));
        evidence.Add(ScopeEvidence(MarketplaceCapabilities.PriceWrite, identity, "https://shopify.dev/docs/api/admin-graphql/2026-10/mutations/productVariantsBulkUpdate", scopesResult, scopes, ["write_products"], now));
        evidence.Add(ScopeEvidence(MarketplaceCapabilities.ProductWrite, identity, "https://shopify.dev/docs/api/admin-graphql/2026-10/mutations/productSet", scopesResult, scopes, ["write_products"], now));
        evidence.Add(ScopeEvidence(MarketplaceCapabilities.InventoryWrite, identity, "https://shopify.dev/docs/api/admin-graphql/2026-10/mutations/inventorySetQuantities", scopesResult, scopes, ["write_inventory"], now));
        evidence.Add(ScopeEvidence(MarketplaceCapabilities.PublicationRead, identity, "https://shopify.dev/docs/api/admin-graphql/2026-10/queries/publications", scopesResult, scopes, ["read_publications"], now));
        evidence.Add(ScopeEvidence(MarketplaceCapabilities.PublicationWrite, identity, "https://shopify.dev/docs/api/admin-graphql/2026-10/mutations/publishablePublish", scopesResult, scopes, ["write_publications"], now));
        evidence.Add(ScopeEvidence(MarketplaceCapabilities.ShipmentWrite, identity, "https://shopify.dev/docs/api/admin-graphql/2026-10/objects/Fulfillment", scopesResult, scopes, ["write_assigned_fulfillment_orders", "write_merchant_managed_fulfillment_orders", "write_third_party_fulfillment_orders"], now));
        evidence.Add(ScopeEvidence(MarketplaceCapabilities.ReturnRead, identity, "https://shopify.dev/docs/api/admin-graphql/2026-10/objects/Return", scopesResult, scopes, ["read_returns", "read_marketplace_returns"], now));
        evidence.Add(ScopeEvidence(MarketplaceCapabilities.ReturnWrite, identity, "https://shopify.dev/docs/api/admin-graphql/2026-10/mutations/returnApproveRequest", scopesResult, scopes, ["write_returns", "write_marketplace_returns"], now));
        evidence.Add(ScopeEvidence(InvoicingCapabilities.InvoiceDeliver, identity, "https://shopify.dev/docs/api/admin-graphql/latest/mutations/metafieldsSet", scopesResult, scopes, ["write_orders"], now));
        evidence.Add(new(MarketplaceCapabilities.LabelWrite, "NOT_SUPPORTED", identity.ApiVersion, identity.Environment, identity.ExternalStoreId, "https://shopify.dev/docs/api/admin-graphql/2026-10/objects/Fulfillment", identity.ApiVersion, null, null, "Shopify Admin API sipariş takibi sunar; taşıyıcı etiketi satın alma/oluşturma bu API kapsamına dahil değildir.", null, now));
        return AdapterResult<IReadOnlyList<CapabilityEvidence>>.Success(evidence, products.RateLimit ?? orders.RateLimit ?? categories.RateLimit);
    }

    private static CapabilityEvidence ScopeEvidence(string code, ConnectionIdentity identity, string source, AdapterResult<JsonElement> scopeResult, IReadOnlySet<string> scopes, string[] requiredScopes, DateTimeOffset verified)
    {
        if (!scopeResult.IsSuccess)
            return new(code, "UNKNOWN", identity.ApiVersion, identity.Environment, identity.ExternalStoreId, source, identity.ApiVersion, string.Join(",", requiredScopes), null, "Shopify uygulama scope listesi doğrulanamadı.", null, verified);
        var granted = requiredScopes.Where(scopes.Contains).ToArray();
        var support = granted.Length > 0 ? "SUPPORTED" : "NOT_SUPPORTED";
        return new(code, support, identity.ApiVersion, identity.Environment, identity.ExternalStoreId, source, identity.ApiVersion, string.Join(",", requiredScopes), null,
            granted.Length > 0 ? $"Shopify uygulamasında {string.Join(", ", granted)} izni var." : $"Shopify uygulamasında gerekli izinlerden biri yok: {string.Join(", ", requiredScopes)}.", null, verified);
    }
    private static CapabilityEvidence Supported(string code, ConnectionIdentity identity, string source, string note, DateTimeOffset verified, string? scope = null) => new(code, "SUPPORTED", identity.ApiVersion, identity.Environment, identity.ExternalStoreId, source, identity.ApiVersion, scope, null, note, null, verified);
    private static CapabilityEvidence Probe<T>(string code, ConnectionIdentity identity, string source, AdapterResult<T> result, string operation, DateTimeOffset verified, string scope) => result.IsSuccess
        ? Supported(code, identity, source, $"{operation} başarılı.", verified, scope)
        : new(code, "UNKNOWN", identity.ApiVersion, identity.Environment, identity.ExternalStoreId, source, identity.ApiVersion, scope, null, $"{operation} doğrulanamadı: {result.Error?.SafeMessage ?? "Shopify yanıtı alınamadı."}", null, verified);
}
