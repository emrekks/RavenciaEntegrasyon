using System.Net;
using System.Text.Json;
using MarketplaceHub.Application;
using Microsoft.Extensions.Configuration;

namespace MarketplaceHub.Infrastructure.Adapters.Shopify;

public sealed partial class ShopifyHttpClient
{
    public async Task<AdapterResult<InvoiceDeliveryResult>> DeliverAsync(AdapterContext context, InvoiceDeliveryCommand command, CancellationToken cancellationToken)
    {
        var shop = await authentication.LoadAsync(context.TenantId, context.ConnectionId, settings.ApiVersion, cancellationToken);
        if (shop is null) return CredentialFailure(Fail<InvoiceDeliveryResult>(AdapterErrorClass.Authentication, "SHOPIFY_CREDENTIAL_INVALID", "Shopify yetkilendirmesi bulunamadı.", HttpStatusCode.Unauthorized));
        var automaticDeliveryAllowed = IntegrationRuntimePolicy.AllowsAutomaticInvoiceMarketplaceDelivery(shop.Connection, context, configuration.GetValue<bool>("FeatureFlags:InvoiceMarketplaceDeliveryWrites"))
            && await CanWriteAsync(shop, context, InvoicingCapabilities.InvoiceDeliver, cancellationToken);
        var manualDeliveryAllowed = IntegrationRuntimePolicy.AllowsExplicitManualInvoiceMarketplaceDelivery(
                shop.Connection,
                context,
                ExternalWriteGate.GlobalWritesEnabled(configuration),
                ConnectionExternalWritesEnabled(shop.Connection.SettingsJson))
            && await HasVerifiedWriteCapabilityAsync(shop, context, InvoicingCapabilities.InvoiceDeliver, cancellationToken);
        if (!automaticDeliveryAllowed && !manualDeliveryAllowed)
            return WriteClosed<InvoiceDeliveryResult>();
        if (!TryReadInvoiceDeliveryPayload(command.PayloadJson, out var delivery))
            return Fail<InvoiceDeliveryResult>(AdapterErrorClass.Validation, "SHOPIFY_INVOICE_DELIVERY_INVALID", "Shopify fatura iletimi için siparişin GraphQL kimliği, HTTPS PDF bağlantısı ve fatura numarası zorunludur.", HttpStatusCode.BadRequest);

        var current = await ReadInvoiceMetafieldsAsync(shop, delivery.OrderId, cancellationToken);
        if (!current.IsSuccess) return AdapterResult<InvoiceDeliveryResult>.Failure(current.Error!, current.RateLimit);
        if (InvoiceMetafieldsMatch(current.Value!, delivery.InvoiceLink))
            return AdapterResult<InvoiceDeliveryResult>.Success(new(delivery.OrderId, "INVOICED", true, delivery.InvoiceNumber, delivery.InvoiceLink), current.RateLimit);
        if (HasAnyInvoiceMetafield(current.Value!))
            return Failure<InvoiceDeliveryResult>(new(AdapterErrorClass.BusinessConflict, "SHOPIFY_INVOICE_METAFIELD_CONFLICT", "Siparişte Ravencia fatura bilgisi mevcut ve seçilen faturayla eşleşmiyor; mevcut bağlantı üzerine yazılmadı.", (int)HttpStatusCode.Conflict, null, null), current.RateLimit);

        const string mutation = "mutation($metafields:[MetafieldsSetInput!]!){metafieldsSet(metafields:$metafields){userErrors{field message code}}}";
        var inputs = BuildInvoiceMetafieldSetInputs(delivery.OrderId, delivery.InvoiceLink, current.Value!.PdfUrl?.CompareDigest);
        var written = await QueryAsync(shop, mutation, new { metafields = inputs }, cancellationToken);
        if (!written.IsSuccess) return AdapterResult<InvoiceDeliveryResult>.Failure(written.Error!, written.RateLimit);
        var mutationPayload = written.Value!.RootElement.GetProperty("metafieldsSet");
        if (HasUserErrors(mutationPayload, out var userError))
            return Failure<InvoiceDeliveryResult>(new(AdapterErrorClass.BusinessConflict, "SHOPIFY_INVOICE_METAFIELD_WRITE_REJECTED", userError ?? "Shopify fatura metafield yazımını reddetti.", (int)HttpStatusCode.Conflict, null, null), written.RateLimit);

        // A successful mutation response is not enough to mark delivery. Read
        // the link back and verify the exact permanent URL.
        var verified = await ReadInvoiceMetafieldsAsync(shop, delivery.OrderId, cancellationToken);
        if (!verified.IsSuccess) return AdapterResult<InvoiceDeliveryResult>.Failure(verified.Error!, verified.RateLimit);
        if (!InvoiceMetafieldsMatch(verified.Value!, delivery.InvoiceLink))
            return Failure<InvoiceDeliveryResult>(new(AdapterErrorClass.BusinessConflict, "SHOPIFY_INVOICE_METAFIELD_READBACK_MISMATCH", "Shopify metafield yazımı doğrulanamadı; kayıt incelemede bırakıldı ve tekrar yazılmadı.", (int)HttpStatusCode.Conflict, null, null), verified.RateLimit);
        return AdapterResult<InvoiceDeliveryResult>.Success(new(delivery.OrderId, "INVOICED", true, delivery.InvoiceNumber, delivery.InvoiceLink), verified.RateLimit ?? written.RateLimit);
    }

