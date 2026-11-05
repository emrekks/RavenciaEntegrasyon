# Faturalama ve İade Akışları: Uçtan Uca Hata Denetimi

Kapsam: `InvoicingJobProcessor.cs` (tamamı), `InvoicingBillingService.cs` (taslak, doğrulama, kuyruklama, iptal, teslim, yeniden deneme), `InvoicingWorkspaceWorkflow.cs` (onay, yeniden taslak, iptal sonrası değiştirme), `src/MarketplaceHub.Application` altındaki `Invoice*` politikaları, `InvoiceStateMachine`, `InvoiceAdjustmentTracking`, `MarketplaceInvoiceReopen`, `ReturnModels.cs`, `HepsiburadaReturnActionPolicy`, `MarketplaceSalesService.ProcessReturnActionInstantAsync` / `ApplyDispositionAsync`, `ReturnPendingStock`, `CancelledReturnClaimCleanup`, `ReturnMarketplaceAmounts`. Commit `d4cb6ed` (5 Kas 2026). Yalnız kod okundu. Canlı sunucuya erişilmedi, test çalıştırılmadı.

Önceki denetimde (`research_notes/Ravencia proje hata denetimi/`) yazılmış bulgular burada tekrarlanmadı, yalnız atıf yapıldı. Bunlar: `IsLineVatRateUnknown`, `MarketplaceReturnStatus.Canonicalize` → ActionRequired, `MarketplaceInvoiceCreationPolicy.IsEnabled` varsayılanı, `invoiceDateTime: null` çökmesi, ReturnClaimStateMachine'deki ölü Cancelled geçişleri, E-Faturam `x-request-id` sorunu ve İHRAC eşlemesi.

Ciddiyet: Kritik / Yüksek / Orta / Düşük. Güven: [Kesin] = kod yolu uçtan uca izlendi; [Muhtemel] = kod kesin, tetiklenme bir pazaryeri davranışına bağlı; [Tahmin] = doğrulanmadı.

## 1. Çift fatura, iptal veya iade edilmiş siparişe fatura, kısmi iade ve paket tutarları

### Takeaway
Çift mali faturaya karşı korumalar güçlü. Siparişe tek SALE kaydı izni veren benzersiz indeks, aynı faturanın her denemede aynı ETTN/UUID ile gönderilmesi ve belirsiz sonucun yalnız okumayla çözülmesi bunu sağlıyor. Bu yüzden "yeniden deneme veya yeniden açma sonrası ikinci e-Arşiv" yolu bulunmadı. Asıl açık şurada: taslak oluşturulurken uygulanan sipariş ve paket kapıları (iptal edilen satır, Returned/Undelivered/PaymentPending paket) worker'daki `Submit` anında yeniden uygulanmıyor. Ayrıca tek bir iade satırı onaylandığında bile faturanın tamamı "düzeltme gerekli" durumuna geçip pazaryeri iletimi kalıcı olarak kesiliyor.

### Cited Findings
- **[Orta][Kesin kod / Muhtemel tetiklenme] Taslak kapıları `Submit` anında yeniden uygulanmıyor. Taslaktan sonra iptal edilen satır veya iade/teslim edilemedi durumuna geçen paket yine faturalanıyor.**
  - Taslak oluşturulurken şunlar reddediliyor: `IsSaleInvoiceBlockedOrder` / `IsSaleInvoiceBlockedPackage` (CANCELLED, RETURNED, UNDELIVERED, PAYMENT_PENDING) ve satır adedi `OrderedQuantity − CancelledQuantity`. — [InvoicingBillingService.cs:1086-1112, 1229](src/MarketplaceHub.Infrastructure/Persistence/InvoicingBillingService.cs), [DashboardModels.cs:40-46](src/MarketplaceHub.Domain/DashboardModels.cs)
  - `EnqueueSubmitAsync` yalnız `SaleIssueRefusalAsync` çağırıyor. Bu da yalnız alıcı/para birimi/mükellef kontrolü yapıyor. — [InvoicingBillingService.cs:1395-1396, 1977-1988](src/MarketplaceHub.Infrastructure/Persistence/InvoicingBillingService.cs)
  - Worker'daki `Submit` yalnız `Status != Cancelled` paketleri okuyor ve yalnız tüm paketler iptal ise duruyor. Satırları taslaktaki donmuş hâliyle (`InvoiceLines`) gönderiyor. — [InvoicingJobProcessor.cs:136, 172-176, 271](src/MarketplaceHub.Infrastructure/Persistence/InvoicingJobProcessor.cs)
  - Senaryo: Toplu onayda 2 ürünlü sipariş için taslak + Submit kuyruğa alınıyor (bulk öncelik veya `EFATURAM_RATE_LIMITED` ile `RetryScheduled`). Bu arada müşteri bir ürünü iptal ediyor (`CancelledQuantity=1`) ya da paket `Undelivered` oluyor. Retry geldiğinde e-Arşiv iki ürünle ve tam tutarla kesiliyor.
  - Çalışma alanının "değişen taslak yeniden kurulur" koruması (`UnsentDraftDriftAsync`) yalnız sağlayıcı öncesi reddedilmiş (`CanRetryPreProviderFailure`) faturada çalışıyor. Kuyruktaki iş için çalışmıyor. — [InvoicingWorkspaceWorkflow.cs:113-139](src/MarketplaceHub.Infrastructure/Persistence/InvoicingWorkspaceWorkflow.cs)
