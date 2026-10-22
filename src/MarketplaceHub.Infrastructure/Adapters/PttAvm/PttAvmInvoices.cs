using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Infrastructure.Persistence;

namespace MarketplaceHub.Infrastructure.Adapters.PttAvm;

// Invoice link delivery (phase 6): SOAP SaveInvoince {lineItemId: arr:int list, orderId, content, url}. The panel
// sends the document-bound PDF link of the invoice for the order's live lines (one invoice per order). Every call
// passes the adapter's write gate, so nothing is sent while external writes are off. The order list answers each
// line's IsInvoice, which is the read-back: an order whose live lines all say true holds an invoice.
public sealed partial class PttAvmHttpClient : IInvoiceMarketplacePort
{
    internal const string SaveInvoice = "SaveInvoince";

    public async Task<AdapterResult<InvoiceDeliveryResult>> DeliverAsync(AdapterContext context, InvoiceDeliveryCommand command, CancellationToken cancellationToken)
    {
        var request = PttAvmInvoiceMapper.Request(command.PayloadJson);
        if (request.Error is not null) return Failure<InvoiceDeliveryResult>(AdapterErrorClass.Validation, "PTTAVM_INVOICE_LINK_INVALID", request.Error, HttpStatusCode.UnprocessableEntity);
        var load = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken);
        if (load.Account is null) return CredentialFailure<InvoiceDeliveryResult>(load);
        if (WriteBlocked<InvoiceDeliveryResult>(load.Account, "fatura linki") is { } blocked) return blocked;

        var saved = await SendSoapAsync(load.Account, SaveInvoice, PttAvmInvoiceMapper.Parameters(request), cancellationToken);
        if (!saved.IsSuccess) return AdapterResult<InvoiceDeliveryResult>.Failure(saved.Error!, saved.RateLimit);
        return PttAvmInvoiceMapper.Outcome(saved.Value!) switch
        {
            PttAvmInvoiceMapper.SaveOutcome.Saved => AdapterResult<InvoiceDeliveryResult>.Success(new(request.OrderNumber!, "INVOICED", true, request.InvoiceNumber, request.StoredLink), saved.RateLimit),
            // The documented sample answers Success=false with "işlem gerçekleştirilmiştir": the outcome is read back instead of guessed.
            PttAvmInvoiceMapper.SaveOutcome.Unclear => Failure<InvoiceDeliveryResult>(AdapterErrorClass.ContractViolation, "PTTAVM_INVOICE_RESULT_UNCLEAR", $"PTT AVM fatura kaydı için çelişkili cevap verdi ({PttAvmInvoiceMapper.Message(saved.Value!)}); sonuç sipariş okunarak doğrulanacak.", HttpStatusCode.BadGateway),
            _ => Failure<InvoiceDeliveryResult>(AdapterErrorClass.Validation, "PTTAVM_INVOICE_REJECTED", $"PTT AVM faturayı kabul etmedi: {PttAvmInvoiceMapper.Message(saved.Value!) ?? "gerekçe verilmedi"}.", HttpStatusCode.UnprocessableEntity)
        };
    }

    // The order's live lines (not cancelled) all carry IsInvoice=true once PTT AVM holds an invoice sent through the API.
    // An invoice uploaded in the seller panel leaves IsInvoice=false, so false is UNVERIFIED, never "no invoice".
    public async Task<AdapterResult<InvoiceDeliveryStatus>> QueryDeliveryAsync(AdapterContext context, ExternalInvoiceDeliveryReference reference, CancellationToken cancellationToken)
    {
        var orderNumber = reference.OrderNumber ?? reference.ExternalOrderId ?? reference.ExternalReference;
        if (orderNumber.StartsWith("order:", StringComparison.Ordinal)) orderNumber = orderNumber["order:".Length..];
        var order = await GetAsync(context, orderNumber, reference.Lookup?.Order, cancellationToken);
        if (!order.IsSuccess) return AdapterResult<InvoiceDeliveryStatus>.Failure(order.Error!, order.RateLimit);
        var invoiced = PttAvmInvoiceMapper.Invoiced(order.Value!);
        return invoiced is null
            ? Failure<InvoiceDeliveryStatus>(AdapterErrorClass.ContractViolation, "PTTAVM_INVOICE_STATUS_UNCLEAR", "PTT AVM siparişinin satırları fatura durumunu (IsInvoice) taşımıyor.", HttpStatusCode.BadGateway)
            : AdapterResult<InvoiceDeliveryStatus>.Success(new(reference.ExternalReference, invoiced.Value ? "INVOICED" : MarketplaceHub.Domain.MarketplaceInvoiceStatePolicy.Unverified, IsTerminal: true), order.RateLimit);
    }
}

