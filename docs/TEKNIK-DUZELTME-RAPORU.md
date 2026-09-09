# Teknik Düzeltme ve Doğrulama Raporu

Tarih: 2026-09-09
Kapsam: Kaynak kod incelemesi, sınırlı ve geri alınabilir kod/migration/test hazırlığı
Durum: Kritik veri doğruluğu ve dayanıklılık düzeltmeleri uygulandı; canlı ortam doğrulaması yapılmadı.

## Sonuç özeti

Kaynak kodda 1, 2, 3, 5, 6, 7, 8, 13 ve 14 numaralı bulgular doğrulandı ve bu turda kod değişikliği yapıldı. 4 ve 15 numaralı bulgular koşula bağlı/eksik kapsam olarak ele alındı. 9 ve 10 için mevcut korumalar korundu; eksik negatif veya operasyonel testler ayrıca belirtilmiştir. 11 ve 12 için yeni entegrasyon veya büyük refactor yapılmadı.

Bu çalışma sırasında canlı pazaryeri yazması, gerçek fatura işlemi, production migrationı, veri silme/toplu dönüşüm veya dış yedek hedefe aktarım yapılmadı.

## Bulgular ve uygulanan düzeltmeler

### 1. Fatura teslim durumu / append-only çelişkisi — doğrulandı, düzeltildi

`MarketplaceDelivery` üzerinde append-only koruması varken teslim işlemcisi aynı satırı retry ve doğrulama akışında değiştiriyordu.

Uygulanan çözüm:

- `MarketplaceDelivery` olay/deneme geçmişi olarak immutable bırakıldı.
- Değiştirilebilir son durum için `MarketplaceDeliveryState` eklendi.
- Her yeni başlangıç, timeout, retryable failure, confirmation retry, submitted, failed ve confirmed geçişi ayrı tarihçe kaydı oluşturuyor.
- Trendyol delivery adapter’ında provider idempotency header’ı ve delivery-status sorgusu doğrulanmadığı için timeout, ağ/5xx ve conflict sonucu `UNKNOWN`/manuel uzlaştırma durumuna alınır; sabit dış işlem anahtarı korunur ve körlemesine yeni işlem başlatılmaz. Rate-limit gibi açıkça reddedilen istekler retryable kalır.
- `NotSupported`, uzak sistemin sipariş/package akışı nihai kaynak olduğu açık bir `SUBMITTED`/pending durumuna ayrıldı.
- Eski teslim kayıtları yeni state satırına yazılmadan bootstrap ediliyor; geçmiş kayıtlar değiştirilmiyor.

Bu akış için gerçek PostgreSQL + sahte marketplace port entegrasyon testi henüz eklenmedi. Append-only garantisi ve timeout/süreç çökmesi senaryosu gerçek veritabanı test ortamında ayrıca doğrulanmalıdır.

### 2. Stok/fiyat A → B → A tekilleştirme — doğrulandı, düzeltildi

Önceki iş anahtarı yalnız provider payload hash’ine bağlıydı. Bu nedenle aynı payload değerine dönülen yeni hedef revizyonu geçmiş iş ile karışabiliyordu.

Uygulanan çözüm:

- `PriceInventoryOutboxPolicy` hedef revizyon anahtarını payload hash’inden ayırıyor.
- Anahtar; bağlantı, varyant/teklif, fiyat-stok değeri ve projection/price version bilgilerini içeriyor.
- Aynı revizyon satır sırası değişse de aynı anahtarı üretiyor.
- Poll sonucu yalnızca iş satırının hem fiyat hem stok version’ı hâlâ güncelse güncel kayda işleniyor.
- Eski queued iş, yeni revision’ı başarılı kabul ettiremiyor.

Regresyonlar: 10 → 9 → 10 stok, aynı revizyonun sırası değişmiş payloadı ve eski tamamlanmanın yeni revizyona uygulanmaması test edildi.

### 3. Bölünmüş paket miktarları — doğrulandı, düzeltildi

Önceki `Max` tabanlı satır güncellemesi farklı paketlerdeki miktarların toplamını kaybedebiliyordu.

Uygulanan çözüm:

