using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Xml.Linq;
using MarketplaceHub.Application;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MarketplaceHub.Infrastructure.Adapters.PttAvm;

// PTT AVM adapter over the SOAP web service (PttAvmSoap.cs). The seller panel's Entegrasyon Bilgileri screen
// gives a store id (Mağaza ID, kept as the connection's ExternalStoreId), an API user name and an API
// password; the last two travel in the WS-Security UsernameToken of every call. The flows follow the phases in
// docs/PTTAVM-ENTEGRASYON-PLANI.md; phase 1 is the connection test and the read-only probe.
public sealed partial class PttAvmHttpClient(
    IHttpClientFactory clients,
    PttAvmAuthenticationHandler authentication,
    IOptions<PttAvmOptions> options,
    TimeProvider timeProvider,
    ILogger<PttAvmHttpClient> logger,
    IConfiguration configuration)
    : IConnectionPort
{
    internal const string ClientName = "PttAvm";
    internal const string DocumentationHost = "https://developers.pttavm.com/en/";
    internal const string SupplierInfo = "KullaniciTedarikciBilgisiGetir";
    internal const string MainCategories = "GetMainCategories";
    internal const string Orders = "SiparisKontrolListesiV2";
    internal const string StockList = "StokKontrolListesi";
    // PTT AVM works in Turkey time; order dates are sent as plain dates, as in the documented sample.
    internal static readonly TimeSpan TurkeyOffset = TimeSpan.FromHours(3);
    private readonly PttAvmOptions settings = options.Value;

    private TimeSpan RequestTimeout => settings.Timeout > TimeSpan.Zero && settings.Timeout < TimeSpan.FromMinutes(2) ? settings.Timeout : TimeSpan.FromSeconds(30);

    // Every PTT AVM write goes through this gate: the server-wide switch, the connection switch,
    // a production connection that was verified or activated. Callers above check the same switches
    // when they queue work; this keeps a write from leaving even when a caller forgets.
    internal AdapterResult<T>? WriteBlocked<T>(PttAvmRequestContext account, string what)
    {
        var connection = account.Connection;
        var allowed = configuration.GetValue<bool>("FeatureFlags:ExternalWrites")
            && ConnectionWritesEnabled(connection.SettingsJson)
            && string.Equals(connection.Environment, "PRODUCTION", StringComparison.OrdinalIgnoreCase)
            && (string.Equals(connection.Status, "ACTIVE", StringComparison.OrdinalIgnoreCase) || string.Equals(connection.Status, "VERIFIED", StringComparison.OrdinalIgnoreCase));
        return allowed
            ? null
            : Failure<T>(AdapterErrorClass.Validation, "PTTAVM_EXTERNAL_WRITES_DISABLED", $"PTT AVM bağlantısında dış yazma kapalı; {what} gönderilmedi.", HttpStatusCode.Forbidden);
    }

    internal static bool ConnectionWritesEnabled(string settingsJson)
    {
        try
        {
            using var document = JsonDocument.Parse(settingsJson);
            return document.RootElement.TryGetProperty("ExternalWritesEnabled", out var enabled) && enabled.ValueKind == JsonValueKind.True;
        }
        catch (JsonException) { return false; }
    }

    public async Task<AdapterResult<ConnectionIdentity>> TestAsync(AdapterContext context, CancellationToken cancellationToken)
    {
        var load = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken);
        return load.Account is null ? CredentialFailure<ConnectionIdentity>(load) : await TestAsync(load.Account, cancellationToken);
    }

    // The supplier info call is the cheapest authenticated read: no parameters, and its TedarikciId is the store the
    // API user belongs to. A numeric Mağaza ID on the connection must match it; anything else (a store name typed in
    // its place) gives way to the TedarikciId, which the connection test then saves on the connection.
    internal async Task<AdapterResult<ConnectionIdentity>> TestAsync(PttAvmRequestContext account, CancellationToken cancellationToken)
    {
        var connection = account.Connection;
        var storeId = StoreId(connection.ExternalStoreId);
        var result = await SendSoapAsync(account, SupplierInfo, [], cancellationToken);
        if (!result.IsSuccess) return AdapterResult<ConnectionIdentity>.Failure(result.Error!, result.RateLimit);
        var supplier = Supplier(result.Value!);
        if (supplier is null)
            return Failure<ConnectionIdentity>(AdapterErrorClass.ContractViolation, "PTTAVM_CONNECTION_CONTRACT_INVALID", $"PTT AVM tedarikçi bilgisi yanıtı beklenen yapıda değil (yanıt öğeleri: {ElementShape(result.Value)}).", HttpStatusCode.BadGateway);
        if (storeId is not null && long.Parse(supplier.Value.Id, CultureInfo.InvariantCulture) != long.Parse(storeId, CultureInfo.InvariantCulture))
            return Failure<ConnectionIdentity>(AdapterErrorClass.Validation, "PTTAVM_STORE_MISMATCH",
                $"Bu API kullanıcısı başka bir PTT AVM mağazasına ait (Mağaza ID {supplier.Value.Id}); bağlantıda Mağaza ID {storeId} yazılı. Entegrasyon Bilgileri ekranındaki Mağaza ID'yi girin ya da o mağazanın API kullanıcısını kullanın.", HttpStatusCode.Conflict);
        return AdapterResult<ConnectionIdentity>.Success(
            new("PTTAVM", connection.Environment, supplier.Value.Id, connection.ApiVersion, supplier.Value.Id),
            result.RateLimit);
    }

    // The documented answer: <KullaniciTedarikciBilgisiGetirResult><a:TedarikciAdi/><a:TedarikciId/></…>.
    internal static (string Id, string? Name)? Supplier(XElement result)
    {
        if (IsNil(result) || ContractValue(result, "TedarikciId") is not { } id || !long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number <= 0)
            return null;
        return (number.ToString(CultureInfo.InvariantCulture), ContractValue(result, "TedarikciAdi"));
    }

    public async Task<AdapterResult<IReadOnlyList<CapabilityEvidence>>> DiscoverCapabilitiesAsync(AdapterContext context, CancellationToken cancellationToken)
    {
        var load = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken);
        return load.Account is null ? CredentialFailure<IReadOnlyList<CapabilityEvidence>>(load) : await DiscoverCapabilitiesAsync(load.Account, cancellationToken);
    }

    // Read-only probe: the supplier info of the connection test, the main category list, yesterday's and today's
    // orders and the first page of the store's stock list. A capability whose answer does not have the
    // documented shape stays UNKNOWN; write capabilities stay UNKNOWN until a controlled first write on the live store.
    internal async Task<AdapterResult<IReadOnlyList<CapabilityEvidence>>> DiscoverCapabilitiesAsync(PttAvmRequestContext account, CancellationToken cancellationToken)
    {
        var connection = await TestAsync(account, cancellationToken);
        if (!connection.IsSuccess) return AdapterResult<IReadOnlyList<CapabilityEvidence>>.Failure(connection.Error!, connection.RateLimit);
        var identity = connection.Value!;
        var now = timeProvider.GetUtcNow();

        var categories = await SendSoapAsync(account, MainCategories, [], cancellationToken);
        var categoriesError = categories.IsSuccess ? MainCategoryError(categories.Value!) : categories.Error!.Code;
        var orders = await SendSoapAsync(account, Orders, OrderProbeParameters(now), cancellationToken);
        var products = await SendSoapAsync(account, StockList, StockProbeParameters(identity.ExternalStoreId), cancellationToken);

        IReadOnlyList<CapabilityEvidence> evidence =
        [
            new(MarketplaceCapabilities.ConnectionTest, "SUPPORTED", identity.ApiVersion, identity.Environment, identity.ExternalStoreId, DocumentationHost + "listeleme-entegrasyonu_soap/tedarikci-bilgisi_soap", "soap", null, null, "PTT AVM API kullanıcı adı ve şifresi kabul edildi; Mağaza ID tedarikçi bilgisinden doğrulandı.", null, now),
            new(MarketplaceCapabilities.ReferenceRead, categoriesError is null ? "SUPPORTED" : "UNKNOWN", identity.ApiVersion, identity.Environment, identity.ExternalStoreId, DocumentationHost + "katalog-entegrasyonu_soap/ana-kategori-listesi_soap", "soap", null, null,
                categoriesError is null ? "Ana kategori listesi salt okunur okundu." : $"Ana kategori listesi okunamadı ({categoriesError}); yetenek UNKNOWN bırakıldı.", null, now),
            new(MarketplaceCapabilities.OrderRead, orders.IsSuccess ? "SUPPORTED" : "UNKNOWN", identity.ApiVersion, identity.Environment, identity.ExternalStoreId, DocumentationHost + "siparis-entegrasyonu_soap/siparis-kontrol-v2_soap", "soap", null, null,
                orders.IsSuccess ? "Dünün ve bugünün siparişleri salt okunur listelendi." : $"Sipariş listeleme denemesi tamamlanamadı ({orders.Error!.Code}); yetenek UNKNOWN bırakıldı.", null, now),
            new(MarketplaceCapabilities.ProductRead, products.IsSuccess ? "SUPPORTED" : "UNKNOWN", identity.ApiVersion, identity.Environment, identity.ExternalStoreId, DocumentationHost + "listeleme-entegrasyonu_soap/stok-kontrol-listesi_soap", "soap", null, null,
                products.IsSuccess ? "Stok kontrol listesinin ilk sayfası salt okunur okundu." : $"Stok kontrol listesi okunamadı ({products.Error!.Code}); yetenek UNKNOWN bırakıldı.", null, now)
        ];
        return AdapterResult<IReadOnlyList<CapabilityEvidence>>.Success(evidence, connection.RateLimit ?? orders.RateLimit);
    }

    // GetMainCategoriesResult is {success, main_category, error} and GetCategoryTreeResult {success, category_tree, error};
    // null when the list was read.
    internal static string? MainCategoryError(XElement result, string listName = "main_category")
    {
        if (IsNil(result)) return "PTTAVM_CONTRACT_INVALID: boş yanıt";
        if (!string.Equals(ContractValue(result, "success"), "true", StringComparison.OrdinalIgnoreCase))
        {
            var error = result.Elements().FirstOrDefault(x => x.Name.LocalName == "error");
            return $"PTTAVM_REQUEST_REJECTED: {Clip(ContractValue(error, "error_message") ?? ContractValue(error, "error_code")) ?? "success=false"}";
        }
        return result.Elements().Any(x => x.Name.LocalName == listName) ? null : $"PTTAVM_CONTRACT_INVALID: {ElementShape(result)}";
    }

    // SiparisKontrolListesiV2: at most 40 days apart; AktifSiparisler=0 returns every status.
    internal static IReadOnlyList<SoapParameter> OrderProbeParameters(DateTimeOffset now)
    {
        var today = now.ToOffset(TurkeyOffset).Date;
        return [new("BaslangicTarihi", Day(today.AddDays(-1))), new("BitisTarihi", Day(today)), new("AktifSiparisler", "0")];
    }

    // The first page of the store's stock list, with the filters of the documented sample.
    internal static IReadOnlyList<SoapParameter> StockProbeParameters(string storeId) =>
    [
        new("ShopId", StoreId(storeId) ?? "0"), new("SearchKategoriId", "0"), new("SearchAltKategoriId", "0"), new("SearchUrunAdi", ""), new("SearchBarkod", ""),
        new("SearchAktifPasif", "1"), new("SearchMevcut", "0"), new("SearchTedarikciKategoriId", "0"), new("SearchYeniKategoriId", ""), new("SearchPage", "0")
    ];

    internal static string Day(DateTime value) => value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    // The Mağaza ID is the numeric supplier id PTT AVM shows on the Entegrasyon Bilgileri screen.
    internal static string? StoreId(string? value)
    {
        var trimmed = value?.Trim();
        return !string.IsNullOrEmpty(trimmed) && trimmed.Length <= 18 && trimmed.All(char.IsAsciiDigit) && long.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number > 0
            ? trimmed
            : null;
    }

    internal static AdapterResult<T> CredentialFailure<T>(PttAvmCredentialLoad load) => load.LegacyApiKeyPayload
        ? Failure<T>(AdapterErrorClass.Authentication, "PTTAVM_CREDENTIALS_REJECTED", PttAvmErrorAdvice.LegacyCredentials, HttpStatusCode.Unauthorized)
        : Failure<T>(AdapterErrorClass.Authentication, "PTTAVM_CREDENTIAL_INVALID", "PTT AVM bağlantısı veya şifreli API kullanıcı bilgileri eksik ya da geçersiz.", HttpStatusCode.Unauthorized);

    private static AdapterResult<T> Failure<T>(AdapterErrorClass @class, string code, string message, HttpStatusCode? status = null) =>
        AdapterResult<T>.Failure(new(@class, code, message, status is null ? null : (int)status, null, null));
}
