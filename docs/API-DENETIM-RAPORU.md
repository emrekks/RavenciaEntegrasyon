# API Denetim Raporu (19 Ekim 2026)

Kapsam: pazaryeri adaptörleri (Trendyol, Hepsiburada, N11, Pazarama, Shopify), panelin kendi REST API'si ve
senkron/iş altyapısı. İnceleme salt okunurdu: kod değiştirilmedi, pazaryerlerine istek atılmadı. Bulgular resmi
API dokümanlarıyla (Trendyol, Hepsiburada OpenAPI, n11, shopify.dev) karşılaştırıldı; Pazarama dokümanı yalnız
repodaki plan üzerinden kontrol edildi.

İşaretler: **✓** kodda tekrar doğrulandı · **(d)** doğrulanamadı, canlı/SIT denemesi gerekir.

---

## 1. Önce düzeltilmesi gerekenler

### Güvenlik
| # | Bulgu | Yer | Etki |
|---|---|---|---|
| G1 ✓ | Rol kontrolü yolun sonuyla (`EndsWith("/credential")` vb.) yapılıyor; sonuna `/` eklenen istek genel `/connections` kuralına düşüyor | `Api/Security/RoleAuthorizationMiddleware.cs:51-57` | OPERATIONS rolü `PUT .../credential/` ile mağaza kimlik bilgisini değiştirebilir, `.../deep-delete/` ile bağlantı silebilir |
| G2 | Bağlantı veri sıfırlama ve görünürlük OPERATIONS'a açık; kimlik bilgisiyle bağlantı oluşturma da öyle (kimlik değiştirme ise yönetici istiyor) | `MarketplaceEndpoints.cs:16, 53-54` | Yıkıcı işlem yetkisi tutarsız |
| G3 | Ürün görseli proxy'sinde SSRF: alan adı iç IP'ye çözülürse engellenmiyor, yönlendirme takip ediliyor, yanıt önce tamamen belleğe okunuyor | `Catalog/CatalogEndpoints.cs:154-176` | Her rol sunucuya iç ağa istek attırabilir; büyük dosya belleği şişirir |
| G4 | Parola ile yeniden doğrulamada deneme sınırı yok | `InvoicingEndpoints.cs:592`, `AuthEndpoints.cs` | Ele geçmiş oturumla parola tahmini |
| G5 | Webhook hız sınırı IP başına 60/dk | `Program.cs:70-72` | Pazaryerleri tek IP'den gönderir; kampanya yoğunluğunda 429 → HB akışı kara listeye alabilir |