- Paket-shaped stream sayfası sipariş bazında birleştiriliyor.
- Her paket kimliğinin en güncel olayı ve o olaya ait allocation’ı kullanılıyor.
- `OriginExternalPackageId` ile değiştirilen parent paket, mevcut child paketlerle birlikte çift sayılmıyor.
- Farklı güncel paketler toplanıyor; paket içi fragment doğrulaması ile yeni siparişin tam aggregate doğrulaması ayrılıyor.
- Sipariş satırı miktarı mevcut kayıtta daha küçük bir fragment yüzünden geriye çekilmiyor.

Regresyonlar: iki pakette iki iptal, sırası bozuk/replay allocation, replacement parent ve iki paketten toplam satır miktarı test edildi.

### 4. Stok otoritesi ve rezervasyon yaşam döngüsü — koşula bağlı risk, kısmen düzeltildi

Pazaryeri catalog snapshot’ının fiziksel stok kabul edilmesi ile yerel sipariş rezervasyonunun birlikte uygulanması riskliydi. Ayrıca bazı `ConnectionInventoryPolicy` alanları kullanılmıyordu.

Uygulanan çözüm:

- `ObservedRemoteQuantity` ve `ObservedRemoteAt` alanları eklendi.
- Varsayılan/eksik/bilinmeyen otorite `CENTRAL`; uzak miktar yalnız açıkça `REMOTE_AUTHORITATIVE` seçilirse `OnHand` değerini değiştiriyor.
- Catalog okuması uzak gözlem olarak saklanıyor; yerel fiziksel stok sessizce ezilmiyor.
- Rezervasyon modu, reserve/release status listeleri ve `NONE`/`DISABLED` desteği uygulandı.
- Sevk/teslim miktarı aktif rezervasyon mevcutsa fiziksel stoktan tekil `ORDER_SHIPPED` ledger hareketiyle tüketiliyor.
- Aynı satırın tekrar gözlenmesi ledger toplamı nedeniyle ikinci stok düşümü üretmiyor.
- İlk kez görülen geçmişte sevk edilmiş sipariş, yerel aktif rezervasyon yoksa açılış fiziksel stokunu geriye dönük değiştirmiyor.

İş kuralı kararı: Merkezi stok varsayılan ve güvenli moddur. Uzak stoktan başlangıç/overwrite isteyen bağlantı için `REMOTE_AUTHORITATIVE` açıkça seçilmelidir. İade sonrası otomatik satılabilir stoğa dönüş bu turda yapılmadı; iade kabul/kalite kontrol kararına bağlı kalmalıdır.

Gerçek PostgreSQL eşzamanlı reservation/ledger yarışı ve tenant izolasyonlu transaction testi henüz yoktur.

### 5. Gerçek uzlaştırma / güncellik anlamı — doğrulandı, düzeltildi

`ReconcileStock`, dispatch sonucundan bağımsız `ReconciledAt` ilerletebiliyordu.

`ReconcileStock` artık dispatch sonucunu kontrol ediyor; retry sonucunu üst katmana taşıyor ve başarısız/bloklanmış/yalnızca kuyruğa alınmış işlemi uzlaştırılmış saymıyor. `ReconciledAt` yalnız gerçek catalog gözlemi sırasında güncelleniyor.

### 6. Circuit breaker half-open sahipliği — doğrulandı, düzeltildi

Half-open istek, rate-limit bekleme veya beklenmeyen exception/caller cancellation yolunda bayrağı bırakabiliyordu.

Her çıkış yolunu `resultRecorded` ile ayıran cleanup eklendi. Sonuç kaydedilmeden çıkan half-open deneme sahipliği serbest bırakılıyor. Sahte zaman ve bloklayan HTTP handler ile deterministic toparlanma testi eklendi.

### 7. API idempotency ve transaction sınırı — risk doğrulandı, kısmen düzeltildi

İdempotency kaydı ile iş verisinin commit sınırı ayrıydı; hata halinde kaydın silinmesi, iş verisi commit edilmişken kör retry riskine yol açıyordu.

Uygulanan çözüm:

