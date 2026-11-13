# Domain ve Application katmanı iş mantığı denetimi (Ravencia Marketplace Hub)

Kapsam: `src/MarketplaceHub.Domain`, `src/MarketplaceHub.Application` (HEAD `3787b73`). Yalnız okuma yapıldı; repo ve canlı sunucuya dokunulmadı.
Kaynak gösterimi: dosya yolları repo köküne göredir (`src/...`). Ortamda `dotnet` kurulu değil (`~/.dotnet`, `/usr/share/dotnet` yok), bu yüzden `dotnet test` çalıştırılamadı.

Okunan dosyalar (tamamı satır satır): InvoiceAmounts, InvoiceIssuePolicy, InvoiceTotalsReconciliationPolicy, CatalogPricePolicy, ChannelOfferPricePolicy, IdempotencyPolicies, JobHandlers, JobPriorityPolicy, PriceInventorySendPolicy, OrderLineVariantMatching, SynchronizationPolicies (1459 satırın tamamı), EInvoiceRecipientPolicy, InvoiceSubmission/Cancellation outcome politikaları, MarketplaceInvoiceCreationPolicy, InvoiceDeliveryAutoRetryPolicy, InvoiceMarketplaceAutoDeliveryPolicy, InvoiceMarketplaceRetryPolicy, MarketplaceInvoiceReopenPolicy, JobWaitingPolicy, ShopifyOrderHistoryPolicy, ProductApprovalReconciliationPolicy, Domain: OrderModels (durum eşlemeleri), ReturnModels, InvoiceModels (durum makinesi), DashboardModels, CommissionTariffModels (bantlar), TurkishTaxIdentity, JobRetryPolicy. ReportsContracts yalnız record tanımları içeriyor (hesap mantığı Infrastructure/ReportBuilder'da, kapsam dışı).

## 1. Para / KDV / indirim hesapları hatalı yuvarlıyor veya topluyor mu?

### Takeaway
Fatura tutar motoru (`InvoiceAmounts.TryCalculatePackage`) tamamen `decimal`, `AwayFromZero` ile kuruşa yuvarlıyor ve kuruş farkını tek satıra yazıyor; çift indirim veya double kullanımı bulunmadı. Bulunan sorunlar kenar durumlar: iki ayrı 0,01 toleransın üst üste binmesi, sipariş toplamlarının iki farklı kaynaktan karışık alınması.

### Cited Findings
- **[Düşük][Kesin] Tolerans birikimi paketi gereksiz yere reddediyor.** `InvoiceAmounts.TryCalculatePackage` önce `|lineGrossTotal − grossTotal| ≤ 0,01` ve `|grossTotal − discount − payable| ≤ 0,01` kontrolünü yapıyor (satır 56-57), sonra satırların ödenecek toplamı ile paket toplamı arasındaki farkın en fazla 0,01 olmasını istiyor (satır 88-94). Satır toplamı `lineGrossTotal − discount` olduğu için iki 0,01 sapma aynı yönde olunca fark 0,02'ye çıkıyor ve paket "Yuvarlama sonrası fatura toplamı paket toplamıyla uyuşmuyor" hatasıyla reddediliyor. Örnek: satırlar 100,00, pazaryeri brüt 100,01, indirim 0, ödenecek 100,02 → ilk iki kontrol geçer, fark = 100,02 − 100,00 = 0,02 > 0,01 → ret. — [src/MarketplaceHub.Domain/InvoiceAmounts.cs:56-57, 88-94](src/MarketplaceHub.Domain/InvoiceAmounts.cs)
- **[Düşük][Muhtemel] `ResolveOrderTotals` brüt, indirim ve ödenecek tutarı farklı kaynaklardan karıştırabiliyor.** Brüt ve indirim, tüm aktif paketlerde `GrossAmount > 0` ise paketlerden geliyor; ödenecek tutar ise ancak tüm paketlerde `NetAmount > 0` ise paketlerden, değilse siparişten geliyor (satır 20-25). Örnek: biri iptal edilmiş iki paketli sipariş, aktif paketin NetAmount'u 0 (bilinmiyor), GrossAmount'u dolu → brüt yalnız aktif paketi, ödenecek tutar ise iptal edilen paket dahil tüm siparişi gösteriyor. Sonuç: `brüt − indirim ≠ ödenecek` olan bir toplam. — [src/MarketplaceHub.Domain/InvoiceAmounts.cs:14-26](src/MarketplaceHub.Domain/InvoiceAmounts.cs)
- **Kontrol edildi, hata yok:** satır birim fiyatı indirim sonrası net / adet, 4 basamağa yuvarlanıyor (satır 84). E-Faturam payload'ı birim fiyatı `(TaxableAmount + DiscountAmount)/Quantity` ile yeniden hesaplıyor, yani indirim iki kez düşülmüyor. — [src/MarketplaceHub.Infrastructure/Adapters/TrendyolEFaturam/Contracts/TrendyolEFaturamInvoicePayload.cs:35-43](src/MarketplaceHub.Infrastructure/Adapters/TrendyolEFaturam/Contracts/TrendyolEFaturamInvoicePayload.cs)
- **Kontrol edildi, hata yok:** `TurkishInvoiceNote` / `IntegerToTurkish` için 1.000, 1.500, 2.500, 100.000, 1.001.000, 1.002.000 ve 1.000.001.000 elle izlendi. Hepsi doğru çıktı (ör. "BİR MİLYAR BİN"). — [src/MarketplaceHub.Domain/InvoiceAmounts.cs:277-324](src/MarketplaceHub.Domain/InvoiceAmounts.cs)
- **Kontrol edildi, hata yok:** VKN ve TCKN kontrol hanesi algoritmaları standart algoritmayla birebir aynı. — [src/MarketplaceHub.Domain/TurkishTaxIdentity.cs:13-36](src/MarketplaceHub.Domain/TurkishTaxIdentity.cs)
- **Kontrol edildi, hata yok:** `ChannelOfferPricePolicy` `decimal` kullanıyor. Yönlü yuvarlama modları (`ToZero`, `ToPositiveInfinity`, `ToNegativeInfinity`) .NET Core 3+'ta doğrudan yönlü yuvarlama yapıyor. "UP" modu `ToPositiveInfinity`'ye eşlenmiş; Java'daki UP (sıfırdan uzağa) ile pozitif fiyatlarda aynı sonucu veriyor. — [src/MarketplaceHub.Application/ChannelOfferPricePolicy.cs:12-40](src/MarketplaceHub.Application/ChannelOfferPricePolicy.cs)
- **Kontrol edildi, hata yok:** `InvoiceTotalsReconciliationPolicy` tam eşitlik arıyor. `TryCalculatePackage` toplamları tam eşit ürettiği için bu doğru (TaxExclusive = Σ(payable − vat), satırlar 2 basamaklı). — [src/MarketplaceHub.Application/InvoiceTotalsReconciliationPolicy.cs:10-19](src/MarketplaceHub.Application/InvoiceTotalsReconciliationPolicy.cs)
- **Kontrol edildi, hata yok:** `CommissionTariffBands.BandFor` fiyatı kuruşa yuvarlıyor. Bant boşluğunda `null` dönüyor, yani komisyon tahmin edilmiyor. — [src/MarketplaceHub.Domain/CommissionTariffModels.cs:112-117](src/MarketplaceHub.Domain/CommissionTariffModels.cs)

### Inferences
- Para hesaplarında double/float kullanımı yok. Tek `double` olan `QuotaUsageView.AveragePerMinute` ve sync sağlık `Days` alanı gösterim amaçlı.

### Gaps
- Rapor rakamları (ReportBuilder, Infrastructure) bu katmanda değil, incelenmedi.
- Testler çalıştırılamadı (dotnet yok).

## 2. Tarih/saat mantığı İstanbul–UTC ayrımını yanlış mı işliyor?

### Takeaway
Application katmanındaki tarih hesapları `DateTimeOffset.ToOffset(+03:00)` ile doğru yapılıyor. Npgsql'e offset'i 0 olmayan bir değer yazma riski bu katmanda yalnız bir sabitte var. Asıl tarih hatası günlük zamanlama penceresinde: yorum ile kod çelişiyor.

### Cited Findings
- **[Düşük-Orta][Kesin] `DailyScheduleWindowPolicy` yorumla çelişiyor; kaçırılan pencere "hemen" değil, ancak bir sonraki gün koşuyor.** Yorum: "A policy that missed a whole window ... runs at once". Kod ise hemen çalıştırmayı yalnız `now − last ≥ interval + 1 gün` olduğunda yapıyor (satır 1165), yani günlük bir iş için son çalışmadan 48 saat sonra. Senaryo: iş Pazartesi 14:30'da çalıştı, worker Salı 14:00–17:00 arasında kapalıydı. Salı 18:00'de `now − last` = 27,5 saat < 48 saat ve pencere dışında → koşmaz. Çarşamba 14:xx'te koşar. Günlük işler (referans/finans senkronu) bir tam gün kayar. Testler (`BackgroundProcessingAuditTests.cs:261-267`) bu kenar durumu kapsamıyor. — [src/MarketplaceHub.Application/SynchronizationPolicies.cs:1144-1167](src/MarketplaceHub.Application/SynchronizationPolicies.cs)
- **[Düşük][Tahmin] `N11OrderHistoryPolicy.EarliestAvailable` offset'i +03:00.** `Start()` bu değeri olduğu gibi döndürüyor (satır 26, 31). Tek çağrı yeri olan `MarketplaceJobProcessor.cs:4045` değeri yalnız pencere üretmek için kullanıyor. Değer `timestamptz` olarak kaydedilirse "offset 0 only" kuralını çiğner. Kaydedildiğine dair kod görülmedi. — [src/MarketplaceHub.Application/SynchronizationPolicies.cs:26-32](src/MarketplaceHub.Application/SynchronizationPolicies.cs)
- **[Düşük][Kesin] Pazarama ve PTT AVM `StartDay` eski watermark için alt sınır koymuyor.** Fonksiyon `min(watermark − 2 gün, bugün − 2 gün)` döndürüyor. Örnek: 1 yıl önceki bir watermark, 3 aylık "InitialLookbackMonths" yerine 1 yıllık bir taramaya (13 adet 28 günlük pencere) yol açar. — [src/MarketplaceHub.Application/SynchronizationPolicies.cs:57-64, 90-97](src/MarketplaceHub.Application/SynchronizationPolicies.cs)
- **Bayat yorumlar (hata değil):** `ExternalEffectReconcilePolicy` ve `DailyScheduleWindowPolicy` yorumları "worker 00:30-09:00 durduruluyor" diyor. CLAUDE.md'ye göre gece duruşu 27 Ekim 2026'dan beri yok. — [src/MarketplaceHub.Application/SynchronizationPolicies.cs:1086-1088, 1144-1146](src/MarketplaceHub.Application/SynchronizationPolicies.cs)
- **Kontrol edildi, hata yok:** `HepsiburadaReturnHistoryPolicy.Hour()` UTC'ye çevirerek saat sınırını buluyor. `ListingVerificationPolicy` ve `PazaramaOpenOrderPolicy` İstanbul gününü `ToOffset(+3).Date` ile alıyor. `PriceInventorySendPolicy.RecordSent` `ToUniversalTime()` uyguluyor. — [src/MarketplaceHub.Application/PriceInventorySendPolicy.cs:84](src/MarketplaceHub.Application/PriceInventorySendPolicy.cs)

### Inferences
- Hepsiburada'nın İstanbul yerel saatleri Infrastructure'daki eşleyicide (`HepsiburadaJsonMapper.QueryTime`) işleniyor. Application katmanında İstanbul saatini UTC sanan bir hesap bulunmadı.

### Gaps
- Infrastructure'daki `TurkeyTime.cs` ve eşleyiciler kapsam dışı kaldı.

## 3. Hangi politikalar eksik bir alandan pazaryeri durumu çıkarıyor? (kural: bilinmeyen bilinmeyen kalır)

### Takeaway
Üç yerde bilinmeyen değer bilinen bir duruma çevriliyor: KDV oranı kontrolü, iade durum eşlemesi ve iade satırının "aksiyon gerekli" sayılması. Bunlardan ilki mali belgeye yansıyabildiği için en önemlisi.

### Cited Findings
- **[Orta][Kesin] `InvoiceIssuePolicy.IsLineVatRateUnknown` ham satır verisi eksik, boş `{}` veya bozuk JSON olduğunda `false` ("biliniyor") döndürüyor.** İlgili satırlar: 99, 104 ve 107-110 (`catch` → `false`). Bu durumda satır veritabanında hangi oranla tutuluyorsa (0% dahil) o oranla faturalanıyor. Örnek: SourceSnapshotJson'u `null` olan bir Trendyol satırı, VatRate=0 ile kayıtlı → kontrol geçer → %0 KDV'li e-Arşiv kesilir. Davranış dokümante edilmiş ("Lines without a raw snapshot ... carry the rate they were stored with (0 included)"), ama "bilinmeyen bilinmeyen kalır" kuralıyla çelişiyor. Çağrı yerleri: `InvoicingBillingService.cs:1168, 1330`, `InvoicingWorkspaceWorkflow.cs:366`. Ek olarak `VatRatePaths` platform kodunu büyük/küçük harf duyarlı ve `Trim`siz eşliyor (satır 82-89). "Trendyol" yazımı gelirse kontrol tamamen atlanır. — [src/MarketplaceHub.Application/InvoiceIssuePolicy.cs:82-111](src/MarketplaceHub.Application/InvoiceIssuePolicy.cs)
- **[Orta][Kesin] `MarketplaceReturnStatus.Canonicalize` tanımadığı her ham durumu `ActionRequired`'a eşliyor** (satır `_ => ReturnClaimStatus.ActionRequired`). Test bunu bilerek doğruluyor: `ReturnLifecyclePolicyTests.cs:56` "unknown-provider-status" → ActionRequired. Bilinmeyen durum, operatöre "karar verin" diye gösteriliyor (dashboard `PendingReturnStatuses = [ActionRequired]`). Bu, kapanmış bir iadeye onay/ret gönderilmesine yol açabilir. Aynı mantıkla `ReturnLineStatusPolicy.IsActionable(null)` → `true`. — [src/MarketplaceHub.Domain/ReturnModels.cs:18-40, 48-49](src/MarketplaceHub.Domain/ReturnModels.cs)
- **[Düşük][Tahmin] `ShipmentPackageStatusPolicy.Aggregate([])` → `New`.** `StatusSources` hiç güncel paket döndürmezse (ör. yalnız placeholder ve süpersede edilmiş paketler) sipariş "Yeni" görünür. Boş liste için ayrıca bir koruma var mı, çağıran kodda doğrulanmadı. — [src/MarketplaceHub.Domain/OrderModels.cs:323-326](src/MarketplaceHub.Domain/OrderModels.cs)
- **[Düşük][Kesin] `MarketplaceInvoiceCreationPolicy.IsEnabled` ayar anahtarı yoksa Shopify dışı pazaryerlerinde faturalamayı açık sayıyor** (satır 20 ve 30: `return !shopify`). Aynı dosyadaki yorum N11/Pazarama'nın varsayılanının `false` olduğunu söylüyor. Ayar satırı eksik veya göç edilmemiş bir N11/Pazarama bağlantısı mali faturalamaya açık başlar. Bu, `AutomaticPriceStockWriteGate` içindeki "olmayan satır kapalı sayılır" ilkesiyle de tutarsız. — [src/MarketplaceHub.Application/MarketplaceInvoiceCreationPolicy.cs:12-36](src/MarketplaceHub.Application/MarketplaceInvoiceCreationPolicy.cs)
- **Doğru uygulananlar:** `MarketplaceInvoiceStatePolicy.FromRemote` fatura numarası veya linkini "faturalandı" kanıtı saymıyor, bilinmeyen için `Unknown` döndürüyor. `EInvoiceRecipientPolicy.Decide` bilinmeyen mükellef durumunda faturayı durduruyor. `CommissionTariffBands.BandFor` boşlukta `null` döndürüyor. `AutomaticPriceStockWriteGate.IsOpen` yalnız açık `true` ile açılıyor. — [src/MarketplaceHub.Domain/OrderModels.cs:48-66](src/MarketplaceHub.Domain/OrderModels.cs); [src/MarketplaceHub.Application/EInvoiceRecipientPolicy.cs:56-71](src/MarketplaceHub.Application/EInvoiceRecipientPolicy.cs)

### Inferences
- KDV bulgusu fatura doğruluğunu doğrudan etkileyebilir. Ham verisi olmayan satırların canlıda ne kadar yaygın olduğu `/prod-sql` ile sayılmalı (bu denetimde yapılmadı).

### Gaps
- Canlıda `SourceSnapshotJson IS NULL` olan satır sayısı ölçülmedi (canlıya dokunulmadı).

## 4. Durum makineleri geçersiz geçişlere izin veriyor mu, durum atlıyor mu?

### Takeaway
Paket, iade ve fatura durum makineleri genel olarak tutarlı. Sorunlar: aynı rank tablosunun iki kopyası, Hepsiburada'da "Undelivered"ın OnHold'a eşlenip ileri durumdan geri dönememesi, dizgeye göre duyarsız bir "İHRAC" eşlemesi ve iade makinesinde kullanılmayan (ölü) Cancelled geçişleri.

### Cited Findings
- **[Düşük-Orta][Kesin] Hepsiburada lifecycle'ında "UNDELIVERED", generic politikadaki `Undelivered` (rank 70) yerine `OnHold`'a (rank 40) eşleniyor** (satır 257). `Reconcile` yalnız rank'ı ≥ mevcut olanı uyguluyor (satır 295). Bu yüzden Shipped (60) olan bir paket lifecycle okumasında "Undelivered" gelse de Shipped kalıyor. Yorum, teslim ve teslim edilemedi durumları için paket durumunun kaynak olduğunu söylüyor. Paket akışı bunu yakalamazsa sipariş "Kargoda" takılı kalır. — [src/MarketplaceHub.Domain/OrderModels.cs:250-296](src/MarketplaceHub.Domain/OrderModels.cs)
- **[Düşük][Kesin] Aynı rank tablosu iki kopya halinde duruyor:** `ShipmentPackageStateMachine.ProgressRank` (satır 158-173) ve `ShipmentPackageStatusPolicy.Rank` (satır 345-360). Biri değişip diğeri değişmezse geçiş kontrolü ile toplu durum hesabı birbirinden ayrılır. — [src/MarketplaceHub.Domain/OrderModels.cs:158-173, 345-360](src/MarketplaceHub.Domain/OrderModels.cs)
- **[Düşük][Kesin] `ReturnClaimStateMachine`'deki Cancelled geçişleri pratikte ölü kod.** `MarketplaceJobProcessor.cs:12262` hedef durum Cancelled olduğunda (`ShouldPersist == false`) durum makinesine hiç girmiyor, `CancelledReturnClaimCleanup`'a gidiyor. Buna karşın tablo `Requested/AwaitingShipment → Cancelled`'a izin veriyor, `ActionRequired/InTransit → Cancelled`'a izin vermiyor. Kurallar çelişkili görünse de etkisiz. Ek olarak `ActionRequired → InTransit` reddediliyor ve bu geçiş "authoritative" da değil: ham durum güncelleniyor ama statü ActionRequired kalıyor ve `RETURN_STATUS_TRANSITION_REFUSED` issue'su açılıyor (satır 12322-12341). — [src/MarketplaceHub.Domain/ReturnModels.cs:91-117](src/MarketplaceHub.Domain/ReturnModels.cs)
- **[Düşük][Kesin, etkisi Tahmin] `InvoiceIssuePolicy.IsMicroExport`, `Contains("İHRAC", OrdinalIgnoreCase)` kullanıyor.** Ordinal karşılaştırma Türkçe İ/i eşlemesi yapmıyor. "ihracat" veya ASCII "IHRACAT" içeren bir `orderType`/`shipmentPackageType` yakalanmaz. Mikro ihracat siparişine yurt içi e-Arşiv kesilebilir. Pazaryerinin bu dizgeleri gönderip göndermediği doğrulanmadı. — [src/MarketplaceHub.Application/InvoiceIssuePolicy.cs:145-148](src/MarketplaceHub.Application/InvoiceIssuePolicy.cs)
- **[Düşük][Tahmin] `MarketplaceInvoiceStatePolicy.ShouldApply` kaynak zamanı olmayan yeni bir okumayı, kaynak zamanı olan mevcut duruma uyguluyor** (yalnız `incomingObservedAt ≥ currentObservedAt` ise, satır 77). Zaman damgası taşımayan kısmi veya önbellekli bir yanıt, daha yeni bir pazaryeri durumunu ezebilir. — [src/MarketplaceHub.Domain/OrderModels.cs:68-83](src/MarketplaceHub.Domain/OrderModels.cs)
- **Kontrol edildi, hata yok:** `InvoiceStateMachine` terminal durumlardan (Cancelled, CancelledLocal, Rejected) çıkışa izin vermiyor. `ShipmentPackageStateMachine` Cancelled ve Returned'ı değişmez tutuyor, ileri atlamalarda yalnız tek yönlü (monoton) ilerlemeye izin veriyor. — [src/MarketplaceHub.Domain/InvoiceModels.cs:25-51](src/MarketplaceHub.Domain/InvoiceModels.cs)

### Inferences
- İade iptali akışı durum makinesi dışında yönetildiği için, Cancelled ile ilgili tablo kuralları yanıltıcı ama zararsız.

### Gaps
- Hepsiburada paket akışının Undelivered'ı ayrıca işleyip işlemediği Infrastructure'da doğrulanmadı.

## 5. Diğer politikalar (idempotency, eşleme, stok) ve testler

### Takeaway
Idempotency ve fiyat-stok gönderim politikalarında doğrudan bir hata bulunmadı. Küçük kenar durumlar var: boş hbSku ile varyant eşleme ve stok içe aktarımının hesaba katmadığı bir hareket türü. Testler bazı tartışmalı davranışları (bilinmeyen iade durumu → ActionRequired) bilerek doğruluyor.

### Cited Findings
- **[Düşük][Kesin] `OrderLineVariantMatcher.Codes`: `Text(root,"hbSku") ?? Text(root,"sku")`.** hbSku boş dizge ("") ya da yalnız boşluksa `null` olmadığı için "sku"ya düşülmüyor. MarketplaceVariantId ""/" " oluyor ve 1. adım (hbSku eşlemesi) boşa gidiyor. Ayrıca `Add()` kırpıyor, MarketplaceVariantId ise kırpılmıyor. — [src/MarketplaceHub.Application/OrderLineVariantMatching.cs:59](src/MarketplaceHub.Application/OrderLineVariantMatching.cs)
- **[Düşük][Muhtemel] `MarketplaceStockImportPolicy.OnHandMovementTypes` = ORDER_RECEIVED, MANUAL_ADJUSTMENT, RETURN_PASS.** Okumadan sonra eldeki stoğu düşüren `ORDER_SHIPMENT_PROGRESS` hareketi (`QuantityDelta = -shipmentDelta`, `MarketplaceJobProcessor.cs:11002-11003`) listede yok. Okuma ile yazma arasında bir sevkiyat ilerlemesi kaydedilirse içe aktarılan on-hand o kadar fazla çıkar. Yorumdaki "return restock" yalnız RETURN_PASS ile karşılanıyor. — [src/MarketplaceHub.Application/SynchronizationPolicies.cs:454-461](src/MarketplaceHub.Application/SynchronizationPolicies.cs)
- **[Düşük][Kesin] Ölü veya yanıltıcı öğeler:**
  - `InvoiceIssuePolicy.Refusal(invoiceType, …)`, `invoiceType` parametresini kullanmıyor (satır 45-49).
  - `JobPriorityPolicy`'de `<param name="userInitiated">` XML yorumu, bu parametresi olmayan `IsUserAction`'a yapışmış (satır 27-31).
  - `WebhookPollingPolicy.SilenceLimit` (6 saat) `IsHealthy` içinde kullanılmıyor (satır 885, 921-926).
  - `OrderInventoryStockPolicy.OnHandDelta` shortfall'u hesaba katmıyor; yeni `Plan()` ile tutarsız, olası eski (legacy) kod (satır 393-399).
  — [src/MarketplaceHub.Application/SynchronizationPolicies.cs](src/MarketplaceHub.Application/SynchronizationPolicies.cs)
- **[Düşük][Tahmin] `ShopifyOrderHistoryPolicy.ModifiedAfter`: baseline dışı bir koşuda watermark `null`, lastSuccessAt dolu ise `null` döndürüyor** (satır 22-23). Bu, filtresiz (tüm geçmiş) bir okuma anlamına gelebilir. — [src/MarketplaceHub.Application/ShopifyOrderHistoryPolicy.cs:10-24](src/MarketplaceHub.Application/ShopifyOrderHistoryPolicy.cs)
- **Kontrol edildi, hata yok:**
  - `ApiIdempotencyPolicy` önce hash uyuşmazlığına bakıyor (KeyReused).
  - `PriceInventoryOutboxPolicy.HoldsDedupKey` ve `AutomaticSendMayRetire` mantığı tutarlı.
  - `MatchesLastSent` operatör önceliği doğru (`PriceOmitted || (A && B)`).
  - `JobRetryPolicy` jitter'ı tüm GUID'in hash'inden alınıyor.
  - `InvoiceDeliveryAutoRetryPolicy.DelayAfter` 1/3/6/12 saat, ardından `null`.
  — [src/MarketplaceHub.Application/PriceInventorySendPolicy.cs:61-64](src/MarketplaceHub.Application/PriceInventorySendPolicy.cs)
- **Testler:**
  - `dotnet` ortamda olmadığı için test suite çalıştırılamadı.
  - `ReturnLifecyclePolicyTests.cs:56`, bilinmeyen sağlayıcı durumunu ActionRequired olarak doğruluyor; "bilinmeyen bilinmeyen kalır" kuralına aykırı davranışı sabitliyor.
  - `BackgroundProcessingAuditTests.cs:261-267` DailyScheduleWindow'un "kaçırılan pencere" durumunu test etmiyor.
  - `InvoiceAmountsTests.cs` mevcut. Çift 0,01 tolerans birikimi için bir test bulunmadı (dosya ayrıntılı okunmadı). — tests/MarketplaceHub.Application.Tests/

### Inferences
- En yüksek öncelikli düzeltme adayları (azalan sırayla):
  1. KDV oranı bilinmeyen satırın ham verisi yoksa "biliniyor" sayılması.
  2. Bilinmeyen iade durumunun ActionRequired'a eşlenmesi.
  3. Ayar yokken N11/Pazarama faturalamasının açık sayılması.
  4. DailyScheduleWindow'un yorumla çelişmesi.

### Gaps
- Infrastructure (`MarketplaceJobProcessor`, `ReportBuilder`, adaptörler) yalnız çağrı yerlerini doğrulamak için açıldı. Oradaki iş mantığı bu denetimin kapsamında değil.
- Canlı veriyle sayısal doğrulama yapılmadı (salt okunur kısıt ve kapsam gereği).
