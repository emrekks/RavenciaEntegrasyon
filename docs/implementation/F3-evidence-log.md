## 2026-08-12 — v10.44 Stage return runtime acceptance

- **Release/deploy:** `ed2dfc9` source CI PASS; `release-2026-08-12-v10.44` immutable publish PASS. App `sha256:214a3bc4614c0573a3915eba3705abff4814b6c7b6a8d2607bf6300f3742334a`, edge `sha256:1dcd2fd246d71b80a817cf690f7b5c6309995eb72fc3a8bc96ab2b0fa8722ad3`; hedefte migration, API/Worker/Caddy health ve `/health/ready` 200 PASS.
- **Stage read kanıtı:** Paneldeki normal `İadeleri eşitle` operasyonu `TRENDYOL_RETURN_SYNC` job'u `8542af70a19c4464b78273ee54c9fd16` olarak enqueue edildi ve `1/6` denemede `SUCCEEDED` oldu. İade ekranı 25 paketi, durum sekmelerini ve 1–5 ürün satırını gösterdi.
- **Korunan sınır:** Bu kabul yalnız Stage salt-okunur return sync'tir. Production endpoint/credential boundary, external-write switch, authorization, idempotency, audit ve yazma güvenlikleri değiştirilmedi.

## 2026-08-12 — v10.43 CI format correction

- İlk source CI yalnız `IntegrationRuntimePolicyTests` satır biçiminden başarısız oldu. Test düzenlendi; solution build ve üç policy testi PASS, yeni source CI bekleniyor.

## 2026-08-11 — Stage manual runtime refactor

- **Kapsam:** Trendyol STAGE/ACTIVE bağlantısındaki manuel read, return action, shipment action, common label, product ve price/inventory job enqueue yolları.
- **Kanıt:** `IntegrationRuntimePolicy` Stage manuel bağlamında capability/evidence/fixture/write-switch kontrollerini runtime blocker olmaktan çıkarır; Production yolunda capability + global/connection write switch korunur. Adapter write guard aynı ayrımı tekrar uygular.
- **Korunanlar:** credential, HTTPS endpoint environment boundary, ACTIVE connection, payload doğrulama, idempotency, concurrency, audit, provider hata/retry davranışı.
- **Doğrulama:** solution build ve web typecheck PASS; gerçek Stage smoke henüz çalıştırılmadı.

## 2026-08-11 - v10.41 Stage iade ürün satırı kanıtı

- v10.40 immutable deployment sonrası önceki return-sync retry işi 4/6 denemede `SUCCEEDED`; panel toplam 23 paket (`1 REQUESTED`, `20 CANCELLED`, `2 DISPUTED`) gösterdi.
- Credential ve müşteri verisi yazdırmayan şema probu `items[].orderLine` ve nested `claimItems[]` alanlarını doğruladı. Aynı satırda parent order line id `10524304`, claim item orderLineItemId `57322050`; yerel sipariş satırı `ExternalLineId=10524304` idi.
- Mapper claim action line id'yi nested claim item'dan, sipariş bağı kimliğini parent `orderLine.id` alanından alır. Hedefli sözleşme testi farklı iki kimliği sabitler; yeniden sync mevcut claim'lere eksik ürün satırlarını idempotent ekleyecektir.
- Adapter sözleşme testleri `51/51`, source CI `#130`, immutable release `#120` ve v10.41 deployment/readiness geçti. App digest `sha256:95d2b607f4d830bcd0bcf83b5b1b6b03bcc69f7f563b4e58b4487ca7c155c0f0`, edge digest `sha256:9ca9c78012e4fff39245d846c8218ff052f1f504bfb0b77cff67b11f42ac1603`.
- Yalnız Stage bağlantısının `RETURNS` incremental cursor'u kontrollü olarak sıfırlanıp tam salt-okunur backfill çalıştırıldı; iş `92dc03f1bca241e49687a5aad9987dcd` ilk denemede `SUCCEEDED`. Panel 25 paket gösterdi; tüm kayıtlar 1–5 ürün içeriyor ve `0 ürün` sayısı sıfır.

## 2026-08-11 - v10.40 Stage return-sync süre kanıtı

