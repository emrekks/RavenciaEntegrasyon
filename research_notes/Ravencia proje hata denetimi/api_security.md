# API Katmanı ve Güvenlik Denetimi (src/MarketplaceHub.Api, Infrastructure/Identity+Security, appsettings, deploy/caddy)

Kapsam ve yöntem: commit `3787b73` üzerinde salt-okur kod incelemesi. Canlı sunucuya dokunulmadı; canlıdaki ayar değerleri (ör. MFA zorunluluğu) doğrulanmadı. Kaynak bağlantıları depo içi dosya yollarıdır (satır numarası `:N`). Gizli değerler not edilmedi, yalnız konumları verildi.

Genel tablo: Kod tabanı belirgin biçimde sertleştirilmiş (önceki güvenlik incelemelerine atıflar var: "security review 2026-10-29 A-1/A-2", "G5/G8/G11", "A13"). Kritik ya da Yüksek önem derecesinde, izlenip doğrulanmış bir açık bulunamadı. Bulguların çoğu Orta/Düşük; aşağıda her biri dosya:satır, senaryo, önem ve güven etiketiyle listelendi.

## 1. Her uç nokta kimlik doğrulamanın arkasında mı (sağlık ve webhook dışında)? Webhook'lar doğrulanıyor mu?

### Takeaway
Evet: `/api/v1` altındaki tüm uç noktalar işleyici içinde `Tenant(http)` kontrolü yapıyor (otomatik tarama + elle inceleme); oturumsuz kalanlar kasıtlı olanlar (auth, `/public/*` belge/görsel bağlantıları, `/hooks/*`, `/health/*`, token korumalı `/internal/*`). Webhook'lar route-token HMAC araması + platforma özel Basic/API-key/HMAC doğrulamasıyla korunuyor. Kimlik doğrulama ASP.NET `[Authorize]` ile değil, her işleyicideki elle kontrolle yapılıyor; bu "fail-open" bir tasarım (yeni bir uç nokta kontrolü unutursa açık kalır), ancak bugün açık bir örnek yok.

