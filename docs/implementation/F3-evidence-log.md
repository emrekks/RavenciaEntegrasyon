# F3 Trendyol Kanıt Günlüğü

## 2026-08-09 — v10.17 mikro ihracat termin ve kabuk görünümü

| Kanıt | Durum | Not |
| --- | --- | --- |
| Mikro termin canlı snapshot | BLOCKED_REMOTE_DATA | `1238693012` ve `1238692471` için `agreedDeliveryDate`, `estimatedDeliveryStartDate`, `estimatedDeliveryEndDate`, `lastDeliveryDate` ve `deliveryDate` alanlarının tamamı canlı Stage snapshotında `null`; uygulama tarih uydurmaz. |
| Resmî sözleşme | VERIFIED_DOC | Trendyol `getShipmentPackages` örneği mikro ihracatta `agreedDeliveryDate` ve tahmini teslim tarihlerini tanımlar: https://developers.trendyol.com/v2.0/docs/get-order-packages-getshipmentpackages |
| Kabuk ve detay UI | PASS_TARGETED | Kalıcı ikon menüsü ve kaldırılan sipariş özet kartı hedefli App/sipariş testleriyle doğrulandı. |

## 2026-08-09 — v10.16.1 mikro ihracat etiketi yerleşimi

| Kanıt | Durum | Not |
| --- | --- | --- |
| Tekil fatura rozeti | CODED | “Mikro ihracat” yalnız fatura sütununda gösterilir; sipariş bilgileri sütunundaki tekrar ve uzun etiket kaldırılmıştır. |

## 2026-08-09 — v10.16 sipariş menüsü ve mikro ihracat geri uyumluluğu

| Kanıt | Durum | Not |
| --- | --- | --- |
| Menü yerleşimi | PASS_LOCAL | Açık sipariş satırı üst katmana alınır; fatura ve işlem menüleri tetikleyicinin altındaki boşluk yeterliyse aşağı, değilse yukarı açılır. Mobil sabit alt yüzey korunur. |
| Ürün ve fatura metni | PASS_LOCAL | Ürün bilgi bloğu dikey ortalanır; menü ve pencere adı “Fatura & Adres Bilgileri” olur. |
| Mikro ihracat geri uyumluluğu | PASS_LOCAL | Öncelik resmî `micro`, `microExport`, `3pByTrendyol` ve sipariş tipi alanlarındadır. Bu alanları taşımayan tarihsel Stage snapshotlarında yalnız PM3 ile Arvato kimliğinin birlikte bulunması ihracat partneri sinyalidir; sipariş numarası sabitlenmez. |
| Doğrulama | PASS_LOCAL | .NET solution build 0 hata/0 uyarı, hedefli backend 5/5, TypeScript ve sipariş ekranı Vitest 4/4 geçti. |
| Stage | REVALIDATION_REQUIRED | `1238692471` mevcut snapshotı resmî mikro alanlarını taşımıyor; canlı API read-back bulunana kadar dar tarihsel partner geri uyumluluğu kullanılır. |

## 2026-08-09 — v10.15 sipariş filtre çalışma alanı

| Kanıt | Durum | Not |
| --- | --- | --- |
| Filtre yüzeyi | PASS_LOCAL | Arama, platform ve sipariş durumu sık kullanılan yüzeyde; listeleme, tarih aralığı, kargo, fatura ve sayfa boyutu gelişmiş filtrelerde toplanır. Uygula/Temizle akışı client-side filtrelerin açıkça uygulanmasını sağlar. |
| Operasyon listesi | PASS_LOCAL | Sipariş satırları küçük görsel boşlukla ayrılır; dar ekranda filtreler tek kolonlu responsive düzene geçer. |
| Frontend doğrulaması | PASS_LOCAL | TypeScript, 5 dosyada 16/16 Vitest ve production web build geçti. |
| Trendyol Stage | REVALIDATION_REQUIRED | Bu UI değişikliği dış yazma açmaz; gerçek Stage sipariş görünümüyle yeniden kabul gerekir. |

## 2026-08-09 — v10.14 tekil sipariş salt-okunur yenileme

| Kanıt | Durum | Not |
| --- | --- | --- |
| Operasyon yüzeyi | PASS_LOCAL | Aktif `ORDER_READ=SUPPORTED` bağlantıda sipariş numarası, mevcut salt-okunur `order-sync-jobs` akışına tekil `externalOrderId` olarak gönderilir. Hedefli Vitest 6/6, TypeScript ve production web build geçti. |
| Koruma | CODED | UI yalnız zaten mevcut olan read job'ı kuyruğa alır; Trendyol'a yazma başlatmaz. |
| Dokümantasyon | PASS_LOCAL | `verify-documentation-transaction.py --base fa08a1012fcd060888025ca38b3b1b1945478c05` geçti. |
| Hedef read-back | BLOCKED_REMOTE_NOT_FOUND | Canlı Stage bağlantısında `1238693012` için salt-okunur tekil iş `REMOTE_ORDER_NOT_FOUND` ile bloklandı; snapshot değiştirilmedi. Bu nedenle `3pByTrendyol=true` alanı ve mikro ihracat etiketi Stage'de henüz doğrulanamadı. |

