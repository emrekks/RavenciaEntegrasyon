# Ravencia Entegrasyon — Derin Kod İnceleme Raporu

**Tarih:** 31 Ağustos 2026
**İncelenen commit:** `cfc286d4f4fc95e9d5997c551361c09cbc9b8183`
**Kapsam:** API, worker, domain, persistence, Trendyol adaptörü, React paneli, CSS, testler ve deployment akışı
**İnceleme türü:** Statik kod inceleme, build/test, bağımlılık güvenlik taraması ve canlı sağlık doğrulaması

## 1. Yönetici özeti

Proje derleniyor, mevcut 32 backend testi geçiyor ve bilinen paket güvenlik açığı taramalarında açık görünmüyor. Buna rağmen mevcut test seti gerçek API, veritabanı, worker ve frontend akışlarını kapsamadığı için yeşil build üretim güvenilirliğini kanıtlamıyor.

İncelemede doğrulanan ana sonuçlar:

- **1 kritik**, **7 yüksek**, **10 orta** ve **4 düşük/bakım** öncelikli bulgu vardır.
- Dış sistemden gelmesi gereken webhook POST isteği, webhook doğrulamasına ulaşmadan rol middleware'i tarafından **403** ile engellenebilir.
- Birden fazla paketi bulunan siparişlerde `CANCELLED`/`RETURNED` paket, aktif paketi örterek siparişin tamamını terminal durumda gösterebilir.
- Backend'de tanımlı `PARTIALLY_CANCELLED` ve `RETURN_IN_TRANSIT` durumları uçtan uca tamamlanmamıştır; frontend'in “Tümü” görünümü de gerçek bir tüm-kayıt sorgusu değildir.
- Sipariş ingest akışında sipariş satırı, varyant, paket, event, allocation ve rezervasyon başına ayrı sorgular vardır. Büyük senkronizasyonlarda bu yapı belirgin N+1 yükü üretir.
- Worker'ın iki lease lane'i birbirinden ayrılmamıştır; “background” lane de yüksek öncelikli işleri alabilir. Boşta dahi her saniye transaction ve sorgu çalıştırılır.
- Frontend, sipariş ekranı açıldığında 10 durum için tüm cursor sayfalarını yükler; her gerçek zamanlı invalidation bu yükü tekrar tetikleyebilir.
- CSS dosyası 680 KB, 13.198 satır ve 5.225 `!important` içermektedir. Aynı bileşenler birçok katmanda tekrar override edilmiştir.
- Deployment'ın hızlı yolu test/backup çalıştırmaz, readiness için tek deneme yapar ve commit başına yeni image üretirken eski image'ları temizlemez.

Önerilen sıra: önce webhook ve sipariş durum bütünlüğü, ardından ingest/worker performansı, sonra frontend veri modeli ve test/CI, son olarak CSS ve bundle borcu.

## 2. Doğrulama sonuçları

| Kontrol | Sonuç | Not |
|---|---:|---|
| `dotnet build MarketplaceHub.sln --no-restore` | Başarılı | 0 uyarı, 0 hata |
| `dotnet test MarketplaceHub.sln --no-build` | Başarılı | 32/32 test, 0 atlanan |
| `npm run build` | Başarılı | JS ve CSS bundle boyutu uyarısı var |
| Frontend JS | 733,99 KB | gzip 196,78 KB; Vite 500 KB uyarısı |
| Frontend CSS | 575,98 KB minified | gzip 88,47 KB; kaynak dosya 680.677 byte |
| `npm audit --omit=dev` | Temiz | 0 bilinen açık |
| `dotnet list ... --vulnerable --include-transitive` | Temiz | Tüm projelerde 0 bilinen açık |
| `dotnet format --verify-no-changes --no-restore` | Başarısız | 6.600 biçim tanısı: 6.516 satır sonu, 77 boşluk, 4 import, 3 charset |
| Canlı readiness | Başarılı | `https://panel.ravencia.com/health/ready` → HTTP 200 / `Healthy` |

### Test kapsamı gerçeği

Test projesinde yalnızca üç kaynak dosyası vardır:

- `MandatorySynchronizationScenariosTests.cs`: 22 `[Fact]/[Theory]` tanımı
- `ProductPublicationComposerTests.cs`: 2 tanım
- `TrendyolResilienceHandlerTests.cs`: 3 tanım

Theory satırlarıyla toplam 32 test çalışır. Testlerde `WebApplicationFactory`, `TestServer`, gerçek `AppDbContext`, SQLite/PostgreSQL fixture, Playwright, Vitest/Jest veya Testing Library kullanımı yoktur. Bu nedenle middleware sırası, SQL davranışı, transaction, gerçek worker leasing, frontend state ve deployment akışı test edilmemektedir.

## 3. Kritik ve yüksek öncelikli bulgular

### K-01 — Webhook POST isteği rol middleware'inde engelleniyor

**Önem:** Kritik
**Alan:** Güvenlik / entegrasyon doğruluğu

**Kanıt**

