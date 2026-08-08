# F3 Trendyol Kanıt Günlüğü

Bu dosya yalnız tekrar üretilebilir kanıtları içerir. Önceki platformlara ait tarihsel kayıtlar aktif kanıt sayılmaz.

## 2026-08-08 yerel eşitleme doğrulaması

- Exact .NET `10.0.302` restore ve backend build: `PASS` (0 uyarı, 0 hata).
- F3 shipment/common-label, return evidence ve fake adapter kontrat derleme uyumsuzlukları düzeltildi.
- Docker/PostgreSQL testleri: `NOT_RUN / BLOCKED_ENVIRONMENT`.
- Frontend typecheck, production build ve 13 Vitest davranış testi: `PASS`.
- Docker gerektirmeyen backend testleri: `PASS` (Domain 32, Application 54, Adapter Contract 49, API Integration 2; toplam 137).
- GitHub CI PostgreSQL integration paketi: `PASS` (10/10); ilk full-stack tarayıcı koşusu Chromium kurulumu eksikliği nedeniyle `BLOCKED_TOOLING`, iş akışı düzeltildi.
- Çözüm geneli `dotnet format` whitespace ihlalleri giderildi; yeniden CI doğrulaması bekliyor.
- Tam kaynak doğrulama iş akışı `PASS`; release Playwright paketindeki güncel UI/fixture uyumsuzlukları düzeltildi, yerel Playwright 3/3 `PASS` ve yeniden release koşusu bekliyor.
- Playwright config typecheck blokajı ek Node tipi bağımlılığı eklenmeden giderildi; yeniden CI koşusu bekliyor.
- Production ve Stage durumu yükseltilmedi; Docker/PostgreSQL ve gerçek Stage kabulü bekliyor.

| Kanıt | Durum | Not |
| --- | --- | --- |
| Adapter klasör sınırı | PASS_LOCAL | Yalnız Trendyol ve TrendyolEFaturam adapter klasörleri beklenir |
| Connection scope | PASS_LOCAL | Service/UI/Worker yalnız `TRENDYOL` ve `TRENDYOL_EFATURAM` kabul eder |
| Reference mapper/leaf guard | PASS_LOCAL | Scoped snapshot ve leaf category doğrulaması vardır |
| Product/order/return contract fixtures | PASS_LOCAL_PARTIAL | Fixture kapsamı gerçek Stage payload’larıyla genişletilmeli |
| Product publication orchestration | CODED_STATIC_VERIFIED / DYNAMIC_NOT_RUN | Create payload composer, durable job, external-effect fence, batch polling, satır sonucu, status API ve kalıcı HTTPS media URL kaydı eklendi; exact .NET/PostgreSQL ve Stage doğrulaması bekliyor |
| Combined stock-price write | MISSING | Ayrı portlar uzak birleşik sözleşmeyle uyumlu değildir |
| Stage safe-write | BLOCKED_EXTERNAL | Credential ve açık operasyon onayı gerekir |
| Reconciliation/rollback | PARTIAL | Yerel iskelet var; gerçek uzak fark senaryosu gerekir |
## 2026-08-05 production sertleştirme v7

| Kanıt | Durum | Not |
| --- | --- | --- |
| Webhook bounded ingress | CODED_STATIC_VERIFIED / DYNAMIC_NOT_RUN | Content-Length olmasa da gerçek 10 MiB byte sınırı, endpoint body limit ve IP bazlı rate limit uygulanır. |
| Webhook route secret log koruması | CODED_STATIC_VERIFIED / CADDY_NOT_RUN | Webhook yolları Caddy access logundan çıkarıldı; ASP.NET hosting diagnostics request log seviyesi bastırıldı. |
| Planlı senkronizasyon üreticisi | CODED_STATIC_VERIFIED / POSTGRES_NOT_RUN | Aktif bağlantı ve capability/policy bazlı order, return ve reference jobları deterministic dedup/jitter ile oluşturulur. |
### 2026-08-05 - Retry ve tenant izolasyonu ek sertleştirmesi