    public async Task<AdapterResult<InvoiceDeliveryStatus>> QueryDeliveryAsync(AdapterContext context, ExternalInvoiceDeliveryReference reference, CancellationToken cancellationToken)
    {
        var shop = await authentication.LoadAsync(context.TenantId, context.ConnectionId, settings.ApiVersion, cancellationToken);
        if (shop is null) return CredentialFailure(Fail<InvoiceDeliveryStatus>(AdapterErrorClass.Authentication, "SHOPIFY_CREDENTIAL_INVALID", "Shopify yetkilendirmesi bulunamadı.", HttpStatusCode.Unauthorized));
        if (!IntegrationRuntimePolicy.AllowsManualRead(shop.Connection))
            return Fail<InvoiceDeliveryStatus>(AdapterErrorClass.NotSupported, "SHOPIFY_CONNECTION_INACTIVE", "Shopify fatura durumu yalnızca etkin bağlantıdan okunabilir.", HttpStatusCode.Forbidden);
        var orderId = reference.ExternalOrderId ?? reference.ExternalReference;
        if (!IsShopifyOrderGid(orderId))
            return Fail<InvoiceDeliveryStatus>(AdapterErrorClass.Validation, "SHOPIFY_INVOICE_READBACK_REFERENCE_INVALID", "Shopify fatura kontrolü için sipariş kimliği gereklidir.", HttpStatusCode.BadRequest);
        var read = await ReadInvoiceMetafieldsAsync(shop, orderId!, cancellationToken);
        if (!read.IsSuccess) return AdapterResult<InvoiceDeliveryStatus>.Failure(read.Error!, read.RateLimit);
        var invoiceUrl = EmptyToNull(read.Value!.PdfUrl?.Value);
        return AdapterResult<InvoiceDeliveryStatus>.Success(new(orderId!, DeliverySlotStatus(invoiceUrl),
            true, null, invoiceUrl, ReferencesAreAuthoritative: true), read.RateLimit);
    }

    private async Task<AdapterResult<ShopifyInvoiceMetafields>> ReadInvoiceMetafieldsAsync(ShopifyRequestContext shop, string orderId, CancellationToken cancellationToken)
    {
        const string query = "query($id:ID!){order(id:$id){id pdf:metafield(namespace:\"ravencia\",key:\"invoice_pdf_url\"){value compareDigest}}}";
        var result = await QueryAsync(shop, query, new { id = orderId }, cancellationToken);
        if (!result.IsSuccess) return AdapterResult<ShopifyInvoiceMetafields>.Failure(result.Error!, result.RateLimit);
        try
        {
            var order = result.Value!.RootElement.GetProperty("order");
            if (order.ValueKind == JsonValueKind.Null)
                return Failure<ShopifyInvoiceMetafields>(new(AdapterErrorClass.NotFound, "SHOPIFY_ORDER_NOT_FOUND", "Shopify siparişi bulunamadı.", (int)HttpStatusCode.NotFound, null, null), result.RateLimit);
            return AdapterResult<ShopifyInvoiceMetafields>.Success(new(ReadMetafield(order, "pdf")), result.RateLimit);
        }
        catch (Exception exception) when (exception is KeyNotFoundException or InvalidOperationException or JsonException)
        {
            return Fail<ShopifyInvoiceMetafields>(AdapterErrorClass.ContractViolation, "SHOPIFY_INVOICE_METAFIELD_CONTRACT_INVALID", "Shopify sipariş fatura metafield yanıtı beklenen alanları içermiyor.", HttpStatusCode.BadGateway);
        }
    }

