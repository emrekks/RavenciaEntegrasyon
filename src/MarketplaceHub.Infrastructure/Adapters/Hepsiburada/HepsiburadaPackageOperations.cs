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
        return await GetCommonLabelAsync(account, cargoTrackingNumber, format, cancellationToken);
    }

    internal async Task<AdapterResult<CommonLabelDocument>> GetCommonLabelAsync(HepsiburadaRequestContext account, string cargoTrackingNumber, string format, CancellationToken cancellationToken)
    {
        if (!IntegrationRuntimePolicy.AllowsManualRead(account.Connection)) return await Unsupported<CommonLabelDocument>("Hepsiburada etiketi yalnız etkin veya doğrulanmış bağlantıdan okunabilir.");
        if (string.IsNullOrWhiteSpace(cargoTrackingNumber) || cargoTrackingNumber.Any(char.IsWhiteSpace)) return Failure<CommonLabelDocument>(AdapterErrorClass.Validation, "HEPSIBURADA_PACKAGE_ID_INVALID", "Paket numarası geçersiz.", HttpStatusCode.BadRequest);
        var normalizedFormat = (format ?? string.Empty).Trim().ToUpperInvariant();
        if (normalizedFormat is not ("ZPL" or "BASE64ZPL" or "PDF" or "PNG" or "JPG"))
            return Failure<CommonLabelDocument>(AdapterErrorClass.Validation, "HEPSIBURADA_LABEL_FORMAT_UNSUPPORTED", "Etiket biçimi ZPL, BASE64ZPL, PDF, PNG veya JPG olmalıdır.", HttpStatusCode.BadRequest);
        var response = await SendBytesAsync(account, account.OmsBaseAddress, PackageLabel(account.Connection.ExternalStoreId, cargoTrackingNumber, normalizedFormat), cancellationToken);
        if (!response.IsSuccess) return AdapterResult<CommonLabelDocument>.Failure(response.Error!, response.RateLimit);
        // A PDF/PNG/JPG answer holds one file per parcel; only ZPL joins them into one print job.
        // Returning the first file alone would leave the other parcels without a label.
        if (normalizedFormat is "PDF" or "PNG" or "JPG" && LabelParcelCount(response.Value!) is > 1 and var parcels)
            return Failure<CommonLabelDocument>(AdapterErrorClass.Validation, "HEPSIBURADA_LABEL_MULTI_PARCEL", $"Bu paketin {parcels} parça etiketi var; {normalizedFormat} biçimi tek dosya döner. Tüm parçaların etiketi için ZPL veya Base64 ZPL biçimini seçin.", HttpStatusCode.UnprocessableEntity);
        var label = LabelContent(response.Value!, normalizedFormat);
        if (label is null || label.Length is 0 or > 5 * 1024 * 1024) return Failure<CommonLabelDocument>(AdapterErrorClass.ContractViolation, "HEPSIBURADA_LABEL_CONTRACT_INVALID", "Hepsiburada etiket içeriği boş, okunamadı veya 5 MiB sınırını aşıyor.", HttpStatusCode.BadGateway);
        return AdapterResult<CommonLabelDocument>.Success(new(cargoTrackingNumber, normalizedFormat, label), response.RateLimit);
    }

    // How many parcel labels a JSON answer carries (1 for a plain file).
    internal static int LabelParcelCount(byte[] body) => LabelEntries(body)?.Length ?? 1;

    private static string[]? LabelEntries(byte[] body)
    {
        var start = Array.FindIndex(body, value => !char.IsWhiteSpace((char)value));
        if (start < 0 || body[start] != (byte)'{') return null;
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            JsonElement data = default;
            foreach (var name in new[] { "data", "Data", "labels", "Labels" })
                if (root.TryGetProperty(name, out data)) break;
            var entries = data.ValueKind switch
            {
                JsonValueKind.Array => data.EnumerateArray().Where(entry => entry.ValueKind == JsonValueKind.String).Select(entry => entry.GetString()!).ToArray(),
                JsonValueKind.String => [data.GetString()!],
                _ => []
            };
            return entries.Where(entry => !string.IsNullOrWhiteSpace(entry)).ToArray();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    // The label service may answer with the file itself or with JSON {"format": ..., "data": [...]}
    // whose entries are base64 (PDF, PNG, JPG, BASE64ZPL) or ZPL text, one per parcel. ZPL parcels are
    // joined into one print job; a binary format holds one parcel only (several are refused before
    // this). Null when the JSON carries no usable label.
    internal static byte[]? LabelContent(byte[] body, string format)
    {
        var start = Array.FindIndex(body, value => !char.IsWhiteSpace((char)value));
        if (start < 0 || body[start] != (byte)'{') return body;
        try
        {
            var entries = LabelEntries(body) ?? [];
            if (entries.Length == 0) return null;
            if (format is "PDF" or "PNG" or "JPG" && entries.Length > 1) return null;
            if (format == "BASE64ZPL") return System.Text.Encoding.ASCII.GetBytes(string.Join(string.Empty, entries));
            if (format == "ZPL")
                return System.Text.Encoding.UTF8.GetBytes(string.Join('\n', entries.Select(entry =>
                    entry.TrimStart().StartsWith("^XA", StringComparison.Ordinal) ? entry : System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(entry)))));
            return Convert.FromBase64String(entries[0]);
        }
        catch (Exception exception) when (exception is JsonException or FormatException)
        {
            return null;
        }
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

    // Every field but the lines is optional in Hepsiburada's package request; an empty one is left out, and so is the
    // warehouse when neither of its fields is given.
    internal static string CreatePackagePayload(CreateOrderPackageCommand command)
    {
        var payload = new Dictionary<string, object>(StringComparer.Ordinal);
        void Add(string name, string? value) { if (!string.IsNullOrWhiteSpace(value)) payload[name] = value.Trim(); }
        Add("barcode", command.Barcode);
        Add("cargoCompany", command.CargoCompany);
        Add("carrier", command.Carrier);
        Add("creationReason", command.CreationReason);
        payload["deci"] = command.Deci;
        payload["lineItemRequests"] = command.LineItems.Select(line => new { id = line.LineItemId, quantity = line.Quantity, serialNumbers = Array.Empty<string>() }).ToArray();
        payload["parcelQuantity"] = command.ParcelQuantity;
        var warehouse = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(command.ShippingAddressLabel)) warehouse["shippingAddressLabel"] = command.ShippingAddressLabel.Trim();
        if (!string.IsNullOrWhiteSpace(command.ShippingModel)) warehouse["shippingModel"] = command.ShippingModel.Trim();
        if (warehouse.Count > 0) payload["warehouse"] = warehouse;
        return JsonSerializer.Serialize(payload);
    }

    internal static bool ValidOrderPackageCommand(CreateOrderPackageCommand command)
    {
        static bool OptionalText(string? value, int max) => string.IsNullOrEmpty(value) || value == value.Trim() && value.Length <= max;
        if (!OptionalText(command.Barcode, 128) || command.Barcode?.Any(char.IsWhiteSpace) == true
            || !OptionalText(command.CargoCompany, 100) || !OptionalText(command.Carrier, 100)
            || !OptionalText(command.CreationReason, 100) || !OptionalText(command.ShippingAddressLabel, 100)
            || !OptionalText(command.ShippingModel, 100) || command.Deci is < 0 or > 10000
            || command.ParcelQuantity is < 1 or > 100 || command.LineItems is null || command.LineItems.Count is < 1 or > 100)
            return false;
        return command.LineItems.All(line => !string.IsNullOrWhiteSpace(line.LineItemId) && line.LineItemId == line.LineItemId.Trim() && !line.LineItemId.Any(char.IsWhiteSpace) && line.Quantity is > 0 and <= 10000)
            && command.LineItems.Select(line => line.LineItemId).Distinct(StringComparer.Ordinal).Count() == command.LineItems.Count;
    }

    // C17: the label endpoint answers 400 when the package has no common (shared) barcode label, e.g. it does
    // not ship with a carrier that prints Hepsiburada's common label. That explanation is given only when the
    // response itself says so; any other 400 (wrong format, wrong package state...) shows Hepsiburada's own text.
    internal static AdapterError NoCommonLabel(string? requestId) => new(AdapterErrorClass.Validation, "HEPSIBURADA_COMMON_LABEL_UNAVAILABLE",
        "Hepsiburada bu paket için ortak barkodlu etiket vermedi (400). Paketin kargo firması ortak barkod kullanmıyor olabilir; etiketi kargo firmasının sisteminden alın.", 400, null, requestId);

    internal static AdapterError LabelRejected(string body, string? requestId)
    {
        var details = LabelErrorDetails(body);
        if (details is not null && SaysNoCommonLabel(details)) return NoCommonLabel(requestId);
        return new(AdapterErrorClass.Validation, "HEPSIBURADA_LABEL_REJECTED",
            details is null
                ? "Hepsiburada etiket isteğini reddetti (400) ve neden bildirmedi."
                : $"Hepsiburada etiket isteğini reddetti (400): {details}", 400, null, requestId);
    }

    private static readonly string[] CommonLabelPhrases =
        ["ortak barkod", "ortak etiket", "common barcode", "common label", "commonbarcode", "commonlabel", "common_label", "common_barcode"];

    internal static bool SaysNoCommonLabel(string details) =>
        CommonLabelPhrases.Any(phrase => details.Contains(phrase, StringComparison.OrdinalIgnoreCase));

    // Hepsiburada's code/message fields (JSON) or a short plain-text body, with links, e-mails and long ids masked.
    internal static string? LabelErrorDetails(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        var details = HepsiburadaHttpClient.SafeInvoiceErrorDetails(body);
        if (details is not null) return details;
        var text = body.Trim();
        if (text.StartsWith('{') || text.StartsWith('[') || text.StartsWith('<')) return null;
        text = System.Text.RegularExpressions.Regex.Replace(text, @"https?://\S+", "[url]", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\b[\w.+-]+@[\w.-]+\.[A-Za-z]{2,}\b", "[email]");
        text = System.Text.RegularExpressions.Regex.Replace(text, @"(?<!\d)\d{10,11}(?!\d)", "[id]");
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();
        if (text.Length == 0) return null;
        return text.Length <= 300 ? text : text[..300];
    }

    private static async Task<string> ReadBoundedTextAsync(HttpContent content, int maxBytes, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        var buffer = new byte[maxBytes];
        var total = 0;
        int read;
        while (total < maxBytes && (read = await stream.ReadAsync(buffer.AsMemory(total, maxBytes - total), cancellationToken)) > 0)
            total += read;
        return Encoding.UTF8.GetString(buffer, 0, total);
    }

    private async Task<AdapterResult<byte[]>> SendBytesAsync(HepsiburadaRequestContext context, Uri baseAddress, string path, CancellationToken cancellationToken)
    {
        if (!string.Equals(settings.AuthenticationMode, "BASIC", StringComparison.OrdinalIgnoreCase))
            return AdapterResult<byte[]>.Failure(new(AdapterErrorClass.NotSupported, "HEPSIBURADA_AUTHENTICATION_UNVERIFIED", "Hepsiburada auth biçimi SIT hesabında doğrulanana kadar bağlantı isteği gönderilmedi.", null, null, null));
        // C42: the same bounded client as every other Hepsiburada call (the body is also capped below).
        var client = clients.CreateBoundedClient("Hepsiburada");
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(baseAddress, path));
        if (!ApplyAuthentication(request, context))
            return Failure<byte[]>(AdapterErrorClass.Validation, "HEPSIBURADA_INTEGRATOR_NAME_INVALID", "Hepsiburada entegratör adı geçerli bir User-Agent kimliği olmalıdır.", HttpStatusCode.UnprocessableEntity);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/pdf"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("image/png"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("image/jpeg"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/plain"));
        // C17: the label service answers JSON ({"format", "data": [...]}) as well as the file itself.
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var timeout = new QuotaAwareTimeout(request, HepsiburadaRateLimitHandler.QuotaAcquiredKey, RequestTimeout, cancellationToken);
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            var rate = RateLimit(response);
            var requestId = response.Headers.TryGetValues("X-Request-Id", out var ids) ? ids.FirstOrDefault() : null;
            if (!response.IsSuccessStatusCode)
            {
                var retryAfter = response.Headers.RetryAfter?.Delta ?? (response.Headers.RetryAfter?.Date is { } retryDate ? retryDate - timeProvider.GetUtcNow() : null);
                var error = response.StatusCode == HttpStatusCode.BadRequest
                    ? LabelRejected(await ReadBoundedTextAsync(response.Content, 16 * 1024, timeout.Token), requestId)
                    : Error(response.StatusCode, retryAfter ?? rate?.RetryAfter, requestId);
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
            if (!timeout.QuotaAcquired) return LocalQuotaWait<byte[]>();
            return AdapterResult<byte[]>.Failure(new(AdapterErrorClass.TransientNetwork, "HEPSIBURADA_TIMEOUT", "Hepsiburada isteği zaman aşımına uğradı.", null, TimeSpan.FromSeconds(15), null));
        }
        catch (HttpRequestException exception)
        {
            logger.LogWarning(exception, "Hepsiburada label API isteği başarısız. ConnectionId: {ConnectionId}", context.Connection.Id);
            return AdapterResult<byte[]>.Failure(new(AdapterErrorClass.TransientNetwork, "HEPSIBURADA_NETWORK_ERROR", "Hepsiburada bağlantısı geçici olarak kurulamadı.", null, TimeSpan.FromSeconds(15), null));
        }
    }
}