- v10.39 deployment sonrası bağlantı testi `18:06:42` tarihinde `RETURN_READ=SUPPORTED` üretti.
- İlk tam `TRENDYOL_RETURN_SYNC` denemesi sekiz status read çağrısının sıralı toplam süresinde `REMOTE_TIMEOUT` oldu ve `RETRY_SCHEDULED` durumuna geçti; kalıcı veya uydurma iade kaydı oluşmadı.
- Stage'e özel, bağımsız ve salt-okunur status çağrıları `Task.WhenAll` ile paralelleştirildi. Production canonical read, dış yazmalar ve fail-closed hata davranışı değişmedi.

## 2026-08-11 - v10.39 Stage getClaims durum filtresi kanıtı

- Credential göstermeyen salt-okunur runtime probunda filtresiz, `size/page` filtreli ve storefront başlıksız Stage istekleri aynı `SupplierApiDomainNotFoundException` / `order.not.found` 404 sonucunu verdi.
- Aynı resmî endpoint `size=50&page=0&claimItemStatus=Created` ile storefront başlığından bağımsız HTTP 200 ve 1439 bayt claim cevabı döndürdü. Sorunun auth, endpoint veya storefront değil, Stage'in filtresiz claims davranışı olduğu kanıtlandı.
- Adapter yalnız `STAGE` + filtresiz 404 koşulunda `Created`, `WaitingInAction`, `WaitingFraudCheck`, `Accepted`, `Cancelled`, `Rejected`, `Unresolved`, `InAnalysis` durumlarını salt-okunur sorgular. Production tek canonical çağrı ve tüm write kapıları korunur.
- Hedefli adapter sözleşme testleri `50/50 PASS`; CI, immutable release, deployment ve Stage capability/sync yeniden kabulü bekleniyor.

## 2026-08-11 - v10.38 Türkiye claims Stage 404 fallback

- R3 production deployment sonrası Stage capability testi `17:34:46` tarihinde yenilendi; CONNECTION/ORDER/PRODUCT/REFERENCE read `SUPPORTED`, claims GET `REMOTE_RESOURCE_NOT_FOUND`/HTTP 404 ve `RETURN_READ=UNKNOWN` kaldı.
- Resmî Türkiye V2 getClaims referansı storefront headerı tanımlamaz. Canonical `storeFrontCode=TR` GET korunarak yalnız claims read 404 sonrası aynı endpoint başlıksız bir kez denenir; yazma yollarına fallback eklenmedi.
- Trendyol adapter sözleşme testleri `50/50` geçti. Merkezi CI, immutable release, deployment ve gerçek Stage yeniden kabulü bekleniyor; capability elle yükseltilmedi.

## 2026-08-11 - v10.37 Trendyol resmî iade satırı uyumluluğu

- Resmî getClaims sözleşmesindeki `items[].claimItems[]` alanı iade satırı, durum ve neden eşlemesine eklendi; tarihsel düz `items[]` ve doğrudan `claimItems[]` geri uyumluluğu korundu.
- Resmî örnekte miktar alanı bulunmadığından her claim item varsayılan `1` adet kabul edilir.
- Solution build, Trendyol adapter sözleşme testleri `50/50`, web testleri `19/19`, typecheck ve production web build geçti. Yerel Docker motoru kapalı olduğundan Testcontainers grupları `BLOCKED_TOOLING`; Linux CI sonucu bekleniyor. Yerel Python bulunmadığından documentation transaction kontrolü de CI'ya bırakıldı.
- Stage bağlantı testi `SUCCEEDED` (correlation `b9738ca506b74e9fb4f44045ad77c12f`); dağıtılmış eski sürüm `RETURN_READ=UNKNOWN` bıraktı. Güncel kodun hedefli testi ve Stage yeniden kabulü bekleniyor; capability elle yükseltilmedi.
- Düzeltmeyi içeren r3 immutable app/edge imajları release `#116` ile yayımlandı. Hedef sunucu erişimi bulunmadığından deployment ve güncel paketle Stage `RETURN_READ`/sync kabulü `BLOCKED_TARGET_ACCESS`; eski runtime üzerinde capability elle yükseltilmedi.

## 2026-08-11 - v10.36 iade eşitleme ve referans çalışma alanı

- Return sync, yerelde eksik olan bağlı siparişi yalnız exact remote read ile hydrate eder; sipariş bulunamazsa claim üretmek yerine mevcut `RETURN_ORDER_NOT_FOUND` audit davranışını korur.
- İade ekranı referans sekme, filtre ve ayrıntılı satır düzenine taşındı.
- Infrastructure build 0 hata/0 uyarı, Trendyol adapter sözleşme testi `50/50`, iade operasyonları web testi `4/4` ve web typecheck `PASS`; gerçek Trendyol Stage return-read henüz çalıştırılmadı (`NOT_RUN`).

