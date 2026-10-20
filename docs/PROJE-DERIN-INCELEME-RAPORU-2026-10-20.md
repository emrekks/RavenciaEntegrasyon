# Proje Derin İnceleme Raporu (20 Ekim 2026)

Bu rapor, 19 Ekim tam taramasından ve onun 147 maddelik düzeltmesinden sonra yapılan ikinci, daha derin incelemedir.
Kapsam: son iki günde yazılan yaklaşık 40 commit'in gerileme incelemesi, önceki raporda bakılmayan alanlar (ürün kataloğu,
eşleştirme, içe aktarım, pano, sorular, kimlik ve oturum) ve her pazaryeri adaptörünün güncel resmi dokümanla birebir
karşılaştırılması. Hiçbir dosya değiştirilmedi, hiçbir pazaryeri API'si çağrılmadı; sunucuda yalnız salt-okunur sorgu çalıştı.

Kaynak: `origin/main` = `289d0b54`. Satır numaraları bu sürüme aittir.

---

## 1. Özet

Sekiz alanda **271 bulgu** çıktı: **35 yüksek**, 116 orta, 120 düşük. Kritik ve yüksek bulguların çoğu kodda ikinci kez
doğrulandı; altısı canlı veriyle ölçüldü (bölüm 2).

En önemli beş sonuç:

1. **Pano ciro sayıları güvenilir değil.** Ciro müşterinin ödediğini değil indirim öncesi fiyatı topluyor (son 30 günde
   468.147 TL gösteriliyor, ödenen 449.148 TL, %4 fazla). Trendyol sipariş saatleri 3 saat ileride saklandığı için
   Trendyol siparişlerinin %23'ü panoda ertesi güne düşüyor.
2. **Kargo etiketi olduğu gibi kullanılamıyor.** Adres bloğu alıcı adını basmıyor; barkod çubukları tarayıcının
   varsayılan yazdırma ayarında boş çıkıyor (arka plan rengiyle çiziliyor).
3. **Stok ve katalog tutarlılığı.** Hızlı stok alanı ve ürün düzenleyici arada satılan ürünün stoğunu eski sayıya geri
   yazabiliyor (çift satış). SKU/barkod/seçenek anahtarları panelde ve pazaryeri içe aktarımında iki farklı kuralla
   normalleştiriliyor; içe aktarım çift varyant açabiliyor ve kullanıcının yüklediği görselleri eziyor.
4. **Pazaryeri sözleşme uyumsuzlukları.** Shopify iade sorgusu 2026-10 şemasında reddediliyor (Shopify iadeleri hiç
   gelmiyor); Trendyol'a giden tüm paket ve fatura yazmaları mikro ihracat paketlerinde bile `TR` mağaza koduyla gidiyor;
   Trendyol iade reddinde kanıt dosyası yanlış alan adıyla gönderiliyor; açık Shopify siparişleri "elle inceleme"ye düşüyor.
5. **Son iki günün düzeltmelerinden doğan gerilemeler.** 36 gerileme bulundu, 7'si yüksek: iptal edilen faturanın teslim
   işi çalışmaya devam edip link kaldırmayı bozabiliyor; sürüm çakışması yaşayan işler 24 saat deneme hakkı yakmadan
   dönüyor; tek bir HTTP yanıtından sonra aynı işlem 24 saat tekrar denenemiyor; fiş onayında yanlış parola oturumu
   kapatıyor.

Güçlü yanlar korunuyor: kiracı izolasyonu ve dosya erişimi tutarlı, CSRF/çerez/oturum doğru, idempotency ve belirsiz dış
yazma kayıtları sağlam, 19 Ekim düzeltmelerinin büyük çoğunluğu (W1, W2, W3, W5, W6, W7, W14, Q1–Q5, Q7–Q10, Q15, Q17,
P3, P6, P7, P10, S1, S3, S4, S6, S7, S9, S10) doğru çalışıyor.

---

## 2. Canlı veriyle doğrulananlar (salt-okunur)

| Bulgu | Ölçüm | Sonuç |
|---|---|---|
| Pano ciroyu brüt topluyor | Son 30 gün: brüt 468.147 TL, net 449.148 TL (494 sipariş) | **Doğrulandı**, %4 fazla gösterim |
| Trendyol saatleri 3 saat ileride | Son 2 günde `CreatedAt − OrderedAt` ortalaması −2,47 saat; 465 siparişin 109'u (%23) 21:00 sonrası | **Doğrulandı** |
| Karışık para birimi | Tüm platformlarda tüm siparişler TRY | Bugün etkisi yok, kod hatası duruyor |
| KDV iki kez eklenmesi | 9.886 teklifin hepsi KDV dahil | Bugün etkisi yok |
| Shopify açık sipariş "elle inceleme" | 11 Shopify siparişinin hepsi teslim edilmiş | Bugün görünmüyor; ilk açık siparişte ortaya çıkar |
| Bölünmüş sipariş yanlış "Teslim Edildi" | Böyle sipariş şu an 0 | Kod hatası doğrulandı, canlıda henüz yok |
| Trendyol açık paket taraması kilidi 20 dk tutar | Son 24 saatte en uzun 49 sn | Bugün risk değil; hacim artarsa olur |
| Webhook alımı yavaş | `TRENDYOL_WEBHOOK_INGEST` p95 32 sn, en uzun 62 sn (normali <1 sn) | **Yeni bulgu**: kilit/kota beklemesi, incelenmeli |
| Ürün görselleri büyük | 79 yüklenmiş görsel, ortalama 1,3–2,4 MB; 9.031 pazaryeri görseli tam boyut | Önizleme önerisi (bölüm 6, Dalga 3) |

---

## 3. Acil liste (önce bunlar)

