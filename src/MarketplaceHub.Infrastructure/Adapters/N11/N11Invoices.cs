using System.Globalization;
using System.Net;
using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Infrastructure.Persistence;

namespace MarketplaceHub.Infrastructure.Adapters.N11;

// Invoice link delivery: SOAP SellerInvoiceService/SaveLinkSellerInvoice
// (https://api.n11.com/ws/SellerInvoiceService.wsdl). An order holds one active link and a new
// link replaces the previous one, so resending the same invoice cannot create a duplicate.
// n11 has no service to read the link back; the answer to the save call is the only proof.
public sealed partial class N11HttpClient : IInvoiceMarketplacePort
{
    private const string SellerInvoiceService = "ws/sellerInvoiceService/";
    private const int MaximumInvoiceLinkLength = 2048;
    private static readonly string[] InvoiceLinkExtensions = [".pdf", ".png", ".jpeg", ".jpg"];

    public async Task<AdapterResult<InvoiceDeliveryResult>> DeliverAsync(AdapterContext context, InvoiceDeliveryCommand command, CancellationToken cancellationToken)
    {
        var request = InvoiceLinkRequest(command.PayloadJson);
        if (request.Error is not null) return Failure<InvoiceDeliveryResult>(AdapterErrorClass.Validation, "N11_INVOICE_LINK_INVALID", request.Error, HttpStatusCode.UnprocessableEntity);
        var account = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken);
        if (account is null) return CredentialMissing<InvoiceDeliveryResult>();
        if (WriteBlocked<InvoiceDeliveryResult>(account, "fatura linki") is { } blocked) return blocked;

        var inner = SoapElement("url", request.Link) + SoapElement("orderNumber", request.OrderNumber)
            + SoapElement("invoiceDateTime", request.InvoiceDateTime) + SoapElement("invoiceNumber", request.InvoiceNumber);
        var saved = await SendSoapAsync(account, SellerInvoiceService, "SaveLinkSellerInvoice", inner, cancellationToken);
        if (!saved.IsSuccess) return AdapterResult<InvoiceDeliveryResult>.Failure(saved.Error!, saved.RateLimit);

        // n11 echoes the saved link and invoice number; matching echoes prove this attempt.
        var echoedLink = Child(saved.Value, "url");
        var echoedNumber = Child(saved.Value, "invoiceNumber");
        var verified = string.Equals(echoedLink, request.Link, StringComparison.Ordinal)
            && string.Equals(echoedNumber, request.InvoiceNumber, StringComparison.Ordinal);
        return AdapterResult<InvoiceDeliveryResult>.Success(new(request.OrderNumber!, "INVOICED", verified,
            verified ? request.InvoiceNumber : null,
            verified ? request.StoredLink : null));
    }

    // No read-back service exists. Before a delivery the platform is treated as holding no link;
    // the next save replaces any earlier link. A confirmation read cannot be answered.
    public Task<AdapterResult<InvoiceDeliveryStatus>> QueryDeliveryAsync(AdapterContext context, ExternalInvoiceDeliveryReference reference, CancellationToken cancellationToken) =>
        Task.FromResult(string.IsNullOrWhiteSpace(reference.InvoiceNumber) && string.IsNullOrWhiteSpace(reference.InvoiceLink)
            ? AdapterResult<InvoiceDeliveryStatus>.Success(new(reference.ExternalReference, "NOT_INVOICED", IsTerminal: true))
            : Failure<InvoiceDeliveryStatus>(AdapterErrorClass.NotSupported, "N11_INVOICE_READBACK_UNSUPPORTED", "N11 gönderilen fatura linkini geri okuma servisi sunmuyor.", HttpStatusCode.NotImplemented));

    internal sealed record N11InvoiceLinkRequest(string? Link, string? StoredLink, string? OrderNumber, string? InvoiceNumber, string? InvoiceDateTime, string? Error);

    internal static N11InvoiceLinkRequest InvoiceLinkRequest(string payloadJson)
    {
        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            var root = document.RootElement;
            var stored = Text(root, "invoiceLink");
            var orderNumber = Text(root, "orderNumber");
            var invoiceNumber = Text(root, "invoiceNumber");
            DateTimeOffset? issuedAt = root.TryGetProperty("invoiceDateTime", out var issued) && issued.ValueKind == JsonValueKind.Number && issued.TryGetInt64(out var milliseconds)
                ? DateTimeOffset.FromUnixTimeMilliseconds(milliseconds)
                : null;
            if (stored is null || orderNumber is null || invoiceNumber is null || issuedAt is null)
                return new(null, stored, orderNumber, invoiceNumber, null, "N11 fatura linki için link, sipariş numarası, fatura numarası ve fatura tarihi gerekir.");
            var link = InvoiceDocumentLink.PdfAlias(stored);
            if (!Uri.TryCreate(link, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
                return new(null, stored, orderNumber, invoiceNumber, null, "N11 yalnız https ile başlayan fatura linklerini kabul ediyor.");
            if (link.Length > MaximumInvoiceLinkLength)
                return new(null, stored, orderNumber, invoiceNumber, null, "N11 fatura linki en fazla 2048 karakter olabilir.");
            if (!InvoiceLinkExtensions.Any(extension => uri.AbsolutePath.EndsWith(extension, StringComparison.OrdinalIgnoreCase)))
                return new(null, stored, orderNumber, invoiceNumber, null, "N11 fatura linkinin pdf, png veya jpeg dosyasına işaret etmesi gerekir.");
            return new(link, stored, orderNumber, invoiceNumber, InvoiceDateTime(issuedAt.Value), null);
        }
        catch (JsonException)
        {
            return new(null, null, null, null, null, "N11 fatura gönderim verisi okunamadı.");
        }
    }

    // The WSDL types invoiceDateTime as a plain string. n11's SOAP services use dd/MM/yyyy dates,
    // so the invoice time is sent in Turkey time as dd/MM/yyyy HH:mm. Confirm on the first live save.
    internal static string InvoiceDateTime(DateTimeOffset issuedAt) =>
        TimeZoneInfo.ConvertTime(issuedAt, TurkeyTimeZone).ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture);

    private static readonly TimeZoneInfo TurkeyTimeZone = FindTurkeyTimeZone();

    private static TimeZoneInfo FindTurkeyTimeZone()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("Europe/Istanbul"); }
        catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return TimeZoneInfo.CreateCustomTimeZone("Turkey", TimeSpan.FromHours(3), "Turkey", "Turkey");
        }
    }

    private static string? Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!.Trim()
            : null;
}
