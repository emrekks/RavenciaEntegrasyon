using System.Text.Json;

namespace MarketplaceHub.Application;

public static class MarketplaceInvoiceCreationPolicy
{
    public const string DisabledErrorCode = "INVOICE_CREATION_DISABLED";
    public const string DisabledMessage = "Fatura oluşturma bu bağlantı ayarlarında kapalı. Entegrasyonlar bölümünden açın.";

    public static bool IsEnabled(string? platformCode, string? settingsJson)
    {
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