## 2026-08-09 — v10.13 Trendyol İhracat Partnerliği mikro etiketi

| Kanıt | Durum | Not |
| --- | --- | --- |
| Resmî kaynak | PASS_DOCUMENTED | `getShipmentPackages` belgesi, `3pByTrendyol=true` durumunda `micro=false` döndüğünü; bunun Trendyol İhracat Partnerliği paketi olduğunu belirtir. |
| Yerel doğrulama | PASS_LOCAL | .NET build (0 hata, 0 uyarı), 49/49 Trendyol adapter contract testi ve dokümantasyon transaction doğrulaması geçti. |
| Snapshot/eşleme | PASS_LOCAL | `3pByTrendyol` saklanır ve `micro`/`microExport` ile birlikte ihracat etiketi türetiminde değerlendirilir. |
| Trendyol Stage | REVALIDATION_REQUIRED | Sipariş `1238693012` için tam paket read-back ile alanın ve ekran etiketinin doğrulanması beklenir. |

## 2026-08-09 — v10.12 Stream cursor geçerlilik kurtarması

| Kanıt | Durum | Not |
| --- | --- | --- |
| Production read-back | PASS_PRODUCTION_READ | v10.10 imleç isteği tarih filtresi olmadan da eski saklanmış `nextCursor` için HTTP 400 döndürdü; v10.12 imleci temizleyerek son watermark’tan başladı ve `TRENDYOL_ORDER_SYNC` başarıyla tamamlandı. |
| Yerel doğrulama | PASS_LOCAL | .NET build (0 hata, 0 uyarı), 49/49 Trendyol adapter contract testi ve dokümantasyon transaction doğrulaması geçti. |
| Kurtarma kuralı | PASS_LOCAL | Sadece saklanmış, boş olmayan cursor ve HTTP 400 validation kombinasyonunda bir defa cursor temizlenir; son kalıcı watermark ile ilk sayfadan başlanır. |
| Koruma | CODED | İkinci 400 veya başka hata sınıfı başarısız kalır; hata gizlenmez ve sonsuz döngü oluşmaz. |
| Trendyol Stage | PASS_READ_ONLY | 204 siparişte `agreedDeliveryDate`, `invoiceStatus` ve `micro` snapshot alanları kaydedildi. Fatura dağılımı: 33 `INVOICED`, 171 `NOTINVOICED`; bu veri kümesinde `micro=true` kayıt yoktur. |

## 2026-08-09 — v10.11 CI browser proof bekleme kararlılığı

| Kanıt | Durum | Not |
| --- | --- | --- |
| GitHub kaynak doğrulaması | OBSERVED_CI_FAILURE | Tam çözüm .NET build ve Docker/PostgreSQL testleri geçti; browser proof, soğuk Vite derlemesinde giriş formu için 30 saniyelik varsayılan bekleme sınırına ulaştı. |
| Yerel doğrulama | PASS_LOCAL | Node syntax, TypeScript ve dokümantasyon transaction doğrulaması geçti. |
| Test dayanıklılığı | PASS_LOCAL | Browser proof ağ sakinliğini ve 60 saniyelik açık bekleme sınırını kullanır; uygulama davranışı değiştirilmez. |
| Tam CI | REVALIDATION_REQUIRED | İmaj yayınından önce GitHub doğrulama hattı yeniden tamamlanmalıdır. |

## 2026-08-08 — v10.10 Stream cursor filter uyumluluğu

| Kanıt | Durum | Not |
| --- | --- | --- |
| Production hata tekrarı | OBSERVED_PRODUCTION | v10.9 ile `REMOTE_ORDER_NOT_FOUND` geçildi; saklanmış `nextCursor` ile `lastModifiedStartDate` birlikte gönderildiğinde Stream endpointi HTTP 400 döndürdü. |
| Yerel doğrulama | PASS_LOCAL | .NET build (0 hata, 0 uyarı), 49/49 Trendyol adapter contract testi ve dokümantasyon transaction doğrulaması geçti. |
| İstek kuralı | PASS_LOCAL | İlk istek tarih aralığını taşır. Trendyol’un dönmüş olduğu devam imleci kullanıldığında tarih filtresi tekrar gönderilmez. |
| Trendyol Stage | REVALIDATION_REQUIRED | Yeni imleç çağrısıyla salt-okunur eşitlemenin başarıyla tamamlanması ve snapshot read-back beklenir. |