- Yalnız `COMPLETED` kayıtlar replay süresi bitince siliniyor.
- `IN_PROGRESS`/yarım kalmış kayıtlar `UNKNOWN` durumuna taşınıyor.
- `UNKNOWN` ve `IN_PROGRESS` aynı payload için 409 döndürüyor; aynı anahtarın farklı payloadı 409 `IDEMPOTENCY_KEY_REUSED`.
- Başarılı response gövdesi kalıcı kayda yazılmadan istemciye kopyalanmıyor.
- Endpoint exception/500 sonrası yalnız idempotency durumu `UNKNOWN` olarak saklanmaya çalışılıyor; endpoint’in yarım tracked değişiklikleri yanlışlıkla ayrıca commit edilmiyor.
- Unique yarışında yalnız beklenen PostgreSQL constraint yutuluyor; başka `DbUpdateException` maskelenmiyor.

Bilinen sınır: Aynı DB transaction’ı dış pazaryeri çağrısına exactly-once sağlamaz. Kritik dış işlemler için sabit işlem anahtarı, outbox ve uzlaştırma gerekir. Gerçek HTTP/API integration testi ve commit sonrası bağlantı kopması testi bu turda çalıştırılmadı.

### 8. Ham HTML güven sınırı — doğrulandı, düzeltildi

Catalog açıklama editöründe `dangerouslySetInnerHTML` için görünür bir allowlist sanitizer yoktu.

`DOMPurify` allowlist ile eklendi. Script, event handler, style/class/id, iframe, embed, object, template, SVG ve tehlikeli/data/protocol-relative URI’ler engelleniyor; HTTPS ve kontrollü relative görsel/link biçimleri korunuyor. Sanitization hem görsel editör senkronizasyonunda hem de kaydetme öncesinde uygulanıyor.

Kötücül fixture testleri: script/event/style/javascript URI, iframe/data URI ve geçerli relative path/HTTPS image senaryoları test edildi.

Import preview ve apply akışında ürün açıklaması için sunucu tarafında da aynı dar allowlist yaklaşımı uygulandı. Ham satır `RawJson` içinde denetim izi olarak korunurken, eşleştirme, özet ve apply akışı yalnız `SafeValuesJson` içindeki sanitize edilmiş açıklamayı kullanıyor; böylece içe aktarılan HTML daha sonra hangi görüntüleme noktasına ulaşırsa ulaşsın güvenlik sınırı backend’de de korunuyor.

### 9. Secret ve paketleme hijyeni — mevcut korumalar büyük ölçüde yeterli

`.gitignore` ve `.dockerignore`; `.local`, secret, data-protection key, log, build çıktısı, `.git`, test çıktısı ve arşivleri dışlıyor. Docker restore aşamasında kilit dosyalarının eksik kopyalanması düzeltildi. Secret değerleri rapor/test çıktısına yazılmadı.

Anahtar silme veya döndürme yapılmadı. Gerçek secret rotasyonu ve off-host key saklama operasyon prosedürüdür.

### 10. Tenant korumaları — mevcut korumalar var, eksik negatif test riski sürüyor

Tenant filtreleri, composite foreign key’ler ve bağlantı-tenant kontrolleri korunuyor. Bu turda izolasyonu gevşeten global filtre veya varsayılan tenant bypass’ı eklenmedi.

API/dosya/export/job/SignalR için tenantlar arası negatif integration test paketi bu repoda hazır olmadığı için eklenmedi; production güvenlik iddiası için gereklidir.

### 11. Adaptör seçimi — bulgu koşula bağlı, kapsam korunarak ertelendi

Mevcut çalışan akış Trendyol ve Trendyol E-Faturam ile sınırlı; desteklenmeyen platformlar için altı yeni entegrasyon yazılmadı. Trendyol’a özgü mevcut kapsam ve bağlantı kontrolleri korunuyor. Genel adapter factory refactoru, gerçek platform capability sözleşmeleri olmadan bu turda yapılmadı.

### 12. Büyük servislerin ayrılması — doğrulandı, bilinçli olarak ertelendi

`MarketplaceJobProcessor` büyük ve çok sorumluluklu. Ancak kritik veri düzeltmeleri tamamlanmadan yalnız dosyayı parçalayacak refactor yapılmadı. Sonraki adım Application orkestrasyonu ile Infrastructure erişimini ayıran küçük handler sınırlarını testlerle birlikte çıkarmaktır.

