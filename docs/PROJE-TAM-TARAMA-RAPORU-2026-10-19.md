# Proje Tam Tarama Raporu (19 Ekim 2026)

Bu rapor projenin bütününü (API, iş kuyruğu ve veritabanı, pazaryeri adaptörleri, iş mantığı, web arayüzü, altyapı ve CI/CD)
kaynak kod üzerinden tarar; tespit edilen eksikleri ve hataları önem sırasıyla listeler, her biri için somut çözüm önerir.
Hiçbir dosya değiştirilmedi; sunucuda yalnız salt-okunur kontrol yapıldı.

**Yöntem.** Dokuz alan ayrı ayrı okundu. Her bulgu için dosya ve satır verilir; "doğrulanmadı" yazanlar kod okumasıyla
mekanizması görülen ama canlı veri veya platform dokümanıyla teyit edilemeyen maddelerdir. En ağır 14 bulgu rapor
yazılmadan önce kaynak kodda ikinci kez birebir doğrulandı.

**Bilinen ve bu raporda tekrarlanmayan maddeler:** yedeğin sunucu dışında kopyası yok (S3/IAM kararı sizde); Pazarama
kimlik bilgileri reddediliyor (doğru Client Id/Secret girilmeli); HB SIT webhook'u HB'nin kurulumunu bekliyor;
`MarketplaceJobProcessor`'ın parçalanması (JobTypeRegistry/IJobHandler/SyncRunner) ayrı bir yapısal iş.

---

## 1. Genel sonuç

Proje sağlam bir çekirdeğe sahip. Çok kiracılı veri izolasyonu her uçta açık `TenantId` filtresiyle sağlanıyor; CSRF, çerez
bayrakları, webhook imza doğrulaması, SSRF korumalı medya vekili, idempotency ve "belirsiz dış yazma" kayıtları (effect record)
doğru kurgulanmış. Derleme 0 uyarı, 1.857 + 388 test yeşil, 0 bilinen zafiyet. Veri çekimi tarafı bugünkü itibarıyla
optimize (bkz. bölüm 2).

Buna karşılık dokuz alanda toplam **147 bulgu** çıktı; bunların **18'i yüksek** önemde. Yüksek önemdekiler beş kümede toplanıyor:

1. **Mali doğruluk.** Trendyol'da e-Fatura mükellefi kurumsal alıcıya hep e-Arşiv kesiliyor (adres anlık görüntüsü yanlış
   düzeyde okunuyor). "Düzeltme faturası" olarak ikinci bir tam tutarlı satış faturası korumasız açılabiliyor. Sipariş ve
   sevk tarihleri UTC günüyle gidiyor.
2. **Sipariş verisi bütünlüğü.** Çok paketli Trendyol siparişlerinde sipariş tutarı sayfada görülen son paketin tutarına
   düşüyor. Bölünen paketin üst paketi "Yeni" sayılıp sonsuza dek açık kalıyor. Pazaryerinden "iptal" gelen onaylı iade,
   karar ve stok kayıtlarıyla birlikte siliniyor.
3. **Stok güvenliği.** Bir pazaryerindeki satış, azalan stoğu diğer pazaryerlerine anında iletmiyor (15 dakikalık uzlaştırma
   taramasına kalıyor); o tarama da ilk "gönderilecek değişiklik yok" adayında duruyor.
4. **İş kuyruğu dayanıklılığı.** HB geçmiş okuması her hatada baştan başlıyor (10 dakikalık zaman aşımına hiç sığmayabilir).
   Trendyol fatura uzlaştırması sipariş okumalarıyla aynı satırlara ayrı kilit grubundan yazıp sürüm çakışması üretiyor.
   Pazaryerinin gönderdiği bekleme süresi sınırsız uygulanıyor. Webhook/iş/inbox tabloları için saklama süresi yok.
5. **Arayüz ve işletim.** Oturum düşünce sayfalar sonsuza dek yeniden deniyor; elle girilen fatura tarihi her kayıtta 3 saat
   kayıyor; anlık güncelleme anahtarları stok ve fatura ekranlarını yenilemiyor. Dış yazmalar HTTP isteği içinde tarayıcının
   iptal belirteciyle çalışıyor. Yedek zamanlayıcısı sunucunun kapalı olduğu saate ayarlı; deploy öncesi yedek günün ilk
   yedeğini yeniden kullanıyor; uyarı/alarm kanalı yok.

---

## 2. Ölçümler

