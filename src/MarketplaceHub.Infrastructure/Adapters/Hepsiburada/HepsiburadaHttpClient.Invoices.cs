using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Microsoft.Extensions.Configuration;

namespace MarketplaceHub.Infrastructure.Adapters.Hepsiburada;

public sealed partial class HepsiburadaHttpClient
{
    // Waits between the post-delivery reads of the order (about 10 s in all, well inside the job lease).
    internal static readonly TimeSpan[] InvoiceReadbackWaits = [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(5)];

    public async Task<AdapterResult<AdapterPageResult<RemoteMissingInvoicePackage>>> ListMissingInvoicePackagesAsync(AdapterContext context, AdapterPageRequest page, CancellationToken cancellationToken)
    {
        if (!string.Equals(settings.AuthenticationMode, "BASIC", StringComparison.OrdinalIgnoreCase))
            return await Unsupported<AdapterPageResult<RemoteMissingInvoicePackage>>("Hepsiburada eksik fatura listesi için BASIC kimlik doğrulaması gerekir.");
        var account = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken);
        if (account is null) return Failure<AdapterPageResult<RemoteMissingInvoicePackage>>(AdapterErrorClass.Authentication, "HEPSIBURADA_CREDENTIAL_INVALID", "Hepsiburada bağlantı bilgileri bulunamadı.", HttpStatusCode.Unauthorized);
        if (!IntegrationRuntimePolicy.AllowsManualRead(account.Connection))
            return await Unsupported<AdapterPageResult<RemoteMissingInvoicePackage>>("Hepsiburada eksik fatura listesi yalnız etkin veya doğrulanmış bağlantıdan okunabilir.");

        var (offset, limit) = Page(page, settings.PageSize, documentedMaximum: 50);
        var query = $"limit={limit.ToString(CultureInfo.InvariantCulture)}&offset={offset.ToString(CultureInfo.InvariantCulture)}";
        var response = await SendAsync(account, account.OmsBaseAddress, HttpMethod.Get, MissingInvoicePackages(account, query), cancellationToken);
        using var responseDocument = response.Value;
        if (!response.IsSuccess) return AdapterResult<AdapterPageResult<RemoteMissingInvoicePackage>>.Failure(response.Error!, response.RateLimit);

