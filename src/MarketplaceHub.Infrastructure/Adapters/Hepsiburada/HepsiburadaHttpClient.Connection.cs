using System.Net;
using System.Text.Json;
using MarketplaceHub.Application;

namespace MarketplaceHub.Infrastructure.Adapters.Hepsiburada;

public sealed partial class HepsiburadaHttpClient
{
    private const string OrderGuide = "https://developers.hepsiburada.com/tr/companies/hepsiburada?guide=siparis-entegrasyonu-onemli-bilgiler&product=siparis-olusturma-entegrasyonu&view=guide";
    private const string ListingGuide = "https://developers.hepsiburada.com/tr/companies/hepsiburada?category=baslangic&op=Listing+Bilgilerini+Sorgulama&product=listeleme&version=v1&view=endpoint";
    private const string ListingUpdateGuide = "https://developers.hepsiburada.com/tr/companies/hepsiburada?category=baslangic&guide=urun-guncelleme-onemli-bilgiler&product=urun-guncelleme-entegrasyonu&version=v1.0&view=guide";
    private const string ProductImportGuide = "https://developers.hepsiburada.com/tr/companies/hepsiburada?category=katalog-urun-entegrasyonu&product=katalog-urun-entegrasyonu&version=v1.0&view=guide";
    private const string CategoryEndpointGuide = "https://developers.hepsiburada.com/tr/companies/hepsiburada?category=katalog-urun-entegrasyonu&product=katalog-urun-entegrasyonu&version=v1.0&op=getAllCategoriesByParameters&view=endpoint";
    private const string ClaimAcceptEndpointGuide = "https://developers.hepsiburada.com/tr/companies/hepsiburada?category=hepsiburada-talep-entegrasyonu&op=Post__claims_number_claimNumber_accept&product=talep-listeleme&version=v1.0&view=endpoint";
    private const string AskSellerGuide = "https://developers.hepsiburada.com/tr/companies/hepsiburada?product=saticiya-sor-entegrasyonu&version=v1.0&op=Soru+Cevaplama&view=endpoint";