### 13. Scheduler / gözlemlenebilirlik — doğrulandı, düzeltildi

Execution group yalnız group adıyla tutulduğu için farklı tenant/mağaza işlerinin birbirini bekletme riski ve tüm `DbUpdateException` hatalarının yarış gibi yutulması doğrulandı.

Uygulanan çözüm:

- In-memory reservation anahtarı tenant + connection + execution group kapsamına alındı.
- Unique yarış yalnız `IX_jobs_TenantId_JobType_JobDedupKey` PostgreSQL unique violation’ı ise ele alınıyor.
- Diğer DB hataları artık maskelenmiyor.
- Mevcut disabled product automation davranışı korunuyor.

Dashboard snapshot ve bootstrap cevabına kuyruktaki en eski iş, son doğrulanmış senkronizasyon, dead/manual-review iş sayıları, son 24 saatte oluşturulan iş sayısı, bilinen rate-limit iş sayısı ve en eski uzak stok gözlemi eklendi. Arayüzde 429 oranı `bilinen rate-limit kodlu işler / son 24 saatte oluşturulan tüm işler` olarak gösteriliyor; hata kodu olmayan işler de paydaya dahil ediliyor. Bu sayede worker’ın ayakta olması ile işlerin sağlıklı ilerlemesi ayrıştırılabiliyor.

### 14. Dağıtım betikleri — doğrulandı, düzeltildi

- `deploy/**/*.sh` LF olarak normalize edildi.
- CI’a `bash -n` ve `shellcheck` adımı eklendi.
- CI .NET SDK sürümü `global.json` ile aynı `10.0.302` değerine sabitlendi.
- Docker restore öncesinde tüm proje/test `packages.lock.json` dosyaları kopyalanıyor.
- Deployment çalıştırılmadı.

Yerel Windows ortamında WSL/bash çalışmadığı ve `shellcheck` kurulu olmadığı için bu iki komut yerelde başarıyla çalıştırılamadı. Kontrol CI Ubuntu runner’a bırakıldı.

### 15. Yedekleme / geri yükleme — kısmi mevcut çözüm, eksik operasyon kapsamı

Mevcut local backup, checksum, private volume, restore verification ve izole restore drill akışları korundu. Restore drill dış yazmaları kapalı çalışacak şekilde tasarlanmış durumda.

Eksikler: şifreli off-host hedef, gerçek zamanlama, retention politikası ve alarm entegrasyonu için hedef/credential tanımı yok. Bu nedenle dış aktarım yapılmadı ve yapılmış gibi raporlanmadı. Bu bilgiler belirlenmeden güvenli bir hedef veya credential seçmek doğru değildir.

## Değiştirilen dosyalar

