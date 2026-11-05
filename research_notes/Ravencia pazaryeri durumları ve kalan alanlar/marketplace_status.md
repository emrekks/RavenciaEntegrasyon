# Pazaryeri bağlantılarının çalışma durumu: durum modeli, hatalar ve yetenek matrisi

Kapsam: Trendyol, Hepsiburada (HB), N11, Pazarama, PttAVM, Shopify, Trendyol E-Faturam. Yalnız kod okundu (commit
`d4cb6ed`); canlı sunucuya erişim yoktu. Önceki denetimde (`research_notes/Ravencia proje hata denetimi/`) yer alan
bulgular tekrarlanmadı, yalnız atıf yapıldı: adapters.md §6 (sınırlayıcının fail-open davranışı, Shopify tazelik
penceresi, ROUTE_TOKEN), api_security.md 1.3 (route-token reddiyle sahte uyarı açık tutma), domain_application.md §5
(`WebhookPollingPolicy.SilenceLimit` sağlıkta kullanılmıyor).

## 1. Durum modeli nasıl kurulmuş?

### Takeaway
Tek bir "bağlantı sağlığı" alanı yok. Durum dört bağımsız katmandan oluşuyor ve hiçbiri diğerini beslemiyor:
(a) `PlatformConnection.Status` + `LastErrorCode`, (b) akış başına `SyncCursor` telemetrisi ve sağlık sınıflandırması,
(c) webhook durumu, (d) gösterge panelindeki iş tablosu matrisi. Entegrasyon kartı yalnız (a)'yı, hem de süzerek
gösteriyor. Bu yüzden bütün akışları dururken bile kart "Bağlı ve Aktif" görünebiliyor.

### Cited Findings
- **(a) Bağlantı düzeyi.** `Status` DRAFT → VERIFIED (başarılı test) → ACTIVE (etkinleştirme) / DISABLED / DELETED.
  `LastErrorCode` iki yerde yazılıyor:
  - Başarısız test ya da keşif ([MarketplaceJobProcessor.cs:3166-3167](src/MarketplaceHub.Infrastructure/Persistence/MarketplaceJobProcessor.cs)).
  - Kimlik sınıfı bir hatayla Blocked olan senkron ([MarketplaceJobProcessor.cs:292-299](src/MarketplaceHub.Infrastructure/Persistence/MarketplaceJobProcessor.cs)).

  Bu kodu yalnız başarılı bir test (satır 3223) ya da tam sıfırlama temizliyor. Kod kalırsa
  `RetestFailedOperationalConnectionsAsync` bağlantıyı 15 dk, 1 sa, sonra 6 saatte bir yeniden test ediyor
  ([ScheduledJobProducer.cs:536-565](src/MarketplaceHub.Infrastructure/Persistence/ScheduledJobProducer.cs),
  [SynchronizationPolicies.cs:1017-1050](src/MarketplaceHub.Application/SynchronizationPolicies.cs)). Bağlantı
  düzeyindeki `LastSuccessAt` son başarılı *testtir*, son senkron değildir.
- **(b) Akış düzeyi.** `RecordSyncAttempt`, `LastAttemptAt` alanını yazar. `RecordSyncCompletion` başarıda
  `LastSuccessAt`'i yazıp `ConsecutiveFailureCount`'u sıfırlar, hatada `LastError` ve `LastErrorAt` yazıp sayacı artırır
  ([MarketplaceJobProcessor.cs:301-372](src/MarketplaceHub.Infrastructure/Persistence/MarketplaceJobProcessor.cs)).
  Dilim (Continue) yalnız `LastSliceAt`'i yazar.

  `SyncPoliciesAsync`, `MarketplaceSyncHealthPolicy.Classify` ile HEALTHY / DELAYED / DEGRADED / OFFLINE üretir.
  Eşikler: 120 s, 300 s, 900 s + beklenen periyot. Ayrıca `MarketplaceCursorProgressPolicy` ile STALLED (imleç 3 periyot
  ya da 15 dk ilerlemedi) hesaplanır.
  ([MarketplaceConnectionService.cs:595-668](src/MarketplaceHub.Infrastructure/Persistence/MarketplaceConnectionService.cs),
  [SynchronizationPolicies.cs:202-280](src/MarketplaceHub.Application/SynchronizationPolicies.cs),
  [SyncCursorProgressTracker.cs](src/MarketplaceHub.Infrastructure/Persistence/SyncCursorProgressTracker.cs) — AppDbContext.SaveChanges içinden.)

  Web bunu [sync-policy-health.ts](src/MarketplaceHub.Web/src/features/marketplace/sync-policy-health.ts) ile etikete
  çeviriyor: "Güncel / Gecikiyor / Kısmi hata / Güncelleme gecikti / İmleç durgun".