- Geçici iade aksiyonu hatalarında karar kaydı terminal `FAILED` yerine `RETRY_SCHEDULED` tutulur; başarılı sonuç aynı idempotency anahtarıyla tekrarlandığında yeniden dış etki üretilmez.
- Operasyon issue dedupe sorgusu tenant kimliğiyle sınırlandırıldı.
- Bu değişiklikler statik olarak incelendi; exact .NET ve gerçek Trendyol Stage testleri `NOT_RUN / BLOCKED_ENVIRONMENT` durumundadır.

### 2026-08-05 - Birleşik eşleme ekranı regression kapsamı

| Kanıt | Durum | Not |
| --- | --- | --- |
| Vitest kategori-kapsamlı özellik/değer akışı | CODED / DYNAMIC_NOT_RUN | Test doğrudan route bileşeni `MappingPage kind="attributes"` üzerinden kategori kapsamı, zorunlu özellik, özellik eşleme PUT'u, değer snapshot'ı ve özellik değeri PUT'unu doğrular. |
| Eski doğrudan bileşen bağımlılığı | CLOSED_STATIC | Test artık `AttributeMappingPage` bileşenini doğrudan çağırmaz; uygulama route'u ile aynı giriş noktasını kullanır. |
| Playwright güncel kabuk ve eşleme akışı | CODED / DYNAMIC_NOT_RUN | Kabuk testi güncel operasyon/ayar menülerini ve rol görünürlüğünü; `f3-mapping.spec.ts` ise gerçek route üzerinde kategori kapsamı, özellik ve değer PUT payload zincirini doğrular. |
| Exact frontend toolchain | BLOCKED_ENVIRONMENT | Mevcut ortam Node `22.16.0`/npm `10.9.2`; proje Node `24.18.1`/npm `11.12.1` ister. Varsayılan registry `zod@4.4.3` paketini 404 döndürdüğü için bağımlılıklar kurulamadı. |
| Trendyol Stage mapping kabulü | BLOCKED_EXTERNAL | Gerçek Stage credential, güncel kategori/özellik/değer snapshot'ı ve kontrollü test verisi gerekir. |

### 2026-08-05 - Product Create durable orchestration

| Kanıt | Durum | Not |
| --- | --- | --- |
| Create sözleşmesi ve worker dispatch | CODED_STATIC_VERIFIED | `IProductPort.CreateAsync`, `TRENDYOL_PRODUCT_CREATE` ve Worker F3 dispatch zinciri eklendi. |
| Güvenli enqueue kapısı | CODED_STATIC_VERIFIED | `PRODUCT_WRITE=SUPPORTED`, global/connection write switch, güncel mapping, teklif, stok ve media URL doğrulamadan job oluşmaz. |
| Payload ve batch durum makinesi | CODED_STATIC_VERIFIED | En fazla 1000 varyantlık create payload; `SUBMIT -> POLL`; 4 saat sınırı; eksik/yinelenen/bilinmeyen barkod sonucu manuel incelemeye gider. |
| Satır bazlı sonuç | CODED_STATIC_VERIFIED | Listing profile, listing variant ve marketplace listing state üzerinde kabul/red/partial durumları kaydedilir; tam kabul `APPROVAL_PENDING` olur. |
| Dış etki ve replay koruması | CODED_STATIC_VERIFIED | Dış çağrı öncesi `ExternalEffectRecord` oluşturulur; belirsiz sonuçta otomatik tekrar yerine `MANUAL_REVIEW`; aynı payload terminal job dahil mevcut job'a döner. |
| PostgreSQL başarı/replay/partial testleri | CODED / DYNAMIC_NOT_RUN | `FakeWorkerPipelineTests` içinde payload, tek dış etki, poll ve kısmi satır sonucu senaryoları eklendi. .NET SDK/Docker bulunmadığı için çalıştırılmadı. |
| Trendyol Stage safe-write | BLOCKED_EXTERNAL | Gerçek credential, kontrollü barkod/SKU, açık operasyon onayı ve rollback gerekir. |
| Create sonrası onay reconciliation | CODED_STATIC_VERIFIED / DYNAMIC_NOT_RUN | Create batch içinde en az bir kabul edilen satır varsa otomatik durable reconciliation işi üretir; `CREATE_REJECTED` satırlar read-back dışında korunur; approved -> unapproved barkod fallback, pending/rejected/locked/archive/blacklist ayrımı, content/variant linkleri ve fail-closed kimlik çatışması eklendi. |