| # | Alan | Bulgu | Neden acil |
|---|---|---|---|
| U1 | Etiket | Alıcı adı basılmıyor; barkod yazdırmada boş çıkıyor | Her kargo etiketi |
| U2 | Pano | Ciro brüt + Trendyol 3 saat kayması | Yönetim kararları yanlış sayıya dayanıyor |
| U3 | Stok | Hızlı stok, düzenleyici ve toplu düzenleme eski değere göre fark gönderiyor | Satılan ürün stoğa geri dönüyor, çift satış |
| U4 | Trendyol | Paket/fatura yazmaları her zaman `TR` mağaza koduyla | Mikro ihracat paketlerinde işleme alma ve fatura linki başarısız |
| U5 | Shopify | İade sorgusu şemada reddediliyor; açık sipariş durumları eşlenmemiş | Shopify iadeleri hiç gelmiyor; yeni siparişler yanlış sekmede |
| U6 | Fatura | İptal edilen faturanın kuyruktaki teslim işi çalışıyor | Kaldırılan link geri gönderilebilir, iptal "tamamlandı"ya döner |
| U7 | Kuyruk | Sürüm çakışması 24 saat hak yakmadan döngü; 23505 terminal | Kota israfı / webhook verisi kaybı |
| U8 | Web | Tek HTTP yanıtından sonra aynı anahtar 24 saat tekrar denenemiyor; fiş onayında yanlış parola oturumu kapatıyor | Operatör iade onaylayamıyor / formu kaybediyor |
| U9 | Güvenlik | MFA kurulumu/değişimi mevcut faktör istemiyor; eski kurtarma kodları geçerli kalıyor; yöneticide MFA zorunlu değil | Çalınan oturum hesabı ele geçirebilir |
| U10 | Katalog | İçe aktarım kullanıcı görsellerini ve galeri sırasını eziyor; URL ile eklenen görsel HB yayınını hep engelliyor | Veri kaybı / yayın yapılamıyor |

---

## 4. Alan bulguları

Önem: **Y** yüksek, **O** orta, **D** düşük. "(d)" = doğrulanmadı, canlı veya doküman teyidi gerekir.

### 4.1 Son iki günün gerilemeleri (G)

| # | Ö | Bulgu | Yer | Çözüm |
|---|---|---|---|---|
| G1 | Y | İptal edilen faturanın teslim işi çalışıyor; CONFIRMED yazıp iptali "tamamlandı"ya çeviriyor, kaldırılan linki yeniden gönderebiliyor | `InvoicingJobProcessor.cs:704-760,984,1462`, `InvoiceAdjustmentTracking.cs:57-70` | Teslim başında fatura/durum kontrolü; sonuç yazımı `CanTransition`'a uysun; iptal ve link kaldırmada bekleyen teslim işlerini iptal et |
| G2 | Y | `DbUpdateConcurrencyException` her seferinde hak iadeli Deferred → 24 saat 10–30 sn'de bir döngü | `Worker.cs:604-607`, `JobLeaseService.cs:201` | İlk 2–3 çakışmada Deferred, sonra normal Retry (sayaç) |
| G3 | Y (d) | KDV hariç tekliflerde fiyat iki kez brütleşebilir (veri göçü yok) | `TrendyolWriteComposers.cs:155-160,276`, `MarketplaceJobProcessor.cs:~6844` | Bugün EXCLUDED teklif yok; yine de tek seferlik hash yeniden hesaplama |
| G4 | Y | Sürümlü idempotency anahtarı herhangi bir HTTP yanıtından sonra tekrar kullanılıyor → 409 UNKNOWN / replay | `web shared/idempotency.ts:24` | Sürümlü anahtar yalnız yanıtsız ağ hatasında; yanıt geldiyse yeni anahtar |
| G5 | Y | Fiş onayında yanlış parola (401 `REAUTHENTICATION_FAILED`) oturumu kapatıyor | `web shared/api/client.ts:82`, `invoicing/pages.tsx:730` | Yalnız `AUTHENTICATION_REQUIRED`'da çıkış; ya da sunucu 403/422 dönsün |
| G6 | Y | Tek etiket yazdırma sunucuya kaydedilmiyor; satır durumu `labelPrintedAt`'i okumuyor | `OrdersPage.tsx:376,534,784,817,987` | Tek yazdırmada da POST (`afterprint`), `['orders']` yenile, satır durumunda sunucu değeri |
| G7 | Y | 23505 (tekil kısıt) ManualReview'a gidiyor; webhook/yoklama yarışında kaybeden iş durur | `Worker.cs:609-610` | 23505'i 1–2 kez Retry; ManualReview yalnız 23514/22001 |
| G8 | O | İade listesi ve özeti her istekte tüm iadeleri belleğe alıyor | `MarketplaceSalesService.ReturnList.cs:89-148,263-305` | Filtre/sayfalama SQL'e; özet kısa önbellek veya GROUP BY |
| G9 | O | Onaylanan sekmesine ActionRequired tam kartlar sızıyor | `ReturnList.cs:121-123` | Her kartı `ReturnTabGroup(card.Status) == tab` ile süz |
| G10 | O | Trendyol paket taraması 20 sayfada kesilince kalıcı 404 | `TrendyolHttpClient.cs:545` | Yeniden denenebilir `SCAN_INCOMPLETE` hatası veya pencereyi daralt |
| G11 | O | Devre yarı-açık sondası kota beklerken herkesi 503 ile reddediyor | `TrendyolResilienceHandler.cs:34`, `DependencyInjection.cs:96-113` | Sonda hakkını kota alındıktan sonra al |
| G12 | O (d) | Uç statü çakışması terminal paketi ManualReview'a çekiyor | `MarketplaceJobProcessor.cs:7930-7935` | Yerel durum zaten terminalse atla |
| G13 | O | İlk görüldüğünde teslim edilmiş HB paketlerinin kargo firması hiç gelmiyor | `HepsiburadaHttpClient.cs:642` | Takip okumasını yalnız firma/takip no biliniyorsa atla |
| G14 | O | Fatura uzlaştırması "orders" kilidine alındığı için açık paket taramasının arkasında bekliyor | `MarketplaceSyncExecutionLock.cs:~114`, `ScheduledJobProducer.cs:111-123` | Ayrı kilit grubu + `Version++` yapmayan yazım |
| G15 | O | Effect süpürmesi, bağlantı kapalı diye bloklanan doğrulamaları sayıp dakikalar içinde UNRESOLVED kapatıyor; iade kararını kapatmıyor | `ExternalEffectSweep.cs:25-90` | Bu blok kodlarını sayma; kararı işleyicideki gibi kapat |
| G16 | O | Yeniden gönderilen soru cevabı hemen REJECTED işaretleniyor | `MarketplaceQuestionAnswerReconciliationPolicy.cs:22,36` | Ret yalnız bu gönderimden yeniyse |
| G17 | O | Çalışma alanı onayı tüm partiyi 90 sn bütçe altında çalıştırıyor | `InvoicingEndpoints.cs:111-125` | Bütçe yalnız satır içi çalışmada |
| G18 | O | Link kaldırma POST'u sunucu bütçesiz ve yeniden kimlik doğrulamasız | `InvoicingEndpoints.cs:134-142` | `WithServerBudget()` + parola onayı |
| G19 | O | `DueAt` neredeyse hiç dolmuyor → vade taraması çalışmıyor | `InvoicingBillingService.cs:1213` | `ScanDue` içinde paketlerden hesapla |
| G20 | O | Sipariş sayfası ile fatura çalışma alanı sağlayıcı öncesi reddi farklı etiketliyor; AdjustmentRequired filtresi yok | `MarketplaceSalesService.cs:2777,2815,303-318` | Ortak etiket fonksiyonu + DÜZELTME filtresi |
| G21 | O | Rol genişlemesi mali "üstüne yazma" uçlarını OPERATIONS'a açtı; ACCOUNTING etiket kaydında 403 | `RoleAuthorizationMiddleware.cs:79-84` | Mali override uçları Elevated; `label-printed` WorkingRoles |
| G22 | O | İşlem Takibi listesi her sayım yenilemesinde 1. sayfaya dönüyor | `operations/pages.tsx:476-487` | İş tiplerini canlı sayımlardan değil sabit sekme tanımından türet |
| G23–G36 | D | Arka plan yoklama başlığı eksik; sonsuz sorgu tüm sayfaları 5 sn'de bir çekiyor; gönderiler sayfasında çift imleç; tek detay hatası toplu etiketi düşürüyor; CSV dışa aktarımda çift etiket filtresi; `HoldsThisInvoice` koruması çalışmıyor; `+03` ofsetli saatler yanlış; KDV'siz Trendyol satırları düşüyor (d); HB satır kimliği değişebilir (d); Shopify iadeleri sessiz kesiliyor; kapatılamayan iade sorunu; tek bayat politika satırı tüm zamanlayıcı geçişini kaybettiriyor; her kiralama outbox olayı; `orders` indeksi CONCURRENTLY değil | rapor ekindeki ayrıntılar | Bölüm 6 Dalga 2 |

