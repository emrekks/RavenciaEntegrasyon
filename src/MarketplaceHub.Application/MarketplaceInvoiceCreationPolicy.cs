using System.Text.Json;

namespace MarketplaceHub.Application;

public static class MarketplaceInvoiceCreationPolicy
{
    public const string DisabledErrorCode = "INVOICE_CREATION_DISABLED";
    public const string DisabledMessage = "Fatura oluşturma bu bağlantı ayarlarında kapalı. Entegrasyonlar bölümünden açın.";
    public const string UnsupportedFiscalProviderMessage = "Shopify için mali fatura sağlayıcısı sözleşmesi henüz doğrulanmadı; güvenli biçimde fatura oluşturma kapalı.";

    // The per-connection "Faturalama" switch. Every marketplace connection carries it; Shopify's stays off because
    // its fiscal provider contract is not verified, so a request to turn it on is refused instead of being stored
    // as a value the invoicing flow would ignore.
    public static string? UpdateRefusal(string? platformCode, bool? requested)
    {
        if (requested is null) return null;
        if (!ActiveIntegrationScope.IsMarketplace(platformCode)) return "Fatura oluşturma ayarı yalnız pazaryeri bağlantılarında kullanılabilir.";
        return requested == true && string.Equals(platformCode, "SHOPIFY", StringComparison.OrdinalIgnoreCase) ? UnsupportedFiscalProviderMessage : null;
    }

    public static bool IsEnabled(string? platformCode, string? settingsJson)
    {
        // Shopify fiscal submission is intentionally fail-closed until the
        // configured provider contract for Shopify orders is verified.
        if (string.Equals(platformCode, "SHOPIFY", StringComparison.OrdinalIgnoreCase)) return false;
        // N11 (SaveLinkSellerInvoice) and Pazarama (order/invoice-link) take an invoice link per order;
        // their connections store InvoiceCreationEnabled=false by default, so fiscal submission starts
        // only after the seller enables it on the connection.
        if (!ActiveIntegrationScope.IsMarketplace(platformCode) || string.IsNullOrWhiteSpace(settingsJson)) return true;

        try
        {
            using var document = JsonDocument.Parse(settingsJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return false;
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (string.Equals(property.Name, "InvoiceCreationEnabled", StringComparison.OrdinalIgnoreCase))
                    return property.Value.ValueKind == JsonValueKind.True;
            }
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