- `src/MarketplaceHub.Application/SynchronizationPolicies.cs`: stok rezervasyon/otorite ve fiyat-stok revision politikaları.
- `src/MarketplaceHub.Application/IdempotencyPolicies.cs`: idempotency karar modeli.
- `src/MarketplaceHub.Application/InvoicingContracts.cs`: belirsiz fatura delivery failure policy’si.
- `src/MarketplaceHub.Api/Catalog/IdempotencyMiddleware.cs`: durable unknown/replay/race davranışı.
- `src/MarketplaceHub.Domain/InventoryModels.cs`: uzak stok gözlem alanları.
- `src/MarketplaceHub.Domain/InvoiceModels.cs`: mutable marketplace delivery state modeli.
- `src/MarketplaceHub.Infrastructure/Persistence/MarketplaceJobProcessor.cs`: fiyat-stok stale guard, package aggregate ve stok ledger/rezervasyon akışı.
- `src/MarketplaceHub.Infrastructure/Persistence/PackageIngestionSafety.cs`: package latest/replacement/toplam miktar politikası.
- `src/MarketplaceHub.Infrastructure/Adapters/Trendyol/Mapping/TrendyolJsonMapper.cs`: bölünmüş paket merge davranışı.
- `src/MarketplaceHub.Infrastructure/Adapters/Trendyol/TrendyolResilienceHandler.cs`: half-open cleanup.
- `src/MarketplaceHub.Infrastructure/Persistence/InvoicingJobProcessor.cs`: append-only history + mutable delivery state.
- `src/MarketplaceHub.Infrastructure/Persistence/{AppDbContext,InvoicingModelConfiguration,CatalogModelConfiguration,InventoryService}.cs`: model ve projection bağlantıları.
- `src/MarketplaceHub.Infrastructure/Persistence/ScheduledJobProducer.cs`: scheduler kapsamı ve dar unique-race handling.
- `src/MarketplaceHub.Infrastructure/Persistence/Migrations/20260909193951_AddMarketplaceDeliveryState.*`: fatura delivery state migrationı.
- `src/MarketplaceHub.Infrastructure/Persistence/Migrations/20260909200648_AddInventoryRemoteObservation.*`: uzak stok gözlem migrationı.
- `src/MarketplaceHub.Infrastructure/Persistence/Migrations/20260909203704_AddDashboardOperationalObservability.*`: dashboard operasyon metriği migrationı.
- `src/MarketplaceHub.Domain/DashboardModels.cs`, `src/MarketplaceHub.Application/DashboardContracts.cs`, `src/MarketplaceHub.Infrastructure/Persistence/DashboardReadService.cs`: tenant-scoped dashboard operasyon metrikleri.
- `src/MarketplaceHub.Web/src/app/App.tsx`, `src/MarketplaceHub.Web/src/styles/design-system.css`: işlem sağlığı kartları ve responsive dashboard düzeni.
- `src/MarketplaceHub.Web/src/shared/security/sanitizeHtml.ts` ve test dosyası: allowlist HTML sanitization.
- `src/MarketplaceHub.Infrastructure/Imports/ImportedHtmlSanitizer.cs` ve testi: import açıklamalarında server-side allowlist sanitization.
- `src/MarketplaceHub.Web/package.json`, `package-lock.json`, `vitest.config.ts`: frontend güvenlik test altyapısı.
- `Dockerfile`, `.github/workflows/ci.yml`: lockfile, SDK ve script validation.
- `src/MarketplaceHub.Api/Settings/SettingsEndpoints.cs`: mevcut CI format kontrolündeki pre-existing whitespace ihlali düzeltildi.

Kullanıcının daha önce yaptığı frontend tema/UI değişiklikleri korunmuştur.

## Eklenen regresyon testleri

- `PriceInventoryOutboxPolicyTests`: A → B → A revision key, line order canonicalization, stale completion guard.
- `PackageIngestionSafetyTests`: split/replay/out-of-order/replacement allocation, partial package fragment ve merged line quantity.
- `InventoryPolicyTests`: shipment consumption, reservation status policy, remote authority overwrite koruması.
- `TrendyolResilienceHandlerTests`: caller cancellation sonrası half-open sahiplik serbest bırakılması.
- `ApiIdempotencyPolicyTests`: replay, farklı payload, in-progress ve unknown kararları.
- `InvoiceDeliveryFailurePolicyTests`: belirsiz delivery hatalarının otomatik retry’a dönmemesi.
- `DashboardMetricPolicyTests`: kuyruk durumu, bilinen rate-limit kodları ve dashboard iş kuralı sınıflandırmaları.
- `sanitizeHtml.test.ts`: kötücül HTML ve geçerli rich text fixture’ları.

Bu testlerin saf policy/HTTP kapsamı geçmiştir. PostgreSQL transaction/unique/FK/lease ve gerçek API middleware entegrasyon testleri ayrı test ortamı gerektirir.

## Çalıştırılan kontroller ve sonuçlar

| Kontrol | Sonuç |
|---|---|
| `dotnet build MarketplaceHub.sln -c Release --no-restore` | Başarılı; 0 warning, 0 error |
| `dotnet test MarketplaceHub.sln -c Release --no-build` | Başarılı; 145/145 |
| Kritik hedefli backend test filtresi | Başarılı; 70/70 |
| `dotnet format MarketplaceHub.sln --verify-no-changes --no-restore` | Başarılı |
| EF `migrations has-pending-model-changes` | Başarılı; pending model change yok |
| `npm.cmd run typecheck` | Başarılı |
| `npm.cmd run test:security` | Başarılı; 2/2 |
| `npm.cmd run build` | Başarılı |
| `npm.cmd run check:bundle` | Başarılı; JS 447.6 KiB / 450 KiB, CSS 361.4 KiB / 650 KiB |
| `npm.cmd audit --omit=dev --audit-level=high` | Başarılı; 0 vulnerability |
| `git diff --check` | Başarılı |
| PowerShell deploy shell line-ending kontrolü | Başarılı; tüm `.sh` dosyaları CRLF içermiyor |

