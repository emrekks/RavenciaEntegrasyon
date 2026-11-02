using System.Text.Json;

namespace MarketplaceHub.Application;

/// <summary>
/// When an issued invoice goes to its order's marketplace by itself (user decision, 2 Nov 2026: what Hepsiburada does,
/// every marketplace with an invoice delivery API does). The gate is only what really decides a write: an adapter for
/// the platform, a live (ACTIVE, PRODUCTION) connection, the server's write switches, the connection's own write switch
/// and a ready document link. A hand-kept capability record does not gate it: the adapters read the package before and
/// check the marketplace's answer after the write. When the invoice is not sent, the reason is a code on the invoice.
/// </summary>
public static class InvoiceMarketplaceAutoDeliveryPolicy
{
    /// <summary>The platforms with an invoice delivery adapter (IInvoiceMarketplacePort).</summary>
    public static readonly string[] DeliveryPlatformCodes = ["TRENDYOL", "HEPSIBURADA", "SHOPIFY", "N11", "PAZARAMA", "PTTAVM"];

    public const string PlatformUnsupportedCode = "INVOICE_AUTO_DELIVERY_PLATFORM_UNSUPPORTED";
    public const string LinkMissingCode = "INVOICE_AUTO_DELIVERY_LINK_MISSING";
    public const string WritesOffCode = "INVOICE_AUTO_DELIVERY_WRITES_OFF";
    public const string ConnectionNotLiveCode = "INVOICE_AUTO_DELIVERY_CONNECTION_NOT_LIVE";
    public const string ConnectionWritesOffCode = "INVOICE_AUTO_DELIVERY_CONNECTION_WRITES_OFF";
    public const string PackageMissingCode = "INVOICE_AUTO_DELIVERY_PACKAGE_MISSING";
    public const string ShopifyScopeMissingCode = "INVOICE_AUTO_DELIVERY_SHOPIFY_SCOPE_MISSING";

    /// <summary>
    /// The scheduled recovery sends an issued invoice that never got a delivery job only within this many days of its
    /// issue, so an old invoice is never sent by surprise; older ones go with the "Pazaryerine Fatura Linkini İlet" button.
    /// </summary>
    public static readonly TimeSpan NeverQueuedRecoveryWindow = TimeSpan.FromDays(14);

    public static bool IsDeliveryPlatform(string? platformCode) =>
        platformCode is not null && DeliveryPlatformCodes.Contains(platformCode.Trim().ToUpperInvariant(), StringComparer.Ordinal);

    public static bool IsSkipCode(string? code) => code?.StartsWith("INVOICE_AUTO_DELIVERY_", StringComparison.Ordinal) == true;

    /// <summary>
    /// Null when the invoice may be queued for delivery; otherwise why it is not sent. Shopify's write scope is read from
    /// the connection test (the store's granted scopes), which its adapter also requires.
    /// </summary>
    public static string? SkipReason(
        string? platformCode,
        string? connectionStatus,
        string? connectionEnvironment,
        string? connectionSettingsJson,
        bool globalWritesEnabled,
        bool invoiceDeliveryWritesEnabled,
        bool documentLinkReady,
        bool shopifyWriteScopeVerified = false)
    {
        if (!IsDeliveryPlatform(platformCode)) return PlatformUnsupportedCode;
        if (!documentLinkReady) return LinkMissingCode;
        if (!globalWritesEnabled || !invoiceDeliveryWritesEnabled) return WritesOffCode;
        if (!string.Equals(connectionStatus, "ACTIVE", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(connectionEnvironment, "PRODUCTION", StringComparison.OrdinalIgnoreCase)) return ConnectionNotLiveCode;
        if (!ConnectionWritesEnabled(connectionSettingsJson)) return ConnectionWritesOffCode;
        if (string.Equals(platformCode?.Trim(), "SHOPIFY", StringComparison.OrdinalIgnoreCase) && !shopifyWriteScopeVerified) return ShopifyScopeMissingCode;
        return null;
    }

    public static bool ConnectionWritesEnabled(string? settingsJson)
    {
        if (string.IsNullOrWhiteSpace(settingsJson)) return false;
        try
        {
            using var document = JsonDocument.Parse(settingsJson);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("ExternalWritesEnabled", out var value) && value.ValueKind == JsonValueKind.True;
        }
        catch (JsonException) { return false; }
    }
}