- **[Yüksek][Kesin] Tek satırlık kısmi iade onayı bile faturanın tamamını `AdjustmentRequired` durumuna taşıyor ve pazaryeri iletimini kalıcı olarak kesiyor.**
  - `FlagAdjustmentsAsync`, siparişte Approved/Completed durumunda *herhangi bir* `ReturnClaim` varsa Accepted, MarketplacePending veya Completed faturayı `AdjustmentRequired` yapıyor. Satır, adet ya da tutar karşılaştırması yok. — [InvoiceAdjustmentTracking.cs:27-49](src/MarketplaceHub.Infrastructure/Persistence/InvoiceAdjustmentTracking.cs)
  - `InvoiceBlocksDelivery(AdjustmentRequired) == true`. Teslim işi "fatura düzeltme bekliyor" diye bloklanıyor. — [InvoicingContracts.cs:263-265](src/MarketplaceHub.Application/InvoicingContracts.cs), [InvoicingJobProcessor.cs:877-879](src/MarketplaceHub.Infrastructure/Persistence/InvoicingJobProcessor.cs)
  - Durum makinesinde `AdjustmentRequired → {CancellationPending, ManualReview}` var. ManualReview'dan çıkış yalnız `Submitting`, o da `ExternalReference` dolu olduğu için `EnqueueSubmit` tarafından reddediliyor. — [InvoiceModels.cs:30-46](src/MarketplaceHub.Domain/InvoiceModels.cs)
  - Senaryo: 3 ürünlü Trendyol siparişinin e-Arşivi kesilmiş, link teslimi sürüyor (MarketplacePending) veya henüz kuyrukta (Accepted). Müşteri 1 ürünü iade ediyor, claim satırı Approved oluyor. Yarım saatlik `ScanDue` faturayı AdjustmentRequired yapıyor ve teslim duruyor. Kalan 2 ürünün faturası pazaryerine hiç ulaşmıyor. Panelin önerdiği tek çıkış tüm e-Arşivi iptal etmek. İade faturası akışı da kapalı (`AdjustmentFlowUnavailable`).
- **[Orta][Kesin] Mali olarak kesilmiş ama `MarketplaceFailed` veya `ManualReview` durumundaki fatura sipariş iptal/iade edildiğinde işaretlenmiyor ve panelden iptal edilemiyor.**
  - `FlagAdjustmentsAsync` yalnız Accepted, MarketplacePending ve Completed durumlarına bakıyor. Durum makinesi de `MarketplaceFailed`/`ManualReview → AdjustmentRequired` geçişine izin vermiyor. — [InvoiceAdjustmentTracking.cs:32, 41](src/MarketplaceHub.Infrastructure/Persistence/InvoiceAdjustmentTracking.cs)
  - `InvoiceCancellationOutcomePolicy.CanRequest` yalnız Accepted, Completed, AdjustmentRequired, CancellationRejected ve (iş yokken) CancellationPending durumlarını kabul ediyor. — [InvoiceCancellationOutcomePolicy.cs:28-30](src/MarketplaceHub.Application/InvoiceCancellationOutcomePolicy.cs)
  - Senaryo: Teslim ön okumasında pazaryerinde başka bir fatura görülüyor (`PLATFORM_INVOICE_ALREADY_PRESENT` → ManualReview, [InvoicingJobProcessor.cs:1240-1248](src/MarketplaceHub.Infrastructure/Persistence/InvoicingJobProcessor.cs)). Bu durumda bizim e-Arşivimiz büyük olasılıkla *ikinci* mali belge. Ancak panelde "İptal" eylemi yok ve iade/iptal izleme kaydı açılmıyor. Tek çıkış portaldan iptal edip "Portalda İptal Ettim, Yeni Fatura Kes" demek ([InvoicingWorkspaceWorkflow.cs:628-652](src/MarketplaceHub.Infrastructure/Persistence/InvoicingWorkspaceWorkflow.cs)).