### 4.2 Ürün kataloğu, yayın, içe aktarım (K)

| # | Ö | Bulgu | Yer | Çözüm |
|---|---|---|---|---|
| K1 | Y | SKU/barkod/seçenek anahtarı iki farklı kuralla normalleştiriliyor (`ToUpperInvariant` vs aksan silme + `-`) → içe aktarım çift varyant açıyor; Türkçe İ/ı eşleşmiyor | `CatalogService.cs:2038`, `ImportJobProcessor.cs:304`, `MarketplaceJobProcessor.cs:7252-7262` | Tek `CatalogKeyNormalizer` + kolonları yeniden hesaplayan migration (çakışma raporuyla) |
| K2 | Y | Varyant seçenek imzası iki biçimde (`KEY=VALUE|…` vs `Key: Value | …`) → N11/Pazarama eşleme panel varyantlarını bulamıyor | `CatalogService.cs:2046`, `MarketplaceJobProcessor.cs:7206,7024-7036` | Tek imza oluşturucu; ayrıştırıcı ikisini de kabul etsin; geri doldurma |
| K3 | Y | İçe aktarım kullanıcı görsellerini ve galeri sırasını eziyor | `MarketplaceJobProcessor.cs:6269-6274,6434,7164-7200` | Kullanıcı görseli/sıra/`media` kirli alanı varsa medya upsert'ini atla |
| K4 | Y | URL ile eklenen görsel (`image/remote`) HB yayınını hep `PRODUCT_MEDIA_MIME_INVALID` ile engelliyor | `HepsiburadaProductPublicationComposer.cs:132`, `CatalogEndpoints.cs:381,776` | MIME kontrolü yalnız yüklenen dosyaya |
| K5 | Y | Model kodu ailesini düzenlemek kardeş ürünleri değiştiriyor ama onları içe aktarımdan korumuyor | `CatalogService.cs:1000-1198` | Değişen her ürünün sürümü + kirli işareti; varyant sayısı ürün başına |
| K6 | Y | Başarısız yayın aynı yükle tekrar denenemiyor; güncelleme/arşiv yolunda 500 | `CatalogService.cs:1266-1395,1591` | Terminal işin dedup anahtarını emekliye ayır; `DbUpdateException` yakala |
| K7 | Y | Ürün listesi ve özeti her istekte tüm kataloğu belleğe alıyor | `CatalogService.cs:396-404,478-490,770-772` | `FamilyKey` kolonu + SQL sayfalama/sayım |
| K8 | Y | Arşivleme medyayı yok ediyor; geri açma varyant ve medyayı getirmiyor; toplu durum değişimi yalnız ürünü değiştiriyor | `CatalogService.cs:515-534,1206-1207` | Arşiv yalnız işaret; medya silme saklama işine; toplu durum tekil mantığı kullansın |
| K9 | Y | CSV/XLSX içe aktarım Türkçe dosyalarda kırılıyor: çok satırlı alan, Windows-1254, `;` ayırıcı, `-`/`+` ile başlayan değerler | `ImportFileReader.cs:14-20,93-99` | Kayıt bazlı ayrıştırıcı, ayırıcı tespiti, cp1254 yedeği, formül nötrleştirme |
| K10 | Y | Başarısız içe aktarım "Uygulanıyor"da takılı kalıyor | `ImportJobProcessor.cs:112-118,227-236` | Hata yolunda `ChangeTracker.Clear()`; 500'lük partiler |
| K11 | Y | Aynı görselin paralel yüklenmesi o görseli kalıcı 500'e sokuyor | `CatalogEndpoints.cs:308`, `AppDbContext.cs:401` | `FirstOrDefault` + kısmi tekil indeks |
| K12–K22 | O | Medya düzenlemesi sürüm/kirli işareti artırmıyor; içe aktarım marka/kategori adlarını ve yaprak durumunu eziyor; barkodsuz varyant `""` (tekil indeks) (d); görsel vekili SVG sunuyor (d); onay uzlaştırması barkod başına 5 dk'da bir 7 gün (günde ~28.800 Trendyol isteği / 100 varyant); arşivlenen görseller hiç silinmiyor; model kodu "ailesi" ilgisiz ürünleri birleştiriyor; ürün silme canlı rezervasyonları siliyor; Trendyol'a desi yoksa `1` gidiyor; CSV uygulaması yayınlanamaz ürün üretiyor; ASCII olmayan barkod kabul ediliyor | ayrıntı: ek A | Dalga 2–3 |
| K23–K28 | D | Aramada `%`/`_` joker; eski seçenek değerleri kalıyor; ikinci eşleme 500; N11/Pazarama tohumunda KDV 0 (d); URL görselde özel sıra bozuluyor; Pazarama satış durumu büyük/küçük harf duyarlı | ek A | Dalga 3 |

