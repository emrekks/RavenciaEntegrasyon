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
    string InvoiceNumber,
    string? SerialNumber = null,
    string? RowNumber = null);

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

            var (serialNumber, rowNumber) = SplitInvoiceNumber(invoiceNumber);
            request = new(packageNumber, orderNumber, invoiceLink, arrangementDate.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture), documentType, invoiceNumber, serialNumber, rowNumber);
            error = string.Empty;
            return true;
        }
        catch (JsonException)
        {
            error = "Hepsiburada fatura bağlantısı isteği geçerli JSON değil.";
            return false;
        }
    }

    private static readonly JsonSerializerOptions BodyOptions = new() { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

    // The PUT .../invoice body. serialNumber and rowNumber are left out when the number cannot be split (needs SIT confirmation).
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
                orderNumber = invoice.OrderNumber,
                serialNumber = invoice.SerialNumber,
                rowNumber = invoice.RowNumber
            }
        }
    }, BodyOptions);

    // NEEDS SIT CONFIRMATION: the invoice schema documents contentType as "pdf" or "html" (not a MIME type) and
    // serialNumber/rowNumber as the parts of the invoice number; checked against the documentation only, not yet
    // against Hepsiburada's SIT environment. A MIME type from the stored document is accepted and mapped.
    internal static string? DocumentContentType(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "pdf" or "application/pdf" => "pdf",
        "html" or "text/html" => "html",
        _ => null
    };

    // A GİB invoice number is a three-character serial and a thirteen-digit row (year and sequence), e.g.
    // DAP2026000000016 -> DAP / 2026000000016. Any other number (a hand-entered one) is sent without the split.
    internal static (string? SerialNumber, string? RowNumber) SplitInvoiceNumber(string invoiceNumber)
    {
        var value = invoiceNumber.Trim();
        return value.Length == 16 && value[..3].All(char.IsAsciiLetterOrDigit) && value[3..].All(char.IsAsciiDigit)
            ? (value[..3], value[3..])
            : (null, null);
    }

    private static string? String(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
