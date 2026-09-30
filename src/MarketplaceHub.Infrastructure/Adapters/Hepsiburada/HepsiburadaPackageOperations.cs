using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using MarketplaceHub.Application;
using Microsoft.Extensions.Logging;

namespace MarketplaceHub.Infrastructure.Adapters.Hepsiburada;

public sealed partial class HepsiburadaHttpClient
{
    public async Task<AdapterResult<PackageActionResult>> ExecutePackageActionAsync(AdapterContext context, PackageActionCommand command, CancellationToken cancellationToken)
    {
        if (!string.Equals(settings.AuthenticationMode, "BASIC", StringComparison.OrdinalIgnoreCase))
            return await Unsupported<PackageActionResult>("Hepsiburada auth biçimi SIT hesabında doğrulanana kadar paket aksiyonu gönderilmedi.");
        var account = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken);
        if (account is null) return Failure<PackageActionResult>(AdapterErrorClass.Authentication, "HEPSIBURADA_CREDENTIAL_INVALID", "Hepsiburada bağlantı bilgileri bulunamadı.", HttpStatusCode.Unauthorized);
        if (string.IsNullOrWhiteSpace(command.ExternalPackageId) || command.ExternalPackageId.Any(char.IsWhiteSpace))
            return Failure<PackageActionResult>(AdapterErrorClass.Validation, "HEPSIBURADA_PACKAGE_ID_INVALID", "Paket numarası geçersiz.", HttpStatusCode.BadRequest);
        if (!IntegrationRuntimePolicy.AllowsExternalWrite(account.Connection, context, GlobalWritesEnabled, ConnectionWritesEnabled(account.Connection.SettingsJson))
            || !await authentication.HasVerifiedWriteEvidenceAsync(account.Connection, cancellationToken, MarketplaceCapabilities.ShipmentWrite))
            return await Unsupported<PackageActionResult>("Hepsiburada paket işlemleri mevcut dış yazma kapıları ve mağaza/ortam kapsamlı SHIPMENT_WRITE kanıtı gerektirir.");

        var action = command.Action.Trim().ToUpperInvariant();
        var endpoint = action switch
        {
            "UNPACK" => UnpackPackage(account.Connection.ExternalStoreId, command.ExternalPackageId),
            "CHANGE_CARGO_PROVIDER" => ChangeCargoCompany(account.Connection.ExternalStoreId, command.ExternalPackageId),
            _ => null
        };
        if (endpoint is null) return Failure<PackageActionResult>(AdapterErrorClass.Validation, "HEPSIBURADA_PACKAGE_ACTION_UNSUPPORTED", "Hepsiburada için yalnız UNPACK ve CHANGE_CARGO_PROVIDER desteklenir.", HttpStatusCode.BadRequest);

        HttpMethod method;
        HttpContent? content;
        if (action == "UNPACK")
        {
            method = HttpMethod.Post;
            content = new StringContent("{}", Encoding.UTF8, "application/json");
        }
        else
        {
            string? shortName;
            try
            {
                using var payload = JsonDocument.Parse(command.PayloadJson);
                shortName = Text(payload.RootElement, "CargoCompanyShortName", "cargoCompanyShortName")?.Trim();
            }
            catch (JsonException)
            {
                return Failure<PackageActionResult>(AdapterErrorClass.Validation, "HEPSIBURADA_CARGO_COMPANY_PAYLOAD_INVALID", "Kargo firması payloadı geçerli JSON olmalıdır.", HttpStatusCode.BadRequest);
            }
            if (string.IsNullOrWhiteSpace(shortName) || shortName.Length > 80 || shortName.Any(char.IsWhiteSpace))
                return Failure<PackageActionResult>(AdapterErrorClass.Validation, "HEPSIBURADA_CARGO_COMPANY_REQUIRED", "CargoCompanyShortName zorunludur.", HttpStatusCode.BadRequest);
            var carriers = await GetChangeableCargoCompaniesAsync(context, command.ExternalPackageId, cancellationToken);
            if (!carriers.IsSuccess) return AdapterResult<PackageActionResult>.Failure(carriers.Error!, carriers.RateLimit);
            if (!carriers.Value!.Any(carrier => string.Equals(carrier.ShortName, shortName, StringComparison.OrdinalIgnoreCase)))
                return Failure<PackageActionResult>(AdapterErrorClass.Validation, "HEPSIBURADA_CARGO_COMPANY_NOT_ALLOWED", "Seçilen kargo firması bu paket için Hepsiburada tarafından sunulmuyor.", HttpStatusCode.BadRequest);
            method = HttpMethod.Put;
            content = new StringContent(JsonSerializer.Serialize(new { CargoCompanyShortName = shortName }), Encoding.UTF8, "application/json");
        }