### 4.3 Sipariş, kargo, etiket (S)

| # | Ö | Bulgu | Yer | Çözüm |
|---|---|---|---|---|
| S1 | Y | Etiket alıcı adını basmıyor (ayar önizlemesi basıyor) | `OrdersPage.tsx:366`, `SystemSettingsPage.tsx:990` | Adres bloğu alanlarını sırayla bas (ad, adres) + test |
| S2 | Y | Barkod çubukları arka plan rengiyle; `print-color-adjust` yok → yazdırmada boş | `design-system.css:397-399,1590-1599` | `print-color-adjust: exact` veya SVG çubuklar |
| S3 | Y | Bir paketi teslim, diğeri yeni olan sipariş "Teslim Edildi" görünüyor | `OrderModels.cs:303-316`, `OrdersPage.tsx:51-58` | Açık iş bekleyen paket varsa en düşük açık durum |
| S4 | Y | Açık Shopify siparişleri (`UNFULFILLED`, `PROCESSING`) ManualReview'a düşüyor | `ShopifyHttpClient.cs:1120,1242`, `OrderModels.cs:300` | Shopify eşlemesi + saklı satır onarımı |
| S5 | Y | Paket satır tahsisleri olay başına toplanıyor → PICKING çift satır, HB ikinci paketi engelliyor | `MarketplaceSalesService.cs:1016-1018,1262,1311-1314` | Paket+satır başına son kayıt; iptal/aşılmış paketleri atla |
| S6 | Y | Belirsiz HB paket oluşturmasından sonra siparişe bir daha paket açılamıyor | `MarketplaceSalesService.cs:1082,1099`, `EffectReconcile.cs:45-97` | Sonuçlanınca rezervasyon anahtarını yeniden adlandır |
| S7 | Y | Yıkıcı Trendyol işlemleri (kalem iptali, elle teslim/iade) onaysız ve serbest JSON ile; her durumda sunuluyor | `ShipmentPages.tsx:101-113`, `MarketplaceSalesService.cs:945-956` | Duruma göre süz, onay penceresi, satır seçici |
| S8–S29 | O | İadeli/incelemedeki siparişte "40 gün gecikti"; vade sıralama etiketleri ters; Shopify faturalanacak tutar indirimi iki kez düşüyor; sekme sayaçları listeyle uyuşmuyor; bazı durumların sekmesi yok; görünen paket iptal paket olabiliyor; sipariş düzeyi "İşleme Al" ilk pakete gidiyor; başarılı işleme almanın tekrarı hata veriyor; kargo değiştirme olmayacak yerlerde sunuluyor ve kısmi sonucu yanlış bildiriyor; gönderiler sayfası eski satırlar ve ters sıra; CSV dışa aktarımda formül enjeksiyonu ve ham kodlar; Shopify elle durum kırılgan; Pazarama tutar/vade iptal kalemleri içeriyor; HB UNPACK hayalet paket (d); HB çok parçalı etikette yalnız ilk parça; Shopify CSV içe aktarım senkronla çakışıyor; A4 etiket ayarları etkisiz; barkod sessiz bölge ve Code128C (d); HB eski zenginleştirme durumu geri alabilir (d); anlık işleme almada sahte "geçiş reddi" (d); sipariş araması büyük/küçük harf duyarlı | ek A | Dalga 2 |
| S30–S40 | D | HB paket penceresi serbest kod alanları; pasif "İşleme Al" yanlış neden; ACCOUNTING etiket kaydı 403; HB yalnız talep siparişleri görünmez; N11 saatleri (d); ikinci N11 bölmesi; kargo takip linkleri; kargo firması alt dize eşleşmesi; erişilebilirlik; toplu etiket kuralları; tek sipariş senkron anahtarları | ek A | Dalga 3 |

