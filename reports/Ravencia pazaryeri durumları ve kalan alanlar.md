# Pazaryeri kartları yeşil, akışlar sessizce duruyor

İkinci tur denetimin en önemli bulgusu şu: **panel, bir pazaryerinin gerçekten çalışıp çalışmadığını en iyimser katmandan gösteriyor.** Bağlantı sağlığı tek bir alanda tutulmuyor. Bağlantı durumu, akış imleçleri, webhook durumu ve gösterge panelinin iş matrisi diye birbirinden habersiz dört katman var. Entegrasyon kartı yalnız birincisine, onu da süzerek bakıyor. Bu yüzden testi zaman aşımına düşmüş ya da bütün akışları OFFLINE olan bir bağlantı yeşil **"Bağlı ve Aktif"** görünebiliyor (T2-Y1). Gösterge panelinin "Siparişler" hücresi, sürekli hata veren Hepsiburada durum taramasını başka bir sipariş işinin başarısıyla örtüyor (T2-Y2). Bu turda yedi **Yüksek** bulgu çıktı ve hiçbiri Kritik değil. Statü görünürlüğü dışındaki beş Yüksek bulgu para ve mali belgeyle ilgili:

- Tek satırlık kısmi iade, kesilmiş e-Arşivin pazaryerine teslimini kalıcı olarak durduruyor (T2-Y3).
- Teslim öncesi okuma hatası faturayı hiçbir kurtarma yolunun almadığı bir durumda bırakıyor (T2-Y4).
- Trendyol ve Hepsiburada eşleme içe aktarması, model kodu yedeğinde renk ve bedene bakmadan yanlış listinge bağ kuruyor (T2-Y5).
- Raporlar, yalnız Trendyol'a ait Net KDV'yi bütün platformların kârından düşüyor (T2-Y6).
- n11 hakediş işi sonsuz dilim döngüsüne girebiliyor (T2-Y7).

Test paketi üç noktada önceki turun hatalarını "doğru" diye sabitliyor (O1, DO2, D4). Bu testler düzeltmeleri CI'da kırmızıya çevirir. Belgedeki "zorunlu senaryo" testlerinin bir kısmı ise hiçbir davranışı ölçmüyor. Bu turda web testleri çalıştırıldı ve temiz geçti: 81 dosya, 419 test. Backend testleri çalıştırılamadı, canlı sunucuya da dokunulmadı. Bu yüzden hiçbir bulgunun canlıdaki etkisi ölçülmedi.

## Önce kötü haberler: yedi Yüksek bulgu

**1. Panel, çalışmayan bağlantıyı "sağlıklı" gösteriyor.** Entegrasyon kartı yalnız `status ACTIVE|VERIFIED` ve kimlik hatası özetine bakıyor (`IntegrationPages.tsx:139, 211-215`). `connectionAuthProblem` yalnız kimlik/erişim regex'ine uyan kodlarda rozet veriyor (`connection-auth-alert.tsx:9-22`). Testte ya da keşifte yazılan `HEPSIBURADA_TIMEOUT`, `N11_NETWORK_ERROR`, `*_RATE_LIMITED` veya `{PLATFORM}_REQUIRED_READ_CAPABILITY` kodu kartı yeşil bırakıyor. Örneğin bir HB bağlantısının testi zaman aşımına düşüyor ve `IsOperationalStage` false oluyor. Bütün elle yapılan okuma ve yazmalar reddediliyor, kart ise "Bağlı ve Aktif" demeye devam ediyor **[Yüksek][Kesin]**. Bütün akışlar OFFLINE olduğunda da kartta uyarı yok. Akış sağlığı yalnız detay sayfasındaki "Dikkat İsteyenler" bölümünde görünüyor.

**2. Gösterge paneli bir işin hatasını başka bir işin başarısıyla örtüyor.** `DashboardIntegrationMatrix` veri türündeki bütün iş tiplerinin en yeni başarısını alıyor. Bir hatayı ancak `CreatedAt > lastSuccess` ise sayıyor (`DashboardIntegrationMatrix.cs:18, 51-58`). "orders" türünde `OrderSync`, `OrderRecoverySync`, `OrderStatusSync` ve `WebhookIngest` aynı kovada. HB iptal ve paket değişikliklerini yalnız `HEPSIBURADA_ORDER_STATUS_SYNC` okuyor. Bu iş sürekli `RetryScheduled`/`Dead` durumunda kalırken her `ORDER_SYNC` başarısı hücreyi "Güncel" tutuyor. Hatanın zamanı `CreatedAt`, başarınınki `CompletedAt` ile ölçülüyor. Uzun süre yeniden denenen bir iş bu yüzden kalıcı olarak gizleniyor **[Yüksek][Kesin]**. Önceki rapor Dashboard'u `isPlaceholderData` kullanımı yüzünden "doğru uygulama" örneği saymıştı. Bu bulgu o değerlendirmeyi daraltıyor: yükleme durumu doğru gösteriliyor, sağlık hesabı ise yanlış.

**3. Kısmi iade bütün faturanın teslimini durduruyor.** `FlagAdjustmentsAsync`, siparişte Approved ya da Completed tek bir `ReturnClaim` görmesi yeterli. Satıra, adede veya tutara bakmadan Accepted, MarketplacePending veya Completed durumundaki faturayı `AdjustmentRequired` yapıyor (`InvoiceAdjustmentTracking.cs:27-49`). Bu durum teslimi bloke ediyor (`InvoicingContracts.cs:263-265`; `InvoicingJobProcessor.cs:877-879`). Durum makinesinden çıkış yalnız iptal ya da ManualReview yoluyla mümkün (`InvoiceModels.cs:30-46`). Örnek: üç ürünlü bir siparişte bir ürün iade ediliyor. Kalan iki ürünün faturası pazaryerine hiç ulaşmıyor ve panelin önerdiği tek çıkış bütün e-Arşivi iptal etmek **[Yüksek][Kesin]**.

**4. Ön okuma hatası faturayı sahipsiz bırakıyor.** Teslim öncesi pazaryeri okuması başarısız olursa fatura `MarketplacePending` durumuna geçiyor, ama teslim durumu kaydı henüz yazılmıyor (`InvoicingJobProcessor.cs:1137-1149`). Hiçbir yol bu faturayı ele almıyor:

- 5 dakikalık kurtarma bir durum satırı istiyor.
- Hiç kuyruğa alınmamış faturaları toplayan kurtarma yalnız `Accepted` faturaya bakıyor.
- Otomatik yeniden deneme yalnız Accepted ve MarketplaceFailed faturalara bakıyor (`InvoicingJobProcessor.cs:2037-2071, 2170`).
- Elle yollar 409, `INVOICE_DELIVERY_RETRY_NOT_APPLICABLE` ya da "WAIT" döndürüyor.

Kesilmiş e-Arşiv pazaryerine hiç gitmiyor ve listede "işleniyor" görünüyor **[Yüksek][Kesin]**.

**5. Eşleme içe aktarması yanlış varyantı bağlıyor.** `MapExistingCatalogProduct` önce barkod, sonra SKU, en son model kodu adayına bakıyor. Tek aday kalırsa renk ve beden kontrolü yapmadan bağlıyor, çünkü Trendyol ve HB'de `crossMatchCodes=false` (`MarketplaceJobProcessor.cs:7280-7298, 6208`). Örnek: panelde tek varyantlı (Siyah S) bir "M1" ürünü var. Trendyol'daki ilk uzak varyant Siyah L ise panelin S stoğu L listingine gidiyor, L siparişleri de S stoğundan düşüyor. `PRODUCT_MAPPING_UNMATCHED` kaydı yalnız düz katalog pazaryerlerinde yazıldığı için Trendyol ve HB'de bu durum hiç görünmüyor **[Yüksek][Kesin kod / Muhtemel tetiklenme]**.

**6. Raporlardaki Net Kâr, Trendyol dışı satışlarda şişiyor.** `moneyBreakdown` şu hesabı yapıyor: `profit = left - (tax ?? 0) - cost`. Burada `tax` yalnız Trendyol siparişlerinin Net KDV'si, `left` ve `cost` ise bütün platformları kapsıyor (`ReportsPage.tsx:419-421, 577, 600-602, 671`). %20 KDV'li bir üründe Trendyol dışı satışların kârı, satışın yaklaşık %16'sı kadar yüksek çıkıyor. "Devlete ödenecek KDV farkı" açıklaması bu kapsamı belirtmiyor **[Yüksek][Kesin]**. Bu, CLAUDE.md'deki "ekranda yanlış rakam eksik rakamdan kötüdür" kuralını önceki turdaki Y2 ve O26'dan sonra bir kez daha çiğniyor.

**7. n11 hakediş işi aynı on günü sonsuza dek okuyabilir.** `due` listesi, son 7 güne ve 30 gün ileriye kadar olan bütün günleri içeriyor ve yeniden eskiye sıralanıyor. İş dilim başına 10 gün okuyor ve fazlası varsa `Continue` dönüyor (`N11SettlementSyncJobHandler.cs:49-58, 116-117`). Sonraki dilim aynı 10 gelecek günü yeniden seçiyor. `Continue` deneme hakkı tüketmediği için iş yaklaşık 2 saniyede bir tekrarlanıyor ve n11 SOAP kotasını harcıyor. Daha eski günlere hiç sıra gelmiyor. Her turda bağlantının bütün finans ExternalId'leri de belleğe yükleniyor (T2-O11) **[Yüksek][Muhtemel]**. Tetiklenmesi, n11'in bu aralıkta 11'den fazla hakediş günü listelemesine bağlı.

## Pazaryerlerinin çalışma durumu nasıl izleniyor

### Dört katman birbirini beslemiyor

Sistemin "pazaryeri çalışıyor mu?" sorusuna verdiği yanıt dört bağımsız katmandan geliyor.

**(a) Bağlantı düzeyi.** `PlatformConnection.Status` şu yolu izliyor: DRAFT, başarılı testle VERIFIED, etkinleştirmeyle ACTIVE, gerekirse DISABLED ya da DELETED. `LastErrorCode` iki yerde yazılıyor: başarısız test veya keşifte (`MarketplaceJobProcessor.cs:3166-3167`) ve kimlik sınıfı bir hatayla Blocked olan senkronda (`:292-299`). Kodu yalnız başarılı bir test ya da tam sıfırlama temizliyor. Kod kaldığı sürece `RetestFailedOperationalConnectionsAsync` bağlantıyı önce 15 dakika, sonra 1 saat, ardından 6 saatte bir yeniden test ediyor (`ScheduledJobProducer.cs:536-565`; `SynchronizationPolicies.cs:1017-1050`). Dikkat: buradaki `LastSuccessAt` son başarılı **testi** gösteriyor, son senkronu değil.

