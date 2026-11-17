using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Adapters.Trendyol.ErrorMapping;
using Microsoft.Extensions.Configuration;

namespace MarketplaceHub.Infrastructure.Adapters.Trendyol;

public sealed partial class TrendyolHttpClient
{
    public async Task<AdapterResult<InvoiceDeliveryResult>> DeliverAsync(AdapterContext context, InvoiceDeliveryCommand command, CancellationToken cancellationToken)
    {
        var authorized = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken); if (authorized is null) return AdapterResult<InvoiceDeliveryResult>.Failure(TrendyolErrorMapper.Configuration());
        return await DeliverAsync(authorized, context, command, cancellationToken);
    }

    internal async Task<AdapterResult<InvoiceDeliveryResult>> DeliverAsync(TrendyolRequestContext authorized, AdapterContext context, InvoiceDeliveryCommand command, CancellationToken cancellationToken)
    {
        var invoiceDeliveryWriteAllowed = IntegrationRuntimePolicy.AllowsAutomaticInvoiceMarketplaceDelivery(
            authorized.Connection,
            context,
            configuration.GetValue<bool>("FeatureFlags:InvoiceMarketplaceDeliveryWrites"))
            || IntegrationRuntimePolicy.AllowsExplicitManualInvoiceMarketplaceDelivery(
                authorized.Connection,
                context,
                GlobalWritesEnabled,
                authorized.ExternalWritesEnabled);
        if (!CanWrite(authorized, context) || !invoiceDeliveryWriteAllowed) return AdapterResult<InvoiceDeliveryResult>.Failure(TrendyolErrorMapper.WriteClosed());
        if (!string.Equals(command.DeliveryType, "LINK", StringComparison.Ordinal)) return AdapterResult<InvoiceDeliveryResult>.Failure(TrendyolErrorMapper.Unsupported("Yalnız resmî link delivery sözleşmesi doğrulandı; file delivery ayrı content akışı kanıtı bekliyor."));
        JsonDocument payload; try { payload = JsonDocument.Parse(command.PayloadJson); } catch (JsonException) { return AdapterResult<InvoiceDeliveryResult>.Failure(TrendyolErrorMapper.Contract()); }
        using (payload)
        {
            if (!payload.RootElement.TryGetProperty("invoiceLink", out var link) || link.ValueKind != JsonValueKind.String || !Uri.TryCreate(link.GetString(), UriKind.Absolute, out var invoiceUri) || invoiceUri.Scheme != "https"
                || !payload.RootElement.TryGetProperty("shipmentPackageId", out var packageId) || packageId.ValueKind is not (JsonValueKind.String or JsonValueKind.Number))
                return AdapterResult<InvoiceDeliveryResult>.Failure(TrendyolErrorMapper.Unsupported("HTTPS invoice link ve shipmentPackageId doğrulanmadan delivery HTTP çağrısı yapılmaz."));

            var isMicro = payload.RootElement.TryGetProperty("micro", out var micro) && micro.ValueKind == JsonValueKind.True;
            if (isMicro)
            {
                if (RemoteJson.Int64(payload.RootElement, "invoiceDateTime") is null
                    || !payload.RootElement.TryGetProperty("invoiceNumber", out var invoiceNumber) || invoiceNumber.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(invoiceNumber.GetString()))
                    return AdapterResult<InvoiceDeliveryResult>.Failure(TrendyolErrorMapper.Unsupported("Mikro ihracat delivery payloadında invoiceDateTime ve invoiceNumber zorunludur."));
            }
            if (InvoiceLinkBody(payload.RootElement) is not { } body)
                return AdapterResult<InvoiceDeliveryResult>.Failure(new(AdapterErrorClass.Validation, InvoiceLinkBodyInvalidCode,
                    "Trendyol fatura linki gönderilmedi: paket numarası sayı değil veya mikro ihracat faturasının numarası GİB biçiminde (3 harf/rakam ve 13 rakam) değil.", null, null, null));
            var response = await SendAsync(authorized, HttpMethod.Post, TrendyolEndpoints.InvoiceLinks(authorized.Connection.ExternalStoreId), JsonContent.Create(body), cancellationToken, storeFrontCode: WriteStoreFront(command.StoreFrontCode)); if (!response.IsSuccess) return AdapterResult<InvoiceDeliveryResult>.Failure(response.Error!, response.RateLimit);
            return AdapterResult<InvoiceDeliveryResult>.Success(new(command.ExternalPackageId, "SUBMITTED"), response.RateLimit);
        }
    }

    internal const string InvoiceLinkBodyInvalidCode = "TRENDYOL_INVOICE_LINK_BODY_INVALID";

    // The documented "send-invoice-link" body: shipmentPackageId as a number (int64), invoiceLink, and the optional
    // invoiceDateTime and invoiceNumber (GİB format, three letters/digits and thirteen digits). The panel's own fields
    // (micro, the other marketplaces' keys) are not sent. Null when the package number does not fit, or when a
    // micro-export invoice number is not in GİB format.
    internal static Dictionary<string, object>? InvoiceLinkBody(JsonElement payload)
    {
        var packageText = payload.TryGetProperty("shipmentPackageId", out var packageValue)
            ? packageValue.ValueKind == JsonValueKind.Number ? packageValue.GetRawText() : packageValue.ValueKind == JsonValueKind.String ? packageValue.GetString()?.Trim() : null
            : null;
        if (!long.TryParse(packageText, NumberStyles.None, CultureInfo.InvariantCulture, out var shipmentPackageId) || shipmentPackageId <= 0) return null;
        if (!payload.TryGetProperty("invoiceLink", out var link) || link.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(link.GetString())) return null;
        var body = new Dictionary<string, object> { ["shipmentPackageId"] = shipmentPackageId, ["invoiceLink"] = link.GetString()! };
        // O9: a null invoiceDateTime is left out of the body (TryGetInt64 would throw on it).
        if (RemoteJson.Int64(payload, "invoiceDateTime") is { } issuedAtMilliseconds)
            body["invoiceDateTime"] = issuedAtMilliseconds;
        if (payload.TryGetProperty("invoiceNumber", out var number) && number.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(number.GetString()))
        {
            var invoiceNumber = number.GetString()!.Trim();
            // The number is optional for a domestic package: one Trendyol would refuse (a manual upload numbered
            // "2026/0045") is left out and the link is still sent. Micro-export requires a valid number.
            if (InvoiceIssuePolicy.IsGibInvoiceNumber(invoiceNumber)) body["invoiceNumber"] = invoiceNumber;
            else if (IsMicroExportPayload(payload)) return null;
        }
        return body;
    }

    private static bool IsMicroExportPayload(JsonElement payload) => payload.TryGetProperty("micro", out var micro) && micro.ValueKind == JsonValueKind.True;

    public async Task<AdapterResult<InvoiceDeliveryStatus>> QueryDeliveryAsync(AdapterContext context, ExternalInvoiceDeliveryReference reference, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(reference.ExternalReference))
            return AdapterResult<InvoiceDeliveryStatus>.Failure(TrendyolErrorMapper.Contract());
        // With the order's date and storefront the package is read in its own window and storefront (one or two
        // requests); without them every storefront is tried in the last 14 days only.
        var package = await ReadShipmentPackageAsync(context, reference.ExternalReference, reference.Lookup ?? new PackageLookup(null), cancellationToken);
        if (!package.IsSuccess) return AdapterResult<InvoiceDeliveryStatus>.Failure(package.Error!, package.RateLimit);
        var invoice = package.Value?.Package.Invoice;
        if (invoice is null || string.IsNullOrWhiteSpace(invoice.RawStatus))
            return AdapterResult<InvoiceDeliveryStatus>.Failure(new(AdapterErrorClass.ContractViolation,
                "TRENDYOL_INVOICE_STATUS_UNAVAILABLE", "Trendyol yanıtı paket için açık fatura durumu içermiyor; mali kesim güvenle başlatılamaz.", 502, null, null), package.RateLimit);
        return AdapterResult<InvoiceDeliveryStatus>.Success(new(reference.ExternalReference, invoice.RawStatus,
            MarketplaceInvoiceStatePolicy.FromRemote(invoice.RawStatus) != MarketplaceInvoiceStatus.Unknown,
            invoice.InvoiceNumber, invoice.InvoiceUrl, invoice.ReferencesAreAuthoritative), package.RateLimit);
    }

    // Removes the link a cancelled invoice left on a package ("Fatura Linki Silme"). Only on the operator's request
    // (an explicit manual invoice write through the same gates as delivery), and only when the package is read
    // first and holds this invoice's link or number; the package is read again to prove the link is gone.
    public async Task<AdapterResult<InvoiceLinkRevocationResult>> RevokeLinkAsync(AdapterContext context, InvoiceLinkRevocationCommand command, CancellationToken cancellationToken)
    {
        var authorized = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken);
        if (authorized is null) return AdapterResult<InvoiceLinkRevocationResult>.Failure(TrendyolErrorMapper.Configuration());
        return await RevokeLinkAsync(authorized, context, command, cancellationToken);
    }

    internal async Task<AdapterResult<InvoiceLinkRevocationResult>> RevokeLinkAsync(TrendyolRequestContext authorized, AdapterContext context, InvoiceLinkRevocationCommand command, CancellationToken cancellationToken)
    {
        if (!CanWrite(authorized, context)
            || !IntegrationRuntimePolicy.AllowsExplicitManualInvoiceMarketplaceDelivery(authorized.Connection, context, GlobalWritesEnabled, authorized.ExternalWritesEnabled))
            return AdapterResult<InvoiceLinkRevocationResult>.Failure(TrendyolErrorMapper.WriteClosed());
        var packageId = command.Reference.ExternalReference?.Trim();
        if (string.IsNullOrWhiteSpace(packageId) || !long.TryParse(packageId, NumberStyles.None, CultureInfo.InvariantCulture, out var shipmentPackageId))
            return AdapterResult<InvoiceLinkRevocationResult>.Failure(new(AdapterErrorClass.Validation, "TRENDYOL_SHIPMENT_PACKAGE_ID_INVALID",
                "Trendyol paket numarası sayısal olmalıdır; silme isteği gönderilmedi.", null, null, null));
        var lookup = (command.Reference.Lookup ?? new PackageLookup(null)) with { InvoiceOnly = true };

        var before = await ReadShipmentPackageAsync(authorized, packageId, lookup, cancellationToken);
        if (!before.IsSuccess) return AdapterResult<InvoiceLinkRevocationResult>.Failure(before.Error!, before.RateLimit);
        var current = LinkPresence(before.Value!);
        if (current.Presence == InvoiceLinkPresence.Absent)
            return AdapterResult<InvoiceLinkRevocationResult>.Success(new(packageId, true, current.RawStatus), before.RateLimit);
        if (current.Presence == InvoiceLinkPresence.Unknown)
            return AdapterResult<InvoiceLinkRevocationResult>.Failure(new(AdapterErrorClass.ContractViolation, "TRENDYOL_INVOICE_STATUS_UNAVAILABLE",
                "Trendyol paketinin fatura durumu okunamadı; silme isteği gönderilmedi.", 502, null, null), before.RateLimit);
        if (!HoldsThisInvoice(before.Value!.Package.Invoice, command))
            return AdapterResult<InvoiceLinkRevocationResult>.Failure(new(AdapterErrorClass.BusinessConflict, MarketplaceInvoiceLinkRevocationPolicy.NotOursCode,
                "Trendyol paketindeki fatura bu faturaya ait görünmüyor; başka bir faturanın linki silinmesin diye istek gönderilmedi.", 409, null, null), before.RateLimit);
        if (CustomerId(before.Value!) is not { } customerId)
            return AdapterResult<InvoiceLinkRevocationResult>.Failure(new(AdapterErrorClass.ContractViolation, "TRENDYOL_CUSTOMER_ID_MISSING",
                "Trendyol paketi müşteri numarasını (customerId) içermiyor; silme isteği gönderilmedi.", 502, null, null), before.RateLimit);

        var deleted = await SendAsync(authorized, HttpMethod.Post, TrendyolEndpoints.InvoiceLinkDelete(authorized.Connection.ExternalStoreId),
            JsonContent.Create(new { serviceSourceId = shipmentPackageId, channelId = 1, customerId }), cancellationToken, storeFrontCode: WriteStoreFront(command.Reference.Lookup?.Order?.StoreFrontCode));
        // 404 is "no invoice link found": the read below decides whether the package is clear.
        if (!deleted.IsSuccess && deleted.Error!.HttpStatus != 404)
            return AdapterResult<InvoiceLinkRevocationResult>.Failure(deleted.Error, deleted.RateLimit);

        var after = await ReadShipmentPackageAsync(authorized, packageId, lookup, cancellationToken);
        if (!after.IsSuccess)
            return AdapterResult<InvoiceLinkRevocationResult>.Failure(new(after.Error!.Class, MarketplaceInvoiceLinkRevocationPolicy.ReadbackPendingCode,
                "Trendyol silme isteği gönderildi ancak paket yeniden okunamadı. Birkaç dakika sonra tekrar deneyin; panel önce paketi okur.", after.Error.HttpStatus, after.Error.RetryAfter, after.Error.RemoteRequestId), after.RateLimit ?? deleted.RateLimit);
        var removed = LinkPresence(after.Value!);
        if (removed.Presence == InvoiceLinkPresence.Absent)
            return AdapterResult<InvoiceLinkRevocationResult>.Success(new(packageId, false, removed.RawStatus), after.RateLimit ?? deleted.RateLimit);
        if (!deleted.IsSuccess)
            return AdapterResult<InvoiceLinkRevocationResult>.Failure(new(AdapterErrorClass.NotFound, "TRENDYOL_INVOICE_LINK_NOT_FOUND",
                "Trendyol bu pakette silinecek bir fatura linki bulamadı ama paket faturalı görünüyor. Faturayı Trendyol Satıcı Paneli’nden kaldırın.", 404, null, deleted.Error!.RemoteRequestId), after.RateLimit ?? deleted.RateLimit);
        return AdapterResult<InvoiceLinkRevocationResult>.Failure(new(AdapterErrorClass.BusinessConflict, MarketplaceInvoiceLinkRevocationPolicy.ReadbackPendingCode,
            "Trendyol silme isteğini kabul etti ancak paket hâlâ faturalı görünüyor. Birkaç dakika sonra tekrar deneyin; panel önce paketi okur.", 409, TimeSpan.FromMinutes(2), null), after.RateLimit ?? deleted.RateLimit);
    }

    internal enum InvoiceLinkPresence { Present, Absent, Unknown }

    // invoiceStatus decides; without it, an observation that carries both reference fields empty means no invoice.
    internal static (InvoiceLinkPresence Presence, string RawStatus) LinkPresence(RemoteOrderPackage package)
    {
        var invoice = package.Package.Invoice;
        var raw = invoice?.RawStatus?.Trim();
        switch (MarketplaceInvoiceStatePolicy.FromRemote(raw))
        {
            case MarketplaceInvoiceStatus.NotInvoiced: return (InvoiceLinkPresence.Absent, raw!);
            case MarketplaceInvoiceStatus.Invoiced or MarketplaceInvoiceStatus.Received or MarketplaceInvoiceStatus.Rejected: return (InvoiceLinkPresence.Present, raw!);
        }
        if (!string.IsNullOrWhiteSpace(invoice?.InvoiceUrl)) return (InvoiceLinkPresence.Present, "LINK_PRESENT");
        return invoice is { ReferencesAreAuthoritative: true } && string.IsNullOrWhiteSpace(invoice.InvoiceNumber)
            ? (InvoiceLinkPresence.Absent, "NO_INVOICE_REFERENCES")
            : (InvoiceLinkPresence.Unknown, raw ?? "UNKNOWN");
    }

    internal static bool HoldsThisInvoice(RemotePackageInvoiceObservation? observed, InvoiceLinkRevocationCommand command)
    {
        var url = observed?.InvoiceUrl?.Trim().TrimEnd('/');
        var number = observed?.InvoiceNumber?.Trim();
        var urlMatches = !string.IsNullOrWhiteSpace(url)
            && command.ExpectedInvoiceLinks.Any(link => string.Equals(link.Trim().TrimEnd('/'), url, StringComparison.OrdinalIgnoreCase));
        var numberMatches = !string.IsNullOrWhiteSpace(number) && !string.IsNullOrWhiteSpace(command.ExpectedInvoiceNumber)
            && string.Equals(number, command.ExpectedInvoiceNumber.Trim(), StringComparison.Ordinal);
        // A link or a number that names another invoice wins over a partial match: a package with the same number but
        // another link (or the same link but another number) holds a live invoice that is not this one.
        if (!string.IsNullOrWhiteSpace(url) && !urlMatches) return false;
        if (!string.IsNullOrWhiteSpace(number) && !string.IsNullOrWhiteSpace(command.ExpectedInvoiceNumber) && !numberMatches) return false;
        return urlMatches || numberMatches;
    }

    // Trendyol's package (Order V2 and the stream) carries the customer number the delete request needs.
    internal static long? CustomerId(RemoteOrderPackage package)
    {
        foreach (var json in new[] { package.OrderSnapshot?.RawJson, package.OrderSnapshot?.CustomerSnapshotJson })
        {
            if (string.IsNullOrWhiteSpace(json)) continue;
            try
            {
                using var document = JsonDocument.Parse(json);
                if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("customerId", out var value)) continue;
                if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) && number > 0) return number;
                if (value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed > 0) return parsed;
            }
            catch (JsonException) { }
        }
        return null;
    }
}