### 4.4 Pano, sorular, bildirimler (P)

| # | Ö | Bulgu | Yer | Çözüm |
|---|---|---|---|---|
| P1 | Y | Ciro brüt topluyor (doğrulandı: %4 fazla) | `DashboardReadService.cs:141` | Ortak `DashboardRevenuePolicy` (önce net) |
| P2 | Y | Para birimleri tek TRY toplamına ekleniyor (bugün etkisiz) | `DashboardReadService.cs:145-153`, `DashboardPage.tsx:201-239` | Gün × para birimi gruplama |
| P3 | Y | Trendyol 21:00 sonrası siparişler ertesi güne düşüyor (doğrulandı: %23) | `DashboardReadService.cs:116-118,139,193-205` | Gün kovasında `StoredClockAhead(platform)`; uzun vadede veri göçü (sahip onayı) |
| P4 | Y | Pano yeniden hesaplaması her olay paketinde tüm geçmişi okuyor (~1/sn) | `OperationsRealtimeBroadcaster.cs:54`, `DashboardReadService.cs:196-365` | 30–60 sn birleştirme, yalnız gerekli kolonlar, worker'a taşı, ölü kısımları sil |
| P5 | Y | Her pano yenilemesi 12 ağır istek; her sipariş olayı tümünü geçersiz kılıyor | `DashboardPage.tsx:32,193-196`, `realtime-query-keys.ts` | Tek `/dashboard/performance` ucu; olayları 30 sn birleştir |
| P6–P9 | O-Y | Bir kiracı hata verince tüm kiracılar her saniye yeniden hesaplanıyor; gerçek zamanlı mesaj yeniden hesaplamaya bağlı; belirsiz soru cevabı soruyu kalıcı kilitliyor; reddedilen cevap sayfadan yeniden yazılamıyor | `OperationsRealtimeBroadcaster.cs:54-109`, `MarketplaceQuestionService.cs:227-274`, `QuestionsPage.tsx:209-279` | Kiracı başına try/catch; mesajı önce gönder; `NOT_DELIVERED` durumu; REJECTED cevaplanabilir |
| P10–P26 | O | Soru hataları yeşil başarı bildirimi; üç farklı "bekleyen sipariş" sayısı; iade sayacı HB kuralını uygulamıyor; dönem kıyası yarım günü tam günle kıyaslıyor; düşük stok sayıları farklı; grafik ürün/paket sayıları yanlış; ciro durum kuralı gevşek ve geçmişi değiştiriyor; "Raporu İndir" eski veriyi indirebiliyor; kanallar görünen ada göre; anlık görüntünün büyük kısmı hiç kullanılmıyor; ters tarih aralığı sınırı aşıyor; entegrasyon matrisi bazı hataları gizliyor; kart toplamı ile döküm farklı kaynaktan; soru senkronu hatası "tamamlandı" görünüyor; tüm soru hataları neden kaybolarak Blocked; N11/Pazarama "Yenile" çalışmıyor; Pazarama "onay bekliyor" cevaplandı sayılıyor | ek A | Dalga 2 |
| P27–P29 | D | Bildirim geçmişi tarayıcıdaki herkesle paylaşılıyor; gerçek zamanlı bağlantı oturumu izlemiyor; hiç senkron olmamış mağaza "son güncelleme"de yok sayılıyor | ek A | Dalga 3 |

### 4.5 Arka plan işleri (Q)