- **(c) Webhook.** `InstantNotificationService.StatusAsync` şu durumlardan birini döndürür: UNSUPPORTED, OFF, ACTIVE,
  PASSIVE, MISSING, UNKNOWN, WAITING, QUIET, CONNECTION_INACTIVE
  ([InstantNotificationService.cs:41-94](src/MarketplaceHub.Infrastructure/Persistence/InstantNotificationService.cs)).
  Web, `webhookTone` ile ok / warn / bad / off / na rengine çevirir
  ([instant-notification-status.ts:17-27](src/MarketplaceHub.Web/src/features/marketplace/instant-notification-status.ts)).
  Zamanlayıcı ise webhook'un sağlıklı olup olmadığına `WebhookPollingPolicy.IsHealthy` ile ayrıca karar veriyor
  ([ScheduledJobProducer.cs:352-368](src/MarketplaceHub.Infrastructure/Persistence/ScheduledJobProducer.cs)).
- **(d) Gösterge paneli matrisi.** Son 7 gündeki `IntegrationJobs` satırlarından veri türü başına ok / warn / bad / off
  üretiliyor ([DashboardIntegrationMatrix.cs](src/MarketplaceHub.Infrastructure/Persistence/DashboardIntegrationMatrix.cs)).
  SyncCursor'a hiç bakmıyor.
- **Devre kesici.** Süreç içi bir `ConcurrentDictionary` kullanılıyor; anahtar host + ilk 2 yol bölümü + hesap hash'i.
  5 ardışık hata devreyi 30 s açıyor, Retry-After içeren bir 429 en çok 15 dk açıyor. Devre açıkken yerel 503/429 ile
  `LOCAL_CIRCUIT_OPEN` dönüyor. Trendyol kendi resilience handler'ını kullanıyor
  ([MarketplaceCircuitBreakerHandler.cs](src/MarketplaceHub.Infrastructure/Adapters/MarketplaceCircuitBreakerHandler.cs)).
  Devre durumu hiçbir yerde kalıcı değil ve UI'da gösterilmiyor; etkisi yalnız akış hatası olarak görünüyor.
- **Yetenek kanıtı.** Test sırasında `DiscoverCapabilitiesAsync` sonuçları `PlatformCapability` satırlarına yazılıyor.
  `IsVerifiedWriteCapability` yalnız HB yazma akışlarını açarken zorunlu
  ([CapabilityEvidencePolicy.cs](src/MarketplaceHub.Application/CapabilityEvidencePolicy.cs),
  [MarketplaceConnectionService.cs:660-680](src/MarketplaceHub.Infrastructure/Persistence/MarketplaceConnectionService.cs)).
  Web yetenekleri TRENDYOL dışındaki pazaryerleri için yüklüyor
  ([connection-capability-query.ts](src/MarketplaceHub.Web/src/features/marketplace/connection-capability-query.ts)).
- **Zaman gösterimi.** Bütün tarih biçimleme `Europe/Istanbul` ile yapılıyor
  ([format-date.ts:4-40](src/MarketplaceHub.Web/src/shared/format-date.ts)). "Son senkron" gösteriminde saat dilimi
  hatası bulunmadı.

### Inferences
- "Çalışıyor mu?" sorusunun tek doğru yanıtı akış tablosunda (b). Kart (a) ve gösterge paneli (d) bundan bağımsız ve
  daha iyimser.

### Gaps
- Gerçek LastErrorCode dağılımı ve imleç yaşları canlıda ölçülmedi; SQL'ler 4. bölümde.

## 2. Doğrulanmış kusurlar

### Takeaway
En önemlileri dört tane:
- Kart, kimlik dışı hata kodlarında ve tüm akışlar durmuşken "Bağlı ve Aktif" (yeşil) gösteriyor.
- Gösterge panelinin "Siparişler" hücresi, sürekli hata veren durum/kurtarma taramasını başka bir sipariş işinin
  başarısıyla örtüyor.
- Trendyol/Shopify webhook'u sağlıksızken bile ACTIVE (yeşil) kalıyor.
- HB, N11 ve Pazarama FINANCE akışları, HB QUESTIONS ve RETURN_LIFECYCLE akışları entegrasyon sayfasında hiç
  gösterilmiyor.

### Cited Findings

**S1 — [Yüksek][Kesin] Kart, kimlik dışı hata kodunu ve akış sağlığını yok sayıp "Bağlı ve Aktif" diyor.**
- Kartta yalnız `connected = status ACTIVE|VERIFIED` ve `connectionAuthSummary` kullanılıyor
  ([IntegrationPages.tsx:139, 211-215](src/MarketplaceHub.Web/src/features/marketplace/integrations/IntegrationPages.tsx)).
- `connectionAuthProblem` yalnız kimlik/erişim regex'ine uyan kodlarda rozet veriyor
  ([connection-auth-alert.tsx:9-22](src/MarketplaceHub.Web/src/features/marketplace/integrations/connection-auth-alert.tsx)).