| Ölçüt | Değer |
|---|---|
| Derleme (tüm çözüm, Release) | 0 uyarı, 0 hata |
| Backend testleri | 1.857 geçti, 76 atlandı (PostgreSQL gerektirir; CI'da çalışır), 0 başarısız |
| Web testleri | 90 dosya, 388 test, hepsi geçti |
| `dotnet list package --vulnerable` | 0 zafiyet (6 proje, geçişli dahil) |
| `npm audit` | 0 zafiyet (27 üretim, 111 geliştirme bağımlılığı) |
| Web tip denetimi | 0 hata |
| ESLint | Depoda yapılandırma yok (3 `eslint-disable` yorumu etkisiz) |
| Ana CSS paketi | 659 KB (90 KB gzip), bütçenin %96'sı; toplam CSS kaynağı 1,63 MB, 48 dosya, >500 `!important` |
| En büyük dosyalar | `MarketplaceJobProcessor.cs` 9.714 satır; `marketplace/pages.tsx` 4.054 satır (74 bileşen, en uzun satır 7.647 karakter); `catalog/pages.tsx` 3.957 satır |
| TODO/FIXME | 1 |
| EF model ve migration | Uyumlu (105 varlık, snapshot ile fark yok) |
| Canlı (son 6 saat) | HB 18 istek/dk, Trendyol 9 istek/dk (kota ~%15), bekleme p95 ≤1,3 sn, başarısız iş yalnız Pazarama (kimlik) |

---

## 3. Öncelik tablosu (yüksek önemdeki bulgular)

| # | Alan | Bulgu | Çözüm (özet) |
|---|---|---|---|
| F1 | Fatura | Trendyol e-Fatura mükellefi alıcıya hep e-Arşiv kesiliyor | `InvoiceAmounts.TrendyolInvoiceType` kökte `eInvoiceAvailable` okusun; e-Fatura gereken siparişi 422 ile durdur |
| F2 | Fatura | `originalInvoiceId` ile korumasız ikinci SATIS faturası | Gerçek İADE akışı yazılana kadar `OriginalInvoiceId != null` isteklerini 422 ile reddet |
| S1 | Sipariş | Çok paketli sipariş tutarı son paketin tutarına düşüyor | Tutarları kalıcı paketlerden yeniden hesapla; sayfa tüm paketleri içermiyorsa üzerine yazma |
| S2 | Sipariş | UNPACKED üst paket "Yeni" sayılıyor, sonsuza dek açık | UNPACKED → iptal/aşıldı statüsü; `OriginExternalPackageId` ile referanslanan paketleri sayım ve fatura hedeflerinden çıkar |
| İ1 | İade | Uzak "iptal" onaylı iadeyi karar ve stok kayıtlarıyla siliyor | Kararı/kanıtı olan iadeyi silme, `Cancelled` olarak sakla ve sorun kaydı aç |
| İ2 | İade | HB belirsiz iade işlemi iadeyi kalıcı kilitliyor | Çözülemeyen effect'te kararı FAILED yap; durum taramasında MANUAL_REVIEW kararları da uzlaştır |
| İ3 | Soru | Reddedilen cevap bir daha gönderilemiyor | Uzak REJECTED gelince gönderim durumunu REJECTED yap, bekleyen metni temizle, REJECTED'a cevaba izin ver |
| K1 | Stok | Satış azalan stoğu diğer pazaryerlerine anında iletmiyor | `ProjectOrderReservations` ve stok ayarlamalarında aktif `ChannelOffer`'ı olan her bağlantıya dispatch kuyruğa al |
| K2 | Stok | Stok uzlaştırma taraması ilk "değişiklik yok" adayında duruyor | `NO_EXTERNAL_CHANGES`/`LIVE_OFFER_REQUIRED` → Success; `ReconcileStock` Blocked'da `continue` |
| Q1 | Kuyruk | HB geçmiş okuması her hatada 0. pencereden başlıyor | Trendyol kuralını uygula (kayıtlı durum devam eder), `retryAfterFailure`'ı baseline tetikleyicisinden çıkar, pencereler arası `Continue` |
| Q2 | Kuyruk | Fatura uzlaştırması "orders" dışı kilitle aynı satırlara yazıyor | `OrderInvoiceReconciliation`'ı "orders" grubuna al veya `Version++` yapmadan `ExecuteUpdate`; `DbUpdateConcurrencyException` → Deferred |
| Q3 | Kuyruk | Webhook/effect/manual işleri, inbox, çözülmüş sorunlar, durum geçmişi sınırsız büyüyor | `JobRetentionService`'i genişlet; inbox 30 gün; çözülmüş sorunları sil; aynı durumda tekrar satırı yazma |
| A1 | Adaptör | Pazaryerinin `Retry-After` süresi sınırsız uygulanıyor; HB `X-RateLimit-Reset` göreli saniye sanılıyor | `JobContracts.FromAdapterError`'da 15 dk ile sınırla; `>1e9` değeri epoch say |
| A2 | Adaptör | Shopify stok mutasyonu (`changeFromQuantity`, `@idempotent`) şema ile uyuşmuyor (doğrulanmalı) | Geliştirme mağazasında introspeksiyon; `compareQuantity` kullan, direktifi kaldır |
| P1 | API | Dış yazmalar istek içinde `RequestAborted` ile çalışıyor; 200 fatura × 45 sn | Sunucu bütçesi (60 sn), istemci belirteci dış yazmaya ulaşmasın, satır içi en çok 5 kalem, kalanı 202 |
| W1 | Web | 401 sonrası sayfalar sonsuza dek yeniden deniyor | `hubApi`'de global `onUnauthorized`; 401/403/404/412/422'de otomatik tekrarı durdur |
| W2 | Web | Elle fatura tarihi her kayıtta 3 saat kayıyor | `datetime-local` için yerel biçimleyici (`toLocalInput`) |
| W3 | Web | Anlık güncelleme anahtarları (`inventory`, `invoices`) hiçbir sorguyla eşleşmiyor | Anahtar haritasını gerçek sorgu köklerine çevir; her anahtarın üreticisi olduğunu test et |

---

## 4. Alan bulguları

Önem: **Y** yüksek, **O** orta, **D** düşük.

### 4.1 Fatura (e-Arşiv / e-Fatura, pazaryerine teslim)

| # | Ö | Bulgu | Yer | Çözüm |
|---|---|---|---|---|
| F1 | Y | `TrendyolInvoiceType` `eInvoiceAvailable`'ı `invoiceAddress` altında arıyor; Trendyol eşleyicisi adres nesnesini doğrudan kök olarak saklıyor → `available` hep false → kurumsal e-Fatura mükellefine e-Arşiv kesiliyor (GİB açısından geçersiz, iptal + yeniden kesim gerekir). Test yalnız sarmalanmış biçimi deniyor. | `Domain/InvoiceAmounts.cs:117`, `Trendyol/Mapping/TrendyolJsonMapper.cs:92,896`, `MarketplaceSalesService.cs:2507`, `TrendyolEFaturamCanonicalPayload.cs:30-31` | Kökte `invoiceAddress` varsa içine in, yoksa kökü kullan; düz biçim için test ekle. TEMELFATURA durumu okunamadığı için (`OutgoingInvoiceStatusPath`) bu siparişleri 422 `EINVOICE_RECIPIENT_REQUIRES_EFATURA` ile durdurmak daha güvenli. |
| F2 | Y | `POST /invoices` `OriginalInvoiceId` taşıyınca sipariş kilidi, mükerrer kontrol, paket uygunluğu ve pazaryeri fatura kontrolü atlanıyor; satır `OrderFiscalGuarded=false`, `SequencePurpose=ADJUSTMENT` olduğu için tekil indeksler de kapsamıyor; yük yine `SATIS` ve tam tutarlı → aynı mal için ikinci e-Arşiv GİB'e gidebilir. | `InvoicingEndpoints.cs:122`, `InvoicingBillingService.cs:1082-1180`, `InvoicingModelConfiguration.cs:68,72`, `TrendyolEFaturamInvoicePayload.cs:77` | Gerçek İADE akışı (İADE tipi, orijinal numara/tarih, orijinalin Accepted/Completed olması) yazılana kadar 422 ile reddet. |
| F3 | O | Sağlayıcıya ulaşmadan reddedilen gönderim (`EFATURAM_OPERATION_FORBIDDEN`, `CAPABILITY_NOT_VERIFIED` vb.) faturayı `Submitting`'de bırakıyor; etiket "İşleniyor", eylem WAIT, paket "Faturalı" sekmesinde. | `InvoicingJobProcessor.cs:285-291`, `InvoicingBillingService.cs:1574-1578,1494`, `InvoicingWorkspaceWorkflow.cs:297,972` | `Submitting && ExternalReference == null && LastErrorCode != null` → yeniden denenebilir ve "hata" etiketi. |
| F4 | O | Sipariş ve sevk tarihi UTC takvim günüyle gidiyor (`.Date` +00:00 değerde); `issuedAt` Türkiye saatinde. 01:30 TR'deki sipariş bir gün önceye yazılıyor. | `TrendyolEFaturamCanonicalPayload.cs:105,110` | `DateOnly.FromDateTime(value.ToOffset(+3h).DateTime)`; ortak `TurkeyOffset`. |
| F5 | O | Faturadan sonra iptal/iade gelince hiçbir şey olmuyor (`AdjustmentRequired` hiç atanmıyor); iptal siparişler çalışma alanından düşüyor; sağlayıcı iptali pazaryeri linkini bırakıyor, yeniden kesim ön kontrolde engelleniyor. | `InvoiceModels.cs:20,40`, `InvoicingBillingService.cs:478`, `InvoicingJobProcessor.cs:1712-1739,228-236` | İptal/onaylı iade + Accepted/Completed SATIS → `AdjustmentRequired` + sorun kaydı; sağlayıcı iptalinden sonra teslim durumunu "geri alınacak" işaretle, linki kaldır. Trendyol link silme ucu sarılı mı doğrulanmalı. |
| F6 | O | `ReplaceCancelledInvoiceAsync` operatör beyanıyla mali slotu boşaltıyor; `Completed → Cancelled` durum makinesinde yasak; sağlayıcıya `QueryStatusAsync` sorulmuyor. | `InvoicingWorkspaceWorkflow.cs:449-473` | `ExternalReference` varsa uzak `CanonicalStatus == CANCELLED` şart, değilse 409. |
| F7 | O | Mikro ihracat / TRY dışı siparişler yurt içi SATIS e-Arşiv olarak kesiliyor; para birimi/ülke kapısı yok (doğrulanmadı: Trendyol bu siparişlerde TRY dışı `currencyCode` gönderiyor mu). | `InvoicingBillingService.cs:1181,1295-1354`, `TrendyolEFaturamInvoicePayload.cs:76-77` | `Currency != TRY || IsMicroExport` → 422 `INVOICE_EXPORT_FLOW_REQUIRED`. |
| F8 | O | Kurumsal tespiti Trendyol `commercial` alanını saymıyor; 11 haneli TCKN + boş vergi dairesi → `11111111111`; TCKN/VKN kontrol hanesi hiçbir yerde doğrulanmıyor. | `TrendyolEFaturamCanonicalPayload.cs:46-51,170-227` | `commercial` işaretini ekle; `ValidateAsync`'te TCKN/VKN algoritması. |
| F9 | O | "Hiç kuyruğa girmemiş" teslim kurtarması `PackageId != null` istiyor; SATIS faturaları `PackageId = null` → ölü kod. | `InvoicingJobProcessor.cs:1655-1670`, `InvoicingBillingService.cs:1174` | Hedefleri siparişin iptal olmayan paketlerinden çöz (`EnqueueDeliveryAsync` gibi). |
| F10 | D | `UnknownResult` için RECONCILE sunuluyor ama `ExternalReference` olmadan çalışamıyor (`F4_JOB_REJECTED`). | `InvoicingBillingService.cs:1409,1496`, `InvoicingJobProcessor.cs:336` | Referans yoksa RECONCILE'ı gizle; "uzakta fatura yok" onayına yönlendir. |
| F11 | D | Yerel uzlaştırma denklemi yanlış: `TaxExclusive + Tax − Discount`; `TaxExclusive` zaten indirimsiz → her indirimli faturada sahte `TOTAL_EQUATION` farkı. | `InvoicingReconciliationService.cs:19` | `− DiscountTotal`'ı kaldır. |
| F12 | D | (a) Toplu onayda `DbUpdateException` sonrası `ChangeTracker.Clear()` yok → kalan kalemler CONFLICT; (b) `Invoice.DueAt` hiç atanmıyor, `ScanDue` çalışmıyor; (c) `UnknownResult` "FATURA_REDDEDILDI" görünüyor; (d) detayda pazaryeri numarası tercih edilip uyuşmazlık gizleniyor. | `InvoicingWorkspaceWorkflow.cs:179-183`, `InvoicingJobProcessor.cs:1619`, `InvoicingBillingService.cs:974,1029` | Sırasıyla: tracker temizle; `DueAt` doldur ya da taramayı kaldır; "belirsiz" etiketi; yerel numarayı göster. |

İyi yapılanlar: SATIS tekilliği filtreli tekil indeks + kiracı kapsamlı `FOR UPDATE` + ön izleme özeti ile katmanlı korunuyor;
pazaryerine teslim gerçekten idempotent (okuma-yazma-okuma, STARTED→UNKNOWN, kör tekrar yok); belirsiz sonuç ile red ayrılıyor.

### 4.2 Sipariş yaşam döngüsü ve içe alım

| # | Ö | Bulgu | Yer | Çözüm |
|---|---|---|---|---|
| S1 | Y | Sayfa birleştirme Gross/Discount/Net'i yalnız sayfadaki paketlerden topluyor; `UpsertOrder` tazeyse üzerine yazıyor. Stream sayfaları tek kardeş paket taşıdığı için 300+200'lük sipariş önce 200, sonra 300 oluyor; son paket teslim edilince kalıcı. Pano ciro `Order.NetAmount` okuyor. | `MarketplaceJobProcessor.cs:7363-7375,7568-7599`, `TrendyolJsonMapper.cs:165-170`, `DashboardReadService.cs:196-198` | Paket döngüsünden sonra tutarları kalıcı paketlerden (üst paket hariç) yeniden hesapla; ya da yalnız tüm paketler sayfadaysa üzerine yaz. |
| S2 | Y | `UNPACKED` → `New`; `Processing/ReadyToShip → New` geçişi yasak → üst paket PROCESSING'de takılıyor, "İşlemde" sekmesinde kalıyor, her sayfada `PACKAGE_TRANSITION_REJECTED`, fatura teslim teyidi hiç olmuyor, açık sipariş taraması sonsuza dek okuyor. Created üst paket New kabul edilince sipariş "Yeni"de kalıyor; tüm çocuklar iptal olsa bile CANCELLED olmuyor. N11 planı UnPacked'in iptal gibi sayılmasını yazıyor. | `Domain/OrderModels.cs:238,297,346,136`, `MarketplaceJobProcessor.cs:7809-7861`, `MarketplaceSalesService.cs:17-23,346-391`, `N11OrderMapper.cs:90-107` | UNPACKED'i operasyonel olmayan statüye (Cancelled veya yeni `Superseded`) eşle, her açık durumdan ulaşılabilir olsun; `OriginExternalPackageId` ile referanslanan paketleri `Aggregate`, `CurrentShipmentPackages`, sekme filtreleri, sayaçlar ve fatura hedeflerinden çıkar; N11 üst/çocuk bağlantısını sayfalar arası kur. |
| S3 | O | Reddedilen geçiş olayın tamamını düşürüyor: kargo/takip/tahsis uygulanmıyor, geçmiş satırı yok, olay `knownEventIds`'e girmediği için her yoklamada yeniden değerlendiriliyor, ManualReview'a hiç çıkmıyor. | `MarketplaceJobProcessor.cs:7809-7825`, `PackageIngestionSafety.cs:81-86` | Redde rağmen meta/fatura gözlemini birleştir; olayı `REJECTED:` işaretiyle kaydet; uzak uç statü yerel reddedilince ManualReview. |
| S4 | O | Anlık İşleme Al `StatusOccurredAt`'i HTTP sonrası yerel saatle yazıyor → pazaryerinin kendi Picking olayı daha eski kalıp düşüyor; PROCESSING geçmiş satırı yok; o olaydaki kargo/fatura alanları atlanıyor. | `MarketplaceSalesService.cs:1328-1332`, `PackageIngestionSafety.cs:86` | `StatusOccurredAt`'e dokunma (yalnız `UpdatedAt`) veya iş yolundaki gibi `GetAsync` + `UpsertOrder` ile geri oku. |
| S5 | O | Liste ucu her satırda maskesiz TCKN/vergi no, e-posta ve ham adres JSON'u döndürüyor; `search` ham anlık görüntüde arıyor; yalnız kiracı kapısı var. Trendyol müşteri anlık görüntüsü tüm paket yükü. | `MarketplaceSalesService.cs:212-213,405,734-741,2519-2522`, `MarketplaceEndpoints.cs:76-79` | Listede maskeli kimlik (`***1234`) ve asgari adres; tam değer yalnız detayda ve yetkiyle. |
| S6 | O | Anlık işlem uzak yazma başarılı olduktan sonra `Version` çakışmasıyla 500 verebiliyor (API yolu "orders" danışma kilidini almıyor); effect `CompletedAt=null` kalıyor, aynı anahtar `EXTERNAL_EFFECT_VERIFYING` döndürüyor, uzlaştırma işi kuyruğa girmemiş. | `MarketplaceSalesService.cs:1265-1431`, `Program.cs:95` | Uzak başarıdan hemen sonra `effect.CompletedAt`'i ayrı `SaveChanges` ile yaz; projeksiyonu `DbUpdateConcurrencyException` yakalayarak uygula (yeniden yükle veya uzlaştırma işi). |
| S7 | D | Paketsiz HB siparişleri 30 gün sonra statüden bağımsız listeden düşüyor (paketlenmeden iptal edilenler İptal sekmesinden kayboluyor). | `HepsiburadaOrderVisibility.cs:14-19` | Kuralı `NEW/ON_HOLD/UNVERIFIED` ile sınırla. |
| S8 | D | Net tutar iptal paket/kalemleri içeriyor; kısmi iptaller ciroya sayılıyor. | `TrendyolJsonMapper.cs:168-170`, `MarketplaceJobProcessor.cs:7682-7688` | Net'i iptal olmayan paketlerden / `OrderedQuantity − CancelledQuantity` ile türet. |
| S9 | D | Paket tam okumada Undelivered olunca `ShippedQuantity` 0'a düşüyor (yalnız görüntü). | `PackageIngestionSafety.cs:106-111` | Undelivered'ı sevk edilmiş kümesine ekle. |
| S10 | D | `AppDbContext` imleç ilerlemesi ve outbox `CreatedAt` için `DateTimeOffset.UtcNow` kullanıyor (geri kalan her yer `TimeProvider`). | `AppDbContext.cs:140,188` | `TimeProvider` enjekte et. |

### 4.3 Stok, fiyat ve yayınlama

| # | Ö | Bulgu | Yer | Çözüm |
|---|---|---|---|---|
| K1 | Y | `STOCK_PROJECTION_DISPATCH` yalnız siparişi veren bağlantı için kuyruğa giriyor (tek enqueue noktası); elle stok ayarı ve iade stok girişi hiç kuyruğa almıyor. Diğer pazaryerleri 15 dakikalık `STOCK_RECONCILE_SHORT`'a kalıyor → tek adetlik üründe çift satış penceresi. | `MarketplaceJobProcessor.cs:8483,8518,8552`, `InventoryService.cs:42-85`, `MarketplaceSalesService.cs:2050,2128` | Aktif `ChannelOffer`'ı olan her bağlantı için bir dispatch; aynı yayılımı `AdjustAsync` ve iade onayına da uygula. |
| K2 | Y | `ReconcileStock` ilk Retry dışı başarısızlıkta `return false`; `DispatchStockProjection` `NO_EXTERNAL_CHANGES`/`LIVE_OFFER_REQUIRED`/`CHANNEL_OFFER_INVALID` için Blocked dönüyor; `ReconciledAt` yalnız içe aktarımda yazıldığı için panelden açılan her varyant her turda aday; sıralama hep aynı adayı öne koyuyor → tarama her 15 dakikada aynı yerde duruyor, K1'in güvenlik ağı çalışmıyor. Canlıda 281 `LIVE_OFFER_REQUIRED` Blocked iş bu kaynaktan. | `MarketplaceJobProcessor.cs:9302-9319,8570` | Bu üç kodu Success say (`SyncPriceInventory` zaten öyle); Blocked'da `continue`. |
| K3 | O | Trendyol fiyat gönderimi stok da taşıyor ama yalnız Fiyat politikası denetleniyor (üretici ve işleyici); Stok politikası kapalıyken sayım sırasında miktarlar eziliyor. | `InventoryService.cs:188-217`, `MarketplaceJobProcessor.cs:1229-1232`, `TrendyolWriteComposers.cs:169` | Trendyol için daima `{Price, Stock}` iste. |
| K4 | O | Fiyat politikası kapatılınca otomatik Trendyol stok gönderimi sessizce duruyor (dispatch Stok'a bakıyor, SUBMIT Fiyat da istiyor → Blocked). | `MarketplaceJobProcessor.cs:8567,1229-1236` | Yalnız stok dispatch'inde son gönderilen fiyatı kullan ya da dispatch'te Fiyat politikasını da denetleyip sorun kaydıyla Success dön. |
| K5 | O | `VatInclusion` ve `RoundingMode` saklanıyor ama hiçbir composer uygulamıyor; `EXCLUDED` ile 100 TL girilen ürün 120 yerine 100'den listeleniyor. | `InventoryService.cs:118-147`, tüm `*WriteComposers`/`*PublicationComposer` | Brüte çevirme (`sale*(1+kdv)`) için ortak yardımcı ya da `INCLUDED` dışını reddet. |
| K6 | O | Rezervasyon politikası (`ReservationMode`, `ReserveOnStatuses`) ve `ExpiresAt` ölü; `IsReservationEnabled` hiç çağrılmıyor. | `SynchronizationPolicies.cs:264`, `MarketplaceJobProcessor.cs:8497-8510` | Rezervasyon dalını politikayla kapıla ya da alanları UI/API'den kaldır. |
| K7 | D | Trendyol/HB fiyat gönderimi 4 ondalık gönderiyor; HB yayın yolu 2'den fazlasını reddediyor. | `TrendyolWriteComposers.cs:167-169` | 2 haneye yuvarla ve hash'e dahil et. |
| K8 | D | Seçenek/web rengi değerleri güncel anlık görüntü dışındaki eski eşlemelere düşebiliyor → `CREATE_REJECTED`. | `ProductPublicationComposer.cs:336,237,253` | `mappings.All` yedeğini kaldır; `ATTRIBUTE_VALUE_MAPPING_REQUIRED` ver. |
| K9 | D | Katalog içe aktarımı `PriceVersion`'ı artırıp `LastPriceHash`'i güncellemiyor → Trendyol'un bildirdiği fiyat ona geri gönderiliyor. | `MarketplaceJobProcessor.cs:6765-6767` | İçe aktarım sonrası hash'i yaz veya aynı bağlantıdan gelen değişimde sürümü artırma. |
| K10 | D | Barkod/stok kodu tekilliği yalnız ürün içinde denetleniyor; başka ürünün aynı bağlantıdaki barkodu kontrol edilmiyor (DB tekil indeks doğrulanmadı). | `*PublicationComposer.cs` | Oluşturmadan önce `ChannelListingVariants`/`MarketplaceVariantLinks` kontrolü, `BARCODE_DUPLICATE`. |
| K11 | D | MAIN stok satırı olmayan aktif teklif fiyat/stok gönderiminden sessizce dışlanıyor. | `TrendyolWriteComposers.cs:137,181,216` | Left join + `INVENTORY_REQUIRED` sorun kaydı. |

İyi yapılanlar: ledger idempotency anahtarları, `ProjectionVersion`/`PriceVersion` taşıma, gönderim öncesi hash ve sonuç
kaydı öncesi sürüm kontrolü; dış yazma güvenliği katmanlı (bayrak → bağlantı anahtarı → politika → aktif teklif → LIVE listeleme).

### 4.4 İade, soru ve bağlantı yaşam döngüsü

| # | Ö | Bulgu | Yer | Çözüm |
|---|---|---|---|---|
| İ1 | Y | `ShouldPersist(target)` durum makinesinden önce değerlendiriliyor; uzak Cancelled gelince onaylı/tamamlanmış iade dahil kararlar, kanıtlar, stok dispozisyonları ve iade siliniyor; onayda stoğa eklenen miktar geri alınmıyor; idempotency anahtarı yeniden kullanılabilir hâle geliyor. | `MarketplaceJobProcessor.cs:9425-9430`, `MarketplaceSalesService.cs:2041-2069`, `CancelledReturnClaimCleanup.cs:7-16` | Kararı/dispozisyonu/kanıtı olmayan iadeyi sil; diğerlerini `Cancelled` olarak sakla ve sorun kaydı aç. |
| İ2 | Y | HB `IReturnPort.GetAsync` desteklenmediği için belirsiz iade işlemi hep `Unknown`; 30 dk sonra effect `UNRESOLVED` kapanıyor ama karar `MANUAL_REVIEW` kalıyor; hiçbir yol temizlemiyor → sonsuza dek `RETURN_DECISION_IN_PROGRESS`. `PREAPPROVAL_CONFIRM_SUBMITTED` açık talep filtresinde yok. | `HepsiburadaHttpClient.cs:897`, `MarketplaceJobProcessor.EffectReconcile.cs:57-110`, `MarketplaceSalesService.cs:1891-1927`, `MarketplaceJobProcessor.cs:9519,9997` | Unresolved → `decision.Status = FAILED` (`EXTERNAL_EFFECT_UNRESOLVED`); `UpsertReturn` MANUAL_REVIEW kararları da uzlaştırsın; HB açık statü listesine ekle. |
| İ3 | Y | Pazaryeri cevabı REJECTED yapınca `Evaluate` Pending dönüyor, `AnswerSubmissionStatus` SUBMITTED kalıyor → `AnswerAsync` hep 409; kapı zaten REJECTED'a izin vermiyor. | `MarketplaceQuestionService.cs:226-251,459-482`, `MarketplaceQuestionAnswerReconciliationPolicy.cs:16-21` | REJECTED gelince gönderimi REJECTED yap, bekleyen metni sil, REJECTED'a cevaba izin ver. |
| İ4 | O | `CanTransition` false olunca `Status/RawStatus/LastRemoteModifiedAt` yazılmıyor, sorun kaydı yok; onaylanan iade uzakta Rejected olsa bile "Onaylı" listelenip her turda yeniden okunuyor. | `MarketplaceJobProcessor.cs:9471-9479`, `MarketplaceSalesService.cs:2071-2089` | `RawStatus`/`LastRemoteModifiedAt`'i yine güncelle, `RETURN_STATUS_TRANSITION_REFUSED` kaydı, uç uzak statüleri yetkili say. |
| İ5 | O | `SetActive`, `Rotate`, `Update`, `Find` DELETED bağlantıyı dışlamıyor; silinmiş bağlantı DISABLED/DRAFT'a dirilebiliyor; aynı mağaza için yeni bağlantı varsa filtreli tekil indeks 500 veriyor; HIDDEN → `SetActive(true)` doğrudan ACTIVE. | `MarketplaceConnectionService.cs:216,351,402,459,474,649` | Bu sorgulara `Status != DELETED`; HIDDEN için `active=true` reddet. |
| İ6 | O | DISABLED bağlantıyı yeniden açmak eski test kanıtına dayanıyor; ACTIVE bağlantıda kimlik reddi hiçbir yerde `LastErrorCode` yazmıyor; UI hata göstermiyor. | `MarketplaceConnectionService.cs:463-472`, `MarketplaceJobProcessor.cs:1933,1988` | `DisabledAt` sakla, devre dışı bırakmadan yeni başarılı test iste; senkron işlerinden gelen Authentication hatasını bağlantıya yaz. |
| İ7 | O | Ortam/mağaza değişikliği eski ortamın kimliklerini, imleçlerini, soru durumunu ve verisini bırakıyor; STAGE siparişleri üretim listelerine karışıyor. | `MarketplaceConnectionService.cs:288-340` | Değişiklikte kimlikleri iptal et, `SyncCursors`/soru durumunu sil; veri varsa 409 ile `ResetConnectionData` iste. |
| İ8 | O | Cevap geçmişinde satıcı cevabı iki kez görünebiliyor (yerel zaman + pazaryeri zamanı). (doğrulanmadı) | `MarketplaceQuestionService.cs:285-287,438-439,484-492` | Yazar+metin eşleşen girdileri 24 saat penceresinde tekilleştir. |
| İ9 | O | Cevap doğrulaması tek küresel kural (Trendyol 10–2000, diğerleri 1–2000); URL/telefon/e-posta kontrolü yok (Trendyol'un reddetme nedeni → İ3). | `MarketplaceQuestionService.cs:231-233`, adaptörler | Platform başına `QuestionAnswerPolicy` (min/max + yasaklı desenler). |
| İ10 | D | N11/Pazarama iadesinde sipariş okuması başarısızsa Trendyol biçimli iskelet sipariş oluşuyor; hiç doldurulmuyor, iade `N11_RETURN_LINE_UNMATCHED` kalıyor. (doğrulanmadı) | `MarketplaceJobProcessor.cs:9439-9453` | Yedeği `TRENDYOL` ile sınırla; N11/Pazarama'da sorun kaydı + atla. |
| İ11 | D | Kanıt gerektirmeyen ret nedenleri Trendyol id'leriyle (`1651/451/2101`) her platforma uygulanıyor; HB/Pazarama neden listeleri derleme zamanı sabit. | `MarketplaceSalesService.cs:1929-1930`, `PazaramaReturns.cs:78`, `HepsiburadaHttpClient.cs:898` | Adaptörün `EvidenceRequired` bayrağını kullan (1 saat önbellek). |
| İ12 | D | Bağlantı derin silme soruları silmiyor; soru listesi bağlantı statüsüne bakmıyor. | `OperationalDataMaintenanceService.cs:280-308`, `MarketplaceQuestionService.cs:22-25` | İki tabloyu temizliğe ekle; join'e statü filtresi. |
| İ13 | D | Trendyol iade son tarihi yoksa sentezleniyor (`lastModified+48h` / `claimDate+7d`) ve yetkili gibi gösteriliyor; iade tutarı modelde hiç yok. | `TrendyolJsonMapper.cs:639-641` | Sentezlenen tarihi "tahmini" işaretle; ileride iade tutarını modele ekle. |

### 4.5 İş kuyruğu, zamanlayıcı ve veritabanı

| # | Ö | Bulgu | Yer | Çözüm |
|---|---|---|---|---|
| Q1 | Y | HB `forceBaseline = full || !hasSnapshots || retryAfterFailure`; kayıtlı durum `LastError` varken atılıyor; pencereler arası `Continue` yok → 30 pencerelik ay 10 dakikalık zaman aşımına sığmazsa sonsuz döngü, 6 denemede Dead, zamanlayıcı 15 dakikada bir yeniden yaratıyor. Trendyol'da düzeltilmiş (7205-7209), HB'de yok. | `MarketplaceJobProcessor.cs:2064-2086,2131-2133`, `JobTypeRegistry.cs:65` | Trendyol kuralı (1 günden yeni anchor'lı kayıtlı durum devam eder); `retryAfterFailure`'ı çıkar; HB pencereleri arasında `JobExecutionResult.Continue`. |
| Q2 | Y | `OrderInvoiceReconciliation` "order-invoices" kilidinde `package.Version++`, `order.Version++` ve `TRENDYOL_INVOICE_DISCOVERY` imlecini yazıyor; "orders" grubundaki sipariş/webhook/durum işleri aynı satırlara yazıyor → `DbUpdateConcurrencyException` → `Worker.ExceptionResult` `_ => Retry("UNHANDLED_EXECUTION_FAILURE")` → 200 siparişlik sayfa kayıp + deneme hakkı + Trendyol'dan yeniden okuma. | `MarketplaceSyncExecutionLock.cs:90`, `MarketplaceJobProcessor.cs:4062-4089`, `Worker.cs:560-571` | Uzlaştırmayı "orders" grubuna al (`IsCoalescableRead` ertelemeye uygun) ya da `Version++` yapmadan `ExecuteUpdate`; `DbUpdateConcurrencyException` → `Deferred` (hak iadeli, 10–30 sn). |
| Q3 | Y | Saklama yalnız `scheduled:%` işleri kapsıyor; her webhook teslimi ham JSON yüklü `webhook:` işi + `inbox_messages` satırı bırakıyor; `effect-reconcile:`, `manual:`, `stock-projection:` vb. de kalıcı; çözülmüş sorunlar silinmiyor ve anahtarlar olay id/zaman damgası taşıyor; `order_status_history` statü değişmese de her `lastModifiedDate` için satır alıyor. | `JobRetentionService.cs:30-33`, `MarketplaceWebhookService.cs:25-34`, `OperationalIssueRetentionService.cs:254-256`, `MarketplaceJobProcessor.cs:7333,7823` | Biten `webhook:`/`effect-reconcile:`/`manual:` işleri 14–30 gün; `inbox_messages` `ProcessedAt < now−30d`; Resolved sorunları N gün sonra sil; `Status==target && RawStatus==remote.RawStatus` iken geçmiş satırı yazma. |
| Q4 | O | `ExternalEffectSweep` `queued` kümesini iş statüsüne bakmadan kuruyor; Dead/Blocked/Cancelled doğrulama işi effect'i kalıcı dışlıyor; iade kararı askıda kalıyor. | `ExternalEffectSweep.cs:565-575` | `Status IN (Pending, Leased, RetryScheduled)` filtresi; uç durumda effect'i `UNRESOLVED` kapat + sorun ya da yeni dedup ekiyle iş yarat. |
| Q5 | O | `MaximumDeferralAge` (24 sa) `Continue` dahil her hak iadesine uygulanıyor; bir günden eski dilimli işler (büyük içe aktarım, 90 günlük uzlaştırma, geçmiş okuma) deneme tüketip ölüyor. | `JobLeaseService.cs:11,133` | `result.Continues`'u yaş kuralından muaf tut veya yaşı son denemeden ölç. |
| Q6 | O | Trendyol kurtarma tek geçici hatadan sonra 3 aylık tam yeniden okuma yapıyor (`retryAfterFailure` Incremental durumu Baseline'a zorluyor); watermark zaten güvenli. | `MarketplaceJobProcessor.cs:2427,2436,7207-7209` | `retryAfterFailure`'ı `forceBaseline`'dan çıkar. |
| Q7 | O | `IsKnownDefault` kullanıcının bilinçli seçtiği değeri (tarihî bir varsayılana eşitse) her 5 saniyede güncel varsayılana çeviriyor; ilk dal kaynak türüne bakmıyor; "kullanıcı ayarladı" işareti yok. | `ScheduledJobProducer.cs:365-374,421-455`, `MarketplaceConnectionService.cs:624-628` | `ConnectionSyncPolicy.UserDefined` alanı; yalnız false olanları yükselt; varsayılan geçişini başlangıçta ve saatte bir çalıştır. |
| Q8 | O | Zamanlayıcı her 5 saniyede bağlantı × politika sorgusu: bağlantılar iki kez izlenerek yükleniyor, `Invoices` üzerinde DISTINCT, politika başına `latest` sorgusu `CreatedAt` sıralı ve `(TenantId, JobType, JobDedupKey)` indeksiyle 14 günlük iş satırını tarıyor; `OrdersArrivedSinceAsync` için `(TenantId, ConnectionId, CreatedAt)` indeksi yok. | `ScheduledJobProducer.cs:18-21,70-179`, `Worker.cs:24` | `ConnectionSyncPolicy.LastScheduledAt` veya indeks `(TenantId, ConnectionId, JobType, CreatedAt DESC)`; orders'a `(TenantId, ConnectionId, CreatedAt)`; `activeJobTypes` bağlantı başına bir kez; yavaş geçişleri ayrı zamanlayıcıya al. |
| Q9 | O | Kiralama sorgusu `LIMIT 1` öncesi tüm uygun satırları ilişkili alt sorgularla sıralıyor; 13 şerit 1–3 saniyede bir yokluyor; binlerce `PRODUCT_CREATE`/dispatch birikince baskın DB yükü. | `JobLeaseService.cs:25-67`, `Worker.cs:147-181` | Çalışan sayılarını CTE ile önceden topla; ya da iki aşamalı kiralama (ucuz aday + çakışma kontrolü). |
| Q10 | O | `DbUpdateException` (DbException değil) ve 23505/23514 kısıt ihlalleri genel Retry'a düşüyor; 6 deneme sonra Dead, özet istisna türünü içermiyor; `InvalidOperationException → ManualReview` "Sequence contains more than one element" veri hatasını isimsiz yutuyor. | `Worker.cs:560-571` | `DbUpdateConcurrencyException => Deferred`; `PostgresException 23505/23514/22001 => ManualReview(kod, tür+mesaj)`; varsayılan özete tür adı. |
| Q11 | D | Hata yolunda `RecordSyncCompletion` DB'ye yazıyor; hata bağlantıdan kaynaklanıyorsa ikinci hata orijinali maskeliyor. | `MarketplaceJobProcessor.cs:156-161,240` | Telemetri yazımını kendi try/catch'ine al, orijinali yeniden fırlat. |
| Q12 | D | Hot şerit (3) webhook, sipariş okumaları, 20 dakikalık açık paket taraması ve yazma işlerini paylaşıyor; `returns-read` tek slot. | `Worker.cs:57-65`, `JobTypeRegistry.cs:64` | `ORDER_STATUS_SYNC`'i durum şeridine al veya hot 4–5, returns-read 2. |
| Q13 | D | HB yaşam döngüsü sayfalama küçülen kümede `Skip(offset)` → siparişler atlanıyor. | `MarketplaceJobProcessor.cs:2901-2923` | `ExternalOrderId > lastSeen` ile keyset sayfalama. |
| Q14 | D | `TryLeaseAsync` `Version` artırmıyor, outbox olayı yok → panel işin başladığını bitince öğreniyor. | `JobLeaseService.cs:69-78` | Kiralama işleminde `Version++` + outbox olayı. |
| Q15 | D | Reaper geçişi ya hep ya hiç: operatör iptaliyle yarışan satır tüm süresi dolmuş kiralamaların kurtarılmasını iptal ediyor. | `JobLeaseService.cs:243-274` | Tükenmiş ve süresi dolmuş kümeleri ayrı `SaveChanges`; satır başına çakışma yakala. |
| Q16 | D | `INVOICE_DUE_SCAN` ölü iş (`DueAt` hiç atanmıyor) ama her 5 dakikada kiracı başına kuyruğa giriyor; `PayloadJson.Contains(guid)` taraması yapıyor. | `InvoicingJobProcessor.cs:1619,1673-1675`, `ScheduledJobProducer.cs:128-137` | `DueAt`'i doldur ya da taramayı kaldır; LIKE yerine dedup anahtarı. |
| Q17 | D | Gece kapanışı her sabah iki izinli webhook boşluğundan birini tüketiyor (gece siparişleri 10 dakikalık toleransı aşıyor). | `SynchronizationPolicies.cs:606-624`, `MarketplaceJobProcessor.cs:2531-2537` | Kapalı pencereye düşen `OrderedAt`'leri boşluk sayma ya da açılış kurtarmasında boşlukları sıfırla. |
| Q18 | D | İade effect uzlaştırması "orders" kilidinde `ReturnClaim` yazıyor; `RETURN_SYNC` ("returns") ile yarışıyor. | `MarketplaceSyncExecutionLock.cs:94`, `EffectReconcile.cs:235-250` | Kilit grubunu `effect.Action`'dan seç. |
| Q19 | D | DbContext'te yeniden deneme stratejisi yok, komut zaman aşımı 30 sn; 200 siparişlik sayfa tek `SaveChanges`. (doğrulanmadı) | `DependencyInjection.cs:33` | `CommandTimeout` artır (retry stratejisi açık işlemlerle çelişir). |
| Q20 | + | Migration ve EF modeli uyumlu; içe alım idempotency (olay id, `LastModifiedAt >=`, monoton durum makinesi, inbox + dedup) sağlam. | — | — |

### 4.6 Pazaryeri adaptörleri

| # | Ö | Bulgu | Yer | Çözüm |
|---|---|---|---|---|
| A1 | Y | Uzak `Retry-After` hiçbir yerde sınırlanmıyor; HB `X-RateLimit-Reset` göreli saniye olarak okunuyor (epoch ise ~55 yıllık bekleme → iş ve bağlantı o kadar geri çekilir). Trendyol/N11/Pazarama/Shopify de sınırsız geçiriyor. | `HepsiburadaHttpClient.cs:1406-1409,1221,1418`, `JobContracts.cs:46` | `>1e9` değeri epoch say; `FromAdapterError`'da `Math.Min(wait, 15 dk)`. |
| A2 | Y | Shopify `inventorySetQuantities` mutasyonu `changeFromQuantity` alanı ve `@idempotent` direktifiyle gönderiliyor; Admin şemasında alan `compareQuantity`, direktif yok → doğrulama hatası `Validation` → Blocked; yazmalar açılınca her stok gönderimi başarısız olur. (şema doğrulanmalı) | `ShopifyHttpClient.cs:520,946-947` | Geliştirme mağazasında `InventorySetQuantityInput` introspeksiyonu; `compareQuantity`, direktifsiz. |
| A3 | O | HB `Find/Text(..., names)` JSON sırasına göre ilk eşleşen özelliği döndürüyor, ad önceliğine göre değil: `ShippedDate` `DeliveredDate`'den önce listelendiği için teslim satırında sevk zamanı kazanıp "Shipped" "Delivered"ı ezebiliyor; kalem/tahsis anahtarları (`id|lineItemId|orderLineId` vs `orderLineId|lineItemId|orderItemId`) uyuşmazsa paket sessizce düşüyor. (etkisi yük bağımlı) | `HepsiburadaJsonMapper.cs:1680-1686,1074-1078,1441,1476-1481` | `TryFind` adları sırayla arasın. |
| A4 | O | Devre açıkken de kota jetonu tüketiliyor: Trendyol'da `WaitForQuotaAsync` `TryEnterCircuit`'ten önce; HB/N11/Pazarama'da rate-limit işleyicisi devre kesicinin dışında (Pazarama fiyat/stok 12,5 sn'lik slotunu yerel 503'e harcıyor). | `TrendyolResilienceHandler.cs:33,40`, `DependencyInjection.cs:96-114` | Önce devre kontrolü; işleyici sırasını değiştir. |
| A5 | O | HB ve N11'de paylaşımlı limit beklemesi 30 saniyelik istek zaman aşımına sayılıyor → kuyrukta bekleyen istekler gönderilmeden `*_TIMEOUT` (deneme hakkı yanıyor). Trendyol/Pazarama `QuotaAcquiredKey` ile çözmüş. | `HepsiburadaHttpClient.cs:1209-1211`, `N11HttpClient.cs:170-172` | `QuotaAcquiredKey` desenini HB ve N11'e taşı. |
| A6 | O | HTTP durum → hata sınıfı boşlukları: HB 408/409 → Validation (geçici 408 işi kalıcı Blocked); Trendyol/N11/Pazarama 409 → Validation (BusinessConflict olmalı); Shopify 404/408 → Validation. | `HepsiburadaHttpClient.cs:1414-1422`, `TrendyolErrorMapper.cs:26`, `N11HttpClient.cs:215`, `PazaramaHttpClient.cs:349`, `ShopifyHttpClient.cs:1464` | `RequestTimeout => TransientNetwork`, `Conflict => BusinessConflict` kolları. |
| A7 | O | E-Faturam oturum açma hatası gönderim sonrası taşıma hatasıyla aynı kodları veriyor → `InvoicingJobProcessor` "sonuç belirsiz" sayıp ManualReview'a atıyor; oysa POST hiç gitmedi. | `TrendyolEFaturamHttpClient.cs:176-179,265-266`, `InvoicingJobProcessor.cs:277-283` | POST öncesi hatalara ayrı kod (`EFATURAM_SIGNIN_UNAVAILABLE`, yeniden denenebilir) ya da "mali istek gönderildi" bayrağı. |
| A8 | O | E-Faturam her işlemde e-posta/şifre ile yeniden oturum açıyor; token önbelleği yok (Pazarama'da var). | `TrendyolEFaturamHttpClient.cs:176-178` | Kimlik başına `exp − pay` kadar önbellek; 401'de unut. |
| A9 | O | Shopify iade sorgusu (`orders(100){returns(100){returnLineItems(100)}}`) maliyet düşürme döngüsü olmadan gidiyor; 1.000 puan tavanını aşarsa `MAX_COST_EXCEEDED` → Blocked, iadeler hiç senkronlanmaz. (doğrulanmadı) | `ShopifyHttpClient.cs:615-617` vs `542-546` | Sipariş sorgusundaki çeyrek-sayfa döngüsünü uygula; iç `first` 20/50. |
| A10 | O | HB paket yoklaması her paket için `ReadPackageTrackingInfoAsync` (N+1): 10 satırlık sayfada 40'a kadar takip isteği; OMS kotasının baskın tüketicisi. | `HepsiburadaHttpClient.cs:633-638,550-557` | Yalnız uç olmayan statülerde takip oku ya da sipariş bazında topla. |
| A11 | O | Pazarama ürün yazma işlemleri belleğinde (`static ConcurrentDictionary`) tutuluyor; API ve worker ayrı süreç → başka süreçten yoklama `PAZARAMA_OPERATION_FORGOTTEN`. | `PazaramaProductWrites.cs:24,54-67` | Satır/kodları iş yüküne yaz ya da operasyon id'sine kodla (Shopify `EncodeCompletedOperation` gibi). |
| A12 | O | Trendyol kota kaynaklı açık devre 503 döndürüyor → `Remote5xx` → düz Retry (hak yanıyor, bağlantı geri çekilmiyor); ortak devre kesici aynı durumda 429 veriyor. | `TrendyolResilienceHandler.cs:114-119,361-370` | `openedForQuota` hatırla, 429 dön. |
| A13 | O | N11/Pazarama yazma kapısı `context.Operation`'a bakmıyor ve VERIFIED üretim bağlantısını kabul ediyor; diğerleri `IntegrationRuntimePolicy.AllowsExternalWrite` kullanıyor. | `N11HttpClient.cs:33-43`, `PazaramaHttpClient.cs:39-49` | İkisini de ortak politikadan geçir. |
| A14 | D | Shopify `FulfillmentStatus` bilinmeyen/ERROR/FAILURE → `SHIPPED`. | `ShopifyHttpClient.cs:1212-1218` | Açık eşleme, SHIPPED'e düşme yok. |
| A15 | D | Shopify THROTTLED 200 ile geldiğinden devre kesiciye ulaşmıyor; bekleme tüm kovanın dolumuna göre hesaplanıyor. | `ShopifyHttpClient.cs:933,1524-1526` | `(requestedCost − available)/restoreRate`. |
| A16 | D | Trendyol iade yoklaması 8 paralel istek, ortak imleç; tükenmiş kovalar için boş sayfalar yeniden isteniyor; log etiketi kayıt eşitliğiyle karışabiliyor. | `TrendyolHttpClient.cs:838-872` | Kova başına imleç; indeksle etiketle. |
| A17 | D | Trendyol paket okuması stream'i sayfa sınırı olmadan tarıyor (12 gün × 200/sayfa × 5 sn); her sayfa iki kez ayrıştırılıyor. | `TrendyolHttpClient.cs:500-546` | Sayfa tavanı; tek ayrıştırma. |
| A18 | D | Trendyol tarih biçimli `Retry-After`'ı düşürüyor (yalnız `Delta`). | `TrendyolHttpClient.cs:903` | `Date` biçimini de hesapla. |
| A19 | D | Trendyol KDV oranı yoksa `vatBaseAmount` (tutar) oran olarak alınıyor. (doğrulanmadı) | `TrendyolJsonMapper.cs:43` | Yedeği kaldır; eksikse sorun kaydı. |
| A20 | D | Gizli bilgi taşıyan konumsal record'larda `ToString` geçersiz kılınmamış (Trendyol, HB, Shopify, E-Faturam); bugün hiçbir log yazmıyor, gizli risk. | `*AuthenticationHandler.cs` | N11/Pazarama'daki gibi `ToString` override. |
| A21 | D | Yanıt gövdeleri sınırsız okunuyor (`ReadAsStringAsync`), yalnız HB etiket ve E-Faturam belge yolu sınırlı. | Tüm istemciler | `MaxResponseContentBufferSize` veya uzunluk kontrolü. |
| A22 | D | Aynı adaptörde saat dilimi varsayımı farklı: HB sorular `AssumeUniversal`, HB siparişler Türkiye saati; Pazarama `orderDate` Türkiye, kalem geçmişi `AssumeUniversal`. (doğrulanmadı) | `HepsiburadaQuestions.cs:126`, `PazaramaOrderMapper.cs:218-237` | Platform başına tek saat dilimi politikası. |
| A23 | D | `HasInvoiceObservation` iki yerde farklı anlamla kopyalanmış. | `TrendyolHttpClient.cs:575-578`, `TrendyolJsonMapper.cs:251-255` | Tek yardımcı. |

Adaptör sağlık özeti: Trendyol sağlam (A4/A12/409). Hepsiburada çalışıyor ama kırılgan (A1, A3, A5, A6, A10) ve en yüksek
getirili düzeltmeler burada. N11 temiz ve temkinli. Pazarama token/kota yönetimi en iyisi; A11 tek gerçek boşluk. Shopify
okumalar iyi, yazmalar kanıtsız (A2, A9 yazmalar açılmadan doğrulanmalı). E-Faturam mali güvenliği doğru, işletimi zayıf (A7, A8).

### 4.7 API katmanı

| # | Ö | Bulgu | Yer | Çözüm |
|---|---|---|---|---|
| P1 | Y | `/invoice-workspace/confirm` 200 kalem × 45 sn satır içi; her adım `http.RequestAborted` alıyor → sekme kapanması/ağ kopması e-Arşiv kesimini ve pazaryeri teslimini ortasında iptal ediyor, kiralama tamamlanmıyor. Aynı desen `/orders/{id}/instant-process` ve `/connections/{id}/test-jobs`. | `InvoicingEndpoints.cs:105-121`, `InvoiceInlineRunner.cs:9-45`, `MarketplaceEndpoints.cs:20-34,99-154` | `ApplicationStopping`'e bağlı 60 sn sunucu bütçesi; istemci belirteci dış yazmaya ulaşmasın; ilk 5 kalem satır içi, kalanı 202 + iş id. |
| P2 | O | Webhook alıcısı doğrulamadan önce 10 MB gövde tamponluyor ve DB sorguluyor; limit anahtarı URL'deki `connectionPublicId` → rastgele GUID'lerle sınırsız istek. | `MarketplaceEndpoints.cs:334-344`, `Program.cs:78-80` | Önce `PublicId` ile bağlantıyı çöz (404), sonra gövde; IP başına dış limit zinciri (`CreateChained`). |
| P3 | O | MFA ve kurtarma kodu denemelerinde oturum başına başarısızlık sınırı yok; `MfaChallenge` oturumu 12 saat geçerli; yalnız IP başına 10/dk. | `AuthEndpoints.cs:183-216` | `MfaFailedAttempts` say, 5'te oturumu iptal et ya da `AccessFailedAsync` ile hesap kilidine bağla. |
| P4 | O | Kimlik doğrulama denetim izi yok (giriş başarısı/başarısızlığı, kilit, MFA, şifre değişimi, oturum iptali). | `AuthEndpoints.cs` | `AuditLog` kayıtları + yapılandırılmış log (kullanıcı, kaynak IP). |
| P5 | O | Rol matrisi: `orders` ve `invoice-workspace` yazmaları varsayılana (OWNER/ADMIN) düşüyor; OPERATIONS/ACCOUNTING için 403; `shipments/{id}/instant-process` ise OPERATIONS'a açık. (amaç doğrulanmadı) | `RoleAuthorizationMiddleware.cs:104-107` | `"orders"` → Operational, `"invoice-workspace"` → InvoiceRoles; test satırları. |
| P6 | O | Pazaryeri HTTP durumu panelin durumu olarak dönüyor (401/403/429); Caddy her API 401'inde tarayıcı önbelleğini siliyor, istemci CSRF'yi düşürüyor. | `MarketplaceEndpoints.cs:43`, `MarketplaceSalesService.cs` çok yer, `Caddyfile` | Tek eşleyici: 401/403 → 502, 429 → 503, 404 → 404, diğer 4xx → 422; Caddy eşleyicisini `/api/v1/auth/*` ile daralt. |
| P7 | D | Idempotent tekrar kayıplı: `ETag`/`Location` saklanmıyor; `application/problem+json` gövdeleri saklanmıyor (409 boş dönüyor). | `IdempotencyMiddleware.cs:100-109,156` | İçerik türü, ETag, Location sakla; `application/` ile başlayanları kaydet. |
| P8 | D | Bozuk JSON gövdeleri 500 veriyor (`request.Email.Trim()` NRE vb.); doğrulama katmanı yok. | `AuthEndpoints.cs:42-80`, `MarketplaceEndpoints.cs:207`, `InvoicingEndpoints.cs:495` | `AddValidation()` + `[Required]` ya da açık null kontrolü → 400. |
| P9 | D | Hata zarfları dosyadan dosyaya farklı (`Results.Json(new {code,title})`, `Results.Problem`, `Results.Conflict`). | `QuestionEndpoints.cs:42-91`, `AuthEndpoints.cs:43-61` | Her hatayı `ApiProblems.Of` üzerinden ver. |
| P10 | D | Her yetkili istekte 3 DB turu (oturum/kullanıcı/üyelik); her anahtarlı mutasyonda DELETE+UPDATE süpürmesi. | `SessionAuthMiddleware.cs:19-36`, `IdempotencyMiddleware.cs:70-77` | Tek birleşik projeksiyon; süpürmeyi saatlik tick'e taşı. |
| P11 | D | `/health/*` kenardan anonim erişilebilir (503 kuyruk durumu dahil). | `Program.cs:118-130`, `Caddyfile` | Caddy vekilinden çıkar ya da `remote_ip` eşleyici. |
| P12 | D | Ayar PUT'ları son yazan kazanır (`Version` artıyor ama `If-Match` yok). | `SettingsEndpoints.cs:100-154` | `OptionalIfMatch` + 412. |
| P13 | D | `mediaRole` form değeri uzunluk sınırsız; kolon `varchar(32)` → 500. | `CatalogEndpoints.cs:314` | `MediaRole()` yardımcısını kullan. |
| P14 | D | Yan etkili GET (`/orders/product-image`) pazaryeri kotası harcıyor; `SameSite=Lax` ile site dışı gezinme çerezi taşıyor. | `MarketplaceEndpoints.cs:82` | `Sec-Fetch-Site` kontrolü ya da kısa ömürlü imzalı belirteç. |
| P15 | D | Arka plan yoklaması (`/orders/summary` 15 sn, `refetchIntervalInBackground`) 30 dakikalık boşta zaman aşımını hiç tetiklemiyor. | `App.tsx:69`, `SessionAuthMiddleware.cs:41` | Arka plan yenilemesini kapat ya da `X-Background-Poll` isteğinde `LastSeenAt`'i yenileme. |
| P16 | D | `Security:TotpEnabled` ölü ayar; MFA zorunlu kılınamıyor. | `appsettings.json` | Kaldır ya da yükseltilmiş roller için zorunlu kıl. |
| P17 | D | İş listesi varsayılan 2.000, en çok 5.000 satır; web `limit` vermiyor. | `JobContracts.cs:160-162`, `operations/pages.tsx:465` | Varsayılan 200 + imleç sayfalama. |
| P18 | D | Uçtan uca API testi yok (`WebApplicationFactory`); pipeline sırası, çerez bayrakları, tekrar davranışı CI'da doğrulanmıyor. | `tests/` | Tek fixture: giriş → CSRF → mutasyon → tekrar; READONLY/OPERATIONS matrisi. |

İyi yapılanlar: kiracı kapsamı her ulaşılabilir sorguda açık ve eksiksiz; CSRF/çerez/same-origin doğru; webhook belirteçleri
HMAC ile saklanıp sabit zamanlı karşılaştırılıyor; medya vekili SSRF'e karşı sertleştirilmiş; önceki denetimin G1/G3/G5
maddeleri bu ağaçta düzeltilmiş.

### 4.8 Web arayüzü

| # | Ö | Bulgu | Yer | Çözüm |
|---|---|---|---|---|
| W1 | Y | `hubApi` 401'de yalnız CSRF'yi sıfırlıyor; `['me']` yeniden alınmıyor; `useAutoRetry` 401/403/404 dahil her hatayı 10/20/40 sn sonra ve her 60 sn'de yeniden deniyor; SignalR `start()` hatası 3 saniyede bir koşulsuz yeniden deniyor → oturum düşünce bütün sayfalar sonsuza dek "Veriler alınamadı" + döner. | `shared/api/client.ts:57-66`, `shared/hooks/useAutoRetry.ts:41-54`, `app/hooks/useOperationsRealtime.ts:48-58`, `App.tsx:195` | 401'de modül seviyesi `onUnauthorized()` (`['me']`'yi hata/REVOKED yap ya da `/?sessionExpired=1`); `useAutoRetry` 401/403/404/412/422'de dursun; SignalR 401'de yeniden denemesin. |
| W2 | Y | Elle fatura tarihi `datetime-local` alanına `toISOString().slice(0,16)` (UTC) ile dolduruluyor, `new Date(local).toISOString()` ile kaydediliyor → her açıp kaydetmede −3 saat. | `invoicing/pages.tsx:130,717`, aynı desen `marketplace/pages.tsx:338` | Ortak `toLocalInput(d)` yardımcısı. |
| W3 | Y | Backend `inventory` ve `invoices` kaynaklarını yayıyor; hook `['inventory']`/`['invoices']` köklerini geçersiz kılıyor ama hiçbir sorgu bu kökleri kullanmıyor (sorgular `['product']`, `['products']`, `['invoice', id]`, `['invoice-viewer']`) → stok ve fatura ekranları anlık güncellenmiyor; pano serilerinde de ortak kök yok. | `useOperationsRealtime.ts:26-27,44`, `AppDbContext.cs:214-225` | Haritayı gerçek köklere çevir; `questions` ekle; pano serilerine ortak kök; "her anahtarın üreticisi var" testi. |
| W4 | O | "Etiket Durumu" filtresi tüm sipariş geçmişini (100 × 200) istemciye çekiyor; yazdırıldı bilgisi `localStorage`'da (tarayıcı başına); Gönderiler ve İadeler sayfaları da tüm sayfaları çekip istemcide dilimliyor. | `marketplace/pages.tsx:115-127,157-163,1644-1647,1855`, `shipping-label.ts:229-240` | `labelPrintedAt`'i pakette sunucuda tut ve sunucuda filtrele; imleç sayfalama. |
| W5 | O | Sevkiyat ve katalog eylemlerinde çift gönderim: bekleme bayrağı yok, her çağrıda yeni idempotency anahtarı, düğme devre dışı kalmıyor. | `marketplace/pages.tsx:1872-1875,2375`, `catalog/pages.tsx:3927-3953` | `useMutation` + `disabled={isPending}` ya da `instant-picking:${id}:v${version}` gibi kararlı anahtar. |
| W6 | O | `window.confirm`/`window.prompt` hâlâ kullanılıyor; URL `prompt`'u doğrulama yapmıyor (sanitizer `http://` görsele izin veriyor, yayın HTTPS istiyor). | `marketplace/pages.tsx:1178,1872,1873,2869`, `catalog/pages.tsx:1419,1424` | Ortak `ConfirmDialog` (`manualConfirmation` deseni); `^https://` kontrollü URL popover'ı. |
| W7 | O | Hiç `ErrorBoundary` yok; `money()` beklenmeyen para birimi koduyla `RangeError` fırlatınca tüm uygulama beyaz sayfa. | `App.tsx:189`, `marketplace/pages.tsx:304,639,1978` | `Routes` etrafında ve rota başına sınır; `Intl.NumberFormat` try/catch. |
| W8 | O | Her `/me` hatası giriş formunu gösteriyor (deploy sırasında 502 → kullanıcı şifre giriyor); "Oturum kilitli" çıkmaz; `logout()` ağ hatasında asılı kalıyor. | `App.tsx:136,209,213` | Yalnız 401'de Login; diğerlerinde `ErrorBox` + tekrar; kilitli durumda "Giriş ekranına dön"; `finally { location.replace }`. |
| W9 | O | 120+ tarih biçimleme çağrısında `timeZone` yok (tarayıcı dilimi); gün sınırları tutarsız (iadeler +03:00, faturalar ve sorular yerel gece yarısı). | `marketplace/pages.tsx:129`, `invoicing/pages.tsx:390-391`, `QuestionsPage.tsx:157-171` | `shared/format-date.ts` (`Europe/Istanbul`) + tek `istanbulDayBoundary`; 8 kopya `money`, 5 kopya tarih yardımcısını birleştir. |
| W10 | O | İşlem Takibi test edilmiş `job-duration.ts` yerine eski kopyayı kullanıyor; `invoice-completion.ts` yalnız testinden import ediliyor. | `operations/pages.tsx:204-216`, `operations/job-duration.ts`, `invoicing/invoice-completion.ts` | Gerçek modülü kullan; ölü modülü sil ya da bağla. |
| W11 | O | Erişilebilirlik: `--rv-color-subtle` 102 bildirimde 3,4–3,9:1 (AA 4,5 altı); beyaz-mor düğme 4,23:1; 43 `role="dialog"` `DesignSystem.Modal` dışında (odak tuzağı yok); `li/label/tr` tıklama işleyicileri; `role="menu"` odaklanamıyor; 21 metinsiz label; 25 `autoFocus`. | `appearance-settings.ts:36-55`, `design-system.css:410`, çok dosya | Subtle'ı ≥#8a8a99/#5e6a82; dolu düğmede #7c3aed; `useDialogSurface`'ı dışa aç ve özel modalları taşı; gerçek `button`. |
| W12 | O | Stil dosyaları aynı özgüllükte çakışıyor; `.rr-btn` 36 px kuralı 40 px `!important` ile üç dosyada eziliyor (30/32/44 de var); aynı seçiciler iki dosyada; özellik başına iki nesil CSS (`*-responsive` + `*-sade`). | `products-sade.css:272`, `orders-sade.css:25,152,200`, `returns-sade.css:276,283,923,976` | `@layer base, components, pages, responsive`; özellik başına tek dosya; `!important`'ı katman katman kaldır. |
| W13 | D | Ölü kod: 4 hiç import edilmeyen CSS; `{false && …}` arkasında `@ts-ignore` ile eski modal; `Badge` ×3, `ErrorBox` ×2, `ShopifyInvoiceStatusModal` ×2, `useMediaQuery` ×2, `idempotency` ×3, `money` ×8. | `questions/*.css`, `integrations-responsive.css`, `catalog/pages.tsx:3518` | Sil ve paylaşılan kite taşı. |
| W14 | D | `marketplace/pages.tsx` tek lazy parça: Siparişler'i açmak eşleme, entegrasyon ve iadeleri de indiriyor (470 KB JS + 231 KB CSS); yalnız InvoicesPage'in sayfa testi var. | `features/marketplace/pages.tsx` | Rota klasörlerine böl (`orders/`, `returns/`, `integrations/`, `mappings/`); Login/MFA, Modal tuzağı, anlık anahtar haritası için RTL testleri. |
| W15 | D | Ana CSS bütçenin %96'sında; `design-system.css` 12 bin satır her sayfada; 881×860, 221 KB logo PNG 38–48 px'te 6 yerde. | `public/pack/brand/ravencia-symbol-transparent.png` | SVG/WebP (<10 KB); CSS'i parçalara ayır. |
| W16 | D | Varsayılan `retry: 1` 4xx'i de yeniden deniyor. | `main.tsx:34` | `retry: (n, e) => n < 1 && !(e.status < 500)`. |
| W17 | D | Sorular sayfası eski `X-Idempotency-Key` başlığını kullanıyor. | `QuestionsPage.tsx:185,197` | `Idempotency-Key`. |
| W18 | D | `platformInfo[].url` şema kontrolsüz `href` (backend üreticisi bulunamadı). | `marketplace/pages.tsx:717` | `https?:` korumasını istemcide de uygula. |
| W19 | D | Türkçe arayüzde geliştirici jargonu ("Order ingestion sonrasında shipment package…", "provider", "credential'ı"). | `marketplace/pages.tsx:1010,1475,1857`, `invoicing/pages.tsx:615,778-779` | Sade Türkçe. |
| W20 | D | Kaydırma kilidi için 4 ayrı mekanizma ve gezinmede 3 zamanlayıcı + `querySelector('[class*="modal-backdrop"]')` ile temizlik. | `App.tsx:22-67` | Her katmanın kullandığı tek referans sayımlı kilit. |
| W21 | D | 35 `exhaustive-deps` uyarısı; `view-tabs.tsx:82` bağımlılıksız layout effect; ESLint yapılandırması yok. | `shared/view-tabs.tsx:82`, `marketplace/pages.tsx:3629`, `QuestionsPage.tsx:220` | ESLint flat config + `npm run lint` CI'a. |

### 4.9 Altyapı, dağıtım, CI/CD, işletim

| # | Ö | Bulgu | Yer | Çözüm |
|---|---|---|---|---|
| D1 | O | Yedek zamanlayıcısı 03:00 UTC (06:00 TR), sunucunun kapalı olduğu saatte; `Persistent=true` ile açılışta koşuyor ve betik api/caddy konteynerleri hazır değilse hata veriyor. Son 5 günün kaydı: her gün çalıştı ama 4 günde "günün yedeği yeniden kullanıldı" (yedek 00:03–00:07Z'de alınmış set), yalnız 19 Ekim'de yeni set üretti. Yarış teorik ama kurgu kırılgan. | `deploy/systemd/ravencia-backup.timer:5-7`, `deploy/scripts/daily-backup.sh:14-22` | `OnCalendar=*-*-* 20:30:00 UTC` (23:30 TR), `RandomizedDelaySec=10m`; betik konteynerleri 5 dakikaya kadar beklesin; `OnFailure=` alarmı. |
| D2 | Y | Deploy öncesi "doğrulanmış yedek" taze değil: `backup.sh` `BACKUP_FORCE=true` yoksa günün setini yeniden kullanıyor; iki deploy betiği de bu bayrağı vermiyor → 18:00 deploy'unda yedek sabahki set; migration bozarsa 9 saatlik sipariş/fatura/webhook kaybı. | `deploy/backup/backup.sh:46-59`, `fast-deploy.sh:95-113`, `deploy.sh:53-70` | İki `run_verified_backup`'ta `-e BACKUP_FORCE=true`. |
| D3 | O | İmajlar ve ön yüz testleri üretim sunucusunda (2 vCPU) derleniyor; CI Dockerfile'ları hiç derlemiyor; proje eklenince imaj sunucuda, deploy ortasında kırılıyor. | `fast-deploy.sh:64-65`, `Dockerfile:5-16`, `ci.yml` | Validate'te GHCR'a build+push (`build-push-action`, gha cache), deploy'da digest ile çek; en azından `ops` işine `docker build`. |
| D4 | O | Kapalı pencerede gelen push hiç deploy olmuyor (`workflow_run` tetikleyicisi, ssh 20 sn'de düşüyor, 09:00'da tekrar yok) → üretim bir sonraki push'a kadar geride. | `deploy.yml:4-8,50-57` | `schedule: '20 6 * * *'` + son yeşil Validate SHA'sını gönder (`ci-entrypoint` zaten içeriyorsa 0 dönüyor). |
| D5 | O | Konteyner logları her deploy'da siliniyor (`json-file`, konteyner yeniden yaratılıyor); 50 MB bütçe bir kez 20 dakikada dolmuştu. | `compose.yaml:36-38,61-63,121-123`, `fast-deploy.sh:141` | `logging: { driver: journald }` (IAM gelince `awslogs`). |
| D6 | O | Geri alma yolu yok; önceki imajlar başarılı deploy sonrası siliniyor; migration'lar ileri yönlü. | `fast-deploy.sh:173-177` | N−1 `manual-*` etiketini tut; `rollback.sh <rev>` + kısa runbook. |
| D7 | O | DB bağlantı tavanı ve geçici hata tekrarı yok; API + Worker EF havuzu + ayrı `NpgsqlDataSource` + LISTEN, varsayılan 100'er → `max_connections=100`; `Command Timeout` yok. | `DependencyInjection.cs:33,76`, `initialize-deployment.sh:92`, `compose.yaml:44-66` | `Maximum Pool Size=25;Timeout=15;Command Timeout=60`; postgres `-c max_connections=150`. |
| D8 | O | Saklama 72 saat (3 geri dönüş noktası); restore tatbikatı zamanlanmamış; yedek seti `deploy/secrets` olmadan (PFX, şifre, `credential_key.txt`) kimlikleri açamıyor. | `backup.sh:16-18,69`, `restore-drill.sh` | 14 günlük + 4 haftalık set; aylık tatbikat zamanlayıcısı; dış hedefe `age`/`gpg` ile şifreli secrets kopyası ya da belgelenmiş emanet. |
| D9 | O | Alarm yok, dış erişilebilirlik kontrolü yok, `/health/queues` kimse yoklamıyor; worker ölse günlerce panelden başka yerde görünmez. | `Program.cs:118-130`, systemd birimleri | Ücretsiz dış izleyici (`/health/ready`, `/health/queues`, 00:30–09:00 bakım penceresi); `OnFailure=ravencia-alert@%n.service` → Telegram/Slack; Actions hata bildirimi. |
| D10 | D | PostgreSQL ayarsız; `/dev/shm` 64 MB (paralel hash/sort'ta "could not resize shared memory"). | `compose.yaml:44-66` | `shm_size: 256m`; `shared_buffers=256MB`, `effective_cache_size=1GB`, `work_mem=16MB`, `log_min_duration_statement=2000`. |
| D11 | D | Metrikler var ama okunamıyor: `DOTNET_EnableDiagnostics=0`, Meter'ın dışa aktarıcısı yok. | `Dockerfile:40`, `MarketplaceHubTelemetry.cs:6-7` | Yalnız debugger/profiler kapat (IPC açık) ya da OpenTelemetry Prometheus exporter. |
| D12 | D | Actions ana sürüm etiketiyle sabitlenmiş; Dependabot/Renovate yok; taban imajlar digest'li ama sessizce eskiyecek. | `ci.yml`, `.github/` | SHA ile sabitle; `dependabot.yml` (actions, docker ×2, npm, nuget). |
| D13 | D | CI hijyeni: `timeout-minutes` yok; CI postgres etiketle (compose digest'le); `ops` işi gerekli compose sürümünü (2.40.2) doğrulamıyor; ön yüz lint betiği yok. | `ci.yml`, `package.json:10-17` | `timeout-minutes: 30`; ESLint + `npm run lint`; postgres digest. |
| D14 | D | `appsettings.Development.json` (seed şifresi `test123`) üretim imajında; yüklenmiyor ama bir ortam değişkeni hatasıyla açılabilir. | `Dockerfile:38`, `Api/appsettings.Development.json:9` | `CopyToPublishDirectory="Never"`. |
| D15 | D | Eski SHA'nın Deploy'u, daha yeni deploy başarısız olduktan sonra "zaten içeriliyor" diye başarı bildiriyor. | `ci-entrypoint.sh:25-29` | `.last-success` kaydı; yalnız ona eşit/içerenlerde 0. |
| D16 | D | İki ayrışmış deploy yolu (`deploy.sh` registry digest / `fast-deploy.sh` yerel build) farklı kontrollerle. | `deploy/scripts/` | Tek yol ya da paylaşılan fonksiyon kütüphanesi. |
| D17 | D | Sağlıksız API yeniden başlatılmıyor (yalnız healthcheck; Worker'ın watchdog'u var, API'nin yok). | `compose.yaml:81-86` | Autoheal sidecar ya da dış izleyici + runbook. |
| D18 | + | Kenar: TLS/HSTS/CSP/nosniff doğru; webhook limiti bağlantı başına 1200/dk, auth 10/dk/IP; forwarded header yalnız `172.30.10.0/24`'ten. `encode zstd gzip` API JSON'unu da sıkıştırıyor (BREACH sınıfı, teorik). | `Caddyfile.production`, `Program.cs:54-81` | — |
| D19 | D | Depo hijyeni: ana çalışma ağacında 724 MB izlenmeyen `.codex-*`/`.release-*` klasörü ve ekran görüntüleri; `.gitignore` bu desenleri (`.dockerignore`'un aksine) içermiyor; 16 uzak dal, 10 codex worktree. Git geçmişi gizli bilgi açısından temiz. | kök dizin, `.gitignore` | `.codex-*`, `.release-*`, `*.jpg` kökte ignore; eski dal ve worktree'leri temizle. |

Ölçümler: `dotnet list package --vulnerable` 0; Validate = plan → ops (shellcheck, compose config) ‖ backend-format ‖ backend
(postgres servisi, build, test, `migrations has-pending-model-changes`) ‖ frontend (typecheck, vitest, build, bütçe, audit) → gate;
Deploy = SHA çöz → ssh zorunlu komut → `/health/ready`. Şerit slotları 13. Pinler: SDK 10.0.302, aspnet 10.0.10, postgres 18.4,
caddy 2.11.3, node 24.18.1, compose 2.40.2.

---

## 5. Doğrulanmayan maddeler (önce teyit)

- **A2 Shopify stok mutasyonu** ve **A9 iade sorgu maliyeti**: geliştirme mağazasında introspeksiyon ve `requestedQueryCost`.
- **A1 HB `X-RateLimit-Reset`** biçimi (epoch mi göreli mi): HB dokümanı/tarayıcı ile.
- **F7** Trendyol mikro ihracat siparişlerinde `currencyCode` değeri.
- **P5** `orders`/`invoice-workspace` rollerinin bilinçli mi kısıtlandığı.
- **İ3** Trendyol'un REJECTED soruya yeniden cevaba izin verip vermediği.
- **A3** HB yüklerinin aynı anda `shippedDate` + `deliveredDate` ya da iki kalem anahtarı taşıyıp taşımadığı (canlı yük).
- **D1** Yarış koşulu: son 5 günde oluşmadı; yine de saat değişikliği önerilir.

---

## 6. Önerilen uygulama sırası

**Dalga 1 — mali ve veri bütünlüğü (1–3 gün, hepsi küçük commit):**
F1, F2, F4, F11; S1, S2; İ1, İ2, İ3; K1, K2; Q1, Q2, Q10; A1; W2, W3; D2.
Her biri mevcut test altyapısına birer regresyon testiyle; PG etkileyenler önce `codex/pazaryeri-faz0`.

**Dalga 2 — dayanıklılık ve işletim (1–2 hafta):**
P1, P2, P3+P4; Q3, Q4, Q5, Q6, Q7, Q8; A4, A5, A6, A7, A8, A12, A13; S3, S4, S5, S6; K3, K4, K5; İ4–İ7; F3, F5, F6, F8, F9;
W1, W4, W5, W7, W8; D1, D4, D5, D6, D7, D9, D10.

**Dalga 3 — kalite ve bakım (sürekli):**
A2/A9 (yazmalar açılmadan), W6, W9–W21, P6–P18, Q9, Q11–Q19, A10, A11, A14–A23, K6–K11, İ8–İ13, F10, F12, S7–S10, D3, D8, D11–D19;
ardından planlanan yapısal geçiş (işlemcinin handler'lara bölünmesi).

---

## 7. Sizin karar vermeniz gerekenler

1. **E-Fatura mükellefi alıcılar (F1):** TEMELFATURA akışı tamamlanana kadar bu siparişleri paneldeki kesimden hariç tutmak
   (422 ile durdurmak) kabul mü? Aksi hâlde e-Arşiv kesilmeye devam eder.
2. **Düzeltme faturası (F2):** `originalInvoiceId` ucunu geçici kapatmak kabul mü? Gerçek İADE faturası ihtiyacı varsa ayrı iş.
3. **UNPACKED üst paket (S2):** iptal gibi mi sayılsın, yoksa yeni bir "aşıldı" statüsü mü gösterilsin?
4. **Rol matrisi (P5):** OPERATIONS/ACCOUNTING kullanıcıları sipariş işleme ve fatura çalışma alanı yazmaları yapabilmeli mi?
5. **Yedek:** S3 + IAM rolü ya da EBS anlık görüntü planı (önceki karar); D8 için secrets emaneti.
6. **Alarm kanalı (D9):** Telegram mı, Slack mi, e-posta mı?

---

## 8. Önceki raporlardan hâlâ açık olanlar

- 6 Ekim planı: B10 staging p95 hedefi, B11 outbox saklama gözlemi, B12 gerçek bloke işte uçtan uca teyit, B13 şifreli
  off-host yedek + imaj SHA–CI bağı, B16 dar viewport görsel regresyon, B18 Trendyol tarih penceresi sağlayıcı teyidi.
- Teknik düzeltme raporu §7: API idempotency ile dış çağrı arasında exactly-once yok (bu raporda P1/S6 ile örtüşüyor); §15 dış yedek.
- API denetimi: yapısal geçiş (`PlatformCapabilities`, handler'lar, `SyncRunner`, `ExternalWriteGuard`, işlemcinin silinmesi).
- 0.9 Trendyol panel dışı fatura sayacı ~25 Ekim'de okunacak; HB Efsane Kasım kuralları Kasım'da kontrol edilecek.

---

## 9. Uygulama durumu (20 Ekim 2026)

147 bulgunun tamamı ele alındı; sekiz paralel düzeltme kolu tek dalda birleştirildi, PostgreSQL dahil 2.309 backend ve
451 web testi CI'da geçti, iki deploy ile canlıya alındı (`58a64202`, `8c86b6c6`). İki migration: `AuditFixesSchema20261020`
(8 kolon, 2 indeks) ve `OrderLabelPrintedAt`.

**Sahip kararları (19–20 Ekim):** e-Fatura mükellefi alıcılar TEMELFATURA okunabilene kadar 422 ile durur (son 90 günde
etkilenen sipariş 0); düzeltme faturası ucu 422; UNPACKED üst paket iptal sayılır ve sayımlardan çıkar; OPERATIONS sipariş,
ACCOUNTING fatura çalışma alanı yazabilir; faturalar İstanbul saatiyle kesilir (E-Faturam açık ofset kabul etmediği için
İstanbul rakamları + `Z`); alarm için webhook URL dosyası.

**Bilinçli olarak değiştirilmeyenler (doğrulama sonucu):**
- **A2:** Shopify `inventorySetQuantities` özgün hâliyle kaldı; 2026-04'ten beri `@idempotent` zorunlu ve `changeFromQuantity` doğru alan.
- **A13:** N11/Pazarama yazma kapısı korunur; canlı N11 bağlantısı VERIFIED ve 30 günde 397 yazma işini başarıyla çalıştırdı.
- **K6:** Rezervasyon politikası alanları uygulanmaz (kodda not var); uygulanırsa canlıdaki 46 rezervasyon (49 adet) serbest kalıp çift satışa yol açardı.

**Kısmi kalanlar:**
| # | Kalan |
|---|---|
| F5 | Pazaryeri fatura linkinin kaldırılması elle (adaptörde silme ucu yok); gerçek İADE faturası akışı yok |
| S5 | Kargo etiketi yazdırdığı için müşteri e-postası listede kalıyor |
| S6 | API'deki anlık işlem yolunda "orders" danışma kilidi yok (effect kaydı ayrı kaydediliyor, çakışmada bir kez yeniden deneniyor) |
| İ6 | Bağlantı ekranı kimlik hatasını henüz göstermiyor (backend `LastErrorCode` yazıyor) |
| İ11 | HB ve Pazarama iade ret nedenleri adaptörde sabit liste |
| İ13 | İade tutarı modelde yok |
| A16 | Trendyol iade imleci kova başına hazır; işleyici imleci hâlâ sayı olarak saklıyor |
| P17 | İş listesinde imleç sayfalaması yok (varsayılan 200, en çok 1.000) |
| W4 | İadeler sayfası tüm listeyi çekiyor (sekme sayıları için sunucu ucu gerekli) |
| W11–W15, W20 | Erişilebilirlik ve CSS katmanlamasının bir kısmı (sürükle-bırak medya ve etiket tasarımcısı klavye erişimi, 25 dosyalar arası tekrar seçici, `design-system.css` bölünmesi) |

**Yazmalar açılmadan önce canlı doğrulama:** Shopify iade sorgusunun gerçek maliyeti (A9) ve başarısız gönderimin panelde görünüşü (A14).

**Sunucuda bir kez elle yapılacaklar (salt-okunur kuralı nedeniyle sahibe bırakıldı):**
```bash
sudo sh -c 'printf ";Maximum Pool Size=25;Timeout=15;Command Timeout=120" >> deploy/secrets/app_db_connection.txt'
bash deploy/scripts/install-ci-deploy.sh "<deploy key .pub satırı>"
git rev-parse HEAD > ~/deploy-logs/.last-success
```
İsteğe bağlı: `deploy/secrets/alert_webhook_url.txt` (Telegram veya Slack webhook URL'si, 600 izin) ve GitHub `ALERT_WEBHOOK_URL` sırrı.

### 9.1 Kısmi kalanların tamamlanması (20 Ekim 2026, akşam)

| # | Sonuç |
|---|---|
| S5 | Müşteri e-postası listeden kalktı; kargo etiketi bilgiyi sipariş detayından alıyor |
| S6 | Anlık işleme alma ve kargo firması değişimi yerel kayıtta "orders" kilidini (3 sn bekleme) alıyor |
| İ6 | Bağlantı kartı kimlik/erişim/ağ geçidi hatasını son görülme zamanı ve "Test Et" ile gösteriyor |
| İ11 | HB ve Pazarama'da ret nedeni ucu yok; listeler dokümanla aynı, kaynak adresleri kodda |
| İ13 | İade tutarı sipariş satırının ödenen fiyatından hesaplanıp gösteriliyor |
| A16 | Trendyol iade imleci metin olarak saklanıyor; biten durumlar yeniden sorgulanmıyor |
| P17 | İşlem Takibi imleçle sayfalanıyor; platform filtresi liste, sayılar ve kuyruk kartlarında |
| W4 | İadeler sunucuda sayfalanıyor ve süzülüyor (`GET /returns/summary`) |
| F5a | Trendyol fatura linki operatör onayıyla kaldırılabiliyor; HB/N11/Pazarama'da silme ucu yok, ekranda talimat var |
| F5b | İade faturası yapılmadı: E-Faturam belgesinde tüketici iadesinin belge türü belirsiz; sorular `docs/IADE-FATURASI-ARASTIRMA.md` |
| W11, W15, W20 | Klavye erişimi (görsel büyütme, etiket blokları), ana CSS 643 → 365 KB, tek kaydırma kilidi |
| W12, W13 | Kısmi: CSS katman taşıması ve iki Shopify penceresinin birleştirilmesi görünür değişiklik riski nedeniyle yapılmadı |

Görsel karşılaştırma düzeneği: `src/MarketplaceHub.Web/tests/visual` (`npm run test:visual`, `npm run check:cascade`).