### 2026-08-05 - Product Create onay uzlaştırması

| Kanıt | Durum | Not |
| --- | --- | --- |
| Approved/unapproved barkod read-back | CODED_STATIC_VERIFIED | Adapter önce `products/approved`, bire bir barkod bulunmazsa `products/unapproved` endpoint'ini okur; hiçbir dış yazma çağrısı yapmaz. |
| Uzak durum eşlemesi | CODED_STATIC_VERIFIED | `APPROVED`, `PENDING_APPROVAL`, `REJECTED`, `ARCHIVED`, `LOCKED`, `BLACKLISTED` ve `NOT_FOUND` durumları yerel satır/profile durumlarına ayrılır. |
| Uzak kimlik kalıcılığı | CODED_STATIC_VERIFIED | Onaylı `contentId` ve `variantId` değerleri tenant + connection benzersizlikleriyle kaydedilir; yerel veya uzak kimlik çatışması sessiz rewire yerine `MANUAL_REVIEW` olur. |
| Durable lifecycle | CODED_STATIC_VERIFIED | En az bir satırı kabul edilen create batch `TRENDYOL_PRODUCT_APPROVAL_RECONCILE` işi üretir; pending/not-found 5 dakika sonra retry olur, yedi günlük yerel operasyon deadline'ı aşılırsa yalnız kabul edilen satırlar manuel incelemeye taşınır ve `CREATE_REJECTED` kanıtları korunur. |
| PostgreSQL onay senaryoları | CODED / DYNAMIC_NOT_RUN | Tam onay, kısmi ret, iki listede görünmeme, önceden farklı kimliğe bağlı link çatışması ve daha yeni payload tarafından supersede edilme senaryoları `FakeWorkerPipelineTests` içinde kodlandı. |
| Contract fixture | CODED / DYNAMIC_NOT_RUN | Approved content/variant kimliği, unapproved rejection nedeni ve pending status mapper testi eklendi; fixture anonimdir. |
| Exact .NET/PostgreSQL doğrulaması | BLOCKED_ENVIRONMENT | .NET SDK `10.0.302` ve Docker/PostgreSQL test ortamı bu çalışma ortamında bulunmuyor. Resmî SDK binary indirme girişimi araç gzip politikası ve kabuk DNS kısıtı nedeniyle tamamlanamadı. |
| Trendyol Stage read-back | BLOCKED_EXTERNAL | Gerçek Stage credential, kontrollü create barkodu ve onay/ret görünürlük akışı gerekir. |

## 2026-08-05 - Trendyol Türkiye CORE kod kapanışı