| # | Ö | Bulgu | Yer | Çözüm |
|---|---|---|---|---|
| Q1 | Y | Trendyol açık paket taraması "orders" kilidini 20 dk'ya kadar tutabilir (bugün en uzun 49 sn) | `MarketplaceSyncExecutionLock.cs:122`, `MarketplaceJobProcessor.cs:3067-3131` | Kendi kilit grubu veya dilimleme + imleç |
| Q2 | Y | Trendyol `RETURN_SYNC` her hatada tam okumayı baştan başlatıyor | `MarketplaceJobProcessor.cs:8881-8883,9606-9608` | Siparişteki düzeltmenin aynısı + `Continue` |
| Q3 | O-Y | İş kiralama/bitiş olayları ekranlarda gereksiz yenileme fırtınası ve saniyede bir pano hesaplaması | `JobLeaseService.cs:83-85`, `DashboardModels.cs:151-171` | İş olayları "jobs" kaynağıyla; hesaplamayı birleştir; `(TenantId, CreatedAt)` indeksi |
| Q4 | O | Tamamlama yazımı başarısız olursa başarılı iş (dış yazma dahil) yeniden çalışıyor | `Worker.cs:301-308` | `CompleteAsync` geri çekilmeli yeniden deneme |
| Q5 | O | Gece kapanışından sonra ilk belirsiz kontrol işlemi UNRESOLVED kapatıyor | `SynchronizationPolicies.cs:751-757` | Gerçek doğrulama süresi + en az N kontrol |
| Q6 | O | Sürüm çakışması 24 saat döngü (G2 ile aynı kök) | `Worker.cs:607` | Sayaç |
| Q7 | O | Etki koruyucu işler sonsuza dek tutuluyor, çoğu hiçbir şeyi korumuyor | `JobRetentionService.cs:24-56` | Tekrarlanamayan anahtarlı koruyucuları 90 gün sonra sil; kısmi indeks |
| Q8 | O | Kullanıcının beklediği fatura işi ilk aktarımla aynı tek slotu paylaşıyor | `Worker.cs:69`, `JobPriorityPolicy.cs:36` | Elle teslim UserAction; vade taraması Reconciliation |
| Q9 | O | `INITIAL_SYNC_PENDING` hak yakıp Dead oluyor | `MarketplaceJobProcessor.cs:56` | Deferred |
| Q10 | O (d) | Bağlantı havuzu 25 yetmeyebilir (sunucu adımı uygulanınca) | `MarketplaceSyncExecutionLock.cs:74-94` | Worker havuzu ≥ 2×slot+10 (≈45) |
| Q11–Q27 | O-D | Dilim başarısı sağlıklı gösteriliyor; Q11 kısmi; günlük işler sabah 09:00'a kayıyor; aynı dakikadaki işlerin jitter'ı aynı; kiralama sorgusu hâlâ tüm satırları sıralıyor; iade doğrulaması kilit uyuşmazlığı; birleştirilen okuma çalışmadan "başarılı"; durum senkronları hot slot alıyor; sorun saklama aynı geçişte çözüp siliyor; temizlenmeyen satırlar; kuyruk metrikleri gerçeği yansıtmıyor; altyapı hataları ManualReview; `AnnounceNewJobsAsync` iş kaçırıyor; `UserDefined` uç durumları; zamanlayıcı her 5 sn'de gereksiz sorgu; watchdog sağlıklı worker'ı yeniden başlatabilir; ölü kilit dalı | ek A | Dalga 2–3 |
| Q28 | O | **Yeni:** Trendyol webhook alımı p95 32 sn (normali <1 sn) | canlı ölçüm | Kilit/kota beklemesini ölç; webhook alımını kota/kilit dışı tut |

### 4.6 Güvenlik, kimlik, ayarlar (A)

| # | Ö | Bulgu | Yer | Çözüm |
|---|---|---|---|---|
| A1 | Y | MFA kurulum/değişimi parola veya mevcut faktör istemiyor; diğer oturumlar kapanmıyor | `AuthEndpoints.cs:169-213,393,82` | Parola + mevcut TOTP/kurtarma kodu; onayda diğer oturumları kapat |
| A2 | Y | Doğrulayıcı değişince eski kurtarma kodları geçerli kalıyor | `AuthEndpoints.cs:211,228,371` | Onayda eski kodları geçersiz kıl |
| A3 | Y (d) | Yöneticiler için MFA canlıda zorunlu değil | `appsettings.json:4` | `Security__RequireMfaForElevatedRoles=true` (sahip kararı) |
| A4 | Y | Tüm verileri silen işlemler yeniden kimlik doğrulaması istemiyor | `MarketplaceEndpoints.cs:55-58`, `OperationalDataMaintenanceService.cs` | Parola + ikinci faktör; kesilmiş faturaları silmeyi reddet |
| A5 | O | Idempotency deposu webhook rota belirtecini düz metin saklıyor; kiracıdaki tüm kullanıcılar arasında paylaşımlı | `IdempotencyMiddleware.cs:62,141-149` | Gizli yanıt gövdesini saklama; anahtara `UserId` |
| A6 | O | İstek özeti tuzsuz SHA-256 → parola tahmin aracı | `IdempotencyMiddleware.cs:56-59`, `InvoicingEndpoints.cs:628` | HMAC veya gizli alanları özetten çıkar |
| A7 | O | Parola sıfırlama, kullanıcı yönetimi, davet yok; tek sahip hesabı kilitlenerek kalıcı devre dışı bırakılabilir | `AuthEndpoints.cs` | Sahip için kullanıcı yönetimi + CLI parola sıfırlama + kademeli kilit |
| A8–A12 | O | Webhook gövdesi belirteç doğrulanmadan tamponlanıyor (IP başı 6000/dk); parola değiştirme ucunda sınırsız tahmin; zorunlu parola değişimi ikinci faktörden önce; SignalR bağlantısı çıkış/iptalden sonra yaşıyor; veri sıfırlama özel dosyaları diskte bırakıyor (KVKK) | ek A | Dalga 2 |
| A13–A28 | D | Görsel vekili SVG; SSRF filtre boşlukları; dosya yolu önek kontrolü; Data Protection amaçları kiracıya bağlı değil; açık fatura linkleri süresiz/önbelleklenebilir; Caddy logu başlıkları yazıyor (d); istemci correlation id'si güveniliyor; ayarlarda If-Match isteğe bağlı; hız sınırı bölümlemesi zayıf; webhook aboneliği OPERATIONS'a açık; `/public` her yöntemi muaf; kurtarma kodu biçimi; giriş zamanlaması hesap varlığını sızdırıyor; zayıf parola listesi küçük; taslak ürün görselleri açık; idempotency gövdesi hız sınırından önce tamponlanıyor | ek A | Dalga 3 |

### 4.7 Web arayüzü (W)