## 2026-08-11 - Stage iade capability teşhisi

- Bağlı Stage hesabında `TRENDYOL_CONNECTION_TEST` işi `SUCCEEDED` (job correlation `206e125b45d042b4981f527b8fb81ad1`); ORDER_READ, PRODUCT_READ ve REFERENCE_READ destekliyken claims probu `RETURN_READ=UNKNOWN` bıraktı.
- `CapabilityView` probe evidence notunu API ve bağlantı ekranına taşır; unknown durumunun adapter hata kodu dağıtım sonrası doğrudan görülebilir. Read gate fail-closed kaldı; capability manuel olarak `SUPPORTED` yapılmadı.
- Infrastructure build 0 hata/0 uyarı ve web typecheck `PASS`; teşhis sürümünün gerçek Stage tekrar koşusu `NOT_RUN`.

## 2026-08-11 - v10.35-r3 yayın kapısı kaydı

- Fatura işlemleri menüsünün görünen başlığı ile erişilebilir adı eşit tutuldu: `Fatura işlemleri`.
- Hedefli `TrendyolOperationsPages.test.tsx` kontrolü `4/4 PASS`.
- Tam web regresyonu, Stage ve canlı kabul bu dar test hizalamasında `NOT_RUN`; yayın hattı sonucu ayrıca kaydedilecektir.

## 2026-08-10 - v10.29 gorsel CSP ve tablo sikilastirma

| Kanit | Durum | Not |
| --- | --- | --- |
| Trendyol CDN CSP | PASS_STATIC | `img-src` yalniz `https://cdn.dsmcdn.com` ile genisletildi; genel HTTPS wildcard acilmadi. |
| Fatura ve tablo CSS | PASS_STATIC | Rozetler kompakt, baslik ile ilk siparis bitisik; detayli tarayici kabul release sonrasina `NOT_RUN` kaydedildi. |

# F3 Trendyol Kanıt Günlüğü

## 2026-08-12 — Stage operator action visibility refactor

| Kanıt | Durum | Not |
| --- | --- | --- |
| Manuel Stage görünürlüğü | CODED_TARGETED_VALIDATED | Shipment/return UI, runtime tarafından kabul edilen Stage manuel işlemlerini capability/evidence durumu nedeniyle gizlemez. Provider gerçek destek/cevabı ve teknik doğrulama korunur. Infrastructure build 0 hata/uyarı, Web typecheck ve `F3Pages.test.tsx` 7/7 geçti. |

## 2026-08-10 - v10.27 canli onayli urun sekli

| Kanit | Durum | Not |
| --- | --- | --- |
| Dogrudan urun satiri | PASS_LOCAL | `content[]` icinde barkod, stok kodu ve `images[]` tasiyan canli cevap sekli kayipsiz eslenir. |
| Geriye uyumluluk | PASS_LOCAL | Eski ic ice `variants[]` fixture davranisi korunur. |
| Canli gorsel kabul | PENDING_DEPLOYMENT | Release sonrasi hedef barkod yeniden esitleme ve tarayici gorsel kontrolu beklenir. |

## 2026-08-10 - v10.26 sipariş satırı kaynak görseli

| Kanıt | Durum | Not |
| --- | --- | --- |
| Trendyol satır snapshot'ı | PASS_LOCAL | Satır ham JSON'u kalıcı saklanır; renk/beden/model siparişten, görsel aynı barkodun salt-okunur onaylı ürün snapshot'ından okunur. |
| Geriye uyumlu migration | PASS_LOCAL | Nullable `SourceSnapshotJson` alanı eklendi; mevcut sipariş verisi silinmez veya yeniden yazılmaz. |
| Sipariş UI | PASS_LOCAL | Fatura hücresi referansla hizalandı; SHIPPED satırında yalnız Kargo Takip/İşlem Yapılamaz kalır; başlık sticky olur. |
| Test/derleme | PASS_LOCAL_WITH_ENV_BLOCK | Solution build, Docker gerektirmeyen testler, TypeScript, 19/19 web testi ve production build geçti; Docker/Testcontainers `BLOCKED_ENVIRONMENT`. |
| Canlı veri zenginleştirme | PENDING_DEPLOYMENT | Eski satırlar salt-okunur sipariş eşitlemesiyle kaynak snapshot kazanır. |


## 2026-08-10 — v10.25 eşleştirme yayın doğrulaması