### Cited Findings
- Program.cs'te `AddAuthentication/AddAuthorization/RequireAuthorization` yok; kimlik `SessionAuthMiddleware` ile `HttpContext.User`'a yazılıyor, rol kontrolü `RoleAuthorizationMiddleware`'de, kiracı kontrolü her işleyicide `Tenant(http)` ile — [Program.cs:129-136](src/MarketplaceHub.Api/Program.cs), [SessionAuthMiddleware.cs:15-45](src/MarketplaceHub.Api/Security/SessionAuthMiddleware.cs)
- `RoleAuthorizationMiddleware` GET/HEAD isteklerinde `ReadRoles` null dönerse oturumsuz isteği de geçiriyor; yani okuma uçlarında oturum zorunluluğu tamamen işleyicideki `Tenant(http)` kontrolüne bağlı — [RoleAuthorizationMiddleware.cs:32-38](src/MarketplaceHub.Api/Security/RoleAuthorizationMiddleware.cs)
- `tenant_id` ve rol claim'i yalnız `SessionState.Active` oturumlarda ekleniyor; MFA/parola-değişimi adımındaki oturum `Tenant()` = null alıp 401 görüyor — [SessionAuthMiddleware.cs:32-36](src/MarketplaceHub.Api/Security/SessionAuthMiddleware.cs), [HttpTenantContextAccessor.cs:13-16](src/MarketplaceHub.Api/Security/HttpTenantContextAccessor.cs)
- Taranan tüm `Map*` uçlarında kiracı kontrolü var; yalnız şunlar oturumsuz: `/api/v1/public/product-media/{assetId}/content` (yalnız canlıya verilmiş ürün görselleri, JPEG/PNG), `/api/v1/public/invoice-documents/{token}/…` ve `/public/manual-invoice-documents/{token}/content` (DataProtection imzalı token + `PermanentUrl` eşleşmesi + geri çekilme kontrolü), `/api/v1/hooks/*`, `/api/v1/auth/*` — [CatalogEndpoints.cs:162-184](src/MarketplaceHub.Api/Catalog/CatalogEndpoints.cs), [InvoicingEndpoints.cs:23-40](src/MarketplaceHub.Api/Invoicing/InvoicingEndpoints.cs)
- `/internal/alert-settings` (+`/import`) oturumsuz ama: Caddy özel olmayan kaynaklara 404 veriyor, API sunucu ağı kontrolü + en az 32 karakter host token'ını `FixedTimeEquals` ile karşılaştırıyor; token yapılandırılmamışsa 404 — [AlertEmailEndpoints.cs:99-106](src/MarketplaceHub.Api/Settings/AlertEmailEndpoints.cs), [Caddyfile.production:44-52](deploy/caddy/Caddyfile.production)
- Webhook: önce `WebhookRouteGate` (connection public id + HMAC'lenmiş route token DB araması, gövde okunmadan), sonra servis içinde `tokenHasher.Verify` (sabit zamanlı) ve platform doğrulayıcı: Trendyol/Hepsiburada `x-api-key` veya `Authorization: Basic` `FixedTimeEquals`; Shopify `X-Shopify-Hmac-Sha256` HMAC-SHA256 — [WebhookRouteGate.cs:24-39](src/MarketplaceHub.Api/Marketplace/WebhookRouteGate.cs), [MarketplaceWebhookService.cs:14-35](src/MarketplaceHub.Infrastructure/Persistence/MarketplaceWebhookService.cs), [TrendyolWebhookVerifier.cs:24-31](src/MarketplaceHub.Infrastructure/Adapters/Trendyol/TrendyolWebhookVerifier.cs), [ShopifyWebhookVerifier.cs:44-49](src/MarketplaceHub.Infrastructure/Adapters/Shopify/ShopifyWebhookVerifier.cs)
- Shopify `ROUTE_TOKEN` aboneliği (client secret yok) yalnız gizli route ile kabul ediliyor; gövde yalnız siparişi adlandırıyor ve sipariş Shopify'dan yeniden okunuyor (tasarım kararı) — [ShopifyWebhookVerifier.cs:41-47](src/MarketplaceHub.Infrastructure/Adapters/Shopify/ShopifyWebhookVerifier.cs)
- SignalR hub'ında `[Authorize]` yok; `OnConnectedAsync` `tenant_id`/`session_id` claim'i yoksa bağlantıyı kesiyor, `HubOriginFilter` Origin'i panelle eşliyor, `OperationsHubSessionWatcher` her dakika geçersiz oturumların bağlantılarını kapatıyor — [OperationsHub.cs:12-25,67-109](src/MarketplaceHub.Api/Realtime/OperationsHub.cs), [HubOriginFilter.cs:9-18](src/MarketplaceHub.Api/Realtime/HubOriginFilter.cs)

#### Bulgu 1.1 — Yönetici rolleri için MFA zorunluluğu varsayılan olarak kapalı — Orta — [Muhtemel]
- `appsettings.json` `Security:RequireMfaForElevatedRoles: false`; üretim compose'u da `${MARKETPLACEHUB_REQUIRE_MFA_FOR_ELEVATED_ROLES:-false}` ile varsayılanı kapalı geçiyor — [appsettings.json:3](src/MarketplaceHub.Api/appsettings.json), [compose.production.yaml:29](deploy/compose/compose.production.yaml), [ElevatedRoleMfaMiddleware.cs:11-31](src/MarketplaceHub.Api/Security/ElevatedRoleMfaMiddleware.cs)
- Senaryo: OWNER/ADMINISTRATOR parolası çalınırsa ve kullanıcı TOTP kurmamışsa saldırgan ikinci faktör olmadan pazaryeri kimlik bilgilerini değiştirebilir (`PUT /connections/{id}/credential`), `deep-delete`/`data-reset`/`full-reset` çalıştırabilir. Canlıdaki `production.env` değeri incelenmedi (salt-okur kural); yorum "owner TOTP kurunca açılacak" diyor.

#### Bulgu 1.2 — `GET /returns/{id}/audits` canlı Trendyol çağrısı yapıyor ama `RequireSameOriginFetch` yok — Düşük — [Kesin]
- Satır yorumu "Reads the return's status history from Trendyol live" diyor; aynı türdeki diğer canlı okumalar (`orders/{id}/finance`, `product-image`, `changeable-cargo-companies`, `rejection-reasons`, `instant-notifications`, `n11-shipment-templates`) filtreyi taşıyor, bu taşımıyor — [MarketplaceEndpoints.cs:170-171](src/MarketplaceHub.Api/Marketplace/MarketplaceEndpoints.cs), [FetchMetadata.cs:5-21](src/MarketplaceHub.Api/Security/FetchMetadata.cs), [ReturnAuditReader.cs:8-13](src/MarketplaceHub.Infrastructure/Persistence/ReturnAuditReader.cs)
- Senaryo: oturum çerezi SameSite=Lax; başka bir sitenin üst düzey gezinmesi (bağlantı/`window.open`) oturum açık bir kullanıcıya bu GET'i yaptırıp Trendyol kotasını harcatabilir. Veri sızmaz (yanıt saldırgana okunmaz); etki kota tüketimi.

#### Bulgu 1.3 — Webhook route-token reddi oturumsuz DB yazımı ve sahte uyarı üretiyor — Düşük — [Kesin]
- Route kapısı reddettiğinde `NoteRouteRefusedAsync` çağrılıyor; bağlantı public id'si bilinen herkes rastgele token ile 1-3 sorgu + `OperationalIssue` güncellemesi tetikliyor (kayıt `Status=Open` yapılıyor, dakikada en çok bir kez) — [MarketplaceEndpoints.cs:351-360](src/MarketplaceHub.Api/Marketplace/MarketplaceEndpoints.cs), [MarketplaceWebhookService.cs:82-125](src/MarketplaceHub.Infrastructure/Persistence/MarketplaceWebhookService.cs)
- Kodun kendi notu public id'nin gizli olmadığını söylüyor ("every panel role reads it, and it sits in the address the marketplace was given") — [WebhookRateLimits.cs:6-19](src/MarketplaceHub.Api/Marketplace/WebhookRateLimits.cs)
- Senaryo: saldırgan sürekli yanlış token gönderip panelde "WEBHOOK_DELIVERY_REJECTED / adres eski ya da hatalı" sorununu açık tutar (yanıltıcı alarm, gerçek sorunların gölgelenmesi). Adres başına 4800/dk limit var, bu yüzden DB yükü sınırlı.

#### Bulgu 1.4 — Shopify tazelik kontrolü imzasız başlığa dayanıyor — Düşük — [Kesin] (etkisi [Muhtemel])
- `X-Shopify-Triggered-At` başlığı yoksa ya da okunamıyorsa teslimat "taze" sayılıyor; başlık imzalı değil — [ShopifyWebhookVerifier.cs:57,88-93](src/MarketplaceHub.Infrastructure/Adapters/Shopify/ShopifyWebhookVerifier.cs)
- Senaryo: yakalanmış imzalı bir gövde, başlık silinerek yeniden gönderilirse beş dakikalık pencere atlanır. Tekilleştirme gövde özetine (topic + SHA-256) dayandığı için aynı gövde inbox kaydı durdukça yeniden işlenmez; inbox saklama süresi dolduktan sonraki tekrar oynatma etkisi incelenmedi.

#### Bulgu 1.5 — SignalR iptal gecikmesi — Düşük — [Kesin]
- Çıkış/oturum iptalinden sonra açık hub bağlantısı en fazla ~1 dakika daha kiracı olaylarını almaya devam eder (kontrol aralığı 1 dk) — [OperationsHub.cs:69](src/MarketplaceHub.Api/Realtime/OperationsHub.cs)

### Inferences
- Kimlik doğrulama "her işleyici kendisi kontrol eder" modeline dayanıyor; bir regresyon testi ya da `RequireAuthorization` tabanlı bir varsayılan-kapalı politika yoksa gelecekte unutulan bir `Tenant(http)` kontrolü doğrudan veri sızıntısına dönüşür. Bugün için bu bir risk, bir açık değil.
- READONLY rolü tüm GET'leri okuyabiliyor (ör. `products/transfer/export.xlsx` maliyet verisi, sipariş müşteri bilgileri); kod yorumları bunun kasıtlı olduğunu söylüyor — [ProductTransferEndpoints.cs:7-8](src/MarketplaceHub.Api/Catalog/ProductTransferEndpoints.cs).

### Gaps
- `ApiSecurityEndToEndTests` gibi testlerin tüm uçları "oturumsuz → 401" için tarayıp taramadığı doğrulanmadı.
- Canlıdaki `MARKETPLACEHUB_REQUIRE_MFA_FOR_ELEVATED_ROLES` değeri ve sahiplerin TOTP durumu bilinmiyor (`/prod-sql` ile `identity.user_securities` okunarak doğrulanabilir).

## 2. Pazaryeri kimlik bilgileri bekleme halinde şifreli mi ve istemciye hiç dönmüyor mu? (oturum/çerez/parola/MFA dahil)

### Takeaway
Evet. Kimlik bilgileri ASP.NET Data Protection ile, kiracı+bağlantıya bağlı alt-amaçla (`b1.` öneki) şifreleniyor; anahtar halkası üretimde sertifika ile şifreli. API çıktısında yalnız `HasCredential` (ve `MaskedHint`) var. Oturum, CSRF, parola ve TOTP mekanizmaları sağlam; yalnız küçük kusurlar bulundu.

### Cited Findings
- Bağlantı kimlik bilgisi `BoundSecret.ProtectForConnection(tenantId, connectionId)` ile saklanıyor; başka bir kiracının satırına kopyalanan değer çözülmüyor — [BoundSecret.cs:5-34](src/MarketplaceHub.Infrastructure/Security/BoundSecret.cs), [MarketplaceConnectionService.cs:198-257,481](src/MarketplaceHub.Infrastructure/Persistence/MarketplaceConnectionService.cs)
- Data Protection anahtarları dosya sisteminde; `PILOT_LOCAL` dışında `DataProtection:CertificatePath` yoksa uygulama açılmıyor (anahtar şifreleme sertifikası zorunlu) — [DependencyInjection.cs:46-57](src/MarketplaceHub.Infrastructure/DependencyInjection.cs)
- `ConnectionView` DTO'su gizli alan taşımıyor (yalnız `HasCredential`) — [MarketplaceContracts.cs:973](src/MarketplaceHub.Application/MarketplaceContracts.cs)
- Alarm e-postası uygulama şifresi GET'te dönmüyor (`appPasswordSaved` bool) — [AlertEmailEndpoints.cs:119-123](src/MarketplaceHub.Api/Settings/AlertEmailEndpoints.cs)
- Oturum token'ı 32 bayt rastgele, DB'de HMAC-SHA256 (sunucu anahtarlı) özetle tutuluyor; çerez `__Host-MarketplaceHub`, HttpOnly, Secure, SameSite=Lax, Path=/ — [TokenHasher.cs:1-23](src/MarketplaceHub.Infrastructure/Security/TokenHasher.cs), [AuthEndpoints.cs:488-489](src/MarketplaceHub.Api/Security/AuthEndpoints.cs)
- CSRF: `/api` altındaki güvensiz metotlarda Origin eşleşmesi + çerez/başlık double-submit (`FixedTimeEquals`); `/api/v1/hooks` hariç — [RequestSecurityMiddleware.cs:19-31,39-53](src/MarketplaceHub.Api/Security/RequestSecurityMiddleware.cs)
- Oturum ömrü: 30 dk boşta / 12 saat mutlak; "Beni Hatırla" 7 gün boşta / 30 gün mutlak; parola değişimi, MFA açma/kapama `SessionVersion`'ı artırıp diğer oturumları iptal ediyor — [SessionLifetime.cs:5-28](src/MarketplaceHub.Api/Security/SessionLifetime.cs), [AuthEndpoints.cs:145-147,247-250,336-339](src/MarketplaceHub.Api/Security/AuthEndpoints.cs)
- Parola politikası 15-64 karakter, büyük/küçük/rakam/simge, yaygın parola listesi; Identity hash (PBKDF2, varsayılan); hesap kilidi 30 deneme/15 dk; ağ başına 5 hata sonrası artan blok — [DependencyInjection.cs:36-45](src/MarketplaceHub.Infrastructure/DependencyInjection.cs), [AuthEndpoints.cs:132-137](src/MarketplaceHub.Api/Security/AuthEndpoints.cs), [SignInThrottle.cs:5-36](src/MarketplaceHub.Api/Security/SignInThrottle.cs)
- TOTP: ±1 adım, `LastAcceptedTimeStep` ile tekrar kullanım engeli (atomik `ExecuteUpdate`); kurtarma kodları HMAC özetli ve partiye bağlı — [TotpService.cs:11-26](src/MarketplaceHub.Infrastructure/Security/TotpService.cs), [AuthEndpoints.cs:275-283,448-455](src/MarketplaceHub.Api/Security/AuthEndpoints.cs)
- Hız limitleri: giriş 10/dk/ağ, adımlar 10/dk/oturum, genel 1800/dk/ağ (IPv6 /64), webhook adres 4800/dk + eşzamanlılık 32 — [RateLimitPolicies.cs:9-52](src/MarketplaceHub.Api/Security/RateLimitPolicies.cs)
- `ForwardedHeaders` yalnız compose `backend` ağına (172.30.10.0/24) güveniyor, `ForwardLimit=1` — [Program.cs:66-71](src/MarketplaceHub.Api/Program.cs), [compose.yaml:263-270](deploy/compose/compose.yaml)

#### Bulgu 2.1 — Girişte tarayıcının önceki oturumu iptal edilmiyor — Düşük — [Kesin]
- `LoginAsync` yeni token üretip çerezi eziyor ama istekte zaten gelen eski oturum çerezinin satırını `Revoked` yapmıyor — [AuthEndpoints.cs:93-100](src/MarketplaceHub.Api/Security/AuthEndpoints.cs)
- Senaryo: ortak bilgisayarda B'nin oturumu açıkken A giriş yaparsa B'nin token'ı sunucuda boşta süresi dolana kadar (30 dk; "Beni Hatırla" ise 7 gün) geçerli kalır; token önceden kopyalanmışsa kullanılabilir.

#### Bulgu 2.2 — `mfa/confirm` hatalı kodları sayaca yazmıyor — Düşük — [Kesin]
- Yanlış kod yalnız `MFA_CODE_INVALID` 400 dönüyor; `SecondFactorFailedAsync`/`PasswordCheck.RegisterFailureAsync` çağrılmıyor (diğer ikinci faktör yolları çağırıyor). Tek koruma `auth-step` 10/dk/oturum limiti — [AuthEndpoints.cs:240-242](src/MarketplaceHub.Api/Security/AuthEndpoints.cs)
- Risk düşük: saldırganın zaten aktif oturumu ve bekleyen kurulum gizli anahtarını bilen kendi uygulaması olmalı; tutarlılık eksiği olarak raporlanmalı.

#### Bulgu 2.3 — Giriş kısıtlama sayaçları bellek içinde — Düşük — [Kesin]
- `SignInThrottle` ağ blokları ve güvenilen ağlar her API yeniden başlatmada (her deploy) sıfırlanıyor; kalıcı tek sınır Identity'nin 30 deneme/15 dk kilidi — [SignInThrottle.cs:22-24](src/MarketplaceHub.Api/Security/SignInThrottle.cs)

#### Bulgu 2.4 — Break-glass geçici parolası stdout'a yazılıyor — Düşük — [Muhtemel]
- `identity reset-password` geçici parolayı `Console.Out`'a basıyor; üretimde servisler journald günlük sürücüsüyle çalışıyor; komut `docker compose run api …` ile çalıştırılırsa parola journald'a düşer (sonraki girişte değiştirme zorunlu olması etkisini sınırlar) — [Program.cs:100-106](src/MarketplaceHub.Api/Program.cs), [compose.production.yaml:34-48](deploy/compose/compose.production.yaml)

### Inferences
- Kimlik bilgisi saklama ve dönüş politikası OWASP önerileriyle uyumlu; anahtar materyalinin (dp-keys + sertifika parolası + `credential_key`) aynı hostta durması tek-host modelinin doğal sınırı.

### Gaps
- `MaskedHint`'in kaç karakter açtığı (`MarketplaceConnectionService.cs:481` civarı `hint` üretimi) incelenmedi.
- Data Protection anahtar rotasyonu/yedek politikası incelenmedi.

## 3. Depoya işlenmiş gizli bilgi ya da gerçek görünümlü anahtar var mı?

### Takeaway
Gerçek bir gizli değer bulunamadı. Üretim gizlileri `deploy/secrets/*` (git dışı) ve `_FILE` ortam değişkenleriyle okunuyor; yalnız geliştirme yapılandırmasında zayıf bir yerel tohum parolası var.

### Cited Findings
- `.gitignore` `.env`, `.env.*` ve `secrets/` dizinini dışlıyor; `.env.example` yalnız yer tutucu ve "never place secrets here" uyarısı içeriyor — [.env.example:1-12](.env.example)
- `appsettings.json` boş `ConnectionStrings:AppDb` ve boş `Security:CredentialKey` içeriyor; değerler `ApplyFileBackedSecrets` ile dosyalardan geliyor — [appsettings.json:2-3](src/MarketplaceHub.Api/appsettings.json), [Program.cs:41](src/MarketplaceHub.Api/Program.cs)
- Üretim compose'u gizlileri Docker secrets olarak `../secrets/*.pfx|*.txt` dosyalarından bağlıyor — [compose.production.yaml:20-21,40-43,82-85](deploy/compose/compose.production.yaml)
- Anahtar/parola deseni taraması (`git ls-files` üzerinde regex) yalnız kod/test/şablon eşleşmeleri verdi: `deploy/scripts/initialize-deployment.sh:64` (etkileşimli okuma), `docs/tasarim-onerileri/sistem-ayarlari.html:828` (UI maketi), `src/MarketplaceHub.Infrastructure/Alerts/AlertEmail.cs:74` (ayrıştırıcı), `tests/ops/test-send-alert.sh:56` (açıkça sahte test değeri) — tarama: bu oturumda çalıştırıldı.

#### Bulgu 3.1 — Geliştirme yapılandırmasında zayıf yerel yönetici parolası — Düşük — [Kesin]
- `LocalDevelopmentSeed` bloğu (e-posta + kısa parola) ve `AllowedHosts: "*"` depoda — [appsettings.Development.json:2-11](src/MarketplaceHub.Api/appsettings.Development.json)
- Yalnız `IsDevelopment()` ve `MARKETPLACEHUB_ENVIRONMENT=PILOT_LOCAL` birlikteyken tohumlanıyor — [Program.cs:90-95](src/MarketplaceHub.Api/Program.cs). Üretimde etkisi yok; geliştirici ortamı dışarı açılırsa risk.

### Inferences
- Git geçmişinde (eski commit'lerde) gizli değer olup olmadığı taranmadı; yalnız mevcut ağaç ve `*.pfx`/`deploy/secrets/*` yolları için `git log --all` sorgusu boş döndü.

### Gaps
- Tam geçmiş taraması (gitleaks/trufflehog benzeri) yapılmadı.

## 4. API'de hesaplanan rapor/dashboard verilerinde mantık hatası var mı (tarih aralıkları, İstanbul/UTC sınırları)?

### Takeaway
Dashboard dönem çözümü doğru görünüyor (kiracı saat diliminde yerel gece yarısı, bitiş günü dahil, en çok 366 gün, ters/gelecek aralık reddi; varsayılan saat dilimi `Europe/Istanbul`). Bir API sözleşmesi tutarsızlığı bulundu: hakediş özetinde `includeTest` uygulanıyor, detay satırlarında sabit `false`.

### Cited Findings
- `DashboardPerformancePeriod.Resolve`: `start > end` ve gelecekte başlayan dönem 400, `MaximumDays = 366`, bitiş sınırı `LocalMidnight(end.AddDays(1), timezone)` (yarı açık aralık) — [DashboardReadService.cs:529-552](src/MarketplaceHub.Infrastructure/Persistence/DashboardReadService.cs)
- Kiracı `Timezone` varsayılanı `"Europe/Istanbul"`; `ResolveTimezone` bulunamazsa Windows/IANA eşdeğerini, o da yoksa UTC'yi deniyor — [IdentityModels.cs:14](src/MarketplaceHub.Domain/IdentityModels.cs), [DashboardReadService.cs:472-481](src/MarketplaceHub.Infrastructure/Persistence/DashboardReadService.cs)
- Gün parametreleri katı `yyyy-MM-dd` ile ayrıştırılıyor — [DashboardEndpoints.cs:37-44](src/MarketplaceHub.Api/Dashboard/DashboardEndpoints.cs)
- Rapor sayfalama sınırları uçta doğrulanıyor (`page` 0-100000, `limit` 1-200, `sort`/`dir` beyaz liste) — [ReportsEndpoints.cs:80-81,114-115](src/MarketplaceHub.Api/Reports/ReportsEndpoints.cs)

#### Bulgu 4.1 — `/reports/payouts/lines` `includeTest`'i yok sayıyor — Düşük — [Kesin] (UI etkisi bugün yok)
- Özet uç `PayoutQuery(..., includeTest ?? false)` kullanıyor; satır ucu sabit `new PayoutQuery(Split(platformCodes), null, false)` gönderiyor ve `ScopeAsync` test mağazalarını `!x.IsTest` ile eliyor — [ReportsEndpoints.cs:100,116](src/MarketplaceHub.Api/Reports/ReportsEndpoints.cs), [PayoutsService.cs:55-59,109-124](src/MarketplaceHub.Infrastructure/Persistence/PayoutsService.cs)
- Senaryo: `GET /reports/payouts?includeTest=true` bir test mağazasının hakedişini gösterir, aynı mağaza için `GET /reports/payouts/lines?connectionId=<test>&…` boş döner (özet ile detay tutmaz). Web istemcisi bugün `includeTest: false` gönderdiği için kullanıcıya yansımıyor — [ReportsPage.tsx:112,1641,1831](src/MarketplaceHub.Web/src/features/reports/ReportsPage.tsx)

### Inferences
- `ReportsService`/`DashboardReadService` içindeki tüm gün kovası hesapları satır satır doğrulanmadı; yalnız dönem çözümü ve sınır kontrolleri incelendi.

### Gaps
- Raporlanan sayıların canlı veriyle karşılaştırması (`/prod-sql`) bu denetimde yapılmadı (salt-okur kod incelemesi).
- `ReportsService` satış kovalarının (gün/hafta/ay) DST'siz İstanbul (UTC+3) için doğru gruplandığı ayrıca test edilmeli.

## 5. Diğer HTTP katmanı kusurları (girdi doğrulama, durum kodları, hata yanıtları, yükleme, SQL, IDOR, yetki tutarlılığı)

### Takeaway
Girdi doğrulama, dosya yükleme (sihirli bayt kontrolü), SSRF koruması, ham SQL (tamamı parametreli) ve kiracı izolasyonu (tüm sorgularda `TenantId` filtresi) sağlam. Kalan kusurlar küçük: yetki kuralları arasında bir tutarsızlık, beklenmeyen istisnalarda tutarsız hata biçimi, birkaç bilgi sızıntısı riski.

### Cited Findings
- Ham SQL: `SqlQueryRaw/ExecuteSqlRaw` kullanımları (9 yer) yalnız sabit metin + `NpgsqlParameter`; `ProductActivityService` dinamik birleştirdiği değerleri sınıf sabitlerinden alıyor (`Literals` "never user input") — [ProductActivityService.cs:44-56,268-273](src/MarketplaceHub.Infrastructure/Persistence/ProductActivityService.cs), [ReportsService.cs:434-461](src/MarketplaceHub.Infrastructure/Persistence/ReportsService.cs), [QueueHealthService.cs:25-46](src/MarketplaceHub.Infrastructure/Persistence/QueueHealthService.cs)
- SSRF: görsel vekili yalnız https, kendi yönlendirme takibi (≤3), `ConnectCallback` ile her çözülen IP'nin genel olması şartı, proxy kapalı, içerik sihirli baytla JPEG/PNG/GIF/WebP — [PublicNetwork.cs:13-124](src/MarketplaceHub.Api/Security/PublicNetwork.cs)
- Yüklemeler: ürün görseli JPEG/PNG sihirli bayt + 6 MiB; iade kanıtı PDF/JPEG/PNG sihirli bayt + 10 MiB; xlsx/csv uzantı + boyut — [CatalogEndpoints.cs:331-351](src/MarketplaceHub.Api/Catalog/CatalogEndpoints.cs), [MarketplaceEndpoints.cs:246-283](src/MarketplaceHub.Api/Marketplace/MarketplaceEndpoints.cs), [CommissionTariffEndpoints.cs:108-130](src/MarketplaceHub.Api/Catalog/CommissionTariffEndpoints.cs)
- Sayfalama: katalog/pazaryeri/fatura `PageSize` 1-200 (aşımda 400), işler/log/aktivite servisleri `Math.Clamp(..., MaximumLimit)`, sorular 1-100, fatura çalışma alanı 20/50/100/200 — [CatalogEndpoints.cs:1020](src/MarketplaceHub.Api/Catalog/CatalogEndpoints.cs), [JobContracts.cs:190](src/MarketplaceHub.Application/JobContracts.cs), [MarketplaceQuestionService.cs:72](src/MarketplaceHub.Infrastructure/Persistence/MarketplaceQuestionService.cs), [InvoicingBillingService.cs:287](src/MarketplaceHub.Infrastructure/Persistence/InvoicingBillingService.cs)
- Pazaryeri 401/403/429 durumları panelin kendi oturum/limit durumlarıyla karışmasın diye 502/503'e çevriliyor; derin `ArgumentException` mesajları genel metne indiriliyor — [MarketplaceErrorStatus.cs:10-45](src/MarketplaceHub.Api/Marketplace/MarketplaceErrorStatus.cs), [RequestArgumentError.cs:6-16](src/MarketplaceHub.Api/Security/RequestArgumentError.cs)
- Güvenlik başlıkları: API `nosniff`, `X-Frame-Options DENY`, `CSP default-src 'self'`; Caddy HSTS, CSP, COOP/CORP, Permissions-Policy; API/hub yanıtları sıkıştırılmıyor (BREACH); günlükte CSRF/idempotency başlıkları ve sorgu dizeleri siliniyor; gizli yol parçaları Serilog'da maskeleniyor — [RequestSecurityMiddleware.cs:13-16](src/MarketplaceHub.Api/Security/RequestSecurityMiddleware.cs), [Caddyfile.production:13-31,118-131](deploy/caddy/Caddyfile.production), [SecretPathEnricher.cs:9-52](src/MarketplaceHub.Api/Security/SecretPathEnricher.cs)
- CORS politikası yok (aynı köken uygulaması); bu doğru.

#### Bulgu 5.1 — Etiket belgesi indirme yetkisi "etiket basıldı" yetkisiyle tutarsız — Düşük — [Muhtemel]
- `POST /orders/{id}/label-printed` `WorkingRoles` (ACCOUNTING dahil; yorum: "accounting prints labels too"), ama `GET /shipments/{id}/documents/{docId}/content` `OperationalRoles` (ACCOUNTING yok) — [RoleAuthorizationMiddleware.cs:61,89-92](src/MarketplaceHub.Api/Security/RoleAuthorizationMiddleware.cs)
- Senaryo: ACCOUNTING kullanıcısı saklı kargo etiketi (ZPL/PDF) belgesini indirmeye çalışınca 403 `PERMISSION_DENIED` alır. Web istemcisinin etiketleri bu uçtan mı yoksa istemci tarafında mı ürettiği doğrulanmadı.

#### Bulgu 5.2 — Beklenmeyen istisnalar panelin hata biçimini kullanmıyor — Düşük — [Muhtemel]
- `ApiExceptionHandler` yalnız `BadHttpRequestException`'ı `code/correlationId/retryable` biçimine çeviriyor; diğer istisnalar varsayılan ProblemDetails (code yok) ile 500 dönüyor — [ApiExceptionHandler.cs:10-18](src/MarketplaceHub.Api/Security/ApiExceptionHandler.cs)
- `ArgumentException → 400` filtresi yalnız Catalog/Marketplace/Invoicing gruplarında; Dashboard/Reports/Jobs/Settings/Questions/AlertEmail gruplarında yok (oradan fırlayan bir `ArgumentException` 500 olur) — [CatalogEndpoints.cs:19-23](src/MarketplaceHub.Api/Catalog/CatalogEndpoints.cs), [DashboardEndpoints.cs:10](src/MarketplaceHub.Api/Dashboard/DashboardEndpoints.cs), [JobEndpoints.cs:9](src/MarketplaceHub.Api/Operations/JobEndpoints.cs)
- Senaryo: eşzamanlılık çatışması (`DbUpdateConcurrencyException`) yakalanmayan bir uçta 409/412 yerine 500 ve `retryable: true` olmayan çıplak ProblemDetails; istemci `code` alanına göre davranıyorsa yanlış mesaj gösterir. Somut bir tetikleyici uç doğrulanmadı.

#### Bulgu 5.3 — Pazaryeri adaptör özeti doğrudan istemciye dönüyor — Düşük — [Muhtemel]
- `instant-process` başarısız eşitlemede `sync.ErrorSummary`'yi kullanıcıya iletiyor (diğer yollar `SafeMessage` kullanıyor) — [MarketplaceEndpoints.cs:312-315](src/MarketplaceHub.Api/Marketplace/MarketplaceEndpoints.cs)
- Senaryo: `ErrorSummary` pazaryeri ham yanıt metni ya da iç ayrıntı içeriyorsa panelde görünür. `JobCompletion.ErrorSummary`'nin kaynağı/temizlenip temizlenmediği doğrulanmadı.

#### Bulgu 5.4 — Eski idempotency kayıt araması kullanıcıya bağlı değil — Düşük — [Kesin]
- Yeni kayıtlar `StoredKey(userId, key)` ile kullanıcıya bağlı; ama "legacy" araması ham `key` ile ve kullanıcısız yapılıyor; eşleşirse 409 gövdesinde önceki `responseStatus/resourceId/jobId` dönüyor — [IdempotencyMiddleware.cs:94-106](src/MarketplaceHub.Api/Catalog/IdempotencyMiddleware.cs)
- Senaryo: aynı kiracıdaki başka bir kullanıcı aynı rota ve aynı ham anahtarla (eski biçimde kalmış IN_PROGRESS/UNKNOWN kayıt) istek atarsa diğer kullanıcının iş kimliğini görür. Yalnız kiracı içi ve yalnız eski kayıtlar; etkisi çok sınırlı.

#### Bulgu 5.5 — `orders/product-image` saklı URL'ye yönlendiriyor — Düşük — [Tahmin]
- Uç `Results.Redirect(value)` yapıyor; `value` `MediaUrls` ile katalog medyasından geliyor ve operasyon rolünün kaydettiği herhangi bir https adresi (`PRODUCT_MEDIA_URL`) olabilir — [MarketplaceEndpoints.cs:117](src/MarketplaceHub.Api/Marketplace/MarketplaceEndpoints.cs), [MarketplaceSalesService.cs:926-990](src/MarketplaceHub.Infrastructure/Persistence/MarketplaceSalesService.cs), [CatalogEndpoints.cs:799-818](src/MarketplaceHub.Api/Catalog/CatalogEndpoints.cs)
- `MediaUrls`'in ham uzak URL mi yoksa panelin vekil yolunu mu döndürdüğü doğrulanmadı; oturum + aynı köken zorunlu olduğundan açık yönlendirme etkisi düşük.

### Inferences
- IDOR: incelenen tüm tekil kayıt sorguları `TenantId == tenant.TenantId` ile filtreleniyor (ör. sevkiyat belgesi join'i, ürün medyası, iş, sipariş finansı); servis katmanındaki tüm sorgular tek tek doğrulanmadı ama API katmanında kiracı kimliği her zaman oturumdan geliyor, istemciden alınmıyor.
- Mass assignment: komutlar ayrı `record` DTO'ları; ayarlar uçları JSON'u şema ile normalleştiriyor (`ShippingLabelSettingsSchema.Normalize`, `TryNormalizeAppearance`) — [SettingsEndpoints.cs:99-147](src/MarketplaceHub.Api/Settings/SettingsEndpoints.cs).

### Gaps
- Frontend DTO beklentileri ile API sözleşmeleri sistematik olarak karşılaştırılmadı (yalnız hakediş `includeTest` örneği incelendi).
- xlsx ayrıştırıcılarının (komisyon tarifesi, promosyon, ürün aktarımı) zip-bomb/XML genişleme dayanıklılığı Application/Infrastructure tarafında incelenmedi.
- `IdempotencyMiddleware`'in yanıtı tamamen belleğe alması (xlsx dışa aktarımları dahil) büyük yanıtlarda bellek baskısı yaratabilir; ölçülmedi.