- Test ya da keşif sırasında oluşan geçici bir hata (`HEPSIBURADA_TIMEOUT`, `N11_NETWORK_ERROR`, `*_RATE_LIMITED`) veya
  `{PLATFORM}_REQUIRED_READ_CAPABILITY` kodu `LastErrorCode`'a yazılıyor
  ([MarketplaceJobProcessor.cs:3166-3204](src/MarketplaceHub.Infrastructure/Persistence/MarketplaceJobProcessor.cs)),
  ama kart yine yeşil "Bağlı ve Aktif" gösteriyor.
- Senaryo: bir STAGE HB bağlantısının testi zaman aşımına düşüyor. Ardından `IsOperationalStage` false oluyor
  ([IntegrationRuntimePolicy.cs:17-20](src/MarketplaceHub.Application/IntegrationRuntimePolicy.cs)) ve bütün manuel
  okuma/yazmalar reddediliyor. Kart ise yeşil kalıyor.
- Aynı şekilde, bütün akışlar OFFLINE olduğunda da kart hiçbir uyarı göstermiyor. Akış sağlığı yalnız detay sayfasındaki
  "Dikkat İsteyenler" bölümünde görünüyor.

**S2 — [Yüksek][Kesin] Gösterge panelinde aynı türdeki farklı işler birbirinin hatasını örtüyor.**
- `lastSuccess`, veri türündeki *tüm* job tiplerinin en yeni başarısı. Hata ise ancak `CreatedAt > lastSuccess` ise
  sayılıyor ([DashboardIntegrationMatrix.cs:51-58](src/MarketplaceHub.Infrastructure/Persistence/DashboardIntegrationMatrix.cs)).
- "orders" türünde `OrderSync`, `OrderRecoverySync`, `OrderStatusSync` ve `WebhookIngest` birlikte (satır 18).
- Senaryo: HB `HEPSIBURADA_ORDER_STATUS_SYNC` saat 10:00'da oluşturuluyor ve sürekli `RetryScheduled` / `Dead` durumunda
  kalıyor. HB iptal ve paket değişikliklerini yalnız bu tarama okuyor
  ([SynchronizationPolicies.cs:892-900](src/MarketplaceHub.Application/SynchronizationPolicies.cs)). 10:01'deki
  `ORDER_SYNC` başarısı onu gizliyor ve hücre "Güncel" kalıyor.
- Ayrıca `CreatedAt` (hata) ile `CompletedAt ?? CreatedAt` (başarı) karşılaştırılıyor. Uzun yeniden denenen bir işin
  `CreatedAt`'i sabit kaldığı için, gizlenme kalıcı oluyor.

**S3 — [Orta][Kesin] Uzun süre duran bir akış gösterge panelinde "İşlem Yok" (gri) olarak görünüyor.**
- Pencere 7 gün; 7 günden eski bir başarı yok sayılıyor ve `lastSuccess` null oluyor. Problem de yoksa durum "off"
  ([DashboardIntegrationMatrix.cs:11, 56](src/MarketplaceHub.Infrastructure/Persistence/DashboardIntegrationMatrix.cs);
  etiket "Son 7 günde işlem yok", [DashboardPage.tsx:39,68](src/MarketplaceHub.Web/src/features/dashboard/DashboardPage.tsx)).
