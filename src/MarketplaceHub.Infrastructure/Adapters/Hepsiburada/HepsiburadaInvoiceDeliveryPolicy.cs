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
            var documentType = DocumentContentType(contentType);
            if (documentType is null)
            {
                error = "Hepsiburada fatura bağlantısı PDF veya HTML belge sunmalıdır.";
                return false;
            }
            if (string.IsNullOrWhiteSpace(invoiceNumber))
            {
                error = "Hepsiburada fatura bağlantısı doğrulaması için fatura numarası zorunludur.";
                return false;
            }

            request = new(packageNumber, orderNumber, invoiceLink, arrangementDate.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture), documentType, invoiceNumber);
            error = string.Empty;
            return true;
        }
        catch (JsonException)
        {
            error = "Hepsiburada fatura bağlantısı isteği geçerli JSON değil.";
            return false;
        }
    }

    // The PUT .../invoice body exactly as live deliveries have been accepted (204): a MIME contentType and no
    // serialNumber/rowNumber. A schema copy shows contentType "pdf"/"html" and the split invoice number; that shape was
    // never confirmed against Hepsiburada, so the live-proven body stays until SIT shows the other one is required.
    public static string Body(HepsiburadaInvoiceDeliveryRequest invoice) => JsonSerializer.Serialize(new
    {
        arrangementDate = invoice.ArrangementDate,
        invoiceLink = invoice.InvoiceLink.AbsoluteUri,
        invoices = new[]
        {
            new
            {
                arrangementDate = invoice.ArrangementDate,
                contentType = invoice.ContentType,
                invoiceLink = invoice.InvoiceLink.AbsoluteUri,
                orderNumber = invoice.OrderNumber
            }
        }
    });

    // The stored document's MIME type is sent as is (lower case); a bare "pdf"/"html" is mapped back to it.
    internal static string? DocumentContentType(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "pdf" or "application/pdf" => "application/pdf",
        "html" or "text/html" => "text/html",
        _ => null
    };

    private static string? String(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