## 2026-08-08 — v10.9 tam paket okuma bulunamadı toleransı

| Kanıt | Durum | Not |
| --- | --- | --- |
| Production read-back | PASS_PARTIAL | Tam paket okuması ile 204 siparişte `micro`, `agreedDeliveryDate` ve `invoiceStatus` snapshot alanları kaydedildi. |
| Yarış durumu | OBSERVED_PRODUCTION | Akış sayfasından gelen bir sipariş, takip eden tam paket okumada `REMOTE_ORDER_NOT_FOUND` döndürdü; pazar yeri tarafında listeden kalkmış paket, eşitlemenin tamamını blokladı. |
| Yerel doğrulama | PASS_LOCAL | .NET build (0 hata, 0 uyarı), 49/49 Trendyol adapter contract testi ve dokümantasyon transaction doğrulaması geçti. |
| Dayanıklılık | PASS_LOCAL | Sadece `NotFound` sınıfında akış kaydıyla devam edilir; kimlik doğrulama, hız sınırı, 5xx ve sözleşme hataları job retry/audit akışına aynen gider. |
| Trendyol Stage | REVALIDATION_REQUIRED | Bu düzeltmeden sonra salt-okunur job başarısı ve alanların read-back sonucu yeniden kaydedilmelidir. |

## 2026-08-08 — v10.8 tam sipariş zenginleştirme

| Kanıt | Durum | Not |
| --- | --- | --- |
| Production veri teşhisi | PASS_PRODUCTION_READ | 3.552 siparişte akış snapshot’ları yalnız müşteri/adres alanlarını taşıyordu; mikro, termin ve uzak fatura alanları yoktu. Yerel fatura kaydı sayısı 0 idi. |
| Resmî tam paket read-back | BUILD_PASS | Akıştan gelen her sipariş, idempotent upsert öncesi `/v2/orders?orderNumber=…` tam paketiyle okunur. `micro`, `agreedDeliveryDate`, `invoiceStatus` ve HTTPS `invoiceLink` snapshot’a alınır. |
| Fatura görünümü | BUILD_PASS | Uzak `Invoiced`, `Received`, `Rejected`, `NotInvoiced` durumları okunur; güvenli HTTPS fatura linki yalnız varsa “Faturayı Gör” olarak gösterilir. |
| Yerel doğrulama | PASS_LOCAL | .NET build; 49 adapter contract testi; TypeScript; 14/14 Vitest; frontend production build exit code 0. |
| Trendyol Stage | BLOCKED_EXTERNAL | Gerçek Stage/read-back yanıtıyla alanların kabulü bu değişiklikten sonra yeniden doğrulanmalıdır. |

Bu dosya yalnız tekrar üretilebilir kanıtları içerir. Önceki platformlara ait tarihsel kayıtlar aktif kanıt sayılmaz.

## 2026-08-08 — v10.6 sipariş operasyon etkileşimleri

| Kanıt | Durum | Not |
| --- | --- | --- |
| Fatura ve işlem menüleri | PASS_LOCAL | Fatura bilgileri penceresi, kesilmiş fatura yönlendirmesi, işleme alma/kargo değişimi görünümü ve dış yazma başlatmayan pasif iptal kontrolü eklendi. |
| Sipariş görseli geri dönüşü | PASS_LOCAL | Sipariş satırı varyant kimliği yoksa aynı tenant içindeki stok koduyla katalog varyantı/görselini eşleştirir; eşleşme yoksa veri uydurulmaz. |
| Görsel durum/hizalama | PASS_LOCAL | İptal kırmızı, mikro ihracat mavi satır vurgusu; iptal/taşıma/teslim zamanı ve alıcı/fatura hizaları güncellendi. |
| .NET restore ve build | PASS_LOCAL | `dotnet restore MarketplaceHub.sln --locked-mode` ve `dotnet build MarketplaceHub.sln --no-restore`: 0 hata, 0 uyarı. |
| Frontend doğrulaması | PASS_LOCAL | TypeScript, 5 dosyada 14/14 Vitest ve production build geçti. Node `24.15.0`; proje hedefi `24.18.1` olduğundan exact-toolchain değildir. |
| Trendyol Stage | BLOCKED_EXTERNAL | Gerçek Stage kabulü ve dış yazma kanıtı bu UI teslimiyle değişmedi. |

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
| v10.4 tam sipariş tablosu | PASS_LOCAL | Order list sözleşmesi ürün satırları, görsel, varyant, alıcı/adres, finans ve paket verileriyle toplu zenginleştirildi; backend build, 14/14 Vitest, TypeScript ve production build geçti. |
| v10.3 sipariş referans tablosu | PASS_LOCAL | Sipariş durum sekmeleri, kompakt toolbar, kolonlu satır düzeni ve açılır detay görünümü güncellendi; 14/14 Vitest, TypeScript ve production build geçti. |
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