| Kanıt | Durum | Not |
| --- | --- | --- |
| Uçtan uca kapsam | PASS_LOCAL | Kategori kapsamlı özellik ve değer eşleştirmesi, `scopeExternalId` gövdesini doğrulayarak Playwright'ta 3/3 geçti. |
| Yayın hattı | RETRY_REQUIRED | v10.24 imaj hattı, eski E2E beklentisi kapsam alanını beklemediği için durdu; kod davranışı değil, güncellenen test beklentisi bu kayıtta düzeltildi. |

## 2026-08-10 — v10.24 eşleştirme merkezi işlev düzeltmesi

| Kanıt | Durum | Not |
| --- | --- | --- |
| Yerel katalog yazımı | CODED | Tek seviye panel kategorisi oluşturma ve arşivleme yalnız yerel kataloğa yazar; Trendyol'a dış yazma başlatmaz. |
| Özellik değerleri | CODED | Kategoriye zaten eklenmiş özellikte girilen yeni seçenekler, kuralı yeniden eklemeye çalışmadan değer listesine kaydedilir. |
| Kapsamlı eşleme | CODED | Kategori özelliği ve değer eşlemeleri `scopeExternalId` ile kaydedilir; aynı Trendyol alanı farklı kategorilerde çakışmaz. |
| Hızlı doğrulama | PASS_LOCAL | `npm run typecheck`, 19/19 Vitest ve production web build geçti. Yerel Node `24.15.0` olduğundan exact `24.18.1` release-toolchain kanıtı değildir; tam release hattı yeniden çalıştırılacak. |

## 2026-08-10 — v10.22 sipariş satırı katalog geri düşümü

| Kanıt | Durum | Not |
| --- | --- | --- |
| Yerel etki | PASS | Dış API yazması yok; yalnız sipariş görünümü ve katalog medya çözümü değişti. |
| Katalog eşleşmesi | CODED | ID/SKU yanında barkod; aktif varyant medyası yoksa aktif ürün ana görseli kullanılır. |
| Hızlı derleme | PASS | `dotnet build src/MarketplaceHub.Infrastructure/MarketplaceHub.Infrastructure.csproj --no-restore` → 0 hata. |
| Web typecheck | NOT_RUN | Yerel `node_modules` bulunmuyor. |

## 2026-08-09 — v10.21 sipariş cursor sayfalama

| Kanıt | Durum | Not |
| --- | --- | --- |
| Yerel kayıt sayımı | VERIFIED_READ_ONLY | Production PostgreSQL'de `sales.orders` toplamı 3.618'dir; panelin `limit=200` isteği yalnız ilk sayfayı kullandığı için ekranda 200 görünüyordu. |
| Tam yerel liste | CODED | Web istemcisi `/orders` cursor devam sayfalarını 200'lük parçalarla birleştirir; sekmeler, filtreler ve sayfalama tüm yerel havuzdan hesaplanır. Eksik veya tekrar eden cursor hata üretir. |
| Dış etki | NOT_APPLICABLE | Değişiklik yalnız yerel salt-okunur listeleme davranışıdır; Trendyol'a yazma yapmaz. |
| Hızlı doğrulama | NOT_RUN | Bu çalışma ortamında web bağımlılıkları bulunmadığı için TypeScript komutu başlatılamadı (`tsc` yok); ayrıntılı test hızlı doğrulama politikası uyarınca ertelendi. |

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
## 2026-08-09 — v10.18 kategori ve marka eşleştirme merkezi

| Kanıt | Durum | Not |
| --- | --- | --- |
| İki sekmeli tutarlı UI | PASS_LOCAL | Kategori ve Marka görünümleri aynı karşılıklı panel/Trendyol kart düzenini kullanır; aktif kapsam dışı platform seçenekleri kaldırıldı. |
| Aranabilir seçim kutuları | PASS_LOCAL | Bağlantı, panel kategorisi/markası ve Trendyol referansı klavye destekli combobox içinde filtrelenir. |
| Panel kategorisi oluşturma | PASS_LOCAL | Mevcut `POST /catalog/categories` endpointi kategori adı ve isteğe bağlı üst kategoriyle çağrılır; yeni kategori otomatik seçilir, dış platform yazımı yoktur. |
| Hedefli web testi | PASS_LOCAL | `F3Pages.test.tsx` ve `CatalogWorkspacePages.test.tsx` toplam 9/9 geçti; TypeScript kontrolü geçti. |
| Trendyol Stage / canlı görsel kabul | NOT_RUN | Tam CI ve production browser doğrulaması deployment sonrasında yapılacaktır. |
## 2026-08-09 — v10.19 tek ekran sipariş operasyonu

