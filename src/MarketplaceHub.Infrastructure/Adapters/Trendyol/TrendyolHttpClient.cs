using System.Globalization;
using MarketplaceHub.Application;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MarketplaceHub.Infrastructure.Adapters.Trendyol;

public sealed partial class TrendyolHttpClient(IHttpClientFactory clients, TrendyolAuthenticationHandler authentication, IConfiguration configuration, IOptions<TrendyolOptions> trendyolOptions, TimeProvider timeProvider, ILogger<TrendyolHttpClient> logger)
    : IConnectionPort, IReferenceDataPort, IProductPort, IProductVisualLookupPort, IInventoryPricePort, IOrderPort, IReturnPort, IReturnBatchReadPort, IReturnAuditPort, IInvoiceMarketplacePort
{
    private bool GlobalWritesEnabled => ExternalWriteGate.GlobalWritesEnabled(configuration);

    private bool CanWrite(TrendyolRequestContext context, AdapterContext adapterContext) =>
        IntegrationRuntimePolicy.AllowsExternalWrite(context.Connection, adapterContext, GlobalWritesEnabled, context.ExternalWritesEnabled);
    private static int Page(string? value) => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var page) && page >= 0 ? page : 0;

    // A known order is read from its own storefront; an unknown one from the documented storefronts and any
    // undocumented one the connection already has orders from (C28).
    private async Task<IReadOnlyList<string>> LookupStorefrontsAsync(TrendyolRequestContext authorized, OrderLookupHint? hint, CancellationToken cancellationToken) =>
        hint?.StoreFrontCode is { Length: > 0 }
            ? TrendyolLookupPolicy.Storefronts(hint, [])
            : await authentication.ReadStorefrontsAsync(authorized.Connection.TenantId, authorized.Connection.Id, cancellationToken);

    // The stored storefront of the package's order; TR only when it is not known (older rows, non-Trendyol callers).
    internal static string WriteStoreFront(string? storeFrontCode) =>
        string.IsNullOrWhiteSpace(storeFrontCode) ? TrendyolLookupPolicy.HomeStoreFront : storeFrontCode.Trim().ToUpperInvariant();
}