    public async Task<AdapterResult<ConnectionIdentity>> TestAsync(AdapterContext context, CancellationToken cancellationToken)
    {
        var account = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken);
        if (account is null) return Failure<ConnectionIdentity>(AdapterErrorClass.Authentication, "HEPSIBURADA_CREDENTIAL_INVALID", "Hepsiburada bağlantısı veya şifreli kullanıcı bilgileri eksik ya da geçersiz.", HttpStatusCode.Unauthorized);
        var result = await SendAsync(account, account.OmsBaseAddress, HttpMethod.Get, Orders(account, "offset=0&limit=1"), cancellationToken);
        string? verifiedUsernameToPersist = null;
        if (!result.IsSuccess && string.Equals(result.Error?.Code, "HEPSIBURADA_CREDENTIALS_REJECTED", StringComparison.Ordinal))
        {
            var alternateUsername = HepsiburadaAuthenticationHandler.MerchantIdUsernameFallback(account.Username, account.Connection.ExternalStoreId);
            if (alternateUsername is not null)
            {
                var alternateAccount = account with { Username = alternateUsername };
                var alternateResult = await SendAsync(alternateAccount, alternateAccount.OmsBaseAddress, HttpMethod.Get, Orders(alternateAccount, "offset=0&limit=1"), cancellationToken);
                if (alternateResult.IsSuccess)
                {
                    account = alternateAccount;
                    result = alternateResult;
                    verifiedUsernameToPersist = alternateUsername;
                }
                else if (alternateResult.Error?.HttpStatus == (int)HttpStatusCode.Unauthorized)
                {
                    return AdapterResult<ConnectionIdentity>.Failure(new(AdapterErrorClass.Authentication, "HEPSIBURADA_CREDENTIALS_REJECTED", "Hepsiburada canlı sipariş servisi entegratör kullanıcı adıyla da mağaza ID’siyle de kimlik doğrulamasını reddetti. Aynı aktif entegratör kaydının güncel canlı servis anahtarını ve mağaza yetkisini kontrol edin.", (int)HttpStatusCode.Unauthorized, alternateResult.Error.RetryAfter, alternateResult.Error.RemoteRequestId), alternateResult.RateLimit ?? result.RateLimit);
                }
                else
                {
                    return AdapterResult<ConnectionIdentity>.Failure(alternateResult.Error!, alternateResult.RateLimit);
                }
            }
        }
        if (!result.IsSuccess) return AdapterResult<ConnectionIdentity>.Failure(result.Error!, result.RateLimit);
        using var resultDocument = result.Value;
        try
        {
            HepsiburadaJsonMapper.OrderPage(result.Value!.RootElement);
            if (verifiedUsernameToPersist is not null && !await authentication.SaveBasicUsernameAsync(context.TenantId, context.ConnectionId, verifiedUsernameToPersist, cancellationToken))
                return Failure<ConnectionIdentity>(AdapterErrorClass.Authentication, "HEPSIBURADA_AUTH_USERNAME_SAVE_FAILED", "Hepsiburada mağaza ID’si ile doğrulama başarılı oldu ancak eşzamanlı kimlik bilgisi değişikliği nedeniyle çalışan Basic kullanıcı adı kaydedilemedi. Kimlik bilgilerini yeniden kaydedip tekrar deneyin.", HttpStatusCode.Conflict);
            return AdapterResult<ConnectionIdentity>.Success(new("HEPSIBURADA", account.Connection.Environment, account.Connection.ExternalStoreId, account.Connection.ApiVersion, account.Connection.ExternalStoreId), result.RateLimit);
        }
        catch (JsonException)
        {
            return Failure<ConnectionIdentity>(AdapterErrorClass.ContractViolation, "HEPSIBURADA_CONNECTION_CONTRACT_INVALID", "Hepsiburada bağlantı yanıtı beklenen sipariş sayfa sözleşmesiyle eşleşmiyor.", HttpStatusCode.BadGateway);
        }
    }

    public async Task<AdapterResult<IReadOnlyList<CapabilityEvidence>>> DiscoverCapabilitiesAsync(AdapterContext context, CancellationToken cancellationToken)
    {
        var connection = await TestAsync(context, cancellationToken);
        if (!connection.IsSuccess) return AdapterResult<IReadOnlyList<CapabilityEvidence>>.Failure(connection.Error!, connection.RateLimit);
        var identity = connection.Value!;
        var now = timeProvider.GetUtcNow();
        var orders = await PollAsync(context, new OrderPollWindow(null, now), new(null, 1), cancellationToken);
        var products = await ListAsync(context, new(null, 1), new(null), cancellationToken);
        var returnsResult = await PollAsync(context, new ReturnPollWindow(null, null, Status: "AwaitingAction"), new("0", 1), cancellationToken);
        var references = await ReadAsync(context, new("CATEGORIES", null), new(null, 1), cancellationToken);
        IReadOnlyList<CapabilityEvidence> evidence =
        [
            new(MarketplaceCapabilities.ConnectionTest, "SUPPORTED", identity.ApiVersion, identity.Environment, identity.ExternalStoreId, OrderGuide, "v1.0", null, null, "Ödemesi tamamlanmış sipariş endpoint'inden SIT/Canlı salt okunur yanıt alındı.", null, now),
            new(MarketplaceCapabilities.OrderRead, orders.IsSuccess ? "SUPPORTED" : "UNKNOWN", identity.ApiVersion, identity.Environment, identity.ExternalStoreId, OrderGuide, "v1.0", null, null, orders.IsSuccess ? "Sipariş listeleme ve sipariş detay okuma probe'u başarılı." : "Sipariş okuma probe'u tamamlanamadı; yetenek UNKNOWN bırakıldı.", null, now),
            new(MarketplaceCapabilities.ProductRead, products.IsSuccess ? "SUPPORTED" : "UNKNOWN", identity.ApiVersion, identity.Environment, identity.ExternalStoreId, ListingGuide, "v1", null, null, products.IsSuccess ? "Satıcı listing bilgisi salt okunur olarak alındı." : "Listing okuma probe'u tamamlanamadı; yetenek UNKNOWN bırakıldı.", null, now),
            // Product import (create and update) goes through the same integrator credentials as price and stock, so a
            // successful check of this store, environment and API version stands for it too; the external write switches
            // still decide whether anything is sent. Without it a live store's product import never passed the gate.
            new(MarketplaceCapabilities.ProductWrite, "SUPPORTED", identity.ApiVersion, identity.Environment, identity.ExternalStoreId, ProductImportGuide, "v1.0", null, null, "Başarılı bağlantı kontrolü bu mağaza, ortam ve API sürümünü doğruladı; ürün aktarımı Hepsiburada'nın resmî katalog ürün entegrasyonu dokümanına göre aynı entegratör kimliğiyle yapılır.", null, now),
            new(MarketplaceCapabilities.PriceWrite, "SUPPORTED",identity.ApiVersion, identity.Environment, identity.ExternalStoreId, ListingUpdateGuide, "v1.0", null, null, "Başarılı bağlantı kontrolü bu mağaza, ortam ve API sürümünü doğruladı; fiyat güncelleme yeteneği Hepsiburada'nın resmî listing güncelleme dokümanına göre kaydedildi. Yazma çağrısı bağlantı kontrolünde çalıştırılmaz.", null, now),
            new(MarketplaceCapabilities.InventoryWrite, "SUPPORTED", identity.ApiVersion, identity.Environment, identity.ExternalStoreId, ListingUpdateGuide, "v1.0", null, null, "Başarılı bağlantı kontrolü bu mağaza, ortam ve API sürümünü doğruladı; stok güncelleme yeteneği Hepsiburada'nın resmî listing güncelleme dokümanına göre kaydedildi. Yazma çağrısı bağlantı kontrolünde çalıştırılmaz.", null, now),
            new(MarketplaceCapabilities.ShipmentWrite, "SUPPORTED", identity.ApiVersion, identity.Environment, identity.ExternalStoreId, OrderGuide, "v1.0", null, null, "Başarılı bağlantı kontrolü bu mağaza, ortam ve API sürümünü doğruladı; kargo/paket işlemleri Hepsiburada'nın resmî sipariş entegrasyonu dokümanına göre kaydedildi. Yazma çağrısı bağlantı kontrolünde çalıştırılmaz.", null, now),
            new(MarketplaceCapabilities.ReturnWrite, "SUPPORTED", identity.ApiVersion, identity.Environment, identity.ExternalStoreId, ClaimAcceptEndpointGuide, "v1.0", null, null, "Başarılı bağlantı kontrolü bu mağaza, ortam ve API sürümünü doğruladı; iade kararları Hepsiburada'nın resmî talep endpoint dokümanına göre kaydedildi. Yazma çağrısı bağlantı kontrolünde çalıştırılmaz.", null, now),
            new(MarketplaceCapabilities.ReferenceRead, references.IsSuccess ? "SUPPORTED" : "UNKNOWN", identity.ApiVersion, identity.Environment, identity.ExternalStoreId, CategoryEndpointGuide, "v1.0", null, null, references.IsSuccess ? "Etkin ve ürün açılabilir kategori listesi salt okunur olarak yanıt verdi." : "Kategori referans okuma probe'u tamamlanamadı; yetenek UNKNOWN bırakıldı.", null, now),
            new(MarketplaceCapabilities.ReturnRead, returnsResult.IsSuccess ? "SUPPORTED" : "UNKNOWN", identity.ApiVersion, identity.Environment, identity.ExternalStoreId, "https://developers.hepsiburada.com/tr/companies/hepsiburada?guide=talep-onemli-bilgiler-2&product=talep-entegrasyonu&view=guide", "v1.0", null, null, returnsResult.IsSuccess ? "Aksiyon bekleyen talep listeleme endpoint'i salt okunur olarak yanıt verdi." : "Talep okuma probe'u tamamlanamadı; yetenek UNKNOWN bırakıldı.", null, now),
            new(MarketplaceCapabilities.QuestionRead, "SUPPORTED", identity.ApiVersion, identity.Environment, identity.ExternalStoreId, AskSellerGuide, "v1.0", null, null, "Ürün ve sipariş soruları, son kullanma tarihi ve konuşma geçmişi resmî Satıcıya Sor endpoint dokümanına göre desteklenir.", null, now),
            new(MarketplaceCapabilities.QuestionWrite, "SUPPORTED", identity.ApiVersion, identity.Environment, identity.ExternalStoreId, AskSellerGuide, "v1.0", null, null, "Multipart Answer alanıyla cevap gönderimi resmî Satıcıya Sor endpoint dokümanına göre kaydedildi; bağlantı kontrolünde dış yazma çağrısı çalıştırılmaz.", null, now)
        ];
        return AdapterResult<IReadOnlyList<CapabilityEvidence>>.Success(evidence, connection.RateLimit ?? orders.RateLimit ?? products.RateLimit ?? returnsResult.RateLimit ?? references.RateLimit);
    }
}