### 2026-08-08 — v10.7 sipariş verisi, termin ve mikro ihracat görünümü

| Kanıt | Durum | Not |
| --- | --- | --- |
| Trendyol snapshot alanları | BUILD_PASS | Sipariş mapper’ı telefon ve E-Fatura mükellefliği alanlarını saklar; mevcut iç içe adres snapshot’ları arayüz tarafından çözümlenir. |
| Fatura bilgileri penceresi | BUILD_PASS | Pencere açıldığında `GET /orders/{id}` ile güncel sipariş ayrıntısı alınır; teslimat/fatura adresi, e-posta, telefon ve mükelleflik gösterilir. |
| Termin gecikme uyarısı | BUILD_PASS | Açık siparişte `shipmentDueAt` geçmişteyse, geçmiş gün hesabıyla kargoya teslim uyarısı gösterilir. |
| Mikro ihracat ve fatura menüsü | BUILD_PASS | Mikro ihracat satır/fatura etiketleri eklenir; kesilmiş faturada fatura bilgisi ve pasif silme denetimi gösterilir. |
| Yerel doğrulama | PASS_LOCAL | .NET solution build, TypeScript, 14/14 Vitest ve frontend production build exit code 0. |
| Trendyol Stage | BLOCKED_EXTERNAL | Gerçek sipariş payload’ı ve canlı/Stage fatura durum read-back kabulü gerekir. |

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
## 2026-08-08 — v10.5 sipariş operasyon düzeni

| Kanıt | Durum | Not |
| --- | --- | --- |
| Doğrudan sipariş satırı | PASS_LOCAL | Açılır ayrıntı satırı kaldırıldı; müşteri, ürün, kargo, fatura ve teslim/termin bilgisi ana satırda kalır. |
| Arama ve filtreler | PASS_LOCAL | Sipariş/paket/takip/model/stok/barkod/müşteri araması ile tarih, kargo, fatura, sayfa boyutu ve seçimli toplu işlem yüzeyi kodlandı. |
| Taşmasız dar kolon düzeni | PASS_LOCAL | 1294 px viewport için kolon minimumları ve ürün kartı ölçüleri sıkılaştırıldı; daha dar görünümde yatay kaydırma kontrollü olarak devreye girer. |
| Vitest | PASS_LOCAL | 5 dosyada 14/14 test geçti; sipariş satırının doğrudan görünümü, ayrık varyant alanları, model kodu ve açılır satırın kalkması test edildi. |
| TypeScript / production build | PASS_LOCAL | `npm run typecheck` ve `npm run build` exit code 0. Node `24.15.0`, hedef `24.18.1` olduğundan exact-toolchain doğrulaması değildir. |
| Trendyol Stage | BLOCKED_EXTERNAL | Gerçek Stage kabulü ve dış yazma kanıtı bu UI teslimiyle değişmedi. |

## 2026-08-08 — v10 birleşik operatör arayüzü

| Kanıt | Durum | Not |
| --- | --- | --- |
| v10.2 beyaz navigasyon | PASS_LOCAL | Açık sidebar, aktif menü, hover, ikon ve mobil alt navigasyon renkleri güncellendi; 14/14 Vitest, TypeScript ve production build geçti. |
| v10.1 görünür kabuk revizyonu | PASS_LOCAL | Koyu kurumsal navigasyon, üst bar, sayfa başlık yüzeyi, metrik/rapor kartları ve mobil alt navigasyon production CSS derlemesinde doğrulandı. |
| Ortak görsel sistem | PASS_LOCAL | Sayfa başlığı, panel, form, buton, kart, boş durum ve responsive davranışlar ortak token ve ölçülerle hizalandı. |
| Sipariş hızlı ayrıntısı | PASS_LOCAL | Açılır alan tekil sipariş sorgusunu yalnız açıldığında çalıştırır; müşteri, teslimat/fatura adresi, ürün, SKU, barkod, tutar ve kargo paketini gösterir. |
| Vitest | PASS_LOCAL | 5 dosyada 14/14 test geçti; sipariş ayrıntısının müşteri, adres, ürün, toplam ve kargo içeriği davranış testiyle doğrulandı. |
| Playwright | PASS_LOCAL | 3/3 tarayıcı testi geçti. |
| TypeScript ve production build | PASS_LOCAL | `npm run typecheck` ve `npm run build` exit code 0. |
| Trendyol Stage | BLOCKED_EXTERNAL | Gerçek Stage kabulü ve dış yazma kanıtları bu UI teslimiyle değiştirilmedi. |