- Senaryo: zamanlayıcı iş üretmeyi bırakıyor (politika kapalı, iş tablosu temizlendi, ya da bağlantı bootstrap'ta kaldı).
  Hücre önce 2 saat sonra "warn", 7 gün sonra nötr gri "İşlem Yok" oluyor. Kırmızıya hiç geçmiyor.

**S4 — [Orta][Kesin] Trendyol/Shopify webhook'u sağlıksızken ACTIVE (yeşil) kalıyor.**
- `healthy` hesaplanıyor ama yalnız mesajı değiştiriyor; `state` RemoteState'ten "ACTIVE" olarak kalıyor
  ([InstantNotificationService.cs:77-93](src/MarketplaceHub.Infrastructure/Persistence/InstantNotificationService.cs)).
- `webhookTone('ACTIVE')` 'ok' döndürüyor. Gösterge panelindeki hücre de "x dk önce" yazıyor
  ([integration-health.ts:12-14](src/MarketplaceHub.Web/src/features/dashboard/integration-health.ts)).
- HB'nin elle kurulan yolunda aynı durum QUIET (warn) oluyor (satır 62). Platformlar arasında tutarsızlık var.
- Senaryo: Trendyol'da sipariş geliyor ama bildirim gelmiyor (unnotified order). Zamanlayıcı yedek okumaya geçiyor;
  dashboard yeşil.

**S5 — [Orta][Kesin] Webhook durumu bilinmeyeni "düzenli geliyor" olarak gösteriyor.**
- Trendyol/Shopify'da `lastReceivedAt` null ve henüz sipariş yokken `IsHealthy` `registeredAt`'i işaret kabul ediyor.
  Mesaj "Bildirimler düzenli geliyor" oluyor (satır 91), oysa tek bir bildirim bile gelmedi.
- HB bu durumda WAITING ("İlk bildirim bekleniyor") gösteriyor. Proje kuralına (unknown stays unknown) aykırı.

**S6 — [Orta][Kesin] Reddedilen webhook uyarısı başarılı teslimlerden sonra kapanmıyor.**
- `webhook-rejected:{conn}:{code}` sorununu yalnız `NoteRejectedAsync` açıyor/güncelliyor
  ([MarketplaceWebhookService.cs:101-122](src/MarketplaceHub.Infrastructure/Persistence/MarketplaceWebhookService.cs)).
- Kabul edilen bir teslim bu sorunu çözmüyor (tüm kodda başka eşleşme yok). Yalnız 30 gün görülmeyince
  `ResolveStaleAsync` kapatıyor ([Worker.cs:607-619](src/MarketplaceHub.Worker/Worker.cs)).
- Sonuç: tek bir reddedilen teslim (eski adres, şifre değişimi sırasında gelen bir çağrı) 30 gün boyunca webhook'u
  "warn / Reddedilen Var" ve kırmızı `role=alert` metniyle gösteriyor
  ([IntegrationPages.tsx:410-411](src/MarketplaceHub.Web/src/features/marketplace/integrations/IntegrationPages.tsx),
  [instant-notification-status.ts:24](src/MarketplaceHub.Web/src/features/marketplace/instant-notification-status.ts)).
  api_security.md 1.3 saldırgan açısını anlatıyor; buradaki kusur meşru akışta da kapanmama.

**S7 — [Orta][Kesin] HB, N11 ve Pazarama'nın bazı akışları entegrasyon sayfasında görünmüyor.**
- Arka uç HB için QUESTIONS, FINANCE ve RETURN_LIFECYCLE; N11 ve Pazarama için FINANCE politikası oluşturup çalıştırıyor
  ([ScheduledJobProducer.cs:834-852, 633-635](src/MarketplaceHub.Infrastructure/Persistence/ScheduledJobProducer.cs)).
- Web'deki `supportsSyncPolicy` setlerinde bu türler yok
  ([marketplace-platform-support.ts:15-41, 97-104](src/MarketplaceHub.Web/src/features/marketplace/marketplace-platform-support.ts)).
  `visibleSyncPolicies` bunları süzüyor
  ([IntegrationPages.tsx:682](src/MarketplaceHub.Web/src/features/marketplace/integrations/IntegrationPages.tsx)).
- Sonuç: bu akışların OFFLINE/DEGRADED/STALLED durumu "Dikkat İsteyenler"e hiç düşmüyor; kapatıp açmak da mümkün değil.
  Finans hatası OperationalIssue olarak ayrıca açılıyor, HB soruları Sorular sayfasında görünüyor. Ama HB
  RETURN_LIFECYCLE (açık iade taraması) için hiçbir görünür sağlık yok.
- Not: "finance" kategorisinin açıklaması Trendyol'a özel yazılmış (satır 680). Görünür yapılırsa metin yanlış olur.

**S8 — [Orta][Kesin] Geçici bir okuma hatası kalıcı "yetenek yok" hatasına dönüşüyor.**
- N11 keşfinde sipariş sondası başarısız olursa ORDER_READ "UNKNOWN" yazılıyor
  ([N11HttpClient.cs:149-158](src/MarketplaceHub.Infrastructure/Adapters/N11/N11HttpClient.cs)). Hata zaman aşımı ya da
  429 bile olsa böyle.
- Test bunu `N11_REQUIRED_READ_CAPABILITY` / NotSupported hatasına çeviriyor
  ([MarketplaceJobProcessor.cs:3197-3204](src/MarketplaceHub.Infrastructure/Persistence/MarketplaceJobProcessor.cs)).
- Daha önce SUPPORTED olan yetenek satırı da `ApplyEvidence` ile UNKNOWN'a düşüyor (satır 3175-3194). Kart bunu
  göstermiyor (S1). Kurtarma testi 15 dk / 1 sa / 6 sa sonra düzeltebiliyor.

**S9 — [Düşük-Orta][Kesin] Pasif Trendyol/Shopify bağlantısında webhook "Açık" görünüyor.**
- CONNECTION_INACTIVE yalnız HB'nin elle kurulan yolunda var (satır 52-53).
- DISABLED bir Trendyol/Shopify bağlantısında abonelik ACTIVE kalıyor; bağlantı pasife alınınca silinmiyor
  ([MarketplaceConnectionService.cs:564-567](src/MarketplaceHub.Infrastructure/Persistence/MarketplaceConnectionService.cs)).
- Alıcı bu teslimleri 404 ile reddediyor (`ReceivingConnectionAsync` yalnız ACTIVE|VERIFIED kabul ediyor,
  [MarketplaceWebhookService.cs:90-95](src/MarketplaceHub.Infrastructure/Persistence/MarketplaceWebhookService.cs)).
- Sayfa ise yeni sipariş gelmediği için "Açık · Bildirimler düzenli geliyor; … 8 dakikada bir" diyor. Pasif bağlantıda
  hiçbir okuma çalışmadığı halde.

**S10 — [Düşük][Kesin] HB webhook durumu zamanlayıcının kuralıyla uyuşmuyor.**
- Panelin HB yolu yalnız `UnnotifiedOrderAsync`'e bakıyor (satır 60-62).
- Zamanlayıcı ve API Sağlık sayfası ise `IsHealthy` içinde son 24 saatteki WEBHOOK_HEALTH boşluklarını da sayıyor
  (`GapLimit=2`).
- Sonuç: HB webhook "Açık · Bildirimler geliyor; … 8 dakikada bir" görünürken API Sağlık sayfası WEBHOOK_FALLBACK
  gösterebiliyor. HB'ye boşluk yazılıp yazılmadığı ayrıca doğrulanmadı [Muhtemel].

**S11 — [Düşük][Kesin] "8 dakikada bir" ifadesi sabit yazılmış.**
- Yer: [InstantNotificationService.cs:68-69, 91](src/MarketplaceHub.Infrastructure/Persistence/InstantNotificationService.cs).
- Gerçek aralık `max(policy, 480 s)` ([SynchronizationPolicies.cs:915-916](src/MarketplaceHub.Application/SynchronizationPolicies.cs)).
  ORDERS politikası 15 dk olan bir mağazada metin yanlış oluyor.
- Sağlıksız dal ise `OrderReadMinutesAsync` ile politika aralığını gösteriyor. Ama sağlıksızken zamanlayıcının
  uyguladığı aralık bu olmayabilir (SyncCadencePolicy.EffectiveSeconds, ortam tabanı) [Muhtemel].

**S12 — [Düşük][Kesin] Shopify'da Trendyol metinleri gösteriliyor.**
- UNKNOWN mesajı her platformda "Trendyol'daki durum okunamadı" (satır 76).
- Etiketler PASSIVE için 'Trendyol durdurdu', MISSING için 'Trendyol’da kayıt yok'
  ([instant-notification-status.ts:8](src/MarketplaceHub.Web/src/features/marketplace/instant-notification-status.ts)).
  Shopify sayfasında ve dashboard tooltip'inde de bu etiketler görünüyor.

**S13 — [Düşük][Kesin] Akış sağlığı bağlantı durumunu dikkate almıyor.**
- `effectiveEnabled = x.Enabled && (yazma gerekmiyorsa || writesEnabled)`. Bağlantının DISABLED olması hesaba girmiyor
  ([MarketplaceConnectionService.cs:665](src/MarketplaceHub.Infrastructure/Persistence/MarketplaceConnectionService.cs)).
- Sonuç: pasif bir bağlantının detay sayfasında açık politikaların hepsi bir süre sonra "Güncelleme gecikti" olarak
  "Dikkat İsteyenler"e düşüyor ([IntegrationPages.tsx:761](src/MarketplaceHub.Web/src/features/marketplace/integrations/IntegrationPages.tsx)).
- Aynı sorun, zamanlayıcının hiç kuyruğa almadığı politikada da var: üretim dışı ortamda FINANCE
  ([ScheduledJobProducer.cs:105](src/MarketplaceHub.Infrastructure/Persistence/ScheduledJobProducer.cs)) sonsuza dek
  "İlk çalışma bekliyor" görünüyor.

**S14 — [Düşük][Kesin] Gösterge panelinde bazı sipariş işleri hiç sayılmıyor.**
- "orders" türünde `HepsiburadaWebhookIngest` yok; yalnız Trendyol ve Shopify ingest var
  ([DashboardIntegrationMatrix.cs:18](src/MarketplaceHub.Infrastructure/Persistence/DashboardIntegrationMatrix.cs)).
  HB bildirim işleme hataları hücreye yansımıyor.
- N11 uzlaştırma (OrderReconciliation) ve TRENDYOL_ORDER_CARGO_INFO_RECONCILIATION da hiçbir hücrede yok.

**S15 — [Düşük][Kesin] Kısmi okuma kayıtlarında dahi gerçek bir okuma olmadan "başarılı" sayılan işler var.**
- Kilit çakışmasında aynı türde bir iş zaten çalışıyorsa okuma ona katlanıyor ve iş `contention.Succeeded` döndürüyor
  ([MarketplaceJobProcessor.cs:71-90](src/MarketplaceHub.Infrastructure/Persistence/MarketplaceJobProcessor.cs)).
- Bu iş Succeeded kaydediliyor. Gösterge panelindeki `SuccessQuery` her Succeeded işi başarı sayıyor; çalışmayan bir
  okuma "Güncel" tazeliği üretebiliyor [Muhtemel; SyncCursor etkilenmiyor].

**S16 — [Düşük][Kesin] Başarısız test bağlantının sürümünü artırıyor.**
- Test hatasında `connection.Version++` var (satır 3166-3167, 3201, 3210). Arka plan kimlik hatası yazımı ise bilerek
  sürüm artırmıyor (satır 288-290 yorumu).
- 6 saatte bir çalışan otomatik kurtarma testi başarısız olduğunda, aynı anda ayar kaydeden kullanıcı 412 alıyor.

**S17 — [Düşük][Muhtemel] Keşifte artık listelenmeyen yetenek eski seviyesinde kalıyor.**
- `DiscoverCapabilitiesAsync`'in artık döndürmediği bir kod için satır güncellenmiyor ya da silinmiyor
  (döngü yalnız gelen kanıt üzerinden; satır 3175-3194).
- Eski SUPPORTED + VerifiedAt satırı `IsVerifiedWriteCapability` için geçerli kalabiliyor.

**S18 — [Düşük][Muhtemel] Geçmiş sipariş kurtarması webhook'u yanlışlıkla sağlıksız sayabilir.**
- `UnnotifiedOrderAsync`, `Orders.CreatedAt`'e (panelin ekleme zamanı) bakıyor
  ([ScheduledJobProducer.cs:371-377](src/MarketplaceHub.Infrastructure/Persistence/ScheduledJobProducer.cs)).
- ORDER_RECOVERY, tam sıfırlama sonrası yeniden aktarım ya da CSV içe aktarma eski siparişleri o an eklediğinde,
  bunlar "bildirimi gelmemiş sipariş" sayılıyor. Webhook sağlıksız görünüyor ve yoklama sıklaşıyor.

**S19 — [Düşük][Muhtemel] İade listesi her pazaryerinde ret seçeneğini gösteriyor.**
- Satır menüsü `actionRequired` olan her iadede "İade Ret Talebi"ni gösteriyor, platformdan bağımsız
  ([ReturnPages.tsx:157-158](src/MarketplaceHub.Web/src/features/marketplace/returns/ReturnPages.tsx)).
- N11 adaptörü ret için `N11_RETURN_ACTION_UNSUPPORTED` döndürüyor
  ([N11Returns.cs:71-74](src/MarketplaceHub.Infrastructure/Adapters/N11/N11Returns.cs)); PttAVM'de iade yazma yok.
- Diyalog `allowedActions`'a bakıyor olabilir. Sunucu tarafında N11 için izin listesinin hesaplandığı yer bulunamadı
  [Tahmin].

**S20 — [Düşük][Kesin] Pazarama dış yazma metinleri birbiriyle çelişiyor.**
- Otomatik grup açıklaması "Ürün açma ve iade kararları sonraki aşamalarda açılacak" diyor.
- Manuel grup açıklaması ise "iade onay/retlerini Pazarama’ya gönderir" diyor
  ([IntegrationPages.tsx:655-658](src/MarketplaceHub.Web/src/features/marketplace/integrations/IntegrationPages.tsx)).
- Adaptörde `updateRefund` ve `product/create` var (PazaramaReturns.cs, PazaramaProductWrites.cs).

**Kontrol edildi, kusur bulunmadı:**
- Akış sağlığı `LastSuccessAt` yaşına bakıyor. Hiç çalışmayan veya sürekli ertelenen bir akış en geç
  periyot + 15 dk içinde OFFLINE oluyor; durmuş imleç "flaglenmiyor" kusuru akış tablosunda yok.
- Hatalar başarılı koşuda sıfırlanıyor.
- Kimlik hatası kalıcı değil; otomatik kurtarma testi var.
- Devre kesicinin yarı açık sondası rate-limit kapısında doğru alınıyor; dört işleyici de `PassProbeGate` çağırıyor.
- Finans sorunları (Trendyol, HB, N11, Pazarama) başarıda `Resolved` yapılıyor
  ([TrendyolFinanceSyncJobHandler.cs:379-381](src/MarketplaceHub.Infrastructure/Jobs/Handlers/TrendyolFinanceSyncJobHandler.cs) vb.).
- UI'daki saat dilimi gösterimi doğru.

### Inferences
- Önerilen sıra:
  1. Karta akış sağlığı özeti ve auth dışı LastErrorCode için bir "Uyarı" rozeti eklemek (S1, S8).
  2. Gösterge panelinde başarı ve hatayı job tipi başına eşleştirmek, ya da doğrudan SyncCursor sağlığını kullanmak
     (S2, S3).
  3. Webhook için ACTIVE+!healthy durumunu QUIET'e eşlemek; başarılı teslimde `webhook-rejected` sorununu çözmek
     (S4-S6).
  4. Web politika setlerini arka uçla aynı listeden üretmek (S7).

### Gaps
- `Status` enum'larının DB'de metin mi sayı mı tutulduğu (IssueStatus, JobStatus) doğrulanmadı. SQL'lerde iki biçim
  de not edildi.
- WEBHOOK_HEALTH boşluklarının HB için yazılıp yazılmadığı izlenmedi (S10).

## 3. Pazaryeri başına yetenek matrisi (adaptör gerçeği ile UI iddiası)

### Takeaway
Adaptörler ve UI büyük ölçüde uyumlu. Sapmalar şunlar: N11 iade reddi ve PttAVM kargo/iade yazma yok ama UI genel
menüler gösteriyor; Pazarama metni çelişkili; HB/N11/Pazarama finans ve HB soru akışı UI'da gizli (S7). Webhook yalnız
Trendyol (API), HB (elle tanım) ve Shopify'da (API) var; N11, Pazarama ve PttAVM için "Desteklenmiyor".

### Cited Findings
Kaynak: `.claude/skills/marketplace-docs/*.md` uç nokta/iş türü tabloları, adaptör dosyaları ve
`ScheduledJobProducer.Supports*Policy`. ✓ = adaptörde uygulanmış, ✗ = yok / NotSupported, ~ = kısmi.

| Yetenek | Trendyol | Hepsiburada | N11 | Pazarama | PttAVM | Shopify | E-Faturam |
|---|---|---|---|---|---|---|---|
| Sipariş okuma | ✓ stream v2 | ✓ orders + packages | ✓ shipmentPackages | ✓ getOrdersForApi | ✓ SiparisKontrolListesiV2 | ✓ GraphQL orders | — |
| Sipariş durum / kargo yazma | ✓ paket durumu, split, unsupplied, common label | ✓ paket oluştur/çöz, kargo değiştir, etiket | ~ satır onayı, split, işçilik, collection | ✓ updateOrderStatus, split, iptal kararı | ✗ (kargo REST engelli; UI "satıcı panelinden" diyor) | ~ yalnız `fulfillmentTrackingInfoUpdate` (SHOPIFY_SHIPMENT_ACTION_UNSUPPORTED) | — |
| Stok yazma | ✓ | ✓ inventory-uploads | ✓ price-stock task | ✓ updateStock-v2 | ✓ UpdateProductsStockPrice | ✓ inventorySetQuantities | — |
| Fiyat yazma | ✓ | ✓ | ✓ | ✓ updatePrice-v2 | ✓ | ✓ productVariantsBulkUpdate | — |
| Ürün oluşturma/güncelleme | ✓ v2 products | ✓ import + ticket-api | ✓ create/update task | ✓ product/create (UI "sonraki aşama" diyor, S20) | ✓ UpdateProductsV3 | ✓ productSet | — |
| İade okuma | ✓ claims | ✓ claims | ✓ SOAP ReturnService (detay ✗) | ✓ getRefund (detay ✗) | ✓ salt okunur | ✓ return | — |
| İade karar yazma | ✓ approve + issue | ✓ accept/reject/preapproval | ~ yalnız APPROVE (N11_RETURN_ACTION_UNSUPPORTED) | ✓ updateRefund | ✗ | ✓ approve/decline | — |
| Sorular | ✓ okuma + cevap | ✓ issues (politika UI'da gizli, S7) | ✓ SOAP productService | ✓ | ✗ (politika yok) | ✗ | — |
| Fatura linki yükleme | ✓ seller-invoice-links | ✓ (+ okuma) | ✓ SaveLinkSellerInvoice (okuma ✗) | ✓ invoice-link (okuma ✗) | ✓ SaveInvoince | ✓ (IntegrationRuntimePolicy) | ✓ fatura kesme/iptal (sağlayıcı) |
| Finans / hakediş | ✓ settlements, otherfinancials | ✓ mpfinance (UI'da gizli) | ✓ SettlementService (UI'da gizli) | ✓ paymentAgreement (UI'da gizli) | ✗ | ✗ | — |
| Webhook | ✓ API ile kayıt + yeniden açma | ~ elle tanım (MANUAL) | ✗ UNSUPPORTED | ✗ | ✗ | ✓ API, konu başına abonelik | — |

- Webhook desteği: [InstantNotificationService.cs:45, 110](src/MarketplaceHub.Infrastructure/Persistence/InstantNotificationService.cs).
- Otomatik fatura teslim kapısı yalnız TRENDYOL, HEPSIBURADA ve SHOPIFY'ı sayıyor
  ([IntegrationRuntimePolicy.cs:55-73](src/MarketplaceHub.Application/IntegrationRuntimePolicy.cs)). N11, Pazarama ve
  PttAVM kendi `*Invoices.cs` kapılarını kullanıyor; okunmadı.
- Keşfedilen yetenek kodları da platforma göre değişiyor:
  - Trendyol: CONNECTION_TEST, ORDER_READ, REFERENCE_READ, PRODUCT_READ, RETURN_READ, QUESTION_READ, QUESTION_WRITE.
    Yazma yetenekleri keşfedilmiyor ve UI Trendyol yeteneklerini hiç yüklemiyor
    ([TrendyolHttpClient.cs:29-45](src/MarketplaceHub.Infrastructure/Adapters/Trendyol/TrendyolHttpClient.cs)).
  - N11: yalnız CONNECTION_TEST, PRODUCT_READ, ORDER_READ.

### Inferences
- UI'nın "desteklenir" gösterip adaptörün NotSupported döndüğü yerler: N11/PttAVM iade reddi menüsü (S19) ve Pazarama
  metinleri (S20).
- Tersi durum (adaptör destekliyor, UI gizliyor): HB/N11/Pazarama finans ve HB soru/açık iade akışları (S7).

### Gaps
- Her adaptör satırı uçtan uca çalıştırılmadı; matris kod ve belge tablolarından çıkarıldı.
- PttAVM ve Shopify için soru desteğinin olmadığı, politika listelerinin yokluğundan çıkarıldı [Muhtemel].

## 4. Canlıda salt okunur olarak doğrulanması gerekenler (`/prod-sql`, `BEGIN READ ONLY … ROLLBACK`)

### Takeaway
Aşağıdaki sorgular S1, S2, S3, S6, S7 ve S9'un canlıda gerçekleşip gerçekleşmediğini gösterir. Sütun adları EF
varsayılanı (PascalCase, tırnaklı) kabul edildi; önce `/prod-sql` ile tablo sütunlarını listeleyin.

### Cited Findings
- **S1** — aktif bağlantılarda kimlik dışı hata kodu:
  ```sql
  SELECT "PlatformCode","DisplayName","Environment","Status","LastErrorCode","LastTestedAt","LastSuccessAt"
  FROM "PlatformConnections"
  WHERE "Status" IN ('ACTIVE','VERIFIED') AND COALESCE("LastErrorCode",'') <> ''
  ORDER BY "PlatformCode";
  ```
- **Bayat ya da hatalı akışlar** (açık politika × imleç; politika eşlemesi `CursorResourceType` ile aynı):
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
- **S7** — UI'da gizlenen akışların durumu:
  ```sql
  SELECT c."PlatformCode", c."DisplayName", s."ResourceType", s."LastSuccessAt", s."ConsecutiveFailureCount", left(s."LastError",120)
  FROM "SyncCursors" s JOIN "PlatformConnections" c ON c."Id" = s."ConnectionId"
  WHERE (c."PlatformCode" = 'HEPSIBURADA' AND s."ResourceType" IN ('FINANCE','QUESTIONS','RETURN_LIFECYCLE'))
     OR (c."PlatformCode" IN ('N11','PAZARAMA') AND s."ResourceType" = 'FINANCE');
  ```
- **S2** — "orders" hücresinin gizlediği, sürekli hata veren sipariş durum/kurtarma işleri. JobStatus metin değilse
  sayısal değerleri kullanın:
  ```sql
  SELECT j."ConnectionId", j."JobType", j."Status", j."CreatedAt", j."AttemptCount", j."LastErrorCode"
  FROM "IntegrationJobs" j
  WHERE j."CreatedAt" > now() - interval '7 days'
    AND (j."JobType" LIKE '%ORDER_STATUS_SYNC' OR j."JobType" LIKE '%ORDER_RECOVERY_SYNC')
    AND j."Status" IN ('RetryScheduled','Dead','ManualReview','Blocked')
  ORDER BY j."CreatedAt" DESC LIMIT 50;
  ```
- **S6** — başarılı teslimden sonra açık kalan reddedilmiş webhook sorunu:
  ```sql
  SELECT i."DedupeKey", i."Status", i."LastSeenAt", w."LastReceivedAt"
  FROM "OperationalIssues" i
  JOIN "WebhookSubscriptions" w ON w."Status" = 'ACTIVE'
       AND i."DedupeKey" LIKE 'webhook-rejected:' || replace(w."ConnectionId"::text,'-','') || ':%'
  WHERE i."ResolvedAt" IS NULL AND w."LastReceivedAt" > i."LastSeenAt";
  ```
- **S9** — pasif bağlantıda açık kalan abonelik:
  ```sql
  SELECT c."PlatformCode", c."DisplayName", c."Status", w."ExternalSubscriptionId", w."VerifiedAt", w."LastReceivedAt"
  FROM "WebhookSubscriptions" w JOIN "PlatformConnections" c ON c."Id" = w."ConnectionId"
  WHERE w."Status" = 'ACTIVE' AND c."Status" NOT IN ('ACTIVE','VERIFIED');
  ```
- **S5** — hiç bildirim almamış Trendyol/Shopify abonelikleri:
  ```sql
  SELECT c."PlatformCode", c."DisplayName", w."VerifiedAt", w."LastReceivedAt"
  FROM "WebhookSubscriptions" w JOIN "PlatformConnections" c ON c."Id" = w."ConnectionId"
  WHERE w."Status" = 'ACTIVE' AND w."LastReceivedAt" IS NULL AND c."PlatformCode" IN ('TRENDYOL','SHOPIFY');
  ```
- **Devre kesici** (SQL değil): `/prod-logs logs worker --since 24h --grep LOCAL_CIRCUIT_OPEN`, ardından aynı
  sorguyu `api` için çalıştırın. Süreçler arası devre paylaşılmadığı için ikisinin farklı sonuç vermesi beklenir.

### Inferences
- İlk iki sorgu sıfır satır dönmüyorsa, S1 kullanıcıya yanlış "sağlıklı" bilgisi gösteriliyor demektir.

### Gaps
- Canlı erişim olmadığı için hiçbir sorgu çalıştırılmadı.
