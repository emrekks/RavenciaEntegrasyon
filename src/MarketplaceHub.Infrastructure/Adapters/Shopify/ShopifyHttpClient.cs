using System.Globalization;
using System.Net;
using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MarketplaceHub.Infrastructure.Adapters.Shopify;

public sealed partial class ShopifyHttpClient(
    IHttpClientFactory clients,
    ShopifyAuthenticationHandler authentication,
    IOptions<ShopifyOptions> options,
    TimeProvider timeProvider,
    ILogger<ShopifyHttpClient> logger,
    AppDbContext db,
    IConfiguration configuration)
    : IConnectionPort, IReferenceDataPort, IProductPort, IProductVisualLookupPort, IInventoryPricePort, IOrderPort, IReturnPort, IInvoiceMarketplacePort
{
    private readonly ShopifyOptions settings = options.Value;

    private async Task<bool> CanWriteAsync(ShopifyRequestContext shop, AdapterContext context, string capabilityCode, CancellationToken cancellationToken)
    {
        if (!ExternalWriteGate.GlobalWritesEnabled(configuration)) return false;
        if (!ConnectionExternalWritesEnabled(shop.Connection.SettingsJson)
            || !IntegrationRuntimePolicy.AllowsExternalWrite(shop.Connection, context, globalWritesEnabled: true, connectionWritesEnabled: true)) return false;
        return await HasVerifiedWriteCapabilityAsync(shop, context, capabilityCode, cancellationToken);
    }

    private async Task<bool> HasVerifiedWriteCapabilityAsync(ShopifyRequestContext shop, AdapterContext context, string capabilityCode, CancellationToken cancellationToken)
    {
        var capabilities = await db.PlatformCapabilities.AsNoTracking()
            .Where(item => item.TenantId == context.TenantId && item.ConnectionId == context.ConnectionId && item.Code == capabilityCode)
            .ToListAsync(cancellationToken);
        return capabilities.Count == 1 && CapabilityEvidencePolicy.IsVerifiedWriteCapability(capabilities[0], shop.Connection, capabilityCode);
    }

    private static bool ConnectionExternalWritesEnabled(string settingsJson) => ExternalWriteGate.ConnectionWritesEnabled(settingsJson);

    private async Task<bool> CanWriteAsync(ShopifyRequestContext shop, AdapterContext context, string capabilityOne, string capabilityTwo, CancellationToken cancellationToken) =>
        await CanWriteAsync(shop, context, capabilityOne, cancellationToken)
        && await CanWriteAsync(shop, context, capabilityTwo, cancellationToken);

    private static AdapterResult<T> WriteClosed<T>() => Fail<T>(AdapterErrorClass.NotSupported, "SHOPIFY_EXTERNAL_WRITE_CLOSED", "Shopify dış yazması kapalı, bağlantı etkin değil veya gerekli API izni doğrulanmadı.", HttpStatusCode.Forbidden);

    private static bool HasUserErrors(JsonElement payload, out string? message)
    {
        message = null;
        if (!payload.TryGetProperty("userErrors", out var errors) || errors.ValueKind != JsonValueKind.Array || errors.GetArrayLength() == 0) return false;
        message = string.Join("; ", errors.EnumerateArray().Select(error => TextValue(error, "message")).Where(value => !string.IsNullOrWhiteSpace(value)));
        return true;
    }

    private static string? NestedText(JsonElement element, string objectName, string propertyName) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(objectName, out var nested) && nested.ValueKind == JsonValueKind.Object
            ? TextValue(nested, propertyName)
            : null;
    private static string? TextValue(JsonElement element, string property) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    // Shopify's GraphQL Money and Decimal scalars arrive as JSON strings ("2099.0"); Int fields (quantities) as numbers. A
    // number read only as a JSON number left every price, total, discount and refund of an API read at 0 or unknown.
    internal static decimal? DecimalOrNull(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) ? DecimalValue(value) : null;

    // A plain invariant decimal: digits, one point, an optional sign. A thousands separator or an exponent is not
    // a Shopify amount and stays unknown rather than being read as another number.
    internal static decimal? DecimalValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Number when value.TryGetDecimal(out var number) => number,
        JsonValueKind.String when decimal.TryParse(value.GetString()?.Trim(), NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parsed) => parsed,
        _ => null
    };

    private static string ShortId(string? value) => string.IsNullOrWhiteSpace(value) ? "" : value.Split('/').Last();
    // No request context: a token request that failed on the network or timed out is retried as such; otherwise
    // the credentials are missing or rejected.
    private AdapterResult<T> CredentialFailure<T>(AdapterResult<T> credentialInvalid) =>
        authentication.TokenRequestFailure is { } transient ? Failure<T>(transient) : credentialInvalid;
    private static AdapterResult<T> Fail<T>(AdapterErrorClass kind, string code, string message, HttpStatusCode status) => AdapterResult<T>.Failure(new(kind, code, message, (int)status, null, null));
    private static Task<AdapterResult<T>> Unsupported<T>(string message) => Task.FromResult(Fail<T>(AdapterErrorClass.NotSupported, "SHOPIFY_NOT_SUPPORTED", message, HttpStatusCode.NotImplemented));
    private static AdapterResult<T> Failure<T>(AdapterError error, RateLimitMetadata? rate = null) => AdapterResult<T>.Failure(error, rate);
}

