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

        var verified = InvoiceLinkVerified(saved.Value!, request);
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

    // C8: a success answer (SendSoapAsync already required result/status=success) that echoes this link proves the
    // attempt. The order number and invoiceNumber are checked only when n11 echoes them (it is not proven that
    // every answer carries the order number, and n11 echoes invoiceNumber only for e-export orders): a missing echo
    // is not a mismatch, a different one is.
    internal static bool InvoiceLinkVerified(System.Xml.Linq.XElement response, N11InvoiceLinkRequest request)
    {
        var echoedOrder = Child(response, "orderNumber");
        var echoedNumber = Child(response, "invoiceNumber");
        return string.Equals(Child(response, "url"), request.Link, StringComparison.Ordinal)
            && (string.IsNullOrWhiteSpace(echoedOrder) || string.Equals(echoedOrder, request.OrderNumber, StringComparison.Ordinal))
            && (echoedNumber is null || string.Equals(echoedNumber, request.InvoiceNumber, StringComparison.Ordinal));
    }

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
            if (!IsInvoiceNumber(invoiceNumber))
                return new(null, stored, orderNumber, invoiceNumber, null, "N11 fatura numarasını 16 karakter olarak bekliyor: 3 harf veya rakam, 2020–2099 arası yıl ve 9 rakam (örnek: ABC2026000000001).");
            return new(link, stored, orderNumber, invoiceNumber, InvoiceDateTime(issuedAt.Value), null);
        }
        catch (JsonException)
        {
            return new(null, null, null, null, null, "N11 fatura gönderim verisi okunamadı.");
        }
    }

    // n11's invoice link page asks for invoiceDateTime as a millisecond timestamp (sample 1780903800000),
    // the same form the order list takes for its dates.
    internal static string InvoiceDateTime(DateTimeOffset issuedAt) =>
        issuedAt.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);

    // n11's invoice number rule: 3 letters or digits, a year from 2020 to 2099, then 9 digits.
    internal static bool IsInvoiceNumber(string? value) =>
        value is { Length: 16 }
        && value[..3].All(char.IsAsciiLetterOrDigit)
        && value[3..].All(char.IsAsciiDigit)
        && int.Parse(value[3..7], CultureInfo.InvariantCulture) is >= 2020 and <= 2099;

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