- **[Orta][Muhtemel] Teslim hedefleri "güncel paket" kuralını kullanmıyor. Yerine geçilmiş paketler (Trendyol "0" yer tutucu, yeniden gönderimin orijinali) hedefe giriyor.**
  - Taslak ve Submit ön kontrolü `MarketplaceSalesService.CurrentShipmentPackages` kullanıyor. Bu yer tutucuyu ve `OriginExternalPackageId` ile yerine geçilmiş paketi dışarıda bırakıyor. — [MarketplaceSalesService.cs:28-39](src/MarketplaceHub.Infrastructure/Persistence/MarketplaceSalesService.cs), [InvoicingJobProcessor.cs:172](src/MarketplaceHub.Infrastructure/Persistence/InvoicingJobProcessor.cs)
  - Teslim ise ham `ShipmentPackages ... Status != Cancelled` ile hedef seçiyor. Bunu yapan yerler: `Deliver`, `SetInvoiceDeliveryOutcome`, `QueueMarketplaceDeliveryAfterDocument`, `EnqueueDeliveryAsync`, never-queued kurtarma ve `ConfirmMarketplaceDeliveredAsync`. — [InvoicingJobProcessor.cs:585-587, 1747-1749, 1808-1812, 2078-2080, 2103-2107](src/MarketplaceHub.Infrastructure/Persistence/InvoicingJobProcessor.cs), [InvoicingBillingService.cs:1499-1503](src/MarketplaceHub.Infrastructure/Persistence/InvoicingBillingService.cs)
  - Kod içindeki notta yer tutucunun durumunun hiç güncellenmediği yazıyor ("live 11763229493 kept Awaiting next to its delivered package"). PaymentPending durumu Cancelled değil. — [OrderModels.cs:436-441](src/MarketplaceHub.Domain/OrderModels.cs)
  - Senaryo: Trendyol siparişinde `"0"` yer tutucu (PaymentPending) ve gerçek paket var. Fatura Accepted olunca `Deliver` paketleri Id (Guid v7, oluşturma) sırasıyla dolaşıyor ve önce `"0"` için `QueryDeliveryAsync` çağırıyor. Trendyol v2'de bu id bulunamazsa ön okuma hatası oluşuyor ve döngü ilk pakette kırılıyor (`if (!succeeded) return false` / throw). Gerçek pakete link gitmiyor. Gitse bile `AllTargetsConfirmed` yer tutucuyu beklediği için fatura Completed olmuyor.
- **[Düşük][Kesin] `EnqueueReconcileAsync` API'si MarketplacePending ve MarketplaceFailed durumundaki faturayı da uzlaştırmaya alıyor. `Reconcile`, sağlayıcı ACCEPTED dediğinde durumu koşulsuz `Accepted` yapıyor ve bu bir durum gerilemesi.** — [InvoicingBillingService.cs:1483, 1759-1761](src/MarketplaceHub.Infrastructure/Persistence/InvoicingBillingService.cs), [InvoicingJobProcessor.cs:533-537](src/MarketplaceHub.Infrastructure/Persistence/InvoicingJobProcessor.cs)
  - Senaryo: Teslimi UNKNOWN durumunda bekleyen MarketplacePending bir fatura için `POST /invoices/{id}/reconcile-jobs` çağrılıyor. Fatura Accepted'a dönüyor. Bu durumda 5 dakikalık kurtarma (MarketplacePending/Failed arıyor), never-queued kurtarma (teslim durumu olmayanı arıyor) ve otomatik yeniden deneme (`Need` → UNKNOWN durumunda None) faturayı bir daha ele almıyor.
  - Panel arayüzü RECONCILE düğmesini yalnız UnknownResult/Submitted için gösteriyor ([InvoicingBillingService.cs:1623](src/MarketplaceHub.Infrastructure/Persistence/InvoicingBillingService.cs)). Web kodunda bu uç noktaya doğrudan çağrı bulunmadı, yani tetiklenme yalnız API üzerinden mümkün.
