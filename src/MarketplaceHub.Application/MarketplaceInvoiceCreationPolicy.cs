using System.Text.Json;

namespace MarketplaceHub.Application;

public static class MarketplaceInvoiceCreationPolicy
{
    public const string DisabledErrorCode = "INVOICE_CREATION_DISABLED";
    public const string DisabledMessage = "Fatura oluşturma bu bağlantı ayarlarında kapalı. Entegrasyonlar bölümünden açın.";
    public const string UnsupportedFiscalProviderMessage = "Shopify için mali fatura sağlayıcısı sözleşmesi henüz doğrulanmadı; güvenli biçimde fatura oluşturma kapalı.";

    public static bool IsEnabled(string? platformCode, string? settingsJson)
    {
        // Shopify fiscal submission is intentionally fail-closed until the
        // configured provider contract for Shopify orders is verified.
        if (string.Equals(platformCode, "SHOPIFY", StringComparison.OrdinalIgnoreCase)) return false;
        // N11 invoice links go through SaveLinkSellerInvoice; N11 connections store
        // InvoiceCreationEnabled=false by default, so fiscal submission starts only after
        // the seller enables it on the connection.
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