        var response = await SendAsync(account, account.OmsBaseAddress, method, endpoint, content, cancellationToken);
        if (!response.IsSuccess) return AdapterResult<PackageActionResult>.Failure(response.Error!, response.RateLimit);
        return AdapterResult<PackageActionResult>.Success(new(command.ExternalPackageId, action, null), response.RateLimit);
    }

    public async Task<AdapterResult<IReadOnlyList<RemoteCargoCompany>>> GetChangeableCargoCompaniesAsync(AdapterContext context, string externalPackageId, CancellationToken cancellationToken)
    {
        if (!string.Equals(settings.AuthenticationMode, "BASIC", StringComparison.OrdinalIgnoreCase))
            return await Unsupported<IReadOnlyList<RemoteCargoCompany>>("Hepsiburada auth biçimi SIT hesabında doğrulanana kadar kargo firmaları okunmadı.");
        var account = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken);
        if (account is null) return Failure<IReadOnlyList<RemoteCargoCompany>>(AdapterErrorClass.Authentication, "HEPSIBURADA_CREDENTIAL_INVALID", "Hepsiburada bağlantı bilgileri bulunamadı.", HttpStatusCode.Unauthorized);
        if (!IntegrationRuntimePolicy.AllowsManualRead(account.Connection)) return await Unsupported<IReadOnlyList<RemoteCargoCompany>>("Hepsiburada kargo firmaları yalnız etkin veya doğrulanmış bağlantıdan okunabilir.");
        if (string.IsNullOrWhiteSpace(externalPackageId) || externalPackageId.Any(char.IsWhiteSpace)) return Failure<IReadOnlyList<RemoteCargoCompany>>(AdapterErrorClass.Validation, "HEPSIBURADA_PACKAGE_ID_INVALID", "Paket numarası geçersiz.", HttpStatusCode.BadRequest);
        var response = await SendAsync(account, account.OmsBaseAddress, HttpMethod.Get, ChangeableCargoCompanies(account.Connection.ExternalStoreId, externalPackageId), cancellationToken);
        if (!response.IsSuccess) return AdapterResult<IReadOnlyList<RemoteCargoCompany>>.Failure(response.Error!, response.RateLimit);
        try { return AdapterResult<IReadOnlyList<RemoteCargoCompany>>.Success(HepsiburadaJsonMapper.ChangeableCargoCompanies(response.Value!.RootElement), response.RateLimit); }
        catch (JsonException) { return Failure<IReadOnlyList<RemoteCargoCompany>>(AdapterErrorClass.ContractViolation, "HEPSIBURADA_CARGO_COMPANY_CONTRACT_INVALID", "Hepsiburada değiştirilebilir kargo firması yanıtı beklenen sözleşmeyle eşleşmiyor.", HttpStatusCode.BadGateway); }
    }

    public async Task<AdapterResult<IReadOnlyList<RemotePackageableLine>>> GetPackageableLineItemsAsync(AdapterContext context, string externalLineItemId, CancellationToken cancellationToken)
    {
        if (!string.Equals(settings.AuthenticationMode, "BASIC", StringComparison.OrdinalIgnoreCase))
            return await Unsupported<IReadOnlyList<RemotePackageableLine>>("Hepsiburada auth biçimi SIT hesabında doğrulanana kadar paketlenebilir kalemler okunmadı.");
        var account = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken);
        if (account is null) return Failure<IReadOnlyList<RemotePackageableLine>>(AdapterErrorClass.Authentication, "HEPSIBURADA_CREDENTIAL_INVALID", "Hepsiburada bağlantı bilgileri bulunamadı.", HttpStatusCode.Unauthorized);
        if (!IntegrationRuntimePolicy.AllowsManualRead(account.Connection)) return await Unsupported<IReadOnlyList<RemotePackageableLine>>("Hepsiburada paketlenebilir kalemleri yalnız etkin veya doğrulanmış bağlantıdan okunabilir.");
        if (string.IsNullOrWhiteSpace(externalLineItemId) || externalLineItemId.Any(char.IsWhiteSpace)) return Failure<IReadOnlyList<RemotePackageableLine>>(AdapterErrorClass.Validation, "HEPSIBURADA_LINE_ITEM_ID_INVALID", "Sipariş kalemi kimliği geçersiz.", HttpStatusCode.BadRequest);
        var response = await SendAsync(account, account.OmsBaseAddress, HttpMethod.Get, PackageableLineItems(account.Connection.ExternalStoreId, externalLineItemId), cancellationToken);
        if (!response.IsSuccess) return AdapterResult<IReadOnlyList<RemotePackageableLine>>.Failure(response.Error!, response.RateLimit);
        try { return AdapterResult<IReadOnlyList<RemotePackageableLine>>.Success(HepsiburadaJsonMapper.PackageableLineItems(response.Value!.RootElement), response.RateLimit); }
        catch (JsonException) { return Failure<IReadOnlyList<RemotePackageableLine>>(AdapterErrorClass.ContractViolation, "HEPSIBURADA_PACKAGEABLE_LINES_CONTRACT_INVALID", "Hepsiburada paketlenebilir kalem yanıtı beklenen sözleşmeyle eşleşmiyor.", HttpStatusCode.BadGateway); }
    }

    public async Task<AdapterResult<CreateOrderPackageResult>> CreateOrderPackageAsync(AdapterContext context, CreateOrderPackageCommand command, CancellationToken cancellationToken)
    {
        if (!string.Equals(settings.AuthenticationMode, "BASIC", StringComparison.OrdinalIgnoreCase))
            return await Unsupported<CreateOrderPackageResult>("Hepsiburada auth biçimi SIT hesabında doğrulanana kadar paket oluşturulmadı.");
        var account = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken);
        if (account is null) return Failure<CreateOrderPackageResult>(AdapterErrorClass.Authentication, "HEPSIBURADA_CREDENTIAL_INVALID", "Hepsiburada bağlantı bilgileri bulunamadı.", HttpStatusCode.Unauthorized);
        if (!IntegrationRuntimePolicy.AllowsExternalWrite(account.Connection, context, GlobalWritesEnabled, ConnectionWritesEnabled(account.Connection.SettingsJson))
            || !await authentication.HasVerifiedWriteEvidenceAsync(account.Connection, cancellationToken, MarketplaceCapabilities.ShipmentWrite))
            return await Unsupported<CreateOrderPackageResult>("Hepsiburada paket oluşturma mevcut dış yazma kapıları ve mağaza/ortam kapsamlı SHIPMENT_WRITE kanıtı gerektirir.");
        if (!ValidOrderPackageCommand(command)) return Failure<CreateOrderPackageResult>(AdapterErrorClass.Validation, "HEPSIBURADA_PACKAGE_CREATE_INPUT_INVALID", "Hepsiburada paket alanları veya satırları geçersiz.", HttpStatusCode.BadRequest);

        var packageable = await GetPackageableLineItemsAsync(context, command.LineItems[0].LineItemId, cancellationToken);
        // The eligible-lines response may omit the seed line; retain it while
        // checking the additional selected lines.
        if (!packageable.IsSuccess && packageable.Error?.Class != AdapterErrorClass.NotFound)
            return AdapterResult<CreateOrderPackageResult>.Failure(packageable.Error!, packageable.RateLimit);
        // Hepsiburada uses 404 when the seed has no compatible companions; it
        // remains valid to package the seed alone.
        var eligibleLines = packageable.IsSuccess ? packageable.Value! : Array.Empty<RemotePackageableLine>();
        var eligible = eligibleLines.Select(line => line.LineItemId).Append(command.LineItems[0].LineItemId).ToHashSet(StringComparer.Ordinal);
        if (command.LineItems.Skip(1).Any(line => !eligible.Contains(line.LineItemId)))
            return Failure<CreateOrderPackageResult>(AdapterErrorClass.Validation, "HEPSIBURADA_PACKAGE_LINES_NOT_COMPATIBLE", "Seçili kalemlerden biri Hepsiburada yanıtına göre aynı pakete eklenemez.", HttpStatusCode.BadRequest);

        var body = CreatePackagePayload(command);
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        var response = await SendAsync(account, account.OmsBaseAddress, HttpMethod.Post, CreatePackage(account.Connection.ExternalStoreId), content, cancellationToken);
        if (!response.IsSuccess) return AdapterResult<CreateOrderPackageResult>.Failure(response.Error!, response.RateLimit);
        try { return AdapterResult<CreateOrderPackageResult>.Success(new(HepsiburadaJsonMapper.PackageNumber(response.Value!.RootElement)), response.RateLimit); }
        catch (JsonException) { return Failure<CreateOrderPackageResult>(AdapterErrorClass.ContractViolation, "HEPSIBURADA_PACKAGE_CREATE_CONTRACT_INVALID", "Hepsiburada paket oluşturma yanıtında packageNumber yok.", HttpStatusCode.BadGateway); }
    }

    public Task<AdapterResult<bool>> CreateCommonLabelAsync(AdapterContext context, CommonLabelRequest request, CancellationToken cancellationToken) =>
        Unsupported<bool>("Hepsiburada paket etiketi endpoint'i salt okunur; etiket oluşturma POST işlemi yoktur.");

    public async Task<AdapterResult<CommonLabelDocument>> GetCommonLabelAsync(AdapterContext context, string cargoTrackingNumber, CancellationToken cancellationToken, string format = "ZPL")
    {
        if (!string.Equals(settings.AuthenticationMode, "BASIC", StringComparison.OrdinalIgnoreCase))
            return await Unsupported<CommonLabelDocument>("Hepsiburada auth biçimi SIT hesabında doğrulanana kadar etiket okunmadı.");
        var account = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken);
        if (account is null) return Failure<CommonLabelDocument>(AdapterErrorClass.Authentication, "HEPSIBURADA_CREDENTIAL_INVALID", "Hepsiburada bağlantı bilgileri bulunamadı.", HttpStatusCode.Unauthorized);
        if (!IntegrationRuntimePolicy.AllowsManualRead(account.Connection)) return await Unsupported<CommonLabelDocument>("Hepsiburada etiketi yalnız etkin veya doğrulanmış bağlantıdan okunabilir.");
        if (string.IsNullOrWhiteSpace(cargoTrackingNumber) || cargoTrackingNumber.Any(char.IsWhiteSpace)) return Failure<CommonLabelDocument>(AdapterErrorClass.Validation, "HEPSIBURADA_PACKAGE_ID_INVALID", "Paket numarası geçersiz.", HttpStatusCode.BadRequest);
        var normalizedFormat = (format ?? string.Empty).Trim().ToUpperInvariant();
        if (normalizedFormat is not ("ZPL" or "BASE64ZPL" or "PDF" or "PNG" or "JPG"))
            return Failure<CommonLabelDocument>(AdapterErrorClass.Validation, "HEPSIBURADA_LABEL_FORMAT_UNSUPPORTED", "Etiket biçimi ZPL, BASE64ZPL, PDF, PNG veya JPG olmalıdır.", HttpStatusCode.BadRequest);
        var response = await SendBytesAsync(account, account.OmsBaseAddress, PackageLabel(account.Connection.ExternalStoreId, cargoTrackingNumber, normalizedFormat), cancellationToken);
        if (!response.IsSuccess) return AdapterResult<CommonLabelDocument>.Failure(response.Error!, response.RateLimit);
        if (response.Value!.Length is 0 or > 5 * 1024 * 1024) return Failure<CommonLabelDocument>(AdapterErrorClass.ContractViolation, "HEPSIBURADA_LABEL_CONTRACT_INVALID", "Hepsiburada etiket içeriği boş veya 5 MiB sınırını aşıyor.", HttpStatusCode.BadGateway);
        return AdapterResult<CommonLabelDocument>.Success(new(cargoTrackingNumber, normalizedFormat, response.Value), response.RateLimit);
    }

    internal static string ChangeableCargoCompanies(string merchantId, string packageNumber) =>
        $"packages/merchantid/{Uri.EscapeDataString(merchantId)}/packagenumber/{Uri.EscapeDataString(packageNumber)}/changablecargocompanies";

    internal static string ChangeCargoCompany(string merchantId, string packageNumber) =>
        $"packages/merchantid/{Uri.EscapeDataString(merchantId)}/packagenumber/{Uri.EscapeDataString(packageNumber)}/changecargocompany";

    internal static string UnpackPackage(string merchantId, string packageNumber) =>
        $"packages/merchantid/{Uri.EscapeDataString(merchantId)}/packagenumber/{Uri.EscapeDataString(packageNumber)}/unpack";

    internal static string PackageableLineItems(string merchantId, string lineItemId) =>
        $"lineitems/merchantid/{Uri.EscapeDataString(merchantId)}/packageablewith/lineitemid/{Uri.EscapeDataString(lineItemId)}";

    internal static string CreatePackage(string merchantId) => $"packages/merchantid/{Uri.EscapeDataString(merchantId)}";
    internal static string PackageLabel(string merchantId, string packageNumber, string format = "ZPL") =>
        $"packages/merchantid/{Uri.EscapeDataString(merchantId)}/packagenumber/{Uri.EscapeDataString(packageNumber)}/labels?format={Uri.EscapeDataString(format.Trim().ToLowerInvariant())}";

    internal static string CreatePackagePayload(CreateOrderPackageCommand command) => JsonSerializer.Serialize(new
    {
        barcode = command.Barcode.Trim(),
        cargoCompany = command.CargoCompany.Trim(),
        carrier = command.Carrier.Trim(),
        creationReason = command.CreationReason.Trim(),
        deci = command.Deci,
        lineItemRequests = command.LineItems.Select(line => new { id = line.LineItemId, quantity = line.Quantity, serialNumbers = Array.Empty<string>() }).ToArray(),
        parcelQuantity = command.ParcelQuantity,
        warehouse = new { shippingAddressLabel = command.ShippingAddressLabel.Trim(), shippingModel = command.ShippingModel.Trim() }
    });

    internal static bool ValidOrderPackageCommand(CreateOrderPackageCommand command)
    {
        static bool RequiredText(string value, int max) => !string.IsNullOrWhiteSpace(value) && value == value.Trim() && value.Length <= max;
        if (!RequiredText(command.Barcode, 128) || command.Barcode.Any(char.IsWhiteSpace)
            || !RequiredText(command.CargoCompany, 100) || !RequiredText(command.Carrier, 100)
            || !RequiredText(command.CreationReason, 100) || !RequiredText(command.ShippingAddressLabel, 100)
            || !RequiredText(command.ShippingModel, 100) || command.Deci is < 0 or > 10000
            || command.ParcelQuantity is < 1 or > 100 || command.LineItems is null || command.LineItems.Count is < 1 or > 100)
            return false;
        return command.LineItems.All(line => !string.IsNullOrWhiteSpace(line.LineItemId) && line.LineItemId == line.LineItemId.Trim() && !line.LineItemId.Any(char.IsWhiteSpace) && line.Quantity is > 0 and <= 10000)
            && command.LineItems.Select(line => line.LineItemId).Distinct(StringComparer.Ordinal).Count() == command.LineItems.Count;
    }

    private async Task<AdapterResult<byte[]>> SendBytesAsync(HepsiburadaRequestContext context, Uri baseAddress, string path, CancellationToken cancellationToken)
    {
        if (!string.Equals(settings.AuthenticationMode, "BASIC", StringComparison.OrdinalIgnoreCase))
            return AdapterResult<byte[]>.Failure(new(AdapterErrorClass.NotSupported, "HEPSIBURADA_AUTHENTICATION_UNVERIFIED", "Hepsiburada auth biçimi SIT hesabında doğrulanana kadar bağlantı isteği gönderilmedi.", null, null, null));
        var client = clients.CreateClient("Hepsiburada");
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(baseAddress, path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{context.Username}:{context.Password}")));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/pdf"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("image/png"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("image/jpeg"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/plain"));
        request.Headers.UserAgent.ParseAdd("MarketplaceHub/1.0");
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(settings.Timeout > TimeSpan.Zero && settings.Timeout < TimeSpan.FromMinutes(2) ? settings.Timeout : TimeSpan.FromSeconds(30));
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            var rate = RateLimit(response);
            var requestId = response.Headers.TryGetValues("X-Request-Id", out var ids) ? ids.FirstOrDefault() : null;
            if (!response.IsSuccessStatusCode)
            {
                var retryAfter = response.Headers.RetryAfter?.Delta ?? (response.Headers.RetryAfter?.Date is { } retryDate ? retryDate - timeProvider.GetUtcNow() : null);
                var error = Error(response.StatusCode, retryAfter ?? rate?.RetryAfter, requestId);
                return AdapterResult<byte[]>.Failure(error, rate);
            }
            if (response.Content.Headers.ContentLength is > 5 * 1024 * 1024)
                return AdapterResult<byte[]>.Failure(new(AdapterErrorClass.ContractViolation, "HEPSIBURADA_LABEL_TOO_LARGE", "Hepsiburada etiket içeriği 5 MiB sınırını aşıyor.", (int)response.StatusCode, null, requestId), rate);
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var output = new MemoryStream();
            var buffer = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(buffer, timeout.Token)) > 0)
            {
                if (output.Length + read > 5 * 1024 * 1024)
                    return AdapterResult<byte[]>.Failure(new(AdapterErrorClass.ContractViolation, "HEPSIBURADA_LABEL_TOO_LARGE", "Hepsiburada etiket içeriği 5 MiB sınırını aşıyor.", (int)response.StatusCode, null, requestId), rate);
                output.Write(buffer, 0, read);
            }
            return AdapterResult<byte[]>.Success(output.ToArray(), rate);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return AdapterResult<byte[]>.Failure(new(AdapterErrorClass.TransientNetwork, "HEPSIBURADA_TIMEOUT", "Hepsiburada isteği zaman aşımına uğradı.", null, TimeSpan.FromSeconds(15), null));
        }
        catch (HttpRequestException exception)
        {
            logger.LogWarning(exception, "Hepsiburada label API isteği başarısız. ConnectionId: {ConnectionId}", context.Connection.Id);
            return AdapterResult<byte[]>.Failure(new(AdapterErrorClass.TransientNetwork, "HEPSIBURADA_NETWORK_ERROR", "Hepsiburada bağlantısı geçici olarak kurulamadı.", null, TimeSpan.FromSeconds(15), null));
        }
    }
}