- **Kontrol edildi, çift fatura yolu bulunmadı:**
  - (a) Siparişe tek SALE kaydı, durumdan bağımsız bir sayımla ve `IX_invoices_TenantId_OrderId_ActiveSaleFiscal` indeksiyle korunuyor. — [InvoicingBillingService.cs:1074-1080, 1264-1271](src/MarketplaceHub.Infrastructure/Persistence/InvoicingBillingService.cs), [AppDbContextModelSnapshot.cs:2625-2628](src/MarketplaceHub.Infrastructure/Persistence/Migrations/AppDbContextModelSnapshot.cs)
  - (b) UUID, fatura Id'sinden türetiliyor ve her denemede aynı. — [InvoiceSubmissionOutcomePolicy.cs:40-47](src/MarketplaceHub.Application/InvoiceSubmissionOutcomePolicy.cs)
  - (c) STARTED kalmış deneme UnknownResult'a çevriliyor ve yeniden gönderilmiyor. — [InvoicingJobProcessor.cs:109-123](src/MarketplaceHub.Infrastructure/Persistence/InvoicingJobProcessor.cs)
  - (d) Başarılı yanıtın kaydı, iptale aldırmadan 4 kez yeniden deneniyor. — [InvoicingJobProcessor.cs:378-411](src/MarketplaceHub.Infrastructure/Persistence/InvoicingJobProcessor.cs)
  - (e) Yeniden taslak yalnız tüm denemeleri FAILED olan faturada yapılıyor. — [InvoicingWorkspaceWorkflow.cs:516-521](src/MarketplaceHub.Infrastructure/Persistence/InvoicingWorkspaceWorkflow.cs)
  - (f) Satış slotu iptal edilmiş faturadan yalnız sağlayıcı CANCELLED derse boşaltılıyor. — [InvoicingWorkspaceWorkflow.cs:657-673](src/MarketplaceHub.Infrastructure/Persistence/InvoicingWorkspaceWorkflow.cs), [MarketplaceInvoiceReopen.cs:29-50](src/MarketplaceHub.Infrastructure/Persistence/MarketplaceInvoiceReopen.cs)
- **e-Arşiv / e-Fatura seçimi:** Tip taslakta `EInvoiceRecipientResolver` ile belirleniyor, Submit anında `fresh: true` ile yeniden soruluyor ve uyuşmazsa `Refusal` ile duruyor. Yeni bir hata bulunmadı. — [InvoicingJobProcessor.cs:244-248](src/MarketplaceHub.Infrastructure/Persistence/InvoicingJobProcessor.cs)
- **Fatura numaralama:** Numara sağlayıcıdan geliyor (`ApplySubmissionSuccess`, `invoice.InvoiceNumber ??= remote.InvoiceNumber`). Yerelde sıra üretilmiyor, dolayısıyla yerel numara çakışması yok. — [InvoicingJobProcessor.cs:366, 523](src/MarketplaceHub.Infrastructure/Persistence/InvoicingJobProcessor.cs)

### Inferences
- Kısmi iade bulgusu ile "AdjustmentRequired'dan geri dönüş yok" durumu birleşince, iadesi olan her siparişin linki teslim edilmeden kalabilir. Pazaryeri faturasız paket cezası uygularsa doğrudan maliyete dönüşür.
- `Submit` içinde, taslak kapılarının (`IsSaleInvoiceBlockedPackage`, satır adedi ve tutar sapması) worker tarafında yeniden uygulanması, ilk bulgunun penceresini kapatır.

### Gaps
- Trendyol v2'nin `shipmentPackageIds=0` sorgusuna verdiği yanıt (404 mü, boş mu) canlıda doğrulanmadı. Yer tutucu bulgusu bu yüzden [Muhtemel].
- Canlıda AdjustmentRequired durumundaki faturaların kaçının kısmi iade kaynaklı olduğu `/prod-sql` ile sayılmadı.