Çalıştırılamayan kontroller:

- Yerel `bash -n`: Windows ortamında WSL/bash erişimi yok.
- Yerel `shellcheck`: executable kurulu değil.
- Gerçek PostgreSQL concurrency/integration testleri: testcontainers veya CI PostgreSQL test servisi mevcut değil.
- Gerçek Trendyol/Trendyol E-Faturam Stage smoke testi: credential ve dış yazma izni kullanılmadı.
- Gerçek off-host backup transferi ve restore drill: hedef/credential bulunmadığı ve dış aktarım yetkisi olmadığı için yapılmadı.

## Migration etkisi ve geri dönüş notu

Hazırlanan migrationlar production veritabanında çalıştırılmadı.

1. `20260909193951_AddMarketplaceDeliveryState`
   - `billing.marketplace_deliveries.ExternalIdempotencyKey` nullable kolonu ekler.
   - `billing.marketplace_delivery_states` tablosunu, tenant-scoped unique indexleri ve ilgili foreign key’leri ekler.
   - Down işlemi state tablosunu ve nullable kolonu kaldırır. State kullanılmışsa veri kaybı yaratacağından yalnız kontrollü bakım penceresinde ve yedek sonrası değerlendirilmelidir.

2. `20260909200648_AddInventoryRemoteObservation`
   - `inventory.inventory_items.ObservedRemoteQuantity` ve `ObservedRemoteAt` nullable alanlarını ekler.
   - Tenant + observation timestamp indexi ekler.
   - Down işlemi yalnız gözlem alanlarını/indexi kaldırır; mevcut `OnHand`, `Reserved`, `Available` değerlerini dönüştürmez.

3. `20260909203704_AddDashboardOperationalObservability`
   - `dashboard.snapshot` tablosuna kuyruk yaşı, doğrulanmış senkronizasyon, dead/manual-review, son 24 saat iş/rate-limit sayaçları ve stok gözlem yaşı alanlarını ekler.
   - Sayaç alanları `0` varsayılanıyla geriye dönük mevcut snapshot satırlarını destekler.
   - Down işlemi yalnız bu dashboard alanlarını kaldırır; sipariş, stok, iş veya fatura verisini silmez.

Uygulama sırası: önce production dışı restore/staging veritabanında migration + smoke test, sonra backup doğrulaması, ardından kontrollü production bakım penceresi. Bu görevde `database update` çalıştırılmadı.

## Dış yazmalar açılmadan önce kontrol listesi

- [ ] Migrationlar yedek alınmış izole/staging veritabanında uygulanıp rollback planı doğrulanmalı.
- [ ] `REMOTE_AUTHORITATIVE` kararı her connection için iş sahibi tarafından açıkça onaylanmalı.
- [ ] Gerçek Trendyol Stage hesabında sabit idempotency anahtarı ve provider sonucu doğrulanmalı.
- [ ] Invoice delivery timeout/unknown durumu provider sorgusu veya order webhook ile uzlaştırılmalı.
- [ ] PostgreSQL concurrency testleri ve API integration testleri CI’da çalışır hale getirilmeli.
- [ ] Tenantlar arası API/export/job/SignalR negatif testleri eklenmeli.
- [ ] Off-host backup hedefi, encryption-at-rest sağlayıcısı, retention, zamanlama ve alarm sahibi tanımlanmalı.
- [ ] Restore drill; external writes kapalı, izole ağ ve gerçek secret değerleri loglanmadan çalıştırılmalı.
- [ ] CI Ubuntu üzerinde `bash -n` ve `shellcheck` adımları başarılı görülmeli.
- [ ] Production dış yazma bayrağı ve connection capability kontrolleri ayrı bir release gate olarak doğrulanmalı.

Bu rapor, çalıştırılmayan kontroller için başarı iddiasında bulunmaz; kalan maddeler canlı/entegrasyon doğrulaması gerektirmektedir.