    private static ShopifyInvoiceMetafield? ReadMetafield(JsonElement order, string field)
    {
        if (!order.TryGetProperty(field, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        return new(TextValue(value, "value"), TextValue(value, "compareDigest"));
    }

    private static bool HasAnyInvoiceMetafield(ShopifyInvoiceMetafields values) =>
        !string.IsNullOrWhiteSpace(values.PdfUrl?.Value);

    // The order's ravencia.invoice_pdf_url is the only invoice evidence Shopify has: a link is an invoice. No link says
    // nothing (the order may have been invoiced outside the panel), so the order observation keeps the state unknown.
    internal static string InvoiceStatusForUrl(string? invoiceUrl) => EmptyToNull(invoiceUrl) is null ? "UNKNOWN" : "INVOICED";

    // The delivery read before and after a write asks a narrower question: is the panel's own link slot empty? An empty
    // slot is what lets the link be written (the write carries its compare digest), so it reads NOT_INVOICED there.
    internal static string DeliverySlotStatus(string? invoiceUrl) => EmptyToNull(invoiceUrl) is null ? "NOT_INVOICED" : "INVOICED";

    internal static RemoteOrder AttachInvoiceMetafields(RemoteOrder order, string? invoiceUrl, DateTimeOffset observedAt)
    {
        invoiceUrl = EmptyToNull(invoiceUrl);
        var observation = new RemotePackageInvoiceObservation(
            InvoiceStatusForUrl(invoiceUrl),
            null,
            invoiceUrl,
            observedAt,
            ReferencesAreAuthoritative: true);
        // Shopify invoice metafields are order-level. Project the same
        // read-only observation onto each fulfillment/remainder target so the
        // shared reconciliation path refreshes changed links. A removed link reads
        // UNKNOWN and leaves the recorded state as it is (unknown stays unknown).
        return order with { Packages = order.Packages.Select(package => package with { Invoice = observation }).ToArray() };
    }

    private static string? EmptyToNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool InvoiceMetafieldsMatch(ShopifyInvoiceMetafields values, string invoiceLink) =>
        AreShopifyInvoiceMetafieldsMatching(values.PdfUrl?.Value, invoiceLink);

    internal static bool AreShopifyInvoiceMetafieldsMatching(string? currentInvoiceLink, string expectedInvoiceLink) =>
        string.Equals(currentInvoiceLink, expectedInvoiceLink, StringComparison.Ordinal);

    // Only the invoice's PDF link is written; ravencia.invoice_number is no longer read or written.
    internal static object[] BuildInvoiceMetafieldSetInputs(string orderId, string invoiceLink, string? invoiceLinkDigest) =>
    [
        new { ownerId = orderId, @namespace = "ravencia", key = "invoice_pdf_url", type = "url", value = invoiceLink, compareDigest = invoiceLinkDigest }
    ];

    internal static bool IsValidInvoiceDeliveryPayload(string json) => TryReadInvoiceDeliveryPayload(json, out _);

    private static bool TryReadInvoiceDeliveryPayload(string json, out ShopifyInvoiceDeliveryPayload payload)
    {
        payload = default!;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var orderId = TextValue(root, "shopifyOrderId");
            var number = TextValue(root, "invoiceNumber");
            var link = TextValue(root, "invoiceLink");
            if (!IsShopifyOrderGid(orderId) || string.IsNullOrWhiteSpace(number)
                || !Uri.TryCreate(link, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return false;
            payload = new(orderId!, number!, uri.AbsoluteUri);
            return true;
        }
        catch (JsonException) { return false; }
    }

    private static bool IsShopifyOrderGid(string? value) => value is not null && value.StartsWith("gid://shopify/Order/", StringComparison.Ordinal) && value.Length > "gid://shopify/Order/".Length;

    private sealed record ShopifyInvoiceMetafield(string? Value, string? CompareDigest);
    private sealed record ShopifyInvoiceMetafields(ShopifyInvoiceMetafield? PdfUrl);
    private sealed record ShopifyInvoiceDeliveryPayload(string OrderId, string InvoiceNumber, string InvoiceLink);
}