## 2. Pazaryerine teslim, otomatik yeniden deneme, iptal ve yeniden açma

### Takeaway
Teslimin durum makinesi (STARTED/UNKNOWN/SUBMITTED/CONFIRMED) bir pakete ikinci kez yazmayı iyi engelliyor. "Teslim edildi" denmesi için tam kanıt (`HasExactDeliveryProof`) ya da pazaryeri okuması aranıyor. Sorunlar şu iki yerde: ön okuma hatası sonrası faturanın hiçbir kurtarma yolunun kapsamadığı MarketplacePending durumunda takılı kalması, ve geri okuması olmayan pazaryerlerinde (Pazarama, kısmen n11/PTT) belirsiz teslimin sonsuz iş döngüsüne girmesi.

### Cited Findings
- **[Yüksek][Kesin] Teslim öncesi pazaryeri okuması başarısız olursa fatura, teslim durumu kaydı olmadan MarketplacePending'de kalıyor. Hiçbir otomatik yol onu almıyor ve panelde "Tekrar Dene" de yok.**
  - `beforeDelivery` hatasında `invoice.Status = MarketplacePending`, `LastErrorCode = MARKETPLACE_INVOICE_PRECHECK_FAILED` ve `FromAdapter` fırlatılıyor. `MarketplaceDeliveryState` henüz yazılmamış, yazımı 1344. satırda. — [InvoicingJobProcessor.cs:1137-1149](src/MarketplaceHub.Infrastructure/Persistence/InvoicingJobProcessor.cs)
  - İş Dead/Blocked olduğunda kurtarma yollarının hiçbiri faturayı almıyor:
    - 5 dakikalık kurtarma bir state satırı istiyor (join). — [InvoicingJobProcessor.cs:2037-2053](src/MarketplaceHub.Infrastructure/Persistence/InvoicingJobProcessor.cs)
    - never-queued kurtarma `Status == Accepted` istiyor. — [InvoicingJobProcessor.cs:2062-2071](src/MarketplaceHub.Infrastructure/Persistence/InvoicingJobProcessor.cs)
    - Otomatik yeniden deneme yalnız Accepted/MarketplaceFailed durumuna bakıyor. — [InvoicingJobProcessor.cs:2170](src/MarketplaceHub.Infrastructure/Persistence/InvoicingJobProcessor.cs), [InvoiceDeliveryAutoRetryPolicy.cs:58](src/MarketplaceHub.Application/InvoiceDeliveryAutoRetryPolicy.cs)
  - Elle yollar da kapalı:
    - `EnqueueDeliveryAsync` → `CanRetryDelivery(MarketplacePending)` false (409). — [InvoiceMarketplaceRetryPolicy.cs:17-18](src/MarketplaceHub.Application/InvoiceMarketplaceRetryPolicy.cs)
    - `RetryDeliveryAsync` → `Need = None` → `INVOICE_DELIVERY_RETRY_NOT_APPLICABLE`.
    - Çalışma alanı → `ExistingInvoiceNextAction(MarketplacePending)` "WAIT". — [InvoicingWorkspaceWorkflow.cs:458-465](src/MarketplaceHub.Infrastructure/Persistence/InvoicingWorkspaceWorkflow.cs)
  - Senaryo: Trendyol paket okumasında geçici 5xx yaşanıyor ve işin retry bütçesi bitiyor, ya da NotFound sınıfı hata alınıp iş hemen Blocked oluyor. Kesilmiş e-Arşiv pazaryerine hiç gitmiyor. Fatura listede "işleniyor" görünüyor. Tek çıkış operatörün "Pazaryerinde Gördüm" demesi; link gerçekten yoksa bu yanlış olur.
  - Çok paketli siparişte birinci paket CONFIRMED, ikincisinin ön okuması hatalıysa aynı durum ikinci paket için oluşuyor.