| # | Ö | Bulgu | Yer | Çözüm |
|---|---|---|---|---|
| W1 | Y | Arka plan yoklamaları 30 dk boşta kalma süresini hiç doldurmuyor | `shared/api/client.ts:28` | Her aralık sorgusu `X-Background-Poll` göndersin |
| W2 | Y | Hızlı stok alanı eski değere göre fark gönderiyor | `ProductsPage.tsx:212-230` | Yalnız kirliyse kaydet; mutlak değer + beklenen mevcut stok |
| W3 | Y | Ürün düzenleyici kaydı arada değişen stoğu geri alıyor | `ProductEditorPages.tsx:2205-2209` | Yalnız düzenlenen satırlar, yüklenen değere göre |
| W4 | Y | Telefonda Faturalar sayfasında toplu seçim yok | `invoicing/pages.tsx:494-543`, `invoices-responsive.css:241,397` | Onay kutularını telefonda göster veya uzun basma |
| W5–W25 | O | Görsel silmek kaydedilmemiş düzenlemeleri siliyor; kaydedilmemiş değişiklik koruması yok, gerçek zamanlı olay formu sıfırlıyor; bildirim/sekme sayısı/etiket ayarı kullanıcılar ve kiracılar arasında sızıyor; toplu kargo değiştirme satır kurallarını yok sayıyor; ayar hataları yeşil; kimlik kaydı kartı bayat bırakıyor (412); bazı onaylarda iade stoğa eklenemiyor; iade onay mesajı sonucu kontrol etmiyor; pano özel aralık yarım seçimde hata; pano tarayıcı saat dilimi; fatura detay penceresi bayat; liste durumu geri/yenilemede kayboluyor; fiyat/stok sıralaması yalnız sayfada; toplu hızlı düzenleme yarıda; sunucu alan hataları gösterilmiyor; eşleme çakışmaları bayat ve tek tıkla siliniyor; değer eşleme kısmi hatada takılıyor; İşlem Takibi tüm sayfaları 5 sn'de bir çekiyor; HB paket formu düzeltmeden sonra gönderilemiyor; N11/Pazarama "Yenile" sessiz; tek etiket sunucuya gitmiyor | ek A | Dalga 2 |
| W26–W32 | D | Sipariş detay pencere verisi yenilenmiyor; aramalar gecikmesiz; seçili soru başka soruya geçiyor; şablon tek tıkla siliniyor; parola/MFA ekranı gerçek hatayı gizliyor; doğrulayıcı kurulumu kusurları; metin/biçim kalıntıları | ek A | Dalga 3 |

Ölçümler: tip denetimi 0 hata, lint 0, 110 dosyada 495 test geçti, build uyarısız; ana CSS 373,8 kB, ana JS 111 kB.

### 4.8 Pazaryeri API sözleşme uyumu (C)

Doküman kaynakları: developers.trendyol.com, shopify.dev (2026-10), developer.n11.com ve WSDL'ler, isortagim.pazarama.com,
developers.trendyolefaturam.com. developers.hepsiburada.com erişime kapalı (403/401); HB için açık kaynaklı bir SDK'daki
portal tanımları (2026-05) kullanıldı.

| # | Ö | Platform | Bulgu | Yer | Çözüm |
|---|---|---|---|---|---|
| C1 | Y | Shopify | İade sorgusu `ReturnLineItemType` arayüzünde olmayan `fulfillmentLineItem` alanını istiyor → sorgu reddediliyor, iadeler hiç gelmiyor | `ShopifyHttpClient.cs:618,655` | `... on ReturnLineItem { fulfillmentLineItem {...} }` |
| C2 | Y | Trendyol | Paket ve fatura linki yazmaları her zaman `storeFrontCode: TR` | `TrendyolHttpClient.cs:630,763,821,1046` | Komutlara saklı paketin mağaza kodu |
| C3 | Y | Trendyol | İade reddinde kanıt dosyası `file` adıyla; doküman `files` | `TrendyolHttpClient.cs:728` | `files` |
| C4 | Y (d) | HB | Stok/fiyat yükleme durumu `Ready/InProgress/Done` eşlenmemiş → her yoklama `CONTRACT_INVALID` | `HepsiburadaJsonMapper.cs:343-349` | Durumları eşle; `errors[]` oku; SIT'te teyit |
| C5 | Y | HB | Fatura linkinde `contentType` `application/pdf` yerine `pdf`; `serialNumber`/`rowNumber` gönderilmiyor | `HepsiburadaInvoiceDeliveryPolicy.cs:68-80` | Doküman biçimi; SIT'te teyit |
| C6 | Y | Shopify | İade durumu OPEN/CLOSED/DECLINED "işlem bekliyor"a düşüyor | `ReturnModels.cs:17-34` | Shopify eşlemesi |
| C7–C25 | O | çeşitli | HB paket listesi `Offset` büyük harf (d); N11 fatura linki yurt içinde hep "belirsiz"; N11 iadelerinde `sender=ALL` yok (n11depom iadeleri gelmiyor); Trendyol stok tavanı 20.000; Trendyol kanıt sınırı 5 MB; Trendyol User-Agent biçimi doğrulanmıyor; Trendyol/N11 tarih parametreleri GMT+3 (d, plan 0.8); kalem iptali gövdesi doğrulanmıyor; `contentId` filtresi belgelenmemiş; **E-Faturam giden e-Fatura durum ucu belgelenmiş** (TEMELFATURA kilidi açılabilir); HB ortak etiket Accept başlığı; HB soru host'unda hız sınırı yok; HB yükleme partisi 4.000 (bilinen sınır 1.000) (d); HB User-Agent Türkçe adla engelleniyor; HB ürün güncelleme Bearer (d); E-Faturam alıcı alan sınırları (d); N11 n11depom ürün/stok; Trendyol fatura linki gövde tipleri; Shopify iade reddi müşteriye bildirilmiyor | ek A | Dalga 1–2 |
| C26–C43 | D | çeşitli | Shopify kullanımdan kalkan alanlar; Trendyol `UNANSWERED` statüsü; belgelenmemiş vitrinler kota harcıyor; doküman çelişkileri (ürün özellik alanları, elle teslim yolu); HB talep durumu harfi; HB soru sayısı alanı; HB soru durumu 7; N11 ret nedeni biçimi; N11 bölme yanıtı kullanılmıyor; N11 cevap uzunluğu; küçük N11 alanları; Pazarama "onaya gönderildi" başarı sayılıyor; E-Faturam `source`; kullanılmayan V2 yükü; HB etiket istemcisi; Shopify tarih tırnakları | ek A | Dalga 3 |