- Ayrı sipariş detay sayfası kaldırıldı; eski route listeye yönlenir.
- Durum sekmeleri, toplu işlem menüsü, SVG sidebar ikonları, menü daraltma kontrolü ve ürün görsel hizası güncellendi.
- Doğrulama: Vitest 18/18 PASS, TypeScript PASS, Vite build PASS, .NET solution build PASS.
- Dış sipariş/kargo yazımı canlı kontrolde çalıştırılmayacaktır; capability ve açık operasyon onayı kapıları korunur.

## 2026-08-09 — v10.20 sipariş ürün adedi rozeti

- Ürün satırındaki adet rozeti görsel/metin alanının solundan kaldırılıp ürün kartının sağ üst köşesine taşındı.
- Yalnız CSS yerleşimi değişti; sipariş verisi, miktar hesabı ve Trendyol dış yazma kapıları değişmedi.
- TypeScript, 19/19 Vitest ve production web build yerelde geçti; canlı görsel kabul deployment sonrasında yapılacaktır.

## 2026-08-10 — v10.30 siparis liste sunumu

- Varsayilan siparis durum filtresi `NEW` olarak ayarlandi; sekme ve filtre degistirme davranisi korundu.
- Urun miktar rozeti gorsel cercevesinin sag ust kosesine hizalandi ve mikro ihracat fatura etiketindeki bilgi ikonu kaldirildi.
- Ayrintili test: `NOT_RUN`; hizli hedefli kaynak/diff kontrolu ve deployment sonrasi canli tarayici kabulu planlandi.

## 2026-08-10 — v10.30-r3 CI port izolasyonu

- Yayin hattinda .NET build 0 hata, diger test gruplari 177/177 PASS; full-stack tarayici kaniti sabit `5173` portunda iki kez giris formunu bulamadi.
- E2E Vite sunucusu bos localhost portuna alindi; yeniden yayin kaniti bekleniyor.

## 2026-08-10 — v10.30-r4 full-stack kanit oturumu

- Sabit ve dinamik portla iki denemede de UI giris locator'i 60 saniye bekledi; asil siparis akisi baslamadi.
- Kanit oturumu ayni `/api/v1/auth/login` endpointiyle acilir; dashboard ve devamindaki browser/API/Postgres/worker/siparis liste akisi korunur.

## 2026-08-10 — v10.30-r5 full-stack kanit CSRF bootstrap

- Full-stack tarayici kaniti login isteginden once `/api/v1/auth/csrf` ile cookie/token ciftini alir ve `X-CSRF-TOKEN` basligini gonderir.
- `node --check src/MarketplaceHub.Web/e2e/full-stack-fake.mjs`: PASS.
- Exact release hatti sonucu bekleniyor; production auth davranisi degistirilmedi.

## 2026-08-10 — v10.30-r6 full-stack kanit atomik CSRF

- r5 oturum acmayi gecti; siparis esitleme istegi dashboard yuklenirken yenilenen CSRF cookie nedeniyle `REQUEST_VERIFICATION_FAILED` verdi.
- Eslestirme isi POST'u tek Playwright request context icinde yeni alinmis token ile atomik hale getirildi.
- Production middleware ve endpoint davranisi degistirilmedi; exact release sonucu bekleniyor.

## 2026-08-10 — v10.31 siparis adet rozeti gorsel cercevesi

- Canli v10.30-r6 kontrolu: varsayilan `Yeni` PASS; mikro ihracat pseudo ikonu `display:none/content:none` PASS.
- Adet rozeti artik urun gorseliyle ayni `reference-product-media` kapsayicisinda ve cercevenin sag ustune sabitlenir.
- Ayrintili web paketi kullanici hizli test talebi geregi `NOT_RUN`; exact release hatti production oncesi zorunludur.

## 2026-08-10 — v10.32 eslestirme ve siparis gorsel etkilesimi

- Trendyol kategori yollarindaki `[TDG]` sunum onekleri kullanici arayuzunde temizlenir; kaynak snapshot degismez.
- Yerel ozellik karti tiklanarak secilir ve ustteki deger alani secili ozellige yazar. Eksik kategori ozellik verisi icin ayni calisma alaninda guvenli reference-sync isi baslatilir.
- Siparis urun gorselleri modalde buyutulebilir; fatura bekleme rozeti metin olarak korunurken unlem pseudo ikonu kaldirilir.
- Web TypeScript PASS, Vitest 19/19 PASS; Stage reference-sync ve canli gorsel kabul `NOT_RUN`.
# 2026-08-10 — v10.33 kategori/marka eşleştirme akışı