- **[Orta][Muhtemel] Geri okuması olmayan pazaryerinde (Pazarama, n11/PTT'de API dışı durum) UNKNOWN teslim sonsuz iş döngüsüne giriyor.**
  - 5xx, zaman aşımı veya çakışma `InvoiceDeliveryFailureDisposition.Unknown` olarak sınıflanıyor ve state UNKNOWN oluyor. — [InvoicingContracts.cs:109-119](src/MarketplaceHub.Application/InvoicingContracts.cs)
  - Sonraki işler UNKNOWN dalına giriyor. Shopify ve Hepsiburada dışında canlı okuma yok. `InvoiceDeliveryRecoveryPolicy.Decide`, pakette `MarketplaceInvoiceObservedAt > state.UpdatedAt` olmadıkça `WaitForMarketplaceReadback` döndürüyor ve iş 2 dakikalık `Retry` ile tekrar kuruluyor. — [InvoicingJobProcessor.cs:1059-1117](src/MarketplaceHub.Infrastructure/Persistence/InvoicingJobProcessor.cs), [InvoicingContracts.cs:223-230](src/MarketplaceHub.Application/InvoicingContracts.cs)
  - Pazarama `QueryDeliveryAsync` ya UNVERIFIED ya NotSupported dönüyor, gönderilen linki asla okumuyor. — [PazaramaInvoices.cs:38-41](src/MarketplaceHub.Infrastructure/Adapters/Pazarama/PazaramaInvoices.cs)
  - İş bittiğinde (Dead) 5 dakikalık kurtarma, UNKNOWN + MarketplacePending için her 5 dakikalık kovada (`RecoveryDedupKey` içinde `bucket`) yeni iş açıyor. Bu kurtarma yolunda yayın tarihi penceresi ve deneme sayısı üst sınırı yok. HB'ye özgü "3×500 sonra dur" kuralı yalnız Hepsiburada için geçerli. — [InvoicingJobProcessor.cs:2037-2053, 2119-2154](src/MarketplaceHub.Infrastructure/Persistence/InvoicingJobProcessor.cs), [InvoicingContracts.cs:138-140](src/MarketplaceHub.Application/InvoicingContracts.cs)
  - Sonuç: Dış yazma tekrarlanmıyor (doğru), ancak kullanıcıya hiçbir şey gösterilmeden sonsuz iş ve kuyruk yükü oluşuyor. Fatura hiç ManualReview'a düşmüyor.
- **[Düşük][Kesin] `CONFIRMATION_RETRYABLE` dalında fatura durumu `ManualReview`/`Accepted` olsa da durum makinesine bakılmadan `MarketplacePending` yapılıyor ve `Version` artırılmıyor.** — [InvoicingJobProcessor.cs:964-969](src/MarketplaceHub.Infrastructure/Persistence/InvoicingJobProcessor.cs). Etki: ManualReview'daki (ör. `DELIVERY_PROOF_MISSING`) bir fatura, kurtarma işiyle sessizce incelemeden çıkıyor. If-Match kullanan istemciler sürüm değişikliğini görmüyor.
- **[Düşük][Kesin] `Cancel` işi, iptal edilen fatura için sürmekte olan teslimi beklemiyor.** Ancak sonucu `SetInvoiceDeliveryOutcome` (Cancelled → link kaldırma kaydı) ve `Reconcile` CANCELLED → `MarkLinkRevocationRequiredAsync` toparlıyor. Kontrol edildi, link kaldırma takibi düşmüyor. — [InvoicingJobProcessor.cs:1761-1767, 542-547](src/MarketplaceHub.Infrastructure/Persistence/InvoicingJobProcessor.cs)
- **Kontrol edildi, hata yok:**
  - "Pazaryeri kabul etmedi ama teslim edildi" yolu bulunmadı. Bunun sebebi: Completed yalnız tüm hedefler CONFIRMED olduğunda veriliyor ve CONFIRMED için numara+link kanıtı ya da aynı denemede doğrulama gerekiyor. — [InvoicingJobProcessor.cs:1633-1642, 1703-1718](src/MarketplaceHub.Infrastructure/Persistence/InvoicingJobProcessor.cs)
  - Yeniden açma (n11/Pazarama/PTT) yalnız iptal edilmiş fatura linki olan paketi açıyor. PTT'de API ile ikinci link gönderilmiyor. — [MarketplaceInvoiceReopen.cs:20-75](src/MarketplaceHub.Infrastructure/Persistence/MarketplaceInvoiceReopen.cs), [InvoicingJobProcessor.cs:1231-1239](src/MarketplaceHub.Infrastructure/Persistence/InvoicingJobProcessor.cs)
  - Otomatik yeniden deneme 14 günlük pencere ve en çok 5 deneme ile sınırlı. — [InvoiceDeliveryAutoRetryPolicy.cs:25-48](src/MarketplaceHub.Application/InvoiceDeliveryAutoRetryPolicy.cs)

### Inferences
- İlk bulgu, yer tutucu paket bulgusuyla (bölüm 1) zincirleniyor: Trendyol'da yer tutucu paket ön okumada bulunamazsa fatura bu sahipsiz MarketplacePending durumuna düşer.
- `EnqueueDeliveryAsync`/`CanRetryDelivery` politikasına "MarketplacePending + state yok + aktif iş yok" durumunu eklemek ilk bulguyu kapatır. UNKNOWN için bir yaş sınırı ve ManualReview'a düşürme de ikincisini kapatır.

### Gaps
- Pazarama/n11/PTT siparişleri eşitlenirken pakette `MarketplaceInvoiceObservedAt` güncellenip güncellenmediği (`MarketplaceJobProcessor.cs:10218-10225` çevresi) ayrıntılı izlenmedi. n11/PTT için ikinci bulgu bu yüzden [Muhtemel].
- İş çerçevesinin `JobExecutionResult.Retry` için en çok kaç deneme yaptığı bu turda okunmadı.

## 3. İade/claim onay ve red akışı, stok ve tutarlar

### Takeaway
İade kararında satır kapsamı, idempotency anahtarı ve "açık karar varsa yeni karar yok" kuralı var. Stoğa ekleme, kararın anahtarıyla satır başına bir kez yapılıyor. Zayıf nokta eşzamanlılık: claim sürüm kontrolü dış çağrıdan *sonra* yapılıyor, bu yüzden iki operatörün aynı anda verdiği kararlar ikisi birden pazaryerine gidebiliyor. Ayrıca kısmi satır kararında claim durumu yanlış hesaplanıyor.

### Cited Findings
- **[Orta][Kesin kod / Muhtemel tetiklenme] Aynı iade için eşzamanlı iki karar (farklı Idempotency-Key, aynı If-Match) ikisi birden pazaryerine gönderiliyor.**
  - `claim.Version != expectedVersion` yalnız bellekte karşılaştırılıyor.
  - "Açık karar var mı" kontrolü kilitsiz bir okuma.
  - Karar PENDING olarak claim sürümü artırılmadan kaydediliyor (`SaveChangesAsync`, 132. satır) ve dış çağrı (`returns.ExecuteAsync`) yapılıyor.
  - Claim'in `Version` eşzamanlılık belirteci ancak dış çağrıdan sonraki kayıtta (178-179. satırlar) devreye giriyor.
  - Kaynak: [MarketplaceSalesService.cs:2438-2552 (ProcessReturnActionInstantAsync)](src/MarketplaceHub.Infrastructure/Persistence/MarketplaceSalesService.cs), [MarketplaceModelConfiguration.cs:165](src/MarketplaceHub.Infrastructure/Persistence/MarketplaceModelConfiguration.cs)
  - Senaryo: İki sekmede ya da iki operatör aynı anda biri APPROVE, diğeri REJECT gönderiyor. İkisi de pazaryerine gidiyor. İkinci istek `DbUpdateConcurrencyException` (500) ile düşüyor ve kararı PENDING kalıyor. Bu yüzden `RETURN_DECISION_IN_PROGRESS`, efekt taraması sonuçlanana kadar yeni kararları da bloke ediyor.
  - Stok iki kez eklenmiyor. Kontrol edildi: ikinci kararın stok planı sonraki okumada `line.Quantity − alreadyReceived = 0` ile düşüyor. — [ReturnPendingStock.cs:96-100](src/MarketplaceHub.Infrastructure/Persistence/ReturnPendingStock.cs)
- **[Düşük][Kesin] Kısmi satır kararında claim durumu, "işlem bekleyen satır sayısı" ile hesaplanıyor. Daha önce onaylanmış satırı olan bir claim bu yüzden tamamen `Rejected` olarak işaretlenebiliyor.**
  - `StatusAfterLineDecision(claim.Status, action, returnLineIds.Length, actionableLineIds.Count)` toplam satır sayısıyla değil, işlem bekleyen satır sayısıyla karşılaştırıyor. — [MarketplaceSalesService.cs:2582](src/MarketplaceHub.Infrastructure/Persistence/MarketplaceSalesService.cs), [ReturnModels.cs:122-133](src/MarketplaceHub.Domain/ReturnModels.cs)
  - Senaryo: Trendyol claim'inde A satırı daha önce Accepted, B satırı bekliyor. B reddediliyor (1/1) ve claim `Rejected` yapılıyor. Geri okumada Trendyol claim durumu ilk kaleme (A → Approved) göre geliyor. `Rejected → Approved` geçişi durum makinesinde yok, claim yerelde Rejected kalıyor. Sonraki eşitlemede `IsAuthoritativeRemoteOutcome` bunu düzeltebilir.
  - Bu arada `FlagAdjustmentsAsync` (yalnız Approved/Completed claim'e bakıyor) faturayı işaretlemiyor, liste ve özetlerde iade "reddedildi" görünüyor.
- **[Düşük][Tahmin] Trendyol iade tutarında `lineTotalDiscount` birim indirim gibi işleniyor ve iade adediyle çarpılıyor.**
  - Alan adı satır toplamını düşündürüyor. `discount` yoksa `Number(line, "lineTotalDiscount") × quantity` hesaplanıyor. Bu, birden fazla adetli satırda indirimi şişirebilir. — [ReturnMarketplaceAmounts.cs:65, 83-84](src/MarketplaceHub.Infrastructure/Persistence/ReturnMarketplaceAmounts.cs)
  - Aynı varsayım paket toplamında da var. — [TrendyolJsonMapper.cs:165](src/MarketplaceHub.Infrastructure/Adapters/Trendyol/Mapping/TrendyolJsonMapper.cs)
  - Alanın birim mi toplam mı olduğu `/marketplace-docs` içinde yazmıyor. Doğrulanmadı.
- **Kontrol edildi, hata yok:**
  - Hepsiburada kararı tüm satırlara uygulanıyor (kısmi seçim reddediliyor), Refund/Change seçimi zorunlu, MissingInvoice talebi panelden onaylanmıyor. — [MarketplaceSalesService.cs:2461-2466](src/MarketplaceHub.Infrastructure/Persistence/MarketplaceSalesService.cs)
  - `HepsiburadaReturnActionPolicy` açık karar varken eylem listesini boşaltıyor. — [HepsiburadaReturnActionPolicy.cs:11-14](src/MarketplaceHub.Application/HepsiburadaReturnActionPolicy.cs)
  - Elle stok kararı (`ApplyDispositionAsync`) Serializable işlem içinde ve satır adediyle sınırlı.
  - `CancelledReturnClaimCleanup` geçmişi olan claim'i silmiyor, Cancelled olarak saklıyor ve stok kontrolü için bir kayıt açıyor. — [CancelledReturnClaimCleanup.cs:15-47](src/MarketplaceHub.Infrastructure/Persistence/CancelledReturnClaimCleanup.cs)
  - İade tutarı sipariş toplamından tahmin edilmiyor, bilinmeyen değer null kalıyor. — [ReturnMarketplaceAmounts.cs:7-16, 44-58](src/MarketplaceHub.Infrastructure/Persistence/ReturnMarketplaceAmounts.cs)

### Inferences
- Karar kaydı ile claim sürüm artışının dış çağrıdan *önce* aynı kayıtta yapılması (ya da claim üzerinde bir advisory kilit alınması) eşzamanlı çift kararı kapatır.
- Bölüm 1'deki kısmi iade bulgusu, iade akışıyla fatura akışı arasındaki ana bağlantı noktası. İade onayı, faturayı satır ve adet bilgisine bakmadan "düzeltme gerekli" yapıyor.

### Gaps
- Arayüzün aynı iade için çift tıklamada Idempotency-Key'i yeniden kullanıp kullanmadığı (kullanıyorsa ilk bulgu yalnız iki operatör/sekme senaryosuna iner) web kodunda kontrol edilmedi.
- Claim eşitleme yolunun (`MarketplaceJobProcessor.cs` ~12262) karar ve stok etkileşimi bu turda satır satır izlenmedi.
- Canlı veri kontrolü yapılmadı. AdjustmentRequired fatura sayısı, MarketplacePending + state'siz fatura sayısı ve UNKNOWN teslim işlerinin tekrar sayısı `/prod-sql` ile doğrulanmalı.
