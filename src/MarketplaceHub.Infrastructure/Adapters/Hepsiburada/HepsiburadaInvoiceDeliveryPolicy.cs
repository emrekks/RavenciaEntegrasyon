using System.Globalization;
using System.Text.Json;
using MarketplaceHub.Application;

namespace MarketplaceHub.Infrastructure.Adapters.Hepsiburada;

internal sealed record HepsiburadaInvoiceDeliveryRequest(
    string PackageNumber,
    string OrderNumber,
    Uri InvoiceLink,
    string ArrangementDate,
    string ContentType,
    string InvoiceNumber);

internal static class HepsiburadaInvoiceDeliveryPolicy
{
    public static bool TryCreate(
        InvoiceDeliveryCommand command,
        out HepsiburadaInvoiceDeliveryRequest? request,
        out string error)
    {
        request = null;
        error = "Hepsiburada fatura bağlantısı isteği geçersiz.";
        if (!string.Equals(command.DeliveryType, "LINK", StringComparison.Ordinal))
        {
            error = "Hepsiburada yalnız HTTPS fatura bağlantısı teslimini destekler.";
            return false;
        }

        try
        {
            using var payload = JsonDocument.Parse(command.PayloadJson);
            var root = payload.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "Hepsiburada fatura bağlantısı isteği JSON nesnesi olmalıdır.";
                return false;
            }
            var packageNumber = String(root, "shipmentPackageId");
            var orderNumber = String(root, "orderNumber");
            var invoiceLinkText = String(root, "invoiceLink");
            var arrangementDateText = String(root, "arrangementDate");
            var contentType = String(root, "contentType")?.Trim();
            var invoiceNumber = String(root, "invoiceNumber")?.Trim();
            if (string.IsNullOrWhiteSpace(command.ExternalPackageId)
                || string.IsNullOrWhiteSpace(packageNumber)
                || !string.Equals(packageNumber, command.ExternalPackageId, StringComparison.Ordinal))
            {
                error = "Hepsiburada paket numarası teslimat kaydıyla eşleşmiyor.";
                return false;
            }
            if (string.IsNullOrWhiteSpace(orderNumber))
            {
                error = "Hepsiburada fatura doğrulaması için sipariş numarası zorunludur.";
                return false;
            }
            if (!Uri.TryCreate(invoiceLinkText, UriKind.Absolute, out var invoiceLink)
                || invoiceLink.Scheme != Uri.UriSchemeHttps)
            {
                error = "Hepsiburada fatura bağlantısı HTTPS olmalıdır.";
                return false;
            }
            if (!DateTimeOffset.TryParse(arrangementDateText, CultureInfo.InvariantCulture, DateTimeStyles.None, out var arrangementDate))
            {
                error = "Hepsiburada fatura düzenleme tarihi geçerli bir tarih olmalıdır.";
                return false;
            }
            if (!string.Equals(contentType, "application/pdf", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(contentType, "text/html", StringComparison.OrdinalIgnoreCase))
            {
                error = "Hepsiburada fatura bağlantısı PDF veya HTML belge sunmalıdır.";
                return false;
            }
            if (string.IsNullOrWhiteSpace(invoiceNumber))
            {
                error = "Hepsiburada fatura bağlantısı doğrulaması için fatura numarası zorunludur.";
                return false;
            }

            request = new(packageNumber, orderNumber, invoiceLink, arrangementDate.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture), contentType!.ToLowerInvariant(), invoiceNumber);
            error = string.Empty;
            return true;
        }
        catch (JsonException)
        {
            error = "Hepsiburada fatura bağlantısı isteği geçerli JSON değil.";
            return false;
        }
    }

    private static string? String(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