- Özellik eşleme hedef kategorisi kullanıcı tarafından seçilir; teknik Trendyol ad önekleri kaynak snapshot değiştirilmeden yalnız sunumda temizlenir.
- Özellik oluşturma ve değer ekleme ayrıldı; marka çalışma alanı platform odaklı yerel kayıt/çip düzenine taşındı.
- TypeScript PASS, hedefli F3 Vitest 7/7 PASS. Stage referans kabulü ve tam release hattı `NOT_RUN`.

## 2026-08-11 — v10.35 hedefli çalışma alanı düzenlemesi

- Kategori özellik kartlarındaki değerler kategori seçildiğinde görünür kalır; kullanıcı seçtiği karta üstteki alanla değer ekler, değeri çarpı aksiyonuyla pasifleştirir.
- Tekrarlanan değer uyarıları sayfanın geneli yerine ilgili özellik bölümünde gösterilir. Kart seçimi yalnız kenarlık geri bildirimi kullanır.
- Kategori/marka eşleştirme başlıkları ve arama alanları sadeleştirildi; Trendyol kaynak kaydı değiştirilmedi.
- İadeler ekranında mevcut Trendyol iade-eşitleme endpointine yalnız okuma kuyruğu başlatan aksiyon eklendi.
- Hedefli web typecheck `PASS`; Stage iade kabulü ve tam web regresyonu `NOT_RUN`.
# 2026-08-11 - Eşleştirme ve sipariş kaynak alanları hızlı kabulü

- Metin olarak başlayan yerel özelliğe ilk seçenek ekleme akışı seçim tipine güvenli dönüşümle onarıldı.
- Kategori zorunluluk ve kargo sağlayıcı alanları için dar, belgeli geri uyumluluk okuması genişletildi; eksik değer uydurulmaz.
- Solution build ve format: `PASS`; Docker gerektirmeyen .NET testleri `143 PASS`; yerel Docker kapalı olduğundan 19 Testcontainers testi `BLOCKED_ENVIRONMENT`; web typecheck, `19/19` test ve production build `PASS`. Canlı kabul deployment sonrasına bırakıldı.
# 2026-08-11 - Stage label capability canary