**(b) Akış düzeyi.** `SyncCursor` her akış için şu alanları tutuyor: `LastAttemptAt`, `LastSuccessAt`, `LastError`, `ConsecutiveFailureCount` ve `LastSliceAt` (`MarketplaceJobProcessor.cs:301-372`). `MarketplaceSyncHealthPolicy.Classify` bunlardan HEALTHY, DELAYED, DEGRADED veya OFFLINE üretiyor. Eşikler beklenen periyoda eklenen 120, 300 ve 900 saniye. Ayrıca imleç 3 periyot ya da 15 dakika ilerlemezse STALLED veriliyor (`MarketplaceConnectionService.cs:595-668`; `SynchronizationPolicies.cs:202-280`). Web bunları "Güncel / Gecikiyor / Kısmi hata / Güncelleme gecikti / İmleç durgun" etiketlerine çeviriyor. **Doğru yanıtı veren tek katman bu.** Hiç çalışmayan bir akış en geç periyot + 15 dakikada OFFLINE oluyor.

**(c) Webhook.** `InstantNotificationService.StatusAsync` dokuz durumdan birini döndürüyor: UNSUPPORTED, OFF, ACTIVE, PASSIVE, MISSING, UNKNOWN, WAITING, QUIET ve CONNECTION_INACTIVE (`InstantNotificationService.cs:41-94`). Zamanlayıcı ise webhook'un sağlıklı olup olmadığına `WebhookPollingPolicy.IsHealthy` ile ayrıca karar veriyor (`ScheduledJobProducer.cs:352-368`). Panel ile zamanlayıcının kuralları farklı.

**(d) Gösterge paneli matrisi.** Son 7 günün `IntegrationJobs` satırlarından veri türü başına ok, warn, bad veya off üretiyor. `SyncCursor`'a hiç bakmıyor (`DashboardIntegrationMatrix.cs`).

Bunlara görünmeyen bir beşinci bileşen ekleniyor: **devre kesici**. Süreç içi bir `ConcurrentDictionary` kullanıyor. 5 ardışık hata devreyi 30 saniye, Retry-After içeren bir 429 ise en çok 15 dakika açıyor (`MarketplaceCircuitBreakerHandler.cs`). Durum kalıcı değil, API ile Worker arasında paylaşılmıyor ve UI'da gösterilmiyor. Etkisi yalnız akış hatası olarak görünüyor. Önceki rapor bu dosyanın ayrıntılı okunmadığını not etmişti. Bu turda okundu: yarı açık sonda doğru alınıyor, ama görünürlük yok.

### Boşluklar: kart ve gösterge paneli akış tablosundan daha iyimser

Katmanlar birbirini beslemediği için kullanıcı en çok baktığı yerde, yani kartta ve gösterge panelinde, en az bilgiyi görüyor. Kart kimlik dışı hata kodlarını yok sayıyor (T2-Y1). Geçici bir okuma hatası ise kalıcı "yetenek yok" hatasına dönüşüyor (T2-O18). N11 keşfinde sipariş sondası 429 ya da zaman aşımı yüzünden başarısız olursa ORDER_READ UNKNOWN yazılıyor. Bu `N11_REQUIRED_READ_CAPABILITY` hatası oluyor ve daha önce SUPPORTED olan satır da UNKNOWN'a düşüyor (`N11HttpClient.cs:149-158`; `MarketplaceJobProcessor.cs:3175-3204`). Gösterge panelinde uzun süre duran bir akış 2 saat sonra "warn" oluyor. 7 gün sonra nötr gri "İşlem Yok" durumuna geçiyor ve kırmızıya hiç dönmüyor (T2-O13).

Webhook katmanı "bilinmeyen bilinmeyen kalır" kuralını üç yerde çiğniyor:

- Trendyol ve Shopify'da `healthy` hesaplanıyor ama yalnız mesajı değiştiriyor. Durum yeşil ACTIVE kalıyor (T2-O14).
- Hiç bildirim gelmemişse `registeredAt` bir işaret sayılıyor ve ekranda "Bildirimler düzenli geliyor" yazıyor (T2-O15). HB aynı durumda doğru biçimde WAITING gösteriyor.
- Reddedilen tek bir teslim, başarılı teslimlerden sonra da 30 gün boyunca kırmızı `role=alert` uyarısı olarak kalıyor (T2-O16). Önceki turdaki D25 bu yolu saldırgan açısından ele almıştı. Bu bulgu meşru akışta da uyarının kapanmadığını ekliyor.

Pasif bir Trendyol veya Shopify bağlantısında abonelik silinmiyor. Gelen teslimler 404 ile reddediliyor, sayfa ise "Açık" gösteriyor (T2-DO4).

Son olarak, arka ucun çalıştırdığı bazı akışlar arayüzde hiç görünmüyor. HB'nin QUESTIONS, FINANCE ve RETURN_LIFECYCLE akışları ile N11 ve Pazarama'nın FINANCE akışı web'deki `supportsSyncPolicy` setlerinde yok (`marketplace-platform-support.ts:15-41, 97-104`; `IntegrationPages.tsx:682`). Bu akışlar OFFLINE olsa bile "Dikkat İsteyenler"e düşmüyor. Özellikle HB'nin açık iade taraması için hiçbir görünür sağlık göstergesi yok (T2-O17). Ters yönde de bir sorun var: akış sağlığı bağlantının DISABLED olduğunu dikkate almıyor. Pasif bağlantının bütün politikaları bir süre sonra "Güncelleme gecikti" olarak listeleniyor (T2-D4).

### Pazaryeri başına yetenek matrisi

Kaynaklar `.claude/skills/marketplace-docs/*.md` tabloları, adaptör dosyaları ve `ScheduledJobProducer.Supports*Policy`. İşaretler: ✓ uygulanmış, ✗ yok veya NotSupported, ~ kısmi. Matris kod ve belgelerden çıkarıldı, her satır uçtan uca çalıştırılmadı.