- Webhook endpoint'i [`MarketplaceEndpoints.cs:125`](../src/MarketplaceHub.Api/Marketplace/MarketplaceEndpoints.cs#L125) üzerinde `POST /api/v1/hooks/{connectionPublicId}/{routeToken}` olarak tanımlı.
- CSRF katmanı webhook'u bilinçli olarak muaf tutuyor: [`RequestSecurityMiddleware.cs:19`](../src/MarketplaceHub.Api/Security/RequestSecurityMiddleware.cs#L19).
- Pipeline sırası `RequestSecurity → SessionAuth → SessionStateBoundary → RoleAuthorization → Idempotency`: [`Program.cs:83`](../src/MarketplaceHub.Api/Program.cs#L83).
- Rol katmanı yalnız auth yolu, GET ve HEAD için bypass yapıyor: [`RoleAuthorizationMiddleware.cs:15`](../src/MarketplaceHub.Api/Security/RoleAuthorizationMiddleware.cs#L15).
- Dış webhook isteğinde kullanıcı rolü bulunmadığından `allowed=false` olur ve 403 üretilir: [`RoleAuthorizationMiddleware.cs:21`](../src/MarketplaceHub.Api/Security/RoleAuthorizationMiddleware.cs#L21), [`RoleAuthorizationMiddleware.cs:29`](../src/MarketplaceHub.Api/Security/RoleAuthorizationMiddleware.cs#L29).

**Etki**

Trendyol webhook'u route token ve imza doğrulamasına ulaşamaz. Sipariş ve durum değişiklikleri yalnız polling/recovery ile fark edilir; panel gecikir ve webhook tabanlı anlık güncelleme fiilen çalışmaz.

**Öneri**

1. `/api/v1/hooks` yolunu rol middleware'inden açıkça muaf tutun.
2. Muafiyetin yalnız webhook yoluyla sınırlı kaldığını doğrulayın.
3. Endpoint içinde route token, connection, signature/API key doğrulamasını zorunlu tutun.
4. İmzasız, yanlış token'lı ve geçerli webhook için `WebApplicationFactory` entegrasyon testi ekleyin.

**Kabul testi:** Oturumsuz geçerli webhook 2xx; yanlış token/imza 401/403; diğer oturumsuz POST endpoint'ler 403 kalmalı.

---

### Y-01 — Parçalı paketlerde siparişin türetilmiş durumu yanlış terminalleşiyor

**Önem:** Yüksek
**Alan:** Sipariş mantığı / veri bütünlüğü

**Kanıt**

- Ingest sonunda sipariş durumu paketlerin `StatusRank` sırasındaki en yüksek değerinden seçiliyor: [`MarketplaceJobProcessor.cs:2117`](../src/MarketplaceHub.Infrastructure/Persistence/MarketplaceJobProcessor.cs#L2117).
- Aynı yaklaşım anlık işlemde ve reconciliation'da da tekrar edilmiş: [`MarketplaceSalesService.cs:261`](../src/MarketplaceHub.Infrastructure/Persistence/MarketplaceSalesService.cs#L261), [`MarketplaceReconciliationService.cs:33`](../src/MarketplaceHub.Infrastructure/Persistence/MarketplaceReconciliationService.cs#L33).
- `CANCELLED` ve `RETURNED` rank 9, `PROCESSING` rank 2: [`MarketplaceJobProcessor.cs:2555`](../src/MarketplaceHub.Infrastructure/Persistence/MarketplaceJobProcessor.cs#L2555).
- Domain'de `PartiallyCancelled` durumu tanımlı olmasına rağmen aggregate algoritması bunu kombinasyondan üretmiyor: [`OrderModels.cs:11`](../src/MarketplaceHub.Domain/OrderModels.cs#L11).

**Hata örneği**

Bir siparişte Paket A `CANCELLED`, Paket B `PROCESSING` ise maksimum rank nedeniyle sipariş `CANCELLED` olur. Frontend bu siparişi kapalı kabul eder ve aktif paket için işlem kontrolleri kaybolabilir.

**Öneri**

Tek bir merkezi `OrderAggregateStatusPolicy` oluşturun. Durumu yalnız “en yüksek rank” ile değil paket kombinasyonuyla hesaplayın. Örneğin:

- tüm paketler cancelled → `CANCELLED`
- cancelled + aktif paket → `PARTIALLY_CANCELLED` veya aktif aggregate
- tüm paketler delivered → `DELIVERED`
- delivered + return-in-transit → return aggregate

Bu politika ingest, anlık işlem, reconciliation ve read modelde tek kaynaktan kullanılmalı. Mevcut kayıtlar için backfill migration/job düşünülmeli.

**Kabul testi:** En az 2 paketli kombinasyon matrisi; cancelled+processing, cancelled+shipped, delivered+return-in-transit, returned+active durumları.

---

### Y-02 — Sipariş durum modeli uçtan uca eksik; “Tümü” bazı siparişleri göstermiyor

**Önem:** Yüksek
**Alan:** Backend/frontend sözleşmesi

**Kanıt**

- Domain'de `PARTIALLY_CANCELLED` ve `RETURN_IN_TRANSIT` var: [`OrderModels.cs:11`](../src/MarketplaceHub.Domain/OrderModels.cs#L11), [`OrderModels.cs:14`](../src/MarketplaceHub.Domain/OrderModels.cs#L14).
- Raw package mapper bu durumları tanımıyor ve varsayılan olarak `ManualReview` döndürüyor: [`MarketplaceJobProcessor.cs:2553`](../src/MarketplaceHub.Infrastructure/Persistence/MarketplaceJobProcessor.cs#L2553).
- Frontend sorgu listesi bu iki durumu içermiyor: [`MarketplacePages.tsx:40`](../src/MarketplaceHub.Web/src/MarketplacePages.tsx#L40).
- Frontend “Tümü” için backend'e tüm kayıt sorgusu göndermiyor; 10 durum sorgusunu birleştiriyor: [`MarketplacePages.tsx:744`](../src/MarketplaceHub.Web/src/MarketplacePages.tsx#L744), [`MarketplacePages.tsx:764`](../src/MarketplaceHub.Web/src/MarketplacePages.tsx#L764).
- Backend status filtresi exact equality kullanıyor: [`MarketplaceSalesService.cs:19`](../src/MarketplaceHub.Infrastructure/Persistence/MarketplaceSalesService.cs#L19).

**Etki**

Bu durumlara sahip mevcut veya ileride oluşacak kayıtlar “Tümü” dahil sipariş ekranından kaybolabilir. Backend enum, mapper, aggregate, summary, frontend query ve tab sözleşmesi birbirinden kopuktur.

**Öneri**

1. Tek canonical status sözleşmesi oluşturun ve backend/frontend tarafından üretin.
2. Raw mapper'a tüm sağlayıcı karşılıklarını ekleyin.
3. “Tümü” için status parametresiz gerçek server sorgusu kullanın.
4. Liste/summary endpoint'lerini aynı filtreleme semantiğine bağlayın.
5. Contract test ile her enum değerinin API ve UI karşılığını doğrulayın.

---

### Y-03 — Sipariş ingest akışında ağır N+1 sorgu zinciri var

**Önem:** Yüksek
**Alan:** Veritabanı performansı / senkronizasyon süresi

**Kanıt**

`UpsertOrder` içinde:

- sipariş sorgusu: [`MarketplaceJobProcessor.cs:2054`](../src/MarketplaceHub.Infrastructure/Persistence/MarketplaceJobProcessor.cs#L2054)
- repair paketleri: satır 2058
- her remote line için bir veya iki order-line sorgusu: satır 2068–2070
- her line için varyant çözümleme: satır 2073
- her package için package sorgusu: satır 2080
- her package için history existence sorgusu: satır 2082
- her allocation için allocation sorgusu: satır 2114
- siparişin tüm paket durumları için ek sorgu: satır 2117
- her line için rezervasyon projeksiyonu: satır 2118
- her sipariş sonunda `SaveChanges`: satır 2119

Üst seviye senkronizasyon bu metodu siparişler üzerinde sıralı çağırır. Satır/paket sayısı yükseldikçe round-trip sayısı doğrusal değil, iç içe büyür.

**Etki**

Büyük sipariş batch'lerinde worker uzun süre DB bekler; connection pool baskısı, lease süresi aşımı ve senkronizasyon gecikmesi oluşabilir.

**Öneri**

- Batch içindeki external order/package/line kimliklerini önce toplayıp toplu yükleyin.
- Varyantları SKU/barcode dictionary olarak batch başında okuyun.
- Existing history ve allocations için toplu key setleri kullanın.
- Rezervasyonları batchleyin.
- Transaction ve `SaveChanges` sınırını güvenli bir sipariş grubu seviyesine taşıyın.
- EF command counter veya OpenTelemetry ile “sipariş başına SQL sayısı” regresyon testi ekleyin.

**Hedef:** Normal sipariş için sabit sayıda toplu sorgu; satır/paket başına yeni round-trip olmaması.

---

### Y-04 — Worker lane'leri çakışıyor; background işler aç kalabilir

**Önem:** Yüksek
**Alan:** Worker scheduling / performans

**Kanıt**

- Worker bir hot lane (`maximumPriority <= 2`) ve bir background lane (`null`) başlatıyor: [`Worker.cs:14`](../src/MarketplaceHub.Worker/Worker.cs#L14).
- `null` lane için alt öncelik sınırı yok; o da priority 0–2 işi lease edebilir.
- Lease sorgusu priority ascending çalışıyor: [`JobLeaseService.cs:21`](../src/MarketplaceHub.Infrastructure/Persistence/JobLeaseService.cs#L21).
- Her lane yaklaşık her saniye döner: [`Worker.cs:37`](../src/MarketplaceHub.Worker/Worker.cs#L37).
- Her lease denemesi önce `ReapExpiredAsync`, sonra `TryLeaseAsync` çağırır: [`Worker.cs:70`](../src/MarketplaceHub.Worker/Worker.cs#L70).
- `ReapExpiredAsync` boş sonuçta dahi transaction açıp commit eder: [`JobLeaseService.cs:116`](../src/MarketplaceHub.Infrastructure/Persistence/JobLeaseService.cs#L116).

**Etki**

- Sürekli hot backlog varsa priority 3–6 işler starvation yaşayabilir.
- Worker boşta olsa bile iki lane her saniye transaction/sorgu yükü üretir.
- Reaper iki lane tarafından gereksiz tekrarlanır.

**Öneri**

Lane'lere ayrık priority aralığı verin: hot `<=2`, background `>=3`. Reaper'ı tek scheduler döngüsüne taşıyın. Boş poll için exponential backoff + notification (`LISTEN/NOTIFY`) veya daha uzun adaptif interval kullanın. Gerçek `JobLeaseService` ve PostgreSQL ile starvation/lock entegrasyon testi ekleyin.

---

### Y-05 — Sipariş ekranı ve realtime invalidation istek patlaması üretiyor

**Önem:** Yüksek
**Alan:** Frontend performansı / API yükü

**Kanıt**

- Frontend 10 status için paralel sorgu açıyor: [`MarketplacePages.tsx:40`](../src/MarketplaceHub.Web/src/MarketplacePages.tsx#L40), [`MarketplacePages.tsx:744`](../src/MarketplaceHub.Web/src/MarketplacePages.tsx#L744).
- Her status sorgusu `loadAllPages` ile cursor bitene kadar tüm sayfaları alıyor: [`MarketplacePages.tsx:34`](../src/MarketplaceHub.Web/src/MarketplacePages.tsx#L34), [`api.ts:69`](../src/MarketplaceHub.Web/src/api.ts#L69).
- SignalR `orders` event'i order query ağacını invalidate ediyor: [`App.tsx:80`](../src/MarketplaceHub.Web/src/App.tsx#L80).
- Order endpoint'i her sayfa için orders, lines, packages, invoices, connections, variants ve media sorguları çalıştırıyor: [`MarketplaceSalesService.cs:20`](../src/MarketplaceHub.Infrastructure/Persistence/MarketplaceSalesService.cs#L20).

**Etki**

Tek bir operasyon olayı, aktif 10 query'nin her cursor sayfasını yeniden almasına yol açabilir. Sipariş sayısı arttıkça API ve DB yükü katlanır; UI gereksiz büyük veri tutar.

**Öneri**

- Gerçek server-side status/search/sort/pagination kullanın.
- Ekranda yalnız seçili tabın sayfasını yükleyin; tab sayaçları summary endpoint'ten gelsin.
- Realtime event payload'ında değişen order ID/status gönderin ve cache'i noktasal patch edin.
- Invalidations için debounce/coalescing ekleyin.
- React Query `staleTime` ve refetch politikasını açıkça belirleyin.

---

### Y-06 — Idempotency aynı isteğin başarılı sonucunu replay etmiyor

**Önem:** Yüksek
**Alan:** API güvenilirliği

**Kanıt**

- Aynı key ve aynı hash bulunduğunda middleware `409 IDEMPOTENCY_REPLAY` döndürüyor: [`IdempotencyMiddleware.cs:42`](../src/MarketplaceHub.Api/Catalog/IdempotencyMiddleware.cs#L42).
- Yalnız status code kaydediliyor: [`IdempotencyMiddleware.cs:69`](../src/MarketplaceHub.Api/Catalog/IdempotencyMiddleware.cs#L69).
- Response body/header kaydedilmiyor.
- Conflict body içinde `ResourceId` ve `JobId` dönülüyor, fakat middleware veya servislerde bu alanlara atama yok: [`IdempotencyMiddleware.cs:49`](../src/MarketplaceHub.Api/Catalog/IdempotencyMiddleware.cs#L49).

**Etki**

Sunucu işlemi başarıyla tamamladıktan sonra istemci cevabı kaybederse aynı key ile retry başarı sonucunu alamaz. Kullanıcı yeni key ile tekrar denerse dış yan etki tekrar üretilebilir.

**Öneri**

Başarılı response status, gerekli header'lar ve body'yi sınırlı boyutta kaydedip aynı request hash için aynen replay edin. Uzun süren işlerde en az resource/job kimliğini deterministik kaydedin. `IN_PROGRESS`, completed replay, farklı body ile key reuse ve concurrent race entegrasyon testleri ekleyin.

---

### Y-07 — Çoklu tenant şeması ile giriş akışı çelişiyor

**Önem:** Yüksek
**Alan:** Kimlik doğrulama / tenant modeli

**Kanıt**

- Şema yalnız `(TenantId, UserId)` çiftini unique yapıyor; aynı kullanıcı farklı tenant'larda bulunabilir: [`AppDbContext.cs:152`](../src/MarketplaceHub.Infrastructure/Persistence/AppDbContext.cs#L152).
- Login aktif üyeliği `SingleAsync(UserId + Active)` ile bekliyor: [`AuthEndpoints.cs:52`](../src/MarketplaceHub.Api/Security/AuthEndpoints.cs#L52).
- Parola değişimi ve MFA challenge yalnız `UserId` ile `SingleAsync` kullanıyor: [`AuthEndpoints.cs:78`](../src/MarketplaceHub.Api/Security/AuthEndpoints.cs#L78), [`AuthEndpoints.cs:170`](../src/MarketplaceHub.Api/Security/AuthEndpoints.cs#L170).

**Etki**

Bir kullanıcı iki tenant'a üye olduğunda giriş/MFA akışı `Sequence contains more than one element` ile 500 verebilir. Eğer ürün bilinçli olarak tek-tenant üyelik istiyorsa veritabanı bunu zorlamıyor; çoklu tenant istiyorsa tenant seçim akışı eksik.

**Öneri**

Ürün kararını açıklaştırın:

- tek tenant/user ise `UserId` üzerinde unique constraint;
- çoklu tenant ise login sonrası üyelik listesi + tenant seçimi ve session tenant switch endpoint'i.

Her iki durumda da `SingleAsync` yerine domain invariant'ını kontrollü hata ile doğrulayın ve test ekleyin.

## 4. Orta öncelikli bulgular

### O-01 — Order read model CPU tarafında tekrar tekrar liste tarıyor

[`MarketplaceSalesService.cs:35`](../src/MarketplaceHub.Infrastructure/Persistence/MarketplaceSalesService.cs#L35) sonrasında her order için `lines.Where`, `packages.FirstOrDefault`, `invoices.FirstOrDefault`, `packages.Where` ve `packages.Count` tekrar çalışıyor. Bu, sayfa içindeki order ve child row sayılarıyla O(n×m) maliyet üretir.

**Çözüm:** `linesByOrder`, `packagesByOrder`, `invoiceByOrder` dictionary/lookup'larını tek kez oluşturun. `limit+1` sentinel order için child kayıtlarını yüklemeden önce gerçek sayfa order ID'lerini ayırın.

---

### O-02 — Realtime broadcaster aynı timestamp'teki event'leri atlayabilir

Broadcaster `CompletedAt > watermark`, `Take(250)` ve yalnız timestamp watermark kullanıyor: [`OperationsRealtimeBroadcaster.cs:21`](../src/MarketplaceHub.Api/Realtime/OperationsRealtimeBroadcaster.cs#L21), [`OperationsRealtimeBroadcaster.cs:23`](../src/MarketplaceHub.Api/Realtime/OperationsRealtimeBroadcaster.cs#L23), [`OperationsRealtimeBroadcaster.cs:27`](../src/MarketplaceHub.Api/Realtime/OperationsRealtimeBroadcaster.cs#L27).

Aynı `CompletedAt` değerine sahip 250'den fazla job varsa ilk 250 sonrası watermark aynı timestamp'e ilerler; kalanlar `>` filtresi nedeniyle görünmez.

**Çözüm:** Cursor'ı `(CompletedAt, Id)` çifti yapın veya aynı timestamp grubunu tüketene kadar loop edin.

---

### O-03 — SignalR ilk bağlantı hatası sessizce kalıcı olabilir

Frontend `withAutomaticReconnect` kullanıyor fakat ilk `connection.start()` çağrısını `void` ile bırakıyor: [`App.tsx:77`](../src/MarketplaceHub.Web/src/App.tsx#L77), [`App.tsx:96`](../src/MarketplaceHub.Web/src/App.tsx#L96). Automatic reconnect, başarılı ilk bağlantıdan sonraki kopmalar içindir; ilk start hatasında explicit retry/catch yoktur.

**Çözüm:** İlk start için bounded retry/backoff, `onclose` yeniden başlatma ve kullanıcıya degraded-realtime durumu ekleyin.

---

### O-04 — Geçersiz pagination girdileri 400 yerine 500'e dönüşebilir

- Limit validator `ArgumentException` fırlatıyor: [`MarketplaceEndpoints.cs:188`](../src/MarketplaceHub.Api/Marketplace/MarketplaceEndpoints.cs#L188).
- Cursor decoder'lar da `ArgumentException` fırlatıyor: ör. [`MarketplaceSalesService.cs:852`](../src/MarketplaceHub.Infrastructure/Persistence/MarketplaceSalesService.cs#L852), [`CatalogService.cs:835`](../src/MarketplaceHub.Infrastructure/Persistence/CatalogService.cs#L835).
- Global pipeline yalnız varsayılan `UseExceptionHandler` kullanıyor: [`Program.cs:75`](../src/MarketplaceHub.Api/Program.cs#L75).

**Çözüm:** Endpoint'te `Results.ValidationProblem`/400 üretin veya merkezi exception mapper ile validation exception'larını RFC 7807 400'e çevirin.

---

### O-05 — İade kanıtı dosya türü yalnız istemci MIME bilgisine güveniyor

Return evidence endpoint yalnız `file.ContentType` kontrol ediyor: [`MarketplaceEndpoints.cs:138`](../src/MarketplaceHub.Api/Marketplace/MarketplaceEndpoints.cs#L138). Buna karşılık ürün görselleri magic-byte doğruluyor: [`CatalogEndpoints.cs:210`](../src/MarketplaceHub.Api/Catalog/CatalogEndpoints.cs#L210), [`CatalogEndpoints.cs:212`](../src/MarketplaceHub.Api/Catalog/CatalogEndpoints.cs#L212); fatura upload da MIME tespiti yapıyor: [`InvoicingEndpoints.cs:84`](../src/MarketplaceHub.Api/Invoicing/InvoicingEndpoints.cs#L84).

**Etki:** PDF/JPEG/PNG etiketiyle arbitrary içerik saklanıp sağlayıcıya gönderilebilir; sağlayıcı reddi ve içerik türü karışıklığı oluşur.

**Çözüm:** PDF/JPEG/PNG magic-byte doğrulamasını ortak `FileTypeDetector` servisinde birleştirin; extension ve stored MIME'ı tespit edilen türden üretin.

---

### O-06 — Hızlı deployment kırılgan ve disk biriktiriyor

- Hızlı deploy açıkça test ve backup çalıştırmıyor: [`fast-deploy.sh:4`](../deploy/scripts/fast-deploy.sh#L4).
- Her değişiklikte app ve edge image yeniden build ediliyor: [`fast-deploy.sh:32`](../deploy/scripts/fast-deploy.sh#L32).
- Readiness yalnız tek `curl` denemesi: [`fast-deploy.sh:38`](../deploy/scripts/fast-deploy.sh#L38).
- Tam deploy ise 30 denemeli readiness kullanıyor: [`deploy.sh:65`](../deploy/scripts/deploy.sh#L65).
- Image adı her committe benzersiz: [`fast-deploy.sh:29`](../deploy/scripts/fast-deploy.sh#L29); deploy klasöründe eski image cleanup yok.

**Etki:** Uygulama başlangıcı birkaç saniye uzarsa başarılı deploy başarısız raporlanabilir. Eski image'lar zamanla disk doldurur. CSS-only değişiklikte dahi tüm .NET image build edilir.

**Çözüm:** Hızlı yola bounded readiness retry, frontend asset check, worker health ve kontrollü image retention ekleyin. CI'da test edilmiş commit artefaktı üretip sunucuda yeniden build etmek yerine immutable image deploy edin.

---

### O-07 — CSS override zinciri sürdürülebilir değil

Mevcut [`app.css`](../src/MarketplaceHub.Web/src/app.css) ölçümleri:

- 13.198 satır / 680.677 byte
- 5.225 `!important`
- `.bulk-action`: 65 kullanım
- `.orders-reference-filter-shell`: 53 kullanım
- `.bulk-action-menu`: 36 kullanım
- `.shipping-designer-block`: 23 kullanım
- `.workspace-modal`: 18 kullanım

Örneğin `.bulk-action-menu` temel light stilini satır 294'te alıyor, sonra satır 721, 779, 1312, 6909, 9638, 11608, 12323 ve 13112 civarında tekrar override ediliyor. Son görünüm dosya sonundaki yüksek specificity + `!important` kurallarına bağlıdır.

**Etki:** Bir ekran düzeltmesi başka breakpoint veya modalı bozabilir; kullanılmayan selector tespiti zorlaşır; CSS parse/download maliyeti yükselir.

**Çözüm:** Token → base → component → page sırasıyla yeniden modülerleştirin. `orders.css`, `products.css`, `shipping-designer.css` gibi route bazlı dosyalara ayırın. Yeni katmanda `!important` yasaklayıp Stylelint `no-descending-specificity` ve selector testleri ekleyin. Eski katmanları davranış testleriyle kademeli silin.

---

### O-08 — Frontend tek büyük bundle ve route'lar eager yükleniyor

[`App.tsx:6`](../src/MarketplaceHub.Web/src/App.tsx#L6) tüm ana sayfaları eager import ediyor; `React.lazy`/`Suspense` veya dynamic import yok. Üretim build'i tek 733,99 KB JS chunk için uyarı veriyor. CSS de tek 575,98 KB minified dosya.

**Çözüm:** Route-level lazy loading; products/orders/settings için ayrı chunk; ağır designer/print bileşenlerini etkileşim anında yükleme. Bundle budget CI gate ekleyin.

---

### O-09 — Test piramidinin API, DB, worker ve frontend katmanları eksik

32 testin tamamı dar kapsamlı unit/policy testidir. `MandatorySynchronizationScenariosTests` gibi senaryolar gerçek worker lane'ini, SQL'i veya endpoint'i çalıştırmaz. `.github` altında çalışan bir CI workflow ve frontend test script'i bulunmamaktadır.

**Çözüm önceliği:**

1. Webhook middleware entegrasyon testleri.
2. PostgreSQL/Testcontainers ile ingest, lease ve idempotency testleri.
3. Aggregate status kombinasyon testleri.
4. React Testing Library ile tab/cache/state testleri.
5. Playwright ile giriş, sipariş işleme, sayaç güncelleme ve dropdown z-index smoke testleri.
6. CI: format, build, test, frontend build, migration drift, dependency audit.

---

### O-10 — İptal siparişi gizleme kalıcı ve geri alınamaz bir yerel filtre

`Tümünden gizle` order ID'sini localStorage'a ekliyor: [`MarketplacePages.tsx:568`](../src/MarketplaceHub.Web/src/MarketplacePages.tsx#L568), [`MarketplacePages.tsx:587`](../src/MarketplaceHub.Web/src/MarketplacePages.tsx#L587). Parent bu kayıtları tüm listeden çıkarıyor: [`MarketplacePages.tsx:740`](../src/MarketplaceHub.Web/src/MarketplacePages.tsx#L740), [`MarketplacePages.tsx:766`](../src/MarketplaceHub.Web/src/MarketplacePages.tsx#L766). Geri göster/temizle UI'si ve stale ID pruning yoktur.

**Etki:** Aynı kullanıcı farklı tarayıcıda farklı sayılar görür; gizlenen kayıt fark edilmeden kalabilir.

**Çözüm:** Özelliği kaldırın veya “Gizlenen siparişler / geri göster” yönetimi ekleyin. Sunucu taraflı kullanıcı tercihi gerekiyorsa açık bir preference modeli kullanın.

## 5. Düşük öncelikli ve bakım bulguları

### D-01 — Her order row render'ında localStorage tekrar okunuyor

Parent ayarları zaten state'te tutarken her `OrderReferenceRow` `loadShippingLabelSettings()` ve `loadPrintedShippingLabels()` çağırıyor: [`MarketplacePages.tsx:561`](../src/MarketplaceHub.Web/src/MarketplacePages.tsx#L561). 50–200 satırlık sayfada her rerender senkron JSON parse/storage okuması üretir.

**Çözüm:** Ayar ve printed set'i parent'tan prop/context ile geçirin; row içinde yalnız key lookup yapın.

---

### D-02 — Kullanılmayan bağımlılık ve ölü frontend kodu var

- `@tanstack/react-table` ve `react-hook-form` `package.json` içinde bulunuyor fakat `src` altında import edilmiyor.
- Tam bir legacy component yorum bloğunda tutuluyor: [`MarketplacePages.tsx:601`](../src/MarketplaceHub.Web/src/MarketplacePages.tsx#L601).
- Export edilip route/import edilmeyen sayfalar: [`InvoiceDetailPage`](../src/MarketplaceHub.Web/src/InvoicingPages.tsx#L76), [`BillingSettingsPage`](../src/MarketplaceHub.Web/src/InvoicingPages.tsx#L107), [`AttributeMappingPage`](../src/MarketplaceHub.Web/src/MarketplacePages.tsx#L1360).
- İlgili route'lar gerçek sayfa yerine redirect ediyor: [`App.tsx:66`](../src/MarketplaceHub.Web/src/App.tsx#L66).

**Çözüm:** Kullanılmayan paketleri kaldırın; legacy kodu git geçmişine bırakın; sayfaları route'a bağlayın veya silin. TypeScript'te `noUnusedLocals`/`noUnusedParameters`, ESLint ve depcheck ekleyin.

---

### D-03 — `JsonDocument` dispose edilmiyor

[`MarketplaceSalesService.cs:604`](../src/MarketplaceHub.Infrastructure/Persistence/MarketplaceSalesService.cs#L604) `JsonDocument.Parse(settings).RootElement` kullanıyor fakat document dispose edilmiyor. Hemen alttaki metot doğru şekilde `using var doc` kullanıyor.

**Çözüm:** `using var doc = JsonDocument.Parse(settings);` kullanın. Etki düşük olsa da sık çağrıda pooled buffer/GC baskısı oluşturur.

---

### D-04 — Biçim ve satır sonu standardı fiilen uygulanmıyor

`dotnet format --verify-no-changes` 6.600 tanıyla başarısızdır. Build warning-as-error olsa da format CI gate olmadığı için bu borç büyür. En yoğun dosyalar `MarketplaceJobProcessor.cs`, `InvoicingBillingService.cs`, `InvoicingJobProcessor.cs`, `CatalogModelConfiguration.cs`, `AuthEndpoints.cs` ve `TrendyolHttpClient.cs`.

**Çözüm:** Önce tek bir mekanik format commit'i, ardından CI format gate. CRLF/LF kararını `.editorconfig` ve `.gitattributes` ile sabitleyin.

## 6. Mimari ve bakım riski

### Büyük dosyalar

| Dosya | Satır | Yaklaşık boyut |
|---|---:|---:|
| `app.css` | 13.198 | 680 KB |
| `MarketplaceJobProcessor.cs` | 2.557 | 224 KB |
| `MarketplacePages.tsx` | 1.453 | 274 KB |
| `CatalogPages.tsx` | 1.162 | 146 KB |
| `MarketplaceSalesService.cs` | 861 | 88 KB |
| `App.tsx` | 763 | 96 KB |

Bu dosyalar birden fazla sorumluluğu aynı yerde topluyor. Özellikle `MarketplaceJobProcessor` order, return, product, inventory, cursor, recovery ve telemetry davranışlarını aynı sınıfa taşıyor. Değişikliklerin etki alanı genişliyor ve unit test izolasyonu zorlaşıyor.

**Önerilen bölme:**

- `OrderIngestionService`
- `PackageProjectionService`
- `InventoryReservationProjector`
- `ReturnIngestionService`
- `ProductSynchronizationProcessor`
- `SynchronizationCursorService`
- frontend'de route + feature klasörleri
- CSS Modules veya feature stylesheet'leri

## 7. Olumlu kontroller

İncelemede korunması gereken güçlü yönler de doğrulandı:

- `TreatWarningsAsErrors` ile backend derleme disiplini.
- Tenant filtreleri ve birçok entity'de composite unique/index kullanımı.
- CSRF double-submit yaklaşımı, HttpOnly/Secure/SameSite cookie ayarları ve fixed-time token karşılaştırması.
- Webhook servisinde route token ve credential/signature doğrulama tasarımı; kritik sorun bu servise ulaşmadan önceki middleware istisnasıdır.
- Özel dosya depolamada path güvenliği ve boyut limitleri.
- Cursor takip döngüsünde tekrar eden cursor guard'ı: [`api.ts:84`](../src/MarketplaceHub.Web/src/api.ts#L84).
- Mevcut paketlerde npm/NuGet vulnerability taramasının temiz olması.
- Canlı readiness endpoint'i ve tam deploy yolunda API, worker ve frontend asset doğrulaması.

## 8. Önerilen düzeltme yol haritası

### Aşama 0 — Acil, aynı gün

1. Webhook'u rol middleware istisnasına alın ve entegrasyon testi ekleyin.
2. Aggregate status politikasını düzeltin; cancelled+active kombinasyonunu terminal göstermeyin.
3. `PARTIALLY_CANCELLED` / `RETURN_IN_TRANSIT` canonical mapping ve frontend sözleşmesini tamamlayın.

### Aşama 1 — 1–3 gün

1. Sipariş ekranını gerçek server-side pagination'a çevirin.
2. Realtime event ile noktasal cache patch uygulayın.
3. Worker lane priority aralıklarını ayırın; reaper'ı tek döngüye alın.
4. Idempotency response replay'i tamamlayın.
5. Çoklu tenant kararını şema ve login akışında tutarlı hale getirin.

### Aşama 2 — 1 hafta

1. Order ingest sorgularını batchleyin ve SQL sayısı ölçüm testi ekleyin.
2. `OrdersAsync` lookup optimizasyonu.
3. Web/API/DB/worker entegrasyon test seti.
4. CI pipeline ve migration drift gate.
5. Realtime `(CompletedAt, Id)` cursor ve ilk bağlantı retry.

### Aşama 3 — 2–4 hafta

1. CSS katmanlarını feature bazlı sökün, `!important` borcunu azaltın.
2. Route code splitting ve bundle budget.
3. Büyük backend/frontend dosyalarını sorumluluklarına göre bölün.
4. Kullanılmayan paket, export, selector ve legacy yorum bloklarını temizleyin.
5. Format standardını tek commit ile normalize edin.

## 9. Önerilen kabul kapıları

Bir sonraki üretim sürümü için asgari kapılar:

- `dotnet format --verify-no-changes`
- `dotnet build --no-restore`
- `dotnet test --no-build`
- PostgreSQL entegrasyon testleri
- `npm run typecheck`
- frontend unit testleri
- `npm run build` ve bundle budget
- migration pending-model check
- npm ve NuGet vulnerability audit
- Playwright smoke: login, orders, process-order, sayaçlar, dropdown/modal z-index
- deploy sonrası readiness + worker health + frontend asset check

## 10. İnceleme sınırları

Bu denetimde production üzerinde yıkıcı işlem, gerçek Trendyol write, yük testi veya veri mutasyonu yapılmadı. Performans bulguları sorgu/loop yapısının doğrudan kod analiziyle doğrulandı; kesin kapasite sayıları için PostgreSQL `pg_stat_statements`, OpenTelemetry trace ve kontrollü load test gerekir. Migration model drift komutu yerel `dotnet-ef` aracı restore edilmediği için çalıştırılamadı; bunun CI'da zorunlu gate olması önerildi.

## Sonuç

Kod tabanı işlevsel ve önemli güvenlik temellerine sahip; ancak mevcut yeşil test sonucu kritik entegrasyon ve durum-modeli hatalarını yakalamıyor. En yüksek risk, webhook'un erişilemez olması ve parçalı siparişlerde terminal durumun yanlış türetilmesidir. Performans tarafında sipariş ingest N+1 zinciri ile frontend'in tüm status/tüm cursor stratejisi birbirini büyüten yük kaynaklarıdır. Önce doğruluk ve entegrasyon akışları, sonra veri erişimi ve test altyapısı ele alınmalıdır.