| Kanıt | Durum | Not |
| --- | --- | --- |
| Dar Stage test kapısı | CODED_TARGETED_VALIDATED | Owner/Admin yalnız `LABEL_READ`/`LABEL_WRITE` için takip numaralı Stage paketi seçebilir; production bağlantısı, diğer capability'ler ve normal dış-yazma akışı kabul edilmez. |
| Write izolasyonu | CODED_TARGETED_VALIDATED | `LABEL_WRITE` canary yalnız `Picking/Processing` veya `ReadyToShip` Stage paketinde create → read-back yapar. Global/connection external-write switch'i değişmez; normal iş akışları bu istisnayı kullanamaz. |
| Evidence kaydı | CODED_TARGETED_VALIDATED | Başarıda resmi endpoint, Stage/store scope, etiket formatı, dönen gerçek etiket içeriğinin SHA-256 değeri ve audit kaydı yazılır; hata halinde capability `UNKNOWN` kalır. |
| Yerel doğrulama | PASS_TARGETED | `dotnet build MarketplaceHub.sln --no-restore` 0 hata/0 uyarı; `npm.cmd run typecheck` PASS. |
| `LABEL_READ` gerçek Stage canary | PASS_STAGE | 2026-08-11 16:40:39 UTC: Paket `92257909` / takip `7250000170335942` ortak etiket read-back'i `SUCCEEDED`; `LABEL_READ=SUPPORTED`, resmi kaynak URL'si, Stage/store kapsamı, audit kaydı ve 64 karakterlik SHA-256 fixture checksum saklandı. |
| `LABEL_WRITE` gerçek Stage canary | BLOCKED_REMOTE_FIXTURE | Aynı yerel `ReadyToShip` paketin uzaktaki platform durumu `Invoiced` olduğundan create isteği `REMOTE_REQUEST_REJECTED` ile fail-closed engellendi. `LABEL_WRITE` elle yükseltilmedi; gerçek uzak `ReadyToShip` Stage paketi beklenir. |
| Resmî Stage write fixture | BLOCKED_REMOTE_FIXTURE | Trendyol belgesindeki seller `2738`, sipariş `1238522676`, takip `7260000167037306` yerel paket `92038607` olarak bulundu ve uzaktaki durum `Picking` doğrulandı. Buna rağmen create isteği `REMOTE_REQUEST_REJECTED` ile engellendi; dokümandaki tarihsel tracking fixture'ı create endpointi için güncel değil. `LABEL_WRITE` elle yükseltilmedi. |
| Taze write fixture yolu | PENDING_IMPLEMENTATION | Resmî Stage Test Order API (`POST /integration/test/order/orders/core`) taze sipariş/işlem senaryosu oluşturur; dönen sipariş ve güncel takip numarasıyla create → read-back yapılmadan `LABEL_WRITE` destekli sayılmaz. |
| Taze Stage Test Order fixture | CODED_TARGETED_VALIDATED | Sadece Owner/Admin, Trendyol `STAGE`, seller `2738` ve resmî test barkodu `8683772071724` için tek denemelik durable job eklendi. Fake test adapter sözleşmesi güncellendi; .NET build 0 hata/uyarı ve web typecheck PASS. Gerçek Stage yürütmesi bekleniyor. |
| Test Order yanıt sözleşmesi | PASS_TARGETED | `orderNumber` değeri JSON string veya sayı olarak dönse de kayıpsız okunur; Infrastructure hedefli build 0 hata/uyarı verdi. |
| İlk gerçek Test Order dispatch | FAIL_CLOSED_LOCAL | 2026-08-11: Worker F3 job allow-list'inde `TRENDYOL_STAGE_TEST_ORDER` eksik olduğundan iş `UNSUPPORTED_JOB_TYPE` ile dış çağrı yapmadan `DEAD` kaldı. Allow-list düzeltildi; capability durumu değişmedi ve yeni Stage yürütmesi bekleniyor. |
| İkinci gerçek Test Order isteği | BLOCKED_REMOTE_CONTRACT_ALIGNMENT | 2026-08-11: Worker yönlendirmesi geçtikten sonra Stage servisinden `REMOTE_SERVER_ERROR` döndü. Resmî Create Test Order örneğindeki adres alanları ve test barkodu (`9900000000486`) dar fixture'a uyarlandı; normal sipariş ve production yazma yolları değişmedi. Yeni gerçek yürütme bekleniyor. |
| Taze Stage order + read sync | PASS_STAGE | 2026-08-11: Resmî fixture ile `1265633895` siparişi oluşturuldu; `TRENDYOL_STAGE_TEST_ORDER` ve tek sipariş read-sync ilk denemede başarılı oldu. Paket `92286944` / takip `7250000170847858` uzaktan `ReadyToShip` döndü. Eksik canonical yazım nedeniyle yerel durum fail-closed `ManualReview` kaldı; eşleme düzeltildi, capability yükseltilmedi. |
| Idempotent canonical projection repair | CODED_TARGETED_VALIDATED | Aynı raw kaynak olayı geçmişte `ManualReview` olarak kaydedilmişse ve yeni canonical eşleme tanınmış bir durum üretiyorsa, yalnız yerel projeksiyon güncellenir. Yeni dış etki, yeni history olayı veya sıralama kontrolünü atlama yoktur. Gerçek Stage re-sync ve label canary bekleniyor. |
| Timestamp-tolerant projection repair | CODED_TARGETED_VALIDATED | Gerçek Stage re-sync aynı paket/raw durum için farklı olay zamanı döndürdü. Onarım yalnız aynı paket + aynı raw durum + mevcut `ManualReview` koşulunda tanınan canonical durumu projekte eder; dış çağrı ve history kaydı üretmez. |
| Empty-line replay projection path | CODED_TARGETED_VALIDATED | Gerçek re-sync boş order-line tekrarında erken dönüşün paket projeksiyonunu engellediğini gösterdi. Erken dönüş kaldırıldı; paket upsert idempotency, miktar bütünlüğü ve güvenli canonical onarım koşulları korunuyor. |
| Empty-line local projection repair | CODED_TARGETED_VALIDATED | Gerçek Stage re-sync, boş satırlı cevabın ilk satır-miktar kapısından da döndüğünü gösterdi. Yalnız boş satırda mevcut paketin dar local projection onarımı çalışır; satır mevcutsa miktar bütünlüğü korunur. Eski remote zamanında da yalnız tanınan `ManualReview` local raw durum normalize edilir. |
| Taze fixture Picking → label zinciri | CODED_TARGETED_VALIDATED | Resmî common-label sözleşmesi create çağrısından önce `Picking` veya `Invoiced` ister. Canary yalnız son `STAGE_TEST_ORDER_CREATED` audit kaydına ait `STAGE/2738` taze siparişi ve tek doğrulanmış satırı kabul eder; resmî `Picking` payloadını gönderip ardından create/read-back yapar. Genel/production dış yazma anahtarları kapalı kalır. Gerçek Stage kabulü bekleniyor. |
| Taze fixture Picking Stage sonucu | BLOCKED_REMOTE_FIXTURE | 2026-08-11: Paket `92286944` üzerinde resmî `Picking` çağrısı da `REMOTE_REQUEST_REJECTED` ile fail-closed sonlandı. LABEL_WRITE ve SHIPMENT_WRITE yükseltilmedi. Sonraki dar teşhis yalnız sağlayıcının güvenli hata kodunu saklayacak; ham yanıt veya secret saklanmaz. |
| Ortak etiket taşıyıcı uygunluğu | CODED_TARGETED_VALIDATED | 2026-08-12: Son taze Stage fixture metaverisi `Yurtiçi Kargo Marketplace` taşıyıcısını gösterdi. Trendyol ortak etiket sözleşmesi yalnız Trendyol öder `Aras Kargo` veya `TEX` gönderilerini kabul ettiğinden, `LABEL_WRITE` canary ve normal ortak etiket kuyruğu artık uyumsuz taşıyıcı için uzak çağrı yapmadan `COMMON_LABEL_CARRIER_UNSUPPORTED` ile fail-closed durur. `CommonLabelCarrierPolicyTests` 7/7 geçti. Eski provider reddi kanıt değildir; LABEL_WRITE ve SHIPMENT_WRITE `UNKNOWN` kalır. Uygun Stage fixture kabulü beklenir. |
| Manuel runtime capability ayrımı | CODED_TARGETED_VALIDATED | 2026-08-12: Manuel product, price/inventory, shipment, common-label, return ve sync enqueue yolları `UNKNOWN` capability/evidence nedeniyle artık Stage veya Production’da durmaz. Production master + connection write switch ve bütün teknik/idempotency/audit doğrulamaları korunur. `IntegrationRuntimePolicyTests` 3/3 ve Infrastructure build geçti; gerçek provider kabulü `NOT_RUN`dır. |
| Repository formatter | BLOCKED_REPOSITORY_LINE_ENDINGS | `dotnet format MarketplaceHub.sln --verify-no-changes --no-restore`, değiştirilmeyen `CatalogModels.cs` dahil repository-geneli CRLF→LF `ENDOFLINE` ihlalleri nedeniyle başarısız oldu. Geniş mekanik satır-sonu dönüşümü bu işte yapılmadı. |
| Scheduled read capability ayrımı | CODED_TARGETED_VALIDATED | Scheduler `ORDERS`, `RETURNS` ve `REFERENCE_DATA` için capability evidence yokluğunda artık iş üretimini atlamaz. Aktif connection, interval/jitter ve dedup korunur; write/`AUTO_*` kapsamı değişmez. Infrastructure derlemesi 0 hata/uyarı geçti. |
| Stage operasyon yüzeyi | CODED_TARGETED_VALIDATED | Stage bağlantı özeti artık dış yazmayı kapalı veya evidence anahtarına bağlı göstermez. Manuel Stage işlemleri aktif connection, credential, teknik validation, duplicate koruması ve provider response doğrulamasıyla çalışır; teknik capability/evidence ayrıntıları normal kullanıcı yüzeyinden İşlem Takibi/diagnostics'e taşınır. Production kartı write switch korumasını korur. |
| Taze Stage siparişi ve scoped read sync | PASS_STAGE | 2026-08-12: Normal panelden `TRENDYOL_STAGE_TEST_ORDER` `2a51b03fb93a4815ac872e75bc2ff42b` ve ardından yalnız sipariş için `TRENDYOL_ORDER_SYNC` `81789fc3d09c47bc971f01348f2a8a8d` ilk denemede `SUCCEEDED`. Sipariş `1507428594`, paket `92287436`, takip `7250000170858397`, durum `ReadyToShip`; taşıyıcı `Yurtiçi Kargo Marketplace` common-label kapsamı dışında olduğundan LABEL_WRITE canary bilinçli olarak gönderilmedi. |