public static class PttAvmInvoiceMapper
{
    public enum SaveOutcome { Saved, Rejected, Unclear }

    public sealed record InvoiceLinkRequest(string? Link, string? StoredLink, string? OrderNumber, IReadOnlyList<long> LineItemIds, string? InvoiceNumber, string? Error);

    // The delivery payload comes from the invoice workflow: the stored link, the invoice number, the order number and
    // the PTT AVM line ids (LineItemId) of the order's live lines.
    public static InvoiceLinkRequest Request(string payloadJson)
    {
        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            var root = document.RootElement;
            var stored = Text(root, "invoiceLink");
            var orderNumber = Text(root, "orderNumber");
            var invoiceNumber = Text(root, "invoiceNumber");
            var lineIds = new List<long>();
            if (root.TryGetProperty("lineItemIds", out var lines) && lines.ValueKind == JsonValueKind.Array)
                foreach (var line in lines.EnumerateArray())
                    if ((line.ValueKind == JsonValueKind.String ? line.GetString() : line.ValueKind == JsonValueKind.Number ? line.GetRawText() : null) is { } text
                        && long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0)
                        lineIds.Add(id);
            if (stored is null || orderNumber is null || lineIds.Count == 0)
                return new(null, stored, orderNumber, lineIds, invoiceNumber, "PTT AVM fatura gönderimi için link, sipariş numarası ve en az bir sipariş satırı (LineItemId) gerekir; sipariş PTT AVM'den yeniden okunmalı.");
            var link = InvoiceDocumentLink.PdfAlias(stored);
            if (!Uri.TryCreate(link, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
                return new(null, stored, orderNumber, lineIds, invoiceNumber, "PTT AVM'ye yalnız https ile başlayan fatura linkleri gönderilir.");
            return new(link, stored, orderNumber, lineIds.Distinct().ToList(), invoiceNumber, null);
        }
        catch (JsonException)
        {
            return new(null, null, null, [], null, "PTT AVM fatura gönderim verisi okunamadı.");
        }
    }

    // The documented order: lineItemId (arr:int list), orderId, content (empty: the link is used), url.
    internal static IReadOnlyList<PttAvmHttpClient.SoapParameter> Parameters(InvoiceLinkRequest request)
    {
        var lines = new StringBuilder();
        foreach (var id in request.LineItemIds) lines.Append("<arr:int>").Append(id.ToString(CultureInfo.InvariantCulture)).Append("</arr:int>");
        return [new("lineItemId", lines.ToString(), Raw: true), new("orderId", request.OrderNumber), new("content", ""), new("url", request.Link)];
    }

    // SaveInvoinceResult {Error_Message, Success}.
    public static SaveOutcome Outcome(System.Xml.Linq.XElement result)
    {
        if (string.Equals(PttAvmHttpClient.ContractValue(result, "Success"), "true", StringComparison.OrdinalIgnoreCase)) return SaveOutcome.Saved;
        var message = Message(result);
        return message is not null && PttAvmOrderMapper.Fold(message).Contains("gerceklestiril", StringComparison.Ordinal) ? SaveOutcome.Unclear : SaveOutcome.Rejected;
    }

    public static string? Message(System.Xml.Linq.XElement result) => PttAvmHttpClient.ContractValue(result, "Error_Message");

    // True when every live line says IsInvoice=true, false when one says false, null when the lines carry no flag.
    public static bool? Invoiced(RemoteOrder order)
    {
        var flags = order.Lines.Where(line => line.RawStatus != "Cancelled").Select(line => IsInvoice(line.SourceSnapshotJson)).ToList();
        if (flags.Count == 0 || flags.Any(flag => flag is null)) return null;
        return flags.All(flag => flag == true);
    }

    private static bool? IsInvoice(string snapshotJson)
    {
        try
        {
            using var document = JsonDocument.Parse(snapshotJson);
            return document.RootElement.TryGetProperty("IsInvoice", out var value) && value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out var flag) ? flag : null;
        }
        catch (JsonException) { return null; }
    }

    private static string? Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()) ? value.GetString()!.Trim() : null;
}