        try
        {
            var (items, totalCount) = HepsiburadaJsonMapper.MissingInvoicePackages(response.Value!.RootElement);
            var nextOffset = offset + limit;
            var hasMore = totalCount is { } total ? nextOffset < total : items.Count >= limit;
            return AdapterResult<AdapterPageResult<RemoteMissingInvoicePackage>>.Success(
                new(items, hasMore ? nextOffset.ToString(CultureInfo.InvariantCulture) : null, hasMore, totalCount),
                response.RateLimit);
        }
        catch (JsonException exception)
        {
            return Failure<AdapterPageResult<RemoteMissingInvoicePackage>>(AdapterErrorClass.ContractViolation, "HEPSIBURADA_MISSING_INVOICE_CONTRACT_INVALID", $"Hepsiburada eksik fatura listesi beklenen sipariş/paket sayfa sözleşmesiyle eşleşmiyor: {exception.Message}", HttpStatusCode.BadGateway);
        }
    }

    public async Task<AdapterResult<AdapterPageResult<RemoteCancelledOrderLine>>> ListCancelledOrderLinesAsync(AdapterContext context, DateTimeOffset since, DateTimeOffset until, AdapterPageRequest page, CancellationToken cancellationToken)
    {
        var account = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken);
        if (account is null) return Failure<AdapterPageResult<RemoteCancelledOrderLine>>(AdapterErrorClass.Authentication, "HEPSIBURADA_CREDENTIAL_INVALID", "Hepsiburada bağlantı bilgileri bulunamadı.", HttpStatusCode.Unauthorized);
        var (offset, limit) = Page(page, settings.PageSize, documentedMaximum: 50);
        var query = string.Join('&',
            $"offset={offset.ToString(CultureInfo.InvariantCulture)}",
            $"limit={limit.ToString(CultureInfo.InvariantCulture)}",
            "begindate=" + Uri.EscapeDataString(HepsiburadaJsonMapper.QueryTime(since)),
            "enddate=" + Uri.EscapeDataString(HepsiburadaJsonMapper.QueryTime(until)));
        var response = await SendAsync(account, account.OmsBaseAddress, HttpMethod.Get, CancelledOrders(account, query), cancellationToken);
        using var responseDocument = response.Value;
        if (!response.IsSuccess) return AdapterResult<AdapterPageResult<RemoteCancelledOrderLine>>.Failure(response.Error!, response.RateLimit);
        try
        {
            var (items, totalCount) = HepsiburadaJsonMapper.CancelledOrderLines(response.Value!.RootElement);
            var nextOffset = offset + limit;
            var hasMore = totalCount is { } total ? nextOffset < total : items.Count >= limit;
            return AdapterResult<AdapterPageResult<RemoteCancelledOrderLine>>.Success(new(items, hasMore ? nextOffset.ToString(CultureInfo.InvariantCulture) : null, hasMore, totalCount), response.RateLimit);
        }
        catch (JsonException)
        {
            return Failure<AdapterPageResult<RemoteCancelledOrderLine>>(AdapterErrorClass.ContractViolation, "HEPSIBURADA_CANCELLED_LIST_CONTRACT_INVALID", "Hepsiburada iptal edilen sipariş listesi beklenen sayfa sözleşmesiyle eşleşmiyor.", HttpStatusCode.BadGateway);
        }
    }
    public async Task<AdapterResult<InvoiceDeliveryResult>> DeliverAsync(AdapterContext context, InvoiceDeliveryCommand command, CancellationToken cancellationToken)
    {
        if (!string.Equals(settings.AuthenticationMode, "BASIC", StringComparison.OrdinalIgnoreCase))
            return await Unsupported<InvoiceDeliveryResult>("Hepsiburada auth biçimi SIT hesabında doğrulanana kadar fatura bağlantısı gönderilmedi.");
        var account = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken);
        if (account is null) return Failure<InvoiceDeliveryResult>(AdapterErrorClass.Authentication, "HEPSIBURADA_CREDENTIAL_INVALID", "Hepsiburada bağlantı bilgileri bulunamadı.", HttpStatusCode.Unauthorized);
        if (!HepsiburadaInvoiceDeliveryPolicy.TryCreate(command, out var invoice, out var validationError))
            return Failure<InvoiceDeliveryResult>(AdapterErrorClass.Validation, "HEPSIBURADA_INVOICE_DELIVERY_INVALID", validationError, HttpStatusCode.BadRequest);
        var regularWriteAllowed = ExternalWriteGate.AllowsRuntimeWrite(account.Connection, context, GlobalWritesEnabled);
        var automaticInvoiceWriteAllowed = IntegrationRuntimePolicy.AllowsAutomaticInvoiceMarketplaceDelivery(
            account.Connection,
            context,
            configuration.GetValue<bool>("FeatureFlags:InvoiceMarketplaceDeliveryWrites"));
        var explicitManualInvoiceWriteAllowed = IntegrationRuntimePolicy.AllowsExplicitManualInvoiceMarketplaceDelivery(
            account.Connection,
            context,
            GlobalWritesEnabled,
            ConnectionWritesEnabled(account.Connection.SettingsJson));
        var oneTimeInvoiceWriteAllowed = IntegrationRuntimePolicy.AllowsOneTimeHepsiburadaInvoiceDelivery(account.Connection, context, invoice!.OrderNumber);
        if (!(regularWriteAllowed && automaticInvoiceWriteAllowed) && !explicitManualInvoiceWriteAllowed && !oneTimeInvoiceWriteAllowed)
            return await Unsupported<InvoiceDeliveryResult>("Hepsiburada fatura teslimi yalnız genel dış yazma izinleri, ayrı fatura iletme izni veya doğrulanmış tek seferlik fatura yetkisi açıkken kullanılabilir.");

        if (context.IsExchangeInvoiceResend)
        {
            // Owner decision (6 Nov 2026): after an exchange the SAME invoice link already on the package is sent again,
            // only through the automatic delivery gate. The package holds an invoice already, so neither the "empty
            // before" check nor the read-back can prove anything here: Hepsiburada's 2xx is the result, a refusal (409
            // too) is a failure the owner sees.
            if (!(regularWriteAllowed && automaticInvoiceWriteAllowed))
                return await Unsupported<InvoiceDeliveryResult>("Değişim sonrası fatura yeniden gönderimi yalnız otomatik fatura iletme izinleri açıkken yapılır.");
            var resendBody = HepsiburadaInvoiceDeliveryPolicy.Body(invoice!);
            using var resendContent = new StringContent(resendBody, Encoding.UTF8, "application/json");
            var resend = await SendAsync(account, account.OmsBaseAddress, HttpMethod.Put, InvoiceLink(account, invoice.PackageNumber), resendContent, cancellationToken);
            using var resendDocument = resend.Value;
            return resend.IsSuccess
                ? AdapterResult<InvoiceDeliveryResult>.Success(new(invoice.PackageNumber, "INVOICED", false, invoice.InvoiceNumber, invoice.InvoiceLink.AbsoluteUri), resend.RateLimit)
                : AdapterResult<InvoiceDeliveryResult>.Failure(resend.Error!, resend.RateLimit);
        }

        // hasInvoice cannot identify which invoice is attached. Only send when
        // the package is observed empty immediately before this request; a
        // successful post-write read then ties the observation to this attempt.
        var before = await QueryDeliveryAsync(context, new(invoice!.PackageNumber, invoice.OrderNumber), cancellationToken);
        if (!before.IsSuccess) return AdapterResult<InvoiceDeliveryResult>.Failure(before.Error!, before.RateLimit);
        if (!string.Equals(before.Value!.RawStatus, "NOT_INVOICED", StringComparison.Ordinal))
            return AdapterResult<InvoiceDeliveryResult>.Failure(new(AdapterErrorClass.BusinessConflict, "HEPSIBURADA_INVOICE_ALREADY_PRESENT", "Hepsiburada paketi faturalı görünüyor; faturanın bu mali belgeyle eşleştiği kanıtlanmadan üzerine yazılmadı.", (int)HttpStatusCode.Conflict, null, null), before.RateLimit);

        var body = HepsiburadaInvoiceDeliveryPolicy.Body(invoice!);
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        var path = InvoiceLink(account, invoice.PackageNumber);
        var response = await SendAsync(account, account.OmsBaseAddress, HttpMethod.Put, path, content, cancellationToken);
        using var responseDocument = response.Value;
        if (!response.IsSuccess && response.Error!.HttpStatus == (int)HttpStatusCode.Conflict)
        {
            // 409: the package already has an invoice (another tool, or an earlier attempt of ours that landed).
            // It is read back and handed to reconciliation instead of being recorded as a failed delivery.
            var current = await QueryDeliveryAsync(context, new(invoice.PackageNumber, invoice.OrderNumber), cancellationToken);
            if (current.IsSuccess && string.Equals(current.Value!.RawStatus, "INVOICED", StringComparison.Ordinal))
                return AdapterResult<InvoiceDeliveryResult>.Failure(new(AdapterErrorClass.BusinessConflict, "HEPSIBURADA_INVOICE_ALREADY_PRESENT", "Hepsiburada paketin zaten faturalı olduğunu bildirdi; fatura eşleşmesi kontrol edilecek.", (int)HttpStatusCode.Conflict, null, response.Error.RemoteRequestId), current.RateLimit ?? response.RateLimit);
        }
        if (!response.IsSuccess) return AdapterResult<InvoiceDeliveryResult>.Failure(response.Error!, response.RateLimit);
        // Hepsiburada answers 204 before the order read shows the invoice (live 20 Oct 2026: still NOT_INVOICED
        // 0.3 s after the PUT, INVOICED two minutes later). The read is repeated a few times before the result
        // counts as unknown and waits for the next attempt.
        var after = await QueryDeliveryAsync(context, new(invoice.PackageNumber, invoice.OrderNumber), cancellationToken);
        foreach (var wait in InvoiceReadbackWaits)
        {
            if (!after.IsSuccess || string.Equals(after.Value!.RawStatus, "INVOICED", StringComparison.Ordinal)) break;
            await Task.Delay(wait, timeProvider, cancellationToken);
            after = await QueryDeliveryAsync(context, new(invoice.PackageNumber, invoice.OrderNumber), cancellationToken);
        }
        if (!after.IsSuccess) return AdapterResult<InvoiceDeliveryResult>.Failure(after.Error!, after.RateLimit);
        if (!string.Equals(after.Value!.RawStatus, "INVOICED", StringComparison.Ordinal))
            return AdapterResult<InvoiceDeliveryResult>.Failure(new(AdapterErrorClass.BusinessConflict, "HEPSIBURADA_INVOICE_DELIVERY_READBACK_MISSING", "Hepsiburada isteği kabul etti ancak pakette fatura bulunduğu doğrulanmadı; gönderim otomatik tekrarlanmayacak.", (int)HttpStatusCode.Conflict, null, null), after.RateLimit ?? response.RateLimit);
        return AdapterResult<InvoiceDeliveryResult>.Success(new(invoice.PackageNumber, "INVOICED", true, invoice.InvoiceNumber, invoice.InvoiceLink.AbsoluteUri), after.RateLimit ?? response.RateLimit);
    }

    public async Task<AdapterResult<InvoiceDeliveryStatus>> QueryDeliveryAsync(AdapterContext context, ExternalInvoiceDeliveryReference reference, CancellationToken cancellationToken)
    {
        if (!string.Equals(settings.AuthenticationMode, "BASIC", StringComparison.OrdinalIgnoreCase))
            return await Unsupported<InvoiceDeliveryStatus>("Hepsiburada auth biçimi SIT hesabında doğrulanana kadar fatura durumu sorgulanmadı.");
        var account = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken);
        if (account is null) return Failure<InvoiceDeliveryStatus>(AdapterErrorClass.Authentication, "HEPSIBURADA_CREDENTIAL_INVALID", "Hepsiburada bağlantı bilgileri bulunamadı.", HttpStatusCode.Unauthorized);
        if (!IntegrationRuntimePolicy.AllowsManualRead(account.Connection))
            return await Unsupported<InvoiceDeliveryStatus>("Hepsiburada fatura durumu yalnız etkin veya doğrulanmış bağlantıdan okunabilir.");
        if (string.IsNullOrWhiteSpace(reference.OrderNumber))
            return Failure<InvoiceDeliveryStatus>(AdapterErrorClass.Validation, "HEPSIBURADA_ORDER_NUMBER_REQUIRED", "Hepsiburada fatura durumu için sipariş numarası zorunludur.", HttpStatusCode.BadRequest);

        var response = await SendAsync(account, account.OmsBaseAddress, HttpMethod.Get, OrderDetails(account, reference.OrderNumber), cancellationToken);
        using var responseDocument = response.Value;
        if (!response.IsSuccess) return AdapterResult<InvoiceDeliveryStatus>.Failure(response.Error!, response.RateLimit);
        try
        {
            var observation = HepsiburadaJsonMapper.InvoiceObservationForPackage(response.Value!.RootElement, reference.ExternalReference, out var infoMissing);
            var rawStatus = observation?.RawStatus;
            if (rawStatus is null && !infoMissing) rawStatus = HepsiburadaJsonMapper.InvoiceStatus(response.Value.RootElement, out infoMissing);
            // Some lines leave the invoice information out: the package's invoice state is unknown, never "not invoiced",
            // so the delivery (and the fiscal precheck) stops for the owner instead of sending a link.
            if (infoMissing)
                return Failure<InvoiceDeliveryStatus>(AdapterErrorClass.ContractViolation, HepsiburadaInvoiceInfoPolicy.LineInvoiceInfoMissingCode, HepsiburadaInvoiceInfoPolicy.Message, HttpStatusCode.BadGateway);
            if (string.IsNullOrWhiteSpace(rawStatus)) throw new JsonException("Hepsiburada yanıtında istenen paket için fatura durumu yok.");
            return AdapterResult<InvoiceDeliveryStatus>.Success(new(
                reference.ExternalReference,
                rawStatus,
                MarketplaceInvoiceStatePolicy.FromRemote(rawStatus) != MarketplaceInvoiceStatus.Unknown,
                observation?.InvoiceNumber,
                observation?.InvoiceUrl,
                observation?.ReferencesAreAuthoritative == true), response.RateLimit);
        }
        catch (JsonException)
        {
            return Failure<InvoiceDeliveryStatus>(AdapterErrorClass.ContractViolation, "HEPSIBURADA_INVOICE_STATUS_CONTRACT_INVALID", "Hepsiburada sipariş yanıtında fatura durumu beklenen hasInvoice alanıyla eşleşmiyor.", HttpStatusCode.BadGateway);
        }
    }
    internal static string CancelledOrders(HepsiburadaRequestContext context, string query) => $"orders/merchantid/{Uri.EscapeDataString(context.Connection.ExternalStoreId)}/cancelled?{query}";
    internal static string MissingInvoicePackages(HepsiburadaRequestContext context, string query) => $"packages/merchantid/{Uri.EscapeDataString(context.Connection.ExternalStoreId)}/missing-invoice?{query}";
    internal static string InvoiceLink(HepsiburadaRequestContext context, string packageNumber) => $"packages/merchantid/{Uri.EscapeDataString(context.Connection.ExternalStoreId)}/packagenumber/{Uri.EscapeDataString(packageNumber)}/invoice";

    internal static string? SafeInvoiceErrorDetails(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            using var document = JsonDocument.Parse(body);
            var details = new List<string>();
            CollectSafeInvoiceErrorFields(document.RootElement, details, 0);
            if (details.Count == 0) return null;
            var safe = string.Join(" | ", details.Distinct(StringComparer.OrdinalIgnoreCase));
            safe = System.Text.RegularExpressions.Regex.Replace(safe, @"https?://\S+", "[url]", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            safe = System.Text.RegularExpressions.Regex.Replace(safe, @"\b[\w.+-]+@[\w.-]+\.[A-Za-z]{2,}\b", "[email]");
            safe = System.Text.RegularExpressions.Regex.Replace(safe, @"(?<!\d)\d{10,11}(?!\d)", "[id]");
            safe = System.Text.RegularExpressions.Regex.Replace(safe, @"[\r\n\t]+", " ").Trim();
            return safe.Length <= 600 ? safe : safe[..600];
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void CollectSafeInvoiceErrorFields(JsonElement element, List<string> details, int depth)
    {
        if (depth > 3 || element.ValueKind != JsonValueKind.Object || details.Count >= 8) return;
        foreach (var property in element.EnumerateObject())
        {
            if (details.Count >= 8) return;
            if (property.Name.Equals("code", StringComparison.OrdinalIgnoreCase)
                || property.Name.Equals("errorCode", StringComparison.OrdinalIgnoreCase)
                || property.Name.Equals("message", StringComparison.OrdinalIgnoreCase)
                || property.Name.Equals("error", StringComparison.OrdinalIgnoreCase)
                || property.Name.Equals("detail", StringComparison.OrdinalIgnoreCase))
            {
                var value = property.Value.ValueKind switch
                {
                    JsonValueKind.String => property.Value.GetString(),
                    JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => property.Value.ToString(),
                    _ => null
                };
                if (!string.IsNullOrWhiteSpace(value)) details.Add($"{property.Name}={value.Trim()}");
            }
            if (property.Value.ValueKind == JsonValueKind.Object)
            {
                CollectSafeInvoiceErrorFields(property.Value, details, depth + 1);
            }
        }
    }
}