Uyumlu bulunanlar: Trendyol host'ları ve ana sipariş/iade/soru/ürün/webhook uçları; HB sipariş/paket/katalog uçları; N11 REST
ve SOAP servisleri; Pazarama token/sipariş/iade/fiyat-stok; Shopify sipariş/fulfillment/ürün/webhook ve
`inventorySetQuantities` (A2 kararı doğru); E-Faturam oturum/e-Arşiv/iptal/belge.

---

## 5. Sizin kararınız gerekenler

1. **Trendyol saat verisi (P3, plan 0.8):** Saklı Trendyol saatlerini düzelten bir veri göçü mü, yoksa yalnız okuma
   tarafında 3 saat düzeltmesi mi? Göç kalıcı çözüm ama canlı veriyi değiştirir.
2. **Pano ciro tanımı (P1, P16):** "Ciro" net (müşterinin ödediği) mi olsun? İadeler ayrı satırda mı gösterilsin, yoksa
   geçmiş günden mi düşülsün?
3. **Yöneticiler için MFA zorunluluğu (A3):** Açılsın mı? Açılırsa tüm sahip/yönetici hesaplarının doğrulayıcı kurması gerekir.
4. **Kullanıcı yönetimi (A7):** Parola sıfırlama, kullanıcı ekleme/çıkarma ve rol değiştirme ekranı yapılsın mı?
5. **Rol genişlemesi (G21):** OPERATIONS kullanıcıları "iptal edilen faturayı değiştir", "uzakta fatura yok onayı" gibi
   mali üstüne yazma işlemlerini yapabilsin mi? Önerim: hayır, yalnız yöneticiler.
6. **TEMELFATURA (C16):** E-Faturam giden e-Fatura durum ucu belgelenmiş. e-Fatura mükellefi alıcılara gerçek e-Fatura
   kesimi açılsın mı? (Son 90 günde böyle sipariş yok.)
7. **Katalog anahtar birleştirmesi (K1):** Normalleştirme migration'ı mevcut ürünlerde çakışma çıkarabilir; çakışma
   listesini görmeden uygulamayacağım. Onaylıyor musunuz?

---

## 6. Uygulama planı

Her dalga ayrı commit'ler, regresyon testleri, PostgreSQL testleri için codex → main zinciri ve canlı doğrulamayla ilerler.
Paralel kollar dosya sınırına göre bölünür; birleştirmede çakışma, stil kaybı ve PostgreSQL temizlik kontrolleri yapılır.

**Dalga 1 — Acil (1–2 gün):**
U1 etiket (S1, S2), U3 stok (W2, W3, W5 toplu), U4 Trendyol mağaza kodu (C2), U5 Shopify (C1, C6, S4), C3 iade kanıtı,
U6 fatura teslim koruması (G1), U7 kuyruk (G2/Q6 sayaç, G7), U8 web idempotency ve 401 (G4, G5), G6 tek etiket kaydı,
U10 katalog medya (K3, K4), U9 MFA (A1, A2), P1 ciro tanımı (karar 2'ye göre), S3 sipariş toplama, S5 tahsis toplama, S6 HB rezervasyon.

**Dalga 2 — Yüksek ve orta (1 hafta):**
K1/K2 anahtar birleştirme (karar 7 ile), K5–K11, G8–G22, S7–S29, P2–P26, Q1–Q11 ve Q28, A4–A12, W1, W4, W6–W25, C4–C25.
Pano yeniden hesaplamasını birleştirme ve worker'a taşıma (P4, P5, Q3) bu dalgada; sunucu yükünü belirgin düşürür.

**Dalga 3 — Düşük ve iyileştirme (sürekli):**
Kalan D maddeleri; ürün görselleri için WebP önizleme hattı (önizleme ucu, yüklemede küçük sürümler, ön ısıtma, tembel
yükleme); kullanıcı yönetimi (karar 4); Trendyol saat göçü (karar 1).

---

## Ek A — Ayrıntılar

Her bulgunun tam açıklaması, arıza senaryosu, dosya:satır bilgisi ve çözüm ayrıntısı `docs/derin-inceleme-2026-10-20/` klasöründedir:

| Dosya | Alan | Kısaltma |
|---|---|---|
| [1-gerileme-incelemesi.md](derin-inceleme-2026-10-20/1-gerileme-incelemesi.md) | Son iki günün değişiklikleri | G |
| [2-urun-katalogu.md](derin-inceleme-2026-10-20/2-urun-katalogu.md) | Katalog, yayın, içe aktarım | K |
| [3-guvenlik-kimlik.md](derin-inceleme-2026-10-20/3-guvenlik-kimlik.md) | Güvenlik, kimlik, ayarlar | A |
| [4-pano-sorular-bildirimler.md](derin-inceleme-2026-10-20/4-pano-sorular-bildirimler.md) | Pano, sorular, bildirimler | P |
| [5-siparis-kargo-etiket.md](derin-inceleme-2026-10-20/5-siparis-kargo-etiket.md) | Sipariş, kargo, etiket | S |
| [6-arka-plan-isleri.md](derin-inceleme-2026-10-20/6-arka-plan-isleri.md) | Arka plan işleri | Q |
| [7-web-arayuzu.md](derin-inceleme-2026-10-20/7-web-arayuzu.md) | Web arayüzü | W |
| [8-pazaryeri-api-uyumu.md](derin-inceleme-2026-10-20/8-pazaryeri-api-uyumu.md) | Pazaryeri API uyumu | C |