### Kesin hatalar (veri veya işlem bozuluyor)
| # | Bulgu | Yer | Etki |
|---|---|---|---|
| H1 ✓ | Trendyol hata gövdesinde herhangi bir `code` varsa hata, HTTP kodundan bağımsız olarak "doğrulama hatası" sayılıyor (varsayılan kol da değer döndürüyor) | `Trendyol/ErrorMapping/TrendyolErrorMapper.cs:11-13, 38` | Yerel devre kesici (503) ve kodlu 5xx'ler tekrar denenmeyip "Blocked" oluyor; belirsiz yazma kaydı silinip işlem yeniden gönderilebiliyor |
| H2 | Tek bir 429, satıcının bütün Trendyol uçlarını devre kesiciyle kapatıyor | `TrendyolResilienceHandler.cs:112-117, 270-286` | Sipariş kotası dolunca fiyat/stok/fatura da duruyor |
| H3 ✓ | Shopify fiyat-stok: `quantities` dizisi nesne gibi okunuyor, her satır istisna fırlatıyor | `Shopify/ShopifyHttpClient.cs:501` | Shopify'a yazma açıldığı anda hiçbir fiyat-stok gönderilemez |
| H4 ✓ | n11 fatura tarihi `dd/MM/yyyy HH:mm` metni gidiyor; resmi doküman milisaniye zaman damgası istiyor (kod yorumu da "canlıda doğrula" diyor) | `N11/N11Invoices.cs:82-83` | n11 fatura linkleri reddedilebilir |
| H5 | n11 fatura numarası 16 karakter kuralına göre doğrulanmıyor; n11depom siparişleri için `sender` gönderilmiyor (varsayılan yalnız SELLER) | `N11Invoices.cs:59-63`, `N11Orders.cs:18-22`, `N11Returns.cs:30-33` | Hatalı numara n11'de reddedilir; n11depom siparişleri ve iadeleri hiç gelmez |
| H6 | Hepsiburada sipariş detayında paketler kalemlerin içinde (`items[].packageNumber`, `hasInvoice`); kod `packages[]` arıyor | `Hepsiburada/HepsiburadaJsonMapper.cs:481-494, 1419-1422` | Çok paketli siparişte fatura durumu yanlış okunur; detay okuması paket üretmez |
| H7 | HB paket webhook'larında (`intransit/deliver/undeliver`) sipariş no yok, `unpack`'te `orderNumbers[]` dizisi var; panel siparişi bulamayıp olayı atlıyor | `MarketplaceJobProcessor.Webhooks.cs:13-23` | HB webhook açıldığında kargo/teslim olayları işlenmez (yoklama yakalar) |
| H8 | HB ortak etiket yanıtı JSON (`data[]`, `format`); kod ham ZPL/PDF sanıyor | `HepsiburadaPackageOperations.cs:140-143, 187-225` | Etiket çıktısı bozuk |
| H9 | HB satıcı indirimi iç içe alanda (`merchantDiscount.totalPrice.amount`); 0 okunuyor | `HepsiburadaJsonMapper.cs:1073, 1335` (d) | Net tutar/fatura fazla çıkabilir |
| H10 | Trendyol bölünmüş siparişte toplamlara yerini yenisine bırakan eski paket de ekleniyor | `Trendyol/Mapping/TrendyolJsonMapper.cs:163-165` | Tutar iki kat |
| H11 | Pazarama statü 18 ("İptal süreci başlatıldı") "Hazırlanıyor" olarak eşleniyor | `Pazarama/PazaramaOrderMapper.cs:122` | Satıcı iptal talebini görmeyip kargolar |
| H12 | Pazarama fiyat kabul edilip stok reddedilirse tekrar denemede fiyat yeniden gider, stok hiç gitmez | `PazaramaPriceInventory.cs:37-48` | Stok güncellemesi takılı kalır |

### Dayanıklılık (iş sistemi)
| # | Bulgu | Yer | Etki |
|---|---|---|---|
| D1 | Webhook işleme ve etki doğrulama işleri "orders" kilidini almıyor; aynı siparişlere eşzamanlı yazıyor | `MarketplaceSyncExecutionLock.cs:372-392` | Sürüm çakışması → yeniden deneme → Trendyol kotası boşa gider |
| D2 | Dağıtım veya zaman aşımında çalışan fiyat/stok gönderimi "belirsiz" olup manuel incelemeye düşüyor | `MarketplaceJobProcessor.cs:1232-1239, 9486-9495` | Her dağıtımda stok güncellemesi bekler, fazla satış riski |
| D3 | Tek heartbeat hatası (kira dolmamışken) çalışan işi iptal ediyor | `Worker.cs:355-359` | Kısa veritabanı kesintisi yazma ortasında iptal yaratır |
| D4 | En fazla 6 deneme, en uzun bekleme 60 sn (toplam ~2 dk) | `Domain/JobRetryPolicy.cs:5-14` | 5 dakikalık pazaryeri kesintisinde işler ölü kapanır |
| D5 ✓ | Etki taraması yalnız en eski 100 açık kaydı alıyor; bağlantı bilgisi olmayan eski kayıtlar 7 gün yer kaplıyor | `ExternalEffectSweep.cs:16-31` | Çok belirsiz kayıt birikirse yenileri doğrulanmaz |
| D6 | Uzun sipariş döngülerinde EF ChangeTracker temizlenmiyor; her `SaveChanges` tüm takip edilenleri geziyor | `MarketplaceJobProcessor.cs:2364-2464`, `AppDbContext.cs:178-184` | Büyük ilk aktarımda O(n²) yavaşlama ve bellek |
| D7 | İşlemci, kira servisi ve zamanlayıcıda log yok; iş bağlamı (iş türü, bağlantı, korelasyon) loglara eklenmiyor; metrik/trace yok | genel | Sorunları sonradan izlemek zor |

---