| Yetenek | Trendyol | Hepsiburada | N11 | Pazarama | PttAVM | Shopify | E-Faturam |
|---|---|---|---|---|---|---|---|
| Sipariş okuma | ✓ stream v2 | ✓ orders + packages | ✓ shipmentPackages | ✓ getOrdersForApi | ✓ SiparisKontrolListesiV2 | ✓ GraphQL orders | — |
| Sipariş durum / kargo yazma | ✓ paket durumu, split, unsupplied, common label | ✓ paket oluştur/çöz, kargo değiştir, etiket | ~ satır onayı, split, işçilik, collection | ✓ updateOrderStatus, split, iptal kararı | ✗ (UI "satıcı panelinden" diyor) | ~ yalnız `fulfillmentTrackingInfoUpdate` | — |
| Stok yazma | ✓ | ✓ inventory-uploads | ✓ price-stock task | ✓ updateStock-v2 | ✓ UpdateProductsStockPrice | ✓ inventorySetQuantities | — |
| Fiyat yazma | ✓ | ✓ | ✓ | ✓ updatePrice-v2 | ✓ | ✓ productVariantsBulkUpdate | — |
| Ürün oluşturma/güncelleme | ✓ v2 products | ✓ import + ticket-api | ✓ create/update task | ✓ product/create (UI "sonraki aşama" diyor) | ✓ UpdateProductsV3 | ✓ productSet | — |
| İade okuma | ✓ claims | ✓ claims | ✓ SOAP ReturnService (detay ✗) | ✓ getRefund (detay ✗) | ✓ salt okunur | ✓ return | — |
| İade karar yazma | ✓ approve + issue | ✓ accept/reject/preapproval | ~ yalnız APPROVE | ✓ updateRefund | ✗ | ✓ approve/decline | — |
| Sorular | ✓ okuma + cevap | ✓ issues (politika UI'da gizli) | ✓ SOAP productService | ✓ | ✗ | ✗ | — |
| Fatura linki yükleme | ✓ seller-invoice-links | ✓ (+ okuma) | ✓ SaveLinkSellerInvoice (okuma ✗) | ✓ invoice-link (okuma ✗) | ✓ SaveInvoince | ✓ | ✓ fatura kesme/iptal |
| Finans / hakediş | ✓ settlements, otherfinancials | ✓ mpfinance (UI'da gizli) | ✓ SettlementService (UI'da gizli) | ✓ paymentAgreement (UI'da gizli) | ✗ | ✗ | — |
| Webhook | ✓ API ile kayıt + yeniden açma | ~ elle tanım | ✗ | ✗ | ✗ | ✓ API, konu başına | — |

Arayüz ile adaptör iki yönde ayrışıyor. Arayüzün desteklenir gösterip adaptörün NotSupported döndürdüğü yerler şunlar: N11 ve PttAVM'de iade ret menüsü (T2-D10) ve Pazarama'da birbiriyle çelişen yazma metinleri (T2-D11). Adaptörün destekleyip arayüzün gizlediği yerler ise HB, N11 ve Pazarama finansı ile HB soru ve açık iade akışları (T2-O17). Keşif yetenekleri de platforma göre değişiyor. Trendyol yazma yeteneklerini hiç keşfetmiyor ve UI Trendyol yeteneklerini yüklemiyor. N11 yalnız CONNECTION_TEST, PRODUCT_READ ve ORDER_READ keşfediyor. Geri okuması olmayan pazaryerlerinde (Pazarama, n11 ve PttAVM'nin API dışı durumları) fatura teslimi belirsiz kaldığında sonsuz iş döngüsü riski var (T2-O7). PttAVM ve Shopify'da soru desteğinin olmadığı, politika listelerinin yokluğundan çıkarıldı [Muhtemel].

## Önceliklendirilmiş bulgu listesi

Önem ve güven etiketleri notlardaki gibi korundu, hiçbiri yükseltilmedi. Notlarda "Orta-Yüksek" olarak geçen iki bulgu Orta tablosunun başında ve kendi etiketiyle yer alıyor. Kimlikler `T2-` önekiyle önceki raporun kimliklerinden ayrılıyor. "İlişki" notları önceki turdaki bulgulara atıf yapıyor.

### Yüksek

| # | Alan | Bulgu | Dosya:satır | Başarısız senaryo | Güven |
|---|---|---|---|---|---|
| T2-Y1 | Durum/UI | Kart, kimlik dışı hata kodunu ve akış sağlığını yok sayıp "Bağlı ve Aktif" diyor | `IntegrationPages.tsx:139, 211-215`; `connection-auth-alert.tsx:9-22`; `MarketplaceJobProcessor.cs:3166-3204`; `IntegrationRuntimePolicy.cs:17-20` | HB testi zaman aşımına düşer, elle yapılan okuma/yazmalar reddedilir, kart yeşil kalır; bütün akışlar OFFLINE iken de uyarı yok | [Kesin] |
| T2-Y2 | Durum/Dashboard | Aynı türdeki farklı işler birbirinin hatasını örtüyor | `DashboardIntegrationMatrix.cs:18, 51-58`; `SynchronizationPolicies.cs:892-900` | `HEPSIBURADA_ORDER_STATUS_SYNC` sürekli Dead/Retry iken 10:01'deki `ORDER_SYNC` başarısı hücreyi "Güncel" tutar; `CreatedAt` sabit kaldığı için gizlenme kalıcı | [Kesin] |
| T2-Y3 | Faturalama/İade | Tek satırlık kısmi iade bütün faturayı `AdjustmentRequired` yapıp teslimi kalıcı kesiyor | `InvoiceAdjustmentTracking.cs:27-49`; `InvoicingContracts.cs:263-265`; `InvoicingJobProcessor.cs:877-879`; `InvoiceModels.cs:30-46` | 3 ürünlü siparişte 1 ürün iade edilir, kalan 2 ürünün faturası pazaryerine gitmez; tek çıkış bütün e-Arşivi iptal etmek | [Kesin] |
| T2-Y4 | Faturalama | Ön okuma hatası faturayı durum kaydı olmadan `MarketplacePending`'de bırakıyor; otomatik ya da elle kurtarma yok | `InvoicingJobProcessor.cs:1137-1149, 2037-2071, 2170`; `InvoiceMarketplaceRetryPolicy.cs:17-18`; `InvoicingWorkspaceWorkflow.cs:458-465` | Trendyol paket okumasında geçici 5xx yaşanır ve deneme hakkı biter; e-Arşiv pazaryerine hiç gitmez, listede "işleniyor" görünür | [Kesin] |
| T2-Y5 | Katalog | Model kodu yedeği tek adayda renk/bedene bakmadan bağlıyor (Trendyol/HB) | `MarketplaceJobProcessor.cs:7280-7298, 6208` | Panelde Siyah S, uzakta S/M/L var; ilk gelen L, S'ye bağlanır, stok ve siparişler yanlış listinge akar; kayıt da yazılmaz | [Kesin kod / Muhtemel tetiklenme] |
| T2-Y6 | Frontend/Rapor | Net KDV yalnız Trendyol'a ait, ama bütün platformların kârından düşülüyor | `ReportsPage.tsx:419-421, 577, 600-602, 671`; `ReportsContracts.cs` (`ReportNetVatView`) | Trendyol + HB döneminde HB satışlarının KDV'si düşülmez; Net Kâr, marj ve adet başına kâr şişer | [Kesin] |
| T2-Y7 | Senkron/Finans | n11 hakediş işi aynı 10 gelecek günü sonsuz `Continue` ile okuyor | `N11SettlementSyncJobHandler.cs:49-58, 116-117`; `JobContracts.cs:31-35`; `N11Settlements.cs:8-10` | [bugün−7, bugün+30] aralığında 11'den fazla gün listelenir; iş ~2 sn'de bir döner, SOAP kotası tükenir, eski günler hiç okunmaz | [Muhtemel] |

### Orta

| # | Alan | Bulgu | Dosya:satır | Başarısız senaryo | Güven |
|---|---|---|---|---|---|
| T2-O1 | Frontend/İade | **[Orta-Yüksek]** Toplu onay, HB taleplerini kullanıcıya sormadan "Refund" yapıyor | `ReturnBulkDecisionDialog.tsx:125, 176-186`; `return-bulk-approve.ts`; `ReturnPages.tsx:305, 313` | Değişim istenen HB talebi toplu onaylanır, pazaryerine geri alınamaz para iadesi kararı gider | [Kesin] |
| T2-O2 | Senkron/Finans | **[Orta-Yüksek]** Pazarama finansında açlık: hareketsiz yeni siparişler her turda ilk 100'ü dolduruyor | `PazaramaFinanceSyncJobHandler.cs:34-45` | 60 günde 100'den fazla hareketsiz sipariş varsa eski siparişlerin hareketleri ve sonradan dolan `TransferredDate`/`Allowance` hiç okunmaz; ödemeler eksik görünür | [Muhtemel] |
| T2-O3 | Faturalama | Taslak kapıları (iptal satır, Returned/Undelivered/PaymentPending paket) `Submit` anında yeniden uygulanmıyor | `InvoicingBillingService.cs:1086-1112, 1229, 1395-1396, 1977-1988`; `InvoicingJobProcessor.cs:136, 172-176, 271`; `InvoicingWorkspaceWorkflow.cs:113-139` | Toplu onayda kuyruğa alınan iş rate-limit yüzünden bekler; bu arada 1 ürün iptal edilir; e-Arşiv tam tutarla kesilir | [Kesin kod / Muhtemel tetiklenme] |
| T2-O4 | İade | Eşzamanlı iki karar (APPROVE + REJECT) ikisi birden pazaryerine gidiyor; sürüm kontrolü dış çağrıdan sonra | `MarketplaceSalesService.cs:2438-2552`; `MarketplaceModelConfiguration.cs:165` | İki sekme aynı anda karar verir; ikinci istek 500 ile düşer, kararı PENDING kalır ve yeni kararları bloke eder | [Kesin kod / Muhtemel tetiklenme] |
| T2-O5 | Faturalama | `MarketplaceFailed`/`ManualReview` durumundaki kesilmiş fatura iptal/iadede işaretlenmiyor ve panelden iptal edilemiyor | `InvoiceAdjustmentTracking.cs:32, 41`; `InvoiceCancellationOutcomePolicy.cs:28-30`; `InvoicingJobProcessor.cs:1240-1248` | `PLATFORM_INVOICE_ALREADY_PRESENT` ile ManualReview'a düşen ikinci mali belgenin panelde "İptal" eylemi yok | [Kesin] |
| T2-O6 | Faturalama | Teslim hedefleri yerine geçilmiş paketleri (Trendyol "0" yer tutucu) kapsıyor | `InvoicingJobProcessor.cs:585-587, 1747-1749, 1808-1812, 2078-2080, 2103-2107`; `InvoicingBillingService.cs:1499-1503`; `OrderModels.cs:436-441` | Önce "0" için okuma yapılır, hata alınır ve döngü kırılır; gerçek pakete link gitmez, fatura Completed olmaz | [Muhtemel] |
| T2-O7 | Faturalama | Geri okuması olmayan pazaryerinde UNKNOWN teslim sonsuz iş döngüsüne giriyor | `InvoicingJobProcessor.cs:1059-1117, 2037-2053, 2119-2154`; `InvoicingContracts.cs:109-119, 138-140, 223-230`; `PazaramaInvoices.cs:38-41` | Pazarama'da 5xx sonrası durum UNKNOWN kalır; her 5 dakikalık kovada yeni iş açılır, fatura hiç ManualReview'a düşmez | [Muhtemel] |
| T2-O8 | Senkron/Sipariş | Bütünlük denetiminde reddedilen sipariş watermark'ın gerisinde kalıyor; çalışma "başarılı" | `MarketplaceJobProcessor.cs:9448, 9553, 9571, 3735-3790` | Trendyol paketi geçici olarak eksik satırla gelir, reddedilir, watermark ilerler; sipariş değişmezse bir daha okunmaz. İlişki: O12'den farklı, burada siparişin tamamı atlanıyor | [Kesin] |
| T2-O9 | Senkron/Sipariş | HB sayfa içi birleştirme satır durumunu alfabetik seçiyor, indirimi kopyalar üzerinden topluyor | `MarketplaceJobProcessor.cs:9484-9509`; `HepsiburadaJsonMapper.cs:1160-1172` | Aynı satır "Unpacked" ve "Delivered" olarak iki kez gelir, eski durum kazanır; indirim iki katına çıkar (brüt tutarla sınırlı) | [Kesin kod / Muhtemel tetiklenme] |
| T2-O10 | Senkron/İade | İade satırları tazelik kontrolü olmadan eski görüntüyle eziliyor | `MarketplaceJobProcessor.cs:12315, 12387-12393` | Eski kova kopyası "WaitingInAction" yazar, onaylı kararın kapanmasını geciktirir; stok etkisi [Tahmin]. İlişki: D14'ün işlemci tarafındaki sonucu | [Kesin kod / Muhtemel etki] |
| T2-O11 | Senkron/Finans | n11 hakediş işi her turda bağlantının bütün finans ExternalId'lerini belleğe alıyor | `N11SettlementSyncJobHandler.cs:49-53` | Satır sayısı süresiz büyür; T2-Y7 döngüsünde ~2 sn'de bir tekrarlanır | [Kesin] |
| T2-O12 | Senkron/Performans | `UpsertReturn` claim başına birkaç sorgu ve bir commit yapıyor (N+1) | `MarketplaceJobProcessor.cs:12268, 12301, 12377, 12387, 12399, 11916` | 200'lük iade sayfası ~1.000+ sorgu ve 200 commit; `returns` kilidi uzun süre tutulur | [Kesin] |
| T2-O13 | Durum/Dashboard | Uzun süre duran akış "İşlem Yok" (gri) görünüyor, kırmızıya geçmiyor | `DashboardIntegrationMatrix.cs:11, 56`; `DashboardPage.tsx:39, 68` | Zamanlayıcı iş üretmeyi bırakır; hücre 2 saat sonra warn, 7 gün sonra nötr gri olur | [Kesin] |
| T2-O14 | Durum/Webhook | Trendyol/Shopify webhook'u sağlıksızken ACTIVE (yeşil) kalıyor | `InstantNotificationService.cs:77-93`; `instant-notification-status.ts:17-27`; `integration-health.ts:12-14` | Sipariş gelir ama bildirim gelmez; zamanlayıcı yedek okumaya geçer, dashboard yeşil kalır. HB aynı durumda QUIET gösteriyor | [Kesin] |
| T2-O15 | Durum/Webhook | Hiç bildirim gelmemişken "Bildirimler düzenli geliyor" yazıyor | `InstantNotificationService.cs:91` | Trendyol/Shopify'da `lastReceivedAt` null iken `registeredAt` işaret sayılır; "unknown stays unknown" kuralına aykırı | [Kesin] |
| T2-O16 | Durum/Webhook | Reddedilen webhook uyarısı başarılı teslimlerden sonra kapanmıyor | `MarketplaceWebhookService.cs:101-122`; `Worker.cs:607-619`; `IntegrationPages.tsx:410-411` | Şifre değişimi sırasında gelen tek bir red 30 gün boyunca kırmızı `role=alert` gösterir. İlişki: D25'in meşru akıştaki karşılığı | [Kesin] |
| T2-O17 | Durum/UI | HB QUESTIONS/FINANCE/RETURN_LIFECYCLE ve N11/Pazarama FINANCE akışları UI'da gizli | `ScheduledJobProducer.cs:633-635, 834-852`; `marketplace-platform-support.ts:15-41, 97-104`; `IntegrationPages.tsx:680-682` | Akış OFFLINE olsa da "Dikkat İsteyenler"e düşmez; HB açık iade taramasının görünür sağlık göstergesi yok | [Kesin] |
| T2-O18 | Durum/Yetenek | Geçici okuma hatası kalıcı "yetenek yok" hatasına dönüşüyor | `N11HttpClient.cs:149-158`; `MarketplaceJobProcessor.cs:3175-3204` | N11 sipariş sondası 429 alır, ORDER_READ UNKNOWN olur, `N11_REQUIRED_READ_CAPABILITY` yazılır; kart bunu göstermez | [Kesin] |
| T2-O19 | Katalog | Kod eşleştirmesi ayraçları ve büyük/küçük harfi siliyor; farklı kodlar çakışıyor | `CatalogKeyNormalizer.cs:36-52`; `MarketplaceJobProcessor.cs:7271-7298` | "ELB/01" ile "ELB-01" ya da "ÇANTA1" ile "CANTA1" aynı sayılır; katlanmış bir barkod eşleşmesi kesin SKU eşleşmesini geçer | [Kesin kod / Muhtemel veri] |
| T2-O20 | Katalog | Trendyol yayınında seçenek değeri `Trim().ToUpperInvariant()` ile eşleniyor | `ProductPublicationComposer.cs:253, 260, 263-283, 460` | "Kırmızı" ≠ "KIRMIZI"; değer serbest metin gider ya da `OPTION_VALUE_MAPPING_REQUIRED`/`REQUIRED_ATTRIBUTE_MISSING` hatası çıkar | [Kesin] |
| T2-O21 | Katalog | Trendyol'da bütün barkodlar ilk kontrolde NOT_FOUND dönerse yayın sıfırlanıyor | `MarketplaceJobProcessor.cs:905-908`; `ProductApprovalReconciliationPolicy.cs:7-18` | İndeksleme gecikmesinde yayın sıfırlanır, kullanıcı yeniden gönderir, çift içerik oluşur | [Tahmin] |
| T2-O22 | Katalog/PTT | Gönderilmeyen varyantlar yeni baz fiyata göre kayıyor | `TrendyolWriteComposers.cs:556-562, 619-690` | En ucuz varyant stok kaydı olmadığı için kümeden düşer, baz fiyat yükselir, diğer varyantların PTT fiyatı panelden ayrılır | [Muhtemel] |
| T2-O23 | Katalog/PTT | Varyant fiyatı hakkında iki varsayım çelişiyor (açmada tek fiyat, gönderimde fark) | `PttAvmProductPublicationComposer.cs:253-254`; `TrendyolWriteComposers.cs:674-680`; `PttAvmPriceInventory.cs:78-88` | Açıldıktan sonra fiyatları ayrışan ailede fark alanı ya yok sayılıyor ya da açma kuralı gereksiz; hangisinin doğru olduğu belgelenmemiş | [Muhtemel] |
| T2-O24 | Frontend/Rapor | Masraf Kalemleri halkasının toplamı listelenen KDV satırını içermiyor | `ReportsPage.tsx:562-564, 600-605, 616-617, 622, 627, 640` | Ortadaki "Toplam Masraf" KDV'siz, dilimler KDV'li; payların toplamı %100'ü aşar | [Kesin] |
| T2-O25 | Frontend/Rapor | Maliyet girilmemişse Kâr Köprüsü KDV'yi düşüp sonra geri ekliyor | `ReportsPage.tsx:671, 675-685` | Basamaklar `left − tax` noktasında biter, sonuç çubuğu `left` gösterir; fark tam Net KDV kadar | [Kesin] |
| T2-O26 | Frontend/Rapor | Adet başına fiyat ve kâr, tutarı bilinmeyen siparişlerin adetleriyle bölünüyor | `ReportsPage.tsx:802-803, 907, 1249`; `ReportBuilder.cs:471-485` | Shopify'da veya başka para biriminde tutarsız sipariş varken ortalama satış fiyatı ve adet başına kâr düşük çıkar | [Kesin] |
| T2-O27 | Realtime | Uzun kopmadan sonra elle yeniden bağlanma ekranları yenilemiyor | `useOperationsRealtime.ts:23, 40-62`; `OperationsRealtimeBroadcaster.cs:172-179`; `OperationsRealtimeOutboxRetention.cs:7-17` | Deploy'da API 30 sn kapanır, ~14 sn'lik otomatik deneme biter, `onclose → start()` invalidation yapmaz; açık ekranlar eski veriyi gösterir | [Kesin] |
| T2-O28 | Realtime | İptal edilen iade talebinin silinmesi realtime olayı üretmiyor | `AppDbContext.cs:220-224`; `CancelledReturnClaimCleanup.cs:17-20, 53-63`; `MarketplaceJobProcessor.cs:12445-12453` | `ExecuteDeleteAsync` ile silinen talep açık İadeler ekranında ve dashboard rozetlerinde görünmeye devam eder | [Kesin] |
| T2-O29 | Test | "Zorunlu senaryo" testleri ölçtüğünü söylediği davranışı ölçmüyor | `tests/MarketplaceHub.Application.Tests/MandatorySynchronizationScenariosTests.cs:19-41` | Scenario04 iki sabit metnin farklı olduğunu, Scenario05 tek fonksiyon çağrılarını sınıyor; politika bozulsa da testler geçer | [Kesin] |
| T2-O30 | Test | KDV oranı testi O1'deki hatayı "doğru" diye sabitliyor | `tests/.../OrderLineVatRateTests.cs:93-98`; `docs/RAVENCIA-SISTEM-ISLEYIS-BELGESI.md:504` | `("HEPSIBURADA", null)`, `("N11","{}")` ve `("PAZARAMA","not json")` için "biliniyor" bekleniyor; O1'in düzeltmesi bu üç satırı kırar | [Kesin] |
| T2-O31 | Test | Realtime istemcisinin tam kopma sonrası yenilemesi test edilmiyor | `useOperationsRealtime.test.tsx:33-67` | T2-O27 bu yüzden yakalanmadı | [Kesin] |
| T2-O32 | Belge | Senkron sağlık eşikleri belgede iki katı yazılmış | `docs/TRENDYOL-SYNC-ARCHITECTURE.md:96-98`; `MarketplaceConnectionService.cs:618-620`; `Api/appsettings.json:7`; `Worker/appsettings.json:21` | Belge 600/1800 sn diyor, kod 300/900 sn kullanıyor; alarmı belgeye göre yorumlayan yanılır | [Kesin] |
| T2-O33 | Belge | "Zorunlu senaryolar" listesinin testlerde karşılığı yok | `docs/TRENDYOL-SYNC-ARCHITECTURE.md:104`; `MandatorySynchronizationScenariosTests.cs:13-264` | Scenario03, 08 ve 09 yok; out-of-order için tek eşleşme başka bir dosyada (`PackageIngestionSafetyTests.cs:165`) | [Kesin] |

### Düşük-Orta

| # | Alan | Bulgu | Dosya:satır | Başarısız senaryo | Güven |
|---|---|---|---|---|---|
| T2-DO1 | Senkron/Sipariş | Yeni siparişin ilk okunan parçası `Math.Max` yüzünden adedi kalıcı düşük bırakıyor | `MarketplaceJobProcessor.cs:9396-9405, 9690-9692` | 3 adet 2+1 olarak bölünür, ilk sayfada 2 gelir; sonraki 1 `Max(2,1)=2` olur; ciro ve stok düşümü 1 eksik kalır | [Muhtemel] |
| T2-DO2 | Senkron/Finans | n11 anahtarı `orderItemId:gün`; aynı gün içinde ikinci satır `DistinctBy` ile atılıyor | `N11SettlementSyncJobHandler.cs:82-84, 128` | Ertelenen kalem iki gün olur ve ciro iki kez sayılır; satış ile kesinti aynı günde gelirse kesinti kaybolur | [Tahmin] |
| T2-DO3 | Senkron/Performans | Açık paket taramaları her dilimde bütün açık paketleri `Take` olmadan yeniden yüklüyor | `MarketplaceJobProcessor.cs:4325-4334`; `MarketplaceJobProcessor.PttAvmOrders.cs:54-59` | Hiç kapanmayan ManualReview/OnHold paketleri listeyi süresiz büyütür | [Kesin] |
| T2-DO4 | Durum/Webhook | Pasif Trendyol/Shopify bağlantısında webhook "Açık" görünüyor, abonelik silinmiyor | `MarketplaceConnectionService.cs:564-567`; `MarketplaceWebhookService.cs:90-95` | Teslimler 404 ile reddedilir; sayfa "Açık · Bildirimler düzenli geliyor" der | [Kesin] |
| T2-DO5 | Katalog | `CatalogColorMappingPolicy.Normalize` noktasız ı'yı katlamıyor | `CatalogColorMappingPolicy.cs:23-24, 33-43`; `MarketplaceJobProcessor.cs:7018, 7161` | "Fıstık" ve "Yavruağzı" için Web Color yedeği (YESIL/PEMBE) hiç devreye girmez | [Kesin] |
| T2-DO6 | Katalog/PTT | Fiyat-stok kapsamı model kodunu marka bakmadan, ham metinle grupluyor | `TrendyolWriteComposers.cs:572-619`; `ProductFamilyKeyPolicy.cs:3-15` | İki markada "100" kodu var; B'nin varyantları A'nın PTT ürününe A'nın baz fiyatıyla gider | [Muhtemel] |
| T2-DO7 | Frontend/Rapor | Aynı "İade Edilen Tutar" etiketi iki farklı rakam gösteriyor | `ReportsPage.tsx:593, 666, 850, 862, 1344` | 1-14 Ekim verisinde aynı etiket altında 14.435,58 ve 13.994,18 görünür | [Kesin] |
| T2-DO8 | Frontend/Sipariş | Özet hata verince Geciken kartı yanlış ya da 0 gösteriyor | `OrdersPage.tsx:896, 1028, 1031`; `order-summary.ts` | HB filtresi açıkken özet hata verir, kart filtresiz sayıyı gösterir; iki sorgu da hata verirse "0 Paket" yazar | [Kesin] |
| T2-DO9 | Frontend/Komisyon | Tarife eşik alanları "1.250" girdisini 1,25 okuyor | `CommissionTariffsPanel.tsx:462, 469, 600, 1119, 1136` | "Kâr ≥ 1.250 ₺" eşiği 1,25 ₺ olur, toplu seçim neredeyse bütün satırları alır. İlişki: Y1 ile aynı aile | [Kesin] |

### Düşük

| # | Alan | Bulgu | Dosya:satır | Başarısız senaryo | Güven |
|---|---|---|---|---|---|
| T2-D1 | Durum/Webhook | HB webhook durumu zamanlayıcının boşluk kuralını (`GapLimit=2`) kullanmıyor | `InstantNotificationService.cs:60-62` | Panel "Açık", API Sağlık sayfası WEBHOOK_FALLBACK gösterir | [Kesin]; HB'ye boşluk yazılması [Muhtemel] |
| T2-D2 | Durum/Webhook | "8 dakikada bir" ifadesi sabit yazılmış | `InstantNotificationService.cs:68-69, 91`; `SynchronizationPolicies.cs:915-916` | ORDERS politikası 15 dk olan mağazada metin yanlış | [Kesin] |
| T2-D3 | Durum/UI | Shopify'da Trendyol'a özgü durum metinleri görünüyor | `InstantNotificationService.cs:76`; `instant-notification-status.ts:8` | Shopify sayfasında "Trendyol durdurdu" ya da "Trendyol'da kayıt yok" yazar | [Kesin] |
| T2-D4 | Durum | Akış sağlığı bağlantının DISABLED durumunu ve kuyruğa alınmayan politikayı dikkate almıyor | `MarketplaceConnectionService.cs:665`; `ScheduledJobProducer.cs:105` | Pasif bağlantıda bütün politikalar "Güncelleme gecikti" olur; üretim dışında FINANCE sonsuza dek "İlk çalışma bekliyor" görünür | [Kesin] |
| T2-D5 | Durum/Dashboard | HB webhook ingest, N11 uzlaştırma ve Trendyol kargo uzlaştırma hiçbir hücrede yok | `DashboardIntegrationMatrix.cs:18` | HB bildirim işleme hataları hücreye yansımaz | [Kesin] |
| T2-D6 | Durum/Dashboard | Kilit çakışmasında okuma yapmayan iş Succeeded kaydediliyor | `MarketplaceJobProcessor.cs:71-90` | Çalışmayan bir okuma "Güncel" tazeliği üretir (SyncCursor etkilenmez) | [Kesin]; etkisi [Muhtemel] |
| T2-D7 | Durum | Başarısız test bağlantının sürümünü artırıyor | `MarketplaceJobProcessor.cs:3166-3167, 3201, 3210` | 6 saatlik kurtarma testi başarısız olunca aynı anda ayar kaydeden kullanıcı 412 alır | [Kesin] |
| T2-D8 | Durum/Yetenek | Keşifte artık listelenmeyen yetenek eski seviyesinde kalıyor | `MarketplaceJobProcessor.cs:3175-3194` | Eski SUPPORTED + VerifiedAt satırı `IsVerifiedWriteCapability` için geçerli kalır | [Muhtemel] |
| T2-D9 | Durum/Webhook | Geçmiş sipariş kurtarması webhook'u sağlıksız gösterebiliyor | `ScheduledJobProducer.cs:371-377` | Tam sıfırlama veya CSV içe aktarma eski siparişleri ekler, `Orders.CreatedAt` yüzünden "bildirimsiz" sayılırlar ve yoklama sıklaşır | [Muhtemel] |
| T2-D10 | UI/İade | İade ret menüsü her pazaryerinde gösteriliyor | `ReturnPages.tsx:157-158`; `N11Returns.cs:71-74` | N11'de ret `N11_RETURN_ACTION_UNSUPPORTED` döner; PttAVM'de iade yazma yok | [Muhtemel]; sunucu izin listesi [Tahmin] |
| T2-D11 | UI | Pazarama dış yazma metinleri birbiriyle çelişiyor | `IntegrationPages.tsx:655-658` | Bir metin "iade kararları sonraki aşamada" diyor, diğeri "Pazarama'ya gönderir" | [Kesin] |
| T2-D12 | Senkron | İptal edilen okuma döngüleri `true` dönüyor; n11/Pazarama/PTT watermark'ı yarım pencereye ilerletilmek isteniyor | `MarketplaceJobProcessor.cs:4075-4085, 4136-4141, 3790-3800`; `MarketplaceJobProcessor.PttAvmOrders.cs:40-44, 145-147` | Doğruluk, iptal edilmiş token ile `SaveChangesAsync`'in istisna atmasına dayanıyor | [Kesin] |
| T2-D13 | Senkron | Sessiz vitrin okuması `hasMore=true` ve boş `nextCursor` geldiğinde vitrini "okundu" sayıyor | `MarketplaceJobProcessor.cs:3850-3856` | Kalan sayfalardaki SA/AE/AZ siparişleri 6-24 saatlik pencerenin gerisinde kalır | [Kesin] |
| T2-D14 | Senkron | Trendyol açık paket taramasında parti hatasına rağmen checkpoint ilerliyor | `MarketplaceJobProcessor.cs:4352-4372, 4384-4407` | Başarısız siparişler 1 gün boyunca yeniden denenmez | [Kesin] |
| T2-D15 | Senkron/İade | Sipariş eşleşmesi `ExternalOrderId == X \|\| OrderNumber == X` ile `SingleOrDefault` yapılıyor | `MarketplaceJobProcessor.cs:12268, 11916-11920` | İki sipariş eşleşirse zehirli kayıt oluşur ve iade sayfası her turda düşer | [Tahmin] |
| T2-D16 | Senkron/İade | HB iade taraması durum kovası başına ofsetle sayfalıyor | `MarketplaceJobProcessor.cs:11888-11957` | Kovadan çıkan claim sonrakini atlatır, kayıp yalnız tam geçişte toparlanır. İlişki: O10'un iade karşılığı | [Muhtemel] |
| T2-D17 | Stok | Pazarama'da aynı barkodlu ikinci teklif hiç "gönderildi" işaretlenmiyor | `TrendyolWriteComposers.cs:552-553` | Her turda yeniden aday olur ve sessizce düşer; kayıt yazılmaz | [Kesin] |
| T2-D18 | Stok | Envanter kalemi sonradan açılınca sipariş satırı stoğu bir kez daha düşebiliyor | `MarketplaceJobProcessor.cs:10943-10944` | Kalem, satışı zaten yansıtan uzak miktarla oluşur, satır "ilk kez okunmuş" sayılır | [Tahmin] |
| T2-D19 | Finans | Pazarama hareket araması yalnız önceden yüklenmiş satırlarda yapılıyor | `PazaramaFinanceSyncJobHandler.cs:39-41, 65`; `MarketplaceFinanceModelConfiguration.cs:10-34` | Tekil indeks ihlali bütün partiyi düşürür, iş aynı noktada takılır | [Tahmin] |
| T2-D20 | Finans | Sayfa tavanları sessizce kesiyor; Trendyol penceresi yine "kapsandı" sayılıyor | `TrendyolFinanceSyncJobHandler.cs:121, 179-205, 233` | 400 alınan ödeme türü bir daha okunmaz | [Kesin] |
| T2-D21 | Finans | HB "WillBePaid" kaydı hiç silinmiyor; tarih yoksa `now` atanıyor | `HepsiburadaFinanceSyncJobHandler.cs:51, 104` | HB'de iptal edilen kayıt panelde süresiz "ödenecek" kalır | [Tahmin] |
| T2-D22 | Finans | Shopify iade satırları anlık görüntüde yoksa siliniyor | `MarketplaceJobProcessor.cs:10125-10130` | Kısaltılmış `refunds` listesinde eski iadeler kaybolur | [Tahmin] |
| T2-D23 | Performans | `UpsertOrder` her çağrıda platform kodunu yeniden okuyor; Pazarama finansı binlerce parametreli `Contains` kullanıyor | `MarketplaceJobProcessor.cs:9384-9387, 9511-9515`; `PazaramaFinanceSyncJobHandler.cs:34-41` | Sayfa başına 200 gereksiz sorgu; büyüyen sorgu yükü | [Kesin] |
| T2-D24 | Faturalama | `EnqueueReconcileAsync` MarketplacePending/Failed faturayı Accepted'a geriletiyor | `InvoicingBillingService.cs:1483, 1759-1761`; `InvoicingJobProcessor.cs:533-537` | API ile uzlaştırma çağrılır, fatura hiçbir kurtarma yolunun almadığı Accepted durumuna döner (UI'da düğme yok) | [Kesin] |
| T2-D25 | Faturalama | `CONFIRMATION_RETRYABLE` durum makinesini atlıyor ve `Version`'ı artırmıyor | `InvoicingJobProcessor.cs:964-969` | ManualReview'daki fatura sessizce incelemeden çıkar | [Kesin] |
| T2-D26 | Faturalama | `Cancel` süren teslimi beklemiyor | `InvoicingJobProcessor.cs:542-547, 1761-1767` | Sonuç toparlanıyor; link kaldırma takibi düşmüyor (kontrol edildi) | [Kesin] |
| T2-D27 | İade | Kısmi satır kararında claim durumu işlem bekleyen satır sayısıyla hesaplanıyor | `MarketplaceSalesService.cs:2582`; `ReturnModels.cs:122-133` | A satırı onaylı, B reddedilir, claim `Rejected` olur; fatura işaretlenmez | [Kesin] |
| T2-D28 | İade | Trendyol `lineTotalDiscount` birim indirim gibi adetle çarpılıyor | `ReturnMarketplaceAmounts.cs:65, 83-84`; `TrendyolJsonMapper.cs:165` | Çok adetli satırda indirim şişebilir | [Tahmin] |
| T2-D29 | Katalog | Barkod çakışma kontrolü yayın içinde harf duyarsız, bağlantı genelinde duyarlı | `ProductPublicationComposer.cs:127-129, 384-404` | Başka ürünün içe aktarılmış bağındaki barkod kontrol edilmez | [Tahmin] |
| T2-D30 | Katalog | Satışa kapalı ama arşivlenmemiş Trendyol ürünü LIVE görünüyor | `TrendyolJsonMapper.cs:671-675` | `onSale=false` durumu gösterilmez | [Kesin] |
| T2-D31 | Katalog/PTT | Toplam stok 9.999'u aşan ya da 100'den çok varyantlı ürün uyarısız gönderilmiyor | `TrendyolWriteComposers.cs:683-688`; `PttAvmPriceInventory.cs:73-98` | Her turda yeniden denenir, kullanıcıya neden gösterilmez | [Kesin] |
| T2-D32 | Katalog/Promosyon | Promosyon maliyet ve renk eşlemesi bağlantıya bağlı değil, SKU'ya düşüyor | `PromotionOfferService.cs:253-289` | Pazaryeri barkodu başka ürünün SKU'suna eşitse maliyet ve kâr yanlış varyanttan gelir | [Kesin] |
| T2-D33 | Frontend/Sipariş | Metrik kartları hata durumunda süresiz "…" gösteriyor | `OrdersPage.tsx:1028`; `OrderMetricCards.tsx` | Sorgu hata verir, kart "—" yerine sonsuza dek yükleniyor görünür | [Kesin] |
| T2-D34 | Frontend/Sipariş | Sipariş CSV'si 20.000 kayıtta sessizce kesiliyor | `OrdersPage.tsx:132-145, 903, 930` | Bildirim "N sipariş dışa aktarıldı" der, eksik kaldığını söylemez | [Kesin] |
| T2-D35 | Frontend/Sipariş | Etiket filtresinde CSV ile liste farklı küme veriyor | `OrdersPage.tsx:899, 908` | "Basılmadı" filtresinde yalnız bu tarayıcıda basılanlar CSV'den düşer | [Kesin] |
| T2-D36 | Frontend/İade | İade onayındaki adet seçicisi birim yerine satır sayıyor | `ReturnApproveDialog.tsx:86-90, 184` | 2 adetli satırda "1 Adet" seçmek satırın tamamını onaylar | [Muhtemel] |
| T2-D37 | Frontend | Bilinmeyen sekme sayısı 0; bilinmeyen durum değeri API'ye ham gidiyor; TS `ReturnClaim` listede gelmeyen 4 alan tanımlıyor | `ReturnPages.tsx:37, 418`; `order-status-tabs.ts` | `?status=foo` boş liste "kayıt yok" gösterir | [Kesin] |
| T2-D38 | Frontend/Rapor | Negatif kesinti toplamı hesapta ve köprüde farklı ele alınıyor | `ReportsPage.tsx:415-417, 660-669, 1042-1047` | Alacak kâra eklenmez, köprüde masrafa katılır; basamak sonu ile sonuç çubuğu ayrışır | [Muhtemel] |
| T2-D39 | Realtime | "Etiket basıldı" işareti diğer oturumlara yayılmıyor | `MarketplaceSalesService.cs:238-244`; `label-printed.ts:12` | Başka kullanıcı basıldığını görmez, paket iki kez basılır | [Kesin] |
| T2-D40 | Realtime | Birden çok API örneğinde olaylar diğer örneklere ulaşmıyor (backplane yok) | `OperationsRealtimeDispatchLease.cs:6-9`; `Api/Program.cs:59` | Bugün tek örnek var; ölçeklenince sessizce bozulur | [Kesin kod / Tahmin tetiklenme] |
| T2-D41 | Telemetri | Zaman aşımları metrikte "cancelled" olarak görünüyor | `MarketplaceHubTelemetry.cs:36-56` | Pazaryeri zaman aşımı oranı izlenemez | [Kesin] |
| T2-D42 | Telemetri | Metrikler hiçbir yere aktarılmıyor (exporter yok) | `MarketplaceHubTelemetry.cs:6-7`; `docs/CI-CD-DEPLOY.md:481-483` | Geçmiş, alarm ya da pano yok; yalnız `dotnet-counters` ile anlık görülebiliyor | [Kesin] |
| T2-D43 | Test | Var olmayan bir metodun yokluğunu doğrulayan test | `BackgroundProcessingAuditTests.cs:325-332` | Aynı işi farklı adlı bir metod yapsa da test geçer | [Kesin] |
| T2-D44 | Test | HB `Undelivered → OnHold` "documented statuses" adıyla sabitleniyor | `MandatorySynchronizationScenariosTests.cs:149` | DO2'nin düzeltmesi bu testi kırar | [Kesin] |
| T2-D45 | Test | Ayar yoksa faturalama açık sayılıyor; test bunu TRENDYOL için de sabitliyor | `MarketplaceInvoiceCreationPolicyTests.cs:8-14` | D4'ün kapsamı N11/Pazarama ile sınırlı değil; düzeltme bu testi kırar | [Kesin] |
| T2-D46 | Test | Telemetri testi yalnız 429 etiketini sınıyor | `MarketplaceTelemetryTests.cs:10-35` | T2-D41 bu yüzden yakalanmadı | [Kesin] |
| T2-D47 | Test | `POST /auth/mfa/disable` ve kurtarma kodunun ikinci kullanımı testsiz | `AuthEndpoints.cs:35, 451-453`; `ApiSecurityEndToEndTests.cs:23-75` | Kod doğru görünüyor ama regresyon koruması yok | [Kesin] |
| T2-D48 | Test | Fiyat girişi testinde virgülsüz Türkçe binlik (`'1.250'`) yok | `quick-price-edit.test.ts:266-268` | Y1 bu yüzden yakalanmadı | [Kesin] |
| T2-D49 | Test | Outbox dışı yazımların realtime olayı üretmemesi testsiz | (bkz. T2-O28, T2-D39) | Silme/`ExecuteUpdate` yolları sessiz kalır | [Kesin] |
| T2-D50 | Belge | Realtime'ın "her başarılı job sonrası" tetiklendiği yazıyor; gerçekte `SaveChanges` sırasında tetikleniyor | `docs/TRENDYOL-SYNC-ARCHITECTURE.md:48, 100`; `AppDbContext.cs:214-267`; `realtime-query-keys.ts:19, 34` | Okuyan, job bitişinde bütün ekranların yenilendiğini sanır | [Kesin] |
| T2-D51 | Belge | Faturalama varsayılanının istisnası belgede yalnız HB | `docs/RAVENCIA-SISTEM-ISLEYIS-BELGESI.md:624` | Kod Shopify dışındaki bütün pazaryerlerinde açık sayıyor | [Kesin] |
| T2-D52 | Belge | Veri silme onay metni farklı (`VERİLERİ SİL` / "Verileri sil") | `OperationalDataMaintenanceService.cs:20` | Belge ile hata mesajı farklı metin istiyor | [Kesin] |
| T2-D53 | Belge | CI süre sınırları ve `nuget-cache` işi eksik anlatılıyor | `docs/CI-CD-DEPLOY.md:527-531`; `ci.yml:316-319` | Belge 5/15 dk diyor, `nuget-cache` 10 dk | [Kesin] |
| T2-D54 | Belge | "Lint her zaman çalışır" yanıltıcı | `docs/CI-CD-DEPLOY.md:543`; `ci.yml:417-443` | İş, içerik hash'i eşleşince bütünüyle atlanıyor | [Muhtemel] |

## İkinci tur önceki bulguları nasıl değiştiriyor

**Testler hataları bilerek koruyor.** Önceki rapor yalnız `ReturnLifecyclePolicyTests.cs:56` için (O2) bu sorunu not etmişti. Bu tur üç yeni örnek buldu. KDV oranı testi O1'i sabitliyor (T2-O30), HB Undelivered testi DO2'yi sabitliyor (T2-D44), faturalama varsayılanı testi D4'ü sabitliyor (T2-D45). Sonuncusu D4'ün kapsamını da genişletiyor: önceki rapor yalnız N11 ve Pazarama'dan söz ediyordu, test TRENDYOL ve HEPSIBURADA için de "açık" bekliyor. Kural düzeltilirken bu InlineData satırları bilerek değiştirilmezse CI düzeltmeyi reddeder. Bir boşluk da hata ailesini açıklıyor: fiyat girişi testinde `'1.250'` yok (T2-D48). Y1 bu yüzden yakalanmadı. Aynı binlik nokta hatası Komisyon Tarifeleri eşiklerinde de var (T2-DO9). Ortak bir `parseTrNumber` yardımcısı Y1, O24 ve T2-DO9'u birlikte kapatır.

**"Bilinmeyen bilinmeyen kalır" ihlalleri durum katmanına da yayılmış.** Önceki turda on yer sayılmıştı. Bu tur durum ve görüntüleme tarafına yenilerini ekliyor:

- Hiç bildirim gelmemiş webhook "düzenli geliyor" gösteriliyor (T2-O15).
- Geçici sonda hatası "yetenek yok" oluyor (T2-O18).
- Hata veren özet Geciken kartında 0 ya da filtresiz sayı gösteriyor (T2-DO8).
- Bilinmeyen sekme 0 sayılıyor (T2-D37).
- Reddedilen sipariş sessizce atlanıyor ama iş "başarılı" (T2-O8).

**Senkron işleyiciler önceki adaptör bulgularının sonuçlarını taşıyor.** Trendyol claim kova sırası (D14), işlemcide iade satırlarının geriye gitmesine yol açıyor (T2-O10). HB ofset kayması (O10) iade taramasında da var (T2-D16). Stok projeksiyonunda ise olgun bir yapı var: satır başına defter, `InventoryItem.Version` eşzamanlılık belirteci ve tekil idempotency indeksi çift düşümü engelliyor. Kalan risk kenar durumlarda (T2-DO1, T2-D17, T2-D18).

**Faturalamada çift mali belge yolu yine bulunmadı.** Bunu sağlayanlar: tek SALE indeksi, fatura Id'sinden türeyen sabit UUID, belirsiz sonucun yalnız okumayla çözülmesi ve yeniden taslağın yalnız bütünüyle FAILED faturada yapılması. Bu tur aynı sertliğin teslim tarafında olmadığını gösteriyor. Fatura pek çok yolla sahipsiz kalabiliyor: AdjustmentRequired (T2-Y3), state'siz MarketplacePending (T2-Y4), UNKNOWN döngüsü (T2-O7), yer tutucu paket (T2-O6) ve Accepted'a gerileme (T2-D24). T2-O6 ile T2-Y4 zincirleniyor: yer tutucu paket ön okumada bulunamazsa fatura doğrudan sahipsiz duruma düşüyor. Önceki turdaki O9 (`invoiceDateTime: null`) da aynı teslim yolunda.

**Realtime "kaçarsa yeniden okunur" varsayımına dayanıyor, ama bu yol eksik.** Kiracı dağıtımı, origin filtresi ve yük içeriği temiz. Olay yükünde kişisel veri yok, metrik etiketleri düşük kardinaliteli. Yenilemenin tek tetiği `onreconnected`. Elle `start()` ile yeniden bağlanma (T2-O27) ve change tracker dışındaki silme/güncellemeler (T2-O28, T2-D39) bu varsayımın dışında kalıyor. Önceki turdaki D27 (1 dakikalık iptal gecikmesi) bunun güvenlik tarafıydı. Canlı host 7/24 açık ve her doğrulanmış push deploy edildiği için T2-O27 düzenli olarak tetikleniyor.

**Belge sapması en çok `docs/TRENDYOL-SYNC-ARCHITECTURE.md`'de.** Sağlık eşikleri yarıya inmiş (T2-O32), realtime modeli değişmiş (T2-D50) ve zorunlu senaryo listesinin testlerde karşılığı yok (T2-O33). Buna karşılık `marketplace-docs` bakımlı. n11 14/15 gün, Pazarama 28 gün ve 60/30 dk kotası, PttAVM 35/40 gün ve Shopify 45 sn değerleri kodla uyumlu. Tek açık çelişki Trendyol `getClaims` tarih filtresinin anlamı: kod yorumu "oluşturma tarihi" diyor (`MarketplaceJobProcessor.cs:11376-11379`), belge "lastModifiedDate" diyor (`trendyol.md:40-41`). Hangisinin doğru olduğu resmi belgeyle doğrulanmalı.

**Frontend sözleşmeleri temiz, hata türetimlerde.** Orders, Returns, Reports, Payouts, Invoicing ve Pazarama finans tipleri C# kayıtlarıyla alan alan eşleşiyor. Rakam hataları Raporlar'ın kendi aritmetiğinden çıkıyor (T2-Y6, T2-O24, T2-O25, T2-O26). Bunlar önceki turdaki Y2 ve O26 ile birleşince, maliyet girilmemiş ve çok platformlu bir kiracı Genel Bakış'ta birbirini tutmayan üç toplam görebilir: Kâr Köprüsü, Masraf Kalemleri ve Kârlılık defteri.

## Canlıda çalıştırılacak salt okunur kontroller

**Bu kontrollerin hiçbiri çalıştırılmadı.** Bu ortamda canlı sunucuya ssh erişimi ya da anahtar yok. Aşağıdaki sorgular notlardan alındı ve EF varsayılanı olan PascalCase, tırnaklı sütun adlarını kabul ediyor. Çalıştırmadan önce `/prod-sql` ile tablo sütunları listelenmeli. `JobStatus` ve `IssueStatus` değerlerinin metin mi sayı mı tutulduğu da doğrulanmalı. Her sorgu `BEGIN READ ONLY … ROLLBACK` içinde çalıştırılmalı.

**T2-Y1: aktif bağlantıda kimlik dışı hata kodu.**
```sql
SELECT "PlatformCode","DisplayName","Environment","Status","LastErrorCode","LastTestedAt","LastSuccessAt"
FROM "PlatformConnections"
WHERE "Status" IN ('ACTIVE','VERIFIED') AND COALESCE("LastErrorCode",'') <> ''
ORDER BY "PlatformCode";
```

**Bayat ya da hatalı akışlar (açık politika × imleç).** Politika-imleç eşlemesi `CursorResourceType` ile aynı.
```sql
SELECT c."PlatformCode", c."DisplayName", p."ResourceType", p."IntervalSeconds",
       s."LastSuccessAt", s."LastAttemptAt", s."ConsecutiveFailureCount", left(s."LastError",120) AS last_error,
       s."CursorStagnantSince", now() - s."LastSuccessAt" AS age
FROM "ConnectionSyncPolicies" p
JOIN "PlatformConnections" c ON c."Id" = p."ConnectionId"
LEFT JOIN "SyncCursors" s ON s."ConnectionId" = p."ConnectionId" AND s."ResourceType" = CASE
     WHEN p."ResourceType" = 'ORDERS' THEN 'ORDERS_HOT'
     WHEN p."ResourceType" = 'ORDER_RECOVERY' THEN 'ORDERS_RECOVERY'
     WHEN p."ResourceType" LIKE 'ORDER_RECONCILE_%' THEN 'ORDER_RECONCILIATION'
     WHEN p."ResourceType" LIKE 'RETURN_RECONCILE_%' THEN 'RETURN_RECONCILIATION'
     WHEN p."ResourceType" LIKE 'STOCK_RECONCILE_%' THEN 'STOCK_RECONCILIATION'
     ELSE p."ResourceType" END
WHERE c."Status" IN ('ACTIVE','VERIFIED') AND p."Enabled"
  AND p."ResourceType" NOT IN ('PRICE_WRITE','STOCK_WRITE','SHIPMENT_WRITE','RETURN_WRITE','MANUAL_WRITE')
  AND (s."LastSuccessAt" IS NULL OR s."LastSuccessAt" < now() - make_interval(secs => p."IntervalSeconds" + 900)
       OR s."ConsecutiveFailureCount" > 0 OR s."CursorStagnantSince" IS NOT NULL)
ORDER BY age DESC NULLS FIRST;
```

**T2-O17: UI'da gizlenen akışların durumu.**
```sql
SELECT c."PlatformCode", c."DisplayName", s."ResourceType", s."LastSuccessAt", s."ConsecutiveFailureCount", left(s."LastError",120)
FROM "SyncCursors" s JOIN "PlatformConnections" c ON c."Id" = s."ConnectionId"
WHERE (c."PlatformCode" = 'HEPSIBURADA' AND s."ResourceType" IN ('FINANCE','QUESTIONS','RETURN_LIFECYCLE'))
   OR (c."PlatformCode" IN ('N11','PAZARAMA') AND s."ResourceType" = 'FINANCE');
```

**T2-Y2: "orders" hücresinin gizlediği, sürekli hata veren durum ve kurtarma işleri.**
```sql
SELECT j."ConnectionId", j."JobType", j."Status", j."CreatedAt", j."AttemptCount", j."LastErrorCode"
FROM "IntegrationJobs" j
WHERE j."CreatedAt" > now() - interval '7 days'
  AND (j."JobType" LIKE '%ORDER_STATUS_SYNC' OR j."JobType" LIKE '%ORDER_RECOVERY_SYNC')
  AND j."Status" IN ('RetryScheduled','Dead','ManualReview','Blocked')
ORDER BY j."CreatedAt" DESC LIMIT 50;
```

**T2-O16: başarılı teslimden sonra açık kalan reddedilmiş webhook sorunu.**
```sql
SELECT i."DedupeKey", i."Status", i."LastSeenAt", w."LastReceivedAt"
FROM "OperationalIssues" i
JOIN "WebhookSubscriptions" w ON w."Status" = 'ACTIVE'
     AND i."DedupeKey" LIKE 'webhook-rejected:' || replace(w."ConnectionId"::text,'-','') || ':%'
WHERE i."ResolvedAt" IS NULL AND w."LastReceivedAt" > i."LastSeenAt";
```

**T2-DO4: pasif bağlantıda açık kalan abonelik. T2-O15: hiç bildirim almamış Trendyol/Shopify abonelikleri.**
```sql
SELECT c."PlatformCode", c."DisplayName", c."Status", w."ExternalSubscriptionId", w."VerifiedAt", w."LastReceivedAt"
FROM "WebhookSubscriptions" w JOIN "PlatformConnections" c ON c."Id" = w."ConnectionId"
WHERE w."Status" = 'ACTIVE' AND c."Status" NOT IN ('ACTIVE','VERIFIED');

SELECT c."PlatformCode", c."DisplayName", w."VerifiedAt", w."LastReceivedAt"
FROM "WebhookSubscriptions" w JOIN "PlatformConnections" c ON c."Id" = w."ConnectionId"
WHERE w."Status" = 'ACTIVE' AND w."LastReceivedAt" IS NULL AND c."PlatformCode" IN ('TRENDYOL','SHOPIFY');
```

Notlar bazı kontroller için hazır SQL vermiyor, yalnız neyin ölçülmesi gerektiğini söylüyor. Bunlar sütunlar doğrulandıktan sonra yazılmalı:

- **T2-Y7:** `IntegrationJobs` içinde sürekli `SLICE_CONTINUES` dönen bir `N11_SETTLEMENT_SYNC` işi.
- **T2-DO1:** `OrderLines.OrderedQuantity` ile paket tahsis toplamı arasındaki fark.
- **T2-Y3:** `AdjustmentRequired` durumundaki faturalardan kaçının kısmi iade kaynaklı olduğu.
- **T2-Y4:** `MarketplaceDeliveryState` satırı olmayan `MarketplacePending` faturaların sayısı.
- **T2-O7:** UNKNOWN teslim işlerinin tekrar sayısı.
- **T2-O22 ve T2-O23:** fiyatları ayrışmış PttAVM ailelerinin sayısı.
- **Realtime:** outbox birikimi ve `LastDispatchError` değerleri.

Devre kesici için SQL yerine log kontrolü gerekiyor: `/prod-logs logs worker --since 24h --grep LOCAL_CIRCUIT_OPEN`, ardından aynı sorgu `api` için. Devre süreçler arasında paylaşılmadığı için iki sonucun farklı çıkması beklenir. İlk iki sorgudan biri sıfırdan fazla satır döndürürse T2-Y1 canlıda kullanıcıya yanlış "sağlıklı" bilgisi gösteriliyor demektir.

## Denetimin sınırları

**Backend testleri çalıştırılmadı.** Ortamda `dotnet` yok. Bu yüzden 327 backend test dosyasının hangilerinin bugün yeşil olduğu bilinmiyor ve hiçbir backend bulgusu çalıştırılarak doğrulanmadı. Test boşlukları hedefli grep ve örneklemeyle bulundu. Para/KDV, fatura teslimi ve imleç pencerelerinde testi olmayan başka dallar kalmış olabilir. Web tarafında `npx vitest run src/features/marketplace src/features/reports` çalıştırıldı: 81 dosya, 419 test, hepsi başarılı.

**Canlı sunucuya ve veritabanına erişilmedi.** `/prod-sql` ve `/prod-logs` kullanılmadı. Bu yüzden şu sorular yanıtsız: Trendyol v2 `shipmentPackageIds=0` sorgusuna 404 mü yoksa boş yanıt mı veriyor? HB'de aynı `lineItemId` bir sayfada iki kez geliyor mu? n11 hakediş aralığında kaç gün listeliyor? Pazarama mağazasının 60 günlük sipariş hacmi ne? Toplu onaya değişim talebi düşüyor mu? Net KDV hatasının gerçek TL etkisi ne? Muhtemel ve Tahmin etiketli bulguların tetiklenme olasılığı bu yanıtlara bağlı.

**Çevrimdışı resmi pazaryeri belgeleri bu ortamda yoktu** (`C:/Users/emrek/OneDrive/Belgeler/Ravencia-API-Docs/`). Bu yüzden üç soru doğrulanamadı. Birincisi, PttAVM `UpdateProductsStockPrice` çağrısının varyant fiyatını fark olarak mı uyguladığı (T2-O22, T2-O23). İkincisi, Trendyol `lineTotalDiscount` alanının birim mi toplam mı olduğu (T2-D28). Üçüncüsü, Trendyol `getClaims` tarih filtresinin anlamı. Pazaryeri bilgileri yalnız `.claude/skills/marketplace-docs/*.md` dosyalarından alındı.

**Bazı dosyalar kısmen okundu.** Şunlar satır satır değil, kalıp ve grep taramasıyla incelendi:

- `MarketplaceJobProcessor.cs`: claim eşitleme ve stok etkileşimi, ~12262 civarı.
- `ReturnPendingStock.SettleClaimAsync`: T2-O10'un stok etkisi bu yüzden [Tahmin].
- `OrderInventoryStockPolicy.Plan` ve `StockProjectionOutbox`.
- N11, Pazarama ve PttAVM `*Invoices.cs` kapıları.
- `ReportsPage.tsx` 260-330 ve 1150-1600 aralıkları.
- `MappingPages.tsx` (1972 satır) ve `ProductEditorPages.tsx` (3660 satır).
- 60'tan fazla `ExecuteUpdate`/`ExecuteDelete` çağrısı: yalnız Order ve ReturnClaim'e dokunanlar doğrulandı.
- `RUNBOOK-*` ve plan belgeleri.

`CommissionTariffContracts.cs`, `CatalogContracts.cs` ve eşleme sözleşmeleri TS tipleriyle alan alan karşılaştırılmadı.

## İki turun birleşik düzeltme sırası

Sıralama iki ölçüte göre yapıldı: geri alınamayan dış etki ve kullanıcının gördüğü yanlış bilgi. Canlı ölçüm yapılmadığı için sıra kesin değil.

**1. Pazaryerine ve mali belgeye giden geri dönüşsüz yanlış veri.**

- Toplu iade diyaloğunda Hepsiburada öğesi varsa "ücret iadesi / değişim" seçimi iste (T2-O1).
- Model kodu yedeğinde renk ve beden daraltmasını her pazaryeri için zorunlu yap, ya da tek adayı yalnız uzak ürün de tek varyantlıysa kabul et (T2-Y5).
- Hızlı fiyat girişinde Türkçe binliği düzelt ve `'1.250'` testini ekle (Y1, T2-D48). Toplu stokta tam sayı kontrolü yap (O28).
- `IsLineVatRateUnknown`'ı düzelt ve sabitleyen InlineData satırlarını birlikte değiştir (O1, T2-O30). Eksik faturalama ayarını kapalı say ve testi buna göre değiştir (D4, T2-D45).
- `Submit` içinde taslak kapılarını yeniden uygula (T2-O3).
- İade kararında claim sürümünü dış çağrıdan önce artır ya da kilit al (T2-O4).

**2. Fatura teslim tıkanmaları.**

- Kısmi iadede satır ve adet karşılaştırması yap; AdjustmentRequired'dan geri dönüş yolu aç (T2-Y3).
- `CanRetryDelivery`'ye "MarketplacePending + durum kaydı yok + aktif iş yok" durumunu ekle (T2-Y4).
- UNKNOWN teslime yaş sınırı koy ve sınır aşılınca ManualReview'a düşür (T2-O7).
- Teslim hedeflerini `CurrentShipmentPackages` ile seç (T2-O6).
- Uzlaştırma gerilemesini (T2-D24) ve `CONFIRMATION_RETRYABLE` dalını (T2-D25) kapat.
- E-Faturam karar yolunu düzelt (Y4, O8, O9).

**3. Pazaryeri durum görünürlüğü.**

- Karta akış sağlığı özeti ve kimlik dışı `LastErrorCode` için "Uyarı" rozeti ekle (T2-Y1, T2-O18).
- Gösterge panelinde başarı ve hatayı iş tipi başına eşle ya da doğrudan SyncCursor sağlığını kullan (T2-Y2, T2-O13, T2-D5, T2-D6).
- Webhook'ta ACTIVE + sağlıksız durumu QUIET'e, `lastReceivedAt` null durumunu WAITING'e eşle; başarılı teslimde `webhook-rejected` sorununu çöz; pasife alınan bağlantının aboneliğini kapat (T2-O14, T2-O15, T2-O16, T2-DO4).
- Web politika setlerini arka uçla aynı listeden üret (T2-O17).
- Belgedeki eşikleri koda hizala (T2-O32).

**4. Ekrandaki yanlış rakamlar.**

- Net KDV'yi bütün platformlar için hesapla ya da kârı açıkça "Trendyol KDV'si düşülmüş" diye etiketle (T2-Y6).
- Halka toplamına KDV'yi ekle (T2-O24), köprü sonucunu `left − tax` yap (T2-O25), adet metriklerinde tutarı olan siparişlerin adedini kullan (T2-O26).
- Önceki turdan Y2, O26, O23, O24, O25 ve D32'yi birlikte ele al.
- Bilinmeyeni 0 ya da "…" gösteren kartları "—"ye çevir (T2-DO8, T2-D33, T2-D37).
- Ortak bir `parseTrNumber` yardımcısıyla T2-DO9'u da kapat.

**5. Senkron veri bütünlüğü.**

- n11 hakedişte "recent" günleri dilim başına bir kez oku (T2-Y7, T2-O11).
- Pazarama finansında okunmamış eski siparişlere ve ödeme tarihi boş olanlara öncelik ver (T2-O2).
- Reddedilen siparişleri hedefli okuma işine çevir (T2-O8).
- HB satır birleştirmesini `LastModifiedAt` sırasına göre yap ve indirimi tekilleştir (T2-O9).
- İade satırlarını tazelik koşuluna bağla (T2-O10).
- Önceki turdan O6, O7, O10, O11 ve O12, bu turdan T2-DO1 ve T2-D16.

**6. Felaket kurtarma.** Önceki turdaki Y5, O32, O33, O34 ve O35 hâlâ açık ve bu turun bulgularından bağımsız olarak öncelikli.

**7. Realtime ve gözlemlenebilirlik.**

- Elle `start()` sonrasında `realtimeReconnectQueryKeys` invalidation'ını çağır ve bunu test et (T2-O27, T2-O31).
- Silme ve `ExecuteUpdate` yollarına elle "returns" ya da "orders" olayı ekle (T2-O28, T2-D39).
- Bir OpenTelemetry exporter ekle ve zaman aşımını ayrı etiketle (T2-D42, T2-D41).

**8. Arka plan işleri ve kota.**

- Önceki turdan O14, O15, O16, O17, O19 ve O20; bu turdan N+1 ve sınırsız yüklemeler (T2-O12, T2-DO3, T2-D23).
- HB kotasını çıkış IP'si başına tut (Y3, D17, O4).

**9. Katalog anahtar birliği ve kalanlar.**

- `NormalizeLabel`, `CatalogColorMappingPolicy.Normalize` ve kod karşılaştırmasını tek `CatalogKeyNormalizer.Key` çağrısında birleştir (T2-O19, T2-O20, T2-DO5).
- PttAVM varyant fiyat modeli için önce resmi belgeyle doğrulama yap (T2-O22, T2-O23, T2-DO6, T2-D31).
- Tautolojik senaryo testlerini gerçek davranış testleriyle değiştir ve belgedeki listeyi güncelle (T2-O29, T2-O33).
- Kalan Düşük bulgular ve belge temizliği.

## Sonuç

İki tur birlikte okunduğunda kalıp netleşiyor. Sistem dış yazmaları tekrarlamamak ve çift mali belge üretmemek için güçlü korumalara sahip. Takılan, tıkanan ya da çalışmayan bir şeyi **görünür kılmak** için ise aynı özeni göstermiyor. Fatura sahipsiz kalıyor ama listede "işleniyor" yazıyor. Akış OFFLINE ama kart yeşil. Webhook hiç gelmemiş ama "düzenli geliyor" deniyor. Hakediş işi aynı günleri sonsuza dek okuyor ama gösterge panelinde görünmüyor. Bu hatalar veri bozmuyor, ama operatörün müdahale etmesi gereken anı gizliyor. Doğru bilgi zaten var: SyncCursor tablosu ve iş geçmişi gerçeği tutuyor. Sorun, bu bilginin kullanıcının baktığı yere taşınmaması. Durum katmanlarını tek bir "çalışıyor mu?" yanıtında birleştiren, bilinmeyeni sarı ve duranı kırmızı gösteren tek bir kaynak, bu turun Yüksek ve Orta bulgularının neredeyse yarısını kökten kapatır.

İkinci önemli sonuç, test paketinin bazı yerlerde yanlış davranışı koruması. Üç test önceki turun hatalarını sabitliyor, "zorunlu senaryo" testlerinin bir kısmı ise hiçbir şey ölçmüyor. Bu yüzden düzeltmeler test değişikliğiyle birlikte planlanmalı. Bir sonraki adım, yukarıdaki salt okunur sorgularla T2-Y1, T2-Y2, T2-Y4 ve T2-Y7'nin canlıdaki karşılığını saymak ve `dotnet` bulunan bir ortamda backend testlerini çalıştırmak. Bu ölçümler, bu raporun sıralamasını tahminden kanıta taşır.
