# F3 Trendyol Kanıt Günlüğü

Bu dosya yalnız tekrar üretilebilir kanıtları içerir. Önceki platformlara ait tarihsel kayıtlar aktif kanıt sayılmaz.

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
| Create sonrası onay reconciliation | MISSING | Batch SUCCESS yalnız create kabulüdür; approved-products read-back ile LIVE/REJECTED kesinleştirilmelidir. |
