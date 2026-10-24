using System.Net;
using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Infrastructure.Persistence;

namespace MarketplaceHub.Infrastructure.Adapters.Pazarama;

// Invoice link delivery (phase 6): POST order/invoice-link {invoiceLink, orderid, deliveryCompanyId, trackingNumber}.
// With the carrier and tracking number left null the link attaches to every item of the order, which is
// how the panel delivers (one invoice per order). A new link for the same order replaces the earlier one,
// so resending the same invoice cannot create a duplicate. Pazarama has no service to read the link back;
// the successful answer of the save call is the only proof.
public sealed partial class PazaramaHttpClient : IInvoiceMarketplacePort
{
    internal const string InvoiceLink = "order/invoice-link";
    private const int MaximumInvoiceLinkLength = 2048;
    private static readonly string[] InvoiceLinkExtensions = [".pdf", ".png", ".jpeg", ".jpg"];

    public async Task<AdapterResult<InvoiceDeliveryResult>> DeliverAsync(AdapterContext context, InvoiceDeliveryCommand command, CancellationToken cancellationToken)
    {
        var request = PazaramaInvoiceMapper.Request(command.PayloadJson);
        if (request.Error is not null) return Failure<InvoiceDeliveryResult>(AdapterErrorClass.Validation, "PAZARAMA_INVOICE_LINK_INVALID", request.Error, HttpStatusCode.UnprocessableEntity);
        var account = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken);
        if (account is null) return CredentialMissing<InvoiceDeliveryResult>();
        if (WriteBlocked<InvoiceDeliveryResult>(account, "fatura linki") is { } blocked) return blocked;

        var saved = await SendAsync(account, HttpMethod.Post, InvoiceLink, cancellationToken, Json(PazaramaInvoiceMapper.Body(request)));
        if (!saved.IsSuccess) return AdapterResult<InvoiceDeliveryResult>.Failure(saved.Error!, saved.RateLimit);
        saved.Value!.Dispose();
        // The envelope reported success (SendAsync turns success=false into an error), which is all Pazarama says.
        return AdapterResult<InvoiceDeliveryResult>.Success(new(request.OrderNumber ?? request.OrderId!, "INVOICED", true, request.InvoiceNumber, request.StoredLink), saved.RateLimit);
    }

    // No read-back service exists, and an invoice uploaded in the Pazarama seller panel is invisible to the API. Before
    // a delivery the answer is UNVERIFIED (as on n11 and PTT AVM), never "no invoice": the invoice workflow decides
    // from local state (an observed invoice or a hand-set "uploaded") whether to issue or send. A confirmation read of
    // an earlier delivery cannot be answered.
    public Task<AdapterResult<InvoiceDeliveryStatus>> QueryDeliveryAsync(AdapterContext context, ExternalInvoiceDeliveryReference reference, CancellationToken cancellationToken) =>
        Task.FromResult(string.IsNullOrWhiteSpace(reference.InvoiceNumber) && string.IsNullOrWhiteSpace(reference.InvoiceLink)
            ? AdapterResult<InvoiceDeliveryStatus>.Success(new(reference.ExternalReference, MarketplaceHub.Domain.MarketplaceInvoiceStatePolicy.Unverified, IsTerminal: true))
            : Failure<InvoiceDeliveryStatus>(AdapterErrorClass.NotSupported, "PAZARAMA_INVOICE_READBACK_UNSUPPORTED", "Pazarama gönderilen fatura linkini geri okuma servisi sunmuyor.", HttpStatusCode.NotImplemented));

    internal static bool AcceptsInvoiceExtension(Uri uri) => InvoiceLinkExtensions.Any(extension => uri.AbsolutePath.EndsWith(extension, StringComparison.OrdinalIgnoreCase));
    internal static int MaxInvoiceLinkLength => MaximumInvoiceLinkLength;
}

public static class PazaramaInvoiceMapper
{
    public sealed record InvoiceLinkRequest(string? Link, string? StoredLink, string? OrderId, string? OrderNumber, string? InvoiceNumber, string? Error);

    // The delivery payload comes from the invoice workflow: the stored link, the invoice number and the
    // Pazarama order GUID (pazaramaOrderId) read from the order.
    public static InvoiceLinkRequest Request(string payloadJson)
    {
        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            var root = document.RootElement;
            var stored = Text(root, "invoiceLink");
            var orderId = Text(root, "pazaramaOrderId");
            var orderNumber = Text(root, "orderNumber");
            var invoiceNumber = Text(root, "invoiceNumber");
            if (stored is null || orderId is null || !Guid.TryParse(orderId, out var parsedOrderId))
                return new(null, stored, null, orderNumber, invoiceNumber, "Pazarama fatura linki için link ve Pazarama sipariş kimliği (orderId) gerekir; sipariş Pazarama'dan yeniden okunmalı.");
            var link = InvoiceDocumentLink.PdfAlias(stored);
            if (!Uri.TryCreate(link, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
                return new(null, stored, parsedOrderId.ToString(), orderNumber, invoiceNumber, "Pazarama yalnız https ile başlayan fatura linklerini kabul eder.");
            if (link.Length > PazaramaHttpClient.MaxInvoiceLinkLength)
                return new(null, stored, parsedOrderId.ToString(), orderNumber, invoiceNumber, "Pazarama fatura linki en fazla 2048 karakter olabilir.");
            if (!PazaramaHttpClient.AcceptsInvoiceExtension(uri))
                return new(null, stored, parsedOrderId.ToString(), orderNumber, invoiceNumber, "Pazarama fatura linkinin pdf, png veya jpeg dosyasına işaret etmesi gerekir.");
            return new(link, stored, parsedOrderId.ToString(), orderNumber, invoiceNumber, null);
        }
        catch (JsonException)
        {
            return new(null, null, null, null, null, "Pazarama fatura gönderim verisi okunamadı.");
        }
    }

    // Carrier and tracking number stay null: the link covers the whole order.
    public static object Body(InvoiceLinkRequest request) => new
    {
        invoiceLink = request.Link,
        orderid = request.OrderId,
        deliveryCompanyId = (string?)null,
        trackingNumber = (string?)null
    };

    private static string? Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!.Trim()
            : null;
}