## 2. Orta / düşük öncelikli
- **Trendyol:** fatura durumu okuması vitrin/tarih ipucu vermeden 14 vitrini deniyor (`TrendyolHttpClient.cs:778`); sayı olmayan JSON değeri tüm sayfayı düşürüyor (`TrendyolJsonMapper.cs:969-1019`); kargoya son tarih yanlış alanlara düşüyor (`:87`); artımlı ürün okuması içerik/QC değişikliklerini kaçırıyor; 2026 ürün/fiyat kotaları için ayrı kova yok; V2 10.000 kayıt sınırı denetlenmiyor; ölü kod (`SendClaimsReadAsync`, `PackageItemStatuses`).
- **Hepsiburada:** paket listesinde `Offset` büyük harf (d); talep listesinde `lineItemId` yok (d); değiştirilebilir kargo yanıtı tek nesne/PascalCase; fiyat-stok yüklemesi JSON yerine XML ve tr-TR virgülü (yazma kapalı); tarih saat dilimi (d); fatura 409'u "zaten faturalı" olarak ele alınmıyor; kargo bilgisi paket başına ayrı istek.
- **N11:** 15 günlük pencere sınırda (14'e inmeli); sayfa kaydırması + 2 dk örtüşme sipariş kaçırabilir; iade tarihleri UTC sayılıyor (3 saat kayma); iade onayında belgede olmayan `splitQuantity`.
- **Pazarama:** tek sanal paket karışık kalem durumlarını gizliyor; kalem bazlı tek tek durum güncellemesi yarıda kalabiliyor; hız sınırı beklemesi zaman aşımına sayılıyor; hız sınırı anahtarı satıcı değil bağlantı bazlı; token kilit sözlüğü büyüyor.
- **Shopify:** iç içe sorgu maliyeti 1000 puanı aşabilir (d); JSON olmayan 5xx yanıtı yakalanmıyor; vergi/iskonto/kargo eşlemesi eksik, her pakete sipariş toplamı yazılıyor; 3 aylık pencere tek imleçle (25.000 sınırı, `read_all_orders`); API sürümü bağlantı kaydına sıkı bağlı (sürüm yükseltmesi tüm bağlantıları düşürür); client-credentials token önbelleği scoped; `productByHandle` deprecated.
- **REST API:** tüm GET'ler her role açık (PII, etiket, fatura PDF; bazıları pazaryeri kotası harcıyor); her `ArgumentException` `INVALID_CURSOR` dönüyor ve iç mesaj sızıyor; idempotency anahtarı bazı yazma uçlarında zorunlu değil, `If-Match` hash'e dahil değil; pazaryeri 401'i istemciye 401 gidiyor (SPA oturum düştü sanabilir); hata biçimi ve sayfalama stilleri tutarsız; herkese açık fatura linklerinin süresi yok.

---

## 3. Daha temiz yapı önerisi

**REST API**
- Yol tabanlı rol middleware'i yerine uç nokta başına yetki politikası (`RequireAuthorization("Elevated")`).
- Kaynak başına `MapGroup` + `TypedResults`; grup filtreleri: `TenantFilter`, `IdempotencyFilter`, `IfMatchFilter`.
- Tek `ProblemDetails` dönüştürücü (`ServiceError` → problem+json), tek sayfalama modeli, doğrulama katmanı, `AddOpenApi`.
- Dosyalar arasında kopyalanan yardımcılar (`Tenant`, `Problem`, `RequireIdempotency`, `TryIfMatch`) tek yere.

**Adaptörler**
- Alan adı tahmin eden `JsonElement` gezintisi (`Text(item, "orderNumber", "OrderNumber", "orderNo")`) yerine resmi OpenAPI'den üretilmiş tipli DTO'lar. H6, H7, H9 bu yüzden fark edilmedi.
- Platform başına endpoint kataloğu: yol, metot, sayfa parametresi adı, en büyük limit, tarih biçimi/saat dilimi, pencere sınırı tek yerde.
- Elle yazılmış hız sınırlayıcı/devre kesici yerine uç grubu bazlı `Microsoft.Extensions.Http.Resilience` pipeline'ı.
- Hata eşleme işlem bazlı politika nesnesi; satıcı kodu yalnız ilgili işlemde ve 4xx'te.
- Büyük istemci sınıfları (Trendyol 980, HB 1.400 satır) port/API ailesi bazında bölünmeli.
- Gerçek SIT/canlı yanıtlarla sözleşme testleri.

**İş sistemi**
- `JobTypeRegistry`: öncelik, şerit, zaman aşımı, yeniden deneme planı, kilit grubu tek tabloda.
- `PlatformCapabilities`: platform başına desteklenen kaynaklar, pencere, sayfa boyu, kota.
- `IJobHandler<TPayload>` + `JobDispatcher`: 9.500 satırlık işlemcideki `platform == "…"` dallarının yerine.
- `SyncRunner`: fetch → map → upsert → tipli cursor; sayfa sınırı, aynı cursor tekrarı, iptal ve telemetri tek yerde.
- `ExternalWriteGuard`: dış yazmaların hepsi bağlamlı etki kaydı, sınırlı token ve doğrulama işiyle.
- `ILogger` scope + `Meter` + `ActivitySource`.

**Kademeli geçiş (yeniden yazmadan):**
1. Gözlemlenebilirlik + D1-D4.
2. `JobTypeRegistry` ve `PlatformCapabilities`; mevcut sabitler oradan okunur.
3. `IJobHandler` + dispatcher; önce küçük işler taşınır, eski işlemci "bilinmeyen tür" yolu olarak kalır.
4. `SyncRunner` önce Pazarama/N11'de, sonra HB ve Trendyol'da.
5. Yazmalar `ExternalWriteGuard`'a.
6. İşlemci boşalınca silinir. Her adım ayrı commit ve mevcut testlerle doğrulanır.

---

## 4. Önerilen sıra
1. **Dalga 1 (küçük, yüksek etki):** G1-G2 yetki, H1-H2 Trendyol hata/429, H3 Shopify, H4-H5 n11, H6-H7 HB, H10 Trendyol tutarı, H11 Pazarama iptal talebi, D1 kilit grupları, D3 heartbeat.
2. **Dalga 2 (dayanıklılık):** D2, D4-D7, G3-G5, H8-H9, H12 ve "orta" listesi.
3. **Dalga 3 (yapı):** bölüm 3'teki kademeli geçiş.

(d) işaretli maddeler SIT veya canlı okuma ile doğrulanmadan değiştirilmemeli.

---

## 5. Uygulama durumu (19 Ekim 2026)

### Dalga 1 — tamam
| # | Ne yapıldı |
|---|---|
| G1 | Rol kuralları yolun tam segmentleriyle eşleşiyor; `/credential/`, `//deep-delete` gibi yazımlar artık doğru kurala düşüyor |
| G2 | Bağlantı oluşturma, veri sıfırlama ve veri görünürlüğü yönetici (OWNER/ADMINISTRATOR) istiyor |
| H1 | Satıcı kodu yalnız 4xx'te ve yalnız belgeli kargo kodları (CPE001–007, RTE003) için sınıfı belirliyor; yerel devre kesici 503'ü ve kodlu 5xx'ler yeniden deneniyor |
| H2 | Devre kesici satıcı + uç ailesi bazında; sipariş kotasındaki 429 stok/fiyat/iade/fatura uçlarını kapatmıyor |
| H3 | Shopify `quantities` listesinden `available` okunuyor |
| H4 | n11 `invoiceDateTime` milisaniye zaman damgası (dokümandan doğrulandı) |
| H5 | n11 fatura numarası 3+4+9 kuralıyla doğrulanıyor; sipariş paket okumalarında `sender=ALL` (dokümandan doğrulandı). İade servisinde `sender` alanı dokümanda bulunamadı, değiştirilmedi |
| H6 | HB sipariş detayında fatura durumu her paket için kendi kalemlerinden okunuyor (canlı veride `items[].packageNumber/hasInvoice` doğrulandı). Detaydan paket üretimi eklenmedi: paketler paket listesi yolundan sağlıklı geliyor, detaydan üretim paket tutarlarını ezebilirdi |
| H7 | HB paket olayları sipariş numarasını olay yolundan, paneldeki paketten/kalemden, en son HB'den buluyor; `unpack`'teki `orderNumbers[]` okunuyor |
| H10 | Trendyol bölünmüş siparişte yerini yenisine bırakan paket toplamlara eklenmiyor |
| H11 | Pazarama statü 18 → `CancelRequested` (Beklemede); hazırlanıyor görünmüyor |
| D1 | Webhook ve etki doğrulama işleri "orders" kilit grubunda |
| D3 | Tek heartbeat hatası, kira hâlâ geçerliyse işi iptal etmiyor |

### Dalga 2 — tamam
| # | Ne yapıldı |
|---|---|
| D2 | Sonucu belirsiz fiyat-stok gönderimi manuel incelemeye düşmüyor; aynı etki kaydıyla yeniden gönderiliyor (değerler mutlak, içerik güncel mi diye her gönderimde kontrol ediliyor) |
| D4 | Bekleme planı 5 sn, 15 sn, 1 dk, 2 dk, 5 dk (~8,5 dk); deneme sayısı 6 kaldı (değiştirmek migration ister) |
| D5 | Etki taraması yalnız işi kalan kayıtları alıyor |
| D6 | `UpsertOrders` kaydettiği sayfanın kendi yüklediği satırlarını takipten çıkarıyor; çağıranın imleç/iş kayıtları korunuyor |
| D7 | Worker ve API logları `LogContext`'ten zenginleşiyor; her iş JobId, JobType, TenantId, ConnectionId, CorrelationId kapsamında çalışıyor. Metrik/trace (Meter, ActivitySource) henüz yok |
| G3 | Görsel proxy'si yalnız genel internet adreslerine bağlanıyor (bağlanma anında DNS çözülüp kontrol ediliyor), yönlendirmeleri kendi izliyor (en çok 3), boyut sınırında okumayı kesiyor |
| G4 | Tüm yeniden doğrulamalar giriş kilidini paylaşıyor (5 hatalı parola → 15 dk) |
| G5 | Webhook sınırı bağlantı başına 1200/dk |
| H8 | HB etiketi JSON zarfı (`data[]`) gelirse çözülüyor, dosya gelirse eskisi gibi; HB dokümanına erişilemediği için iki biçim de destekleniyor |
| H9 | **Hata değil.** Canlı veride `unitPrice + merchantDiscount.unitPrice` yuvarlak liste fiyatını veriyor (509,25 + 15,75 = 525,00): `totalPrice` zaten indirimli ödenen tutar. Net ve fatura doğru. Brütü değiştirmek fatura kalem kontrolünü bozacağı için dokunulmadı |
| H12 | Pazarama'da stok önce gidiyor, iki istek birbirinden bağımsız; her barkodun fiyatı ve stoku ayrı kaydediliyor |
| Orta | 400 mesajları güvenli; n11 pencereleri 14 gün, iade/soru tarihleri Türkiye saati; Shopify HTML hata sayfası durum koduyla sınıflanıyor; Trendyol'da metin olarak gelen sayı sayfayı düşürmüyor; ölü kod silindi |

### Dalga 2 sonrası kalanlar — tamam
| Konu | Ne yapıldı |
|---|---|
| D7 metrik | `Meter` ve `ActivitySource` "MarketplaceHub": iş sayısı, sonucu ve süresi; pazaryeri isteği sayısı, durumu ve süresi (her istemcinin en içteki işleyicisi). `dotnet-counters` ile okunur; dışa aktarıcı eklenince toplanır |
| Trendyol son tarih | Yalnız `extendedAgreedDeliveryDate` / `agreedDeliveryDate` (canlı: 473/473 siparişte dolu ve kayıtlı değerle aynı); tahmini teslim alanları son tarih sayılmıyor |
| Trendyol fatura okuması | Sipariş tarihi, vitrin ve paket durum zamanıyla okunuyor; 14 vitrinin tamamını denemiyor, 14 günden eski paketleri de buluyor |
| Trendyol genel kota | Belgelendiği gibi uç noktası başına 50/10 sn |
| Trendyol ürün okuması | Artımlı okumada `CONTENT_MODIFIED_DATE` geçişi; onay bekleyenler `LAST_MODIFIED_DATE` ile artımlı |
| Trendyol V2 10.000 sınırı | Bu sınıra takılabilecek tek yol (ulaşılmayan V2 durum dalı) ölü kodla birlikte silindi |
| HB saat dilimi (d) | **Canlı veriyle doğrulandı:** HB tarihleri Türkiye saatiyle, ofsetsiz geliyor. Ofsetsiz tarihler +03:00 okunuyor, sorgu pencereleri Türkiye saatiyle gidiyor. Açık siparişler bir sonraki okumada düzeliyor; eski teslim edilmiş siparişlerin tarihi 3 saat ileri kalabilir |
| HB `Offset` (d) | Kodda büyük harfli parametre yok; hepsi `offset` / `limit` |
| HB talep `lineItemId` (d) | Okunuyor; eşleşmeyen iade kalemi artık sessizce atlanmıyor, operasyon kaydı açılıyor |
| HB 409 | Fatura linki 409 dönerse paket geri okunuyor; faturalıysa iş başarısız değil uzlaştırmaya gidiyor |
| HB diğer | Tek nesne kargo yanıtı destekleniyor; uzlaştırmada aynı paketin takip bilgisi ikinci kez okunmuyor |
| Shopify tutarlar | Her pakete sipariş toplamı yazılıyordu; toplamlar paketlere kalem değeri oranında bölünüyor, tek paket aynı kalıyor |
| Shopify maliyet (d) | Son 24 saatte maliyet hatası yok. 500 puan üstü sorgular loglanıyor; `MAX_COST_EXCEEDED` ayrı kodla tanınıyor ve sayfa küçültülerek yeniden deneniyor |
| Shopify pencere | Sorguda `updated_at:<=`; yarım kalan okuma kendi penceresinde sürüyor; 20.000 kayda yaklaşınca son görülen değişiklikten yeni sorgu |
| Shopify sürüm / token | Bağlantı ayardaki sürümle eşleşmek zorunda değil, farklı sürüm yanıtı loglanıyor; token önbelleği süreç ömrü boyunca ve kimlik bilgisiyle anahtarlı; `productByIdentifier` |
| Pazarama | Kota beklemesi zaman aşımına sayılmıyor ("gönderilmedi" hatası); kota satıcı başına; yarıda kalan kalem güncellemesi tekrar denemede tamamlanıyor; paket durumu en az ilerlemiş kalemden (iptal süreci varsa beklemede) |
| n11 | Örtüşme en az 10 dk; okuma sırasında pencere sayısı değişirse pencere yeniden okunuyor |
| REST rol | Belge içerikleri ve canlı pazaryeri okumaları READONLY rolüne kapalı; herkese açık linkler açık |
| REST idempotency | `If-Match` özetin parçası (gönderildiğinde); 256 karakterden uzun anahtar 400; beş yazma ucunda anahtar zorunlu ve web gönderiyor |
| REST hata biçimi | Soru ve gösterge uçları problem+json; ortak yardımcılar problem+json içerik tipiyle; Pazarama finans ucunda pazaryeri kimlik sorunu 401 yerine 502 |
| Yapı adım 2–3 | `JobTypeRegistry` (kilit grubu, öncelik, zaman aşımı ve ayarı, heartbeat, zaman aşımı sonrası bekleme); `IJobHandler` + dağıtıcı, ilk taşınan iş soru senkronu |

### İncelenip değiştirilmeyenler
- Pazaryeri 401'i SPA oturumunu düşürmüyor; istemci yalnız CSRF belirtecini yeniliyor.
- Pazarama token kilit sözlüğü kimlik bilgisi başına bir kayıt tutuyor; bağlantı sayısıyla sınırlı.
- Herkese açık fatura linkleri süresiz kalıyor: Trendyol ve n11 linki müşteriye süresiz gösteriyor, süre koymak müşterinin faturasını koparır. İptal yolu mevcut (saklanan link değişince eski link çalışmaz).
- HB fiyat-stok XML'inde virgüllü fiyat ürün ve stok yolunda tutarlı; yazma kapalı.
- n11 iade onayında boş `splitQuantity`: dokümanı bulunamadı; kısmi onay arayüzde yok.
- Trendyol'da reddedilen ürünler içe aktarılmıyor: ürün modeline yeni durum ister; barkod bazlı yayın durumu sorgusu bunları görüyor.
- Sayfalama biçimleri (`after`, `page`, `pageNumber`) web'in kullandığı haliyle bırakıldı.

### Kalan yapısal geçiş
- `PlatformCapabilities`, kalan işlerin handler'lara taşınması, `SyncRunner`, `ExternalWriteGuard` ve işlemcinin silinmesi. Her adım ayrı commit ve mevcut testlerle yapılacak; işlemci ~10.800 satır olduğu için birkaç oturumluk çalışma.
