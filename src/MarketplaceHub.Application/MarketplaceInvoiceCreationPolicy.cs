using System.Text.Json;

namespace MarketplaceHub.Application;

public static class MarketplaceInvoiceCreationPolicy
{
    public const string DisabledErrorCode = "INVOICE_CREATION_DISABLED";
    public const string DisabledMessage = "Fatura oluşturma bu bağlantı ayarlarında kapalı. Entegrasyonlar bölümünden açın.";

    // The per-connection "Faturalama" switch. Every marketplace connection carries it and the seller turns it on and off.
    public static string? UpdateRefusal(string? platformCode, bool? requested)
    {
        if (requested is null) return null;
        return ActiveIntegrationScope.IsMarketplace(platformCode) ? null : "Fatura oluşturma ayarı yalnız pazaryeri bağlantılarında kullanılabilir.";
    }

    public static bool IsEnabled(string? platformCode, string? settingsJson)
    {
        // N11 (SaveLinkSellerInvoice) and Pazarama (order/invoice-link) take an invoice link per order;
        // their connections store InvoiceCreationEnabled=false by default, so fiscal submission starts
        // only after the seller enables it on the connection. Shopify was locked off until 30 October 2026: a
        // Shopify connection invoices only once the seller has turned the switch on (no stored value reads as off).
        var shopify = string.Equals(platformCode, "SHOPIFY", StringComparison.OrdinalIgnoreCase);
        if (!ActiveIntegrationScope.IsMarketplace(platformCode)) return true;
        if (string.IsNullOrWhiteSpace(settingsJson)) return !shopify;

        try
        {
            using var document = JsonDocument.Parse(settingsJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return false;
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (string.Equals(property.Name, "InvoiceCreationEnabled", StringComparison.OrdinalIgnoreCase))
                    return property.Value.ValueKind == JsonValueKind.True;
            }
            return !shopify;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