| Kanıt | Durum | Not |
| --- | --- | --- |
| Product V2 create/update/archive | CODED_STATIC_VERIFIED / DYNAMIC_NOT_RUN | Ayrı durable state machine, external-effect fence, batch poll ve approval read-back vardır. |
| Approved product pagination | CODED_CONTRACT_TESTED / DYNAMIC_NOT_RUN | Size en fazla 100; ilk 10.000 kayıt page, devamı nextPageToken cursor. |
| Birleşik fiyat-stok | CODED_STATIC_VERIFIED / DYNAMIC_NOT_RUN | Tek batch, offer/price/projection version kanıtı ve stale sonucu reddetme. |
| Order V2 + stream | CODED_CONTRACT_TESTED / STAGE_NOT_RUN | `/v2/orders`, cursor ve 2026 field alias fixture'ları kodlandı. |
| Shipment action + read-back | CODED_STATIC_VERIFIED / STAGE_NOT_RUN | Yalnız capability allowedActions; TRACKING_NUMBER için Picking/Invoiced durumu ve cargoSenderNumber/providerCode zorunlu; remote order read-back uygulanır. |
| Common label | CODED_STATIC_VERIFIED / STAGE_NOT_RUN | Create, poll, private storage ve document attempt idempotency. |
| Return action/evidence/read-back | CODED_CONTRACT_TESTED / STAGE_NOT_RUN | `claimId`, exact claim read, approve/reject, özel evidence ve conflict/manual review. |
| Capability evidence | CODED_STATIC_VERIFIED / STAGE_EVIDENCE_MISSING | Owner/Admin, ETag, audit, official source ve write fixture SHA-256 zorunlu. |
| Operatör UI | TYPESCRIPT_PASS / VITEST_PLAYWRIGHT_NOT_RUN | Ürün, fiyat-stok, shipment, return ve capability evidence ekranları; `tsc --noEmit` exit 0. |
| Backend dynamic suite | BLOCKED_ENVIRONMENT | .NET SDK ve Docker yok; başarı sayılmadı. |
| Stage safe-write | BLOCKED_EXTERNAL | Credential, kontrollü fixture ve açık operasyon onayı yok. |

## 2026-08-06 — v9 birleşik kategori/özellik/değer eşleme

| Kanıt | Durum | Not |
| --- | --- | --- |
| Toplu kategori kapsamlı mapping read | CODED_STATIC_VERIFIED | `GET /mappings/{type}` seçilen connection ve scope içindeki eşlemeleri tek istekte döndürür; N+1 sorgu kaldırıldı. |
| Kategori özellik başlığı yönetimi | CODED_STATIC_VERIFIED | Panel kategorisine özellik bağlama, yeni seçimli özellik ve seçenek ekleme, zorunlu/özel değer kuralları vardır. |
| Özellik/değer kartları | TYPESCRIPT_STATIC_PASS | Zorunlu/eşlenmemiş kart vurgusu, ilerleme sayacı ve tüm yerel değerleri tek kartta kaydetme akışı eklendi. |
| Kaynak kabul kontrolleri | PASS_LOCAL_STATIC | v9 kabul betiği, operasyon kabul betiği, TSX syntax/semantic ve C# delimiter kontrolleri geçti. |
| Trendyol Stage kabulü | BLOCKED_EXTERNAL | Gerçek Stage credential ve güncel kategori/özellik/değer snapshot'ı gereklidir. |
## 2026-08-08 — v10 birleşik operatör arayüzü

| Kanıt | Durum | Not |
| --- | --- | --- |
| Ortak görsel sistem | PASS_LOCAL | Sayfa başlığı, panel, form, buton, kart, boş durum ve responsive davranışlar ortak token ve ölçülerle hizalandı. |
| Sipariş hızlı ayrıntısı | PASS_LOCAL | Açılır alan tekil sipariş sorgusunu yalnız açıldığında çalıştırır; müşteri, teslimat/fatura adresi, ürün, SKU, barkod, tutar ve kargo paketini gösterir. |
| Vitest | PASS_LOCAL | 5 dosyada 14/14 test geçti; sipariş ayrıntısının müşteri, adres, ürün, toplam ve kargo içeriği davranış testiyle doğrulandı. |
| Playwright | PASS_LOCAL | 3/3 tarayıcı testi geçti. |
| TypeScript ve production build | PASS_LOCAL | `npm run typecheck` ve `npm run build` exit code 0. |
| Trendyol Stage | BLOCKED_EXTERNAL | Gerçek Stage kabulü ve dış yazma kanıtları bu UI teslimiyle değiştirilmedi. |
